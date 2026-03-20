using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

/// <summary>
/// HtmlReportGenerator の HTML 生成メソッド（HtmlEncode・BuildLines）の動作を検証するテスト群。
/// HTML が正しく生成されること（エスケープ・行分割・カバレッジ状態判定）を確認する。
/// </summary>
public class HtmlOutputTests
{
    // -----------------------------------------------------------------------
    // 基本動作のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// HTML の特殊文字（&lt; &gt; &amp; &quot;）が正しくエスケープされることを確認する。
    /// </summary>
    [Fact]
    public void HtmlEncode_EscapesSpecialChars()
    {
        // タグ記号・アンパサンド・クォートが変換されるか確認する
        Assert.Equal("&lt;div&gt;", HtmlReportGenerator.HtmlEncode("<div>"));
        Assert.Equal("a&amp;b",    HtmlReportGenerator.HtmlEncode("a&b"));
        Assert.Equal("&quot;",     HtmlReportGenerator.HtmlEncode("\""));
    }

    /// <summary>
    /// 全文字が実行済み（値=1）の1行ソースを渡した場合、
    /// 1行だけ生成されて "covered" クラスと文字が含まれることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_SingleCoveredLine()
    {
        // 全文字が実行済みの1行を渡す
        var lines = HtmlReportGenerator.BuildLines("hello", [1, 1, 1, 1, 1]);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // "covered" クラスの span タグが含まれているか確認する
        Assert.Contains("class=\"covered\"", lines[0].Html);
        // ソースの文字がそのまま含まれているか確認する
        Assert.Contains("hello", lines[0].Html);
        // 行のステータスが「実行済み」になっているか確認する
        Assert.Equal(LineCoverageStatus.Covered, lines[0].Status);
    }

    /// <summary>
    /// 全文字が未実行（値=0）の1行ソースを渡した場合、
    /// 1行だけ生成されて "uncovered" クラスが含まれることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_SingleUncoveredLine()
    {
        // 全文字が未実行の1行を渡す
        var lines = HtmlReportGenerator.BuildLines("hello", [0, 0, 0, 0, 0]);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // "uncovered" クラスの span タグが含まれているか確認する
        Assert.Contains("class=\"uncovered\"", lines[0].Html);
        // 行のステータスが「未実行」になっているか確認する
        Assert.Equal(LineCoverageStatus.Uncovered, lines[0].Status);
    }

    /// <summary>
    /// 実行済みの文字と未実行の文字が混在する1行を渡した場合、
    /// Partial（部分カバー）ステータスになり両方の span タグが含まれることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_PartialLine_ContainsBothSpans()
    {
        // "AB": A は実行済み（1）、B は未実行（0）→ Partial になるはず
        var lines = HtmlReportGenerator.BuildLines("AB", [1, 0]);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // 行のステータスが「部分カバー」になっているか確認する
        Assert.Equal(LineCoverageStatus.Partial, lines[0].Status);
        // 実行済みの span タグが含まれているか確認する
        Assert.Contains("class=\"covered\"",   lines[0].Html);
        // 未実行の span タグが含まれているか確認する
        Assert.Contains("class=\"uncovered\"", lines[0].Html);
    }

    /// <summary>
    /// 全文字がカバレッジ対象外（値=-1）の1行を渡した場合、
    /// Neutral（対象外）ステータスになることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_NeutralLine_AllOutOfScope()
    {
        // コメント行はカバレッジ対象外（全文字 -1）として渡す
        var lines = HtmlReportGenerator.BuildLines("//comment", [-1, -1, -1, -1, -1, -1, -1, -1, -1]);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // 行のステータスが「対象外」になっているか確認する
        Assert.Equal(LineCoverageStatus.Neutral, lines[0].Status);
    }

    /// <summary>
    /// 改行文字で区切られた2行のソースを渡した場合、正しく2行に分割されることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_MultiLine_SplitsCorrectly()
    {
        // "A\nB": A は実行済み（1）、\n は対象外（-1）、B は未実行（0）
        var lines = HtmlReportGenerator.BuildLines("A\nB", [1, -1, 0]);
        // 2行に分割されているか確認する
        Assert.Equal(2, lines.Count);
        // 1行目が「実行済み」になっているか確認する
        Assert.Equal(LineCoverageStatus.Covered,   lines[0].Status);
        // 2行目が「未実行」になっているか確認する
        Assert.Equal(LineCoverageStatus.Uncovered, lines[1].Status);
    }

