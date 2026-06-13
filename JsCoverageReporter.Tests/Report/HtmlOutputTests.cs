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
    /// null を渡した場合、例外を投げずに空文字を返すことを確認する（防衛処理の検証）。
    /// </summary>
    [Fact]
    public void HtmlEncode_Null_ReturnsEmpty()
    {
        Assert.Equal("", HtmlReportGenerator.HtmlEncode(null));
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
    /// Partial（部分実行）ステータスになり両方の span タグが含まれることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_PartialLine_ContainsBothSpans()
    {
        // "AB": A は実行済み（1）、B は未実行（0）→ Partial になるはず
        var lines = HtmlReportGenerator.BuildLines("AB", [1, 0]);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // 行のステータスが「部分実行」になっているか確認する
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
    /// CR のみ（\r）の改行を含むソースを渡した場合、\r が行区切りとして機能して
    /// 複数行に分割されることを確認する（旧 Mac 形式・インライン HTML スクリプト対応）。
    /// </summary>
    [Fact]
    public void BuildLines_CrInMiddleOfLine_NotInHtml()
    {
        // "a\rb": CR のみの改行。\r で2行に分割されて HTML には出力されないはず
        // source インデックス: a=0, \r=1（行区切り）, b=2
        var lines = HtmlReportGenerator.BuildLines("a\rb", [1, 1, 1]);
        // \r で2行に分割されているか確認する
        Assert.Equal(2, lines.Count);
        // \r が各行の HTML に含まれていないか確認する
        Assert.DoesNotContain("\r", lines[0].Html);
        Assert.DoesNotContain("\r", lines[1].Html);
        // 実際の文字（a・b）は各行に含まれているか確認する
        Assert.Contains("a", lines[0].Html);
        Assert.Contains("b", lines[1].Html);
    }

    /// <summary>
    /// CR のみ（\r）の改行を含む複数行ソースが正しく複数行に分割されることを確認する。
    /// インライン JavaScript（HTML の &lt;script&gt; タグ内）で発生するケースへの対応。
    /// </summary>
    [Fact]
    public void BuildLines_CrOnlyMultiLine_SplitsIntoMultipleLines()
    {
        // "a\rb\rc": CR のみ改行の3行。3行に分割されるはず
        // source インデックス: a=0, \r=1, b=2, \r=3, c=4
        var lines = HtmlReportGenerator.BuildLines("a\rb\rc", [1, -1, 1, -1, 1]);
        // 3行に分割されているか確認する
        Assert.Equal(3, lines.Count);
        // 各行の文字が正しく含まれているか確認する
        Assert.Contains("a", lines[0].Html);
        Assert.Contains("b", lines[1].Html);
        Assert.Contains("c", lines[2].Html);
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
        // 行のステータスが「部分実行」になっているか確認する
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
        // 1行目: A=実行済み、B=未実行 → 「部分実行」になっているか確認する
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
        // 1タブ分のサマリー行を渡す（新シグネチャ）
        var rows = new List<(IReadOnlyList<(string pageUrl, string tabFilename)> pages,
                             string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            ([("https://example.com", "script-0.html")],
             "https://example.com/app.js", 1, 5, 0, 10, "script-0.html")
        };

        var html = HtmlReportGenerator.BuildIndexPage(rows);

        // テーブルヘッダーに「ページ URL」が含まれているか確認する
        Assert.Contains("ページ URL", html);
        // ページ URL の値がセル内に含まれているか確認する
        Assert.Contains("https://example.com", html);
    }


    // -----------------------------------------------------------------------
    // BuildScriptPage — ページ URL 表示のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// BuildScriptPage がページ URL とスクリプトファイル名の両方をページ内に含めることを確認する。
    /// スクリプトはファイル名部分のみ（URL 全体ではなく）表示される。
    /// </summary>
    [Fact]
    public void BuildScriptPage_ContainsBothPageUrlAndScriptUrl()
    {
        // ページ URL リストを渡す（旧: PageInfo(1, ...) → 新: リスト）
        var html = HtmlReportGenerator.BuildScriptPage(
            [("https://example.com/page2")],
            "https://example.com/js/app.js",
            []);

        // ページ URL が HTML に含まれているか確認する
        Assert.Contains("https://example.com/page2", html);
        // スクリプトはファイル名のみ（app.js）が表示されるか確認する
        Assert.Contains("app.js", html);
    }

    /// <summary>
    /// BuildScriptPage にページ URL リストが空の場合、「(不明)」が表示されることを確認する。
    /// </summary>
    [Fact]
    public void BuildScriptPage_EmptyPageUrls_ShowsUnknown()
    {
        // ページ URL リストが空の場合は「(不明)」と表示されるはず
        var html = HtmlReportGenerator.BuildScriptPage(
            new List<string>(),
            "https://example.com/js/app.js",
            []);

        // フォールバックとして "(不明)" が含まれているか確認する
        Assert.Contains("(不明)", html);
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
        var map = CoverageParser.BuildCoverageMap(source, functions);
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
        var map = CoverageParser.BuildCoverageMap(source, functions);
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
        var map = CoverageParser.BuildCoverageMap(source, functions);
        var lines = HtmlReportGenerator.BuildLines(source, map);

        // BuildScriptPage を呼び出して HTML 出力を確認する
        var scriptPage = HtmlReportGenerator.BuildScriptPage(
            [("http://example.com/test.js")], "test.js", lines);

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
        // スクリプト URL 内の < > が HTML エンコードされることを確認する（XSS 防止）
        const string url = "http://example.com/<script>alert(1)</script>";
        var rows = new List<(IReadOnlyList<(string pageUrl, string tabFilename)> pages,
                             string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            ([("http://example.com", "test.html")], url, 1, 10, 0, 10, "test.html")
        };
        var scriptPage = HtmlReportGenerator.BuildIndexPage(rows);

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
        var map = CoverageParser.BuildCoverageMap(source, functions);
        var lines = HtmlReportGenerator.BuildLines(source, map);
        // スクリプト URL に XSS ペイロードを埋め込む
        var scriptPage = HtmlReportGenerator.BuildScriptPage(
            [("http://example.com/test.js")], "<evil>name</evil>", lines);

        // スクリプト URL の < > が &lt; &gt; にエンコードされているべき
        Assert.Contains("&lt;evil&gt;", scriptPage);
        Assert.DoesNotContain("<evil>", scriptPage);
    }

    // -----------------------------------------------------------------------
    // BuildLines — 境界値・エッジケースのテスト
    // 空ソース・末尾改行・ニュートラル行・複数行オフセット検証
    // -----------------------------------------------------------------------

    /// <summary>
    /// ソースコードが改行（\n）で終わる場合、末尾の空行が除外されることを確認する。
    /// "abc\n" を Split('\n') すると ["abc", ""] となるが、末尾の空要素は除外される。
    /// </summary>
    [Fact]
    public void BuildLines_SourceEndingWithNewline_TrailingEmptyLineExcluded()
    {
        // "abc\n" は Split('\n') で ["abc", ""] → 末尾の空要素を除いて 1 行
        var lines = HtmlReportGenerator.BuildLines("abc\n", [1, 1, 1, -1]);
        // 末尾の空行が除外されて1行だけ返されるべき
        Assert.Single(lines);
    }

    /// <summary>
    /// 全文字が -1（カバレッジ対象外）の行は Neutral ステータスになることを確認する。
    /// 空白や上位スコープのコメント行などが対象。
    /// </summary>
    [Fact]
    public void BuildLines_AllNeutralChars_StatusIsNeutral()
    {
        // 全文字が -1（ニュートラル）の行は Neutral になるべき
        var lines = HtmlReportGenerator.BuildLines("   ", [-1, -1, -1]);
        Assert.Single(lines);
        Assert.Equal(LineCoverageStatus.Neutral, lines[0].Status);
    }

    /// <summary>
    /// 3行のソースを渡したとき、各行のオフセット計算が正しく行われることを確認する。
    /// "a\nb\nc" では a=idx0, b=idx2, c=idx4 となり、行ごとに正しい map 値が参照される。
    /// </summary>
    [Fact]
    public void BuildLines_ThreeLineSource_CorrectStatusPerLine()
    {
        // "a\nb\nc": a=idx0(covered), \n=split, b=idx2(uncovered), \n=split, c=idx4(covered)
        const string source = "a\nb\nc";
        var map = new int[] { 1, -1, 0, -1, 1 };
        var lines = HtmlReportGenerator.BuildLines(source, map);

        // 3行が正しく生成されているか確認する
        Assert.Equal(3, lines.Count);
        // 1行目: a(idx=0, map=1) → Covered
        Assert.Equal(LineCoverageStatus.Covered,   lines[0].Status);
        // 2行目: b(idx=2, map=0) → Uncovered（オフセット計算が正しければ idx=2 が参照される）
        Assert.Equal(LineCoverageStatus.Uncovered, lines[1].Status);
        // 3行目: c(idx=4, map=1) → Covered
        Assert.Equal(LineCoverageStatus.Covered,   lines[2].Status);
    }

    // -----------------------------------------------------------------------
    // HtmlEncode のエッジケーステスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// "&lt;" のように既にエンコードされた文字列を HtmlEncode に渡した場合、
    /// &amp; → &amp;amp; に変換されるが &lt; の部分は二重変換されないことを確認する。
    /// （& を最初に変換する実装になっているため）
    /// </summary>
    [Fact]
    public void HtmlEncode_AlreadyEncodedString_AmpersandNotDoubleEncoded()
    {
        // "&lt;" を渡すと: & → &amp; → "&amp;lt;" となる
        // &amp; の & が再び変換されて "&amp;amp;lt;" にはならないはず
        Assert.Equal("&amp;lt;", HtmlReportGenerator.HtmlEncode("&lt;"));
    }

    // -----------------------------------------------------------------------
    // BuildLines — map の長さが source と異なる場合のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// map 配列の長さがソースより短い場合、map 範囲外の文字は -1（ニュートラル）として
    /// 扱われることを確認する。
    /// CDP データが不完全な場合や、ソースが後から変更された場合の安全処理。
    /// </summary>
    [Fact]
    public void BuildLines_MapShorterThanSource_ExtraCharsAreNeutral()
    {
        // "hello"（5文字）に対して map は 2 要素だけ（残り 3 文字は map 範囲外）
        var source = "hello";
        var map = new int[] { 1, 0 }; // h=covered, e=uncovered, l/l/o=範囲外
        var lines = HtmlReportGenerator.BuildLines(source, map);

        // 1行だけ生成されているか確認する
        Assert.Single(lines);

        // covered(h) と uncovered(e) が両方あるため Partial になるはず
        // （l/l/o は -1 → 計数されない）
        Assert.Equal(LineCoverageStatus.Partial, lines[0].Status);
    }

    /// <summary>
    /// 1文字だけのソースコードを渡した場合でも、
    /// 1行のデータが正しく生成されることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_SingleCharSource_ProducesOneLine()
    {
        // 1文字のソース・1要素のマップ
        var lines = HtmlReportGenerator.BuildLines("x", [1]);

        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // 1文字が covered なので行全体が Covered になるべき
        Assert.Equal(LineCoverageStatus.Covered, lines[0].Status);
        // "x" がそのまま HTML に含まれているか確認する
        Assert.Contains("x", lines[0].Html);
    }

    /// <summary>
    /// map 配列が空（長さ 0）の場合、全文字が -1 として扱われ
    /// 行は Neutral ステータスになることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_EmptyMapWithSource_AllCharsNeutral()
    {
        // ソースはあるが map が空（範囲外アクセスを避ける必要がある）
        var lines = HtmlReportGenerator.BuildLines("abc", []);

        // 1行生成される
        Assert.Single(lines);
        // map 範囲外の文字はすべて -1（ニュートラル）→ 行ステータスは Neutral
        Assert.Equal(LineCoverageStatus.Neutral, lines[0].Status);
    }

    // -----------------------------------------------------------------------
    // BuildIndexPage の追加確認テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// スクリプトが 1 件もない状態で BuildIndexPage を呼んでも、
    /// 有効な HTML が返されクラッシュしないことを確認する。
    /// </summary>
    [Fact]
    public void BuildIndexPage_EmptyRows_ReturnsValidHtml()
    {
        // スクリプト行が空のリストを渡す（新シグネチャ）
        var rows = new List<(IReadOnlyList<(string pageUrl, string tabFilename)> pages,
                             string url, int screenCount, int covered, int partial, int total, string mergedFilename)>();

        var html = HtmlReportGenerator.BuildIndexPage(rows);

        Assert.NotNull(html);
        Assert.NotEmpty(html);
        Assert.Contains("<html", html);
        Assert.Contains("ページ URL", html);
    }

    /// <summary>
    /// 複数スクリプトを渡した場合、すべての行が出力に含まれることを確認する。
    /// </summary>
    [Fact]
    public void BuildIndexPage_MultipleRows_AllIncluded()
    {
        // 3スクリプト分のサマリー行を用意する（新シグネチャ・新ファイル名形式）
        var rows = new List<(IReadOnlyList<(string pageUrl, string tabFilename)> pages,
                             string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            ([("https://example.com", "script-0.html")], "https://example.com/a.js", 1, 5,  0, 10, "script-0.html"),
            ([("https://example.com", "script-1.html")], "https://example.com/b.js", 1, 3,  2, 10, "script-1.html"),
            ([("https://example.com", "script-2.html")], "https://example.com/c.js", 1, 0,  0, 10, "script-2.html"),
        };

        var html = HtmlReportGenerator.BuildIndexPage(rows);

        Assert.Contains("a.js", html);
        Assert.Contains("b.js", html);
        Assert.Contains("c.js", html);
        Assert.Contains("script-0.html", html);
        Assert.Contains("script-2.html", html);
    }

    /// <summary>
    /// 複数のページ URL を渡した場合、すべての URL がページ内に含まれることを確認する。
    /// </summary>
    [Fact]
    public void BuildScriptPage_MultiplePageUrls_ShowsAllUrls()
    {
        // 2つのページ URL を渡す
        var html = HtmlReportGenerator.BuildScriptPage(
            [("https://page-a.com/"), ("https://page-b.com/")],
            "app.js",
            []);

        // 両方の URL が HTML に含まれているか確認する
        Assert.Contains("https://page-a.com/", html);
        Assert.Contains("https://page-b.com/", html);
    }

    // -----------------------------------------------------------------------
    // BuildIndexPage — 複数タブの展開 UI テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 複数タブで同じスクリプトが読み込まれた場合、
    /// index.html に details 要素と各タブへのリンクが含まれることを確認する。
    /// </summary>
    [Fact]
    public void BuildIndexPage_MultiTabScript_ShowsDetailsElement()
    {
        // 2タブで同じスクリプトが読み込まれた場合を想定する
        var tabs = new List<(string pageUrl, string tabFilename)>
        {
            ("https://page-a.com/", "script-0-tab0.html"),
            ("https://page-b.com/", "script-0-tab1.html"),
        };
        var rows = new List<(IReadOnlyList<(string pageUrl, string tabFilename)> pages,
                             string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            (tabs, "https://example.com/app.js", 2, 80, 5, 100, "script-0.html"),
        };

        var html = HtmlReportGenerator.BuildIndexPage(rows);

        // <details> 要素が含まれているか確認する（展開 UI）
        Assert.Contains("<details", html);
        // 各タブへのリンクが含まれているか確認する
        Assert.Contains("script-0-tab0.html", html);
        Assert.Contains("script-0-tab1.html", html);
        // 各タブのページ URL が含まれているか確認する
        Assert.Contains("https://page-a.com/", html);
        Assert.Contains("https://page-b.com/", html);
        // 合成ページへのリンクが含まれているか確認する
        Assert.Contains("script-0.html", html);
    }

    /// <summary>
    /// 単一タブのスクリプトの場合、details 要素は使わずに
    /// ページ URL を直接表示することを確認する。
    /// </summary>
    [Fact]
    public void BuildIndexPage_SingleTabScript_NoDetailsElement()
    {
        // 1タブのみの場合は展開 UI が不要
        var rows = new List<(IReadOnlyList<(string pageUrl, string tabFilename)> pages,
                             string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            ([("https://example.com/", "script-0.html")],
             "https://example.com/app.js", 1, 80, 5, 100, "script-0.html"),
        };

        var html = HtmlReportGenerator.BuildIndexPage(rows);

        // 単一タブでは <details> 要素を使わないことを確認する
        Assert.DoesNotContain("<details", html);
        // ページ URL が直接表示されることを確認する
        Assert.Contains("https://example.com/", html);
    }

    // -----------------------------------------------------------------------
    // GetFileName — URL からファイル名を取得するテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// http:// の URL のパスからファイル名部分が返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_HttpUrl_ReturnsFilename()
    {
        Assert.Equal("app.js", HtmlReportGenerator.GetFileName("http://example.com/js/app.js"));
    }

    /// <summary>
    /// https:// の URL のパスからファイル名部分が返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_HttpsUrl_ReturnsFilename()
    {
        Assert.Equal("app.js", HtmlReportGenerator.GetFileName("https://example.com/js/app.js"));
    }

    /// <summary>
    /// ポート番号付きの URL からファイル名部分が返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_UrlWithPort_ReturnsFilename()
    {
        Assert.Equal("util.js", HtmlReportGenerator.GetFileName("http://localhost:3000/scripts/util.js"));
    }

    /// <summary>
    /// クエリ文字列（? 以降）が除去され、ファイル名のみ返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_UrlWithQueryString_ReturnsFilenameOnly()
    {
        Assert.Equal("app.js", HtmlReportGenerator.GetFileName("http://example.com/js/app.js?v=1.2.3"));
    }

    /// <summary>
    /// フラグメント（# 以降）が除去され、ファイル名のみ返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_UrlWithFragment_ReturnsFilenameOnly()
    {
        Assert.Equal("app.js", HtmlReportGenerator.GetFileName("http://example.com/js/app.js#section"));
    }

    /// <summary>
    /// クエリ文字列とフラグメントの両方が除去され、ファイル名のみ返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_UrlWithQueryAndFragment_ReturnsFilenameOnly()
    {
        Assert.Equal("app.js", HtmlReportGenerator.GetFileName("http://example.com/js/app.js?v=1#s"));
    }

    /// <summary>
    /// パス部分がない URL（http://example.com）の場合、ホスト名部分を返すことを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_UrlNoPath_ReturnsHost()
    {
        Assert.Equal("example.com", HtmlReportGenerator.GetFileName("http://example.com"));
    }

    /// <summary>
    /// スキームのみの不正 URL（"http://"）の場合、空文字列が返されることを確認する。
    /// ホスト部が空のためホスト名抽出結果も空文字列になる。
    /// </summary>
    [Fact]
    public void GetFileName_SchemeOnly_ReturnsEmptyString()
    {
        // "http://" はホスト名がない不正 URL — 空文字列を返す
        Assert.Equal("", HtmlReportGenerator.GetFileName("http://"));
    }

    /// <summary>
    /// パスがルートスラッシュのみの URL（http://example.com/）の場合、
    /// ホスト名（example.com）が返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_UrlWithRootSlashOnly_ReturnsHostname()
    {
        // '/' だけのパスの場合はホスト名を返す
        Assert.Equal("example.com", HtmlReportGenerator.GetFileName("http://example.com/"));
    }

    /// <summary>
    /// http/https 以外のスキーム（data: など）の URL はそのまま返されることを確認する。
    /// パス解析を行うとセグメントが不正になるため変換しない。
    /// </summary>
    [Fact]
    public void GetFileName_NonHttpUrl_ReturnsAsIs()
    {
        // http/https/file/data 以外のスキームはそのまま返す
        const string url = "app://some/path.js";
        Assert.Equal(url, HtmlReportGenerator.GetFileName(url));
    }

    /// <summary>
    /// data: URL は巨大な Base64 文字列になりうるため、表示用ラベル "(data URL)" を返すことを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_DataUrl_ReturnsDataUrlLabel()
    {
        // data: URL はそのまま表示すると <h1> が巨大になるため短縮表示する
        Assert.Equal("(data URL)", HtmlReportGenerator.GetFileName("data:text/javascript,var x=1"));
        Assert.Equal("(data URL)", HtmlReportGenerator.GetFileName("DATA:text/javascript;base64," + new string('A', 500)));
    }

    /// <summary>
    /// 空文字列を渡した場合、空文字列が返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", HtmlReportGenerator.GetFileName(""));
    }

    /// <summary>
    /// スラッシュを含まない文字列（ファイル名そのもの）はそのまま返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_PlainFilename_ReturnsAsIs()
    {
        Assert.Equal("app.js", HtmlReportGenerator.GetFileName("app.js"));
    }

    /// <summary>
    /// file:// URL（Windows パス）からファイル名部分が返されることを確認する。
    /// 例: file:///C:/work/demo/app.js → app.js
    /// </summary>
    [Fact]
    public void GetFileName_FileUrl_ReturnsFilename()
    {
        Assert.Equal("app.js", HtmlReportGenerator.GetFileName("file:///C:/work/demo/app.js"));
    }

    /// <summary>
    /// file:// URL（Unix パス）からファイル名部分が返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_FileUrlUnixPath_ReturnsFilename()
    {
        Assert.Equal("util.js", HtmlReportGenerator.GetFileName("file:///home/user/scripts/util.js"));
    }

    /// <summary>
    /// XSS 用の偽 URL（HTML タグ形式）はスラッシュを含むが http/https/file でないため
    /// パス解析を行わず、そのまま返されることを確認する。
    /// （HtmlEncode は呼び出し側で行う）
    /// </summary>
    [Fact]
    public void GetFileName_XssFakeUrl_ReturnsAsIs()
    {
        const string fakeUrl = "<evil>name</evil>";
        Assert.Equal(fakeUrl, HtmlReportGenerator.GetFileName(fakeUrl));
    }

    // -----------------------------------------------------------------------
    // BuildScriptPage — ページ URL が空のときのラベル表示テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// ページ URL が空文字列の場合、プレースホルダー "(URL なし)" が表示されることを確認する。
    /// </summary>
    [Fact]
    public void BuildScriptPage_SinglePageWithEmptyUrl_ShowsPlaceholder()
    {
        var html = HtmlReportGenerator.BuildScriptPage(
            [("")],
            "app.js",
            []);

        // URL が空のときはプレースホルダーが表示されることを確認する
        Assert.Contains("(URL なし)", html);
    }

    /// <summary>
    /// 複数のページ URL がある場合、すべての URL がカンマ区切りで表示されることを確認する。
    /// </summary>
    [Fact]
    public void BuildScriptPage_MultiplePageUrls_CommaSeparated()
    {
        var html = HtmlReportGenerator.BuildScriptPage(
            [("http://example.com/page1"), ("http://example.com/page2")],
            "app.js",
            []);

        // 両方の URL が含まれているか確認する
        Assert.Contains("http://example.com/page1", html);
        Assert.Contains("http://example.com/page2", html);
        // カンマ区切りで複数エントリが並んでいるか確認する
        Assert.Contains(",", html);
    }

    /// <summary>
    /// 3つ以上のページ URL がある場合、すべての URL がカンマ区切りで表示されることを確認する。
    /// 2件テストは既にあるが、3件以上のループ動作を明示的に確認する。
    /// </summary>
    [Fact]
    public void BuildScriptPage_ThreePageUrls_AllUrlsIncluded()
    {
        var html = HtmlReportGenerator.BuildScriptPage(
            [("http://example.com/page1"),
             ("http://example.com/page2"),
             ("http://example.com/page3")],
            "app.js",
            []);

        // すべての URL が含まれているか確認する
        Assert.Contains("http://example.com/page1", html);
        Assert.Contains("http://example.com/page2", html);
        Assert.Contains("http://example.com/page3", html);
        // カンマ区切りで複数エントリが並んでいるか確認する
        Assert.Contains(",", html);
    }

    // -----------------------------------------------------------------------
    // BuildIndexPage — タブ URL が空 / カバレッジ率表示テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 単一ページで pageUrl が空の場合、"(不明)" が表示されることを確認する。
    /// </summary>
    [Fact]
    public void BuildIndexPage_SinglePageWithEmptyUrl_ShowsUnknown()
    {
        var rows = new List<(IReadOnlyList<(string pageUrl, string tabFilename)> pages,
                             string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            ([("", "script-0.html")], "http://example.com/app.js", 1, 5, 0, 10, "script-0.html"),
        };

        var html = HtmlReportGenerator.BuildIndexPage(rows);

        // URL が空のときは "(不明)" が表示されることを確認する
        Assert.Contains("(不明)", html);
    }

    /// <summary>
    /// カバレッジ率（%）が HTML 出力に含まれることを確認する。
    /// covered=5, partial=0, total=10 → 50.0% のはず。
    /// </summary>
    [Fact]
    public void BuildIndexPage_CoveragePercentage_DisplayedInOutput()
    {
        var rows = new List<(IReadOnlyList<(string pageUrl, string tabFilename)> pages,
                             string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            ([("https://example.com", "script-0.html")], "https://example.com/app.js", 1, 5, 0, 10, "script-0.html"),
        };

        var html = HtmlReportGenerator.BuildIndexPage(rows);

        // 個別スクリプトの行カバレッジ率が含まれているか確認する
        Assert.Contains("50.0%", html);
    }

    /// <summary>
    /// 複数スクリプトの合計カバレッジ率が正しく計算されて出力されることを確認する。
    /// covered=3+2=5, partial=0, total=5+5=10 → 50.0% のはず。
    /// </summary>
    [Fact]
    public void BuildIndexPage_OverallCoveragePercentage_Correct()
    {
        var rows = new List<(IReadOnlyList<(string pageUrl, string tabFilename)> pages,
                             string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            ([("https://example.com", "script-0.html")], "https://example.com/a.js", 1, 3, 0, 5, "script-0.html"),
            ([("https://example.com", "script-1.html")], "https://example.com/b.js", 1, 2, 0, 5, "script-1.html"),
        };

        var html = HtmlReportGenerator.BuildIndexPage(rows);

        // 全体カバレッジ率 50.0% が含まれているか確認する
        Assert.Contains("50.0%", html);
        // 個別スクリプトの行数が含まれているか確認する（集計されていないことを確認）
        Assert.Contains("60.0%", html); // a.js = 3/5 = 60.0%
        Assert.Contains("40.0%", html); // b.js = 2/5 = 40.0%
    }

    /// <summary>
    /// Partial（部分実行）行がカバレッジ率計算で 0.5 行換算されることを確認する。
    /// 変更前の計算式: (covered + partial) / total × 100
    /// 変更後の計算式: (covered + partial × 0.5) / total × 100
    /// covered=1, partial=2, total=4 の場合:
    ///   変更前: (1 + 2) / 4 × 100 = 75.0%
    ///   変更後: (1 + 2×0.5) / 4 × 100 = 50.0%
    /// </summary>
    [Fact]
    public void BuildIndexPage_PartialCoverage_UsesHalfWeightInRate()
    {
        // covered=1, partial=2, uncovered=1（total=4）
        var rows = new List<(
            IReadOnlyList<(string pageUrl, string tabFilename)> pages,
            string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            (
                [("http://example.com/", "script-0.html")],
                "http://example.com/app.js",
                1,  // screenCount
                1,  // covered
                2,  // partial
                4,  // total
                "script-0.html"
            )
        };

        var html = HtmlReportGenerator.BuildIndexPage(rows);

        // 0.5換算: (1 + 2×0.5) / 4 × 100 = 50.0%
        Assert.Contains("50.0%", html);
        // 変更前の計算式（75.0%）が出ていないことを確認する
        Assert.DoesNotContain("75.0%", html);
    }

    // -----------------------------------------------------------------------
    // BuildLines — 全文字が NUL の行は Neutral になるテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 行の全文字が NUL（\0）の場合、NUL はカバレッジカウントから除外されるため
    /// coveredCount・uncoveredCount が両方 0 になり、Neutral ステータスになることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_AllNulLine_NeutralStatus()
    {
        // "\0\0" — 2文字とも NUL で、どちらも count=0（未実行）として設定する
        // NUL はカウントから除外されるため uncoveredCount は 0 のまま → Neutral
        const string source = "\0\0";
        var map = new int[] { 0, 0 };

        var lines = HtmlReportGenerator.BuildLines(source, map);

        // 1行が生成されているか確認する
        Assert.Single(lines);
        // NUL 文字のみの行は Neutral になっているか確認する
        Assert.Equal(LineCoverageStatus.Neutral, lines[0].Status);
    }

    // -----------------------------------------------------------------------
    // GetFileName — エッジケーステスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// クエリ文字列を含む URL でクエリ部分を除去したファイル名を返すことを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_UrlWithQueryString_ReturnsFileNameWithoutQuery()
    {
        var result = HtmlReportGenerator.GetFileName("http://example.com/app.js?v=1.2.3");
        Assert.Equal("app.js", result);
    }

    /// <summary>
    /// クエリ文字列だけの URL（パスなし）でホスト名を返すことを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_UrlWithQueryStringOnly_ReturnsHostname()
    {
        var result = HtmlReportGenerator.GetFileName("http://example.com/?q=1");
        Assert.False(string.IsNullOrEmpty(result));
        Assert.Equal("example.com", result);
    }

    /// <summary>
    /// パーセントエンコードされたスペース（%20）を含む URL でデコードされたファイル名を返すことを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_UrlWithPercentEncodedSpace_ReturnsDecodedFilename()
    {
        // %20 → スペースにデコードされる
        var result = HtmlReportGenerator.GetFileName("http://example.com/my%20app.js");
        Assert.Equal("my app.js", result);
    }

    /// <summary>
    /// パーセントエンコードされた日本語ファイル名を含む URL でデコードされたファイル名を返すことを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_UrlWithPercentEncodedJapanese_ReturnsDecodedFilename()
    {
        // %E3%82%A2%E3%83%97%E3%83%AA → アプリ
        var result = HtmlReportGenerator.GetFileName("http://example.com/scripts/%E3%82%A2%E3%83%97%E3%83%AA.js");
        Assert.Equal("アプリ.js", result);
    }

    /// <summary>
    /// パーセントエンコードされたプラス記号（%2B）を含む URL でデコードされたファイル名を返すことを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_UrlWithPercentEncodedPlus_ReturnsDecodedFilename()
    {
        // %2B → + にデコードされる
        var result = HtmlReportGenerator.GetFileName("http://example.com/lib%2Butils.js");
        Assert.Equal("lib+utils.js", result);
    }

    // -----------------------------------------------------------------------
    // テストA: 除算演算子 / を含むソースで BuildCoverageMap がクラッシュしないことを確認する
    // SkipWhitespaceAndCommentsBackward の source[k+1] 境界値の煙幕テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 除算演算子（a / b）を含むソースで BuildCoverageMap が例外なく完了することを確認する。
    /// SkipWhitespaceAndCommentsBackward の行コメント検出ロジックで source[k+1] にアクセスする際、
    /// 境界チェックが正しく行われることを検証する煙幕テスト。
    /// </summary>
    [Fact]
    public void BuildMap_SourceWithDivisionOperator_NoException()
    {
        // async アロー関数の本体に除算演算子が含まれる場合
        const string source = "const f = async () => { return a / b; }";
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);
    }

    // -----------------------------------------------------------------------
    // テストF: %25（パーセント記号のエンコード）を含む URL の GetFileName
    // -----------------------------------------------------------------------

    /// <summary>
    /// %25（= '%' のパーセントエンコード）を含む URL の GetFileName を確認する。
    /// Uri.UnescapeDataString は %25 を '%' にデコードするため、
    /// 結果は "app%.js" になることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_UrlWithPercentEncodedPercent_ReturnsDecodedFilename()
    {
        // %25 → '%' にデコードされる（Uri.UnescapeDataString の動作）
        var result = HtmlReportGenerator.GetFileName("http://example.com/app%25.js");
        Assert.Equal("app%.js", result);
    }

    /// <summary>
    /// %25 を含む URL（%25 自体がパーセントエンコードされた場合: %2525）の GetFileName を確認する。
    /// Uri.UnescapeDataString は1回だけデコードするため %2525 → %25 になる（スペースにはならない）。
    /// </summary>
    [Fact]
    public void GetFileName_DoubleEncodedPercent_DecodesOnce()
    {
        // %2525 = '%' + '2' + '5' のエンコード。Uri.UnescapeDataString は一段階だけデコードする
        // %25 → '%' なので %2525 → %25 になる（二重デコードして ' ' にはならない）
        var result = HtmlReportGenerator.GetFileName("http://example.com/app%2525.js");
        Assert.Equal("app%25.js", result);
    }

    /// <summary>
    /// blob: URL は http/https/file/data のいずれでもないため、そのまま返されることを確認する。
    /// ブラウザが生成する blob URL はスクリプト URL として現れる場合がある。
    /// </summary>
    [Fact]
    public void GetFileName_BlobUrl_ReturnsAsIs()
    {
        const string blobUrl = "blob:https://example.com/550e8400-e29b-41d4-a716-446655440000";
        // blob: は http/https/file/data に該当しないためそのまま返す
        Assert.Equal(blobUrl, HtmlReportGenerator.GetFileName(blobUrl));
    }

    /// <summary>
    /// chrome-extension:// URL は http/https/file/data のいずれでもないため、そのまま返されることを確認する。
    /// ブラウザ拡張機能由来のスクリプトはこのスキームで現れる場合がある。
    /// </summary>
    [Fact]
    public void GetFileName_ChromeExtensionUrl_ReturnsAsIs()
    {
        const string extUrl = "chrome-extension://abcdefghijklmnop/content_script.js";
        // chrome-extension:// は http/https/file/data に該当しないためそのまま返す
        Assert.Equal(extUrl, HtmlReportGenerator.GetFileName(extUrl));
    }

    /// <summary>
    /// パスなし URL にクエリ文字列がある場合（スラッシュなし）、クエリを除いたホスト名を返すことを確認する。
    /// 例: http://example.com?q=1 → example.com（クエリ付きではなく）
    /// </summary>
    [Fact]
    public void GetFileName_NoPathUrlWithQuery_ReturnsHostWithoutQuery()
    {
        Assert.Equal("example.com", HtmlReportGenerator.GetFileName("http://example.com?q=1"));
    }

    /// <summary>
    /// パスなし URL にフラグメントがある場合（スラッシュなし）、フラグメントを除いたホスト名を返すことを確認する。
    /// 例: http://example.com#hash → example.com
    /// </summary>
    [Fact]
    public void GetFileName_NoPathUrlWithFragment_ReturnsHostWithoutFragment()
    {
        Assert.Equal("example.com", HtmlReportGenerator.GetFileName("http://example.com#hash"));
    }

    // -----------------------------------------------------------------------
    // テストG: BuildLines に null を渡した場合の挙動
    // -----------------------------------------------------------------------

    /// <summary>
    /// BuildLines に null ソースを渡した場合、空のリストを返して例外が発生しないことを確認する。
    /// null ガードが source == null のときに空リストを返す実装であることを文書化するテスト。
    /// </summary>
    [Fact]
    public void BuildLines_NullSource_ReturnsEmptyList()
    {
        // null ソースを渡した場合、例外ではなく空リストが返るべき
        var lines = HtmlReportGenerator.BuildLines(null, []);
        Assert.Empty(lines);
    }
}

