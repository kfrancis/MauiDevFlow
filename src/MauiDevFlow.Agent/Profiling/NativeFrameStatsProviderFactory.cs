using Microsoft.Maui.ApplicationModel;
using MauiDevFlow.Agent.Core.Profiling;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
#if ANDROID
using Android.OS;
using Android.Views;
using Microsoft.Maui.Devices;
#endif
#if IOS || MACCATALYST
using CoreAnimation;
using Foundation;
using Microsoft.Maui.Devices;
#endif
#if WINDOWS
using Microsoft.Maui.Devices;
using Microsoft.UI.Xaml.Media;
#endif

namespace MauiDevFlow.Agent.Profiling;

internal static class NativeFrameStatsProviderFactory
{
    public static INativeFrameStatsProvider? Create()
    {
#if ANDROID
        return AndroidFrameMetricsStatsProvider.IsApiSupported
            ? new AndroidFrameMetricsStatsProvider()
            : new AndroidChoreographerFrameStatsProvider();
#elif IOS || MACCATALYST
        return new AppleDisplayLinkFrameStatsProvider();
#elif WINDOWS
        return new WindowsCompositionFrameStatsProvider();
#else
        return null;
#endif
    }
}

internal sealed class FrameStatsAccumulator
{
    private readonly object _gate = new();
    private readonly Queue<double> _durationsMs = new();
    private readonly double _jankThresholdMs;
    private readonly double _stallThresholdMs;
    private readonly int _maxBufferedFrames;

    public FrameStatsAccumulator(double frameBudgetMs, int maxBufferedFrames = 720)
    {
        _jankThresholdMs = Math.Max(16d, frameBudgetMs * 1.5d);
        _stallThresholdMs = 150d;
        _maxBufferedFrames = Math.Max(120, maxBufferedFrames);
    }

    public void Record(double durationMs)
    {
        if (durationMs <= 0d || double.IsNaN(durationMs) || double.IsInfinity(durationMs))
            return;

        lock (_gate)
        {
            _durationsMs.Enqueue(durationMs);
            if (_durationsMs.Count > _maxBufferedFrames)
                _durationsMs.Dequeue();
        }
    }

    public bool TryCreateSnapshot(string source, out NativeFrameStatsSnapshot snapshot)
    {
        List<double> data;
        lock (_gate)
        {
            if (_durationsMs.Count == 0)
            {
                snapshot = new NativeFrameStatsSnapshot();
                return false;
            }

            data = _durationsMs.ToList();
            _durationsMs.Clear();
        }

        data.Sort();
        var avg = data.Average();
        var p50 = Percentile(data, 0.50);
        var p95 = Percentile(data, 0.95);
        var worst = data[^1];

        snapshot = new NativeFrameStatsSnapshot
        {
            TsUtc = DateTime.UtcNow,
            Source = source,
            Fps = avg > 0d ? 1000d / avg : null,
            FrameTimeMsP50 = p50,
            FrameTimeMsP95 = p95,
            WorstFrameTimeMs = worst,
            JankFrameCount = data.Count(frame => frame >= _jankThresholdMs),
            UiThreadStallCount = data.Count(frame => frame >= _stallThresholdMs)
        };
        return true;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
            return 0d;

        var clamped = Math.Clamp(percentile, 0d, 1d);
        var index = (int)Math.Ceiling(sorted.Count * clamped) - 1;
        index = Math.Clamp(index, 0, sorted.Count - 1);
        return sorted[index];
    }
}

#if ANDROID
internal sealed class AndroidFrameMetricsStatsProvider : Java.Lang.Object, INativeFrameStatsProvider, Android.Views.Window.IOnFrameMetricsAvailableListener
{
    private readonly FrameStatsAccumulator _accumulator;
    private WeakReference<Android.Views.Window>? _windowRef;
    private Handler? _frameMetricsHandler;
    private bool _running;

    public AndroidFrameMetricsStatsProvider()
    {
        var frameBudgetMs = ResolveFrameBudgetMs();
        _accumulator = new FrameStatsAccumulator(frameBudgetMs);
    }

    public static bool IsApiSupported => Build.VERSION.SdkInt >= BuildVersionCodes.N;
    public bool IsSupported => IsApiSupported;
    public bool ProvidesExactFrameTimings => true;
    public string Source => "native.android.framemetrics";

