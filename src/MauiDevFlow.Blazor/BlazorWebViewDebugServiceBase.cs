namespace MauiDevFlow.Blazor;

/// <summary>
/// Base class for BlazorWebView debug services. Contains all shared logic for
/// the CDP bridge (message routing, polling, script injection). Platform-specific
/// subclasses provide the WebView capture and JavaScript evaluation.
/// </summary>
public abstract class BlazorWebViewDebugServiceBase : IDisposable
{
    private readonly ChobitsuWebSocketBridge _bridge;
    protected bool IsInitialized;
    private bool _disposed;

    /// <summary>Optional log callback for debug messages.</summary>
    public Action<string>? LogCallback { get; set; }

    public bool IsRunning => _bridge.IsRunning;
    public int Port => _bridge.Port;
    public int ConnectionCount => _bridge.ConnectionCount;

    protected BlazorWebViewDebugServiceBase(int port = 9222)
    {
        _bridge = new ChobitsuWebSocketBridge(port);
        _bridge.LogCallback = (msg) => Log(msg);
        _bridge.OnMessageFromClient += HandleMessageFromClient;
        _bridge.OnClientConnected += (id) => Log($"[BlazorDevFlow] Client connected: {id}");
        _bridge.OnClientDisconnected += (id) => Log($"[BlazorDevFlow] Client disconnected: {id}");
    }

    /// <summary>
    /// Evaluates JavaScript in the WebView and returns the result.
    /// Must be called on the main/UI thread.
    /// </summary>
    protected abstract Task<string?> EvaluateJavaScriptAsync(string script);

    /// <summary>Whether the platform WebView has been captured and is ready.</summary>
    protected abstract bool HasWebView { get; }

    /// <summary>Reload the current page via the native WebView API.</summary>
    protected abstract void ReloadWebView();

    /// <summary>Navigate to a URL via the native WebView API.</summary>
    protected abstract void NavigateWebView(string url);

    /// <summary>
    /// Configures the BlazorWebViewHandler to capture the platform WebView reference.
    /// Called during service registration before the app starts.
    /// </summary>
    public abstract void ConfigureHandler();

