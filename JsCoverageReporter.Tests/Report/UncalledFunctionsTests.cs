using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

/// <summary>
/// 関数単位の未実行一覧（CollectUncalledFunctions と BuildScriptPage の一覧表示）を検証するテスト群。
/// </summary>
public class UncalledFunctionsTests
{
    // テスト用のソースコード（foo = 1〜3行目、bar = 4〜6行目）
    private const string TwoFunctionSource =
        "function foo() {\n  return 1;\n}\nfunction bar() {\n  return 2;\n}\n";

    /// <summary>
    /// TwoFunctionSource に対する ScriptCoverage を作る（fooCount / barCount で実行回数を指定する）。
    /// V8 と同様に、スクリプト全体を覆うトップレベルの無名エントリも含める。
    /// </summary>
    private static ScriptCoverage MakeScript(int fooCount, int barCount)
    {
        int fooStart = 0;
        int fooEnd   = TwoFunctionSource.IndexOf('}') + 1;
        int barStart = TwoFunctionSource.IndexOf("function bar", StringComparison.Ordinal);
        int barEnd   = TwoFunctionSource.LastIndexOf('}') + 1;

        var functions = new List<FunctionCoverage>
        {
            // トップレベルエントリ（スクリプト全体・常に実行済み）
            new FunctionCoverage("", new List<CoverageRange> { new CoverageRange(0, TwoFunctionSource.Length, 1) }),
            new FunctionCoverage("foo", new List<CoverageRange> { new CoverageRange(fooStart, fooEnd, fooCount) }),
            new FunctionCoverage("bar", new List<CoverageRange> { new CoverageRange(barStart, barEnd, barCount) }),
        };
        return new ScriptCoverage(new PageInfo(0, "http://example.com/"), "http://example.com/app.js", TwoFunctionSource, functions);
    }

    // -----------------------------------------------------------------------
    // CollectUncalledFunctions のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 未実行の関数だけが行番号付きで一覧に含まれ、実行済みの関数と
    /// スクリプト全体を覆うトップレベルエントリは除外されることを確認する。
    /// </summary>
    [Fact]
    public void CollectUncalledFunctions_OnlyUncalledListed()
    {
        // foo は未実行、bar は実行済み
        var script = MakeScript(fooCount: 0, barCount: 5);

        var result = HtmlReportGenerator.CollectUncalledFunctions([script], TwoFunctionSource);

        Assert.Single(result);
        Assert.Equal("foo", result[0].Name);
        Assert.Equal(1, result[0].Line); // foo は1行目から始まる
    }

    /// <summary>
    /// 関数の開始オフセットから1始まりの行番号が正しく計算されることを確認する。
    /// </summary>
    [Fact]
    public void CollectUncalledFunctions_LineNumberFromOffset()
    {
        // bar だけ未実行（bar は4行目から始まる）
        var script = MakeScript(fooCount: 1, barCount: 0);

        var result = HtmlReportGenerator.CollectUncalledFunctions([script], TwoFunctionSource);

        Assert.Single(result);
        Assert.Equal("bar", result[0].Name);
        Assert.Equal(4, result[0].Line);
    }

    /// <summary>
    /// グループ（複数タブ・複数ナビゲーション）のいずれかで実行されていれば
    /// 未実行一覧から除外される（OR 合成される）ことを確認する。
    /// </summary>
    [Fact]
    public void CollectUncalledFunctions_ExecutedInAnyGroupEntry_Excluded()
    {
        // タブ1では foo 未実行、タブ2では foo 実行済み → foo は一覧に出ない
        var tab1 = MakeScript(fooCount: 0, barCount: 0);
        var tab2 = MakeScript(fooCount: 3, barCount: 0);

        var result = HtmlReportGenerator.CollectUncalledFunctions([tab1, tab2], TwoFunctionSource);

        Assert.Single(result);
        Assert.Equal("bar", result[0].Name); // bar は両タブで未実行のため残る
    }