    // -----------------------------------------------------------------------
    // HtmlEncode — 境界値・異常データのテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 空文字列を渡した場合、空文字列がそのまま返されることを確認する。
    /// </summary>
    [Fact]
    public void HtmlEncode_EmptyString_ReturnsEmpty()
    {
        // 空文字列はそのまま空文字列になるはず
        Assert.Equal("", HtmlReportGenerator.HtmlEncode(""));
    }

    /// <summary>
    /// HTML 特殊文字を含まない文字列を渡した場合、変換されずそのまま返されることを確認する。
    /// </summary>
    [Fact]
    public void HtmlEncode_NoSpecialChars_ReturnsUnchanged()
    {
        // 変換対象文字（& < > "）を含まない文字列はそのまま返るはず
        Assert.Equal("hello world", HtmlReportGenerator.HtmlEncode("hello world"));
    }

    /// <summary>
    /// 既にエスケープ済みの文字列（"&amp;" など）を渡した場合、
    /// & だけが変換されて "&amp;amp;" になることを確認する（二重変換しない）。
    /// </summary>
    [Fact]
    public void HtmlEncode_AmpersandEscapedFirst_PreventDoubleEncoding()
    {
        // "&amp;" の & だけが変換されて "&amp;amp;" になるはず（"&amp;" が "&amp;amp;" に）
        Assert.Equal("&amp;amp;", HtmlReportGenerator.HtmlEncode("&amp;"));
    }

    /// <summary>
    /// 4つの HTML 特殊文字（& &lt; &gt; "）をすべて含む文字列が正しく変換されることを確認する。
    /// </summary>
    [Fact]
    public void HtmlEncode_AllSpecialCharsInOneString()
    {
        // "&<>\"" → "&amp;&lt;&gt;&quot;" に変換されるはず
        Assert.Equal("&amp;&lt;&gt;&quot;", HtmlReportGenerator.HtmlEncode("&<>\""));
    }

    // -----------------------------------------------------------------------
    // BuildLines — 空・改行・CRLF パターンのテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 空文字列を渡した場合、行データが1件も生成されないことを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_EmptySource_ReturnsNoLines()
    {
        // 空文字列には行がない → 空のリストが返るはず
        var lines = HtmlReportGenerator.BuildLines("", []);
        Assert.Empty(lines);
    }

    /// <summary>
    /// 改行文字（\n）だけのソースを渡した場合、末尾の空要素がトリムされて
    /// 1行（空行）だけ生成されることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_OnlyNewline_ReturnsOneNeutralLine()
    {
        // "\n" のみ → \n で分割すると ["", ""] になるが末尾の空要素をトリムして 1行（空行）になるはず
        var lines = HtmlReportGenerator.BuildLines("\n", [-1]);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // 空行はカバレッジ対象外（Neutral）になっているか確認する
        Assert.Equal(LineCoverageStatus.Neutral, lines[0].Status);
    }

    /// <summary>
    /// 末尾が改行文字（\n）で終わるソースを渡した場合、余分な空行が追加されないことを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_TrailingNewline_DoesNotCreateExtraLine()
    {
        // "hello\n" → 末尾の \n が空要素を作るがトリムされて 1行だけになるはず
        var map = new int[] { 1, 1, 1, 1, 1, -1 };
        var lines = HtmlReportGenerator.BuildLines("hello\n", map);
        // 1行だけ生成されているか確認する（末尾の改行で余分な行が増えないこと）
        Assert.Single(lines);
        // 行が「実行済み」になっているか確認する
        Assert.Equal(LineCoverageStatus.Covered, lines[0].Status);
    }

