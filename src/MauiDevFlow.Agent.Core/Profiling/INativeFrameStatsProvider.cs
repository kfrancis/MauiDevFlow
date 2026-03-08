namespace MauiDevFlow.Agent.Core.Profiling;

public sealed class NativeFrameStatsSnapshot
{
    public DateTime TsUtc { get; set; }
    public string Source { get; set; } = "native.unknown";
    public double? Fps { get; set; }
    public double? FrameTimeMsP50 { get; set; }
    public double? FrameTimeMsP95 { get; set; }
    public double? WorstFrameTimeMs { get; set; }
    public int JankFrameCount { get; set; }
    public int UiThreadStallCount { get; set; }
}

public interface INativeFrameStatsProvider : IDisposable
{
    bool IsSupported { get; }
    string Source { get; }
    void Start();
    void Stop();
    bool TryCollect(out NativeFrameStatsSnapshot snapshot);
}
