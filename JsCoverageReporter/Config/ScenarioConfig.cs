using System.Text.Json;
using System.Text.Json.Serialization;

namespace JsCoverageReporter.Config;

internal class ScenarioConfig
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("scriptFilter")]
    public string? ScriptFilter { get; set; }

    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; set; }

    [JsonPropertyName("actions")]
    public List<ScenarioAction> Actions { get; set; } = [];
}

internal class ScenarioAction
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("selector")]
    public string? Selector { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("milliseconds")]
    public int? Milliseconds { get; set; }
}
