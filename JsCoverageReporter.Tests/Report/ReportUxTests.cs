using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

/// <summary>
/// レポートの構成・見え方・操作性の改善（タイトル・完全URL表示・ナビリンク・実行回数ツールチップ・
/// メタ情報・率の色分け・元ファイル折りたたみ・ソートJS・lang/viewport）を検証するテスト群。
/// </summary>
public class ReportUxTests
{
    /// <summary>2行の実行済みカバレッジ行データを作る簡易ヘルパー。</summary>
    private static List<LineData> MakeLines()
    {
        return
        [
            new LineData("<span class=\"covered\">var a;</span>", LineCoverageStatus.Covered),
            new LineData("<span class=\"covered\">var b;</span>", LineCoverageStatus.Covered),
        ];
    }

    /// <summary>BuildIndexPage 用の1行サマリーを作る簡易ヘルパー。</summary>
    private static List<(IReadOnlyList<(string pageUrl, string tabFilename)> pages,
                         string url, int screenCount, int covered, int partial, int total, string mergedFilename)> MakeRows(
        int covered = 80, int partial = 0, int total = 100)
    {
        return
        [
            ([("http://example.com/", "script-0.html")],
             "http://example.com/app.js", 1, covered, partial, total, "script-0.html"),
        ];
    }

    // -----------------------------------------------------------------------
    // 詳細ページ: タイトル・完全URL・ナビリンク・lang/viewport
    // -----------------------------------------------------------------------

    /// <summary>
    /// ページタイトルにスクリプト名が含まれ、ブラウザのタブで区別できることを確認する。
    /// </summary>
    [Fact]
    public void BuildScriptPage_TitleContainsScriptName()
    {
        var html = HtmlReportGenerator.BuildScriptPage(
            [("http://example.com/")], "http://example.com/js/app.js", MakeLines());

        Assert.Contains("<title>app.js — JS カバレッジ</title>", html);
    }

    /// <summary>
    /// スクリプトの完全な URL がページに表示されること、
    /// URL とファイル名が同じ（非URL文字列）場合は重複表示しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildScriptPage_ShowsFullScriptUrl()
    {
        var html = HtmlReportGenerator.BuildScriptPage(
            [("http://example.com/")], "http://example.com/js/app.js", MakeLines());
        Assert.Contains("class=\"script-url\"", html);
        Assert.Contains("http://example.com/js/app.js", html);

        // GetFileName がそのまま返す文字列（URL ではない）の場合は script-url を出さない
        var html2 = HtmlReportGenerator.BuildScriptPage([], "not-a-url", MakeLines());
        Assert.DoesNotContain("class=\"script-url\"", html2);
    }

    /// <summary>
    /// ナビゲーションリンク（タブ別 → 合成ページなど）が表示され、テキストと href が
    /// HTML エスケープされることを確認する。未指定なら表示されないことも確認する。
    /// </summary>
    [Fact]
    public void BuildScriptPage_NavLinks_Rendered()
    {
        var html = HtmlReportGenerator.BuildScriptPage(
            [], "app.js", MakeLines(), null,
            [("全タブ合成のカバレッジを見る", "script-0.html")]);
        Assert.Contains("class=\"subnav\"", html);
        Assert.Contains("href=\"script-0.html\"", html);
        Assert.Contains("全タブ合成のカバレッジを見る", html);

        var html2 = HtmlReportGenerator.BuildScriptPage([], "app.js", MakeLines());
        Assert.DoesNotContain("class=\"subnav\"", html2);
    }

    /// <summary>
    /// 詳細ページとインデックスページの両方に lang="ja" と viewport メタタグがあることを確認する。
    /// </summary>
    [Fact]
    public void Pages_HaveLangAndViewport()
    {
        var scriptPage = HtmlReportGenerator.BuildScriptPage([], "app.js", MakeLines());
        var indexPage  = HtmlReportGenerator.BuildIndexPage(MakeRows());

        Assert.Contains("<html lang=\"ja\">", scriptPage);
        Assert.Contains("name=\"viewport\"", scriptPage);
        Assert.Contains("<html lang=\"ja\">", indexPage);
        Assert.Contains("name=\"viewport\"", indexPage);
    }

    // -----------------------------------------------------------------------
    // 実行回数（カウントマップとツールチップ）
    // -----------------------------------------------------------------------

    /// <summary>
    /// BuildCountMap が「小さい範囲が後から上書き」の規則で文字ごとの実行回数を書き込むことを確認する。
    /// </summary>
    [Fact]
    public void BuildCountMap_SpecificRangeOverwrites()
    {
        const string source = "0123456789";
        var functions = new List<FunctionCoverage>
        {
            new FunctionCoverage("", new List<CoverageRange>
            {
                new CoverageRange(0, 10, 1), // 全体 1 回
                new CoverageRange(2, 5, 7),  // 内側ブロックは 7 回
            }),
        };
        var counts = CoverageParser.BuildCountMap(source, functions);

        Assert.Equal(1, counts[0]);
        Assert.Equal(7, counts[2]);
        Assert.Equal(7, counts[4]);
        Assert.Equal(1, counts[5]);
    }

