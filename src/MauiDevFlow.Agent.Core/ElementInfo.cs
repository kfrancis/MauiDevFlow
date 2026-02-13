using System.Text.Json.Serialization;

namespace MauiDevFlow.Agent.Core;

/// <summary>
/// Represents a MAUI visual tree element with all inspectable properties.
/// </summary>
public class ElementInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("parentId")]
    public string? ParentId { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("fullType")]
    public string FullType { get; set; } = string.Empty;

    [JsonPropertyName("automationId")]
    public string? AutomationId { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; set; }

    [JsonPropertyName("isVisible")]
    public bool IsVisible { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; }

    [JsonPropertyName("isFocused")]
    public bool IsFocused { get; set; }

    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 1.0;

    [JsonPropertyName("bounds")]
    public BoundsInfo? Bounds { get; set; }

    [JsonPropertyName("gestures")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Gestures { get; set; }

    [JsonPropertyName("nativeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeType { get; set; }

    [JsonPropertyName("nativeProperties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string?>? NativeProperties { get; set; }

    [JsonPropertyName("children")]
    public List<ElementInfo>? Children { get; set; }
}

/// <summary>
/// Element bounding rectangle in screen coordinates.
/// </summary>
public class BoundsInfo
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}