public class BuildLinesEdgeCaseTests
{
    /// <summary>
    /// ソースコードが空文字の場合に BuildLines が空のリストを返すことを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_EmptySource_ReturnsEmptyList()
    {
        var lines = HtmlReportGenerator.BuildLines("", []);

        Assert.Empty(lines);
    }

    /// <summary>
    /// ソースコードが "\r" のみの場合も、空要素除外が正しく機能して
    /// Neutral ステータスの空行1件が返ることを確認する。
    /// （壊れた CRLF 対応）
    /// </summary>
    [Fact]
    public void BuildLines_CarriageReturnOnly_NeutralStatus()
    {
        var lines = HtmlReportGenerator.BuildLines("\r", []);
        Assert.Single(lines);
        Assert.Equal(LineCoverageStatus.Neutral, lines[0].Status);
    }

    /// <summary>
    /// ソースコードが改行文字だけ（"\n"）の場合、
    /// 末尾の空行除外ロジックにより空のリストになることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_OnlyNewline_ReturnsEmptyList()
    {
        // "\n" → Split('\n') = ["", ""] → 末尾の空要素を除外 → [""] → 1行
        // さらにその1行も rawLine = "" で処理される
        var lines = HtmlReportGenerator.BuildLines("\n", []);

        // "\n" は Split で ["", ""] になり、末尾の空行を除いて1行（"" の行）
        // 実質空行が1行あることになる
        Assert.Single(lines);
        Assert.Equal(LineCoverageStatus.Neutral, lines[0].Status);
    }