    /// <summary>
    /// Windows の CRLF（\r\n）改行を含むソースを渡した場合、
    /// \r（キャリッジリターン）が HTML に出力されないことを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_CrlfEnding_CarriageReturnNotInHtml()
    {
        // "ab\r\n": Windows の改行コード。\r は HTML に含まれてはいけない
        // source インデックス: a=0, b=1, \r=2, \n=3（\n で行分割）
        var lines = HtmlReportGenerator.BuildLines("ab\r\n", [1, 1, 1, -1]);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // \r が HTML に含まれていないか確認する
        Assert.DoesNotContain("\r", lines[0].Html);
        // 実際の文字（a・b）は HTML に含まれているか確認する
        Assert.Contains("a", lines[0].Html);
        Assert.Contains("b", lines[0].Html);
    }

    /// <summary>
    /// Windows の CRLF 改行を含む複数行のソースを渡した場合、
    /// すべての行から \r が除去されることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_MultiLineCrlf_AllCrRemoved()
    {
        // "a\r\nb\r\n": CRLF 改行の2行。両方の行から \r が除去されるはず
        var lines = HtmlReportGenerator.BuildLines("a\r\nb\r\n", [1, 1, -1, 1, 1, -1]);
        // 2行に分割されているか確認する
        Assert.Equal(2, lines.Count);
        // 1行目に \r が含まれていないか確認する
        Assert.DoesNotContain("\r", lines[0].Html);
        // 2行目に \r が含まれていないか確認する
        Assert.DoesNotContain("\r", lines[1].Html);
        // 各行の実際の文字が含まれているか確認する
        Assert.Contains("a", lines[0].Html);
        Assert.Contains("b", lines[1].Html);
    }

    /// <summary>
    /// \r が行の途中にある異常データを渡した場合でも、
    /// \r が HTML に出力されないことを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_CrInMiddleOfLine_NotInHtml()
    {
        // "a\rb": 異常な位置の \r。それでも HTML に出力されてはいけない
        var lines = HtmlReportGenerator.BuildLines("a\rb", [1, 1, 1]);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // \r が HTML に含まれていないか確認する
        Assert.DoesNotContain("\r", lines[0].Html);
        // 実際の文字（a・b）は含まれているか確認する
        Assert.Contains("a", lines[0].Html);
        Assert.Contains("b", lines[0].Html);
    }

    // -----------------------------------------------------------------------
    // BuildLines — HTML エスケープのテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// ソースコードに HTML 特殊文字（& &lt; &gt; "）が含まれている場合、
    /// 正しくエスケープされた HTML が生成されることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_HtmlSpecialCharsInSource_AreEscaped()
    {
        // ソースコードが "<>&\"" の場合、各文字がエスケープされるはず
        var source = "<>&\"";
        var map = new int[] { 1, 1, 1, 1 };
        var lines = HtmlReportGenerator.BuildLines(source, map);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // 各文字がエスケープされているか確認する
        Assert.Contains("&lt;",   lines[0].Html);
        Assert.Contains("&gt;",   lines[0].Html);
        Assert.Contains("&amp;",  lines[0].Html);
        Assert.Contains("&quot;", lines[0].Html);
    }

    /// <summary>
    /// TypeScript の型アノテーション（Array&lt;string&gt;）のような &lt; &gt; を含む文字列が
    /// HTML タグとして解釈されないよう正しくエスケープされることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_TypeAnnotationAngles_EscapedInHtml()
    {
        // TypeScript: Array<string> の < と > がエスケープされて HTML タグにならないはず
        var source = "Array<string>";
        var map = new int[source.Length];
        Array.Fill(map, 1);
        var lines = HtmlReportGenerator.BuildLines(source, map);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // < と > がエスケープされているか確認する（"&lt;string&gt;" となるはず）
        Assert.Contains("&lt;string&gt;", lines[0].Html);
        // 生の "<string>" が HTML に残っていないか確認する
        Assert.DoesNotContain("<string>", lines[0].Html);
    }

    /// <summary>
    /// JavaScript の && 演算子のように & を含む文字列が
    /// HTML エスケープされて生の & が残らないことを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_AmpersandOperator_EscapedInHtml()
    {
        // JS: if (a && b) の && は HTML で &amp;&amp; になるはず
        var source = "a&&b";
        var map = new int[] { 1, 1, 1, 1 };
        var lines = HtmlReportGenerator.BuildLines(source, map);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // & が &amp; に変換されているか確認する
        Assert.Contains("&amp;", lines[0].Html);
        // すべてのエンティティを除いた後、生の & が残っていないか確認する
        // （&amp; 以外の & が残っているとブラウザが誤解釈する可能性がある）
        var withoutEntities = lines[0].Html.Replace("&amp;", "").Replace("&lt;", "").Replace("&gt;", "").Replace("&quot;", "");
        Assert.DoesNotContain("&", withoutEntities);
    }