    /// <summary>
    /// 無名関数は "(無名関数)" として表示されることを確認する。
    /// </summary>
    [Fact]
    public void CollectUncalledFunctions_AnonymousFunction_LabeledAsAnonymous()
    {
        const string source = "var f = function() {\n  return 1;\n};\n";
        var functions = new List<FunctionCoverage>
        {
            new FunctionCoverage("", new List<CoverageRange> { new CoverageRange(0, source.Length, 1) }),
            // 無名関数（名前が空・スクリプト全体は覆わない）
            new FunctionCoverage("", new List<CoverageRange> { new CoverageRange(8, source.IndexOf('}') + 1, 0) }),
        };
        var script = new ScriptCoverage(new PageInfo(0, "http://example.com/"), "http://example.com/app.js", source, functions);

        var result = HtmlReportGenerator.CollectUncalledFunctions([script], source);

        Assert.Single(result);
        Assert.Equal("(無名関数)", result[0].Name);
        Assert.Equal(1, result[0].Line);
    }

    /// <summary>
    /// null・空ソース・Ranges なしの防衛処理で例外が発生しないことを確認する。
    /// </summary>
    [Fact]
    public void CollectUncalledFunctions_DefensiveInputs_NoException()
    {
        Assert.Empty(HtmlReportGenerator.CollectUncalledFunctions(null, "var a;"));
        Assert.Empty(HtmlReportGenerator.CollectUncalledFunctions([], ""));

        var script = new ScriptCoverage(
            new PageInfo(0, ""), "u", "var a;",
            new List<FunctionCoverage> { new FunctionCoverage("f", new List<CoverageRange>()) });
        Assert.Empty(HtmlReportGenerator.CollectUncalledFunctions([script], "var a;"));
    }

    // -----------------------------------------------------------------------
    // BuildScriptPage の一覧表示テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 未実行関数の一覧セクションが出力され、行番号リンクと行アンカー（id="L行番号"）が対応することを確認する。
    /// 関数名は HTML エスケープされること（XSS 対策）も検証する。
    /// </summary>
    [Fact]
    public void BuildScriptPage_WithUncalledFunctions_RendersSectionAndAnchors()
    {
        var lines = new List<LineData>
        {
            new LineData("<span class=\"covered\">var a;</span>",   LineCoverageStatus.Covered),
            new LineData("<span class=\"uncovered\">var b;</span>", LineCoverageStatus.Uncovered),
        };
        var uncalled = new List<(string Name, int Line)> { ("<evil>", 2) };

        var html = HtmlReportGenerator.BuildScriptPage([("http://example.com/")], "app.js", lines, uncalled);

        Assert.Contains("未実行関数 (1)", html);
        Assert.Contains("href=\"#L2\"", html);          // 一覧の行番号リンク
        Assert.Contains("id=\"L2\"", html);              // ジャンプ先の行アンカー
        Assert.Contains("&lt;evil&gt;", html);           // 関数名がエスケープされる
        Assert.DoesNotContain("<evil>", html);
    }

    /// <summary>
    /// 未実行関数がない（null または空）場合は一覧セクションが出力されないことを確認する。
    /// </summary>
    [Fact]
    public void BuildScriptPage_NoUncalledFunctions_NoSection()
    {
        var lines = new List<LineData>
        {
            new LineData("<span class=\"covered\">var a;</span>", LineCoverageStatus.Covered),
        };

        var htmlNull  = HtmlReportGenerator.BuildScriptPage([], "app.js", lines);
        var htmlEmpty = HtmlReportGenerator.BuildScriptPage([], "app.js", lines, []);

        Assert.DoesNotContain("未実行関数", htmlNull);
        Assert.DoesNotContain("未実行関数", htmlEmpty);
    }

    // -----------------------------------------------------------------------
    // Generate 統合テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// Generate で生成された詳細ページに未実行関数の一覧が含まれることを確認する。
    /// </summary>
    [Fact]
    public void Generate_DetailPage_ContainsUncalledFunctionList()
    {
        string outputDir = Path.Combine(Path.GetTempPath(), "jscov-uncalled-" + Path.GetRandomFileName());
        try
        {
            // foo 未実行・bar 実行済みのスクリプト
            var script = MakeScript(fooCount: 0, barCount: 1);

            new HtmlReportGenerator().Generate([script], outputDir);

            string page = File.ReadAllText(Path.Combine(outputDir, "scripts", "script-0.html"));
            Assert.Contains("未実行関数 (1)", page);
            Assert.Contains("foo", page);
            Assert.Contains("href=\"#L1\"", page);
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { }
        }
    }
}