    /// <summary>
    /// map の長さがソース文字列より長い場合、余剰要素が安全に無視されることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_MapLongerThanSource_ExtraElementsIgnored()
    {
        const string source = "ab";      // 2文字
        var map = new int[] { 1, 0, 1, 1 }; // 4要素（ソースより2つ多い）

        var lines = HtmlReportGenerator.BuildLines(source, map);

        Assert.Single(lines);
        // 'a'=covered, 'b'=uncovered → Partial
        Assert.Equal(LineCoverageStatus.Partial, lines[0].Status);
    }

    /// <summary>
    /// CRLF 改行を含む複数行のソースで、2行目以降のカバレッジインデックスが
    /// 正確に計算されることを確認する。
    /// \r を含む rawLine の Length が offset 計算に使われるため、
    /// 2行目の文字が正しい map インデックスで参照されることを保証する。
    /// </summary>
    [Fact]
    public void BuildLines_CrlfMultipleLines_SecondLineCoverageCorrect()
    {
        // "a\r\nfoo\r\n" — CRLF 改行の2行
        // source インデックス: a=0, \r=1, \n=2(分割), f=3, o=4, o=5, \r=6, \n=7(末尾)
        // map: a=1(covered), \r=1(skip), f=0, o=0, o=0, \r=0(skip)
        const string source = "a\r\nfoo\r\n";
        var map = new int[] { 1, 1, -1, 0, 0, 0, 0, -1 };

        var lines = HtmlReportGenerator.BuildLines(source, map);

        // 2行に分割されているか確認する（末尾の改行で余分な行が増えないこと）
        Assert.Equal(2, lines.Count);
        // 1行目（"a"）は実行済み（covered）になっているか確認する
        Assert.Equal(LineCoverageStatus.Covered, lines[0].Status);
        // 2行目（"foo"）は未実行（uncovered）になっているか確認する
        // これが Covered になるなら offset ズレのバグがある
        Assert.Equal(LineCoverageStatus.Uncovered, lines[1].Status);
    }