    // -----------------------------------------------------------------------
    // BuildLines — span タグの切り替え・カバレッジ状態のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 1行内でカバレッジ状態が複数回切り替わる場合、
    /// 文字の順序が保たれていて両方の span タグが含まれることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_MultipleSpanTransitionsInOneLine()
    {
        // "ABC": A は実行済み（1）、B は未実行（0）、C は実行済み（1）→ span が3回切り替わる
        var lines = HtmlReportGenerator.BuildLines("ABC", [1, 0, 1]);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // 行のステータスが「部分カバー」になっているか確認する
        Assert.Equal(LineCoverageStatus.Partial, lines[0].Status);
        // 実行済みと未実行の span タグが両方含まれているか確認する
        Assert.Contains("class=\"covered\"",   lines[0].Html);
        Assert.Contains("class=\"uncovered\"", lines[0].Html);
        // 文字 A・B・C がこの順番で HTML に含まれているか確認する
        int posA = lines[0].Html.IndexOf('A');
        int posB = lines[0].Html.IndexOf('B');
        int posC = lines[0].Html.IndexOf('C');
        Assert.True(posA < posB && posB < posC);
    }

    /// <summary>
    /// カバレッジマップが空（長さ0）の場合、全文字がカバレッジ対象外（Neutral）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_EmptyMap_AllCharsNeutral()
    {
        // マップが空 → すべての文字が coverage=-1（対象外）→ Neutral になるはず
        var lines = HtmlReportGenerator.BuildLines("abc", []);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // 行のステータスが「対象外」になっているか確認する
        Assert.Equal(LineCoverageStatus.Neutral, lines[0].Status);
        // "neutral" クラスの span タグが含まれているか確認する
        Assert.Contains("class=\"neutral\"", lines[0].Html);
    }

    /// <summary>
    /// カバレッジマップがソースより短い場合、はみ出た文字がカバレッジ対象外（Neutral）として
    /// 扱われて行状態の判定に影響しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_MapShorterThanSource_ExtraCharsNeutral()
    {
        // "ABCD" でマップが [1,1]（A・B のみ covered）の場合、
        // C・D はマップの範囲外なので coverage=-1（対象外）になる
        // → covered カウント=2、uncovered カウント=0 → 行状態は Covered
        var lines = HtmlReportGenerator.BuildLines("ABCD", [1, 1]);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // A・B が covered、C・D が neutral → 行状態は「実行済み」になっているか確認する
        Assert.Equal(LineCoverageStatus.Covered, lines[0].Status);
        // C・D 部分に "neutral" クラスの span タグが含まれているか確認する
        Assert.Contains("class=\"neutral\"", lines[0].Html);
    }

    // -----------------------------------------------------------------------
    // BuildLines — 複数行・文字インデックスの追跡テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// コードとコードの間に空行がある場合、空行が Neutral（対象外）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_BlankLineBetweenCodeLines_IsNeutral()
    {
        // "A\n\nB": 3行構成（A行・空行・B行）
        // source インデックス: A=0, \n=1（行1の終わり）, \n=2（空行の終わり）, B=3
        var lines = HtmlReportGenerator.BuildLines("A\n\nB", [1, -1, -1, 0]);
        // 3行に分割されているか確認する
        Assert.Equal(3, lines.Count);
        // 1行目（"A"）は「実行済み」になっているか確認する
        Assert.Equal(LineCoverageStatus.Covered,   lines[0].Status);
        // 2行目（空行）は「対象外」になっているか確認する
        Assert.Equal(LineCoverageStatus.Neutral,   lines[1].Status);
        // 3行目（"B"）は「未実行」になっているか確認する
        Assert.Equal(LineCoverageStatus.Uncovered, lines[2].Status);
    }

