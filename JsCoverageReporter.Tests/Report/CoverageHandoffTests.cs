using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

/// <summary>
/// ハンドオフ JSON の往復で coverages が完全復元されること、
/// およびファイル経由のレポート生成が in-process 直接生成とバイト一致することを検証する。
/// </summary>
public class CoverageHandoffTests
{
    // 「生成日時: yyyy-MM-dd HH:mm:ss」の値部分を固定文字列へ置換する。
    private static string NormalizeTimestamp(string html)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            html,
            @"生成日時: \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}",
            "生成日時: <NORMALIZED>");
    }

    private static List<ScriptCoverage> Sample()
    {
        var src = "function f(){\n  return 1;\n}\nf();\n";
        var fns = new List<FunctionCoverage>
        {
            new("f", new List<CoverageRange> { new(0, 12, 1), new(12, src.Length, 0) }),
        };
        return new List<ScriptCoverage>
        {
            new(new PageInfo(2, "https://example.com/p"), "https://example.com/a.js", src, fns),
        };
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = Sample();
        var json = CoverageHandoff.Serialize("https://example.com/target", original);
        var (targetUrl, restored) = CoverageHandoff.Deserialize(json);

        Assert.Equal("https://example.com/target", targetUrl);
        Assert.Single(restored);
        var a = original[0];
        var b = restored[0];
        Assert.Equal(a.Url, b.Url);
        Assert.Equal(a.Source, b.Source);
        Assert.Equal(a.Page.Index, b.Page.Index);
        Assert.Equal(a.Page.Url, b.Page.Url);
        Assert.Equal(a.Functions.Count, b.Functions.Count);
        Assert.Equal(a.Functions[0].FunctionName, b.Functions[0].FunctionName);
        Assert.Equal(a.Functions[0].Ranges[0].StartOffset, b.Functions[0].Ranges[0].StartOffset);
        Assert.Equal(a.Functions[0].Ranges[0].EndOffset, b.Functions[0].Ranges[0].EndOffset);
        Assert.Equal(a.Functions[0].Ranges[0].Count, b.Functions[0].Ranges[0].Count);
    }

    [Fact]
    public void ReportFromFile_MatchesInProcess()
    {
        var coverages = Sample();
        var dirDirect = Path.Combine(Path.GetTempPath(), "JsCov_HDirect_" + Guid.NewGuid().ToString("N"));
        var dirFile   = Path.Combine(Path.GetTempPath(), "JsCov_HFile_" + Guid.NewGuid().ToString("N"));
        var dataFile  = Path.Combine(Path.GetTempPath(), "JsCov_data_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // in-process 直接生成
            new HtmlReportGenerator().Generate(coverages, null, new ReportOptions(dirDirect));

            // ファイル経由（report-from 相当）: ソースマップなしのケースで等価性を確認
            File.WriteAllText(dataFile, CoverageHandoff.Serialize(null, coverages));
            var (targetUrl, restored) = CoverageHandoff.Deserialize(File.ReadAllText(dataFile));
            new HtmlReportGenerator().Generate(restored, null, new ReportOptions(dirFile, TargetUrl: targetUrl));

            foreach (var rel in new[] { "index.html", "scripts/script-0.html" })
            {
                var p = rel.Replace('/', Path.DirectorySeparatorChar);
                // 生成日時 (DateTimeOffset.Now) は2回の Generate 呼び出しで秒がずれうるが、
                // ハンドオフ契約とは無関係なノイズなので比較前に正規化する。
                // それ以外が完全にバイト一致すれば「ファイル経由 == in-process」が保証される。
                Assert.Equal(NormalizeTimestamp(File.ReadAllText(Path.Combine(dirDirect, p))),
                             NormalizeTimestamp(File.ReadAllText(Path.Combine(dirFile, p))));
            }
        }
        finally
        {
            if (Directory.Exists(dirDirect)) { Directory.Delete(dirDirect, true); }
            if (Directory.Exists(dirFile)) { Directory.Delete(dirFile, true); }
            if (File.Exists(dataFile)) { File.Delete(dataFile); }
        }
    }
}