    public void Start()
    {
        if (_running)
            return;
        if (!IsSupported)
            throw new PlatformNotSupportedException("FrameMetrics requires Android API 24+.");

        _running = true;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var activity = Platform.CurrentActivity;
            var window = activity?.Window;
            if (window == null)
                throw new InvalidOperationException("Unable to access current Android window for frame metrics.");

            var looper = Looper.MyLooper() ?? Looper.MainLooper;
            if (looper == null)
                throw new InvalidOperationException("Unable to obtain Android looper for frame metrics listener.");

            _frameMetricsHandler ??= new Handler(looper);
            _windowRef = new WeakReference<Android.Views.Window>(window);
            window.AddOnFrameMetricsAvailableListener(this, _frameMetricsHandler);
        });
    }

    public void Stop()
    {
        _running = false;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_windowRef?.TryGetTarget(out var window) == true)
                window.RemoveOnFrameMetricsAvailableListener(this);
            _windowRef = null;
            _frameMetricsHandler = null;
        });
    }

    public bool TryCollect(out NativeFrameStatsSnapshot snapshot)
    {
        if (!_accumulator.TryCreateSnapshot(Source, out snapshot))
            return false;

        snapshot.NativeMemoryBytes = TryReadAndroidNativeMemoryBytes();
        return true;
    }

    public void OnFrameMetricsAvailable(Android.Views.Window? window, FrameMetrics? frameMetrics, int dropCountSinceLastInvocation)
    {
        if (!_running || frameMetrics == null)
            return;

        var durationNs = frameMetrics.GetMetric((int)FrameMetricsId.TotalDuration);
        if (durationNs <= 0)
            return;

        _accumulator.Record(durationNs / 1_000_000d);
    }

    public new void Dispose()
    {
        Stop();
        base.Dispose();
    }

    private static long? TryReadAndroidNativeMemoryBytes()
    {
        try
        {
            return Android.OS.Debug.NativeHeapAllocatedSize;
        }
        catch
        {
            return null;
        }
    }

    private static double ResolveFrameBudgetMs()
    {
        try
        {
            var refreshRate = DeviceDisplay.Current.MainDisplayInfo.RefreshRate;
            if (refreshRate > 1d && !double.IsInfinity(refreshRate) && !double.IsNaN(refreshRate))
                return 1000d / refreshRate;
        }
        catch
        {
        }

        return 1000d / 60d;
    }
}

internal sealed class AndroidChoreographerFrameStatsProvider : Java.Lang.Object, INativeFrameStatsProvider, Choreographer.IFrameCallback
{
    private readonly FrameStatsAccumulator _accumulator;
    private bool _running;
    private long _lastFrameTimeNanos;

    public AndroidChoreographerFrameStatsProvider()
    {
        var frameBudgetMs = ResolveFrameBudgetMs();
        _accumulator = new FrameStatsAccumulator(frameBudgetMs);
    }

    public bool IsSupported => true;
    public bool ProvidesExactFrameTimings => false;
    public string Source => "native.android.choreographer";

    public void Start()
    {
        if (_running)
            return;

        _running = true;
        _lastFrameTimeNanos = 0;
        MainThread.BeginInvokeOnMainThread(() => Choreographer.Instance.PostFrameCallback(this));
    }

    public void Stop()
    {
        _running = false;
        MainThread.BeginInvokeOnMainThread(() => Choreographer.Instance.RemoveFrameCallback(this));
    }

    public bool TryCollect(out NativeFrameStatsSnapshot snapshot)
    {
        if (!_accumulator.TryCreateSnapshot(Source, out snapshot))
            return false;

        snapshot.NativeMemoryBytes = TryReadAndroidNativeMemoryBytes();
        return true;
    }

    public void DoFrame(long frameTimeNanos)
    {
        if (!_running)
            return;

        if (_lastFrameTimeNanos > 0)
        {
            var durationMs = (frameTimeNanos - _lastFrameTimeNanos) / 1_000_000d;
            _accumulator.Record(durationMs);
        }

        _lastFrameTimeNanos = frameTimeNanos;
        Choreographer.Instance.PostFrameCallback(this);
    }

    public new void Dispose()
    {
        Stop();
        base.Dispose();
    }

    private static double ResolveFrameBudgetMs()
    {
        try
        {
            var refreshRate = DeviceDisplay.Current.MainDisplayInfo.RefreshRate;
            if (refreshRate > 1d && !double.IsInfinity(refreshRate) && !double.IsNaN(refreshRate))
                return 1000d / refreshRate;
        }
        catch
        {
        }

        return 1000d / 60d;
    }

    private static long? TryReadAndroidNativeMemoryBytes()
    {
        try
        {
            return Android.OS.Debug.NativeHeapAllocatedSize;
        }
        catch
        {
            return null;
        }
    }
}
#endif