    // -----------------------------------------------------------------------
    // BuildLines — 境界値テスト
    // -----------------------------------------------------------------------

    /// <summary>行中の NUL 文字（\0）は HTML に出力されないが後続文字の offset は正しいままであることを確認する。</summary>
    [Fact]
    public void BuildLines_NullCharInLine_NotRenderedButOffsetCorrect()
    {
        // source = "ab\0cd", map は全文字 covered
        const string source = "ab\0cd";
        var map = new int[] { 1, 1, 1, 1, 1 };

        var lines = HtmlReportGenerator.BuildLines(source, map);

        Assert.Single(lines);
        // NUL が含まれていないこと（string.Contains(char) は ordinal 比較を使用する）
        Assert.False(lines[0].Html.Contains('\0'), "HTML に NUL 文字が含まれてはならない");
        // 'a','b','c','d' は含まれていること
        Assert.Contains("a", lines[0].Html);
        Assert.Contains("d", lines[0].Html);
        Assert.Equal(LineCoverageStatus.Covered, lines[0].Status);
    }

    // -----------------------------------------------------------------------
    // HtmlEncode — 単一引用符は変換しないことを明示する
    // -----------------------------------------------------------------------

    /// <summary>
    /// HtmlEncode は HTML コンテンツ用なので単一引用符（'）はエスケープしない。
    /// HTML 属性値でない限り ' をエスケープする必要はなく、現在の実装はこれが意図的動作。
    /// BuildLines でも ' はそのまま出力されており、HtmlEncode と挙動が一致していることを示す。
    /// </summary>
    [Fact]
    public void HtmlEncode_SingleQuote_NotEscaped()
    {
        Assert.Equal("it's", HtmlReportGenerator.HtmlEncode("it's"));
    }

    /// <summary>
    /// HtmlEncode でタブ文字（\t）と改行文字（\n）はエスケープされないことを確認する。
    /// これらはHTML特殊文字ではないため変換対象外。
    /// </summary>
    [Fact]
    public void HtmlEncode_TabAndNewline_NotEscaped()
    {
        // タブと改行はそのまま出力される
        Assert.Equal("\t\n", HtmlReportGenerator.HtmlEncode("\t\n"));
    }

    /// <summary>
    /// HtmlEncode で絵文字（サロゲートペア文字）がそのまま出力されることを確認する。
    /// U+1F600 (😀) は .NET で 2 つの char（サロゲートペア）で表現されるが、
    /// HtmlEncode は char 単位でループするため両方そのまま通過する。
    /// </summary>
    [Fact]
    public void HtmlEncode_Emoji_PassedThrough()
    {
        Assert.Equal("😀", HtmlReportGenerator.HtmlEncode("😀"));
    }

    /// <summary>
    /// BuildLines で ' （シングルクォート）がエスケープされずそのまま出力されることを確認する。
    /// テキストノード内では ' のエスケープは不要であり、HtmlEncode と挙動が一致している。
    /// </summary>
    [Fact]
    public void BuildLines_SingleQuoteInSource_NotEscaped()
    {
        // map: 全文字 covered（1）
        var lines = HtmlReportGenerator.BuildLines("it's", [1, 1, 1, 1]);
        Assert.Single(lines);
        // ' がそのまま含まれていること（&#39; にエスケープされていないこと）
        Assert.Contains("it", lines[0].Html);
        Assert.DoesNotContain("&#39;", lines[0].Html);
    }

    // -----------------------------------------------------------------------
    // GetFileName — 境界値テスト
    // -----------------------------------------------------------------------

    /// <summary>ルートパス（"/"）のみの URL はホスト名を返す。</summary>
    [Fact]
    public void GetFileName_RootPathOnly_ReturnsHostname()
    {
        Assert.Equal("localhost", HtmlReportGenerator.GetFileName("http://localhost/"));
    }

    /// <summary>javascript: スキームは HTTP/HTTPS/FILE でないためそのまま返す。</summary>
    [Fact]
    public void GetFileName_JavaScriptScheme_ReturnsAsIs()
    {
        Assert.Equal("javascript:alert(1)", HtmlReportGenerator.GetFileName("javascript:alert(1)"));
    }

    /// <summary>ws:// スキームはそのまま返す。</summary>
    [Fact]
    public void GetFileName_WsScheme_ReturnsAsIs()
    {
        Assert.Equal("ws://localhost/app.js", HtmlReportGenerator.GetFileName("ws://localhost/app.js"));
    }

    /// <summary>フラグメント（# 以降）は除去されてファイル名のみ返す。</summary>
    [Fact]
    public void GetFileName_UrlWithFragment_FragmentStripped()
    {
        Assert.Equal("app.js", HtmlReportGenerator.GetFileName("http://localhost/app.js#section"));
    }

    /// <summary>
    /// U+2028（Unicode 行区切り文字）は SplitOnNewlines の対象外のため1行として扱われる。
    /// この制限を文書化するテスト（ECMAScript では行末文字だが C# の BuildLines では改行扱いしない）。
    /// </summary>
    [Fact]
    public void BuildLines_LineSeparatorU2028_TreatedAsSingleLine()
    {
        // U+2028 は SplitOnNewlines (\n / \r\n / \r のみ対応) で分割されないため
        // 全体が1行として扱われる
        string source = "a" + "\u2028" + "b";
        int[] map = new int[] { 1, -1, 1 };

        var lines = HtmlReportGenerator.BuildLines(source, map);

        // U+2028 で分割されず1行として返されることを確認する
        Assert.Single(lines);
    }
}

/// <summary>
/// HtmlReportGenerator.Generate メソッドの統合テスト群。
/// 実際にファイルを生成し、出力ファイルの存在と内容を確認する。
/// </summary>
public class GenerateTests : IDisposable
{
    // テスト出力用の一時ディレクトリ（テスト終了後に削除する）
    private readonly string _outputDir;

