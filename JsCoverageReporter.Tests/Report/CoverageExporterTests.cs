using System.Text.Json;
using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

/// <summary>
/// CoverageExporter（LCOV / JSON エクスポート）と Generate のファイル出力を検証するテスト群。
/// </summary>
public class CoverageExporterTests
{
    // -----------------------------------------------------------------------
    // BuildLcov のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// ソースマップなしのスクリプトで、行ステータスから LCOV レコードが正しく生成されることを確認する。
    /// 変換規則: Covered/Partial → DA:行,1 / Uncovered → DA:行,0 / Neutral → レコードなし。
    /// </summary>
    [Fact]
    public void BuildLcov_FromStatuses_EmitsDaRecords()
    {
        var script = new ExportScriptData(
            "http://example.com/app.js",
            [(1, "http://example.com/")],
            [LineCoverageStatus.Covered, LineCoverageStatus.Neutral, LineCoverageStatus.Uncovered, LineCoverageStatus.Partial],
            []);

        string lcov = CoverageExporter.BuildLcov([script]);

        Assert.Contains("SF:http://example.com/app.js\n", lcov);
        Assert.Contains("DA:1,1\n", lcov); // Covered（1行目）
        Assert.Contains("DA:3,0\n", lcov); // Uncovered（3行目）
        Assert.Contains("DA:4,1\n", lcov); // Partial → 実行済み扱い（4行目）
        Assert.DoesNotContain("DA:2,", lcov); // Neutral はレコードなし
        Assert.Contains("LF:3\n", lcov); // 計測対象 3 行
        Assert.Contains("LH:2\n", lcov); // 実行された 2 行
        Assert.Contains("end_of_record\n", lcov);
    }

    /// <summary>
    /// ソースマップ解決済みのスクリプトでは、元ファイルのパスで出力され、
    /// バンドル URL の SF レコードは出力されない（二重計上しない）ことを確認する。
    /// </summary>
    [Fact]
    public void BuildLcov_WithSourceFiles_EmitsOriginalPathsOnly()
    {
        var lineFlags = new Dictionary<int, int>
        {
            [0] = SourceMapProjector.CoveredFlag,                                       // 1行目 = 実行済み
            [2] = SourceMapProjector.UncoveredFlag,                                     // 3行目 = 未実行
            [4] = SourceMapProjector.CoveredFlag | SourceMapProjector.UncoveredFlag,    // 5行目 = 部分実行
        };
        var script = new ExportScriptData(
            "http://example.com/bundle.js",
            [(1, "http://example.com/")],
            [LineCoverageStatus.Covered],
            [new ExportSourceFileData("src/a.ts", lineFlags)]);

        string lcov = CoverageExporter.BuildLcov([script]);

        Assert.Contains("SF:src/a.ts\n", lcov);
        Assert.DoesNotContain("SF:http://example.com/bundle.js", lcov); // バンドルは出力しない
        Assert.Contains("DA:1,1\n", lcov); // 実行済み
        Assert.Contains("DA:3,0\n", lcov); // 未実行
        Assert.Contains("DA:5,1\n", lcov); // 部分実行 → 実行済み扱い
        Assert.Contains("LF:3\n", lcov);
        Assert.Contains("LH:2\n", lcov);
    }

