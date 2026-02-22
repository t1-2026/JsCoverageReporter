# JS Coverage Reporter Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Standalone C# console app that uses Playwright.NET to collect JS branch coverage from any web page and generates a self-contained HTML report with green/red color-coded source.

**Architecture:** Playwright.NET opens Chromium headless, enables V8 blockCoverage, executes user-defined actions from a JSON config file, then a pure-C# HTML generator maps character-offset coverage ranges to `<span class="covered/uncovered">` elements—no Node.js required.

**Tech Stack:** .NET 8, Microsoft.Playwright, xUnit 2.x, System.Text.Json (built-in)

---

### Task 1: Project Setup

**Files:**
- Create: `JsCoverageReporter/JsCoverageReporter.csproj`
- Create: `JsCoverageReporter.Tests/JsCoverageReporter.Tests.csproj`
- Create: `JsCoverageReporter.sln`

**Step 1: Create projects and solution**

```bash
cd C:/work/JsCoverageReporter
dotnet new console -o JsCoverageReporter --framework net8.0
dotnet new xunit -o JsCoverageReporter.Tests --framework net8.0
dotnet new sln
dotnet sln add JsCoverageReporter/JsCoverageReporter.csproj
dotnet sln add JsCoverageReporter.Tests/JsCoverageReporter.Tests.csproj
```

**Step 2: Add Microsoft.Playwright**

```bash
dotnet add JsCoverageReporter/JsCoverageReporter.csproj package Microsoft.Playwright
```

**Step 3: Reference main project from test project**

```bash
dotnet add JsCoverageReporter.Tests/JsCoverageReporter.Tests.csproj reference JsCoverageReporter/JsCoverageReporter.csproj
```

**Step 4: Expose internals to test project**

Create `JsCoverageReporter/AssemblyInfo.cs`:
```csharp
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("JsCoverageReporter.Tests")]
```

**Step 5: Verify build**

```bash
dotnet build
```
Expected: Build succeeded.

**Step 6: Install Playwright browser (one-time)**

```bash
pwsh JsCoverageReporter/bin/Debug/net8.0/playwright.ps1 install chromium
```
Expected: Chromium downloaded to Playwright cache.

**Step 7: Commit**

```bash
git init
git add .
git commit -m "chore: initial project setup with Playwright.NET and xUnit"
```

---

### Task 2: Config Models

**Files:**
- Create: `JsCoverageReporter/Config/ScenarioConfig.cs`
- Create: `JsCoverageReporter.Tests/ConfigTests.cs`

**Step 1: Write failing tests**

Create `JsCoverageReporter.Tests/ConfigTests.cs`:
```csharp
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
}
```

**Step 2: Run to verify failure**

```bash
dotnet test JsCoverageReporter.Tests --filter "ConfigTests"
```
Expected: Build error — `ScenarioConfig` not found.

**Step 3: Implement config models**

Create `JsCoverageReporter/Config/ScenarioConfig.cs`:
```csharp
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
```

**Step 4: Run tests**

```bash
dotnet test JsCoverageReporter.Tests --filter "ConfigTests"
```
Expected: 2 tests pass.

**Step 5: Commit**

```bash
git add JsCoverageReporter/Config/ JsCoverageReporter.Tests/ConfigTests.cs
git commit -m "feat: add ScenarioConfig JSON deserialization"
```

---

### Task 3: CoverageData Models

**Files:**
- Create: `JsCoverageReporter/Coverage/CoverageData.cs`

No tests needed — simple data records.

**Step 1: Create models**

Create `JsCoverageReporter/Coverage/CoverageData.cs`:
```csharp
namespace JsCoverageReporter.Coverage;

internal record ScriptCoverage(
    string Url,
    string Source,
    IReadOnlyList<FunctionCoverage> Functions
);

internal record FunctionCoverage(
    string FunctionName,
    IReadOnlyList<CoverageRange> Ranges
);

internal record CoverageRange(
    int StartOffset,
    int EndOffset,
    int Count
);
```

**Step 2: Build**