    public GenerateTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), "GenerateTests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
        {
            Directory.Delete(_outputDir, recursive: true);
        }
    }

    // カバレッジデータなしのシンプルな ScriptCoverage を作るヘルパー
    private static JsCoverageReporter.Coverage.ScriptCoverage MakeScript(
        int tabIndex, string pageUrl, string scriptUrl, string source,
        int covered = 0)
    {
        var ranges = new List<JsCoverageReporter.Coverage.CoverageRange>
        {
            new(0, source.Length, covered),
        };
        var functions = new List<JsCoverageReporter.Coverage.FunctionCoverage>
        {
            new("main", ranges),
        };
        var page = new JsCoverageReporter.Coverage.PageInfo(tabIndex, pageUrl);
        return new JsCoverageReporter.Coverage.ScriptCoverage(page, scriptUrl, source, functions);
    }

    /// <summary>
    /// カバレッジデータが空の場合でも index.html が生成されることを確認する。
    /// </summary>
    [Fact]
    public void Generate_EmptyCoverages_CreatesIndexHtml()
    {
        var generator = new HtmlReportGenerator();

        generator.Generate([], _outputDir);

        // index.html が生成されているか確認する
        string indexPath = Path.Combine(_outputDir, "index.html");
        Assert.True(File.Exists(indexPath), "index.html が生成されていない");

        // index.html の内容に「全体カバレッジ」という文字列が含まれているか確認する
        string content = File.ReadAllText(indexPath);
        Assert.Contains("全体カバレッジ", content);
    }

    /// <summary>
    /// 出力先ディレクトリが既に存在してもエラーにならず上書き（新規作成）されることを確認する。
    /// Directory.CreateDirectory は既存ディレクトリでも安全だが、テストで明示的に動作を保証する。
    /// </summary>
    [Fact]
    public void Generate_OutputDirExists_OverwritesWithoutError()
    {
        // 事前に出力先ディレクトリとダミーファイルを作成する
        Directory.CreateDirectory(_outputDir);
        File.WriteAllText(Path.Combine(_outputDir, "dummy.txt"), "dummy");

        var generator = new HtmlReportGenerator();
        // 例外が発生しないことを確認する
        var ex = Record.Exception(() => generator.Generate([], _outputDir));
        Assert.Null(ex);

        // index.html が正常に生成されていること
        Assert.True(File.Exists(Path.Combine(_outputDir, "index.html")));
    }

    /// <summary>
    /// スクリプト1件の場合、index.html と scripts/script-0.html が生成されることを確認する。
    /// </summary>
    [Fact]
    public void Generate_SingleScript_CreatesIndexAndScriptFile()
    {
        var script = MakeScript(0, "http://example.com/", "http://example.com/app.js",
            "function foo() {\n  return 1;\n}", covered: 1);

        var generator = new HtmlReportGenerator();
        generator.Generate([script], _outputDir);

        // index.html が生成されているか確認する
        Assert.True(File.Exists(Path.Combine(_outputDir, "index.html")));
        // scripts/script-0.html が生成されているか確認する
        Assert.True(File.Exists(Path.Combine(_outputDir, "scripts", "script-0.html")));

        // index.html に app.js へのリンクが含まれているか確認する
        string indexContent = File.ReadAllText(Path.Combine(_outputDir, "index.html"));
        Assert.Contains("app.js", indexContent);
        Assert.Contains("script-0.html", indexContent);
    }

    /// <summary>
    /// スクリプトが複数件ある場合、件数分のスクリプトファイルが生成されることを確認する。
    /// </summary>
    [Fact]
    public void Generate_MultipleScripts_CreatesMultipleScriptFiles()
    {
        var scripts = new[]
        {
            MakeScript(0, "http://example.com/", "http://example.com/app.js",   "var a = 1;\nvar aa = 2;", covered: 1),
            MakeScript(0, "http://example.com/", "http://example.com/util.js",  "var b = 1;\nvar bb = 2;", covered: 0),
            MakeScript(0, "http://example.com/", "http://example.com/extra.js", "var c = 1;\nvar cc = 2;", covered: 1),
        };

        var generator = new HtmlReportGenerator();
        generator.Generate(scripts, _outputDir);

        // 3件分のスクリプトファイルが生成されているか確認する
        string scriptsDir = Path.Combine(_outputDir, "scripts");
        Assert.True(File.Exists(Path.Combine(scriptsDir, "script-0.html")));
        Assert.True(File.Exists(Path.Combine(scriptsDir, "script-1.html")));
        Assert.True(File.Exists(Path.Combine(scriptsDir, "script-2.html")));

        // index.html に 3件分のスクリプト名が含まれているか確認する
        string indexContent = File.ReadAllText(Path.Combine(_outputDir, "index.html"));
        Assert.Contains("app.js",   indexContent);
        Assert.Contains("util.js",  indexContent);
        Assert.Contains("extra.js", indexContent);
    }

    /// <summary>
    /// 同一 URL のスクリプトが複数タブから収集された場合、合成ページとタブ別ページの
    /// 両方が生成されることを確認する。
    /// </summary>
    [Fact]
    public void Generate_SameScriptFromMultipleTabs_CreatesMergedAndTabFiles()
    {
        // 同じ URL "http://example.com/shared.js" が tab0 と tab1 から収集された
        var tab0 = MakeScript(0, "http://example.com/page1", "http://example.com/shared.js",
            "function shared() {\n  return 1;\n}", covered: 1);
        var tab1 = MakeScript(1, "http://example.com/page2", "http://example.com/shared.js",
            "function shared() {\n  return 1;\n}", covered: 0);

        var generator = new HtmlReportGenerator();
        generator.Generate([tab0, tab1], _outputDir);

        string scriptsDir = Path.Combine(_outputDir, "scripts");

        // 合成ページ（script-0.html）が生成されているか確認する
        Assert.True(File.Exists(Path.Combine(scriptsDir, "script-0.html")),
            "合成ページ script-0.html が生成されていない");

        // タブ別ページ（script-0-tab0.html, script-0-tab1.html）が生成されているか確認する
        Assert.True(File.Exists(Path.Combine(scriptsDir, "script-0-tab0.html")),
            "タブ別ページ script-0-tab0.html が生成されていない");
        Assert.True(File.Exists(Path.Combine(scriptsDir, "script-0-tab1.html")),
            "タブ別ページ script-0-tab1.html が生成されていない");

        // index.html に「複数ページ」の表示が含まれているか確認する
        string indexContent = File.ReadAllText(Path.Combine(_outputDir, "index.html"));
        Assert.Contains("複数ページ", indexContent);
    }

    /// <summary>
    /// スクリプトページに URL 内の HTML 特殊文字が正しくエスケープされて出力されることを確認する。
    /// </summary>
    [Fact]
    public void Generate_ScriptUrlWithHtmlChars_EscapedInOutput()
    {
        // XSS のリスクがある URL: スクリプト URL に < > & が含まれる
        // GetFileName は http:// プレフィックスなしの URL をそのまま返すため、
        // エスケープが必要
        var script = MakeScript(0, "http://example.com/", "http://example.com/a&b.js",
            "var x = 1;", covered: 1);

        var generator = new HtmlReportGenerator();
        generator.Generate([script], _outputDir);

        string indexContent = File.ReadAllText(Path.Combine(_outputDir, "index.html"));
        // 生の & が HTML に残っていないこと（&amp; にエスケープされていること）
        // ただし &amp; 等のエンティティの & は除く
        string withoutEntities = indexContent
            .Replace("&amp;", "ENCODED_AMP")
            .Replace("&lt;", "ENCODED_LT")
            .Replace("&gt;", "ENCODED_GT")
            .Replace("&quot;", "ENCODED_QUOT");
        Assert.DoesNotContain("&", withoutEntities);
    }

    /// <summary>
    /// カバレッジ率が正しく index.html に表示されることを確認する。
    /// </summary>
    [Fact]
    public void Generate_CoveragePercentageDisplayed_InIndexHtml()
    {
        // source = "abcde" (5文字), 全行 covered になるようなカバレッジデータ
        var script = MakeScript(0, "http://example.com/", "http://example.com/app.js",
            "abcde", covered: 1);

        var generator = new HtmlReportGenerator();
        generator.Generate([script], _outputDir);

        string indexContent = File.ReadAllText(Path.Combine(_outputDir, "index.html"));
        // 何らかのパーセント表示が含まれているか確認する
        Assert.Contains("%", indexContent);
    }

    // -----------------------------------------------------------------------
    // Bug3: 同一 Page.Index が同一グループに2回現れるとき tabFilename が衝突しないこと
    // -----------------------------------------------------------------------

    /// <summary>
    /// 同じタブ（Page.Index=0）が同じスクリプトを異なるページ URL で2回収集した場合
    /// （ナビゲーション後に同じスクリプトが再読み込みされたケース）、
    /// タブ別ページが衝突せず2ファイル生成されることを確認する。
    /// </summary>
    [Fact]
    public void Generate_SamePageIndexTwiceInGroup_TwoTabFilesCreated()
    {
        var source = "function foo() {\n  return 1;\n}";
        // 同一 tabIndex=0、異なる pageUrl → 同一グループ (url, source) に2件
        var script1 = MakeScript(0, "http://example.com/page1", "http://example.com/app.js", source);
        var script2 = MakeScript(0, "http://example.com/page2", "http://example.com/app.js", source);

        var generator = new HtmlReportGenerator();
        generator.Generate([script1, script2], _outputDir);

        var scriptsDir = Path.Combine(_outputDir, "scripts");
        // script-0-tab0.html と script-0-tab1.html の2ファイルが生成されていること
        var tabFiles = Directory.GetFiles(scriptsDir, "script-0-tab*.html");
        Assert.Equal(2, tabFiles.Length);
    }

    // -----------------------------------------------------------------------
    // テストI: 同一 URL・異なるソースのスクリプトは別グループになる
    // -----------------------------------------------------------------------

    /// <summary>
    /// 同じ URL だがソースコードが異なる2件のスクリプト（例: ホットリロードで別バージョンが来た場合）は
    /// 別グループとして扱われ、2つの別ファイル（script-0.html, script-1.html）が生成されることを確認する。
    /// </summary>
    [Fact]
    public void Generate_SameUrlDifferentSource_CreatesSeperateScriptFiles()
    {
        // 同じ URL だが Source が異なる（v1 と v2）
        var script1 = MakeScript(0, "http://example.com/", "http://example.com/app.js",
            "var version = 1;\nvar extra = 1;", covered: 1);
        var script2 = MakeScript(0, "http://example.com/", "http://example.com/app.js",
            "var version = 2;\nvar extra = 2;", covered: 1);

        var generator = new HtmlReportGenerator();
        generator.Generate([script1, script2], _outputDir);

        string scriptsDir = Path.Combine(_outputDir, "scripts");

        // URL が同じでも Source が違うため別グループになり、2ファイルが生成されるべき
        Assert.True(File.Exists(Path.Combine(scriptsDir, "script-0.html")),
            "script-0.html が生成されていない");
        Assert.True(File.Exists(Path.Combine(scriptsDir, "script-1.html")),
            "script-1.html が生成されていない（同一 URL・異なるソースは別グループになるべき）");
    }

    // -----------------------------------------------------------------------
    // GetFileName — 追加エッジケーステスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// URL にユーザー名:パスワード@ホスト の形式で認証情報が含まれる場合、
    /// パスのファイル名部分（最後のセグメント）が返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_UrlWithAuthentication_ReturnsFilename()
    {
        // http://user:pass@host/scripts/app.js → pathStart は "/scripts/app.js" の '/' の位置
        Assert.Equal("app.js", HtmlReportGenerator.GetFileName("http://user:pass@host/scripts/app.js"));
    }

    /// <summary>
    /// scriptUrl が空文字の場合、BuildScriptPage が例外なく動作し
    /// GetFileName("") = "" がそのまま h1 要素に含まれることを確認する。
    /// </summary>
    [Fact]
    public void BuildScriptPage_EmptyScriptUrl_NoException()
    {
        var lines = new List<LineData>();
        // 例外が発生しないことを確認する
        var html = HtmlReportGenerator.BuildScriptPage([], "", lines);
        // h1 タグが生成されており、空文字が混入してもクラッシュしないこと
        Assert.Contains("<h1>", html);
    }

    // -----------------------------------------------------------------------
    // GetFileName — IPv6 アドレス
    // -----------------------------------------------------------------------

    /// <summary>
    /// IPv6 アドレスを含む URL（http://[::1]:3000/app.js）からファイル名が正しく取得できることを確認する。
    /// [::1] 内の ':' や ']' は schemeLength 以降で IndexOf('/') が最初に見つける '/' より前にあるため
    /// pathStart が正しく計算され、GetFileName が "app.js" を返す。
    /// </summary>
    [Fact]
    public void GetFileName_IPv6Url_ReturnsFilename()
    {
        // http://[::1]:3000/app.js → pathStart は '/' の位置 → rawName = "app.js"
        Assert.Equal("app.js", HtmlReportGenerator.GetFileName("http://[::1]:3000/app.js"));
    }

    // -----------------------------------------------------------------------
    // BuildIndexPage — カバレッジ率ゼロ除算パス
    // -----------------------------------------------------------------------

    /// <summary>
    /// 全行が Neutral（対象外）で対象行数 = 0 のスクリプトのみの場合、
    /// カバレッジ率が 0.0% と表示され、全体集計でもゼロ除算が発生しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildIndexPage_AllNeutralRows_ShowsZeroPercentWithoutException()
    {
        // covered=0, partial=0, total=0 のスクリプト → pct=0（ゼロ除算防止パスを通る）
        var rows = new List<(
            IReadOnlyList<(string pageUrl, string tabFilename)> pages,
            string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            (
                new List<(string, string)> { ("http://example.com/", "script-0.html") },
                "http://example.com/app.js",
                1, 0, 0, 0,
                "script-0.html"
            ),
        };

        string html = HtmlReportGenerator.BuildIndexPage(rows);

        // 0.0% が表示されること（ゼロ除算で例外が起きないこと）
        Assert.Contains("0.0%", html);
        // 対象行数 0 であること
        Assert.Contains("<td class=\"num\">0</td>", html);
    }

    /// <summary>
    /// GetFileName に null を渡した場合、例外なく空文字が返されることを確認する。
    /// #nullable disable 環境で null が渡された場合の安全性を検証する。
    /// </summary>
    [Fact]
    public void GetFileName_NullUrl_ReturnsEmpty()
    {
        // null を渡しても NullReferenceException が出ず空文字が返ること
        var result = HtmlReportGenerator.GetFileName(null);
        Assert.Equal("", result);
    }

    /// <summary>
    /// 全体カバレッジでも対象行数が 0 の場合（全スクリプト Neutral）、
    /// 全体カバレッジ率が 0.0% と表示されることを確認する。
    /// </summary>
    [Fact]
    public void BuildIndexPage_OverallAllNeutral_ShowsZeroOverallPercent()
    {
        // 全スクリプト total=0 → overallPct = 0（全体集計のゼロ除算防止パスを通る）
        var rows = new List<(
            IReadOnlyList<(string pageUrl, string tabFilename)> pages,
            string url, int screenCount, int covered, int partial, int total, string mergedFilename)>();

        string html = HtmlReportGenerator.BuildIndexPage(rows);

        // 全体カバレッジ 0.0% が表示されること
        Assert.Contains("0.0%", html);
    }

    /// <summary>
    /// 不正なパーセントシーケンス（%GG など）を含む URL を渡しても例外が発生しないことを確認する。
    /// Uri.UnescapeDataString が例外を投げた場合でも try-catch で保護されていることを検証する。
    /// </summary>
    [Fact]
    public void GetFileName_InvalidPercentSequence_DoesNotThrow()
    {
        // %GG は不正なパーセントシーケンス → .NET によっては UriFormatException を投げる可能性がある
        var ex = Record.Exception(() => HtmlReportGenerator.GetFileName("http://localhost/foo%GG.js"));
        Assert.Null(ex);
    }

    /// <summary>
    /// 末尾が % だけで終わる不正な URL を渡しても例外が発生しないことを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_TrailingPercent_DoesNotThrow()
    {
        // 末尾 % は不完全なパーセントシーケンス → 例外なく何らかの文字列を返すこと
        var ex = Record.Exception(() => HtmlReportGenerator.GetFileName("http://localhost/app%"));
        Assert.Null(ex);
    }

    /// <summary>
    /// パス部分がない認証情報付き URL でホスト名のみを返す（パスワードを含まない）。
    /// </summary>
    [Fact]
    public void GetFileName_AuthCredentialsNoPath_ReturnsHostOnly()
    {
        // http://user:pass@host はパス部分がないため hostOnly 分岐に入る
        // Bug: @ 以前の認証情報（user:pass）がそのまま返されていた
        Assert.Equal("host",        HtmlReportGenerator.GetFileName("http://user:pass@host"));
        Assert.Equal("example.com", HtmlReportGenerator.GetFileName("http://user@example.com"));
    }

    /// <summary>
    /// URL に複数のパスセグメントがある場合、最後のセグメント（ファイル名）が返されることを確認する。
    /// パス /v1/2/3/app.js → LastIndexOf('/') によって app.js が取得される。
    /// </summary>
    [Fact]
    public void GetFileName_UrlWithMultiplePathSegments_ReturnsLastSegment()
    {
        Assert.Equal("app.js", HtmlReportGenerator.GetFileName("https://cdn.example.com/v1/2/3/app.js"));
        Assert.Equal("main.js", HtmlReportGenerator.GetFileName("http://localhost:3000/assets/js/dist/main.js"));
    }

    /// <summary>
    /// URL に制御文字（\0 や \n など ASCII 0x00-0x1F）が含まれる場合でも
    /// GetFileName が例外を投げずに処理できることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_ControlCharactersInUrl_NoException()
    {
        // 制御文字を含む不正な URL でも例外が発生しないことを確認する
        var ex1 = Record.Exception(() => HtmlReportGenerator.GetFileName("https://example.com/path\0/file.js"));
        Assert.Null(ex1);
        var ex2 = Record.Exception(() => HtmlReportGenerator.GetFileName("https://example.com/path\n/file.js"));
        Assert.Null(ex2);
        var ex3 = Record.Exception(() => HtmlReportGenerator.GetFileName("https://example.com/path\t/file.js"));
        Assert.Null(ex3);
    }

    /// <summary>
    /// XSS 対策: &lt;script&gt; タグが HTML エンコードされることを確認する。
    /// ファイル名や URL に悪意あるスクリプトが含まれる場合に HTML 出力が安全であることを保証する。
    /// </summary>
    [Fact]
    public void HtmlEncode_ScriptTag_EscapedForXss()
    {
        const string input    = "<script>alert('XSS')</script>";
        const string expected = "&lt;script&gt;alert('XSS')&lt;/script&gt;";
        Assert.Equal(expected, HtmlReportGenerator.HtmlEncode(input));
    }

    /// <summary>
    /// 認証情報（user:pass@）がホスト部に含まれ、かつパスが存在する URL では、
    /// パス抽出の段階で認証情報は自然に除かれてファイル名のみが返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_UrlWithCredentialsAndPath_ReturnsFilenameOnly()
    {
        // pathStart は "https://" 以降の最初の '/' なので user:pass@ を含まない
        Assert.Equal("app.js", HtmlReportGenerator.GetFileName("https://user:pass@example.com/js/app.js"));
        Assert.Equal("app.js", HtmlReportGenerator.GetFileName("http://user@localhost:3000/app.js"));
    }

    /// <summary>
    /// 末尾改行なしのソース（最後の文字がファイル末尾）を渡した場合、
    /// オフセット計算が正しく動作して最後の文字のカバレッジが反映されることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_SourceWithNoTrailingNewline_LastCharCovered()
    {
        // "ab" — 末尾に \n がない2文字。b（index=1）が実行済みにマークされること
        const string source = "ab";
        var map = new int[] { 1, 1 };
        var lines = HtmlReportGenerator.BuildLines(source, map);
        // 1行だけ生成されているか確認する
        Assert.Single(lines);
        // 最終文字 'b' を含む行が Covered になっているか確認する
        Assert.Equal(LineCoverageStatus.Covered, lines[0].Status);
        Assert.Contains("b", lines[0].Html);
    }

    /// <summary>
    /// Generate で1行スクリプト（スキップ対象）の後に複数行スクリプトが続く場合、
    /// スキップされたスクリプトで番号がずれず、次のスクリプトが script-0.html になることを確認する。
    /// 修正前は1行スクリプトのスキップ時に i++ していたため script-1.html が生成されていた。
    /// </summary>
    [Fact]
    public void Generate_SingleLineScriptSkipped_NextScriptStartsAtIndex0()
    {
        // 1行スクリプト（スキップ対象）と複数行スクリプトの2件を渡す
        var singleLine = MakeScript(0, "http://example.com/", "http://example.com/inline.js",
            "var x = 1;", covered: 0); // 改行なし → 1行 → スキップされる
        var multiLine  = MakeScript(0, "http://example.com/", "http://example.com/app.js",
            "var a = 1;\nvar b = 2;", covered: 1); // 2行 → スキップされない

        var generator = new HtmlReportGenerator();
        generator.Generate([singleLine, multiLine], _outputDir);

        string scriptsDir = Path.Combine(_outputDir, "scripts");
        // 複数行スクリプトは script-0.html（番号ずれなし）として生成されること
        Assert.True(File.Exists(Path.Combine(scriptsDir, "script-0.html")),
            "script-0.html が生成されていない（1行スキップで番号がずれていた可能性）");
        // script-1.html は生成されないこと（スキップで余分な番号を消費していた場合に生成される）
        Assert.False(File.Exists(Path.Combine(scriptsDir, "script-1.html")),
            "script-1.html が不正に生成されている（番号がずれていた）");
    }

    /// <summary>
    /// 1行スクリプトがスキップされる際に、警告メッセージが標準エラーに出力されることを確認する（M5 修正）。
    /// 修正前はサイレントスキップで、ユーザーがスクリプトが表示されない理由を調べられなかった。
    /// </summary>
    [Fact]
    public void Generate_SingleLineScript_WarningEmittedToStderr()
    {
        var singleLine = MakeScript(0, "http://example.com/", "http://example.com/inline.js",
            "var x = 1;", covered: 0); // 改行なし → 1行 → スキップされる

        var captured = new System.IO.StringWriter();
        var originalErr = Console.Error;
        Console.SetError(captured);
        try
        {
            new HtmlReportGenerator().Generate([singleLine], _outputDir);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        // スキップされた旨の警告が stderr に出力されること
        string output = captured.ToString();
        Assert.Contains("[Warning]", output);
        Assert.Contains("inline.js", output);
    }
}

