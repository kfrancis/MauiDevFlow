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
    /// Maximum size of each log file in bytes before rotation. Default: 1MB.
    /// </summary>
    public long MaxLogFileSize { get; set; } = 1_048_576;

    /// <summary>
    /// Maximum number of rotated log files to keep. Default: 5.
    /// </summary>
    public int MaxLogFiles { get; set; } = 5;
}