#if IOS || MACCATALYST
internal sealed class AppleDisplayLinkFrameStatsProvider : INativeFrameStatsProvider
{
    private readonly FrameStatsAccumulator _accumulator;
    private CADisplayLink? _displayLink;
    private bool _running;
    private double _lastTimestampSeconds;

    public AppleDisplayLinkFrameStatsProvider()
    {
        var frameBudgetMs = ResolveFrameBudgetMs();
        _accumulator = new FrameStatsAccumulator(frameBudgetMs);
    }

    public bool IsSupported => true;
    public bool ProvidesExactFrameTimings => false;
    public string Source => "native.apple.cadisplaylink";

    public void Start()
    {
        if (_running)
            return;

        _running = true;
        _lastTimestampSeconds = 0d;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _displayLink = CADisplayLink.Create(OnTick);
            _displayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
        });
    }

    public void Stop()
    {
        _running = false;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _displayLink?.Invalidate();
            _displayLink?.Dispose();
            _displayLink = null;
        });
    }

    public bool TryCollect(out NativeFrameStatsSnapshot snapshot)
    {
        if (!_accumulator.TryCreateSnapshot(Source, out snapshot))
            return false;

        snapshot.NativeMemoryBytes = TryReadResidentMemoryBytes();
        return true;
    }

    public void Dispose()
    {
        Stop();
    }

    private void OnTick()
    {
        if (!_running || _displayLink == null)
            return;

        var ts = _displayLink.Timestamp;
        if (_lastTimestampSeconds > 0d)
        {
            var durationMs = (ts - _lastTimestampSeconds) * 1000d;
            _accumulator.Record(durationMs);
        }

        _lastTimestampSeconds = ts;
    }

    private static double ResolveFrameBudgetMs()
    {
        try
        {
            var refreshRate = DeviceDisplay.Current.MainDisplayInfo.RefreshRate;
            if (refreshRate > 1d && !double.IsInfinity(refreshRate) && !double.IsNaN(refreshRate))
                return 1000d / refreshRate;
        }
        catch
        {
        }

        return 1000d / 60d;
    }

    private static long? TryReadResidentMemoryBytes()
    {
        try
        {
            return Process.GetCurrentProcess().WorkingSet64;
        }
        catch
        {
            return null;
        }
    }
}
#endif

#if WINDOWS
internal sealed class WindowsCompositionFrameStatsProvider : INativeFrameStatsProvider
{
    private readonly FrameStatsAccumulator _accumulator;
    private bool _running;
    private TimeSpan? _lastRenderingTime;

    public WindowsCompositionFrameStatsProvider()
    {
        var frameBudgetMs = ResolveFrameBudgetMs();
        _accumulator = new FrameStatsAccumulator(frameBudgetMs);
    }

    public bool IsSupported => true;
    public bool ProvidesExactFrameTimings => false;
    public string Source => "native.windows.compositiontarget";

    public void Start()
    {
        if (_running)
            return;

        _running = true;
        _lastRenderingTime = null;
        MainThread.BeginInvokeOnMainThread(() => CompositionTarget.Rendering += OnRendering);
    }

    public void Stop()
    {
        _running = false;
        MainThread.BeginInvokeOnMainThread(() => CompositionTarget.Rendering -= OnRendering);
    }

    public bool TryCollect(out NativeFrameStatsSnapshot snapshot)
    {
        if (!_accumulator.TryCreateSnapshot(Source, out snapshot))
            return false;

        snapshot.NativeMemoryBytes = TryReadResidentMemoryBytes();
        return true;
    }

    public void Dispose() => Stop();

    private void OnRendering(object? sender, object args)
    {
        if (!_running || args is not RenderingEventArgs renderingArgs)
            return;

        if (_lastRenderingTime.HasValue)
        {
            var durationMs = (renderingArgs.RenderingTime - _lastRenderingTime.Value).TotalMilliseconds;
            _accumulator.Record(durationMs);
        }

        _lastRenderingTime = renderingArgs.RenderingTime;
    }

    private static long? TryReadResidentMemoryBytes()
    {
        try
        {
            return Process.GetCurrentProcess().WorkingSet64;
        }
        catch
        {
            return null;
        }
    }

    private static double ResolveFrameBudgetMs()
    {
        try
        {
            var refreshRate = DeviceDisplay.Current.MainDisplayInfo.RefreshRate;
            if (refreshRate > 1d && !double.IsInfinity(refreshRate) && !double.IsNaN(refreshRate))
                return 1000d / refreshRate;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
            || ex is NotSupportedException
            || ex is PlatformNotSupportedException)
        {
        }

        return 1000d / 60d;
    }
}
#endif
