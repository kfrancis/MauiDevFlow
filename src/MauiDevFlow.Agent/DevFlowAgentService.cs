using System.Text.Json;
using System.Reflection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui.Dispatching;
using MauiDevFlow.Logging;

namespace MauiDevFlow.Agent;

/// <summary>
/// The main agent service that hosts the HTTP API and coordinates
/// visual tree inspection and element interactions.
/// </summary>
public class DevFlowAgentService : IDisposable
{
    private readonly AgentOptions _options;
    private readonly AgentHttpServer _server;
    private readonly VisualTreeWalker _treeWalker;
    private FileLogProvider? _logProvider;
    private Application? _app;
    private IDispatcher? _dispatcher;
    private bool _disposed;

    /// <summary>
    /// Delegate for sending CDP commands to the Blazor WebView.
    /// Set by the Blazor package when both are registered.
    /// </summary>
    public Func<string, Task<string>>? CdpCommandHandler { get; set; }

    /// <summary>Whether the CDP handler is ready to process commands.</summary>
    public Func<bool>? CdpReadyCheck { get; set; }

    public bool IsRunning => _server.IsRunning;
    public int Port => _options.Port;

    public DevFlowAgentService(AgentOptions? options = null)
    {
        _options = options ?? new AgentOptions();
        _server = new AgentHttpServer(_options.Port);
        _treeWalker = new VisualTreeWalker();
        RegisterRoutes();
    }

    /// <summary>
    /// Sets the file log provider for serving logs via the API.
    /// Called by AgentServiceExtensions during registration.
    /// </summary>
    public void SetLogProvider(FileLogProvider provider)
        => _logProvider = provider;

    /// <summary>
    /// Writes a log entry originating from the WebView/Blazor console.
    /// Called by the Blazor package via reflection to route JS console output through ILogger.
    /// </summary>
    public void WriteWebViewLog(string level, string category, string message, string? exception = null)
    {
        if (_logProvider == null) return;

        var entry = new FileLogEntry(
            Timestamp: DateTime.UtcNow,
            Level: level,
            Category: category,
            Message: message,
            Exception: exception,
            Source: "webview"
        );
        _logProvider.Writer.Write(entry);
    }

