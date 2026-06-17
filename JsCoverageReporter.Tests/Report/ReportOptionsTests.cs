using System.Text.RegularExpressions;
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

    /// <summary>
    /// 生成日時のタイムスタンプ値のみを固定トークンへ置換する（他のバイトは保持）。
    /// index.html の「生成日時: yyyy-MM-dd HH:mm:ss」と coverage.json の ISO 形式
    /// 「yyyy-MM-ddTHH:mm:sszzz」を対象にする。
    /// </summary>
    private static string NormalizeTimestamps(string s)
    {
        s = Regex.Replace(s, @"生成日時: \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", "生成日時: <TS>");
        // coverage.json の generatedAt は ISO 形式（yyyy-MM-ddTHH:mm:sszzz）。
        // System.Text.Json はタイムゾーンの '+' をエスケープして "\\u002B" の6文字で出力するため、
        // リテラルの [+-] とエスケープ表現 \\u002B の両方を許容しないと正規化されず、
        // 2回の生成が秒境界をまたいだときにバイト比較が稀に失敗する。
        s = Regex.Replace(s, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:[+-]|\\u002[Bb])\d{2}:\d{2}", "<TS>");
        return s;
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
                // index.html / coverage.json は生成日時（DateTimeOffset.Now）を含み、2回の生成が
                // 秒境界をまたぐと差が出る。タイムスタンプ値のみ正規化し、それ以外はバイト一致を検証する。
                Assert.Equal(NormalizeTimestamps(a), NormalizeTimestamps(b));
            }
        }
        finally
        {
            if (Directory.Exists(dirA)) { Directory.Delete(dirA, true); }
            if (Directory.Exists(dirB)) { Directory.Delete(dirB, true); }
        }
    }
}
