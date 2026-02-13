namespace MauiDevFlow.Blazor.Gtk;

/// <summary>
/// Blazor WebView debug service for WebKitGTK on Linux.
/// Captures the WebKit.WebView from the BlazorWebViewHandler and provides
/// CDP command handling via Chobitsu.js injection and JS evaluation.
/// </summary>
public class GtkBlazorWebViewDebugService : IDisposable
{
    private global::WebKit.WebView? _webView;
    private bool _isInitialized;
    private bool _disposed;
    private bool _injecting;
    private bool _chobitsuLoaded;
    private CancellationTokenSource? _drainCts;

    public Action<string>? LogCallback { get; set; }
    public Action<string, string, string?>? WebViewLogCallback { get; set; }

    public bool IsReady => _isInitialized && _webView != null && _chobitsuLoaded;

    /// <summary>
    /// Sets the WebKit.WebView reference for JS evaluation.
    /// Call this after the BlazorWebViewHandler creates the WebView.
    /// </summary>
    public async Task SetWebView(global::WebKit.WebView webView)
    {
        _webView = webView;
        _isInitialized = true;

        Log("[BlazorDevFlow.Gtk] WebView captured, waiting for page load...");
        await Task.Delay(2000);

        Log("[BlazorDevFlow.Gtk] Injecting debug script...");
        await InjectDebugScriptAsync();
    }

    private async Task<string?> EvaluateJavaScriptAsync(string script)
    {
        if (_webView == null) return null;

        try
        {
            var result = await _webView.EvaluateJavascriptAsync(script);
            return result?.ToString();
        }
        catch (Exception ex)
        {
            Log($"[BlazorDevFlow.Gtk] JS eval error: {ex.Message}");
            return null;
        }
    }

    private async Task InjectDebugScriptAsync()
    {
        if (_injecting) return;
        _injecting = true;

        try
        {
            if (_webView == null)
            {
                Log("[BlazorDevFlow.Gtk] WebView is null");
                return;
            }

            // Wait for chobitsu to be available
            for (int i = 0; i < 30; i++)
            {
                var check = await EvaluateJavaScriptAsync(
                    "typeof chobitsu !== 'undefined' ? 'loaded' : 'waiting'");
                if (check == "loaded") break;

                if (i == 10)
                {
                    var hasTag = await EvaluateJavaScriptAsync(
                        "document.querySelector('script[src*=\"chobitsu\"]') ? 'found' : 'missing'");
                    if (hasTag == "missing")
                    {
                        Log("[BlazorDevFlow.Gtk] No chobitsu script tag found. Add <script src=\"chobitsu.js\"></script> to wwwroot/index.html");
                        return;
                    }
                }

                if (i == 29)
                {
                    Log("[BlazorDevFlow.Gtk] Chobitsu not loaded after 15s");
                    return;
                }
                await Task.Delay(500);
            }

            var script = ScriptResources.Load("chobitsu-init.js");
            Log($"[BlazorDevFlow.Gtk] Injecting init script ({script.Length} chars)...");

            var result = await EvaluateJavaScriptAsync(script);
            Log($"[BlazorDevFlow.Gtk] Script injection result: {result ?? "null"}");
            _chobitsuLoaded = true;

            // Inject console interceptor
            var consoleScript = ScriptResources.Load("console-intercept.js");
            await EvaluateJavaScriptAsync(consoleScript);

            StartLogDrain();
        }
        catch (Exception ex)
        {
            Log($"[BlazorDevFlow.Gtk] Failed to inject script: {ex.Message}");
        }
        finally
        {
            _injecting = false;
        }
    }

