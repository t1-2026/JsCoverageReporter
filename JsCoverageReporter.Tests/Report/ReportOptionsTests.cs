using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

/// <summary>
/// ReportOptions 経由の単一エントリと、従来のパラメータ版が
/// バイト一致の出力を生成することを検証する。
/// </summary>
public class ReportOptionsTests
{
    private static List<ScriptCoverage> SampleCoverages()
    {
        var src = "function f(){\n  return 1;\n}\nf();\n";
        var fns = new List<FunctionCoverage>
        {
            new("f", new List<CoverageRange> { new(0, src.Length, 1) }),
        };
        return new List<ScriptCoverage>
        {
            new(new PageInfo(0, "https://example.com"), "https://example.com/a.js", src, fns),
        };
    }

    [Fact]
    public void Generate_OptionsOverload_MatchesParameterOverload()
    {
        var coverages = SampleCoverages();
        var dirA = Path.Combine(Path.GetTempPath(), "JsCov_OptA_" + Guid.NewGuid().ToString("N"));
        var dirB = Path.Combine(Path.GetTempPath(), "JsCov_OptB_" + Guid.NewGuid().ToString("N"));
        try
        {
            // 従来版（パラメータ）
            new HtmlReportGenerator().Generate(coverages, dirA, null, true, true, "https://example.com");
            // 新版（ReportOptions）
            new HtmlReportGenerator().Generate(
                coverages, null,
                new ReportOptions(dirB, WriteLcov: true, WriteJson: true, TargetUrl: "https://example.com"));

            foreach (var rel in new[] { "index.html", "lcov.info", "coverage.json", "scripts/script-0.html" })
            {
                var a = File.ReadAllText(Path.Combine(dirA, rel.Replace('/', Path.DirectorySeparatorChar)));
                var b = File.ReadAllText(Path.Combine(dirB, rel.Replace('/', Path.DirectorySeparatorChar)));
                Assert.Equal(a, b);
            }
        }
        finally
        {
            if (Directory.Exists(dirA)) { Directory.Delete(dirA, true); }
            if (Directory.Exists(dirB)) { Directory.Delete(dirB, true); }
        }
    }
}
