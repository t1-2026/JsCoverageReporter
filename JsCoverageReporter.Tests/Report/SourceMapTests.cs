using System.Text;
using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

/// <summary>
/// ソースマップ対応（SourceMap・SourceMapUrlExtractor・SourceMapProjector・SourceMapLoader・
/// HtmlReportGenerator.Generate の元ファイルページ生成）を検証するテスト群。
/// </summary>
public class SourceMapTests
{
    // -----------------------------------------------------------------------
    // テストヘルパー: Base64 VLQ エンコーダー（任意の mappings 文字列を組み立てるために使う）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 整数列を Base64 VLQ にエンコードする（Source Map v3 の mappings セグメント形式）。
    /// </summary>
    private static string EncodeVlq(params int[] values)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var sb = new StringBuilder();
        foreach (int value in values)
        {
            // 符号を最下位ビットに移す（負なら 1）
            int v;
            if (value < 0) { v = ((-value) << 1) | 1; } else { v = value << 1; }
            // 5ビットずつ下位から出力し、続きがあれば継続ビット（32）を立てる
            do
            {
                int digit = v & 31;
                v >>= 5;
                if (v > 0) { digit |= 32; }
                sb.Append(chars[digit]);
            } while (v > 0);
        }
        return sb.ToString();
    }

    /// <summary>
    /// ソースマップの JSON を組み立てる簡易ヘルパー。
    /// </summary>
    private static string BuildMapJson(string mappings, string[] sources, string?[]? sourcesContent = null)
    {
        var sb = new StringBuilder();
        sb.Append("{\"version\":3,\"sources\":[");
        for (int i = 0; i < sources.Length; i++)
        {
            if (i > 0) { sb.Append(','); }
            sb.Append('"').Append(sources[i]).Append('"');
        }
        sb.Append(']');
        if (sourcesContent != null)
        {
            sb.Append(",\"sourcesContent\":[");
            for (int i = 0; i < sourcesContent.Length; i++)
            {
                if (i > 0) { sb.Append(','); }
                if (sourcesContent[i] == null)
                {
                    sb.Append("null");
                }
                else
                {
                    // \n をエスケープして JSON 文字列にする
                    sb.Append('"').Append(sourcesContent[i]!.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")).Append('"');
                }
            }
            sb.Append(']');
        }
        sb.Append(",\"mappings\":\"").Append(mappings).Append("\"}");
        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // SourceMap.Parse のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 基本的なマップ（2セグメント・デルタエンコード）が正しくデコードされることを確認する。
    /// </summary>
    [Fact]
    public void Parse_BasicMap_DecodesSegments()
    {
        // 行0: セグメント1 = (genCol 0, src 0, srcLine 0, srcCol 0)
        //      セグメント2 = (genCol +8, src +0, srcLine +1, srcCol +0) → 絶対値 (8, 0, 1, 0)
        string mappings = EncodeVlq(0, 0, 0, 0) + "," + EncodeVlq(8, 0, 1, 0);
        var map = SourceMap.Parse(BuildMapJson(mappings, ["a.ts"]));

        Assert.NotNull(map);
        Assert.Single(map!.Sources);
        Assert.Equal("a.ts", map.Sources[0]);
        Assert.Single(map.GeneratedLines);
        var segs = map.GeneratedLines[0];
        Assert.Equal(2, segs.Count);
        Assert.Equal(0, segs[0].GenColumn);
        Assert.Equal(0, segs[0].SourceLine);
        Assert.Equal(8, segs[1].GenColumn);
        Assert.Equal(1, segs[1].SourceLine);
    }

    /// <summary>
    /// 複数の生成行（';' 区切り）で genCol が行ごとにリセットされ、srcLine が累積されることを確認する。
    /// </summary>
    [Fact]
    public void Parse_MultiLineMappings_GenColumnResetsPerLine()
    {
        // 行0: (0,0,0,0)  行1: (0, 0, +1, 0) → srcLine 絶対値 1
        string mappings = EncodeVlq(0, 0, 0, 0) + ";" + EncodeVlq(0, 0, 1, 0);
        var map = SourceMap.Parse(BuildMapJson(mappings, ["a.ts"]));

        Assert.NotNull(map);
        Assert.Equal(2, map!.GeneratedLines.Count);
        Assert.Equal(0, map.GeneratedLines[0][0].GenColumn);
        Assert.Equal(0, map.GeneratedLines[0][0].SourceLine);
        Assert.Equal(0, map.GeneratedLines[1][0].GenColumn); // 行頭でリセット
        Assert.Equal(1, map.GeneratedLines[1][0].SourceLine); // 累積
    }

    /// <summary>
    /// sections 形式（インデックスマップ）は非対応のため null を返すことを確認する。
    /// </summary>
    [Fact]
    public void Parse_SectionsMap_ReturnsNull()
    {
        var result = SourceMap.Parse("{\"version\":3,\"sections\":[]}");
        Assert.Null(result);
    }

    /// <summary>
    /// 不正な JSON・必須フィールド欠落の場合に null を返すことを確認する。
    /// </summary>
    [Fact]
    public void Parse_InvalidInput_ReturnsNull()
    {
        Assert.Null(SourceMap.Parse(null));
        Assert.Null(SourceMap.Parse(""));
        Assert.Null(SourceMap.Parse("{not json"));
        Assert.Null(SourceMap.Parse("{\"version\":3}"));                       // sources も mappings もない
        Assert.Null(SourceMap.Parse("{\"version\":3,\"sources\":[\"a.ts\"]}")); // mappings がない
    }

    /// <summary>
    /// sourceRoot が sources の各パスの前に連結されることを確認する。
    /// </summary>
    [Fact]
    public void Parse_SourceRoot_PrependedToSources()
    {
        string json = "{\"version\":3,\"sourceRoot\":\"webpack://app\",\"sources\":[\"src/a.ts\"],\"mappings\":\"\"}";
        var map = SourceMap.Parse(json);
        Assert.NotNull(map);
        Assert.Equal("webpack://app/src/a.ts", map!.Sources[0]);
    }

    // -----------------------------------------------------------------------
    // SourceMapUrlExtractor のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// //# sourceMappingURL= コメントから URL が抽出されることを確認する。
    /// </summary>
    [Fact]
    public void Extract_SourceMappingUrlComment_ReturnsUrl()
    {
        Assert.Equal("app.js.map", SourceMapUrlExtractor.Extract("var a=1;\n//# sourceMappingURL=app.js.map\n"));
        // レガシー形式 //@ も対応する
        Assert.Equal("app.js.map", SourceMapUrlExtractor.Extract("var a=1;\n//@ sourceMappingURL=app.js.map"));
        // 複数ある場合は最後の出現を使う
        Assert.Equal("second.map", SourceMapUrlExtractor.Extract(
            "//# sourceMappingURL=first.map\nvar a=1;\n//# sourceMappingURL=second.map"));
    }

    /// <summary>
    /// コメントがない・形式が不正な場合に null を返すことを確認する。
    /// </summary>
    [Fact]
    public void Extract_NoOrInvalidComment_ReturnsNull()
    {
        Assert.Null(SourceMapUrlExtractor.Extract(null));
        Assert.Null(SourceMapUrlExtractor.Extract(""));
        Assert.Null(SourceMapUrlExtractor.Extract("var a=1;"));
        // "//#" が直結していない（ただの文字列中の出現）は抽出しない
        Assert.Null(SourceMapUrlExtractor.Extract("var s = 'sourceMappingURL=x.map';"));
        // 値が空の場合も null
        Assert.Null(SourceMapUrlExtractor.Extract("//# sourceMappingURL="));
    }

    // -----------------------------------------------------------------------
    // SourceMapLoader.TryDecodeDataUrl のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// base64 形式の data: URL から JSON がデコードされることを確認する。
    /// </summary>
    [Fact]
    public void TryDecodeDataUrl_Base64_ReturnsJson()
    {
        const string json = "{\"version\":3}";
        string dataUrl = "data:application/json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        Assert.Equal(json, SourceMapLoader.TryDecodeDataUrl(dataUrl));
    }

    /// <summary>
    /// 不正な base64・カンマなしの data: URL は null を返すことを確認する。
    /// </summary>
    [Fact]
    public void TryDecodeDataUrl_Invalid_ReturnsNull()
    {
        Assert.Null(SourceMapLoader.TryDecodeDataUrl("data:application/json;base64"));      // カンマなし
        Assert.Null(SourceMapLoader.TryDecodeDataUrl("data:application/json;base64,@@@@")); // 不正な base64
    }

    // -----------------------------------------------------------------------
    // SourceMapProjector のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 実行済み・未実行のセグメントが元ファイルの行ごとに正しく集計されることを確認する。
    /// </summary>
    [Fact]
    public void Project_CoveredAndUncoveredSegments_AggregatedPerSourceLine()
    {
        // 生成コード（1行・16文字）: 前半8文字 = src行0、後半8文字 = src行1
        const string generated = "let a=1;let b=2;";
        string mappings = EncodeVlq(0, 0, 0, 0) + "," + EncodeVlq(8, 0, 1, 0);
        var sourceMap = SourceMap.Parse(BuildMapJson(mappings, ["a.ts"]));
        Assert.NotNull(sourceMap);

        // カバレッジマップ: 前半 = 実行済み(1)、後半 = 未実行(0)
        var map = new int[16];
        for (int i = 0; i < 8;  i++) { map[i] = 1; }
        for (int i = 8; i < 16; i++) { map[i] = 0; }

        var projected = SourceMapProjector.Project(generated, map, sourceMap!);

        Assert.True(projected.ContainsKey(0));
        var lineFlags = projected[0];
        Assert.Equal(SourceMapProjector.CoveredFlag,   lineFlags[0]); // src行0 = 実行済み
        Assert.Equal(SourceMapProjector.UncoveredFlag, lineFlags[1]); // src行1 = 未実行
    }

    /// <summary>
    /// 同じ元ファイル行に実行済みと未実行の両方が射影された場合、両フラグが立つ（部分実行）ことを確認する。
    /// </summary>
    [Fact]
    public void Project_MixedCoverageOnSameSourceLine_BothFlagsSet()
    {
        const string generated = "let a=1;let b=2;";
        // 両セグメントとも src行0 に対応させる
        string mappings = EncodeVlq(0, 0, 0, 0) + "," + EncodeVlq(8, 0, 0, 0);
        var sourceMap = SourceMap.Parse(BuildMapJson(mappings, ["a.ts"]));
        Assert.NotNull(sourceMap);

        var map = new int[16];
        for (int i = 0; i < 8;  i++) { map[i] = 1; }
        for (int i = 8; i < 16; i++) { map[i] = 0; }

        var projected = SourceMapProjector.Project(generated, map, sourceMap!);
        Assert.Equal(SourceMapProjector.CoveredFlag | SourceMapProjector.UncoveredFlag, projected[0][0]);
    }

    /// <summary>
    /// 対象外（-1）の文字しかないセグメントは集計されないことを確認する。
    /// </summary>
    [Fact]
    public void Project_NeutralOnlySegment_NotAggregated()
    {
        const string generated = "let a=1;";
        string mappings = EncodeVlq(0, 0, 0, 0);
        var sourceMap = SourceMap.Parse(BuildMapJson(mappings, ["a.ts"]));
        Assert.NotNull(sourceMap);

        // 全文字が対象外（-1）
        var map = new int[8];
        Array.Fill(map, -1);

        var projected = SourceMapProjector.Project(generated, map, sourceMap!);
        Assert.Empty(projected);
    }

    // -----------------------------------------------------------------------
    // Generate 統合テスト（元ファイルページの生成とインデックス表示）
    // -----------------------------------------------------------------------

    /// <summary>
    /// ソースマップ付きのスクリプトで、元ファイルの詳細ページが生成され、
    /// インデックスに元ファイル行が表示されることを確認する。
    /// 生成コードは1行（ミニファイ相当）だが、ソースマップがあるためスキップされないことも検証する。
    /// </summary>
    [Fact]
    public void Generate_WithSourceMap_CreatesSourceFilePagesAndIndexRows()
    {
        string outputDir = Path.Combine(Path.GetTempPath(), "jscov-srcmap-" + Path.GetRandomFileName());
        try
        {
            // 生成コード（1行・ミニファイ相当）
            const string generated = "function f(){return 1}";
            // 全体を実行済みとするカバレッジデータ
            var functions = new List<FunctionCoverage>
            {
                new FunctionCoverage("", new List<CoverageRange> { new CoverageRange(0, generated.Length, 1) }),
            };
            var script = new ScriptCoverage(
                new PageInfo(0, "http://example.com/"),
                "http://example.com/app.js",
                generated,
                functions);

            // ソースマップ: 生成コード全体が a.ts の行0 に対応し、sourcesContent に元ソースを含む
            string mappings = EncodeVlq(0, 0, 0, 0);
            var sourceMap = SourceMap.Parse(BuildMapJson(
                mappings,
                ["src/a.ts"],
                ["function f() {\n  return 1\n}\n"]));
            Assert.NotNull(sourceMap);
            var sourceMaps = new Dictionary<string, SourceMap> { ["http://example.com/app.js"] = sourceMap! };

            new HtmlReportGenerator().Generate([script], outputDir, sourceMaps);

            // 1行スクリプトだがソースマップがあるためスキップされず、合成ページが生成されること
            Assert.True(File.Exists(Path.Combine(outputDir, "scripts", "script-0.html")));
            // 元ファイルの詳細ページが生成されること
            string srcPagePath = Path.Combine(outputDir, "scripts", "script-0-src-0.html");
            Assert.True(File.Exists(srcPagePath));

            // 元ファイルページ: 行0 が実行済み（covered）として色付けされていること
            string srcPage = File.ReadAllText(srcPagePath);
            Assert.Contains("src/a.ts", srcPage);
            Assert.Contains("line-covered", srcPage);

            // インデックス: 元ファイルが折りたたみ（details）内にリンク付きで表示されていること
            string index = File.ReadAllText(Path.Combine(outputDir, "index.html"));
            Assert.Contains("src/a.ts", index);
            Assert.Contains("script-0-src-0.html", index);
            Assert.Contains("元ファイル (1)", index);
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// sourcesContent がないソースマップでは、集計行は表示されるが詳細ページは生成されない
    /// （リンクなし表示になる）ことを確認する。
    /// </summary>
    [Fact]
    public void Generate_SourceMapWithoutContent_RowWithoutLink()
    {
        string outputDir = Path.Combine(Path.GetTempPath(), "jscov-srcmap-" + Path.GetRandomFileName());
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

            // sourcesContent なしのマップ
            string mappings = EncodeVlq(0, 0, 0, 0);
            var sourceMap = SourceMap.Parse(BuildMapJson(mappings, ["src/a.ts"]));
            Assert.NotNull(sourceMap);
            var sourceMaps = new Dictionary<string, SourceMap> { ["http://example.com/app.js"] = sourceMap! };

            new HtmlReportGenerator().Generate([script], outputDir, sourceMaps);

            // 詳細ページは生成されないこと
            Assert.False(File.Exists(Path.Combine(outputDir, "scripts", "script-0-src-0.html")));

            // インデックスには集計行が「ソース内容なし」として表示されること
            string index = File.ReadAllText(Path.Combine(outputDir, "index.html"));
            Assert.Contains("src/a.ts", index);
            Assert.Contains("ソース内容なし", index);
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// ソースマップなし（従来動作）の場合、Generate の出力が変わらないことを確認する
    /// （1行スクリプトのスキップが引き続き機能すること）。
    /// </summary>
    [Fact]
    public void Generate_WithoutSourceMap_OneLineScriptStillSkipped()
    {
        string outputDir = Path.Combine(Path.GetTempPath(), "jscov-srcmap-" + Path.GetRandomFileName());
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

            new HtmlReportGenerator().Generate([script], outputDir);

            // ソースマップがないため従来どおり1行スクリプトはスキップされること
            Assert.False(File.Exists(Path.Combine(outputDir, "scripts", "script-0.html")));
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { }
        }
    }

    // -----------------------------------------------------------------------
    // BuildOriginalSourceLines のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 元ソースの各行がフラグに応じた状態（Covered/Uncovered/Partial/Neutral）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildOriginalSourceLines_StatusPerLineFlags()
    {
        const string content = "line0\nline1\nline2\nline3\n";
        var lineFlags = new Dictionary<int, int>
        {
            [0] = SourceMapProjector.CoveredFlag,
            [1] = SourceMapProjector.UncoveredFlag,
            [2] = SourceMapProjector.CoveredFlag | SourceMapProjector.UncoveredFlag,
            // 行3 はフラグなし → Neutral
        };

        var lines = HtmlReportGenerator.BuildOriginalSourceLines(content, lineFlags);

        Assert.Equal(4, lines.Count);
        Assert.Equal(LineCoverageStatus.Covered,   lines[0].Status);
        Assert.Equal(LineCoverageStatus.Uncovered, lines[1].Status);
        Assert.Equal(LineCoverageStatus.Partial,   lines[2].Status);
        Assert.Equal(LineCoverageStatus.Neutral,   lines[3].Status);
        // テキストが HTML エスケープ付きで含まれること
        Assert.Contains("line0", lines[0].Html);
        Assert.Contains("covered", lines[0].Html);
    }
}