    /// <summary>
    /// 複数行のソースで、後半の行のカバレッジ値が前半の行の文字数に基づいて
    /// 正しいインデックスで参照されることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_OffsetContinuesCorrectlyAcrossLines()
    {
        // "AB\nCD": 行1は A（実行済み）と B（未実行）、行2は C・D（実行済み）
        // source インデックス: A=0, B=1, \n=2（対象外）, C=3, D=4
        var lines = HtmlReportGenerator.BuildLines("AB\nCD", [1, 0, -1, 1, 1]);
        // 2行に分割されているか確認する
        Assert.Equal(2, lines.Count);
        // 1行目: A=実行済み、B=未実行 → 「部分カバー」になっているか確認する
        Assert.Equal(LineCoverageStatus.Partial,  lines[0].Status);
        // 2行目: C・D=実行済み → 「実行済み」になっているか確認する
        Assert.Equal(LineCoverageStatus.Covered,  lines[1].Status);
    }

    /// <summary>
    /// 3行のソースで各行が異なるカバレッジ状態になることを確認する。
    /// 末尾の改行による余分な空行が生まれないことも確認する。
    /// </summary>
    [Fact]
    public void BuildLines_ThreeLinesWithDifferentStatuses()
    {
        // "A\nB\n\n": 3行構成（A行・B行・空行）
        // source インデックス: A=0, \n=1, B=2, \n=3, \n=4（末尾）
        // 末尾の \n によって作られる空要素はトリムされるが、
        // \n\n の最初の \n が行2の終わりで、次の \n が行3（空行）になる
        var lines = HtmlReportGenerator.BuildLines("A\nB\n\n", [1, -1, 0, -1, -1]);
        // 3行に分割されているか確認する（末尾の \n で余分な行が増えないこと）
        Assert.Equal(3, lines.Count);
        // 1行目（"A"）は「実行済み」になっているか確認する
        Assert.Equal(LineCoverageStatus.Covered,   lines[0].Status);
        // 2行目（"B"）は「未実行」になっているか確認する
        Assert.Equal(LineCoverageStatus.Uncovered, lines[1].Status);
        // 3行目（空行）は「対象外」になっているか確認する
        Assert.Equal(LineCoverageStatus.Neutral,   lines[2].Status);
    }

    // -----------------------------------------------------------------------
    // BuildLines — 実際の JavaScript コードパターンを模倣したテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 3行のコードで1行目と3行目が実行済み、2行目が未実行の場合に
    /// 各行のステータスが正しく判定されることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_FunctionDeclarationPartlyCovered()
    {
        // JS: function add(a,b){\nreturn a+b;\n} を3行で模倣する
        // source: "line1\nline2\nline3"
        //          01234 5 67890 11 23456
        var source = "line1\nline2\nline3";
        var map = new int[source.Length];

        // 行1（0〜4文字目）を「実行済み」に設定する
        for (int i = 0; i <= 4; i++)
        {
            map[i] = 1;
        }
        // 改行（5文字目）は「カバレッジ対象外」に設定する
        map[5] = -1;

        // 行2（6〜10文字目）を「未実行」に設定する
        for (int i = 6; i <= 10; i++)
        {
            map[i] = 0;
        }
        // 改行（11文字目）は「カバレッジ対象外」に設定する
        map[11] = -1;

        // 行3（12〜16文字目）を「実行済み」に設定する
        for (int i = 12; i <= 16; i++)
        {
            map[i] = 1;
        }

        var lines = HtmlReportGenerator.BuildLines(source, map);
        // 3行に分割されているか確認する
        Assert.Equal(3, lines.Count);
        // 1行目（"line1"）は「実行済み」になっているか確認する
        Assert.Equal(LineCoverageStatus.Covered,   lines[0].Status);
        // 2行目（"line2"）は「未実行」になっているか確認する
        Assert.Equal(LineCoverageStatus.Uncovered, lines[1].Status);
        // 3行目（"line3"）は「実行済み」になっているか確認する
        Assert.Equal(LineCoverageStatus.Covered,   lines[2].Status);
    }