```bash
dotnet build
```
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add JsCoverageReporter/Coverage/CoverageData.cs
git commit -m "feat: add V8 coverage data models"
```

---

### Task 4: HtmlReportGenerator — Coverage Map

Core algorithm: converts V8 range data to a character-level integer array.
Each cell is: `-1` = out of scope, `0` = not executed, `1` = executed.
Ranges are applied largest-first so inner (smaller) branch ranges override the outer function range.

**Files:**
- Create: `JsCoverageReporter/Report/HtmlReportGenerator.cs`
- Create: `JsCoverageReporter.Tests/Report/CoverageMapTests.cs`

**Step 1: Write failing tests**

Create `JsCoverageReporter.Tests/Report/CoverageMapTests.cs`:
```csharp
using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

public class CoverageMapTests
{
    [Fact]
    public void BuildMap_OutOfScope_IsMinusOne()
    {
        var map = HtmlReportGenerator.BuildCoverageMap("abc", []);
        Assert.Equal([-1, -1, -1], map);
    }

    [Fact]
    public void BuildMap_AllCovered()
    {
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(0, 5, 1)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("hello", functions);
        Assert.Equal([1, 1, 1, 1, 1], map);
    }

    [Fact]
    public void BuildMap_AllUncovered()
    {
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(0, 5, 0)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("hello", functions);
        Assert.Equal([0, 0, 0, 0, 0], map);
    }

    [Fact]
    public void BuildMap_InnerRangeOverridesOuter()
    {
        // Outer: whole source covered (count=3)
        // Inner: chars 13-16 are the else-branch, NOT executed (count=0)
        var source = "if(x){A}else{B}";
        //            0123456789012345
        var functions = new[]
        {
            new FunctionCoverage("f", [
                new CoverageRange(0,  16, 3),   // outer function — covered
                new CoverageRange(13, 16, 0),   // else branch — uncovered
            ])
        };
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);
        Assert.All(map[..13], v => Assert.Equal(1, v));   // if-branch: covered
        Assert.All(map[13..], v => Assert.Equal(0, v));   // else-branch: uncovered
    }
}
```

**Step 2: Run to verify failure**

```bash
dotnet test JsCoverageReporter.Tests --filter "CoverageMapTests"
```
Expected: Build error — `HtmlReportGenerator` not found.

**Step 3: Implement**

Create `JsCoverageReporter/Report/HtmlReportGenerator.cs`:
```csharp
using System.Text;
using JsCoverageReporter.Coverage;

namespace JsCoverageReporter.Report;

internal enum LineCoverageStatus { Neutral, Covered, Uncovered, Partial }

internal record LineData(string Html, LineCoverageStatus Status);

internal class HtmlReportGenerator
{
    /// <summary>
    /// Builds a per-character coverage map from V8 range data.
    /// Values: -1 = out of scope, 0 = not executed, 1 = executed.
    /// Processes ranges largest-first so inner branch ranges override the outer function range.
    /// </summary>
    internal static int[] BuildCoverageMap(string source, IEnumerable<FunctionCoverage> functions)
    {
        var map = new int[source.Length];
        Array.Fill(map, -1);

        var allRanges = functions
            .SelectMany(f => f.Ranges)
            .OrderByDescending(r => r.EndOffset - r.StartOffset);

        foreach (var range in allRanges)
        {
            int val = range.Count > 0 ? 1 : 0;
            int end = Math.Min(range.EndOffset, source.Length);
            for (int i = range.StartOffset; i < end; i++)
                map[i] = val;
        }

        return map;
    }
}
```

**Step 4: Run tests**

```bash
dotnet test JsCoverageReporter.Tests --filter "CoverageMapTests"
```
Expected: 4 tests pass.

**Step 5: Commit**

```bash
git add JsCoverageReporter/Report/ JsCoverageReporter.Tests/Report/
git commit -m "feat: implement BuildCoverageMap — inner branch ranges override outer"
```

---

### Task 5: HtmlReportGenerator — HTML Generation

Converts the coverage map to HTML lines with colored `<span>` elements, then assembles the per-script page and the index summary page.

**Files:**
- Modify: `JsCoverageReporter/Report/HtmlReportGenerator.cs`
- Create: `JsCoverageReporter.Tests/Report/HtmlOutputTests.cs`

**Step 1: Write failing tests**

Create `JsCoverageReporter.Tests/Report/HtmlOutputTests.cs`:
```csharp
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