    /// <summary>
    /// Starts the agent and binds to the running MAUI app.
    /// </summary>
    public void Start(Application app, IDispatcher dispatcher)
    {
        if (!_options.Enabled) return;
        _app = app;
        _dispatcher = dispatcher;
        try
        {
            _server.Start();
            Console.WriteLine($"[MauiDevFlow.Agent] HTTP server started on port {_options.Port}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MauiDevFlow.Agent] Failed to start HTTP server: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        await _server.StopAsync();
    }

    private void RegisterRoutes()
    {
        _server.MapGet("/api/status", HandleStatus);
        _server.MapGet("/api/tree", HandleTree);
        _server.MapGet("/api/element/{id}", HandleElement);
        _server.MapGet("/api/query", HandleQuery);
        _server.MapGet("/api/screenshot", HandleScreenshot);
        _server.MapGet("/api/property/{id}/{name}", HandleProperty);
        _server.MapPost("/api/property/{id}/{name}", HandleSetProperty);
        _server.MapPost("/api/action/tap", HandleTap);
        _server.MapPost("/api/action/fill", HandleFill);
        _server.MapPost("/api/action/clear", HandleClear);
        _server.MapPost("/api/action/focus", HandleFocus);
        _server.MapPost("/api/action/navigate", HandleNavigate);
        _server.MapGet("/api/logs", HandleLogs);
        _server.MapPost("/api/cdp", HandleCdp);
    }

    private Task<HttpResponse> HandleStatus(HttpRequest request)
    {
        return Task.FromResult(HttpResponse.Json(new
        {
            agent = "MauiDevFlow.Agent",
            version = "1.0.0",
            platform = DeviceInfo.Current.Platform.ToString(),
            deviceType = DeviceInfo.Current.DeviceType.ToString(),
            idiom = DeviceInfo.Current.Idiom.ToString(),
            appName = _app?.GetType().Assembly.GetName().Name ?? "unknown",
            running = _app != null,
            cdpReady = CdpReadyCheck?.Invoke() ?? false
        }));
    }

    private async Task<HttpResponse> HandleTree(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        int maxDepth = 0;
        if (request.QueryParams.TryGetValue("depth", out var depthStr))
            int.TryParse(depthStr, out maxDepth);

        var tree = await DispatchAsync(() => _treeWalker.WalkTree(_app, maxDepth));
        return HttpResponse.Json(tree);
    }

    private async Task<HttpResponse> HandleElement(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");
        if (!request.RouteParams.TryGetValue("id", out var id))
            return HttpResponse.Error("Element ID required");

        var element = await DispatchAsync(() =>
        {
            var el = _treeWalker.GetElementById(id, _app);
            if (el is not IVisualTreeElement vte) return null;
            return _treeWalker.WalkElement(vte, null, 1, 2);
        });

        return element != null ? HttpResponse.Json(element) : HttpResponse.NotFound($"Element '{id}' not found");
    }

    private async Task<HttpResponse> HandleQuery(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        request.QueryParams.TryGetValue("type", out var type);
        request.QueryParams.TryGetValue("automationId", out var automationId);
        request.QueryParams.TryGetValue("text", out var text);

        if (type == null && automationId == null && text == null)
            return HttpResponse.Error("At least one query parameter required: type, automationId, or text");

        var results = await DispatchAsync(() => _treeWalker.Query(_app, type, automationId, text));
        return HttpResponse.Json(results);
    }

    private async Task<HttpResponse> HandleScreenshot(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        try
        {
            var pngData = await DispatchAsync(async () =>
            {
                var window = _app.Windows.FirstOrDefault();
                if (window?.Page is not VisualElement rootElement) return null;

                // Use VisualDiagnostics.CaptureAsPngAsync for the screenshot
                var result = await VisualDiagnostics.CaptureAsPngAsync(rootElement);
                return result;
            });

            if (pngData == null)
                return HttpResponse.Error("Failed to capture screenshot");

            return HttpResponse.Png(pngData);
        }
        catch (Exception ex)
        {
            return HttpResponse.Error($"Screenshot failed: {ex.Message}");
        }
    }

    private async Task<HttpResponse> HandleProperty(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");
        if (!request.RouteParams.TryGetValue("id", out var id))
            return HttpResponse.Error("Element ID required");
        if (!request.RouteParams.TryGetValue("name", out var propName))
            return HttpResponse.Error("Property name required");

        var value = await DispatchAsync(() =>
        {
            var el = _treeWalker.GetElementById(id, _app);            if (el == null) return (object?)null;

            var type = el.GetType();
            var prop = type.GetProperty(propName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            return prop?.GetValue(el)?.ToString();
        });

        return value != null
            ? HttpResponse.Json(new { id, property = propName, value })
            : HttpResponse.NotFound($"Property '{propName}' not found on element '{id}'");
    }

    private async Task<HttpResponse> HandleSetProperty(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");
        if (!request.RouteParams.TryGetValue("id", out var id))
            return HttpResponse.Error("Element ID required");
        if (!request.RouteParams.TryGetValue("name", out var propName))
            return HttpResponse.Error("Property name required");

        var body = request.BodyAs<SetPropertyRequest>();
        if (body?.Value == null)
            return HttpResponse.Error("value is required");

        var result = await DispatchAsync(() =>
        {
            var el = _treeWalker.GetElementById(id, _app);
            if (el == null) return "Element not found";

            var type = el.GetType();
            var prop = type.GetProperty(propName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (prop == null || !prop.CanWrite)
                return $"Property '{propName}' not found or read-only";

            try
            {
                var converted = ConvertPropertyValue(prop.PropertyType, body.Value);
                prop.SetValue(el, converted);
                return "ok";
            }
            catch (Exception ex)
            {
                return $"Failed to set property: {ex.Message}";
            }
        });

        return result == "ok"
            ? HttpResponse.Json(new { id, property = propName, value = body.Value })
            : HttpResponse.Error(result);
    }

    private static object? ConvertPropertyValue(Type targetType, string value)
    {
        // Handle nullable types
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying == typeof(string)) return value;
        if (underlying == typeof(bool)) return bool.Parse(value);
        if (underlying == typeof(int)) return int.Parse(value);
        if (underlying == typeof(double)) return double.Parse(value);
        if (underlying == typeof(float)) return float.Parse(value);

        // MAUI Color - supports named colors and hex
        if (underlying == typeof(Microsoft.Maui.Graphics.Color))
        {
            // Try hex format (#RRGGBB or #AARRGGBB)
            if (value.StartsWith('#'))
                return Microsoft.Maui.Graphics.Color.FromArgb(value);

            // Try named colors via reflection on Colors class (check both properties and fields)
            var colorsType = typeof(Microsoft.Maui.Graphics.Colors);
            var colorProp = colorsType.GetProperty(value,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
            if (colorProp != null)
                return colorProp.GetValue(null);

            var colorField = colorsType.GetField(value,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
            if (colorField != null)
                return colorField.GetValue(null);

            // Try Color.FromArgb as last resort (for rgb hex without #)
            try { return Microsoft.Maui.Graphics.Color.FromArgb($"#{value}"); }
            catch { }

            throw new ArgumentException($"Unknown color: '{value}'. Use hex (#FF6347) or a named color (Red, Blue, Green, etc.).");
        }

        // MAUI Thickness (uniform or "left,top,right,bottom")
        if (underlying == typeof(Microsoft.Maui.Thickness))
        {
            var parts = value.Split(',');
            return parts.Length switch
            {
                1 => new Microsoft.Maui.Thickness(double.Parse(parts[0])),
                2 => new Microsoft.Maui.Thickness(double.Parse(parts[0]), double.Parse(parts[1])),
                4 => new Microsoft.Maui.Thickness(double.Parse(parts[0]), double.Parse(parts[1]),
                    double.Parse(parts[2]), double.Parse(parts[3])),
                _ => throw new ArgumentException($"Invalid Thickness format: {value}")
            };
        }

        // Enum types
        if (underlying.IsEnum)
            return Enum.Parse(underlying, value, ignoreCase: true);

        // Fallback: TypeConverter
        var converter = System.ComponentModel.TypeDescriptor.GetConverter(underlying);
        if (converter.CanConvertFrom(typeof(string)))
            return converter.ConvertFromString(value);

        throw new ArgumentException($"Cannot convert '{value}' to {targetType.Name}");
    }

    private async Task<HttpResponse> HandleTap(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        var body = request.BodyAs<ActionRequest>();
        if (body?.ElementId == null)
            return HttpResponse.Error("elementId is required");

        var result = await DispatchAsync(() =>
        {
            var el = _treeWalker.GetElementById(body.ElementId, _app);
            if (el == null) return "Element not found";

            switch (el)
            {
                case Button btn:
                    btn.SendClicked();
                    return "ok";
                case ImageButton imgBtn:
                    imgBtn.SendClicked();
                    return "ok";
                case CheckBox cb:
                    cb.IsChecked = !cb.IsChecked;
                    return "ok";
                case Switch sw:
                    sw.IsToggled = !sw.IsToggled;
                    return "ok";
                case RadioButton rb:
                    rb.IsChecked = true;
                    return "ok";
                case ToolbarItem ti:
                    ((IMenuItemController)ti).Activate();
                    return "ok";
                case MenuItem mi:
                    ((IMenuItemController)mi).Activate();
                    return "ok";
                case Picker picker:
                    picker.Focus();
                    return "ok";
                case DatePicker datePicker:
                    datePicker.Focus();
                    return "ok";
                case TimePicker timePicker:
                    timePicker.Focus();
                    return "ok";
                case Page page when page.Parent is TabbedPage tabbed:
                    tabbed.CurrentPage = page;
                    return "ok";
                case ShellContent sc:
                    if (Shell.Current != null)
                    {
                        sc.IsVisible = true;
                        Shell.Current.CurrentItem = sc.Parent as ShellSection ?? Shell.Current.CurrentItem;
                    }
                    return "ok";
                case ShellSection ss:
                    if (Shell.Current != null)
                        Shell.Current.CurrentItem = ss;
                    return "ok";
                case IView view when view is View v:
                    // Try TapGestureRecognizer: Command first, then Tapped event via reflection
                    var tapGesture = v.GestureRecognizers.OfType<TapGestureRecognizer>().FirstOrDefault();
                    if (tapGesture != null)
                    {
                        if (tapGesture.Command != null)
                        {
                            tapGesture.Command.Execute(tapGesture.CommandParameter);
                            return "ok";
                        }
                        // Fire the Tapped event via reflection (SendTapped is internal)
                        if (TryInvokeTapped(tapGesture, v))
                            return "ok";
                        return $"TapGestureRecognizer found but SendTapped reflection failed on {el.GetType().FullName}";
                    }

                    // Native platform fallback for UIControl/Android.Views.View
                    if (v is VisualElement nativeVe && TryNativeTap(nativeVe))
                        return "ok";

                    return $"No tap handler on {el.GetType().FullName} (gestures:{v.GestureRecognizers.Count}, type:{v.GetType().Name})";
                default:
                    return $"Unhandled type: {el.GetType().FullName}";
            }
        });

        return result == "ok" ? HttpResponse.Ok("Tapped") : HttpResponse.Error(result);
    }

    /// <summary>
    /// Invokes the Tapped event on a TapGestureRecognizer via reflection.
    /// Calls internal SendTapped(View sender, Func&lt;IElement?, Point?&gt;? getPosition) method.
    /// </summary>
    private static bool TryInvokeTapped(TapGestureRecognizer tapGesture, View sender)
    {
        try
        {
            // SendTapped is internal on TapGestureRecognizer itself
            var sendTapped = typeof(TapGestureRecognizer).GetMethod("SendTapped",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (sendTapped != null)
            {
                var paramCount = sendTapped.GetParameters().Length;
                var args = paramCount switch
                {
                    0 => Array.Empty<object>(),
                    1 => new object[] { sender },
                    _ => new object?[] { sender, null }
                };
                sendTapped.Invoke(tapGesture, args);
                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MauiDevFlow] TryInvokeTapped failed: {ex.GetBaseException().Message}");
        }
        return false;
    }

    /// <summary>
    /// Attempts to tap a native platform view as a fallback.
    /// iOS: UIControl.SendActionForControlEvents(.TouchUpInside)
    /// Android: View.PerformClick()
    /// </summary>
    private static bool TryNativeTap(VisualElement ve)
    {
        try
        {
            var platformView = ve.Handler?.PlatformView;
            if (platformView == null) return false;

#if IOS || MACCATALYST
            if (platformView is UIKit.UIControl control)
            {
                control.SendActionForControlEvents(UIKit.UIControlEvent.TouchUpInside);
                return true;
            }
#elif ANDROID
            if (platformView is Android.Views.View androidView && androidView.Clickable)
            {
                androidView.PerformClick();
                return true;
            }
#endif
        }
        catch { }
        return false;
    }

    private async Task<HttpResponse> HandleFill(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        var body = request.BodyAs<FillRequest>();
        if (body?.ElementId == null || body.Text == null)
            return HttpResponse.Error("elementId and text are required");

        var result = await DispatchAsync(() =>
        {
            var el = _treeWalker.GetElementById(body.ElementId, _app);
            if (el == null) return "Element not found";

            switch (el)
            {
                case Entry entry:
                    entry.Text = body.Text;
                    entry.Unfocus();
                    return "ok";
                case Editor editor:
                    editor.Text = body.Text;
                    editor.Unfocus();
                    return "ok";
                case SearchBar searchBar:
                    searchBar.Text = body.Text;
                    searchBar.Unfocus();
                    return "ok";
                default:
                    return $"Unhandled type: {el.GetType().FullName}";
            }
        });

        return result == "ok" ? HttpResponse.Ok("Text set") : HttpResponse.Error(result);
    }

    private async Task<HttpResponse> HandleClear(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        var body = request.BodyAs<ActionRequest>();
        if (body?.ElementId == null)
            return HttpResponse.Error("elementId is required");

        var success = await DispatchAsync(() =>
        {
            var el = _treeWalker.GetElementById(body.ElementId, _app);
            if (el == null) return false;

            switch (el)
            {
                case Entry entry:
                    entry.Text = string.Empty;
                    return true;
                case Editor editor:
                    editor.Text = string.Empty;
                    return true;
                case SearchBar searchBar:
                    searchBar.Text = string.Empty;
                    return true;
                default:
                    return false;
            }
        });

        return success ? HttpResponse.Ok("Cleared") : HttpResponse.Error("Element does not accept text input");
    }

    private async Task<HttpResponse> HandleFocus(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        var body = request.BodyAs<ActionRequest>();
        if (body?.ElementId == null)
            return HttpResponse.Error("elementId is required");

        var success = await DispatchAsync(() =>
        {
            var el = _treeWalker.GetElementById(body.ElementId, _app);
            if (el is not VisualElement ve) return false;
            ve.Focus();
            return true;
        });

        return success ? HttpResponse.Ok("Focused") : HttpResponse.Error("Cannot focus element");
    }

    private async Task<HttpResponse> HandleNavigate(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        var body = request.BodyAs<NavigateRequest>();
        if (string.IsNullOrEmpty(body?.Route))
            return HttpResponse.Error("route is required");

        var result = await DispatchAsync(async () =>
        {
            try
            {
                if (Shell.Current != null)
                {
                    await Shell.Current.GoToAsync(body.Route);
                    return "ok";
                }
                return "No Shell.Current available";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        });

        return result == "ok" ? HttpResponse.Ok($"Navigated to {body.Route}") : HttpResponse.Error(result ?? "Navigation failed");
    }

    private async Task<T> DispatchAsync<T>(Func<T> func)
    {
        if (_dispatcher == null || _dispatcher.IsDispatchRequired == false)
            return func();

        var tcs = new TaskCompletionSource<T>();
        _dispatcher.Dispatch(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return await tcs.Task;
    }

    private async Task<T?> DispatchAsync<T>(Func<Task<T?>> func) where T : class
    {
        if (_dispatcher == null || _dispatcher.IsDispatchRequired == false)
            return await func();

        var tcs = new TaskCompletionSource<T?>();
        _dispatcher.Dispatch(async () =>
        {
            try { tcs.SetResult(await func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return await tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _server.Dispose();
        _logProvider?.Dispose();
    }

    private Task<HttpResponse> HandleLogs(HttpRequest request)
    {
        if (_logProvider == null)
            return Task.FromResult(HttpResponse.Error("File logging is not enabled"));

        var limitStr = request.QueryParams.GetValueOrDefault("limit", "100");
        var skipStr = request.QueryParams.GetValueOrDefault("skip", "0");
        var source = request.QueryParams.TryGetValue("source", out var s) ? s : null;

        if (!int.TryParse(limitStr, out var limit)) limit = 100;
        if (!int.TryParse(skipStr, out var skip)) skip = 0;

        var entries = _logProvider.Reader.Read(limit, skip, source);
        return Task.FromResult(HttpResponse.Json(entries));
    }

    private async Task<HttpResponse> HandleCdp(HttpRequest request)
    {
        if (CdpCommandHandler == null)
            return HttpResponse.Error("CDP not available (Blazor debug service not registered)");

        if (!(CdpReadyCheck?.Invoke() ?? false))
            return HttpResponse.Error("CDP not ready (WebView not initialized)");

        if (string.IsNullOrEmpty(request.Body))
            return HttpResponse.Error("Missing CDP command body");

        try
        {
            var result = await CdpCommandHandler(request.Body);
            return new HttpResponse
            {
                ContentType = "application/json",
                Body = result
            };
        }
        catch (Exception ex)
        {
            return HttpResponse.Error($"CDP command failed: {ex.Message}");
        }
    }
}

// Request DTOs
public class ActionRequest
{
    public string? ElementId { get; set; }
}

public class FillRequest
{
    public string? ElementId { get; set; }
    public string? Text { get; set; }
}

public class NavigateRequest
{
    public string? Route { get; set; }
}

public class SetPropertyRequest
{
    public string? Value { get; set; }
}