    /// <summary>
    /// MergeCountMaps が文字ごとの最大値で合成することを確認する。
    /// </summary>
    [Fact]
    public void MergeCountMaps_TakesMaxPerChar()
    {
        var merged = CoverageParser.MergeCountMaps([1, 5, 0], [3, 2]);
        Assert.Equal([3, 5, 0], merged);
    }

    /// <summary>
    /// BuildLines に countMap を渡すと、行の MaxCount に実行済み文字の最大回数が入り、
    /// BuildScriptPage の行番号ガターに実行回数のツールチップが付くことを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_WithCountMap_GutterTooltipShowsCount()
    {
        const string source = "var a = 1;\n";
        var map = new int[source.Length];
        Array.Fill(map, 1); // 全文字実行済み
        var countMap = new int[source.Length];
        Array.Fill(countMap, 42);

        var lines = HtmlReportGenerator.BuildLines(source, map, countMap);
        Assert.Equal(42, lines[0].MaxCount);

        var html = HtmlReportGenerator.BuildScriptPage([], "app.js", lines);
        Assert.Contains("title=\"実行回数: 42\"", html);
    }

    /// <summary>
    /// countMap 未指定（従来呼び出し）では MaxCount が 0 のままツールチップが付かないことを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_WithoutCountMap_NoTooltip()
    {
        const string source = "var a = 1;\n";
        var map = new int[source.Length];
        Array.Fill(map, 1);

        var lines = HtmlReportGenerator.BuildLines(source, map);
        Assert.Equal(0, lines[0].MaxCount);

        var html = HtmlReportGenerator.BuildScriptPage([], "app.js", lines);
        Assert.DoesNotContain("実行回数:", html);
    }

    // -----------------------------------------------------------------------
    // インデックスページ: メタ情報・率の色分け・折りたたみ・ソート
    // -----------------------------------------------------------------------

    /// <summary>
    /// 対象 URL と生成日時を渡すとメタ情報行が表示され、未指定なら表示されないことを確認する。
    /// </summary>
    [Fact]
    public void BuildIndexPage_MetaInfo_ShownWhenProvided()
    {
        var generatedAt = new DateTimeOffset(2026, 6, 13, 10, 30, 0, TimeSpan.FromHours(9));
        var html = HtmlReportGenerator.BuildIndexPage(MakeRows(), null, "http://example.com/", generatedAt);

        Assert.Contains("class=\"meta\"", html);
        Assert.Contains("生成日時: 2026-06-13 10:30:00", html);
        Assert.Contains("対象 URL: http://example.com/", html);
        Assert.Contains("JsCoverageReporter", html);

        var htmlDefault = HtmlReportGenerator.BuildIndexPage(MakeRows());
        Assert.DoesNotContain("class=\"meta\"", htmlDefault);
    }

    /// <summary>
    /// カバレッジ率セルに率に応じた色分けクラスが付くことを確認する
    /// （80% 以上 = rate-high、50% 以上 = rate-mid、未満 = rate-low）。
    /// </summary>
    [Fact]
    public void BuildIndexPage_RateCell_ColorClassByThreshold()
    {
        Assert.Contains("rate-high", HtmlReportGenerator.BuildIndexPage(MakeRows(covered: 90, total: 100)));
        Assert.Contains("rate-mid",  HtmlReportGenerator.BuildIndexPage(MakeRows(covered: 60, total: 100)));
        Assert.Contains("rate-low",  HtmlReportGenerator.BuildIndexPage(MakeRows(covered: 10, total: 100)));
    }

    /// <summary>
    /// ソースマップの元ファイル一覧が折りたたみ（details）で出力されることを確認する。
    /// </summary>
    [Fact]
    public void BuildIndexPage_SourceFiles_CollapsedInDetails()
    {
        var srcRows = new Dictionary<string, List<(string path, int covered, int partial, int total, string srcFilename)>>
        {
            ["script-0.html"] =
            [
                ("src/a.ts", 10, 0, 20, "script-0-src-0.html"),
                ("src/b.ts", 5,  0, 5,  "script-0-src-1.html"),
            ],
        };
        var html = HtmlReportGenerator.BuildIndexPage(MakeRows(), srcRows);

        Assert.Contains("元ファイル (2)", html);
        Assert.Contains("class=\"srcfiles\"", html);
        Assert.Contains("script-0-src-0.html", html);
        Assert.Contains("src/b.ts", html);
    }