// -----------------------------------------------------------------------
// コードレビュー指摘対応: BuildScriptPage 追加テストケース
// -----------------------------------------------------------------------

/// <summary>
/// コードレビューで指摘された BuildScriptPage の追加テストケース。
/// </summary>
public class BuildScriptPageReviewTests
{
    /// <summary>
    /// pageInfos が空リストの場合、BuildScriptPage が "(不明)" フォールバックを返すことを確認する。
    /// pageInfos.Count == 0 のコードパスが正しく動作することを検証する。
    /// </summary>
    [Fact]
    public void BuildScriptPage_EmptyPageInfosList_ShowsFallback()
    {
        // pageInfos が空 → "(不明)" が表示されるはず
        var html = HtmlReportGenerator.BuildScriptPage([], "app.js", []);

        // "(不明)" フォールバック文字列が含まれているか確認する
        Assert.Contains("(不明)", html);
        // 有効な HTML が返っているか確認する
        Assert.Contains("<html", html);
    }
}

// -----------------------------------------------------------------------
// コードレビュー指摘対応: GenerateTests の追加テストケース（BOM・XSS）
// -----------------------------------------------------------------------

/// <summary>
/// コードレビューで指摘された Generate の追加テストケース。
/// </summary>
public class GenerateReviewTests : IDisposable
{
    private readonly string _outputDir;

