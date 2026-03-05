namespace MauiDevFlow.Agent.Core;

/// <summary>
/// Configuration options for the MauiDevFlow Agent.
/// </summary>
public class AgentOptions
{
    /// <summary>Default port when none is specified via code or MSBuild property.</summary>
    public const int DefaultPort = 9223;

    /// <summary>
    /// Port for the HTTP API server. Default: 9223.
    /// Override at build time with -p:MauiDevFlowPort=XXXX.
    /// </summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// Whether the agent is enabled. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum tree walk depth. 0 = unlimited. Default: 0.
    /// </summary>
    public int MaxTreeDepth { get; set; } = 0;

    /// <summary>
    /// Whether to capture ILogger output to rotating log files. Default: true.
    /// </summary>
    public bool EnableFileLogging { get; set; } = true;

    /// <summary>
    /// Whether to register the FileLogProvider as an ILoggerProvider so that
    /// ILogger output is written to the rotating log files. Default: true.
    /// Requires <see cref="EnableFileLogging"/> to be true.
    /// </summary>
    public bool CaptureILogger { get; set; } = true;

    /// <summary>
    /// Maximum size of each log file in bytes before rotation. Default: 1MB.
    /// </summary>
    public long MaxLogFileSize { get; set; } = 1_048_576;

    /// <summary>
    /// Maximum number of rotated log files to keep. Default: 5.
    /// </summary>
    public int MaxLogFiles { get; set; } = 5;

    /// <summary>
    /// Whether to capture Console.Out and Console.Error output into the file log pipeline.
    /// Output is tee'd — original streams still receive everything. Default: true.
    /// Requires <see cref="EnableFileLogging"/> to be true.
    /// </summary>
    public bool CaptureConsole { get; set; } = true;

    /// <summary>
    /// Whether to capture Trace/Debug output into the file log pipeline. Default: true.
    /// Requires <see cref="EnableFileLogging"/> to be true.
    /// </summary>
    public bool CaptureTrace { get; set; } = true;

    /// <summary>
    /// Whether to intercept HttpClient requests for network monitoring. Default: true.
    /// When enabled, all IHttpClientFactory-created HttpClients are automatically monitored.
    /// </summary>
    public bool EnableNetworkMonitoring { get; set; } = true;

    /// <summary>
    /// Maximum size of request/response bodies to capture, in bytes. Default: 256KB.
    /// Bodies larger than this are truncated. Set to 0 to disable body capture.
    /// </summary>
    public int MaxNetworkBodySize { get; set; } = 256 * 1024;

    /// <summary>
    /// Maximum number of network requests to keep in the ring buffer. Default: 500.
    /// </summary>
    public int MaxNetworkBufferSize { get; set; } = 500;
}
