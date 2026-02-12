using System.Text.Json.Serialization;

namespace MauiDevFlow.Logging;

/// <summary>
/// A single log entry stored in JSONL format.
/// </summary>
public record FileLogEntry(
    [property: JsonPropertyName("t")] DateTime Timestamp,
    [property: JsonPropertyName("l")] string Level,
    [property: JsonPropertyName("c")] string Category,
    [property: JsonPropertyName("m")] string Message,
    [property: JsonPropertyName("e")] string? Exception = null,
    [property: JsonPropertyName("s")] string? Source = null
);
