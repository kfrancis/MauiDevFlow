using System.Text.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui.Dispatching;
using MauiDevFlow.Agent.Core.Profiling;
using MauiDevFlow.Logging;
using MauiDevFlow.Agent.Core.Network;

namespace MauiDevFlow.Agent.Core;

/// <summary>
/// The main agent service that hosts the HTTP API and coordinates
/// visual tree inspection and element interactions.
/// </summary>
public class DevFlowAgentService : IDisposable, IMarkerPublisher
{
    private readonly AgentOptions _options;
    private readonly AgentHttpServer _server;
    private readonly VisualTreeWalker _treeWalker;
    private FileLogProvider? _logProvider;
    private BrokerRegistration? _brokerRegistration;
    protected Application? _app;
    protected IDispatcher? _dispatcher;
    private bool _disposed;

    /// <summary>
    /// The network request store for capturing HTTP traffic.
    /// </summary>
    public NetworkRequestStore NetworkStore { get; }

    private readonly IProfilerCollector _profilerCollector;
    private readonly ProfilerSessionStore _profilerSessions;
    private readonly SemaphoreSlim _profilerStateGate = new(1, 1);
    private CancellationTokenSource? _profilerLoopCts;
    private Task? _profilerLoopTask;
    private DateTime _lastAutoJankSpanTsUtc = DateTime.MinValue;
    private const int UiHookScanIntervalMs = 3000;
    private readonly ConditionalWeakTable<BindableObject, UiHookState> _uiHookStates = new();
    private readonly List<Action> _uiHookUnsubscribers = new();
    private readonly object _uiHookGate = new();
    private int _uiHookGeneration = 1;
    private int _uiHookScanInFlight;
    private DateTime _lastUiHookScanTsUtc = DateTime.MinValue;
    private Shell? _hookedShell;
    private DateTime? _navigationStartedAtUtc;
    private string? _navigationTargetRoute;
    private DateTime _lastUserActionTsUtc = DateTime.MinValue;
    private string? _lastUserActionName;
    private string? _lastUserActionElementPath;
    private readonly ConditionalWeakTable<Page, PageLifecycleState> _pageLifecycleStates = new();
    private readonly ConditionalWeakTable<VisualElement, ElementRenderState> _elementRenderStates = new();
    private readonly ConditionalWeakTable<BindableObject, ScrollBatchState> _scrollBatchStates = new();