    public void Start()
    {
        if (IsRunning)
        {
            Log("[BlazorDevFlow] Start called but already running");
            return;
        }

        Log("[BlazorDevFlow] Starting bridge...");
        _bridge.Start();
        Log($"[BlazorDevFlow] Bridge started, IsRunning={IsRunning}");

        if (IsInitialized && HasWebView)
        {
            Log("[BlazorDevFlow] WebView already initialized, injecting now");
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await InjectDebugScriptAsync();
            });
        }
    }

    public async Task StopAsync()
    {
        Log("[BlazorDevFlow] Stopping...");
        await _bridge.StopAsync();
    }

    /// <summary>
    /// Called by platform subclasses after capturing the WebView reference.
    /// </summary>
    protected async Task OnWebViewCapturedAsync()
    {
        IsInitialized = true;

        Log("[BlazorDevFlow] Waiting 2s for page to load...");
        await Task.Delay(2000);

        if (IsRunning)
        {
            Log("[BlazorDevFlow] Server is running, injecting debug script...");
            await InjectDebugScriptAsync();
        }
        else
        {
            Log("[BlazorDevFlow] Server not running, skipping injection");
        }
    }

    private bool _injecting;

    private async Task InjectDebugScriptAsync()
    {
        if (_injecting) return;
        _injecting = true;

        try
        {
            await InjectDebugScriptCoreAsync();
        }
        finally
        {
            _injecting = false;
        }
    }

    private async Task InjectDebugScriptCoreAsync()
    {
        if (!HasWebView)
        {
            Log("[BlazorDevFlow] InjectDebugScript: WebView is null");
            return;
        }

        // Wait for chobitsu to be available (loaded via <script> tag in index.html)
        for (int i = 0; i < 30; i++)
        {
            var check = await EvaluateJavaScriptAsync(
                "typeof chobitsu !== 'undefined' ? 'loaded' : 'waiting'");
            if (i == 0 || check?.ToString() == "loaded")
                Log($"[BlazorDevFlow] Chobitsu check #{i}: {check}");
            if (check?.ToString() == "loaded") break;

            // After a few attempts, check the HTML for the script tag to give an early error
            if (i == 5)
            {
                var hasTag = await EvaluateJavaScriptAsync(
                    "document.querySelector('script[src*=\"chobitsu\"]') ? 'found' : 'missing'");
                if (hasTag?.ToString() == "missing")
                {
                    Log("[BlazorDevFlow] ❌ Missing required script tag in wwwroot/index.html.");
                    Log("[BlazorDevFlow] Add this before </body>:  <script src=\"chobitsu.js\"></script>");
                    Log("[BlazorDevFlow] The chobitsu.js file is delivered automatically by the Redth.MauiDevFlow.Blazor NuGet package as a static web asset.");
                    return;
                }
            }

            if (i == 29)
            {
                Log("[BlazorDevFlow] Chobitsu not loaded after 15s — the script tag exists but chobitsu.js may not be loading correctly.");
                return;
            }
            await Task.Delay(500);
        }

        var script = ChobitsuDebugScript.GetInjectionScript(_bridge.Port);
        Log($"[BlazorDevFlow] Injecting init script ({script.Length} chars)...");

        try
        {
            var result = await EvaluateJavaScriptAsync(script);
            Log($"[BlazorDevFlow] Script injection result: {result?.ToString() ?? "null"}");

            await SetupResponseHandlerAsync();
        }
        catch (Exception ex)
        {
            LogError("[BlazorDevFlow] Failed to inject script", ex);
        }
    }

    private void HandleMessageFromClient(string connectionId, string message)
    {
        Log($"[BlazorDevFlow] Message from client {connectionId}: {message.Substring(0, Math.Min(100, message.Length))}...");

        if (!HasWebView || !IsInitialized)
        {
            Log("[BlazorDevFlow] Cannot forward message - WebView not ready");
            return;
        }

        if (message.Contains("\"method\":\"Input.insertText\""))
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await HandleInputInsertTextAsync(message);
            });
            return;
        }

        if (message.Contains("\"method\":\"Page.reload\""))
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await HandlePageReloadAsync(message);
            });
            return;
        }

        if (message.Contains("\"method\":\"Page.navigate\""))
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await HandlePageNavigateAsync(message);
            });
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var handlerScript = ChobitsuDebugScript.GetMessageHandlerScript(message);
                await EvaluateJavaScriptAsync(handlerScript);
                Log("[BlazorDevFlow] Message forwarded to WebView");
            }
            catch (Exception ex)
            {
                LogError("[BlazorDevFlow] Failed to forward message", ex);
            }
        });
    }

    private async Task HandleInputInsertTextAsync(string message)
    {
        try
        {
            var json = System.Text.Json.JsonDocument.Parse(message);
            var id = json.RootElement.GetProperty("id").GetInt32();
            var text = json.RootElement.GetProperty("params").GetProperty("text").GetString() ?? "";

            Log($"[BlazorDevFlow] Input.insertText: '{text}'");

            var escapedText = EscapeJsString(text);
            var textLength = text.Length;

            var script = ScriptResources.Load("insert-text.js")
                .Replace("%TEXT%", escapedText)
                .Replace("%TEXT_LENGTH%", textLength.ToString());

            var result = await EvaluateJavaScriptAsync(script);
            Log($"[BlazorDevFlow] insertText result: {result}");

            var response = $"{{\"id\":{id},\"result\":{{}}}}";
            await _bridge.SendToClientsAsync(response);
        }
        catch (Exception ex)
        {
            LogError("[BlazorDevFlow] Failed to handle Input.insertText", ex);
        }
    }

    private async Task HandlePageReloadAsync(string message)
    {
        try
        {
            var json = System.Text.Json.JsonDocument.Parse(message);
            var id = json.RootElement.GetProperty("id").GetInt32();

            Log("[BlazorDevFlow] Page.reload: reloading via native WebView API");

            ReloadWebView();
            await Task.Delay(1500);

            Log("[BlazorDevFlow] Page.reload: re-injecting debug script after reload");
            await InjectDebugScriptAsync();

            var response = $"{{\"id\":{id},\"result\":{{}}}}";
            await _bridge.SendToClientsAsync(response);
            Log("[BlazorDevFlow] Page.reload: complete");
        }
        catch (Exception ex)
        {
            LogError("[BlazorDevFlow] Failed to handle Page.reload", ex);
        }
    }

    private async Task HandlePageNavigateAsync(string message)
    {
        try
        {
            var json = System.Text.Json.JsonDocument.Parse(message);
            var id = json.RootElement.GetProperty("id").GetInt32();
            var url = json.RootElement.GetProperty("params").GetProperty("url").GetString() ?? "";

            Log($"[BlazorDevFlow] Page.navigate: navigating to {url} via native WebView API");

            NavigateWebView(url);
            await Task.Delay(1500);

            Log("[BlazorDevFlow] Page.navigate: re-injecting debug script after navigation");
            await InjectDebugScriptAsync();

            var frameId = "main";
            var response = $"{{\"id\":{id},\"result\":{{\"frameId\":\"{frameId}\"}}}}";
            await _bridge.SendToClientsAsync(response);
            Log("[BlazorDevFlow] Page.navigate: complete");
        }
        catch (Exception ex)
        {
            LogError("[BlazorDevFlow] Failed to handle Page.navigate", ex);
        }
    }

    private static string EscapeJsString(string s)
    {
        return s.Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
    }

    private async Task SetupResponseHandlerAsync()
    {
        Log("[BlazorDevFlow] Setting up response handler...");

        var checkScript = ScriptResources.Load("setup-response-handler.js");

        var result = await EvaluateJavaScriptAsync(checkScript);
        Log($"[BlazorDevFlow] Response handler check result: {result?.ToString() ?? "null"}");

        Log("[BlazorDevFlow] Starting response polling loop...");
        _ = PollForResponsesAsync();
    }

    private async Task PollForResponsesAsync()
    {
        int pollCount = 0;

        while (IsRunning && IsInitialized && !_disposed && HasWebView)
        {
            try
            {
                var pollScript = ScriptResources.Load("poll-responses.js");

                var result = await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    return (await EvaluateJavaScriptAsync(pollScript))?.ToString() ?? "[]";
                });

                if (pollCount < 10 || (result != "[]" && result != "\"[]\""))
                {
                    Log($"[BlazorDevFlow] Poll #{pollCount} raw result: [{result}]");
                }

                if (!string.IsNullOrEmpty(result) && result != "[]" && result != "\"[]\"")
                {
                    Log($"[BlazorDevFlow] Poll got non-empty: {result.Substring(0, Math.Min(300, result.Length))}");

                    var trimmed = result;
                    if (trimmed.StartsWith("\"") && trimmed.EndsWith("\""))
                    {
                        trimmed = trimmed.Substring(1, trimmed.Length - 2);
                        trimmed = trimmed.Replace("\\\"", "\"").Replace("\\\\", "\\");
                    }

                    if (trimmed.StartsWith("[") && trimmed != "[]")
                    {
                        try
                        {
                            var messages = System.Text.Json.JsonSerializer.Deserialize<string[]>(trimmed);
                            if (messages != null)
                            {
                                foreach (var msg in messages)
                                {
                                    await _bridge.SendToClientsAsync(msg);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogError("[BlazorDevFlow] JSON deserialize failed", ex);
                        }
                    }
                }

                pollCount++;
            }
            catch (Exception ex)
            {
                LogError("[BlazorDevFlow] Poll error", ex);
            }

            await Task.Delay(50);
        }
    }

    protected void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);
        Console.WriteLine(message);
        LogCallback?.Invoke(message);
    }

    protected void LogError(string message, Exception? ex = null)
    {
        Log($"[ERROR] {message}");
        if (ex != null)
            Log($"  Exception: {ex.GetType().Name}: {ex.Message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Log("[BlazorDevFlow] Disposing...");
        _bridge.StopAsync().GetAwaiter().GetResult();
        _bridge.Dispose();
    }
}