    /// <summary>
    /// ソート用 JavaScript と data 属性（ソートキー）が出力されることを確認する。
    /// data-rate はカルチャに依存しない小数点表記であること。
    /// </summary>
    [Fact]
    public void BuildIndexPage_SortScriptAndDataAttributes_Present()
    {
        var html = HtmlReportGenerator.BuildIndexPage(MakeRows(covered: 80, partial: 5, total: 100));

        Assert.Contains("<script type=\"text/javascript\">", html);
        Assert.Contains("tr class=\"script\"", html);
        Assert.Contains("data-name=\"app.js\"", html);
        Assert.Contains("data-covered=\"80\"", html);
        Assert.Contains("data-rate=\"82.5\"", html); // (80 + 5*0.5) / 100 → 82.5（小数点はピリオド）
    }
    // -----------------------------------------------------------------------
    // URL 集約・表示画面数・テンプレート修正（ガター sticky / scroll-margin / favicon）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 同じページ URL を複数の画面で開いた場合、インデックスでは1エントリに集約され
    /// （「複数ページ」の展開なし・URL 別ページなし）、表示画面数に画面数が表示されることを確認する。
    /// </summary>
    [Fact]
    public void Generate_SameUrlInMultipleScreens_MergedIntoSingleEntry()
    {
        string outputDir = Path.Combine(Path.GetTempPath(), "jscov-merge-" + Path.GetRandomFileName());
        try
        {
            const string source = "function f() {\n  return 1;\n}\n";
            var functions = new List<FunctionCoverage>
            {
                new FunctionCoverage("", new List<CoverageRange> { new CoverageRange(0, source.Length, 1) }),
            };
            // 画面1と画面2が同じ URL で同じスクリプトを読み込んだ
            var screen1 = new ScriptCoverage(new PageInfo(0, "http://example.com/menu"), "http://example.com/app.js", source, functions);
            var screen2 = new ScriptCoverage(new PageInfo(1, "http://example.com/menu"), "http://example.com/app.js", source, functions);

            new HtmlReportGenerator().Generate([screen1, screen2], outputDir);

            string index = File.ReadAllText(Path.Combine(outputDir, "index.html"));
            // URL は1エントリに集約され「複数ページ」の展開 UI は出ないこと
            Assert.DoesNotContain("複数ページ", index);
            Assert.Contains("http://example.com/menu", index);
            // 表示画面数 = 2 が data 属性に入ること
            Assert.Contains("data-screens=\"2\"", index);
            // URL 別ページ（tab ファイル）は生成されないこと
            Assert.False(File.Exists(Path.Combine(outputDir, "scripts", "script-0-tab0.html")));
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// 異なるページ URL から読み込まれた場合は従来どおり「複数ページ」の展開と
    /// URL 別ページが生成されることを確認する（集約は同一 URL のみ）。
    /// </summary>
    [Fact]
    public void Generate_DifferentUrls_StillCreatesPerUrlPages()
    {
        string outputDir = Path.Combine(Path.GetTempPath(), "jscov-merge-" + Path.GetRandomFileName());
        try
        {
            const string source = "function f() {\n  return 1;\n}\n";
            var functions = new List<FunctionCoverage>
            {
                new FunctionCoverage("", new List<CoverageRange> { new CoverageRange(0, source.Length, 1) }),
            };
            var screen1 = new ScriptCoverage(new PageInfo(0, "http://example.com/page1"), "http://example.com/app.js", source, functions);
            var screen2 = new ScriptCoverage(new PageInfo(1, "http://example.com/page2"), "http://example.com/app.js", source, functions);

            new HtmlReportGenerator().Generate([screen1, screen2], outputDir);

            string index = File.ReadAllText(Path.Combine(outputDir, "index.html"));
            Assert.Contains("複数ページ (2)", index);
            Assert.True(File.Exists(Path.Combine(outputDir, "scripts", "script-0-tab0.html")));
            Assert.True(File.Exists(Path.Combine(outputDir, "scripts", "script-0-tab1.html")));
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// インデックスのテーブルに「表示画面数」列があり、画面数が表示されることを確認する。
    /// </summary>
    [Fact]
    public void BuildIndexPage_ScreenCountColumn_Present()
    {
        var html = HtmlReportGenerator.BuildIndexPage(MakeRows());
        Assert.Contains("表示画面数", html);
        Assert.Contains("data-screens=\"1\"", html);
    }

    /// <summary>
    /// 詳細ページのテンプレート修正を確認する:
    /// 行番号ガターの sticky 固定（横スクロールで行番号が消えない）、
    /// 行の scroll-margin-top（ジャンプ先が sticky 凡例に隠れない）、favicon の 404 抑止。
    /// </summary>
    [Fact]
    public void ScriptPage_TemplateFixes_Present()
    {
        var html = HtmlReportGenerator.BuildScriptPage([], "app.js", MakeLines());
        // ガターは横スクロール時も左端に固定される
        Assert.Contains("position:sticky;left:0", html);
        // アンカージャンプ先が sticky 凡例に隠れないよう余白を取る
        Assert.Contains("scroll-margin-top", html);
        // favicon の 404 リクエストを抑止する data: URL
        Assert.Contains("<link rel=\"icon\" href=\"data:,\">", html);

        var index = HtmlReportGenerator.BuildIndexPage(MakeRows());
        Assert.Contains("<link rel=\"icon\" href=\"data:,\">", index);
    }
}