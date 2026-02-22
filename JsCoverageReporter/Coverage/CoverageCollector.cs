using Microsoft.Playwright;

namespace JsCoverageReporter.Coverage;

internal class CoverageCollector(IPage page) : IAsyncDisposable
{
    private ICDPSession? _cdp;

    public async Task StartAsync()
    {
        _cdp = await page.Context.NewCDPSessionAsync(page);
        await _cdp.SendAsync("Profiler.enable");
        await _cdp.SendAsync("Debugger.enable");
        await _cdp.SendAsync("Profiler.startPreciseCoverage", new Dictionary<string, object>
        {
            ["callCount"] = true,
            ["detailed"] = true,
            ["allowTriggeredUpdates"] = true,
        });
    }

    public async Task<IReadOnlyList<ScriptCoverage>> StopAsync(string? scriptFilter)
    {
        if (_cdp is null)
            return [];

        System.Text.Json.JsonElement? result = null;
        try
        {
            result = await _cdp.SendAsync("Profiler.takePreciseCoverage");
        }
        finally
        {
            await _cdp.SendAsync("Profiler.stopPreciseCoverage");
            await _cdp.SendAsync("Profiler.disable");
        }

        if (result is null)
            return [];

        var root = result.Value;
        if (!root.TryGetProperty("result", out var resultArray))
            return [];

        var scripts = new List<ScriptCoverage>();

        foreach (var entry in resultArray.EnumerateArray())
        {
            var url = entry.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
            var scriptId = entry.TryGetProperty("scriptId", out var sidProp) ? sidProp.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(url))
                continue;

            if (!string.IsNullOrEmpty(scriptFilter) && !url.Contains(scriptFilter))
                continue;

            string source = "";
            try
            {
                var srcResult = await _cdp.SendAsync("Debugger.getScriptSource", new Dictionary<string, object>
                {
                    ["scriptId"] = scriptId,
                });
                if (srcResult.HasValue && srcResult.Value.TryGetProperty("scriptSource", out var srcProp))
                    source = srcProp.GetString() ?? "";
            }
            catch (PlaywrightException)
            {
                continue;
            }

            if (string.IsNullOrEmpty(source))
                continue;

            var functions = new List<FunctionCoverage>();

            if (entry.TryGetProperty("functions", out var funcsArray))
            {
                foreach (var func in funcsArray.EnumerateArray())
                {
                    var funcName = func.TryGetProperty("functionName", out var fnProp) ? fnProp.GetString() ?? "" : "";
                    var ranges = new List<CoverageRange>();

                    if (func.TryGetProperty("ranges", out var rangesArray))
                    {
                        foreach (var r in rangesArray.EnumerateArray())
                        {
                            var start = r.TryGetProperty("startOffset", out var sProp) ? sProp.GetInt32() : 0;
                            var end = r.TryGetProperty("endOffset", out var eProp) ? eProp.GetInt32() : 0;
                            var count = r.TryGetProperty("count", out var cProp) ? cProp.GetInt32() : 0;
                            ranges.Add(new CoverageRange(start, end, count));
                        }
                    }

                    functions.Add(new FunctionCoverage(funcName, ranges));
                }
            }

            scripts.Add(new ScriptCoverage(url, source, functions));
        }

        return scripts;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cdp is not null)
            await _cdp.DisposeAsync();
    }
}