    public GenerateReviewTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), "GenerateReviewTests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
        {
            Directory.Delete(_outputDir, recursive: true);
        }
    }

    private static JsCoverageReporter.Coverage.ScriptCoverage MakeScriptWithRanges(
        int tabIndex, string pageUrl, string scriptUrl, string source,
        IReadOnlyList<JsCoverageReporter.Coverage.CoverageRange> ranges)
    {
        var functions = new List<JsCoverageReporter.Coverage.FunctionCoverage>
        {
            new("main", ranges),
        };
        var page = new JsCoverageReporter.Coverage.PageInfo(tabIndex, pageUrl);
        return new JsCoverageReporter.Coverage.ScriptCoverage(page, scriptUrl, source, functions);
    }

    /// <summary>
    /// BOM（バイトオーダーマーク U+FEFF）付きのソースを Generate に渡した場合、
    /// V8 のオフセット（BOM を文字としてカウントしない）と正しく一致することを確認する。
    /// BUG: BOM を除去せずに BuildCoverageMap に渡すと、オフセットが1ずれて末尾文字が中立になる。
    /// FIX: Generate で canonical.Source.TrimStart('\uFEFF') してから BuildCoverageMap を呼ぶ。
    /// </summary>
    [Fact]
    public void Generate_BomPrefixedSource_CoverageAlignedToContent()
    {
        // BOM + "var x;" = 7文字（BOM=1 + jsContent=6）
        // V8 は BOM を文字としてカウントしないため、range(0, 6, 1) は jsContent の全6文字を covered にする
        const string jsContent = "var x;\nvar y;";
        string source = "\uFEFF" + jsContent; // BOM + jsContent

        // V8 スタイルのカバレッジ: BOM を除いた 0〜5 (全jsContent) を covered
        var ranges = new List<JsCoverageReporter.Coverage.CoverageRange>
        {
            new(0, jsContent.Length, 1), // offset 0〜5 → jsContent の全文字
        };

        var script = MakeScriptWithRanges(
            0, "http://example.com/", "http://example.com/app.js", source, ranges);

        var generator = new HtmlReportGenerator();
        generator.Generate([script], _outputDir);

        string scriptContent = File.ReadAllText(
            Path.Combine(_outputDir, "scripts", "script-0.html"));

        // BOM が正しく除去されていれば全文字 covered → neutral span は現れないはず
        // BOM 未除去だと ';'（最後の文字）が V8 のオフセット範囲外になって neutral になる
        Assert.DoesNotContain("class=\"neutral\"", scriptContent);

        // covered span が存在すること（ソースコードが正しく処理されていることを確認する）
        Assert.Contains("class=\"covered\"", scriptContent);
    }

    /// <summary>
    /// ページ URL（スクリプト詳細ページの見出しに表示される URL）に
    /// XSS ペイロードが含まれる場合、HTML エンコードされることを確認する。
    /// BuildScriptPage は pageInfos[0].url を HtmlEncode するため安全なはず。
    /// </summary>
    [Fact]
    public void Generate_XssInPageUrl_EscapedInScriptPage()
    {
        // ページ URL に </script><svg onload=alert(1)> を埋め込む
        const string xssPageUrl = "http://evil.com/</script><svg onload=alert(1)>";
        var ranges = new List<JsCoverageReporter.Coverage.CoverageRange>
        {
            new(0, 21, 1),
        };
        var script = MakeScriptWithRanges(
            0, xssPageUrl, "http://example.com/app.js", "var x = 1;\nvar y = 2;", ranges);

        var generator = new HtmlReportGenerator();
        generator.Generate([script], _outputDir);

        string scriptContent = File.ReadAllText(
            Path.Combine(_outputDir, "scripts", "script-0.html"));

        // XSS ペイロードの < > が &lt; &gt; にエスケープされているか確認する
        Assert.Contains("&lt;/script&gt;", scriptContent);
        // 生の </script> タグが HTML に残っていないことを確認する（ブラウザでスクリプト実行を防ぐ）
        Assert.DoesNotContain("</script><svg", scriptContent);
    }

    /// <summary>
    /// TC-17: BuildIndexPage の tab pageUrl に "javascript:alert(1)" が含まれる場合、
    /// 表示テキストとして HTML エンコード（または素通り）されるだけで
    /// href 属性に "javascript:" が現れないことを確認する（XSS 防止）。
    /// pageUrl は表示テキストにのみ使われ、href は常に tabFilename 経由のため安全。
    /// </summary>
    [Fact]
    public void BuildIndexPage_JavascriptSchemeInPageUrl_NotUsedAsHref()
    {
        // tab の pageUrl に javascript: スキームを含む文字列を渡す
        var tabs = new List<(string pageUrl, string tabFilename)>
        {
            ("javascript:alert(1)", "script-0.html"),
        };
        var rows = new List<(
            IReadOnlyList<(string pageUrl, string tabFilename)> pages,
            string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            (tabs, "http://example.com/app.js", 1, 5, 0, 10, "script-0.html"),
        };

        string html = HtmlReportGenerator.BuildIndexPage(rows);

        // href 属性に "javascript:" が現れないこと（XSS 防止の核心）
        Assert.DoesNotContain("href=\"javascript:", html);
        // href は tabFilename 経由でのみ設定されること
        Assert.Contains("scripts/script-0.html", html);
    }

    /// <summary>
    /// BOM（U+FEFF）のみからなるソースを Generate に渡した場合、
    /// BOM を除去した後のソースが空文字になっても例外が発生しないことを確認する。
    /// BuildCoverageMap("") は空マップ、BuildLines("", []) は空リストを返すため
    /// BuildScriptPage に空の lines が渡される。
    /// </summary>
    [Fact]
    public void Generate_BomOnlySource_DoesNotThrow()
    {
        // BOM だけからなるソース（除去後は空文字）
        string source = "\uFEFF";
        var ranges = new List<JsCoverageReporter.Coverage.CoverageRange>();
        var script = MakeScriptWithRanges(
            0, "http://example.com/", "http://example.com/app.js", source, ranges);

        var generator = new HtmlReportGenerator();
        var ex = Record.Exception(() => generator.Generate([script], _outputDir));

        // 例外なく完了すること
        Assert.Null(ex);
        // index.html が生成されること
        Assert.True(File.Exists(Path.Combine(_outputDir, "index.html")));
    }

    /// <summary>
    /// BOM のみのソースは mergedLines.Count が 1 以下になるため、
    /// スクリプト個別ページ（script-0.html 等）が生成されないことを確認する。
    /// </summary>
    [Fact]
    public void Generate_BomOnlySource_NoScriptFileCreated()
    {
        string source = "﻿";
        var script = MakeScriptWithRanges(
            0, "http://example.com/", "http://example.com/app.js", source, []);

        new HtmlReportGenerator().Generate([script], _outputDir);

        // スクリプト個別ページは作られないこと（行数 1 以下のスクリプトはスキップされる）
        string scriptsDir = Path.Combine(_outputDir, "scripts");
        bool anyScriptFile = Directory.Exists(scriptsDir) &&
                             Directory.GetFiles(scriptsDir, "script-*.html").Length > 0;
        Assert.False(anyScriptFile);
    }
}

/// <summary>
/// コードレビュー指摘対応: TG-7/TG-11/M-1 の追加テスト。
/// GetFileName・BuildIndexPage のエッジケースを確認する。
/// </summary>
public class GetFileNameEdgeCaseTests
{
    /// <summary>
    /// TG-7: URL のファイル名部分に %2F（パーセントエンコードされたスラッシュ）が含まれる場合、
    /// Uri.UnescapeDataString によって '/' にデコードされた表示名が返されることを確認する。
    /// （表示名への影響のみで、HTML の href 属性には使われないため XSS リスクなし）
    /// </summary>
    [Fact]
    public void GetFileName_PercentEncodedSlash_DecodedToSlashInDisplayName()
    {
        // %2F はパーセントエンコードされた '/' — デコード後は "my/app.js" になる
        string result = HtmlReportGenerator.GetFileName("http://example.com/scripts/my%2Fapp.js");
        Assert.Equal("my/app.js", result);
    }

    /// <summary>
    /// TG-11: BuildIndexPage に covered + partial * 0.5 が total を超えるデータを渡した場合、
    /// カバレッジ率が 100% 超で表示されることを確認する（クランプなし・そのまま表示）。
    /// </summary>
    [Fact]
    public void BuildIndexPage_CoveredPlusHalfPartialExceedsTotal_OverHundredPercentDisplayed()
    {
        // covered=10, partial=10, total=10 → pct = 100 * (10 + 5) / 10 = 150.0%
        var tabs = new List<(string pageUrl, string tabFilename)>
        {
            ("http://example.com/", "script-0.html"),
        };
        var rows = new List<(
            IReadOnlyList<(string pageUrl, string tabFilename)> pages,
            string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            (tabs, "http://example.com/app.js", 1, 10, 10, 10, "script-0.html"),
        };

        string html = HtmlReportGenerator.BuildIndexPage(rows);

        // カバレッジ率 150.0% がそのまま出力される（クランプなし）
        Assert.Contains("150.0%", html);
    }

    /// <summary>
    /// M-1 fix: GetFileName でルートパス（末尾 '/' のみ）の URL に認証情報（user:pass@）が含まれる場合、
    /// 認証情報を除去したホスト名のみを返すことを確認する。
    /// 修正前は "user:pass@example.com" が返されてレポートに認証情報が露出していた。
    /// </summary>
    [Fact]
    public void GetFileName_RootPathUrlWithAuthInfo_AuthInfoStripped()
    {
        // http://user:pass@example.com/ → パス部分が "/" のみ → ルートパスケース
        string result = HtmlReportGenerator.GetFileName("http://user:pass@example.com/");
        // 認証情報が除去され、ホスト名のみが返される
        Assert.Equal("example.com", result);
        Assert.DoesNotContain("user", result);
        Assert.DoesNotContain("pass", result);
    }

    /// <summary>
    /// BuildScriptPage に複数のページ URL を渡した場合、空のエントリはプレースホルダーで表示され
    /// 非空のエントリは URL が表示されることを確認する。
    /// </summary>
    [Fact]
    public void BuildScriptPage_TwoPageUrlsOneEmpty_PlaceholderForEmpty()
    {
        // 1件目: URL なし（プレースホルダー表示）、2件目: URL あり
        var html = HtmlReportGenerator.BuildScriptPage(
            [(""), ("https://example.com/page")],
            "https://example.com/app.js",
            []);

        // 空 URL のエントリはプレースホルダーで表示されること
        Assert.Contains("(URL なし)", html);
        // 2件目の URL が含まれること
        Assert.Contains("https://example.com/page", html);
    }

    // ─── TC-4: BuildLines — 行中の \0 文字 ──────────────────────────────────────

