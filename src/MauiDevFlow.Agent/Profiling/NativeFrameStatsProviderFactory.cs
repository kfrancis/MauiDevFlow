using Microsoft.Maui.ApplicationModel;
using MauiDevFlow.Agent.Core.Profiling;
using System.Collections.Generic;
using System.Linq;
#if ANDROID
using Android.Views;
using Microsoft.Maui.Devices;
#endif
#if IOS || MACCATALYST
using CoreAnimation;
using Foundation;
using Microsoft.Maui.Devices;
#endif

namespace MauiDevFlow.Agent.Profiling;

internal static class NativeFrameStatsProviderFactory
{
    public static INativeFrameStatsProvider? Create()
    {
#if ANDROID
        return new AndroidChoreographerFrameStatsProvider();
#elif IOS || MACCATALYST
        return new AppleDisplayLinkFrameStatsProvider();
#else
        return null;
#endif
    }
}

internal sealed class FrameStatsAccumulator
{
    private readonly object _gate = new();
    private readonly List<double> _durationsMs = new();
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
            _durationsMs.Add(durationMs);
            if (_durationsMs.Count > _maxBufferedFrames)
                _durationsMs.RemoveRange(0, _durationsMs.Count - _maxBufferedFrames);
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

            data = new List<double>(_durationsMs);
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
        => _accumulator.TryCreateSnapshot(Source, out snapshot);

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
        => _accumulator.TryCreateSnapshot(Source, out snapshot);

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
}
#endif
