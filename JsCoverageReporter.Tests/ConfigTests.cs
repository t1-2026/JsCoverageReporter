using JsCoverageReporter.Config;
using System.Text.Json;

namespace JsCoverageReporter.Tests;

public class ConfigTests
{
    [Fact]
    public void Deserialize_MinimalConfig()
    {
        var json = """{"url": "https://example.com"}""";
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;
        Assert.Equal("https://example.com", config.Url);
        Assert.Empty(config.Actions);
        Assert.Null(config.ScriptFilter);
    }

    [Fact]
    public void Deserialize_AllActionTypes()
    {
        var json = """
        {
            "url": "https://example.com",
            "scriptFilter": "app.js",
            "actions": [
                { "type": "click",           "selector": "#btn" },
                { "type": "fill",            "selector": "input", "value": "hello" },
                { "type": "navigate",        "url": "https://example.com/p2" },
                { "type": "waitForSelector", "selector": ".ready" },
                { "type": "hover",           "selector": ".menu" },
                { "type": "wait",            "milliseconds": 500 }
            ]
        }
        """;
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;
        Assert.Equal("app.js", config.ScriptFilter);
        Assert.Equal(6, config.Actions.Count);
        Assert.Equal("click",   config.Actions[0].Type);
        Assert.Equal("#btn",    config.Actions[0].Selector);
        Assert.Equal("hello",   config.Actions[1].Value);
        Assert.Equal("https://example.com/p2", config.Actions[2].Url);
        Assert.Equal(500,       config.Actions[5].Milliseconds);
    }

    [Fact]
    public void Deserialize_TimeoutMs_WhenSet()
    {
        var json = """{"url": "https://example.com", "timeoutMs": 10000}""";
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;
        Assert.Equal(10000, config.TimeoutMs);
    }

    [Fact]
    public void Deserialize_TimeoutMs_NullWhenNotSet()
    {
        var json = """{"url": "https://example.com"}""";
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;
        Assert.Null(config.TimeoutMs);
    }
}