    /// <summary>
    /// 行の途中に \0（ヌル文字）がある場合、\0 はレンダリングされないが
    /// 後続の文字のカバレッジが正しく取得されることを確認する。
    /// （BuildLines で \0 は表示スキップされるが、idx = offset + i の計算は CDP マップと一致する）
    /// </summary>
    [Fact]
    public void BuildLines_NullCharMidLine_SubsequentCharHasCorrectCoverage()
    {
        // "a\0b" — a(idx=0): covered(1)、\0(idx=1): neutral(-1)、b(idx=2): uncovered(0)
        const string source = "a\0b";
        int[] map = [1, -1, 0];

        var lines = HtmlReportGenerator.BuildLines(source, map);

        // 1行だけ生成される
        Assert.Single(lines);
        var line = lines[0];

        // a は covered、b は uncovered → Partial 行
        Assert.Equal(LineCoverageStatus.Partial, line.Status);
        // a が covered span に含まれること
        Assert.Contains("covered", line.Html);
        // b が uncovered span に含まれること
        Assert.Contains("uncovered", line.Html);
    }

    // ─── TC-5: GetFileName — chrome-extension:// URL ─────────────────────────────

    /// <summary>
    /// chrome-extension:// スキームの URL は http/https/file 以外のため、
    /// クエリ・フラグメントを含む URL 全体がそのまま返されることを確認する。
    /// （現状の仕様として文書化。XSS 対策は呼び出し元の HtmlEncode で行う）
    /// </summary>
    [Fact]
    public void GetFileName_ChromeExtensionUrlWithQueryAndFragment_ReturnsAsIs()
    {
        // chrome-extension:// は非対応スキームのため全文字列をそのまま返す
        string input = "chrome-extension://abcdef1234567890/content.js?v=1#top";
        string result = HtmlReportGenerator.GetFileName(input);
        Assert.Equal(input, result);
    }

    // ─── TC-8: BuildIndexPage — 全行 Neutral のスクリプト ───────────────────────

    /// <summary>
    /// 全行が Neutral（カバレッジ対象外）のスクリプトのみの場合、
    /// total=0 となり 0% が表示されることを確認する（ゼロ除算なし）。
    /// </summary>
    [Fact]
    public void BuildIndexPage_AllNeutralLines_ShowsZeroPercent()
    {
        // 全行 Neutral → covered=0, partial=0, total=0
        var rows = new List<(
            IReadOnlyList<(string pageUrl, string tabFilename)> pages,
            string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            ([("https://example.com/", "script-0.html")],
             "https://example.com/app.js", 1, 0, 0, 0, "script-0.html"),
        };

        string html = HtmlReportGenerator.BuildIndexPage(rows);

        // 0.0% が表示されること（ゼロ除算例外が発生しないこと）
        Assert.Contains("0.0%", html);
    }

    // ─── TC-9: GetFileName — 末尾スラッシュの有無で結果が同じになること ─────────

    /// <summary>
    /// "https://example.com/" と "https://example.com" は別コードパスを通るが、
    /// どちらもホスト名 "example.com" を返すことを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_TrailingSlashVsNoSlash_SameResult()
    {
        string withSlash    = HtmlReportGenerator.GetFileName("https://example.com/");
        string withoutSlash = HtmlReportGenerator.GetFileName("https://example.com");

        Assert.Equal(withoutSlash, withSlash);
        Assert.Equal("example.com", withSlash);
    }

    // ─── TC-10: GetFileName — IPv6 アドレスを含む URL ───────────────────────────

    /// <summary>
    /// IPv6 アドレス（[::1]）を含む URL から正しくファイル名部分を取得できることを確認する。
    /// IPv6 アドレスの [] 内に '/' は含まれないため pathStart の検索が正しく動作する。
    /// </summary>
    [Fact]
    public void GetFileName_IPv6Url_ReturnsFileName()
    {
        // http://[::1]/app.js → app.js
        string result = HtmlReportGenerator.GetFileName("http://[::1]/app.js");
        Assert.Equal("app.js", result);
    }

    // ─── TC-11: GetFileName — ユーザー情報（user:pass@）付き URL ───────────────

    /// <summary>
    /// user:pass@ 形式の認証情報を含む URL でも、
    /// パス部分からファイル名が正しく取得できることを確認する。
    /// （認証情報はパス検索に影響しない）
    /// </summary>
    [Fact]
    public void GetFileName_UserInfoInUrl_ReturnsFileName()
    {
        // http://user:pass@host/file.js → file.js
        string result = HtmlReportGenerator.GetFileName("http://user:pass@host/file.js");
        Assert.Equal("file.js", result);
    }

    // ─── TC-12: GetFileName — ポート番号がユーザー情報の : と紛らわしい URL ──

    /// <summary>
    /// user:8080@ 形式（ポート番号のように見えるユーザー情報）を含む URL でも、
    /// ファイル名部分が正しく取得できることを確認する。
    /// pathStart の検索はスキーム直後の最初の / を探すだけなので影響を受けない。
    /// </summary>
    [Fact]
    public void GetFileName_PortLikeUserInfoInUrl_ReturnsFileName()
    {
        // http://user:8080@host/file.js → ユーザー情報の : がポートに見えるが正しく処理される
        string result = HtmlReportGenerator.GetFileName("http://user:8080@host/file.js");
        Assert.Equal("file.js", result);
    }

    // ─── TC-13: BuildLines — ソース末尾が CRLF で終わる場合 ────────────────

    /// <summary>
    /// ソースが CRLF（\r\n）で終わる場合でも正しい行数が返ることを確認する。
    /// source.EndsWith('\n') が true のため末尾の空行がカウントされないことを検証する。
    /// </summary>
    [Fact]
    public void BuildLines_SourceEndingWithCRLF_CorrectLineCount()
    {
        // "abc\r\ndef\r\n": CRLF で終わる2行。行数は2であること
        string source = "abc\r\ndef\r\n";
        var map = new int[source.Length];
        Array.Fill(map, 1); // 全文字実行済み
        var lines = HtmlReportGenerator.BuildLines(source, map);
        // 末尾の空行はカウントされないため 2 行
        Assert.Equal(2, lines.Count);
        // 各行に \r が含まれないこと
        Assert.DoesNotContain("\r", lines[0].Html);
        Assert.DoesNotContain("\r", lines[1].Html);
    }

    // -----------------------------------------------------------------------
    // BuildLines — 1 文字ソースの境界値テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 1 文字だけのソース（"a"）でも正しく 1 行が返ることを確認する（境界値テスト）。
    /// </summary>
    [Fact]
    public void BuildLines_SingleCharSource_ReturnsOneLine()
    {
        // source = "a"（改行なし）→ 1 行
        var lines = HtmlReportGenerator.BuildLines("a", [1]);
        Assert.Single(lines);
        Assert.Equal(LineCoverageStatus.Covered, lines[0].Status);
    }

    /// <summary>
    /// 改行のみのソース（"\n\n"）の行数が正しく 2 になることを確認する。
    /// source.EndsWith('\n') が true のため末尾の空要素が除外される。
    /// </summary>
    [Fact]
    public void BuildLines_OnlyNewlines_CorrectLineCount()
    {
        // "\n\n" は Split('\n') で ["", "", ""] → 末尾の空要素を除いて 2 行
        var lines = HtmlReportGenerator.BuildLines("\n\n", [-1, -1]);
        Assert.Equal(2, lines.Count);
    }

    /// <summary>
    /// CR のみ（\r）1文字のソースを渡した場合、1行の Neutral 行が返ることを確認する。
    /// source.EndsWith('\r') が true のため末尾の空要素が除外されて行数は 1 になる。
    /// </summary>
    [Fact]
    public void BuildLines_OnlyCr_ReturnsOneNeutralLine()
    {
        // "\r" のみ — CR で行分割され末尾の空要素を除いて 1 行（空行 = Neutral）
        var lines = HtmlReportGenerator.BuildLines("\r", [-1]);
        Assert.Single(lines);
        Assert.Equal(LineCoverageStatus.Neutral, lines[0].Status);
    }

    /// <summary>
    /// 全行が未実行（covered=0, partial=0, total=10）の場合、
    /// カバレッジ率が "0.0%" と表示されることを確認する（境界値テスト）。
    /// </summary>
    [Fact]
    public void BuildIndexPage_AllUncovered_ShowsZeroPercent()
    {
        var tabs = new List<(string pageUrl, string tabFilename)>
        {
            ("http://example.com/", "script-0.html"),
        };
        var rows = new List<(IReadOnlyList<(string pageUrl, string tabFilename)> pages,
                             string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            (tabs, "http://example.com/app.js", 1, 0, 0, 10, "script-0.html"),
        };

        string html = HtmlReportGenerator.BuildIndexPage(rows);

        Assert.Contains("0.0%", html);
    }

    /// <summary>
    /// 全行が実行済み（covered=10, partial=0, total=10）の場合、
    /// カバレッジ率が "100.0%" と表示されることを確認する（境界値テスト）。
    /// </summary>
    [Fact]
    public void BuildIndexPage_AllCovered_ShowsHundredPercent()
    {
        var tabs = new List<(string pageUrl, string tabFilename)>
        {
            ("http://example.com/", "script-0.html"),
        };
        var rows = new List<(IReadOnlyList<(string pageUrl, string tabFilename)> pages,
                             string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            (tabs, "http://example.com/app.js", 1, 10, 0, 10, "script-0.html"),
        };

        string html = HtmlReportGenerator.BuildIndexPage(rows);

        Assert.Contains("100.0%", html);
    }

    /// <summary>
    /// スキームなし（相対パス）の URL（例: /js/app.js）はパース対象外のためそのまま返されることを確認する。
    /// http:// / https:// / file:// で始まらない文字列は変換せずそのまま返す。
    /// </summary>
    [Fact]
    public void GetFileName_RelativePath_ReturnsAsIs()
    {
        // "/js/app.js" — http でも https でも file でもないためそのまま返す
        string result = HtmlReportGenerator.GetFileName("/js/app.js");
        Assert.Equal("/js/app.js", result);
    }

    /// <summary>
    /// https://user:pass@example.com/scripts/app.js のように認証情報が含まれる URL でも
    /// ファイル名（app.js）が正しく返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_UrlWithCredentials_ReturnsFilename()
    {
        // https://user:pass@example.com/scripts/app.js → app.js
        string result = HtmlReportGenerator.GetFileName("https://user:pass@example.com/scripts/app.js");
        Assert.Equal("app.js", result);
    }

    /// <summary>
    /// I-1 修正: 同じファイル名を持つスクリプトが複数ある場合（異なるホストで同名ファイル）、
    /// BuildIndexPage のリンクテキストに連番サフィックスが付いて区別できることを確認する。
    /// 修正前は両方とも "app.js" と表示されユーザーが区別できなかった。
    /// </summary>
    [Fact]
    public void BuildIndexPage_DuplicateFilename_AppendsCounter()
    {
        // 同じファイル名 "app.js" を持つが異なるホストの URL を持つ 2 件のスクリプト
        var tabs = new List<(string pageUrl, string tabFilename)>
        {
            ("http://example.com/", "script-0.html"),
        };
        var rows = new List<(
            IReadOnlyList<(string pageUrl, string tabFilename)> pages,
            string url, int screenCount, int covered, int partial, int total, string mergedFilename)>
        {
            (tabs, "http://host1.com/app.js", 1, 5, 0, 10, "script-0.html"),
            (tabs, "http://host2.com/app.js", 1, 3, 0, 10, "script-1.html"),
        };

        string html = HtmlReportGenerator.BuildIndexPage(rows);

        // 1 件目はサフィックスなし
        Assert.Contains(">app.js<", html);
        // 2 件目は "(2)" サフィックス付き
        Assert.Contains(">app.js (2)<", html);
    }
}