public class HtmlOutputTests
{
    [Fact]
    public void HtmlEncode_EscapesSpecialChars()
    {
        Assert.Equal("&lt;div&gt;", HtmlReportGenerator.HtmlEncode("<div>"));
        Assert.Equal("a&amp;b",    HtmlReportGenerator.HtmlEncode("a&b"));
        Assert.Equal("&quot;",     HtmlReportGenerator.HtmlEncode("\""));
    }

    [Fact]
    public void BuildLines_SingleCoveredLine()
    {
        var lines = HtmlReportGenerator.BuildLines("hello", [1, 1, 1, 1, 1]);
        Assert.Single(lines);
        Assert.Contains("class=\"covered\"", lines[0].Html);
        Assert.Contains("hello", lines[0].Html);
        Assert.Equal(LineCoverageStatus.Covered, lines[0].Status);
    }

    [Fact]
    public void BuildLines_SingleUncoveredLine()
    {
        var lines = HtmlReportGenerator.BuildLines("hello", [0, 0, 0, 0, 0]);
        Assert.Single(lines);
        Assert.Contains("class=\"uncovered\"", lines[0].Html);
        Assert.Equal(LineCoverageStatus.Uncovered, lines[0].Status);
    }

    [Fact]
    public void BuildLines_PartialLine_ContainsBothSpans()
    {
        // "AB": A is covered, B is uncovered → Partial
        var lines = HtmlReportGenerator.BuildLines("AB", [1, 0]);
        Assert.Single(lines);
        Assert.Equal(LineCoverageStatus.Partial, lines[0].Status);
        Assert.Contains("class=\"covered\"",   lines[0].Html);
        Assert.Contains("class=\"uncovered\"", lines[0].Html);
    }

    [Fact]
    public void BuildLines_NeutralLine_AllOutOfScope()
    {
        var lines = HtmlReportGenerator.BuildLines("//comment", [-1, -1, -1, -1, -1, -1, -1, -1, -1]);
        Assert.Single(lines);
        Assert.Equal(LineCoverageStatus.Neutral, lines[0].Status);
    }

    [Fact]
    public void BuildLines_MultiLine_SplitsCorrectly()
    {
        // source = "A\nB", map: A=covered, \n=out-of-scope, B=uncovered
        var lines = HtmlReportGenerator.BuildLines("A\nB", [1, -1, 0]);
        Assert.Equal(2, lines.Count);
        Assert.Equal(LineCoverageStatus.Covered,   lines[0].Status);
        Assert.Equal(LineCoverageStatus.Uncovered, lines[1].Status);
    }
}
```

**Step 2: Run to verify failure**

```bash
dotnet test JsCoverageReporter.Tests --filter "HtmlOutputTests"
```
Expected: Build errors — `BuildLines`, `HtmlEncode` not found.

**Step 3: Add BuildLines and HtmlEncode to HtmlReportGenerator**

Add these methods inside the `HtmlReportGenerator` class in `JsCoverageReporter/Report/HtmlReportGenerator.cs`:

```csharp
internal static string HtmlEncode(string text) =>
    text.Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

