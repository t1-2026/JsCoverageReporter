using System.Text;
using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

/// <summary>
/// LoadAllAsync の並列取得が、挿入順を保った決定的な辞書を返すことを検証する。
/// data: URL のインラインソースマップを使うためネットワークに依存しない。
/// </summary>
public class SourceMapParallelTests
{
    private static string InlineMap(string file)
    {
        // sources を1つ持つ最小の有効なソースマップ
        var json = "{\"version\":3,\"sources\":[\"" + file + "\"],\"names\":[],\"mappings\":\"\",\"sourcesContent\":[\"x\"]}";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return "data:application/json;base64," + b64;
    }

    private static ScriptCoverage ScriptWithMap(string url, string file)
    {
        var src = "console.log(1);\n//# sourceMappingURL=" + InlineMap(file);
        return new ScriptCoverage(new PageInfo(0, "https://example.com"), url, src,
            new List<FunctionCoverage>());
    }

    [Fact]
    public async Task LoadAllAsync_ReturnsAllMaps_InInsertionOrder()
    {
        var coverages = new List<ScriptCoverage>
        {
            ScriptWithMap("https://example.com/a.js", "a.ts"),
            ScriptWithMap("https://example.com/b.js", "b.ts"),
            ScriptWithMap("https://example.com/c.js", "c.ts"),
        };

        var result = await SourceMapLoader.LoadAllAsync(coverages);

        Assert.Equal(3, result.Count);
        Assert.Equal(
            new[] { "https://example.com/a.js", "https://example.com/b.js", "https://example.com/c.js" },
            result.Keys.ToArray());
    }

    [Fact]
    public async Task LoadAllAsync_DuplicateUrl_AttemptedOnce()
    {
        var coverages = new List<ScriptCoverage>
        {
            ScriptWithMap("https://example.com/a.js", "a.ts"),
            ScriptWithMap("https://example.com/a.js", "a.ts"),
        };

        var result = await SourceMapLoader.LoadAllAsync(coverages);

        Assert.Single(result);
        Assert.True(result.ContainsKey("https://example.com/a.js"));
    }
}
