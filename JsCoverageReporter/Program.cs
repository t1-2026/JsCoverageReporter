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

if (string.IsNullOrEmpty(scenario.Url))
{
    Console.Error.WriteLine("Error: config 'url' field is required.");
    return 1;
}

Console.WriteLine($"Opening: {scenario.Url}");

try
{
    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = true,
    });
    var page = await browser.NewPageAsync();

    await using var collector = new CoverageCollector(page);
    await collector.StartAsync();

    await page.GotoAsync(scenario.Url);
    await ActionRunner.RunAsync(page, scenario.Actions);

    Console.WriteLine("Collecting coverage...");
    var coverages = await collector.StopAsync(scenario.ScriptFilter);
    Console.WriteLine($"  {coverages.Count} script(s) captured.");

    new HtmlReportGenerator().Generate(coverages, outputDir);
    Console.WriteLine($"Report: {Path.Combine(outputDir, "index.html")}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