internal static List<LineData> BuildLines(string source, int[] map)
{
    var result = new List<LineData>();
    var rawLines = source.Split('\n');
    int offset = 0;

    foreach (var rawLine in rawLines)
    {
        var sb = new StringBuilder();
        int coveredCount = 0, uncoveredCount = 0;
        int currentState = -2; // sentinel: "no span open yet"

        for (int i = 0; i < rawLine.Length; i++)
        {
            int idx = offset + i;
            int coverage = idx < map.Length ? map[idx] : -1;

            if (coverage != currentState)
            {
                if (currentState != -2) sb.Append("</span>");
                currentState = coverage;
                string cls = coverage switch { 1 => "covered", 0 => "uncovered", _ => "neutral" };
                sb.Append($"<span class=\"{cls}\">");
            }

            sb.Append(HtmlEncode(rawLine[i].ToString()));

            if (coverage == 1) coveredCount++;
            else if (coverage == 0) uncoveredCount++;
        }

        if (currentState != -2) sb.Append("</span>");

        var status = (coveredCount, uncoveredCount) switch
        {
            (0, 0)   => LineCoverageStatus.Neutral,
            ( > 0, 0) => LineCoverageStatus.Covered,
            (0, > 0)  => LineCoverageStatus.Uncovered,
            _         => LineCoverageStatus.Partial,
        };

        result.Add(new LineData(sb.ToString(), status));
        offset += rawLine.Length + 1; // +1 for the '\n' we split on
    }

    return result;
}
```

**Step 4: Run tests**

```bash
dotnet test JsCoverageReporter.Tests --filter "HtmlOutputTests"
```
Expected: 6 tests pass.

**Step 5: Add Generate, BuildScriptPage, BuildIndexPage**

Add these methods to the `HtmlReportGenerator` class:

```csharp
public void Generate(IReadOnlyList<ScriptCoverage> coverages, string outputDir)
{
    Directory.CreateDirectory(outputDir);
    var scriptsDir = Path.Combine(outputDir, "scripts");
    Directory.CreateDirectory(scriptsDir);

    var summaryRows = new List<(string url, int covered, int total, string filename)>();

    for (int i = 0; i < coverages.Count; i++)
    {
        var script = coverages[i];
        var filename = $"script-{i}.html";

        var map   = BuildCoverageMap(script.Source, script.Functions);
        var lines = BuildLines(script.Source, map);

        int covered = lines.Count(l => l.Status is LineCoverageStatus.Covered or LineCoverageStatus.Partial);
        int total   = lines.Count(l => l.Status != LineCoverageStatus.Neutral);

        File.WriteAllText(
            Path.Combine(scriptsDir, filename),
            BuildScriptPage(script.Url, lines),
            Encoding.UTF8);

        summaryRows.Add((script.Url, covered, total, filename));
    }

    File.WriteAllText(
        Path.Combine(outputDir, "index.html"),
        BuildIndexPage(summaryRows),
        Encoding.UTF8);
}

private static string BuildScriptPage(string url, List<LineData> lines)
{
    var sb = new StringBuilder();
    sb.AppendLine("""
        <!DOCTYPE html><html><head><meta charset="utf-8">
        <title>JS Coverage</title>
        <style>
        body{font-family:monospace;font-size:13px;margin:0;background:#fff}
        h1{padding:8px 12px;background:#2d2d2d;color:#fff;margin:0;font-size:13px;word-break:break-all}
        .source{white-space:pre}
        .line{display:flex;line-height:1.6}
        .gutter{min-width:48px;padding:0 8px;text-align:right;user-select:none;
                background:#f5f5f5;color:#aaa;border-right:2px solid #e0e0e0}
        .code{padding:0 8px;flex:1;overflow-x:auto}
        .line-covered   .gutter{background:#c6efc6;color:#3a7d3a;border-color:#8fc98f}
        .line-uncovered .gutter{background:#f0c6c6;color:#7d3a3a;border-color:#c98f8f}
        .line-partial   .gutter{background:#f0e8a0;color:#6b6000;border-color:#c9b800}
        span.covered  {background:#d4f8d4}
        span.uncovered{background:#f8d4d4}
        span.neutral  {}
        </style></head><body>
        """);
    sb.AppendLine($"<h1>{HtmlEncode(url)}</h1><div class=\"source\">");

    for (int i = 0; i < lines.Count; i++)
    {
        var line = lines[i];
        string cls = line.Status switch
        {
            LineCoverageStatus.Covered   => "line line-covered",
            LineCoverageStatus.Uncovered => "line line-uncovered",
            LineCoverageStatus.Partial   => "line line-partial",
            _                            => "line",
        };
        sb.AppendLine($"<div class=\"{cls}\"><span class=\"gutter\">{i + 1}</span><span class=\"code\">{line.Html}</span></div>");
    }

    sb.AppendLine("</div></body></html>");
    return sb.ToString();
}