    /// <summary>
    /// 空リスト・null で空文字を返し例外が発生しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildLcov_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal("", CoverageExporter.BuildLcov([]));
        Assert.Equal("", CoverageExporter.BuildLcov(null));
    }

    // -----------------------------------------------------------------------
    // BuildJson のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// JSON 出力の構造と集計値（部分実行 0.5 行換算のカバレッジ率を含む）を確認する。
    /// </summary>
    [Fact]
    public void BuildJson_TotalsAndStructure_Correct()
    {
        var lineFlags = new Dictionary<int, int>
        {
            [0] = SourceMapProjector.CoveredFlag,
            [1] = SourceMapProjector.UncoveredFlag,
        };
        var script = new ExportScriptData(
            "http://example.com/app.js",
            [(1, "http://example.com/")],
            // covered=1, partial=1, uncovered=1 → total=3, rate = 100*(1+0.5)/3 = 50.0
            [LineCoverageStatus.Covered, LineCoverageStatus.Partial, LineCoverageStatus.Uncovered, LineCoverageStatus.Neutral],
            [new ExportSourceFileData("src/a.ts", lineFlags)]);

        string json = CoverageExporter.BuildJson([script], DateTimeOffset.Now);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 全体集計（camelCase で出力されること）
        var overall = root.GetProperty("overall");
        Assert.Equal(1,    overall.GetProperty("covered").GetInt32());
        Assert.Equal(1,    overall.GetProperty("partial").GetInt32());
        Assert.Equal(3,    overall.GetProperty("total").GetInt32());
        Assert.Equal(50.0, overall.GetProperty("rate").GetDouble());

        // スクリプト別
        var scripts = root.GetProperty("scripts");
        Assert.Equal(1, scripts.GetArrayLength());
        var s0 = scripts[0];
        Assert.Equal("http://example.com/app.js", s0.GetProperty("url").GetString());
        Assert.Equal(1, s0.GetProperty("pages")[0].GetProperty("tab").GetInt32());
        Assert.Equal("http://example.com/", s0.GetProperty("pages")[0].GetProperty("url").GetString());

        // 元ファイル別（covered=1, uncovered=1 → total=2, rate=50.0）
        var sf0 = s0.GetProperty("sourceFiles")[0];
        Assert.Equal("src/a.ts", sf0.GetProperty("path").GetString());
        Assert.Equal(1,    sf0.GetProperty("covered").GetInt32());
        Assert.Equal(0,    sf0.GetProperty("partial").GetInt32());
        Assert.Equal(2,    sf0.GetProperty("total").GetInt32());
        Assert.Equal(50.0, sf0.GetProperty("rate").GetDouble());

        // 生成日時が含まれること
        Assert.False(string.IsNullOrEmpty(root.GetProperty("generatedAt").GetString()));
    }

    /// <summary>
    /// スクリプトが空の場合も有効な JSON（全体 0 件・rate 0）が生成されることを確認する。
    /// </summary>
    [Fact]
    public void BuildJson_EmptyScripts_ValidJsonWithZeroTotals()
    {
        string json = CoverageExporter.BuildJson([], DateTimeOffset.Now);
        using var doc = JsonDocument.Parse(json);
        var overall = doc.RootElement.GetProperty("overall");
        Assert.Equal(0, overall.GetProperty("total").GetInt32());
        Assert.Equal(0.0, overall.GetProperty("rate").GetDouble());
        Assert.Equal(0, doc.RootElement.GetProperty("scripts").GetArrayLength());
    }

    // -----------------------------------------------------------------------
    // Generate 統合テスト（--lcov / --json 相当のファイル出力）
    // -----------------------------------------------------------------------

    /// <summary>
    /// writeLcov / writeJson を指定すると lcov.info と coverage.json が出力されること、
    /// lcov.info が BOM なし（lcov 系ツールが読める形式）であることを確認する。
    /// </summary>
    [Fact]
    public void Generate_WithExportFlags_WritesLcovAndJson()
    {
        string outputDir = Path.Combine(Path.GetTempPath(), "jscov-export-" + Path.GetRandomFileName());
        try
        {
            // 2行のスクリプト（全行実行済み）
            const string source = "var a = 1;\nvar b = 2;\n";
            var functions = new List<FunctionCoverage>
            {
                new FunctionCoverage("", new List<CoverageRange> { new CoverageRange(0, source.Length, 1) }),
            };
            var script = new ScriptCoverage(
                new PageInfo(0, "http://example.com/"),
                "http://example.com/app.js",
                source,
                functions);

            new HtmlReportGenerator().Generate([script], outputDir, sourceMaps: null, writeLcov: true, writeJson: true);

            // lcov.info が出力されていること
            string lcovPath = Path.Combine(outputDir, "lcov.info");
            Assert.True(File.Exists(lcovPath));
            string lcov = File.ReadAllText(lcovPath);
            Assert.Contains("SF:http://example.com/app.js", lcov);
            Assert.Contains("DA:1,1", lcov);
            Assert.Contains("DA:2,1", lcov);
            Assert.Contains("LH:2", lcov);

            // BOM なしで出力されていること（先頭バイトが 'S'）
            byte[] bytes = File.ReadAllBytes(lcovPath);
            Assert.True(bytes.Length > 0 && bytes[0] == (byte)'S');

            // coverage.json が出力されており、有効な JSON であること
            string jsonPath = Path.Combine(outputDir, "coverage.json");
            Assert.True(File.Exists(jsonPath));
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            Assert.Equal(2, doc.RootElement.GetProperty("overall").GetProperty("total").GetInt32());
            Assert.Equal(100.0, doc.RootElement.GetProperty("overall").GetProperty("rate").GetDouble());
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// フラグ未指定（デフォルト）では lcov.info / coverage.json が出力されないこと（従来動作の維持）を確認する。
    /// </summary>
    [Fact]
    public void Generate_WithoutExportFlags_NoExportFiles()
    {
        string outputDir = Path.Combine(Path.GetTempPath(), "jscov-export-" + Path.GetRandomFileName());
        try
        {
            const string source = "var a = 1;\nvar b = 2;\n";
            var functions = new List<FunctionCoverage>
            {
                new FunctionCoverage("", new List<CoverageRange> { new CoverageRange(0, source.Length, 1) }),
            };
            var script = new ScriptCoverage(
                new PageInfo(0, "http://example.com/"),
                "http://example.com/app.js",
                source,
                functions);

            new HtmlReportGenerator().Generate([script], outputDir);

            Assert.False(File.Exists(Path.Combine(outputDir, "lcov.info")));
            Assert.False(File.Exists(Path.Combine(outputDir, "coverage.json")));
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// ソースマップ付きスクリプトの LCOV 出力が元ファイル単位になることを Generate 経由で確認する
    /// （SourceMapTests の Generate テストと同じ構成で LCOV 出力を検証する）。
    /// </summary>
    [Fact]
    public void Generate_WithSourceMapAndLcov_LcovUsesOriginalPaths()
    {
        string outputDir = Path.Combine(Path.GetTempPath(), "jscov-export-" + Path.GetRandomFileName());
        try
        {
            const string generated = "function f(){return 1}";
            var functions = new List<FunctionCoverage>
            {
                new FunctionCoverage("", new List<CoverageRange> { new CoverageRange(0, generated.Length, 1) }),
            };
            var script = new ScriptCoverage(
                new PageInfo(0, "http://example.com/"),
                "http://example.com/app.js",
                generated,
                functions);

            // 生成コード全体が src/a.ts の行0 に対応する最小マップ（mappings = "AAAA"）
            var sourceMap = SourceMap.Parse(
                "{\"version\":3,\"sources\":[\"src/a.ts\"],\"sourcesContent\":[\"function f() {\\n  return 1\\n}\\n\"],\"mappings\":\"AAAA\"}");
            Assert.NotNull(sourceMap);
            var sourceMaps = new Dictionary<string, SourceMap> { ["http://example.com/app.js"] = sourceMap! };

            new HtmlReportGenerator().Generate([script], outputDir, sourceMaps, writeLcov: true);

            string lcov = File.ReadAllText(Path.Combine(outputDir, "lcov.info"));
            // 元ファイルパスで出力され、バンドル URL は SF に現れないこと
            Assert.Contains("SF:src/a.ts", lcov);
            Assert.DoesNotContain("SF:http://example.com/app.js", lcov);
            Assert.Contains("DA:1,1", lcov); // a.ts の1行目が実行済み
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { }
        }
    }
}