    private sealed class UiHookState
    {
        public int Generation { get; set; }
        public HashSet<string> HookKeys { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PageLifecycleState
    {
        public DateTime AppearingAtUtc { get; set; }
        public string? Route { get; set; }
        public bool FirstLayoutPublished { get; set; }
        public int SizeChangedCount { get; set; }
        public int MeasureInvalidatedCount { get; set; }
    }

    private sealed class ElementRenderState
    {
        public DateTime TrackingStartedAtUtc { get; set; }
        public string? Role { get; set; }
        public bool FirstLayoutPublished { get; set; }
        public int SizeChangedCount { get; set; }
        public int MeasureInvalidatedCount { get; set; }
    }

    private sealed class ScrollBatchState
    {
        public bool IsActive { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime LastEventAtUtc { get; set; }
        public int EventCount { get; set; }
        public int FlushVersion { get; set; }
        public double StartOffsetX { get; set; }
        public double StartOffsetY { get; set; }
        public double LastOffsetX { get; set; }
        public double LastOffsetY { get; set; }
        public int? StartFirstVisibleIndex { get; set; }
        public int? StartLastVisibleIndex { get; set; }
        public int? LastFirstVisibleIndex { get; set; }
        public int? LastLastVisibleIndex { get; set; }
    }

    /// <summary>
    /// Delegate for sending CDP commands to the Blazor WebView.
    /// Set by the Blazor package when both are registered.
    /// Deprecated: use RegisterCdpWebView() for multi-WebView support.
    /// Setting this property registers the handler as WebView index 0.
    /// </summary>
    public Func<string, Task<string>>? CdpCommandHandler
    {
        get => _cdpWebViews.Count > 0 ? _cdpWebViews[0].CommandHandler : null;
        set
        {
            if (value == null)
            {
                if (_cdpWebViews.Count > 0)
                    _cdpWebViews.RemoveAt(0);
                return;
            }
            if (_cdpWebViews.Count > 0)
                _cdpWebViews[0].CommandHandler = value;
            else
                _cdpWebViews.Add(new CdpWebViewInfo { Index = 0, CommandHandler = value, ReadyCheck = () => true });
        }
    }

    /// <summary>Whether the CDP handler is ready to process commands.
    /// Deprecated: use RegisterCdpWebView() for multi-WebView support.</summary>
    public Func<bool>? CdpReadyCheck
    {
        get => _cdpWebViews.Count > 0 ? _cdpWebViews[0].ReadyCheck : null;
        set
        {
            if (_cdpWebViews.Count > 0 && value != null)
                _cdpWebViews[0].ReadyCheck = value;
        }
    }

    private readonly List<CdpWebViewInfo> _cdpWebViews = new();
    private int _nextWebViewIndex = 0;

    /// <summary>Register a CDP-capable WebView with the agent.</summary>
    public int RegisterCdpWebView(Func<string, Task<string>> commandHandler, Func<bool> readyCheck,
        string? automationId = null, string? elementId = null, string? url = null)
    {
        var index = _nextWebViewIndex++;
        _cdpWebViews.Add(new CdpWebViewInfo
        {
            Index = index,
            AutomationId = automationId,
            ElementId = elementId,
            Url = url,
            CommandHandler = commandHandler,
            ReadyCheck = readyCheck,
        });
        return index;
    }

    /// <summary>Unregister a CDP WebView by index.</summary>
    public void UnregisterCdpWebView(int index)
    {
        _cdpWebViews.RemoveAll(w => w.Index == index);
    }

    /// <summary>Update metadata for a registered WebView.</summary>
    public void UpdateCdpWebView(int index, string? automationId = null, string? elementId = null, string? url = null)
    {
        var wv = _cdpWebViews.FirstOrDefault(w => w.Index == index);
        if (wv == null) return;
        if (automationId != null) wv.AutomationId = automationId;
        if (elementId != null) wv.ElementId = elementId;
        if (url != null) wv.Url = url;
    }

    private CdpWebViewInfo? ResolveCdpWebView(string? webviewId)
    {
        if (_cdpWebViews.Count == 0) return null;
        if (string.IsNullOrEmpty(webviewId)) return _cdpWebViews[0]; // default to first

        // Try index
        if (int.TryParse(webviewId, out var idx))
        {
            var byIndex = _cdpWebViews.FirstOrDefault(w => w.Index == idx);
            if (byIndex != null) return byIndex;
        }

        // Try AutomationId
        var byAutomationId = _cdpWebViews.FirstOrDefault(w =>
            !string.IsNullOrEmpty(w.AutomationId) && w.AutomationId.Equals(webviewId, StringComparison.OrdinalIgnoreCase));
        if (byAutomationId != null) return byAutomationId;

        // Try ElementId
        var byElementId = _cdpWebViews.FirstOrDefault(w =>
            !string.IsNullOrEmpty(w.ElementId) && w.ElementId.Equals(webviewId, StringComparison.OrdinalIgnoreCase));
        if (byElementId != null) return byElementId;

        return null;
    }

    public bool IsRunning => _server.IsRunning;
    public int Port => _options.Port;

    public DevFlowAgentService(AgentOptions? options = null)
    {
        _options = options ?? new AgentOptions();
        _server = new AgentHttpServer(_options.Port);
        _treeWalker = CreateTreeWalker();
        NetworkStore = new NetworkRequestStore(_options.MaxNetworkBufferSize);
        _profilerCollector = CreateProfilerCollector();
        _profilerSessions = new ProfilerSessionStore(
            Math.Max(1, _options.MaxProfilerSamples),
            Math.Max(1, _options.MaxProfilerMarkers),
            Math.Max(1, _options.MaxProfilerSpans));
        if (_options.EnableNetworkMonitoring)
            DevFlowHttp.SetStore(NetworkStore);
        NetworkStore.OnRequestCaptured += HandleCapturedNetworkRequest;
        RegisterRoutes();
    }

    /// <summary>
    /// Parses the optional "window" query parameter as a 0-based window index.
    /// Returns null when not specified (callers should default to first window).
    /// </summary>
    private static int? ParseWindowIndex(HttpRequest request)
    {
        if (request.QueryParams.TryGetValue("window", out var ws) && int.TryParse(ws, out var wi))
            return wi;
        return null;
    }

    /// <summary>
    /// Gets the window at the given index, or the first window when index is null.
    /// </summary>
    private Window? GetWindow(int? index)
    {
        if (_app == null) return null;
        if (index == null) return _app.Windows.FirstOrDefault() as Window;
        if (index.Value < 0 || index.Value >= _app.Windows.Count) return null;
        return _app.Windows[index.Value] as Window;
    }

    /// <summary>
    /// Creates the visual tree walker. Override in platform-specific subclasses
    /// to return a walker with native info population.
    /// </summary>
    protected virtual VisualTreeWalker CreateTreeWalker() => new VisualTreeWalker();

    /// <summary>
    /// Creates the profiler collector. Override in platform-specific subclasses
    /// to provide native frame/CPU integrations.
    /// </summary>
    protected virtual IProfilerCollector CreateProfilerCollector() => new RuntimeProfilerCollector();

    /// <summary>Platform name for status reporting. Override for platforms without DeviceInfo.</summary>
    protected virtual string PlatformName => DeviceInfo.Current.Platform.ToString();

    /// <summary>Device type for status reporting. Override for platforms without DeviceInfo.</summary>
    protected virtual string DeviceTypeName => DeviceInfo.Current.DeviceType.ToString();

    /// <summary>Device idiom for status reporting. Override for platforms without DeviceInfo.</summary>
    protected virtual string IdiomName => DeviceInfo.Current.Idiom.ToString();

    /// <summary>
    /// Gets the display density (scale factor) for a specific window. Returns 1.0 for standard,
    /// 2.0 for @2x (Retina), 3.0 for @3x (iPhone Pro Max), etc.
    /// Used to auto-scale screenshots to 1x logical resolution.
    /// Override in platform-specific agents to query the native window's actual screen density,
    /// which may vary across displays in multi-monitor setups.
    /// </summary>
    protected virtual double GetWindowDisplayDensity(IWindow? window)
    {
        try { return DeviceDisplay.MainDisplayInfo.Density; }
        catch { return 1.0; }
    }

    /// <summary>Gets native window dimensions when MAUI reports 0. Override for platform-specific access.</summary>
    protected virtual (double width, double height) GetNativeWindowSize(IWindow window) => (0, 0);

    private bool IsProfilerFeatureAvailable => _options.EnableProfiler;

    /// <summary>
    /// Sets the file log provider for serving logs via the API.
    /// Called by AgentServiceExtensions during registration.
    /// </summary>
    public void SetLogProvider(FileLogProvider provider)
        => _logProvider = provider;

    public void SetBrokerRegistration(BrokerRegistration registration)
        => _brokerRegistration = registration;

    /// <summary>
    /// Writes a log entry originating from the WebView/Blazor console.
    /// Called by the Blazor package via reflection to route JS console output through ILogger.
    /// </summary>
    public void WriteWebViewLog(string level, string category, string message, string? exception = null)
    {
        if (_logProvider == null) return;

        var entry = new Logging.FileLogEntry(
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
        await StopProfilerAsync();
        await _server.StopAsync();
    }

    private void RegisterRoutes()
    {
        _server.MapGet("/api/status", HandleStatus);
        _server.MapGet("/api/tree", HandleTree);
        _server.MapGet("/api/element/{id}", HandleElement);
        _server.MapGet("/api/query", HandleQuery);
        _server.MapGet("/api/hittest", HandleHitTest);
        _server.MapGet("/api/screenshot", HandleScreenshot);
        _server.MapGet("/api/property/{id}/{name}", HandleProperty);
        _server.MapPost("/api/property/{id}/{name}", HandleSetProperty);
        _server.MapPost("/api/action/tap", HandleTap);
        _server.MapPost("/api/action/fill", HandleFill);
        _server.MapPost("/api/action/clear", HandleClear);
        _server.MapPost("/api/action/focus", HandleFocus);
        _server.MapPost("/api/action/navigate", HandleNavigate);
        _server.MapPost("/api/action/resize", HandleResize);
        _server.MapPost("/api/action/scroll", HandleScroll);
        _server.MapGet("/api/logs", HandleLogs);
        _server.MapPost("/api/cdp", HandleCdp);
        _server.MapGet("/api/cdp/webviews", HandleCdpWebViews);
        _server.MapGet("/api/cdp/source", HandleCdpSource);
        _server.MapGet("/api/profiler/capabilities", HandleProfilerCapabilities);
        _server.MapPost("/api/profiler/start", HandleProfilerStart);
        _server.MapPost("/api/profiler/stop", HandleProfilerStop);
        _server.MapGet("/api/profiler/samples", HandleProfilerSamples);
        _server.MapPost("/api/profiler/marker", HandleProfilerMarker);
        _server.MapPost("/api/profiler/span", HandleProfilerSpan);
        _server.MapGet("/api/profiler/hotspots", HandleProfilerHotspots);

        // Network monitoring
        _server.MapGet("/api/network", HandleNetworkList);
        _server.MapGet("/api/network/{id}", HandleNetworkDetail);
        _server.MapPost("/api/network/clear", HandleNetworkClear);

        // WebSocket: live network monitoring stream
        _server.MapWebSocket("/ws/network", HandleNetworkWebSocket);

        // WebSocket: live log streaming
        _server.MapWebSocket("/ws/logs", HandleLogsWebSocket);
    }

    private async Task<HttpResponse> HandleStatus(HttpRequest request)
    {
        var windowIndex = ParseWindowIndex(request);
        var result = await DispatchAsync(() =>
        {
            var window = GetWindow(windowIndex);
            var w = window?.Width ?? 0;
            var h = window?.Height ?? 0;

            // Try getting window size from native platform view if MAUI reports invalid values
            if (window != null && (!double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0))
            {
                var (nw, nh) = GetNativeWindowSize(window);
                if (nw > 0) w = nw;
                if (nh > 0) h = nh;
            }

            return new
            {
                agent = "MauiDevFlow.Agent",
                version = typeof(DevFlowAgentService).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
                platform = PlatformName,
                deviceType = DeviceTypeName,
                idiom = IdiomName,
                displayDensity = GetWindowDisplayDensity(window),
                appName = _app?.GetType().Assembly.GetName().Name ?? "unknown",
                running = _app != null,
                cdpReady = _cdpWebViews.Any(w => w.IsReady),
                cdpWebViewCount = _cdpWebViews.Count,
                windowCount = _app?.Windows.Count ?? 0,
                windowWidth = double.IsFinite(w) ? w : 0,
                windowHeight = double.IsFinite(h) ? h : 0,
                profiler = BuildProfilerCapabilitiesPayload(),
                profilerSession = _profilerSessions.CurrentSession
            };
        });

        return HttpResponse.Json(result!);
    }

    private async Task<HttpResponse> HandleTree(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        int maxDepth = 0;
        if (request.QueryParams.TryGetValue("depth", out var depthStr))
            int.TryParse(depthStr, out maxDepth);

        var windowIndex = ParseWindowIndex(request);
        var tree = await DispatchAsync(() => _treeWalker.WalkTree(_app, maxDepth, windowIndex));
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
            if (el is IVisualTreeElement vte)
                return (object?)_treeWalker.WalkElement(vte, null, 1, 2);

            // Synthetic elements: build detail from marker
            if (el != null)
                return (object?)_treeWalker.BuildSyntheticElementInfo(id, el);

            return null;
        });

        return element != null ? HttpResponse.Json(element) : HttpResponse.NotFound($"Element '{id}' not found");
    }

    private async Task<HttpResponse> HandleQuery(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        // CSS selector takes precedence over simple filters
        if (request.QueryParams.TryGetValue("selector", out var selector) && !string.IsNullOrWhiteSpace(selector))
        {
            try
            {
                var results = await DispatchAsync(() => _treeWalker.QueryCss(_app, selector));
                return HttpResponse.Json(results);
            }
            catch (FormatException ex)
            {
                return HttpResponse.Error($"Invalid CSS selector: {ex.Message}");
            }
        }

        request.QueryParams.TryGetValue("type", out var type);
        request.QueryParams.TryGetValue("automationId", out var automationId);
        request.QueryParams.TryGetValue("text", out var text);

        if (type == null && automationId == null && text == null)
            return HttpResponse.Error("At least one query parameter required: type, automationId, text, or selector");

        var simpleResults = await DispatchAsync(() => _treeWalker.Query(_app, type, automationId, text));
        return HttpResponse.Json(simpleResults);
    }

    private async Task<HttpResponse> HandleHitTest(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        if (!request.QueryParams.TryGetValue("x", out var xStr) || !double.TryParse(xStr, out var x))
            return HttpResponse.Error("x coordinate is required");
        if (!request.QueryParams.TryGetValue("y", out var yStr) || !double.TryParse(yStr, out var y))
            return HttpResponse.Error("y coordinate is required");

        var windowIndex = ParseWindowIndex(request);

        var result = await DispatchAsync(() =>
        {
            var window = GetWindow(windowIndex);
            if (window == null) return (object?)null;

            // Ensure tree is walked so element IDs are assigned and synthetic bounds are populated
            _treeWalker.WalkTree(_app!, 0, windowIndex);

            // Build active Shell context to filter out inactive ShellItem subtrees
            var activeShellItemIds = BuildActiveShellItemIds(window);

            var platformHits = VisualTreeElementExtensions.GetVisualTreeElements(window, x, y);

            // Supplement with bounds-based hit testing — some platforms (e.g. macOS AppKit)
            // don't traverse into all containers via GetVisualTreeElements
            var boundsHits = _treeWalker.HitTestByBounds(x, y, _app!, windowIndex);
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var allHits = new List<IVisualTreeElement>();
            foreach (var h in platformHits)
            {
                seen.Add(h);
                allHits.Add(h);
            }
            foreach (var bh in boundsHits)
            {
                if (seen.Add(bh))
                    allHits.Add(bh);
            }

            var elements = new List<object>();

            // Detect modal pages — elements behind the topmost modal should be excluded
            var modalPage = window.Navigation?.ModalStack?.LastOrDefault();

            // Check synthetic elements first — they represent visible nav chrome
            // (nav bar, tab bar, flyout button) that sits on top of MAUI content.
            var syntheticHits = _treeWalker.HitTestSynthetics(x, y);
            foreach (var (synId, marker, bounds) in syntheticHits)
            {
                // If modal is active, only include synthetics belonging to the modal page
                if (modalPage != null && !IsSyntheticForPage(marker, modalPage))
                    continue;

                var synInfo = new Dictionary<string, object?>
                {
                    ["id"] = synId,
                    ["type"] = _treeWalker.GetSyntheticTypeName(marker),
                    ["bounds"] = bounds,
                    ["windowBounds"] = bounds, // synthetic bounds are already window-absolute
                    ["synthetic"] = true,
                };
                var text = _treeWalker.GetSyntheticText(marker);
                if (text != null) synInfo["text"] = text;
                elements.Add(synInfo);
            }

            foreach (var hit in allHits)
            {
                if (hit is not IVisualTreeElement vte) continue;

                // Skip elements under inactive ShellItem subtrees
                if (activeShellItemIds != null && IsUnderInactiveShellItem(hit, activeShellItemIds))
                    continue;

                // Skip elements behind the modal page
                if (modalPage != null && !IsDescendantOfPage(hit, modalPage))
                    continue;

                var id = _treeWalker.GetIdForElement(vte);
                if (id == null) continue;

                var info = new Dictionary<string, object?> { ["id"] = id, ["type"] = hit.GetType().Name };
                if (hit is VisualElement ve)
                {
                    info["automationId"] = ve.AutomationId;
                    info["bounds"] = new BoundsInfo
                    {
                        X = double.IsFinite(ve.Frame.X) ? ve.Frame.X : 0,
                        Y = double.IsFinite(ve.Frame.Y) ? ve.Frame.Y : 0,
                        Width = double.IsFinite(ve.Frame.Width) ? ve.Frame.Width : 0,
                        Height = double.IsFinite(ve.Frame.Height) ? ve.Frame.Height : 0
                    };

                    var wb = _treeWalker.ResolveWindowBoundsPublic(ve);
                    if (wb != null) info["windowBounds"] = wb;
                }
                if (hit is Label l) info["text"] = l.Text;
                else if (hit is Button b) info["text"] = b.Text;
                elements.Add(info);
            }

            return (object?)new { x, y, window = windowIndex ?? 0, elements };
        });

        return result != null
            ? HttpResponse.Json(result)
            : HttpResponse.Error($"Window {windowIndex ?? 0} not found");
    }

    /// <summary>
    /// Builds a set of active ShellItem objects for filtering hit test results.
    /// Returns null if the window doesn't contain a Shell (no filtering needed).
    /// </summary>
    private static HashSet<object>? BuildActiveShellItemIds(Window window)
    {
        var shell = window.Page as Shell;
        if (shell == null) return null;

        var currentItem = shell.CurrentItem;
        if (currentItem == null) return null;

        // Only the current ShellItem is active
        return new HashSet<object>(ReferenceEqualityComparer.Instance) { currentItem };
    }

    /// <summary>
    /// Checks if an element is under an inactive ShellItem subtree.
    /// Walks up the parent chain to find the containing ShellItem.
    /// </summary>
    private static bool IsUnderInactiveShellItem(object element, HashSet<object> activeShellItems)
    {
        var current = element as Element;
        while (current != null)
        {
            if (current is ShellItem si)
                return !activeShellItems.Contains(si);
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// Checks if an element is a descendant of the given page (or the page itself).
    /// Used to filter hit test results when a modal page is active.
    /// </summary>
    private static bool IsDescendantOfPage(object element, Page page)
    {
        var current = element as Element;
        while (current != null)
        {
            if (ReferenceEquals(current, page)) return true;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// Checks if a synthetic marker belongs to the given modal page context.
    /// The modal page may be a NavigationPage, TabbedPage, or FlyoutPage wrapping
    /// inner pages, so we use descendant checks rather than reference equality.
    /// </summary>
    private static bool IsSyntheticForPage(object marker, Page modalPage)
    {
        return marker switch
        {
            VisualTreeWalker.NavBarTitleMarker m => IsDescendantOfPage(m.Page, modalPage),
            ToolbarItem ti => IsDescendantOfPage(ti, modalPage),
            VisualTreeWalker.BackButtonMarker => true,
            VisualTreeWalker.SearchHandlerMarker => false,
            _ => false
        };
    }

    protected virtual async Task<HttpResponse> HandleScreenshot(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        int? maxWidth = null;
        if (request.QueryParams.TryGetValue("maxWidth", out var mwStr) && int.TryParse(mwStr, out var mw) && mw > 0)
            maxWidth = mw;

        // Auto-scale to 1x by default on HiDPI displays. Override with scale=native to keep full resolution.
        bool autoScale = true;
        if (request.QueryParams.TryGetValue("scale", out var scaleParam))
        {
            autoScale = !scaleParam.Equals("native", StringComparison.OrdinalIgnoreCase)
                     && !scaleParam.Equals("full", StringComparison.OrdinalIgnoreCase);
        }

        // Resolve the target window and its display density on the UI thread
        var windowIndex = ParseWindowIndex(request);
        var density = await DispatchAsync(() =>
        {
            var w = GetWindow(windowIndex);
            return GetWindowDisplayDensity(w);
        });

        // Check for fullscreen mode (captures all windows including dialogs)
        if (request.QueryParams.TryGetValue("fullscreen", out var fs) &&
            fs.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var pngData = await CaptureFullScreenAsync();
                if (pngData != null)
                    return HttpResponse.Png(ResizePngIfNeeded(pngData, maxWidth, density, autoScale));
                return HttpResponse.Error("Full-screen capture not supported on this platform");
            }
            catch (Exception ex)
            {
                return HttpResponse.Error($"Full-screen screenshot failed: {ex.Message}");
            }
        }

        // Element-level screenshot by ID
        if (request.QueryParams.TryGetValue("id", out var elementId) && !string.IsNullOrWhiteSpace(elementId))
        {
            try
            {
                var element = await DispatchAsync(() =>
                {
                    var el = _treeWalker.GetElementById(elementId, _app);
                    return el as VisualElement;
                });

                if (element == null)
                    return HttpResponse.Error($"Element '{elementId}' not found or not a VisualElement");

                var pngData = await DispatchAsync(() => CaptureElementScreenshotAsync(element));
                if (pngData == null)
                    return HttpResponse.Error($"Capture returned null for element '{elementId}'");

                return HttpResponse.Png(ResizePngIfNeeded(pngData, maxWidth, density, autoScale));
            }
            catch (Exception ex)
            {
                return HttpResponse.Error($"Element screenshot failed: {ex.Message}");
            }
        }

        // Element-level screenshot by CSS selector (captures first match)
        if (request.QueryParams.TryGetValue("selector", out var selector) && !string.IsNullOrWhiteSpace(selector))
        {
            try
            {
                var matchId = await DispatchAsync(() =>
                {
                    var results = _treeWalker.QueryCss(_app, selector);
                    return results.Count > 0 ? results[0].Id : null;
                });

                if (matchId == null)
                    return HttpResponse.Error($"No elements matching selector '{selector}'");

                var element = await DispatchAsync(() =>
                {
                    var el = _treeWalker.GetElementById(matchId, _app);
                    return el as VisualElement;
                });

                if (element == null)
                    return HttpResponse.Error($"Element '{matchId}' not found or not a VisualElement");

                var pngData = await DispatchAsync(() => CaptureElementScreenshotAsync(element));
                if (pngData == null)
                    return HttpResponse.Error($"Capture returned null for element '{matchId}'");

                return HttpResponse.Png(ResizePngIfNeeded(pngData, maxWidth, density, autoScale));
            }
            catch (FormatException ex)
            {
                return HttpResponse.Error($"Invalid CSS selector: {ex.Message}");
            }
            catch (Exception ex)
            {
                return HttpResponse.Error($"Element screenshot failed: {ex.Message}");
            }
        }

        try
        {
            var pngData = await DispatchAsync(async () =>
            {
                var window = GetWindow(windowIndex);
                if (window == null) return null;

                // If a modal page is displayed, capture it instead of the underlying page
                VisualElement? topModal = null;
                try
                {
                    var modalStack = window.Page?.Navigation?.ModalStack;
                    if (modalStack?.Count > 0 && modalStack[^1] is VisualElement ms)
                        topModal = ms;
                }
                catch { }

                // Fallback: check Window's visual children for modal pages
                // (on some platforms like GTK, modals appear as direct children of the Window)
                if (topModal == null && window is IVisualTreeElement windowVte)
                {
                    var children = windowVte.GetVisualChildren();
                    for (int i = children.Count - 1; i >= 0; i--)
                    {
                        if (children[i] is Page page && page != window.Page)
                        {
                            topModal = page;
                            break;
                        }
                    }
                }

                if (topModal != null)
                    return await CaptureScreenshotAsync(topModal);

                if (window.Page is not VisualElement rootElement) return null;

                return await CaptureScreenshotAsync(rootElement);
            });

            if (pngData == null)
                return HttpResponse.Error("Failed to capture screenshot");

            return HttpResponse.Png(ResizePngIfNeeded(pngData, maxWidth, density, autoScale));
        }
        catch (Exception ex)
        {
            return HttpResponse.Error($"Screenshot failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Captures a screenshot of the given root element. Override in platform-specific subclasses.
    /// </summary>
    protected virtual async Task<byte[]?> CaptureScreenshotAsync(VisualElement rootElement)
    {
        return await VisualDiagnostics.CaptureAsPngAsync(rootElement);
    }

    /// <summary>
    /// Captures a screenshot of a specific element in the visual tree.
    /// Override in platform-specific subclasses when VisualDiagnostics.CaptureAsPngAsync
    /// is not supported (e.g. macOS AppKit).
    /// </summary>
    protected virtual async Task<byte[]?> CaptureElementScreenshotAsync(VisualElement element)
    {
        return await VisualDiagnostics.CaptureAsPngAsync(element);
    }

    /// <summary>
    /// Captures a full-screen screenshot including all windows (dialogs, popups, etc.).
    /// Override in platform-specific subclasses for native support.
    /// Returns null if not supported.
    /// </summary>
    protected virtual Task<byte[]?> CaptureFullScreenAsync()
    {
        return Task.FromResult<byte[]?>(null);
    }

    /// <summary>
    /// Resizes a PNG image based on display density and/or max width constraint.
    /// By default, HiDPI screenshots are scaled to 1x logical resolution (e.g., a 3x iPhone
    /// screenshot of 1290px becomes 430px). An explicit maxWidth overrides density scaling.
    /// </summary>
    private static byte[] ResizePngIfNeeded(byte[] pngData, int? maxWidth, double density = 1.0, bool autoScale = true)
    {
        // Determine target width: explicit maxWidth takes priority, then auto-scale by density
        int? targetWidth = maxWidth;
        if (targetWidth == null && autoScale && density > 1.0)
        {
            try
            {
                using var probe = SkiaSharp.SKBitmap.Decode(pngData);
                if (probe != null)
                    targetWidth = (int)(probe.Width / density);
            }
            catch { return pngData; }
        }

        if (targetWidth == null || targetWidth <= 0) return pngData;

        try
        {
            using var original = SkiaSharp.SKBitmap.Decode(pngData);
            if (original == null || original.Width <= targetWidth.Value) return pngData;

            var scale = (float)targetWidth.Value / original.Width;
            var newHeight = (int)(original.Height * scale);

            using var resized = original.Resize(new SkiaSharp.SKImageInfo(targetWidth.Value, newHeight), SkiaSharp.SKFilterQuality.Medium);
            if (resized == null) return pngData;

            using var image = SkiaSharp.SKImage.FromBitmap(resized);
            using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }
        catch
        {
            return pngData;
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
            var el = _treeWalker.GetElementById(id, _app);
            if (el == null) return (object?)null;

            // Support dot-path notation (e.g., "Shadow.Radius")
            var parts = propName.Split('.');
            object? current = el;
            PropertyInfo? prop = null;
            foreach (var part in parts)
            {
                if (current == null) return null;
                var type = current.GetType();
                prop = type.GetProperty(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null) return null;
                current = prop.GetValue(current);
            }
            return FormatPropertyValue(current);
        });

        return value != null
            ? HttpResponse.Json(new { id, property = propName, value })
            : HttpResponse.NotFound($"Property '{propName}' not found on element '{id}'");
    }

    private static string? FormatPropertyValue(object? value)
    {
        if (value == null) return null;
        if (value is string s) return s;

        // Try TypeConverter first — handles Thickness, CornerRadius, Color, enums, etc.
        var converter = System.ComponentModel.TypeDescriptor.GetConverter(value.GetType());
        if (converter.CanConvertTo(typeof(string))
            && converter.GetType() != typeof(System.ComponentModel.TypeConverter)
            && converter is not System.ComponentModel.CollectionConverter)
        {
            try
            {
                var result = converter.ConvertToString(value);
                if (result != null) return result;
            }
            catch { }
        }

        // Fallback for complex types that lack TypeConverter ConvertTo support
        return value switch
        {
            Shadow shadow => FormatShadow(shadow),
            SolidColorBrush scb => $"SolidColorBrush Color={scb.Color?.ToArgbHex() ?? "(null)"}",
            LinearGradientBrush lgb => $"LinearGradientBrush StartPoint={lgb.StartPoint}, EndPoint={lgb.EndPoint}, Stops=[{FormatGradientStops(lgb.GradientStops)}]",
            RadialGradientBrush rgb => $"RadialGradientBrush Center={rgb.Center}, Radius={rgb.Radius}, Stops=[{FormatGradientStops(rgb.GradientStops)}]",
            Brush brush => brush.GetType().Name,
            Microsoft.Maui.Controls.Shapes.RoundRectangle rr => $"RoundRectangle CornerRadius={FormatPropertyValue(rr.CornerRadius)}",
            Microsoft.Maui.Controls.Shapes.Shape shape => shape.GetType().Name,
            ColumnDefinitionCollection cols => string.Join(", ", cols.Select(c => FormatGridLength(c.Width))),
            RowDefinitionCollection rows => string.Join(", ", rows.Select(r => FormatGridLength(r.Height))),
            LayoutOptions lo => $"{lo.Alignment}{(lo.Expands ? ", Expands" : "")}",
            LinearItemsLayout lin => $"LinearItemsLayout Orientation={lin.Orientation}, ItemSpacing={lin.ItemSpacing}",
            GridItemsLayout grid => $"GridItemsLayout Span={grid.Span}, Orientation={grid.Orientation}, HorizontalSpacing={grid.HorizontalItemSpacing}, VerticalSpacing={grid.VerticalItemSpacing}",
            FileImageSource fis => $"File: {fis.File}",
            UriImageSource uis => $"Uri: {uis.Uri}",
            FontImageSource fontIs => $"Font: {fontIs.Glyph} ({fontIs.FontFamily})",
            ImageSource img => img.GetType().Name,
            System.Collections.ICollection col => $"{col.GetType().Name} ({col.Count} items)",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? value.GetType().Name,
        };
    }

    private static string FormatGridLength(GridLength gl) => gl.IsStar
        ? (gl.Value == 1 ? "*" : $"{gl.Value}*")
        : gl.IsAbsolute ? gl.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "Auto";

    private static string FormatGradientStops(GradientStopCollection? stops)
    {
        if (stops == null || stops.Count == 0) return "";
        return string.Join(", ", stops.Select(s =>
            $"{s.Color.ToArgbHex()} {(s.Offset * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture)}%"));
    }

    private static string FormatShadow(Shadow shadow)
    {
        var parts = new List<string>();
        if (shadow.Brush is SolidColorBrush scb)
            parts.Add($"Brush={scb.Color?.ToArgbHex()}");
        else if (shadow.Brush != null)
            parts.Add($"Brush={shadow.Brush.GetType().Name}");
        parts.Add($"Offset=({shadow.Offset.X},{shadow.Offset.Y})");
        parts.Add($"Radius={shadow.Radius}");
        parts.Add($"Opacity={shadow.Opacity}");
        return string.Join(", ", parts);
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

        var startedAtUtc = DateTime.UtcNow;
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

        PublishUiOperationSpan(
            "action.set-property",
            startedAtUtc,
            result == "ok",
            result == "ok" ? null : result,
            id,
            new { property = propName });

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

        var startedAtUtc = DateTime.UtcNow;
        var result = await DispatchAsync(() =>
        {
            var el = _treeWalker.GetElementById(body.ElementId, _app);
            if (el == null) return "Element not found";

            switch (el)
            {
                case Button btn:
                    try { btn.SendClicked(); }
                    catch { if (btn is VisualElement ve && !TryNativeTap(ve)) return $"Native tap failed on Button"; }
                    return "ok";
                case ImageButton imgBtn:
                    try { imgBtn.SendClicked(); }
                    catch { if (imgBtn is VisualElement ve && !TryNativeTap(ve)) return $"Native tap failed on ImageButton"; }
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
                case VisualTreeWalker.BackButtonMarker back:
                    back.Navigation.PopAsync();
                    return "ok";
                case VisualTreeWalker.FlyoutButtonMarker flyoutBtn:
                    flyoutBtn.Shell.FlyoutIsPresented = true;
                    return "ok";
                case VisualTreeWalker.ShellFlyoutItemMarker flyoutItem:
                    flyoutItem.Shell.CurrentItem = flyoutItem.Item;
                    return "ok";
                case VisualTreeWalker.ShellTabMarker shellTab:
                    shellTab.Shell.CurrentItem.CurrentItem = shellTab.Section;
                    return "ok";
                case VisualTreeWalker.FlyoutToggleMarker flyoutToggle:
                    flyoutToggle.FlyoutPage.IsPresented = !flyoutToggle.FlyoutPage.IsPresented;
                    return "ok";
                case VisualTreeWalker.TabbedPageTabMarker tab:
                    tab.TabbedPage.CurrentPage = tab.Page;
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

        PublishUiOperationSpan(
            "action.tap",
            startedAtUtc,
            result == "ok",
            result == "ok" ? null : result,
            body.ElementId);

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
    /// Override in platform-specific subclasses for native tap support.
    /// </summary>
    protected virtual bool TryNativeTap(VisualElement ve)
    {
        return false;
    }

    private async Task<HttpResponse> HandleFill(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        var body = request.BodyAs<FillRequest>();
        if (body?.ElementId == null || body.Text == null)
            return HttpResponse.Error("elementId and text are required");

        var startedAtUtc = DateTime.UtcNow;
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

        PublishUiOperationSpan(
            "action.fill",
            startedAtUtc,
            result == "ok",
            result == "ok" ? null : result,
            body.ElementId,
            new { textLength = body.Text.Length });

        return result == "ok" ? HttpResponse.Ok("Text set") : HttpResponse.Error(result);
    }

    private async Task<HttpResponse> HandleClear(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        var body = request.BodyAs<ActionRequest>();
        if (body?.ElementId == null)
            return HttpResponse.Error("elementId is required");

        var startedAtUtc = DateTime.UtcNow;
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

        PublishUiOperationSpan(
            "action.clear",
            startedAtUtc,
            success,
            success ? null : "Element does not accept text input",
            body.ElementId);

        return success ? HttpResponse.Ok("Cleared") : HttpResponse.Error("Element does not accept text input");
    }

    private async Task<HttpResponse> HandleFocus(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        var body = request.BodyAs<ActionRequest>();
        if (body?.ElementId == null)
            return HttpResponse.Error("elementId is required");

        var startedAtUtc = DateTime.UtcNow;
        var success = await DispatchAsync(() =>
        {
            var el = _treeWalker.GetElementById(body.ElementId, _app);
            if (el is not VisualElement ve) return false;
            ve.Focus();
            return true;
        });

        PublishUiOperationSpan(
            "action.focus",
            startedAtUtc,
            success,
            success ? null : "Cannot focus element",
            body.ElementId);

        return success ? HttpResponse.Ok("Focused") : HttpResponse.Error("Cannot focus element");
    }

    private async Task<HttpResponse> HandleNavigate(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        var body = request.BodyAs<NavigateRequest>();
        if (string.IsNullOrEmpty(body?.Route))
            return HttpResponse.Error("route is required");

        var startedAtUtc = DateTime.UtcNow;
        Publish(new ProfilerMarker
        {
            TsUtc = DateTime.UtcNow,
            Type = "navigation.start",
            Name = body.Route,
            PayloadJson = JsonSerializer.Serialize(new { route = body.Route })
        });

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

        Publish(new ProfilerMarker
        {
            TsUtc = DateTime.UtcNow,
            Type = "navigation.end",
            Name = body.Route,
            PayloadJson = JsonSerializer.Serialize(new { route = body.Route, success = result == "ok", error = result == "ok" ? null : result })
        });

        PublishUiOperationSpan(
            "action.navigate",
            startedAtUtc,
            result == "ok",
            result == "ok" ? null : result,
            elementPath: body.Route,
            tags: new { route = body.Route });

        return result == "ok" ? HttpResponse.Ok($"Navigated to {body.Route}") : HttpResponse.Error(result ?? "Navigation failed");
    }

    private async Task<HttpResponse> HandleResize(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        var body = request.BodyAs<ResizeRequest>();
        if (body == null || body.Width <= 0 || body.Height <= 0)
            return HttpResponse.Error("width and height are required (positive integers)");

        var startedAtUtc = DateTime.UtcNow;
        var windowIndex = ParseWindowIndex(request);
        var result = await DispatchAsync(() =>
        {
            var window = GetWindow(windowIndex);
            if (window?.Handler?.PlatformView == null)
                return "No window available";

            try
            {
                // Use platform-specific resize
                TryNativeResize(window, body.Width, body.Height);
                return "ok";
            }
            catch (Exception ex)
            {
                return $"Resize failed: {ex.Message}";
            }
        });

        PublishUiOperationSpan(
            "action.resize",
            startedAtUtc,
            result == "ok",
            result == "ok" ? null : result,
            tags: new { width = body.Width, height = body.Height, windowIndex });

        return result == "ok"
            ? HttpResponse.Json(new { success = true, width = body.Width, height = body.Height })
            : HttpResponse.Error(result);
    }

    /// <summary>
    /// Platform-specific window resize. Override in platform agents for native support.
    /// </summary>
    protected virtual void TryNativeResize(IWindow window, int width, int height)
    {
        // Default: try casting to MAUI Window which has settable Width/Height
        if (window is Window mauiWindow)
        {
            mauiWindow.Width = width;
            mauiWindow.Height = height;
        }
    }

    private record ResizeRequest(int Width, int Height);

    private async Task<HttpResponse> HandleScroll(HttpRequest request)
    {
        if (_app == null) return HttpResponse.Error("Agent not bound to app");

        var body = request.BodyAs<ScrollRequest>();
        if (body == null)
            return HttpResponse.Error("Request body is required");

        var position = ParseScrollToPosition(body.ScrollToPosition);
        var startedAtUtc = DateTime.UtcNow;
        var result = await DispatchAsync(async () =>
        {
            // Priority 1: Scroll by item index on a specific ItemsView
            if (body.ItemIndex.HasValue)
            {
                object? targetObj = null;
                if (!string.IsNullOrEmpty(body.ElementId))
                {
                    targetObj = _treeWalker.GetElementById(body.ElementId, _app);
                    if (targetObj == null) return "Element not found";
                }

                // Find the ItemsView — either the target itself or its ancestor
                var itemsView = targetObj as ItemsView ?? (targetObj is VisualElement tve ? FindAncestor<ItemsView>(tve) : null);
                // Since ListView inherits from ItemsView in .NET 10+, ItemsView check covers both
                if (itemsView == null && targetObj == null)
                {
                    // No element specified — find first ItemsView on the page
                    var window = GetWindow(ParseWindowIndex(request));
                    if (window?.Page != null)
                        itemsView = FindDescendant<ItemsView>(window.Page);
                }

                if (itemsView != null)
                {
                    await ScrollWithTimeoutAsync(
                        () => { itemsView.ScrollTo(body.ItemIndex.Value, body.GroupIndex ?? -1, position, body.Animated); return Task.CompletedTask; },
                        () => { itemsView.ScrollTo(body.ItemIndex.Value, body.GroupIndex ?? -1, position, false); return Task.CompletedTask; });
                    return "ok";
                }

                return "No CollectionView or ListView found for item-index scroll";
            }

            // Priority 2: Scroll element into view
            if (!string.IsNullOrEmpty(body.ElementId))
            {
                var el = _treeWalker.GetElementById(body.ElementId, _app);
                if (el == null) return "Element not found";

                if (el is VisualElement ve)
                {
                    // 2a: Check for ItemsView ancestor — use BindingContext to find item index
                    var ancestorItemsView = FindAncestor<ItemsView>(ve);
                    if (ancestorItemsView != null && ve.BindingContext != null)
                    {
                        var index = GetItemIndex(ancestorItemsView.ItemsSource, ve.BindingContext);
                        if (index >= 0)
                        {
                            await ScrollWithTimeoutAsync(
                                () => { ancestorItemsView.ScrollTo(index, position: position, animate: body.Animated); return Task.CompletedTask; },
                                () => { ancestorItemsView.ScrollTo(index, position: position, animate: false); return Task.CompletedTask; });
                            return "ok";
                        }
                    }

                    // 2b: Check for ScrollView ancestor (existing behavior)
                    var scrollView = FindAncestor<ScrollView>(ve);
                    if (scrollView != null)
                    {
                        await ScrollWithTimeoutAsync(
                            () => scrollView.ScrollToAsync(ve, (ScrollToPosition)position, body.Animated),
                            () => scrollView.ScrollToAsync(ve, (ScrollToPosition)position, false));
                        return "ok";
                    }

                    // 2d: Element is itself a scrollable view — apply delta
                    if (el is ScrollView sv && (body.DeltaX != 0 || body.DeltaY != 0))
                    {
                        var newX = Math.Max(0, sv.ScrollX + body.DeltaX);
                        var newY = Math.Max(0, sv.ScrollY + body.DeltaY);
                        await ScrollWithTimeoutAsync(
                            () => sv.ScrollToAsync(newX, newY, body.Animated),
                            () => sv.ScrollToAsync(newX, newY, false));
                        return "ok";
                    }

                    // 2e: Element is an ItemsView — apply delta via native scroll
                    if (el is ItemsView && (body.DeltaX != 0 || body.DeltaY != 0))
                    {
                        if (await TryNativeScroll(ve, body.DeltaX, body.DeltaY))
                            return "ok";
                        return $"Native scroll not supported on this platform for {el.GetType().Name}";
                    }

                    // 2f: Try native scroll as final fallback
                    if (body.DeltaX != 0 || body.DeltaY != 0)
                    {
                        if (await TryNativeScroll(ve, body.DeltaX, body.DeltaY))
                            return "ok";
                    }
                }

                return $"No scrollable ancestor found for element '{body.ElementId}'";
            }

            // Priority 3: Delta scroll with no element — find first scrollable on current page
            var pageWindow = GetWindow(ParseWindowIndex(request));
            if (pageWindow?.Page == null) return "No page available";

            // Use the current visible page (Shell.CurrentPage or the window page)
            var currentPage = (pageWindow.Page as Shell)?.CurrentPage ?? pageWindow.Page;

            // 3a: Try ItemsView via native scroll first (CollectionView/ListView are more common scroll targets)
            var targetItemsView = FindDescendant<ItemsView>(currentPage);
            if (targetItemsView is VisualElement ive)
            {
                if (await TryNativeScroll(ive, body.DeltaX, body.DeltaY))
                    return "ok";
            }

            // 3b: Try ScrollView on current page
            var targetScroll = FindDescendant<ScrollView>(currentPage);
            if (targetScroll != null)
            {
                var newX = targetScroll.ScrollX + body.DeltaX;
                var newY = targetScroll.ScrollY + body.DeltaY;
                var x = Math.Max(0, newX);
                var y = Math.Max(0, newY);
                await ScrollWithTimeoutAsync(
                    () => targetScroll.ScrollToAsync(x, y, body.Animated),
                    () => targetScroll.ScrollToAsync(x, y, false));
                return "ok";
            }

            return "No scrollable view found on page";
        });

        PublishUiOperationSpan(
            "action.scroll",
            startedAtUtc,
            result == "ok",
            result == "ok" ? null : result,
            body.ElementId,
            new { body.DeltaX, body.DeltaY, body.Animated });

        return result == "ok" ? HttpResponse.Ok("Scrolled") : HttpResponse.Error(result ?? "Scroll failed");
    }

    /// <summary>
    /// Parse a ScrollToPosition string to the MAUI enum value.
    /// </summary>
    private static ScrollToPosition ParseScrollToPosition(string? value)
    {
        if (string.IsNullOrEmpty(value)) return ScrollToPosition.MakeVisible;
        return value.ToLowerInvariant() switch
        {
            "start" => ScrollToPosition.Start,
            "center" => ScrollToPosition.Center,
            "end" => ScrollToPosition.End,
            "makevisible" => ScrollToPosition.MakeVisible,
            _ => ScrollToPosition.MakeVisible
        };
    }

    /// <summary>
    /// Get item from an IEnumerable by index.
    /// </summary>
    private static object? GetItemByIndex(System.Collections.IEnumerable? source, int index)
    {
        if (source == null) return null;
        if (source is System.Collections.IList list && index >= 0 && index < list.Count)
            return list[index];
        var i = 0;
        foreach (var item in source)
        {
            if (i == index) return item;
            i++;
        }
        return null;
    }

    /// <summary>
    /// Find the index of an item in an IEnumerable by reference or equality.
    /// </summary>
    private static int GetItemIndex(System.Collections.IEnumerable? source, object item)
    {
        if (source == null) return -1;
        if (source is System.Collections.IList list)
            return list.IndexOf(item);
        var i = 0;
        foreach (var obj in source)
        {
            if (ReferenceEquals(obj, item) || Equals(obj, item)) return i;
            i++;
        }
        return -1;
    }

    /// <summary>
    /// Try to scroll a native view by pixel delta. Override in platform-specific subclasses.
    /// Returns true if the scroll was handled natively.
    /// </summary>
    protected virtual Task<bool> TryNativeScroll(VisualElement element, double deltaX, double deltaY)
    {
        return Task.FromResult(false);
    }

    /// <summary>
    /// Animated ScrollToAsync can deadlock on iOS when dispatched.
    /// Fall back to non-animated scroll if the animated version doesn't complete in time.
    /// </summary>
    private static async Task ScrollWithTimeoutAsync(Func<Task> animatedScroll, Func<Task> fallbackScroll)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var scrollTask = animatedScroll();
        var completed = await Task.WhenAny(scrollTask, Task.Delay(3000, cts.Token));
        if (completed == scrollTask)
        {
            cts.Cancel();
            return;
        }
        // Animated scroll timed out — fall back to non-animated
        await fallbackScroll();
    }

    private static T? FindAncestor<T>(Element element) where T : Element
    {
        var current = element.Parent;
        while (current != null)
        {
            if (current is T match) return match;
            current = current.Parent;
        }
        return null;
    }

    private static T? FindDescendant<T>(Element element) where T : Element
    {
        if (element is T match) return match;
        if (element is IVisualTreeElement vte)
        {
            foreach (var child in vte.GetVisualChildren())
            {
                if (child is Element childElement)
                {
                    var found = FindDescendant<T>(childElement);
                    if (found != null) return found;
                }
            }
        }
        return null;
    }

    protected async Task<T> DispatchAsync<T>(Func<T> func)
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

    protected async Task<T?> DispatchAsync<T>(Func<Task<T?>> func) where T : class
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

    private object BuildProfilerCapabilitiesPayload()
    {
        var capabilities = _profilerCollector.GetCapabilities();
        return new
        {
            available = IsProfilerFeatureAvailable,
            supportedInBuild = true,
            featureEnabled = _options.EnableProfiler,
            platform = capabilities.Platform,
            managedMemorySupported = capabilities.ManagedMemorySupported,
            nativeMemorySupported = capabilities.NativeMemorySupported,
            gcSupported = capabilities.GcSupported,
            cpuPercentSupported = capabilities.CpuPercentSupported,
            fpsSupported = capabilities.FpsSupported,
            frameTimingsEstimated = capabilities.FrameTimingsEstimated,
            nativeFrameTimingsSupported = capabilities.NativeFrameTimingsSupported,
            jankEventsSupported = capabilities.JankEventsSupported,
            uiThreadStallSupported = capabilities.UiThreadStallSupported,
            threadCountSupported = capabilities.ThreadCountSupported
        };
    }

    private Task<HttpResponse> HandleProfilerCapabilities(HttpRequest request)
        => Task.FromResult(HttpResponse.Json(BuildProfilerCapabilitiesPayload()));

    private async Task<HttpResponse> HandleProfilerStart(HttpRequest request)
    {
        if (!_options.EnableProfiler)
            return HttpResponse.Error("Profiler is disabled. Set AgentOptions.EnableProfiler=true");

        var body = request.BodyAs<StartProfilerRequest>();
        var intervalMs = body?.SampleIntervalMs ?? _options.ProfilerSampleIntervalMs;
        if (intervalMs < 50 || intervalMs > 60_000)
            return HttpResponse.Error("sampleIntervalMs must be between 50 and 60000");

        var session = await StartProfilerAsync(intervalMs);
        return HttpResponse.Json(new { session, capabilities = BuildProfilerCapabilitiesPayload() });
    }

    private async Task<HttpResponse> HandleProfilerStop(HttpRequest request)
    {
        var session = await StopProfilerAsync();
        return HttpResponse.Json(new { session, stoppedAtUtc = DateTime.UtcNow });
    }

    private Task<HttpResponse> HandleProfilerSamples(HttpRequest request)
    {
        if (!long.TryParse(request.QueryParams.GetValueOrDefault("sampleCursor", "0"), out var sampleCursor))
            sampleCursor = 0;
        if (!long.TryParse(request.QueryParams.GetValueOrDefault("markerCursor", "0"), out var markerCursor))
            markerCursor = 0;
        if (!long.TryParse(request.QueryParams.GetValueOrDefault("spanCursor", "0"), out var spanCursor))
            spanCursor = 0;
        if (!int.TryParse(request.QueryParams.GetValueOrDefault("limit", "500"), out var limit))
            limit = 500;

        limit = Math.Clamp(limit, 1, 5000);
        var batch = _profilerSessions.GetBatch(sampleCursor, markerCursor, limit, spanCursor);
        return Task.FromResult(HttpResponse.Json(batch));
    }

    private Task<HttpResponse> HandleProfilerMarker(HttpRequest request)
    {
        if (!IsProfilerFeatureAvailable)
            return Task.FromResult<HttpResponse>(HttpResponse.Error("Profiler is not available"));
        if (!_profilerSessions.IsActive)
            return Task.FromResult<HttpResponse>(HttpResponse.Error("No active profiler session"));

        var body = request.BodyAs<PublishProfilerMarkerRequest>();
        if (string.IsNullOrWhiteSpace(body?.Name))
            return Task.FromResult(HttpResponse.Error("name is required"));

        var marker = new ProfilerMarker
        {
            TsUtc = DateTime.UtcNow,
            Type = string.IsNullOrWhiteSpace(body.Type) ? "user.action" : body.Type!,
            Name = body.Name!,
            PayloadJson = body.PayloadJson
        };

        Publish(marker);
        return Task.FromResult(HttpResponse.Ok("Marker published"));
    }

    private Task<HttpResponse> HandleProfilerSpan(HttpRequest request)
    {
        if (!IsProfilerFeatureAvailable)
            return Task.FromResult<HttpResponse>(HttpResponse.Error("Profiler is not available"));
        if (!_profilerSessions.IsActive)
            return Task.FromResult<HttpResponse>(HttpResponse.Error("No active profiler session"));

        var body = request.BodyAs<PublishProfilerSpanRequest>();
        if (string.IsNullOrWhiteSpace(body?.Name))
            return Task.FromResult(HttpResponse.Error("name is required"));

        var startTsUtc = body.StartTsUtc?.ToUniversalTime() ?? DateTime.UtcNow;
        var endTsUtc = body.EndTsUtc?.ToUniversalTime() ?? startTsUtc;

        var span = new ProfilerSpan
        {
            SpanId = Guid.NewGuid().ToString("N"),
            ParentSpanId = body.ParentSpanId,
            TraceId = body.TraceId,
            StartTsUtc = startTsUtc,
            EndTsUtc = endTsUtc,
            Kind = string.IsNullOrWhiteSpace(body.Kind) ? "ui.operation" : body.Kind!,
            Name = body.Name!,
            Status = string.IsNullOrWhiteSpace(body.Status) ? "ok" : body.Status!,
            ThreadId = body.ThreadId,
            Screen = body.Screen,
            ElementPath = body.ElementPath,
            TagsJson = body.TagsJson,
            Error = body.Error
        };

        Publish(span);
        return Task.FromResult(HttpResponse.Ok("Span published"));
    }

    private Task<HttpResponse> HandleProfilerHotspots(HttpRequest request)
    {
        if (!int.TryParse(request.QueryParams.GetValueOrDefault("limit", "20"), out var limit))
            limit = 20;
        if (!int.TryParse(request.QueryParams.GetValueOrDefault("minDurationMs", "16"), out var minDurationMs))
            minDurationMs = 16;

        limit = Math.Clamp(limit, 1, 200);
        minDurationMs = Math.Clamp(minDurationMs, 0, 60_000);
        var kind = request.QueryParams.GetValueOrDefault("kind");
        var hotspots = _profilerSessions.GetHotspots(limit, minDurationMs, kind);
        return Task.FromResult(HttpResponse.Json(hotspots));
    }

    private async Task<ProfilerSessionInfo> StartProfilerAsync(int intervalMs)
    {
        await _profilerStateGate.WaitAsync();
        try
        {
            var current = _profilerSessions.CurrentSession;
            if (current?.IsActive == true)
                return current;

            _profilerCollector.Start(intervalMs);
            var session = _profilerSessions.Start(intervalMs);
            _lastAutoJankSpanTsUtc = DateTime.MinValue;
            EnsureAutoUiHooks();
            _profilerLoopCts = new CancellationTokenSource();
            _profilerLoopTask = Task.Run(() => RunProfilerLoopAsync(intervalMs, _profilerLoopCts.Token));
            return session;
        }
        finally
        {
            _profilerStateGate.Release();
        }
    }

    private async Task<ProfilerSessionInfo?> StopProfilerAsync()
    {
        await _profilerStateGate.WaitAsync();
        try
        {
            var cts = _profilerLoopCts;
            var loopTask = _profilerLoopTask;
            _profilerLoopCts = null;
            _profilerLoopTask = null;

            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
            }

            if (loopTask != null)
            {
                try
                {
                    await loopTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            _profilerCollector.Stop();
            StopAutoUiHooks();
            return _profilerSessions.Stop();
        }
        finally
        {
            _profilerStateGate.Release();
        }
    }

    private async Task RunProfilerLoopAsync(int intervalMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            EnsureAutoUiHooks();
            if (_profilerCollector.TryCollect(out var sample))
            {
                _profilerSessions.AddSample(sample);
                PublishNativeFrameSignals(sample);
                TryPublishAutoJankSpan(sample);
            }

            await Task.Delay(intervalMs, ct);
        }
    }

    private void PublishNativeFrameSignals(ProfilerSample sample)
    {
        if (sample.JankFrameCount <= 0 && sample.UiThreadStallCount <= 0)
            return;

        if (sample.JankFrameCount > 0)
        {
            Publish(new ProfilerMarker
            {
                TsUtc = sample.TsUtc,
                Type = "ui.frame.jank.native",
                Name = sample.FrameSource,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    jankFrames = sample.JankFrameCount,
                    frameTimeMsP95 = sample.FrameTimeMsP95,
                    worstFrameTimeMs = sample.WorstFrameTimeMs,
                    frameSource = sample.FrameSource,
                    frameQuality = sample.FrameQuality
                })
            });
        }

        if (sample.UiThreadStallCount > 0)
        {
            Publish(new ProfilerMarker
            {
                TsUtc = sample.TsUtc,
                Type = "ui.thread.stall.native",
                Name = sample.FrameSource,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    stallCount = sample.UiThreadStallCount,
                    worstFrameTimeMs = sample.WorstFrameTimeMs,
                    frameSource = sample.FrameSource,
                    frameQuality = sample.FrameQuality
                })
            });
        }
    }

    private void TryPublishAutoJankSpan(ProfilerSample sample)
    {
        var frameMs = sample.FrameTimeMsP95;
        var hasNativeJankSignal = sample.JankFrameCount > 0 || sample.UiThreadStallCount > 0;
        if (!frameMs.HasValue || (frameMs.Value < 20d && !hasNativeJankSignal))
            return;

        var throttleMs = sample.FrameSource.StartsWith("native.", StringComparison.OrdinalIgnoreCase) ? 100d : 250d;
        if (_lastAutoJankSpanTsUtc != DateTime.MinValue
            && (sample.TsUtc - _lastAutoJankSpanTsUtc).TotalMilliseconds < throttleMs)
            return;

        _lastAutoJankSpanTsUtc = sample.TsUtc;
        var (actionName, actionElementPath, actionLagMs) = GetRecentUserAction(sample.TsUtc, TimeSpan.FromSeconds(3));
        var isStall = sample.UiThreadStallCount > 0 || (sample.WorstFrameTimeMs ?? 0d) >= 150d;
        Publish(new ProfilerSpan
        {
            SpanId = Guid.NewGuid().ToString("N"),
            TraceId = _profilerSessions.CurrentSession?.SessionId,
            StartTsUtc = sample.TsUtc.AddMilliseconds(-frameMs.Value),
            EndTsUtc = sample.TsUtc,
            Kind = "ui.operation",
            Name = isStall
                ? (string.IsNullOrWhiteSpace(actionName) ? "ui.thread.stall" : "ui.action.stall")
                : (string.IsNullOrWhiteSpace(actionName) ? "ui.frame.jank" : "ui.action.jank"),
            Status = isStall ? "error" : "ok",
            ThreadId = Environment.CurrentManagedThreadId,
            Screen = Shell.Current?.CurrentState?.Location?.ToString(),
            ElementPath = actionElementPath,
            TagsJson = JsonSerializer.Serialize(new
            {
                frameTimeMsP95 = frameMs.Value,
                fps = sample.Fps,
                frameSource = sample.FrameSource,
                frameQuality = sample.FrameQuality,
                jankFrameCount = sample.JankFrameCount,
                uiThreadStallCount = sample.UiThreadStallCount,
                worstFrameTimeMs = sample.WorstFrameTimeMs,
                actionName,
                actionLagMs
            })
        });
    }

    private void EnsureAutoUiHooks()
    {
        if (!IsProfilerFeatureAvailable || !_profilerSessions.IsActive || _dispatcher == null || !_options.EnableHighLevelUiHooks)
            return;

        var now = DateTime.UtcNow;
        if ((now - _lastUiHookScanTsUtc).TotalMilliseconds < UiHookScanIntervalMs)
            return;
        if (Interlocked.CompareExchange(ref _uiHookScanInFlight, 1, 0) != 0)
            return;

        _lastUiHookScanTsUtc = now;

        void Scan()
        {
            try
            {
                TryEnsureShellNavigationHooks();
                ScanUiTreeForHooks();
            }
            catch (Exception ex)
            {
                Publish(new ProfilerMarker
                {
                    TsUtc = DateTime.UtcNow,
                    Type = "profiler.hook.error",
                    Name = "ui-hook-scan",
                    PayloadJson = JsonSerializer.Serialize(new { error = ex.GetBaseException().Message })
                });
            }
            finally
            {
                Interlocked.Exchange(ref _uiHookScanInFlight, 0);
            }
        }

        if (_dispatcher.IsDispatchRequired)
        {
            _dispatcher.Dispatch(Scan);
        }
        else
        {
            Scan();
        }
    }

    private void StopAutoUiHooks()
    {
        if (_hookedShell != null)
        {
            _hookedShell.Navigating -= OnShellNavigating;
            _hookedShell.Navigated -= OnShellNavigated;
            _hookedShell = null;
        }

        lock (_uiHookGate)
        {
            foreach (var unsubscribe in _uiHookUnsubscribers)
                unsubscribe();
            _uiHookUnsubscribers.Clear();
            _uiHookGeneration = _uiHookGeneration == int.MaxValue ? 1 : _uiHookGeneration + 1;
            _navigationStartedAtUtc = null;
            _navigationTargetRoute = null;
            _lastUserActionTsUtc = DateTime.MinValue;
            _lastUserActionName = null;
            _lastUserActionElementPath = null;
        }
    }

    private void TryEnsureShellNavigationHooks()
    {
        var shell = Shell.Current;
        if (shell == null || ReferenceEquals(shell, _hookedShell))
            return;

        if (_hookedShell != null)
        {
            _hookedShell.Navigating -= OnShellNavigating;
            _hookedShell.Navigated -= OnShellNavigated;
        }

        shell.Navigating += OnShellNavigating;
        shell.Navigated += OnShellNavigated;
        _hookedShell = shell;
    }

    private void ScanUiTreeForHooks()
    {
        if (_app is not IVisualTreeElement appElement)
            return;

        foreach (var child in appElement.GetVisualChildren())
        {
            if (child is Element element)
                ScanElementForHooks(element);
        }
    }

    private void ScanElementForHooks(Element element)
    {
        var detailedHooksEnabled = _options.EnableDetailedUiHooks;
        switch (element)
        {
            case Button button when detailedHooksEnabled:
                AttachButtonHook(button);
                break;
            case ImageButton imageButton when detailedHooksEnabled:
                AttachImageButtonHook(imageButton);
                break;
            case Entry entry when detailedHooksEnabled:
                AttachEntryHook(entry);
                break;
            case SearchBar searchBar when detailedHooksEnabled:
                AttachSearchBarHook(searchBar);
                break;
            case CheckBox checkBox when detailedHooksEnabled:
                AttachCheckBoxHook(checkBox);
                break;
            case Switch toggle when detailedHooksEnabled:
                AttachSwitchHook(toggle);
                break;
            case Picker picker when detailedHooksEnabled:
                AttachPickerHook(picker);
                break;
            case ScrollView scrollView:
                AttachScrollViewHook(scrollView);
                break;
            case CollectionView collectionView:
                AttachCollectionViewHook(collectionView);
                break;
            case Page page:
                AttachPageHooks(page);
                break;
        }

        if (detailedHooksEnabled && element is View view)
        {
            foreach (var tapGesture in view.GestureRecognizers.OfType<TapGestureRecognizer>())
                AttachTapGestureHook(tapGesture);
        }

        if (element is not IVisualTreeElement visualElement)
            return;

        foreach (var child in visualElement.GetVisualChildren())
        {
            if (child is Element childElement)
                ScanElementForHooks(childElement);
        }
    }

    private bool TryRegisterUiHook(BindableObject target, string hookKey, Action? unsubscribe = null)
    {
        lock (_uiHookGate)
        {
            var state = _uiHookStates.GetOrCreateValue(target);
            if (state.Generation != _uiHookGeneration)
            {
                state.Generation = _uiHookGeneration;
                state.HookKeys.Clear();
            }

            if (!state.HookKeys.Add(hookKey))
                return false;

            if (unsubscribe != null)
                _uiHookUnsubscribers.Add(unsubscribe);

            return true;
        }
    }

    private void AttachButtonHook(Button button)
    {
        if (!TryRegisterUiHook(button, "Button.Clicked", () => button.Clicked -= OnButtonClicked))
            return;
        button.Clicked += OnButtonClicked;
    }

    private void AttachImageButtonHook(ImageButton imageButton)
    {
        if (!TryRegisterUiHook(imageButton, "ImageButton.Clicked", () => imageButton.Clicked -= OnImageButtonClicked))
            return;
        imageButton.Clicked += OnImageButtonClicked;
    }

    private void AttachEntryHook(Entry entry)
    {
        if (!TryRegisterUiHook(entry, "Entry.Completed", () => entry.Completed -= OnEntryCompleted))
            return;
        entry.Completed += OnEntryCompleted;
    }

    private void AttachSearchBarHook(SearchBar searchBar)
    {
        if (!TryRegisterUiHook(searchBar, "SearchBar.SearchButtonPressed", () => searchBar.SearchButtonPressed -= OnSearchBarSearchButtonPressed))
            return;
        searchBar.SearchButtonPressed += OnSearchBarSearchButtonPressed;
    }

    private void AttachCheckBoxHook(CheckBox checkBox)
    {
        if (!TryRegisterUiHook(checkBox, "CheckBox.CheckedChanged", () => checkBox.CheckedChanged -= OnCheckBoxCheckedChanged))
            return;
        checkBox.CheckedChanged += OnCheckBoxCheckedChanged;
    }

    private void AttachSwitchHook(Switch toggle)
    {
        if (!TryRegisterUiHook(toggle, "Switch.Toggled", () => toggle.Toggled -= OnSwitchToggled))
            return;
        toggle.Toggled += OnSwitchToggled;
    }

    private void AttachPickerHook(Picker picker)
    {
        if (!TryRegisterUiHook(picker, "Picker.SelectedIndexChanged", () => picker.SelectedIndexChanged -= OnPickerSelectedIndexChanged))
            return;
        picker.SelectedIndexChanged += OnPickerSelectedIndexChanged;
    }

    private void AttachCollectionViewHook(CollectionView collectionView)
    {
        if (!TryRegisterUiHook(collectionView, "CollectionView.SelectionChanged", () => collectionView.SelectionChanged -= OnCollectionViewSelectionChanged))
            return;
        collectionView.SelectionChanged += OnCollectionViewSelectionChanged;
        if (TryRegisterUiHook(collectionView, "CollectionView.Scrolled", () => collectionView.Scrolled -= OnCollectionViewScrolled))
            collectionView.Scrolled += OnCollectionViewScrolled;
        AttachRenderHooks(collectionView, "collection");
    }

    private void AttachScrollViewHook(ScrollView scrollView)
    {
        if (TryRegisterUiHook(scrollView, "ScrollView.Scrolled", () => scrollView.Scrolled -= OnScrollViewScrolled))
            scrollView.Scrolled += OnScrollViewScrolled;
        AttachRenderHooks(scrollView, "scroll");
    }

    private void AttachRenderHooks(VisualElement element, string role)
    {
        var renderState = _elementRenderStates.GetOrCreateValue(element);
        if (renderState.TrackingStartedAtUtc == default)
            renderState.TrackingStartedAtUtc = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(renderState.Role))
            renderState.Role = role;

        if (TryRegisterUiHook(element, $"{role}.SizeChanged", () => element.SizeChanged -= OnTrackedElementSizeChanged))
            element.SizeChanged += OnTrackedElementSizeChanged;
        if (TryRegisterUiHook(element, $"{role}.MeasureInvalidated", () => element.MeasureInvalidated -= OnTrackedElementMeasureInvalidated))
            element.MeasureInvalidated += OnTrackedElementMeasureInvalidated;
    }

    private void AttachTapGestureHook(TapGestureRecognizer tapGesture)
    {
        if (!TryRegisterUiHook(tapGesture, "TapGestureRecognizer.Tapped", () => tapGesture.Tapped -= OnTapGestureTapped))
            return;
        tapGesture.Tapped += OnTapGestureTapped;
    }

    private void AttachPageHooks(Page page)
    {
        if (TryRegisterUiHook(page, "Page.Appearing", () => page.Appearing -= OnPageAppearing))
            page.Appearing += OnPageAppearing;
        if (TryRegisterUiHook(page, "Page.Disappearing", () => page.Disappearing -= OnPageDisappearing))
            page.Disappearing += OnPageDisappearing;
        if (TryRegisterUiHook(page, "Page.SizeChanged", () => page.SizeChanged -= OnPageSizeChanged))
            page.SizeChanged += OnPageSizeChanged;
        if (TryRegisterUiHook(page, "Page.MeasureInvalidated", () => page.MeasureInvalidated -= OnPageMeasureInvalidated))
            page.MeasureInvalidated += OnPageMeasureInvalidated;
        AttachRenderHooks(page, "page");
    }

    private void OnButtonClicked(object? sender, EventArgs args)
        => TrackUiInteraction("ui.input.button.click", sender as Element);

    private void OnImageButtonClicked(object? sender, EventArgs args)
        => TrackUiInteraction("ui.input.image-button.click", sender as Element);

    private void OnEntryCompleted(object? sender, EventArgs args)
        => TrackUiInteraction("ui.input.entry.complete", sender as Element);

    private void OnSearchBarSearchButtonPressed(object? sender, EventArgs args)
        => TrackUiInteraction("ui.input.search.submit", sender as Element);

    private void OnCheckBoxCheckedChanged(object? sender, CheckedChangedEventArgs args)
        => TrackUiInteraction("ui.input.checkbox.toggle", sender as Element, new { value = args.Value });

    private void OnSwitchToggled(object? sender, ToggledEventArgs args)
        => TrackUiInteraction("ui.input.switch.toggle", sender as Element, new { value = args.Value });

    private void OnPickerSelectedIndexChanged(object? sender, EventArgs args)
    {
        var picker = sender as Picker;
        TrackUiInteraction("ui.input.picker.select", picker, new { selectedIndex = picker?.SelectedIndex });
    }

    private void OnCollectionViewSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        var selectionCount = args.CurrentSelection?.Count ?? 0;
        TrackUiInteraction("ui.input.collection.select", sender as Element, new { selectionCount });
    }

    private void OnCollectionViewScrolled(object? sender, ItemsViewScrolledEventArgs args)
    {
        if (sender is not CollectionView collectionView)
            return;

        var horizontalOffset = TryReadDoubleProperty(args, "HorizontalOffset");
        var verticalOffset = TryReadDoubleProperty(args, "VerticalOffset");
        var firstVisibleItem = TryReadIntProperty(args, "FirstVisibleItemIndex");
        var lastVisibleItem = TryReadIntProperty(args, "LastVisibleItemIndex");

        TrackScrollEvent(
            collectionView,
            sourceName: "collection-view",
            offsetX: horizontalOffset,
            offsetY: verticalOffset,
            firstVisibleIndex: firstVisibleItem,
            lastVisibleIndex: lastVisibleItem);
    }

    private void OnScrollViewScrolled(object? sender, ScrolledEventArgs args)
    {
        if (sender is not ScrollView scrollView)
            return;

        TrackScrollEvent(
            scrollView,
            sourceName: "scroll-view",
            offsetX: args.ScrollX,
            offsetY: args.ScrollY);
    }

    private void TrackScrollEvent(
        BindableObject source,
        string sourceName,
        double offsetX,
        double offsetY,
        int? firstVisibleIndex = null,
        int? lastVisibleIndex = null)
    {
        if (!IsProfilerFeatureAvailable || !_profilerSessions.IsActive)
            return;

        var now = DateTime.UtcNow;
        var state = _scrollBatchStates.GetOrCreateValue(source);
        var elementPath = BuildElementPath(source as Element);

        if (!state.IsActive)
        {
            state.IsActive = true;
            state.StartedAtUtc = now;
            state.StartOffsetX = offsetX;
            state.StartOffsetY = offsetY;
            state.EventCount = 0;
            state.StartFirstVisibleIndex = firstVisibleIndex;
            state.StartLastVisibleIndex = lastVisibleIndex;
            RememberUserAction("ui.scroll", elementPath, now);
            Publish(new ProfilerMarker
            {
                TsUtc = now,
                Type = "ui.scroll.start",
                Name = sourceName,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    source = sourceName,
                    elementPath,
                    offsetX,
                    offsetY,
                    firstVisibleIndex,
                    lastVisibleIndex
                })
            });
        }

        state.EventCount++;
        state.LastEventAtUtc = now;
        state.LastOffsetX = offsetX;
        state.LastOffsetY = offsetY;
        state.LastFirstVisibleIndex = firstVisibleIndex;
        state.LastLastVisibleIndex = lastVisibleIndex;
        var flushVersion = ++state.FlushVersion;

        if (_dispatcher != null)
        {
            _dispatcher.DispatchDelayed(
                TimeSpan.FromMilliseconds(220),
                () => TryFlushScrollBatch(source, sourceName, state, flushVersion));
        }
        else
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(220);
                TryFlushScrollBatch(source, sourceName, state, flushVersion);
            });
        }
    }

    private void TryFlushScrollBatch(BindableObject source, string sourceName, ScrollBatchState state, int flushVersion)
    {
        if (!state.IsActive || flushVersion != state.FlushVersion)
            return;
        if ((DateTime.UtcNow - state.LastEventAtUtc).TotalMilliseconds < 180)
            return;

        state.IsActive = false;
        var startTsUtc = state.StartedAtUtc;
        var endTsUtc = state.LastEventAtUtc;
        if (startTsUtc == default || endTsUtc < startTsUtc)
            return;

        var deltaX = state.LastOffsetX - state.StartOffsetX;
        var deltaY = state.LastOffsetY - state.StartOffsetY;
        var visibleShift = ComputeVisibleShift(state);
        var elementPath = BuildElementPath(source as Element);

        Publish(new ProfilerMarker
        {
            TsUtc = endTsUtc,
            Type = "ui.scroll.end",
            Name = sourceName,
            PayloadJson = JsonSerializer.Serialize(new
            {
                source = sourceName,
                elementPath,
                deltaX,
                deltaY,
                visibleShift,
                events = state.EventCount
            })
        });

        Publish(new ProfilerSpan
        {
            SpanId = Guid.NewGuid().ToString("N"),
            TraceId = _profilerSessions.CurrentSession?.SessionId,
            StartTsUtc = startTsUtc,
            EndTsUtc = endTsUtc,
            Kind = "ui.scroll",
            Name = "ui.scroll.batch",
            Status = "ok",
            ThreadId = Environment.CurrentManagedThreadId,
            Screen = Shell.Current?.CurrentState?.Location?.ToString(),
            ElementPath = elementPath,
            TagsJson = JsonSerializer.Serialize(new
            {
                source = sourceName,
                events = state.EventCount,
                startOffsetX = state.StartOffsetX,
                startOffsetY = state.StartOffsetY,
                endOffsetX = state.LastOffsetX,
                endOffsetY = state.LastOffsetY,
                deltaX,
                deltaY,
                startFirstVisibleIndex = state.StartFirstVisibleIndex,
                startLastVisibleIndex = state.StartLastVisibleIndex,
                endFirstVisibleIndex = state.LastFirstVisibleIndex,
                endLastVisibleIndex = state.LastLastVisibleIndex,
                visibleShift
            })
        });
    }

    private static int? ComputeVisibleShift(ScrollBatchState state)
    {
        if (!state.StartFirstVisibleIndex.HasValue || !state.LastFirstVisibleIndex.HasValue)
            return null;

        return Math.Abs(state.LastFirstVisibleIndex.Value - state.StartFirstVisibleIndex.Value);
    }

    private void OnTrackedElementMeasureInvalidated(object? sender, EventArgs args)
    {
        if (sender is not VisualElement element)
            return;

        var state = _elementRenderStates.GetOrCreateValue(element);
        state.MeasureInvalidatedCount++;
    }

    private void OnTrackedElementSizeChanged(object? sender, EventArgs args)
    {
        if (sender is not VisualElement element)
            return;

        var state = _elementRenderStates.GetOrCreateValue(element);
        state.SizeChangedCount++;
        if (state.FirstLayoutPublished || element.Width <= 0 || element.Height <= 0)
            return;

        if (state.TrackingStartedAtUtc == default)
            state.TrackingStartedAtUtc = DateTime.UtcNow;

        state.FirstLayoutPublished = true;
        PublishUiOperationSpan(
            "ui.render.first-layout",
            state.TrackingStartedAtUtc,
            true,
            null,
            BuildElementPath(element),
            new
            {
                role = state.Role,
                viewType = element.GetType().Name,
                width = element.Width,
                height = element.Height,
                sizeChangedCount = state.SizeChangedCount,
                measureInvalidatedCount = state.MeasureInvalidatedCount
            });
    }

    private void OnTapGestureTapped(object? sender, TappedEventArgs args)
    {
        var parameter = args.Parameter?.ToString();
        TrackUiInteraction("ui.input.tap-gesture", sender as Element, new { parameter });
    }

    private void OnPageAppearing(object? sender, EventArgs args)
    {
        if (sender is not Page page)
            return;

        var now = DateTime.UtcNow;
        var route = Shell.Current?.CurrentState?.Location?.ToString();
        var state = _pageLifecycleStates.GetOrCreateValue(page);
        state.AppearingAtUtc = now;
        state.Route = route;
        state.FirstLayoutPublished = false;
        state.SizeChangedCount = 0;
        state.MeasureInvalidatedCount = 0;

        TrackUiInteraction("ui.page.appearing", page, new { route, page = page.GetType().Name });
        TryPublishNavigationToAppearing(page, route);
    }

    private void OnPageDisappearing(object? sender, EventArgs args)
    {
        if (sender is not Page page)
            return;

        var route = Shell.Current?.CurrentState?.Location?.ToString();
        TrackUiInteraction("ui.page.disappearing", page, new { route, page = page.GetType().Name });
    }

    private void OnPageMeasureInvalidated(object? sender, EventArgs args)
    {
        if (sender is not Page page)
            return;

        var state = _pageLifecycleStates.GetOrCreateValue(page);
        state.MeasureInvalidatedCount++;
    }

    private void OnPageSizeChanged(object? sender, EventArgs args)
    {
        if (sender is not Page page)
            return;

        var now = DateTime.UtcNow;
        var state = _pageLifecycleStates.GetOrCreateValue(page);
        state.SizeChangedCount++;
        if (state.FirstLayoutPublished || page.Width <= 0 || page.Height <= 0)
            return;

        state.FirstLayoutPublished = true;
        var startTsUtc = state.AppearingAtUtc == default ? now : state.AppearingAtUtc;
        var route = state.Route ?? Shell.Current?.CurrentState?.Location?.ToString();

        PublishUiOperationSpan(
            "ui.page.first-layout",
            startTsUtc,
            true,
            null,
            BuildElementPath(page),
            new
            {
                route,
                page = page.GetType().Name,
                width = page.Width,
                height = page.Height,
                sizeChangedCount = state.SizeChangedCount,
                measureInvalidatedCount = state.MeasureInvalidatedCount
            });

        TryPublishNavigationToFirstLayout(page, route);
    }

    private void OnShellNavigating(object? sender, ShellNavigatingEventArgs args)
    {
        var startedAtUtc = DateTime.UtcNow;
        var targetRoute = TryReadNavigationRoute(args, "Target")
            ?? Shell.Current?.CurrentState?.Location?.ToString()
            ?? "unknown";

        lock (_uiHookGate)
        {
            _navigationStartedAtUtc = startedAtUtc;
            _navigationTargetRoute = targetRoute;
        }
        RememberUserAction("navigation.start", targetRoute, startedAtUtc);

        Publish(new ProfilerMarker
        {
            TsUtc = startedAtUtc,
            Type = "navigation.start",
            Name = targetRoute,
            PayloadJson = JsonSerializer.Serialize(new { route = targetRoute })
        });
    }

    private void OnShellNavigated(object? sender, ShellNavigatedEventArgs args)
    {
        var endedAtUtc = DateTime.UtcNow;
        DateTime startedAtUtc;
        string route;

        lock (_uiHookGate)
        {
            startedAtUtc = _navigationStartedAtUtc ?? endedAtUtc;
            route = _navigationTargetRoute
                ?? TryReadNavigationRoute(args, "Current")
                ?? Shell.Current?.CurrentState?.Location?.ToString()
                ?? "unknown";
        }

        var source = TryReadNavigationSource(args) ?? "unknown";
        var currentPage = Shell.Current?.CurrentPage?.GetType().Name;

        Publish(new ProfilerMarker
        {
            TsUtc = endedAtUtc,
            Type = "navigation.end",
            Name = route,
            PayloadJson = JsonSerializer.Serialize(new { route, source, page = currentPage })
        });
        RememberUserAction("navigation.route", route, endedAtUtc);

        PublishUiOperationSpan(
            "navigation.shell.completed",
            startedAtUtc,
            true,
            null,
            route,
            new { route, source, page = currentPage });
    }

    private void TryPublishNavigationToAppearing(Page page, string? route)
    {
        DateTime? navigationStartedAtUtc;
        string? navigationRoute;
        lock (_uiHookGate)
        {
            navigationStartedAtUtc = _navigationStartedAtUtc;
            navigationRoute = _navigationTargetRoute;
        }

        if (!navigationStartedAtUtc.HasValue)
            return;

        PublishUiOperationSpan(
            "navigation.to-page-appearing",
            navigationStartedAtUtc.Value,
            true,
            null,
            BuildElementPath(page),
            new
            {
                targetRoute = navigationRoute,
                currentRoute = route,
                page = page.GetType().Name
            });
    }

    private void TryPublishNavigationToFirstLayout(Page page, string? route)
    {
        DateTime? navigationStartedAtUtc;
        string? navigationRoute;
        lock (_uiHookGate)
        {
            navigationStartedAtUtc = _navigationStartedAtUtc;
            navigationRoute = _navigationTargetRoute;
            _navigationStartedAtUtc = null;
            _navigationTargetRoute = null;
        }

        if (!navigationStartedAtUtc.HasValue)
            return;

        PublishUiOperationSpan(
            "navigation.to-first-layout",
            navigationStartedAtUtc.Value,
            true,
            null,
            BuildElementPath(page),
            new
            {
                targetRoute = navigationRoute,
                currentRoute = route,
                page = page.GetType().Name
            });
    }

    private void TrackUiInteraction(string name, Element? element, object? tags = null)
    {
        if (!IsProfilerFeatureAvailable || !_profilerSessions.IsActive)
            return;

        var startedAtUtc = DateTime.UtcNow;
        var elementPath = BuildElementPath(element);
        var markerPayload = JsonSerializer.Serialize(new
        {
            name,
            elementPath,
            tags
        });

        Publish(new ProfilerMarker
        {
            TsUtc = startedAtUtc,
            Type = "user.action",
            Name = name,
            PayloadJson = markerPayload
        });

        RememberUserAction(name, elementPath, startedAtUtc);

        if (_dispatcher != null)
        {
            _dispatcher.DispatchDelayed(
                TimeSpan.FromMilliseconds(1),
                () => PublishUiOperationSpan(name, startedAtUtc, true, null, elementPath, tags));
            return;
        }

        PublishUiOperationSpan(name, startedAtUtc, true, null, elementPath, tags);
    }

    private static string? BuildElementPath(Element? element)
    {
        if (element == null)
            return null;

        if (!string.IsNullOrWhiteSpace(element.AutomationId))
            return $"{element.GetType().Name}#{element.AutomationId}";
        if (element is Page page && !string.IsNullOrWhiteSpace(page.Title))
            return $"{page.GetType().Name}:{page.Title}";
        if (element is VisualElement visualElement && !string.IsNullOrWhiteSpace(visualElement.StyleId))
            return $"{visualElement.GetType().Name}[{visualElement.StyleId}]";

        return element.GetType().Name;
    }

    private void RememberUserAction(string name, string? elementPath, DateTime timestampUtc)
    {
        lock (_uiHookGate)
        {
            _lastUserActionTsUtc = timestampUtc;
            _lastUserActionName = name;
            _lastUserActionElementPath = elementPath;
        }
    }

    private (string? ActionName, string? ElementPath, double? LagMs) GetRecentUserAction(DateTime sampleTsUtc, TimeSpan maxAge)
    {
        lock (_uiHookGate)
        {
            if (_lastUserActionTsUtc == DateTime.MinValue || string.IsNullOrWhiteSpace(_lastUserActionName))
                return (null, null, null);

            var lag = sampleTsUtc - _lastUserActionTsUtc;
            if (lag < TimeSpan.Zero || lag > maxAge)
                return (null, null, null);

            return (_lastUserActionName, _lastUserActionElementPath, lag.TotalMilliseconds);
        }
    }

    private static double TryReadDoubleProperty(object instance, string propertyName)
    {
        var value = instance.GetType().GetProperty(propertyName)?.GetValue(instance);
        return value switch
        {
            double asDouble => asDouble,
            float asFloat => asFloat,
            int asInt => asInt,
            long asLong => asLong,
            _ => 0d
        };
    }

    private static int? TryReadIntProperty(object instance, string propertyName)
    {
        var value = instance.GetType().GetProperty(propertyName)?.GetValue(instance);
        return value switch
        {
            int asInt => asInt,
            long asLong => (int)asLong,
            short asShort => asShort,
            _ => null
        };
    }

    private static string? TryReadNavigationRoute(object eventArgs, string statePropertyName)
    {
        var state = eventArgs.GetType().GetProperty(statePropertyName)?.GetValue(eventArgs);
        if (state == null)
            return null;

        var location = state.GetType().GetProperty("Location")?.GetValue(state);
        return location?.ToString() ?? state.ToString();
    }

    private static string? TryReadNavigationSource(object eventArgs)
        => eventArgs.GetType().GetProperty("Source")?.GetValue(eventArgs)?.ToString();

    public void Publish(ProfilerMarker marker)
    {
        if (!IsProfilerFeatureAvailable || !_profilerSessions.IsActive)
            return;

        if (marker.TsUtc == default)
            marker.TsUtc = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(marker.Type))
            marker.Type = "user.action";
        if (string.IsNullOrWhiteSpace(marker.Name))
            marker.Name = marker.Type;

        _profilerSessions.AddMarker(marker);
    }

    public void Publish(ProfilerSpan span)
    {
        if (!IsProfilerFeatureAvailable || !_profilerSessions.IsActive)
            return;

        if (string.IsNullOrWhiteSpace(span.Kind))
            span.Kind = "ui.operation";
        if (string.IsNullOrWhiteSpace(span.Name))
            span.Name = span.Kind;
        if (string.IsNullOrWhiteSpace(span.Status))
            span.Status = "ok";
        if (span.StartTsUtc == default)
            span.StartTsUtc = DateTime.UtcNow;
        if (span.EndTsUtc == default || span.EndTsUtc < span.StartTsUtc)
            span.EndTsUtc = span.StartTsUtc;
        if (span.ThreadId == null)
            span.ThreadId = Environment.CurrentManagedThreadId;

        _profilerSessions.AddSpan(span);
    }

    private void PublishUiOperationSpan(
        string name,
        DateTime startedAtUtc,
        bool success,
        string? error = null,
        string? elementPath = null,
        object? tags = null)
    {
        var endTsUtc = DateTime.UtcNow;
        var route = Shell.Current?.CurrentState?.Location?.ToString();
        var span = new ProfilerSpan
        {
            SpanId = Guid.NewGuid().ToString("N"),
            TraceId = _profilerSessions.CurrentSession?.SessionId,
            StartTsUtc = startedAtUtc,
            EndTsUtc = endTsUtc,
            Kind = "ui.operation",
            Name = name,
            Status = success ? "ok" : "error",
            ThreadId = Environment.CurrentManagedThreadId,
            Screen = route,
            ElementPath = elementPath,
            TagsJson = tags == null ? null : JsonSerializer.Serialize(tags),
            Error = error
        };

        Publish(span);
    }

    private void HandleCapturedNetworkRequest(NetworkRequestEntry entry)
    {
        if (!IsProfilerFeatureAvailable || !_profilerSessions.IsActive)
            return;

        var endTimestampUtc = entry.Timestamp.UtcDateTime;
        var startTimestampUtc = endTimestampUtc - TimeSpan.FromMilliseconds(Math.Max(0, entry.DurationMs));
        var markerName = $"{entry.Method} {entry.Path ?? entry.Url}";

        Publish(new ProfilerMarker
        {
            TsUtc = startTimestampUtc,
            Type = "network.request.start",
            Name = markerName,
            PayloadJson = JsonSerializer.Serialize(new
            {
                id = entry.Id,
                method = entry.Method,
                url = entry.Url,
                host = entry.Host
            })
        });

        Publish(new ProfilerMarker
        {
            TsUtc = endTimestampUtc,
            Type = "network.request.end",
            Name = markerName,
            PayloadJson = JsonSerializer.Serialize(new
            {
                id = entry.Id,
                method = entry.Method,
                url = entry.Url,
                statusCode = entry.StatusCode,
                durationMs = entry.DurationMs,
                error = entry.Error
            })
        });

        if (entry.DurationMs >= 50 || !string.IsNullOrWhiteSpace(entry.Error))
        {
            Publish(new ProfilerSpan
            {
                SpanId = Guid.NewGuid().ToString("N"),
                TraceId = _profilerSessions.CurrentSession?.SessionId,
                StartTsUtc = startTimestampUtc,
                EndTsUtc = endTimestampUtc,
                Kind = "network.request",
                Name = markerName,
                Status = string.IsNullOrWhiteSpace(entry.Error) ? "ok" : "error",
                ThreadId = Environment.CurrentManagedThreadId,
                Screen = Shell.Current?.CurrentState?.Location?.ToString(),
                TagsJson = JsonSerializer.Serialize(new
                {
                    id = entry.Id,
                    method = entry.Method,
                    host = entry.Host,
                    statusCode = entry.StatusCode
                }),
                Error = entry.Error
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkStore.OnRequestCaptured -= HandleCapturedNetworkRequest;
        StopAutoUiHooks();
        _profilerLoopCts?.Cancel();
        _profilerLoopCts?.Dispose();
        _profilerCollector.Stop();
        _profilerStateGate.Dispose();
        _brokerRegistration?.Dispose();
        _server.Dispose();
        _logProvider?.Dispose();
    }

    // ── Network monitoring endpoints ──

    private Task<HttpResponse> HandleNetworkList(HttpRequest request)
    {
        var limit = int.TryParse(request.QueryParams.GetValueOrDefault("limit", "100"), out var l) ? l : 100;
        var host = request.QueryParams.GetValueOrDefault("host");
        var method = request.QueryParams.GetValueOrDefault("method");
        int? status = request.QueryParams.TryGetValue("status", out var s) && int.TryParse(s, out var si) ? si : null;

        var entries = NetworkStore.GetRecent(limit, host, method, status);
        // Return summary-only (no headers/body) for the list
        var summaries = entries.Select(e => e.ToSummary()).ToList();
        return Task.FromResult(HttpResponse.Json(summaries));
    }

    private Task<HttpResponse> HandleNetworkDetail(HttpRequest request)
    {
        var id = request.RouteParams.GetValueOrDefault("id");
        if (string.IsNullOrEmpty(id))
            return Task.FromResult(HttpResponse.Error("Missing request ID"));

        var entry = NetworkStore.GetById(id);
        if (entry == null)
            return Task.FromResult(HttpResponse.NotFound($"Network request '{id}' not found"));

        return Task.FromResult(HttpResponse.Json(entry));
    }

    private Task<HttpResponse> HandleNetworkClear(HttpRequest request)
    {
        NetworkStore.Clear();
        return Task.FromResult(HttpResponse.Ok("Network request buffer cleared"));
    }

    private async Task HandleNetworkWebSocket(
        System.Net.Sockets.TcpClient client,
        System.Net.Sockets.NetworkStream stream,
        HttpRequest request,
        CancellationToken ct)
    {
        // Send replay of recent entries
        var recent = NetworkStore.GetRecent(100);
        var replayMsg = JsonSerializer.Serialize(new
        {
            type = "replay",
            entries = recent.Select(e => e.ToSummary())
        });
        await AgentHttpServer.WebSocketSendTextAsync(stream, replayMsg, ct);

        // Subscribe to live entries
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sendQueue = new System.Collections.Concurrent.ConcurrentQueue<Network.NetworkRequestEntry>();

        void OnRequest(Network.NetworkRequestEntry entry) => sendQueue.Enqueue(entry);
        NetworkStore.OnRequestCaptured += OnRequest;

        try
        {
            // Read loop (handles client messages + detects disconnection)
            var readTask = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var msg = await AgentHttpServer.WebSocketReadTextAsync(stream, cts.Token);
                    if (msg == null) { await cts.CancelAsync(); break; }

                    try
                    {
                        using var doc = JsonDocument.Parse(msg);
                        var msgType = doc.RootElement.GetProperty("type").GetString();

                        if (msgType == "get_details" && doc.RootElement.TryGetProperty("id", out var idEl))
                        {
                            var id = idEl.GetString();
                            var entry = id != null ? NetworkStore.GetById(id) : null;
                            var resp = JsonSerializer.Serialize(new { type = "details", entry });
                            await AgentHttpServer.WebSocketSendTextAsync(stream, resp, cts.Token);
                        }
                        else if (msgType == "clear")
                        {
                            NetworkStore.Clear();
                        }
                    }
                    catch { }
                }
            }, cts.Token);

            // Send loop — drain queue and send pings periodically
            var lastPing = DateTime.UtcNow;
            while (!cts.Token.IsCancellationRequested)
            {
                while (sendQueue.TryDequeue(out var entry))
                {
                    try
                    {
                        var json = JsonSerializer.Serialize(new { type = "request", entry = entry.ToSummary() });
                        await AgentHttpServer.WebSocketSendTextAsync(stream, json, cts.Token);
                    }
                    catch { await cts.CancelAsync(); break; }
                }

                // Send WebSocket ping every 15 seconds to keep connection alive
                if ((DateTime.UtcNow - lastPing).TotalSeconds >= 15)
                {
                    try
                    {
                        await AgentHttpServer.WebSocketSendPingAsync(stream, cts.Token);
                        lastPing = DateTime.UtcNow;
                    }
                    catch { await cts.CancelAsync(); break; }
                }

                try { await Task.Delay(50, cts.Token); }
                catch { break; }
            }

            await readTask;
        }
        finally
        {
            NetworkStore.OnRequestCaptured -= OnRequest;
        }
    }

    private async Task HandleLogsWebSocket(
        System.Net.Sockets.TcpClient client,
        System.Net.Sockets.NetworkStream stream,
        HttpRequest request,
        CancellationToken ct)
    {
        if (_logProvider == null) return;

        // Parse optional source filter from query string
        request.QueryParams.TryGetValue("source", out var sourceFilter);

        // Parse optional replay count (default 100, 0 to skip replay)
        var replayCount = 100;
        if (request.QueryParams.TryGetValue("replay", out var replayStr) && int.TryParse(replayStr, out var rc))
            replayCount = Math.Max(0, rc);

        // Send replay of recent log entries
        if (replayCount > 0)
        {
            var recent = _logProvider.Reader.Read(replayCount, 0, sourceFilter);
            var replayMsg = JsonSerializer.Serialize(new { type = "replay", entries = recent });
            await AgentHttpServer.WebSocketSendTextAsync(stream, replayMsg, ct);
        }

        // Subscribe to live log entries
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sendQueue = new System.Collections.Concurrent.ConcurrentQueue<Logging.FileLogEntry>();

        void OnLog(Logging.FileLogEntry entry)
        {
            if (sourceFilter != null && !string.Equals(entry.Source, sourceFilter, StringComparison.OrdinalIgnoreCase))
                return;
            sendQueue.Enqueue(entry);
        }
        _logProvider.Writer.OnLogWritten += OnLog;

        try
        {
            // Read loop (detects disconnection)
            var readTask = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var msg = await AgentHttpServer.WebSocketReadTextAsync(stream, cts.Token);
                    if (msg == null) { await cts.CancelAsync(); break; }
                }
            }, cts.Token);

            // Send loop — drain queue and send pings periodically
            var lastPing = DateTime.UtcNow;
            while (!cts.Token.IsCancellationRequested)
            {
                while (sendQueue.TryDequeue(out var entry))
                {
                    try
                    {
                        var json = JsonSerializer.Serialize(new { type = "log", entry });
                        await AgentHttpServer.WebSocketSendTextAsync(stream, json, cts.Token);
                    }
                    catch { await cts.CancelAsync(); break; }
                }

                if ((DateTime.UtcNow - lastPing).TotalSeconds >= 15)
                {
                    try
                    {
                        await AgentHttpServer.WebSocketSendPingAsync(stream, cts.Token);
                        lastPing = DateTime.UtcNow;
                    }
                    catch { await cts.CancelAsync(); break; }
                }

                try { await Task.Delay(50, cts.Token); }
                catch { break; }
            }

            await readTask;
        }
        finally
        {
            _logProvider.Writer.OnLogWritten -= OnLog;
        }
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
        if (_cdpWebViews.Count == 0)
            return HttpResponse.Error("CDP not available (no Blazor WebViews registered)");

        request.QueryParams.TryGetValue("webview", out var webviewId);
        var webView = ResolveCdpWebView(webviewId);

        if (webView == null)
            return HttpResponse.Error($"WebView '{webviewId}' not found. Use GET /api/cdp/webviews to list available WebViews.");

        if (!webView.IsReady)
            return HttpResponse.Error($"CDP not ready on WebView {webView.Index} (WebView not initialized)");

        if (string.IsNullOrEmpty(request.Body))
            return HttpResponse.Error("Missing CDP command body");

        try
        {
            var result = await webView.CommandHandler(request.Body);
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

    private Task<HttpResponse> HandleCdpWebViews(HttpRequest request)
    {
        var webviews = _cdpWebViews.Select(w => new
        {
            index = w.Index,
            automationId = w.AutomationId,
            elementId = w.ElementId,
            url = w.Url,
            isReady = w.IsReady,
        }).ToList();

        return Task.FromResult(HttpResponse.Json(new { webviews }));
    }

    private async Task<HttpResponse> HandleCdpSource(HttpRequest request)
    {
        if (_cdpWebViews.Count == 0)
            return HttpResponse.Error("CDP not available (no Blazor WebViews registered)");

        request.QueryParams.TryGetValue("webview", out var webviewId);
        var webView = ResolveCdpWebView(webviewId);

        if (webView == null)
            return HttpResponse.Error($"WebView '{webviewId}' not found. Use GET /api/cdp/webviews to list available WebViews.");

        if (!webView.IsReady)
            return HttpResponse.Error($"CDP not ready on WebView {webView.Index} (WebView not initialized)");

        try
        {
            var cdpCommand = System.Text.Json.JsonSerializer.Serialize(new
            {
                id = 99999,
                method = "Runtime.evaluate",
                @params = new { expression = "document.documentElement.outerHTML", returnByValue = true }
            });

            var resultJson = await webView.CommandHandler(cdpCommand);
            using var doc = System.Text.Json.JsonDocument.Parse(resultJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("result", out var result) &&
                result.TryGetProperty("result", out var innerResult) &&
                innerResult.TryGetProperty("value", out var value))
            {
                return new HttpResponse
                {
                    ContentType = "text/html",
                    Body = value.GetString() ?? ""
                };
            }

            return HttpResponse.Error("Failed to extract page source from CDP response");
        }
        catch (Exception ex)
        {
            return HttpResponse.Error($"Failed to get page source: {ex.Message}");
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

public class ScrollRequest
{
    public string? ElementId { get; set; }
    public double DeltaX { get; set; }
    public double DeltaY { get; set; }
    public bool Animated { get; set; } = true;
    public int? ItemIndex { get; set; }
    public int? GroupIndex { get; set; }
    public string? ScrollToPosition { get; set; }
}
