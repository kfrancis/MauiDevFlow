using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MauiDevFlow.Agent.Core;

/// <summary>
/// Lightweight HTTP server using TcpListener (sandbox-friendly, no HttpListener).
/// Routes incoming requests to registered handlers.
/// </summary>
public class AgentHttpServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;
    private readonly int _port;
    private readonly Dictionary<string, Func<HttpRequest, Task<HttpResponse>>> _getRoutes = new();
    private readonly Dictionary<string, Func<HttpRequest, Task<HttpResponse>>> _postRoutes = new();

    public int Port => _port;
    public bool IsRunning => _listenTask != null && !_listenTask.IsCompleted;

    public AgentHttpServer(int port = 9223)
    {
        _port = port;
    }

    public void MapGet(string path, Func<HttpRequest, Task<HttpResponse>> handler)
        => _getRoutes[path.TrimEnd('/')] = handler;

    public void MapPost(string path, Func<HttpRequest, Task<HttpResponse>> handler)
        => _postRoutes[path.TrimEnd('/')] = handler;

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AgentHttpServer));
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _listenTask = AcceptLoop(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_listenTask != null)
            await _listenTask.ConfigureAwait(false);
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { /* swallow connection errors */ }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                if (request == null) return;

                var response = await RouteRequestAsync(request).ConfigureAwait(false);
                await WriteResponseAsync(stream, response, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[MauiDevFlow.Agent] Request error: {ex.GetType().Name}: {ex.Message}"); }
    }

    private async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var totalRead = 0;

        // Read with timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token).ConfigureAwait(false);
            if (read == 0) return null;
            totalRead = read;
        }
        catch { return null; }

        var raw = Encoding.UTF8.GetString(buffer, 0, totalRead);
        var lines = raw.Split("\r\n");
        if (lines.Length == 0) return null;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        var method = requestLine[0];
        var fullPath = requestLine[1];

        // Parse path and query string
        var queryStart = fullPath.IndexOf('?');
        var path = queryStart >= 0 ? fullPath[..queryStart] : fullPath;
        var queryString = queryStart >= 0 ? fullPath[(queryStart + 1)..] : "";

        var queryParams = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(queryString))
        {
            foreach (var param in queryString.Split('&'))
            {
                var kv = param.Split('=', 2);
                if (kv.Length == 2)
                    queryParams[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
            }
        }

        // Find body (after blank line)
        string? body = null;
        var blankLineIdx = raw.IndexOf("\r\n\r\n");
        if (blankLineIdx >= 0)
        {
            body = raw[(blankLineIdx + 4)..];

            // Check Content-Length for more body data
            var contentLengthLine = lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            if (contentLengthLine != null)
            {
                var clValue = contentLengthLine.Split(':', 2)[1].Trim();
                if (int.TryParse(clValue, out var contentLength) && body.Length < contentLength)
                {
                    var remaining = contentLength - body.Length;
                    var bodyBuffer = new byte[remaining];
                    var bodyRead = 0;
                    while (bodyRead < remaining)
                    {
                        var r = await stream.ReadAsync(bodyBuffer.AsMemory(bodyRead, remaining - bodyRead), ct).ConfigureAwait(false);
                        if (r == 0) break;
                        bodyRead += r;
                    }
                    body += Encoding.UTF8.GetString(bodyBuffer, 0, bodyRead);
                }
            }
        }

        return new HttpRequest
        {
            Method = method,
            Path = path.TrimEnd('/'),
            QueryParams = queryParams,
            Body = body
        };
    }

    private async Task<HttpResponse> RouteRequestAsync(HttpRequest request)
    {
        var routes = request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ? _postRoutes : _getRoutes;

        // Try exact match first
        if (routes.TryGetValue(request.Path, out var handler))
            return await handler(request).ConfigureAwait(false);

        // Try pattern match (e.g., /api/element/{id})
        foreach (var kvp in routes)
        {
            var routeParts = kvp.Key.Split('/');
            var requestParts = request.Path.Split('/');
            if (routeParts.Length != requestParts.Length) continue;

            bool match = true;
            for (int i = 0; i < routeParts.Length; i++)
            {
                if (routeParts[i].StartsWith('{') && routeParts[i].EndsWith('}'))
                {
                    var paramName = routeParts[i][1..^1];
                    request.RouteParams[paramName] = requestParts[i];
                    continue;
                }
                if (!routeParts[i].Equals(requestParts[i], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return await kvp.Value(request).ConfigureAwait(false);

            request.RouteParams.Clear();
        }

        return HttpResponse.NotFound("Route not found");
    }

    private static async Task WriteResponseAsync(NetworkStream stream, HttpResponse response, CancellationToken ct)
    {
        var bodyBytes = response.Body != null ? Encoding.UTF8.GetBytes(response.Body) : Array.Empty<byte>();
        var headerBuilder = new StringBuilder();
        headerBuilder.Append($"HTTP/1.1 {response.StatusCode} {response.StatusText}\r\n");
        headerBuilder.Append($"Content-Type: {response.ContentType}\r\n");
        headerBuilder.Append($"Content-Length: {(response.BodyBytes ?? bodyBytes).Length}\r\n");
        headerBuilder.Append("Access-Control-Allow-Origin: *\r\n");
        headerBuilder.Append("Connection: close\r\n");
        headerBuilder.Append("\r\n");

        var headerBytes = Encoding.UTF8.GetBytes(headerBuilder.ToString());
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        await stream.WriteAsync(response.BodyBytes ?? bodyBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _listener?.Stop();
        _cts?.Dispose();
    }
}

public class HttpRequest
{
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = "/";
    public Dictionary<string, string> QueryParams { get; set; } = new();
    public Dictionary<string, string> RouteParams { get; set; } = new();
    public string? Body { get; set; }

    private static readonly JsonSerializerOptions _readOptions = new() { PropertyNameCaseInsensitive = true };

    public T? BodyAs<T>() where T : class
        => Body != null ? JsonSerializer.Deserialize<T>(Body, _readOptions) : null;
}

public class HttpResponse
{
    public int StatusCode { get; set; } = 200;
    public string StatusText { get; set; } = "OK";
    public string ContentType { get; set; } = "application/json";
    public string? Body { get; set; }
    public byte[]? BodyBytes { get; set; }

    public static HttpResponse Json(object data) => new()
    {
        Body = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true })
    };

    public static HttpResponse Png(byte[] data) => new()
    {
        ContentType = "image/png",
        BodyBytes = data
    };

    public static HttpResponse Ok(string? message = null) => new()
    {
        Body = JsonSerializer.Serialize(new { success = true, message })
    };

    public static HttpResponse Error(string message, int statusCode = 400) => new()
    {
        StatusCode = statusCode,
        StatusText = statusCode == 404 ? "Not Found" : "Bad Request",
        Body = JsonSerializer.Serialize(new { success = false, error = message })
    };

    public static HttpResponse NotFound(string message = "Not found") => Error(message, 404);
}