    /// <summary>
    /// 日本語などのマルチバイト文字（Unicode）を含むソースコードが
    /// 正しく処理されて HTML に出力されることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_UnicodeCharsInSource_RenderedCorrectly()
    {
        // 日本語の文字（UTF-16 で1コードユニット）が正しく処理されるか確認する
        var lines = HtmlReportGenerator.BuildLines("日本語", [1, 1, 1]);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // 行のステータスが「実行済み」になっているか確認する
        Assert.Equal(LineCoverageStatus.Covered, lines[0].Status);
        // 日本語の文字がそのまま HTML に含まれているか確認する
        Assert.Contains("日本語", lines[0].Html);
    }

    // -----------------------------------------------------------------------
    // BuildIndexPage — ページ URL 列のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// BuildIndexPage がテーブルヘッダーに「ページ URL」列を含めることを確認する。
    /// </summary>
    [Fact]
    public void BuildIndexPage_IncludesPageUrlColumnHeader()
    {
        // 1スクリプト分のサマリー行を渡す（pageUrl フィールドを含む新シグネチャ）
        var rows = new List<(string pageUrl, string url, int covered, int partial, int total, string filename)>
        {
            ("https://example.com", "https://example.com/app.js", 5, 0, 10, "script-0-tab0.html")
        };

        var html = HtmlReportGenerator.BuildIndexPage(rows);

        // テーブルヘッダーに「ページ URL」が含まれているか確認する
        Assert.Contains("ページ URL", html);
        // ページ URL の値がセル内に含まれているか確認する
        Assert.Contains("https://example.com</", html);
    }


    // -----------------------------------------------------------------------
    // BuildScriptPage — ページ URL 表示のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// BuildScriptPage がページ URL とスクリプト URL の両方をページ内に含めることを確認する。
    /// </summary>
    [Fact]
    public void BuildScriptPage_ContainsBothPageUrlAndScriptUrl()
    {
        // tab1 のページから取得したスクリプトを想定する
        var html = HtmlReportGenerator.BuildScriptPage(
            new PageInfo(1, "https://example.com/page2"),
            "https://example.com/js/app.js",
            []);

        // ページ URL が HTML に含まれているか確認する
        Assert.Contains("https://example.com/page2", html);
        // スクリプト URL が HTML に含まれているか確認する
        Assert.Contains("https://example.com/js/app.js", html);
    }

    /// <summary>
    /// BuildScriptPage に空の Url を持つ PageInfo を渡した場合、
    /// "(tab {Index})" 形式のフォールバック文字列が表示されることを確認する。
    /// </summary>
    [Fact]
    public void BuildScriptPage_EmptyPageUrl_ShowsTabIndex()
    {
        // ページ URL が空（取得できなかった）の場合を想定する
        var html = HtmlReportGenerator.BuildScriptPage(
            new PageInfo(2, ""),
            "https://example.com/js/app.js",
            []);

        // フォールバックとして "(tab 2)" が含まれているか確認する
        Assert.Contains("(tab 2)", html);
    }

    // -----------------------------------------------------------------------
    // BuildLines — \r / \0 をカバレッジカウントから除外するテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// \r\n 改行のとき、\r の coverage 値が行のステータス判定に影響しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_CrCoveredButCodeUncovered_StatusIsUncovered()
    {
        // \r\n 改行のとき、\r の coverage が 0 でも行の判定に影響しないことを確認する
        // ソース: "x\r\n" — x=index0, \r=index1, \n=index2
        const string source = "x\r\n";

        // x（index 0）は未実行（0）、\r（index 1）は covered（1）とする
        // BuildLines が \r をスキップしなければ \r の count=1 が covered に加算され Partial になる
        var functions = new List<FunctionCoverage>
        {
            new FunctionCoverage("", new List<CoverageRange>
            {
                // x だけを count=0 にする（\r は count=1 の範囲に含まれると想定）
                new CoverageRange(0, 1, 0),
                new CoverageRange(1, 2, 1),
            }),
        };
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);
        var lines = HtmlReportGenerator.BuildLines(source, map);

