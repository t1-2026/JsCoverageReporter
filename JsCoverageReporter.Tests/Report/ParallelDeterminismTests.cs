using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

/// <summary>
/// 並列レポート生成が決定的（複数回生成しても全出力がバイト一致）であることを検証する。
/// 同一 URL・別ソースや複数スクリプトを含め、連番・重複サフィックス・インデックス順の安定を確認する。
/// </summary>
public class ParallelDeterminismTests
{
    private static List<ScriptCoverage> ManyScripts()
    {
        var list = new List<ScriptCoverage>();
        for (int n = 0; n < 12; n++)
        {
            var src = $"function f{n}(){{\n  return {n};\n}}\nf{n}();\nfunction g{n}(){{\n  return 0;\n}}\n";
            var fns = new List<FunctionCoverage>
            {
                new($"f{n}", new List<CoverageRange> { new(0, 20, 1) }),
                new($"g{n}", new List<CoverageRange> { new(25, src.Length, 0) }),
            };
            // 一部はファイル名が衝突するように同じベース名 app.js を使う
            var url = (n % 3 == 0) ? "https://example.com/app.js" : $"https://example.com/m{n}.js";
            list.Add(new ScriptCoverage(new PageInfo(n, "https://example.com"), url, src, fns));
        }
        return list;
    }

    private static Dictionary<string, string> ReadAll(string dir)
    {
        var map = new Dictionary<string, string>();
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            map[Path.GetRelativePath(dir, f)] = File.ReadAllText(f);
        }
        return map;
    }

    [Fact]
    public void Generate_IsDeterministic_AcrossRuns()
    {
        var coverages = ManyScripts();
        var dir1 = Path.Combine(Path.GetTempPath(), "JsCov_Det1_" + Guid.NewGuid().ToString("N"));
        var dir2 = Path.Combine(Path.GetTempPath(), "JsCov_Det2_" + Guid.NewGuid().ToString("N"));
        try
        {
            new HtmlReportGenerator().Generate(coverages, null, new ReportOptions(dir1, WriteLcov: true, WriteJson: true));
            new HtmlReportGenerator().Generate(coverages, null, new ReportOptions(dir2, WriteLcov: true, WriteJson: true));

            var a = ReadAll(dir1);
            var b = ReadAll(dir2);

            Assert.Equal(a.Keys.OrderBy(x => x), b.Keys.OrderBy(x => x));
            foreach (var key in a.Keys)
            {
                // coverage.json は生成時刻を含むため比較から除外する
                if (key.EndsWith("coverage.json")) { continue; }
                Assert.Equal(a[key], b[key]);
            }
        }
        finally
        {
            if (Directory.Exists(dir1)) { Directory.Delete(dir1, true); }
            if (Directory.Exists(dir2)) { Directory.Delete(dir2, true); }
        }
    }
}