private static string BuildIndexPage(
    List<(string url, int covered, int total, string filename)> rows)
{
    int totalCovered = rows.Sum(r => r.covered);
    int totalLines   = rows.Sum(r => r.total);
    double overallPct = totalLines > 0 ? 100.0 * totalCovered / totalLines : 0;

    var sb = new StringBuilder();
    sb.AppendLine($"""
        <!DOCTYPE html><html><head><meta charset="utf-8">
        <title>JS Coverage Report</title>
        <style>
        body{{font-family:sans-serif;padding:24px;color:#333}}
        h1{{font-size:20px}}
        table{{border-collapse:collapse;width:100%;margin-top:16px}}
        th,td{{border:1px solid #ddd;padding:8px 12px;text-align:left}}
        th{{background:#f5f5f5;font-weight:600}}
        td.num{{text-align:right;font-variant-numeric:tabular-nums}}
        a{{color:#1a7a4a;text-decoration:none}}
        a:hover{{text-decoration:underline}}
        </style></head><body>
        <h1>JS Coverage Report</h1>
        <p>Overall coverage: <strong>{overallPct:F1}%</strong> ({totalCovered} / {totalLines} lines)</p>
        <table>
        <tr><th>Script</th><th class="num">Covered</th><th class="num">Total</th><th class="num">%</th></tr>
        """);

    foreach (var (url, covered, total, filename) in rows)
    {
        double pct = total > 0 ? 100.0 * covered / total : 0;
        sb.AppendLine($"<tr><td><a href=\"scripts/{filename}\">{HtmlEncode(url)}</a></td>" +
                      $"<td class=\"num\">{covered}</td><td class=\"num\">{total}</td>" +
                      $"<td class=\"num\">{pct:F1}%</td></tr>");
    }

    sb.AppendLine("</table></body></html>");
    return sb.ToString();
}
```

**Step 6: Build**

```bash
dotnet build
```
Expected: Build succeeded.

**Step 7: Commit**

```bash
git add JsCoverageReporter/Report/ JsCoverageReporter.Tests/Report/
git commit -m "feat: implement HTML report with line/branch coverage coloring"
```

---

### Task 6: ActionRunner

**Files:**
- Create: `JsCoverageReporter/Actions/ActionRunner.cs`

Integration-tested in Task 9 (requires browser). No unit tests here.

**Step 1: Implement**

Create `JsCoverageReporter/Actions/ActionRunner.cs`:
```csharp
using JsCoverageReporter.Config;
using Microsoft.Playwright;

namespace JsCoverageReporter.Actions;

internal static class ActionRunner
{
    internal static async Task RunAsync(IPage page, IEnumerable<ScenarioAction> actions)
    {
        foreach (var action in actions)
        {
            switch (action.Type)
            {
                case "click":
                    await page.ClickAsync(action.Selector!);
                    break;
                case "fill":
                    await page.FillAsync(action.Selector!, action.Value ?? "");
                    break;
                case "navigate":
                    await page.GotoAsync(action.Url!);
                    break;
                case "waitForSelector":
                    await page.WaitForSelectorAsync(action.Selector!);
                    break;
                case "hover":
                    await page.HoverAsync(action.Selector!);
                    break;
                case "wait":
                    await Task.Delay(action.Milliseconds ?? 0);
                    break;
                default:
                    Console.Error.WriteLine($"[Warning] Unknown action type '{action.Type}' — skipping.");
                    break;
            }
        }
    }
}
```

**Step 2: Build**

```bash
dotnet build
```
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add JsCoverageReporter/Actions/ActionRunner.cs
git commit -m "feat: implement ActionRunner for config-defined page interactions"
```

---

### Task 7: CoverageCollector

**Files:**
- Create: `JsCoverageReporter/Coverage/CoverageCollector.cs`

**Step 1: Implement**

Create `JsCoverageReporter/Coverage/CoverageCollector.cs`:
```csharp
using Microsoft.Playwright;

namespace JsCoverageReporter.Coverage;

internal class CoverageCollector(IPage page)
{
    public async Task StartAsync() =>
        await page.Coverage.StartJSCoverageAsync(new CoverageStartJSCoverageOptions
        {
            ReportAnonymousScripts = false,
            ResetOnNavigation = false,
        });

    public async Task<IReadOnlyList<ScriptCoverage>> StopAsync(string? scriptFilter)
    {
        var entries = await page.Coverage.StopJSCoverageAsync();

        return entries
            .Where(e => !string.IsNullOrEmpty(e.Source))
            .Where(e => string.IsNullOrEmpty(scriptFilter) || e.Url.Contains(scriptFilter))
            .Select(e => new ScriptCoverage(
                e.Url,
                e.Source!,
                e.Functions
                    .Select(f => new FunctionCoverage(
                        f.FunctionName,
                        f.Ranges
                            .Select(r => new CoverageRange(r.StartOffset, r.EndOffset, r.Count))
                            .ToList()))
                    .ToList()))
            .ToList();
    }
}
```

**Step 2: Build**