        // \r をスキップすれば、行の実コードは x のみ → count=0 → Uncovered
        Assert.Equal(LineCoverageStatus.Uncovered, lines[0].Status);
    }

    /// <summary>
    /// \0（ヌル文字）はカバレッジカウントに含まれないことを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_NulCharUncovered_NotCountedInCoverage()
    {
        // \0（ヌル文字）はカバレッジカウントに含まれないことを確認する
        // ソース: "x\0" — x=index0, \0=index1
        const string source = "x\0";

        // x は covered（1）、\0 は uncovered（0）
        var functions = new List<FunctionCoverage>
        {
            new FunctionCoverage("", new List<CoverageRange>
            {
                new CoverageRange(0, 1, 1),
                new CoverageRange(1, 2, 0),
            }),
        };
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);
        var lines = HtmlReportGenerator.BuildLines(source, map);

        // \0 をスキップすれば、行の実コードは x のみ → count=1 → Covered
        Assert.Equal(LineCoverageStatus.Covered, lines[0].Status);
    }

    // -----------------------------------------------------------------------
    // Task 8: XSS 防止のテスト
    // ソースコード・URL・ファイル名に含まれる特殊文字が HTML エンコードされることを確認する
    // -----------------------------------------------------------------------

    /// <summary>
    /// ソースコード内の &lt;script&gt; タグが HTML エンコードされて出力されることを確認する（XSS 防止）。
    /// BuildLines が各文字を HtmlEncode するため、ソース内の HTML タグはエスケープされるべき。
    /// </summary>
    [Fact]
    public void BuildScriptPage_ScriptTagInSource_IsHtmlEncoded()
    {
        // ソースコード内の <script> タグが HTML エンコードされて出力されることを確認する（XSS 防止）
        const string source = "var x = \"<script>alert(1)</script>\";";
        var functions = new List<FunctionCoverage>();
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);
        var lines = HtmlReportGenerator.BuildLines(source, map);

        // BuildScriptPage を呼び出して HTML 出力を確認する
        // ページ情報（タブ0）を指定する
        var pageInfo = new PageInfo(0, "http://example.com/test.js");
        var scriptPage = HtmlReportGenerator.BuildScriptPage(pageInfo, "test.js", lines);

        // <script> タグが &lt;script&gt; にエンコードされているべき
        Assert.Contains("&lt;script&gt;", scriptPage);
        Assert.DoesNotContain("<script>alert(1)</script>", scriptPage);
    }

    /// <summary>
    /// URL 内の &lt; &gt; が HTML エンコードされて出力されることを確認する（XSS 防止）。
    /// BuildIndexPage は URL を HtmlEncode するため、URL 内の HTML タグはエスケープされるべき。
    /// </summary>
    [Fact]
    public void BuildIndexPage_XssInUrl_IsHtmlEncoded()
    {
        // URL 内の < > が HTML エンコードされて出力されることを確認する（XSS 防止）
        const string url = "http://example.com/<script>alert(1)</script>";
        var rows = new List<(string pageUrl, string url, int covered, int partial, int total, string filename)>
        {
            ("http://example.com", url, 10, 0, 10, "test.html")
        };
        var scriptPage = HtmlReportGenerator.BuildIndexPage(rows);

        // URL の < > が &lt; &gt; にエンコードされているべき
        Assert.Contains("&lt;script&gt;", scriptPage);
        Assert.DoesNotContain("<script>", scriptPage);
    }

    /// <summary>
    /// スクリプト URL 内の &lt; &gt; が HTML エンコードされてスクリプトページのタイトルに表示されることを確認する（XSS 防止）。
    /// BuildScriptPage は scriptUrl を HtmlEncode するため、URL 内の HTML タグはエスケープされるべき。
    /// </summary>
    [Fact]
    public void BuildScriptPage_XssInScriptUrl_IsHtmlEncoded()
    {
        // スクリプト URL 内の < > が HTML エンコードされて出力されることを確認する（XSS 防止）
        const string source = "var x = 1;";
        var functions = new List<FunctionCoverage>();
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);
        var lines = HtmlReportGenerator.BuildLines(source, map);
        var pageInfo = new PageInfo(0, "http://example.com/test.js");

        // スクリプト URL に XSS ペイロードを埋め込む
        var scriptPage = HtmlReportGenerator.BuildScriptPage(pageInfo, "<evil>name</evil>", lines);

        // スクリプト URL の < > が &lt; &gt; にエンコードされているべき
        Assert.Contains("&lt;evil&gt;", scriptPage);
        Assert.DoesNotContain("<evil>", scriptPage);
    }

}