    /// <summary>
    /// Sends a CDP command to chobitsu and returns the response.
    /// </summary>
    public async Task<string> SendCdpCommandAsync(string cdpJson)
    {
        if (!IsReady)
            return "{\"error\":\"WebView not ready\"}";

        try
        {
            var json = System.Text.Json.JsonDocument.Parse(cdpJson);
            var id = json.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
            var method = json.RootElement.TryGetProperty("method", out var methodProp) ? methodProp.GetString() ?? "" : "";

            if (method == "Input.insertText")
                return await HandleInputInsertTextAsync(cdpJson, id);
            if (method == "Page.reload")
                return await HandlePageReloadAsync(id);
            if (method == "Page.navigate")
                return await HandlePageNavigateAsync(cdpJson, id);
            if (method.StartsWith("Browser."))
                return HandleBrowserMethod(method, id);

            var escaped = cdpJson.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "\\r");
            var sendScript = ScriptResources.Load("cdp-send-receive.js")
                .Replace("%CDP_MESSAGE%", escaped);
            var readScript = ScriptResources.Load("cdp-read-response.js");

            await EvaluateJavaScriptAsync(sendScript);
            await Task.Delay(50);

            for (int i = 0; i < 60; i++)
            {
                var result = await EvaluateJavaScriptAsync(readScript);
                var unescaped = UnescapeEvalResult(result);
                if (unescaped != null)
                    return unescaped;
                await Task.Delay(50);
            }

            return "{\"error\":\"cdp timeout\"}";
        }
        catch (Exception ex)
        {
            return $"{{\"error\":\"{EscapeJsonString(ex.Message)}\"}}";
        }
    }

    private string HandleBrowserMethod(string method, int id)
    {
        if (method == "Browser.getVersion")
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                id,
                result = new
                {
                    protocolVersion = "1.3",
                    product = "MAUI Blazor WebView (WebKitGTK)/1.0",
                    userAgent = "MauiDevFlow.Gtk",
                    jsVersion = ""
                }
            });
        return $"{{\"id\":{id},\"result\":{{}}}}";
    }

    private async Task<string> HandleInputInsertTextAsync(string cdpJson, int id)
    {
        var json = System.Text.Json.JsonDocument.Parse(cdpJson);
        var text = json.RootElement.GetProperty("params").GetProperty("text").GetString() ?? "";
        var escapedText = EscapeJsString(text);

        var script = ScriptResources.Load("insert-text.js")
            .Replace("%TEXT%", escapedText)
            .Replace("%TEXT_LENGTH%", text.Length.ToString());
        await EvaluateJavaScriptAsync(script);
        return $"{{\"id\":{id},\"result\":{{}}}}";
    }

    private async Task<string> HandlePageReloadAsync(int id)
    {
        _webView?.Reload();
        await Task.Delay(1500);
        await InjectDebugScriptAsync();
        return $"{{\"id\":{id},\"result\":{{}}}}";
    }

    private async Task<string> HandlePageNavigateAsync(string cdpJson, int id)
    {
        var json = System.Text.Json.JsonDocument.Parse(cdpJson);
        var url = json.RootElement.GetProperty("params").GetProperty("url").GetString() ?? "";
        _webView?.LoadUri(url);
        await Task.Delay(1500);
        await InjectDebugScriptAsync();
        return $"{{\"id\":{id},\"result\":{{\"frameId\":\"main\"}}}}";
    }

    private static string? UnescapeEvalResult(string? result)
    {
        if (string.IsNullOrEmpty(result)) return null;
        if (result.StartsWith("\"") && result.EndsWith("\""))
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(result); }
            catch { }
        }
        return result;
    }

    private static string EscapeJsString(string s)
        => s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"")
            .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    private static string EscapeJsonString(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    private void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);
        Console.WriteLine(message);
        LogCallback?.Invoke(message);
    }

    private void StartLogDrain()
    {
        _drainCts?.Cancel();
        _drainCts = new CancellationTokenSource();
        var ct = _drainCts.Token;

        Task.Run(async () =>
        {
            var drainScript = ScriptResources.Load("drain-console-logs.js");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, ct);
                    if (!IsReady || WebViewLogCallback == null) continue;

                    var raw = await EvaluateJavaScriptAsync(drainScript);
                    var json = UnescapeEvalResult(raw);
                    if (string.IsNullOrEmpty(json) || json == "null") continue;

                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    foreach (var entry in doc.RootElement.EnumerateArray())
                    {
                        var jsLevel = entry.GetProperty("l").GetString() ?? "log";
                        var message = entry.GetProperty("m").GetString() ?? "";
                        var exception = entry.TryGetProperty("e", out var eProp) ? eProp.GetString() : null;

                        var level = jsLevel switch
                        {
                            "error" => "Error",
                            "warn" => "Warning",
                            "debug" => "Debug",
                            "info" => "Information",
                            _ => "Information"
                        };
                        WebViewLogCallback(level, message, exception);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _drainCts?.Cancel();
        _drainCts?.Dispose();
    }
}