```bash
dotnet build
```
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add JsCoverageReporter/Coverage/CoverageCollector.cs
git commit -m "feat: implement CoverageCollector using Playwright V8 JS coverage API"
```

---

### Task 8: Program.cs — CLI Entry Point

**Files:**
- Modify: `JsCoverageReporter/Program.cs`

**Step 1: Replace auto-generated content**

Replace the full contents of `JsCoverageReporter/Program.cs` with:
```csharp
using JsCoverageReporter.Actions;
using JsCoverageReporter.Config;
using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;
using Microsoft.Playwright;
using System.Text.Json;

string? configPath = null;
string outputDir = "./report";

for (int i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--config") configPath = args[i + 1];
    if (args[i] == "--output") outputDir  = args[i + 1];
}

if (configPath is null)
{
    Console.Error.WriteLine("Usage: JsCoverageReporter --config <scenario.json> [--output <dir>]");
    return 1;
}

ScenarioConfig scenario;
try
{
    var json = File.ReadAllText(configPath);
    scenario = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)
               ?? throw new InvalidOperationException("Config file deserialized to null.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error loading config: {ex.Message}");
    return 1;
}

Console.WriteLine($"Opening: {scenario.Url}");

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = true,
});
var page = await browser.NewPageAsync();

var collector = new CoverageCollector(page);
await collector.StartAsync();

await page.GotoAsync(scenario.Url);
await ActionRunner.RunAsync(page, scenario.Actions);

Console.WriteLine("Collecting coverage...");
var coverages = await collector.StopAsync(scenario.ScriptFilter);
Console.WriteLine($"  {coverages.Count} script(s) captured.");

new HtmlReportGenerator().Generate(coverages, outputDir);
Console.WriteLine($"Report: {Path.GetFullPath(Path.Combine(outputDir, "index.html"))}");
return 0;
```

**Step 2: Build**

```bash
dotnet build
```
Expected: Build succeeded.

**Step 3: Run all unit tests**

```bash
dotnet test JsCoverageReporter.Tests
```
Expected: All tests pass.

**Step 4: Commit**

```bash
git add JsCoverageReporter/Program.cs
git commit -m "feat: implement CLI entry point — wire all components"
```

---

### Task 9: End-to-End Test

**Step 1: Create .gitignore**

Create `C:/work/JsCoverageReporter/.gitignore`:
```
bin/
obj/
dist/
*.report/
report/
sample-report/
```

**Step 2: Create sample scenario**

Create `C:/work/JsCoverageReporter/sample-scenario.json`:
```json
{
  "url": "https://example.com",
  "scriptFilter": ""
}
```

**Step 3: Run the tool**

```bash
cd C:/work/JsCoverageReporter
dotnet run --project JsCoverageReporter -- --config sample-scenario.json --output ./sample-report
```
Expected output:
```
Opening: https://example.com
Collecting coverage...
  N script(s) captured.
Report: C:\work\JsCoverageReporter\sample-report\index.html
```

**Step 4: Open the report**

```bash
start ./sample-report/index.html
```
Expected: Browser shows the index page listing captured scripts with coverage %.
Click a script link → source code with green/red highlighted ranges visible.

**Step 5: Commit**

```bash
git add .gitignore sample-scenario.json
git commit -m "chore: add .gitignore and sample scenario for E2E testing"
```

---

### Task 10: Publish Self-Contained Executable

**Step 1: Publish**

```bash
cd C:/work/JsCoverageReporter
dotnet publish JsCoverageReporter -r win-x64 --self-contained -p:PublishSingleFile=true -o ./dist
```
Expected: `dist/JsCoverageReporter.exe` created (~100 MB including .NET runtime).

**Step 2: Smoke-test the binary**

```bash
./dist/JsCoverageReporter.exe --config sample-scenario.json --output ./dist-report
start ./dist-report/index.html
```
Expected: Works identically to the `dotnet run` version.

**Step 3: Commit**

```bash
git add .
git commit -m "chore: verify self-contained publish works"
```

---

## Setup Reminder for End Users

Include this note in your README or install script:

```
# One-time browser installation (required by Playwright):
.\JsCoverageReporter.exe  # run once to extract; then:
pwsh playwright.ps1 install chromium
```

Or distribute a `setup.ps1`:
```powershell
$toolDir = Split-Path $MyInvocation.MyCommand.Path
& "$toolDir\JsCoverageReporter.exe" --help 2>$null
pwsh "$toolDir\playwright.ps1" install chromium
```
