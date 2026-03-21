using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

/// <summary>
/// HtmlReportGenerator.BuildCoverageMap メソッドの動作を検証するテスト群。
/// BuildCoverageMap は「ソースコードの各文字が実行されたかどうか」を配列で返す。
/// 配列の値の意味: -1=カバレッジ対象外、0=未実行、1=実行済み
/// </summary>
public class CoverageMapTests
{
    // -----------------------------------------------------------------------
    // 基本動作のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 関数データが空（カバレッジ対象なし）の場合、全文字が -1（対象外）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_OutOfScope_IsMinusOne()
    {
        // 関数データを渡さない → 全文字が「カバレッジ対象外」になるはず
        var map = HtmlReportGenerator.BuildCoverageMap("abc", []);
        Assert.Equal([-1, -1, -1], map);
    }

    /// <summary>
    /// ソース全体をカバーする範囲（count=1）を渡した場合、全文字が 1（実行済み）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_AllCovered()
    {
        // 0〜5文字目を「1回実行された」範囲として渡す
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(0, 5, 1)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("hello", functions);
        Assert.Equal([1, 1, 1, 1, 1], map);
    }

    /// <summary>
    /// ソース全体をカバーする範囲（count=0）を渡した場合、全文字が 0（未実行）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_AllUncovered()
    {
        // 0〜5文字目を「一度も実行されなかった」範囲として渡す
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(0, 5, 0)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("hello", functions);
        Assert.Equal([0, 0, 0, 0, 0], map);
    }

    /// <summary>
    /// 外側の大きな範囲（実行済み）の内側に小さな範囲（未実行）がある場合、
    /// 内側の範囲が外側を上書きできることを確認する。
    /// これにより if/else の分岐が正確に表現できる。
    /// </summary>
    [Fact]
    public void BuildMap_InnerRangeOverridesOuter()
    {
        // ソース: "if(x){A}else{B}"
        //         0123456789012345
        // 外側: 関数全体（0〜16）が実行済み（count=3）
        // 内側: else ブランチ（13〜16）が未実行（count=0）
        var source = "if(x){A}else{B}";
        var functions = new[]
        {
            new FunctionCoverage("f", [
                new CoverageRange(0,  16, 3),   // 外側の関数全体：実行済み
                new CoverageRange(13, 16, 0),   // else ブランチ：未実行
            ])
        };
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);
        // 0〜12文字目（if ブランチ）は実行済み
        Assert.All(map[..13], v => Assert.Equal(1, v));
        // 13〜15文字目（else ブランチ）は未実行
        Assert.All(map[13..], v => Assert.Equal(0, v));
    }

    // -----------------------------------------------------------------------
    // 境界値・異常データのテスト（エラーにならず安全に処理されることを確認する）
    // -----------------------------------------------------------------------

    /// <summary>
    /// ソースコードが空文字列の場合、長さ0の配列が返されることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_EmptySource_ReturnsEmptyArray()
    {
        // 空のソースコードを渡す → 文字数が0なので配列も空になるはず
        var map = HtmlReportGenerator.BuildCoverageMap("", []);
        Assert.Empty(map);
    }

    /// <summary>
    /// ソースコードが空文字列でも、範囲を渡した場合にエラーにならず空配列が返されることを確認する。
    /// 範囲はソース長（0）にクランプされるためループが回らず、何も書き込まれない。
    /// </summary>
    [Fact]
    public void BuildMap_EmptySourceWithRanges_ReturnsEmptyArray()
    {
        // ソースが空でも範囲データを渡した場合 → クランプされて空配列になるはず
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(0, 5, 1)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("", functions);
        Assert.Empty(map);
    }

    /// <summary>
    /// 開始位置と終了位置が同じ（長さ0）範囲を渡した場合、何も書き込まれないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_ZeroLengthRange_NoEffect()
    {
        // startOffset と endOffset が同じ（2, 2）→ 長さゼロの範囲なので何も変わらないはず
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(2, 2, 1)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("hello", functions);
        Assert.Equal([-1, -1, -1, -1, -1], map);
    }

    /// <summary>
    /// 終了位置がソースの長さを超えている範囲を渡した場合、
    /// ソース末尾にクランプされて例外なく処理されることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_RangeExceedingSourceLength_Clamped()
    {
        // endOffset=100 だがソース長は5 → 5にクランプされて全文字が covered になるはず
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(0, 100, 1)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("hello", functions);
        Assert.Equal([1, 1, 1, 1, 1], map);
    }

    /// <summary>
    /// 開始位置が負の数の範囲を渡した場合、0にクランプされて例外なく処理されることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_NegativeStartOffset_ClampedToZero()
    {
        // startOffset=-5 → 0にクランプ。範囲(0, 3)として先頭3文字が covered になるはず
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(-5, 3, 1)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("hello", functions);
        Assert.Equal([1, 1, 1, -1, -1], map);
    }

    /// <summary>
    /// 開始位置がソースの長さ以上の範囲を渡した場合、何も書き込まれないことを確認する。
    /// （開始位置 > 終了位置になるため、ループが回らない）
    /// </summary>
    [Fact]
    public void BuildMap_RangeStartBeyondSource_NoEffect()
    {
        // startOffset=10 だがソース長は5 → start(10) > clamp後のend(5) でループが回らない
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(10, 20, 1)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("hello", functions);
        Assert.Equal([-1, -1, -1, -1, -1], map);
    }

    /// <summary>
    /// ソース末尾を超えて終わる範囲を渡した場合、ソース内の部分だけ書き込まれることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_PartiallyOutOfBoundsRange_ClampedWrite()
    {
        // 範囲(3, 10)はソース末尾(5)を超えている → (3, 5)にクランプされて4〜5文字目だけ covered になるはず
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(3, 10, 1)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("hello", functions);
        Assert.Equal([-1, -1, -1, 1, 1], map);
    }

    /// <summary>
    /// 実行回数（count）が1より大きい場合でも、実行済み（値=1）として扱われることを確認する。
    /// 何度実行されても「実行済み」の区別しかない。
    /// </summary>
    [Fact]
    public void BuildMap_CountGreaterThanOne_StillCovered()
    {
        // count=100（関数が100回呼ばれた）でも値は 1（covered）になるはず
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(0, 3, 100)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("abc", functions);
        Assert.Equal([1, 1, 1], map);
    }

    /// <summary>
    /// 実行回数（count）が負の数の不正データの場合、未実行（値=0）として扱われることを確認する。
    /// 判定条件が「count > 0」なので、0以下はすべて未実行扱いになる。
    /// </summary>
    [Fact]
    public void BuildMap_NegativeCount_TreatedAsUncovered()
    {
        // count=-1 の不正データ → count > 0 が false なので未実行（0）扱いになるはず
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(0, 3, -1)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("abc", functions);
        Assert.Equal([0, 0, 0], map);
    }

    /// <summary>
    /// 複数の関数のカバレッジ範囲がすべてマップに書き込まれることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_MultipleFunctions_RangesMerged()
    {
        // 2つの関数の範囲を渡す（f1: 0〜3文字目、f2: 3〜5文字目）
        var functions = new[]
        {
            new FunctionCoverage("f1", [new CoverageRange(0, 3, 1)]),
            new FunctionCoverage("f2", [new CoverageRange(3, 5, 1)]),
        };
        var map = HtmlReportGenerator.BuildCoverageMap("hello", functions);
        // 全文字が covered になるはず
        Assert.Equal([1, 1, 1, 1, 1], map);
    }

    // -----------------------------------------------------------------------
    // 実際の JavaScript コードパターンを模倣したテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 宣言だけして一度も呼ばれなかった関数は、全体が未実行（値=0）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_AllUncovered()
    {
        // JS: function unused() { return 42; }
        // 宣言はされているが呼ばれていない → 範囲全体が count=0（未実行）
        var source = "function unused(){return 42;}";
        var functions = new[]
        {
            new FunctionCoverage("unused", [new CoverageRange(0, source.Length, 0)])
        };
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);
        // すべての文字が未実行（0）になっているか確認する
        Assert.All(map, v => Assert.Equal(0, v));
    }

    /// <summary>
    /// 三項演算子のように「外側は実行済み、片方の分岐だけ実行済み、もう片方は未実行」の場合、
    /// 内側の細かい範囲が外側の大きな範囲を正しく上書きすることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_TernaryBranches_InnerWins()
    {
        // JS: cond ? "yes" : "no" を模倣する
        // source: "yes_no_"（yes=0〜2文字目、no=4〜6文字目）
        //          0123456
        var source = "yes_no_";
        var functions = new[]
        {
            new FunctionCoverage("f", [
                new CoverageRange(0, 7, 1),  // 外側の関数全体：実行済み
                new CoverageRange(0, 3, 1),  // true 分岐（"yes"）：実行済み
                new CoverageRange(4, 7, 0),  // false 分岐（"no"）：未実行
            ])
        };
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);
        // 0〜2文字目（"yes"）は実行済みになっているか確認する
        Assert.All(map[..3], v => Assert.Equal(1, v));
        // 4〜6文字目（"no"）は未実行になっているか確認する
        Assert.All(map[4..7], v => Assert.Equal(0, v));
    }

    /// <summary>
    /// 外側の関数は実行されたが、内側のコールバック関数は呼ばれなかった場合、
    /// コールバック部分が未実行（値=0）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_NestedCallback_InnerNeverCalled()
    {
        // JS: outer 関数は実行されたが、引数に渡したコールバック（inner）は呼ばれなかった
        // source = "OOOOOOOOIIIIIIIIOOOOOOO"（O=outer のみの部分, I=inner の部分）
        //           0       8      16    23
        var source = new string('O', 8) + new string('I', 8) + new string('O', 7);
        var functions = new[]
        {
            new FunctionCoverage("outer",    [new CoverageRange(0,  23, 3)]),  // outer: 実行済み
            new FunctionCoverage("callback", [new CoverageRange(8,  16, 0)]),  // callback: 未実行
        };
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);
        // 0〜7文字目（outer のみの部分）は実行済みになっているか確認する
        Assert.All(map[..8],   v => Assert.Equal(1, v));
        // 8〜15文字目（callback の本体）は未実行になっているか確認する
        Assert.All(map[8..16], v => Assert.Equal(0, v));
        // 16〜22文字目（outer のみの部分）は実行済みになっているか確認する
        Assert.All(map[16..],  v => Assert.Equal(1, v));
    }

    /// <summary>
    /// switch 文で一致した case だけが実行される場合、
    /// 一致した case は実行済み、それ以外の case は未実行になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_SwitchCases_OnlyMatchedCaseCovered()
    {
        // JS: switch(x) { case 'a': ...; case 'b': ...; case 'c': ...; } で case 'a' だけ実行
        // source: "aaa_bbb_ccc"（各 case を4文字で模倣）
        //          01234567890
        var source = "aaa_bbb_ccc";
        var functions = new[]
        {
            new FunctionCoverage("f", [
                new CoverageRange(0, 11, 1),  // switch 全体：実行済み（switch 文自体は通った）
                new CoverageRange(0,  4, 1),  // case 'a'：実行済み
                new CoverageRange(4,  8, 0),  // case 'b'：未実行
                new CoverageRange(8, 11, 0),  // case 'c'：未実行
            ])
        };
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);
        // 0〜3文字目（case 'a'）は実行済みになっているか確認する
        Assert.All(map[..4],   v => Assert.Equal(1, v));
        // 4〜7文字目（case 'b'）は未実行になっているか確認する
        Assert.All(map[4..8],  v => Assert.Equal(0, v));
        // 8〜10文字目（case 'c'）は未実行になっているか確認する
        Assert.All(map[8..11], v => Assert.Equal(0, v));
    }

    // -----------------------------------------------------------------------
    // MarkUncalledFunctionBodiesAsUncovered のテスト
    // V8 の遅延コンパイルにより未実行関数がカバレッジデータに含まれなかった場合の補正
    // -----------------------------------------------------------------------

    /// <summary>
    /// カバレッジデータが空（全文字が対象外）のとき、
    /// ソース中の function 宣言の本体全体が未実行(0)としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunctionDeclaration_MarkedAsUncovered()
    {
        // "function foo() { x; }" — カバレッジデータなし → 全バイトが対象外(-1)から始まる
        var source = "function foo() { x; }";
        var map = HtmlReportGenerator.BuildCoverageMap(source, []);
        // V8 が含めなかった関数として検出され、全バイトが未実行(0)になるか確認する
        Assert.All(map, v => Assert.Equal(0, v));
    }

    /// <summary>
    /// 呼ばれた関数（カバレッジデータあり）と呼ばれなかった関数（データなし）が混在する場合、
    /// 呼ばれた関数は実行済み(1)のまま、呼ばれなかった関数は未実行(0)になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_MixedCalledAndUncalledFunctions_CorrectlyMarked()
    {
        // "function a(){} function b(){}"
        //  0         1         2
        //  0123456789012345678901234567890
        // function a(){} は 0〜13（14文字）
        // スペース         は 14
        // function b(){} は 15〜28（14文字）
        var source = "function a(){} function b(){}";
        var functions = new[]
        {
            // function a() は呼ばれた（カバレッジデータあり、全体が実行済み）
            new FunctionCoverage("a", [new CoverageRange(0, 14, 1)]),
        };
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);
        // function a() の部分（0〜13）は実行済み(1)のままであることを確認する
        Assert.All(map[..14], v => Assert.Equal(1, v));
        // スペース（14）はカバレッジ対象外(-1)のままであることを確認する
        Assert.Equal(-1, map[14]);
        // function b() の部分（15〜28）は未実行(0)としてマークされていることを確認する
        Assert.All(map[15..], v => Assert.Equal(0, v));
    }

    /// <summary>
    /// 行コメント内の function キーワードは検出されず、
    /// 対象外(-1)のままになることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionInLineComment_RemainsNeutral()
    {
        // コメント内の function はスキップされるため補正の対象にならない
        var source = "// function foo() { x; }";
        var map = HtmlReportGenerator.BuildCoverageMap(source, []);
        // コメント内なので中立(-1)のままであることを確認する
        Assert.All(map, v => Assert.Equal(-1, v));
    }

    /// <summary>
    /// 文字列リテラル内の function キーワードは検出されず、
    /// 対象外(-1)のままになることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionInStringLiteral_RemainsNeutral()
    {
        // 文字列内の function はスキップされるため補正の対象にならない
        var source = "\"function foo() { x; }\"";
        var map = HtmlReportGenerator.BuildCoverageMap(source, []);
        // 文字列内なので中立(-1)のままであることを確認する
        Assert.All(map, v => Assert.Equal(-1, v));
    }

    /// <summary>
    /// "function" という文字列が識別子の一部として現れる場合（例: functionHelper）、
    /// function キーワードとして検出されず対象外(-1)のままになることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionAsPartOfIdentifier_RemainsNeutral()
    {
        // functionHelper は識別子なので function キーワードとして検出されない
        var source = "functionHelper();";
        var map = HtmlReportGenerator.BuildCoverageMap(source, []);
        // 識別子の一部なので中立(-1)のままであることを確認する
        Assert.All(map, v => Assert.Equal(-1, v));
    }

    // -----------------------------------------------------------------------
    // 正規表現リテラル内の特殊文字による誤検出テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 関数本体に /}/ という正規表現が含まれる場合、
    /// FindMatchingBrace が正規表現内の } を関数終端と誤認識しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionBodyWithRegexContainingBrace_CorrectlyMarked()
    {
        // function foo() { var re = /}/; return 1; }
        // FindMatchingBrace が /}/ 内の } で誤終了すると、return 1; 以降が中立(-1)のまま残る
        var source = "function foo() { var re = /}/; return 1; }";
        var map = HtmlReportGenerator.BuildCoverageMap(source, []);
        // カバレッジデータなし → V8 未コンパイル → 全文字が未実行(0)になるべき
        Assert.All(map, v => Assert.Equal(0, v));
    }

    /// <summary>
    /// 関数本体に /{/ と /}/ を含む複雑な正規表現がある場合も正しくマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionBodyWithRegexContainingBraces_CorrectlyMarked()
    {
        // function foo() { var re = /[{}]/; }
        var source = "function foo() { var re = /[{}]/; }";
        var map = HtmlReportGenerator.BuildCoverageMap(source, []);
        Assert.All(map, v => Assert.Equal(0, v));
    }

    /// <summary>
    /// 正規表現リテラル内の ) がパラメータ括弧の終わりと誤認識されないことを確認する。
    /// 未実行の関数のパラメータに正規表現が含まれる場合でも、関数本体が赤くなるべき。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionParamWithRegexContainingParen_CorrectlyMarked()
    {
        // 正規表現リテラル内の ) がパラメータ括弧の終わりと誤認識されないことを確認する
        // 未実行の関数のパラメータに正規表現が含まれる場合でも、関数本体が赤くなるべき
        const string source = "function f(x = /a)b/) { return x; }";
        //                     0         1         2         3
        //                     0123456789012345678901234567890123456
        // 文字列長: 35
        // { は index 22、} は index 34

        // カバレッジデータなし → V8 未コンパイル → 全文字が未実行(0)になるべき
        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 関数本体の { } 内は 0（未実行・赤）でなければならない
        Assert.Equal(0, map[22]); // {
        Assert.Equal(0, map[34]); // }
    }

    /// <summary>
    /// 未実行の async function において async キーワードも赤くマークされることを確認する。
    /// カバレッジデータなし（V8 が未コンパイル）の状態で MarkUncalledFunctionBodiesAsUncovered が
    /// function キーワードとともに async キーワードも 0（未実行・赤）にマークするか検証する。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncUncalledFunction_AsyncKeywordAlsoMarkedAsUncovered()
    {
        // カバレッジデータなし → V8 が async function f を未コンパイルのまま残した状態を模倣する
        const string source = "async function f() { return 1; }";
        //                     0         1         2         3
        //                     01234567890123456789012345678901
        // async: 0-4, ' ': 5, function: 6-13, ' ': 14, f: 15, (: 16, ): 17, ' ': 18, {: 19, ..., }: 31
        // 文字列長: 32

        // カバレッジデータを渡さない → MarkUncalledFunctionBodiesAsUncovered が補正対象にする
        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // async（index 0–4）も未実行（0）でなければならない
        Assert.Equal(0, map[0]); // 'a'
        Assert.Equal(0, map[4]); // 'c'
        // function キーワードの先頭も 0
        Assert.Equal(0, map[6]); // 'f' of function
        // 関数本体も 0
        Assert.Equal(0, map[19]); // {
        Assert.Equal(0, map[31]); // }
    }

    [Fact]
    public void BuildMap_FunctionKeywordInRegexLiteral_RemainsNeutral()
    {
        // 正規表現リテラル内の "function" が関数として誤検出されないことを確認する
        // 正規表現内の function キーワードは -1（ニュートラル）のままであるべき
        // 正規表現 /function() {}/ は見かけ上の関数宣言に見えるが、あくまで正規表現パターン
        const string source = "var re = /function() {}/;";
        //                     0         1         2
        //                     0123456789012345678901234

        // カバレッジデータなし（全体が -1 ニュートラル）の状態でマップを構築する
        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 正規表現内の "function" は検出されてはいけないため、すべて -1 のまま
        // 修正前は /function() {}/ 内の {} が関数本体と誤認識され、0（未実行）になってしまう
        // index 10 は 'f'（function の先頭）
        Assert.Equal(-1, map[10]);
        // index 22 は '}'（正規表現内の閉じ波括弧）
        Assert.Equal(-1, map[22]);
        // index 23 は '/'（正規表現の終わり）
        Assert.Equal(-1, map[23]);
    }

    // -----------------------------------------------------------------------
    // アロー関数の未実行検出テスト
    // ブロック本体 {} を持つアロー関数の補正処理を検証する
    // -----------------------------------------------------------------------

    /// <summary>
    /// 未実行のアロー関数（ブロック本体）がカバレッジデータにない場合、
    /// => からブロック本体 {} 全体が未実行(0)としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledArrowFunctionWithBlock_MarkedAsUncovered()
    {
        // 未実行のアロー関数（ブロック本体）が赤くマークされることを確認する
        const string source = "const f = () => { return 1; };";
        //                     0         1         2
        //                     012345678901234567890123456789

        // カバレッジデータなし（全体 -1 ニュートラル）
        var functions = new List<FunctionCoverage>();
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);

        // => は index 13–14、{ は index 16、} は index 28
        Assert.Equal(0, map[13]); // '=' of =>
        Assert.Equal(0, map[16]); // {
        Assert.Equal(0, map[28]); // }
    }

    /// <summary>
    /// 実行済みのアロー関数（ブロック本体）はカバレッジデータがあるため、
    /// 未実行(0)としてマークされないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_CalledArrowFunctionWithBlock_NotMarkedAsUncovered()
    {
        // 実行済みのアロー関数（ブロック本体）は赤くならないことを確認する
        const string source = "const f = () => { return 1; };";

        // 関数全体を実行済み（count=1）にする
        var functions = new List<FunctionCoverage>
        {
            new FunctionCoverage("f", new List<CoverageRange>
            {
                new CoverageRange(0, source.Length, 1),
            }),
        };
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);

        // 実行済みなので { } 内は 1（緑）のまま
        Assert.Equal(1, map[16]); // {
        Assert.Equal(1, map[28]); // }
    }

    /// <summary>
    /// アロー関数でもブロック本体でない場合（式本体）は検出の対象外であることを確認する。
    /// 式本体の場合は V8 CDP が範囲データを提供するため、ここでの処理は不要。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledArrowFunctionWithExpression_NotMarkedAsUncovered()
    {
        // アロー関数でもブロック本体でない場合（式本体）は検出しない
        // 式本体の場合は V8 CDP が範囲データを提供するため、ここでの処理は不要
        const string source = "const f = x => x + 1;";
        //                     0         1         2
        //                     012345678901234567890

        // カバレッジデータなし（全体 -1 ニュートラル）
        var functions = new List<FunctionCoverage>();
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);

        // 式本体なので => の位置（index 13）は -1（ニュートラル）のまま
        // （ブロックではないので MarkUncalledFunctionBodiesAsUncovered に影響されない）
        Assert.Equal(-1, map[13]); // '=' of =>
    }

    // -----------------------------------------------------------------------
    // メソッド短縮構文（method shorthand）の未実行検出テスト
    // オブジェクトリテラル・クラスのメソッドが未実行の場合の補正を検証する
    // -----------------------------------------------------------------------

    /// <summary>
    /// 未実行のメソッド短縮構文が赤くマークされることを確認する。
    /// オブジェクトリテラル内の greet() { } のようなメソッドは
    /// function キーワードを使わないため、通常の補正では検出されない。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledMethodShorthand_MarkedAsUncovered()
    {
        // 未実行のメソッド短縮構文が赤くマークされることを確認する
        const string source = "const obj = { greet() { return 1; } };";
        //                     0         1         2         3
        //                     0123456789012345678901234567890123456789
        // greet は index 14、( は index 19、) は index 20、{ は index 22、} は index 34

        // カバレッジデータなし（全体 -1 ニュートラル）
        var functions = new List<FunctionCoverage>();
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);

        // greet（index 14）から始まり、{ } 内も 0（未実行）
        Assert.Equal(0, map[14]); // 'g' of greet
        Assert.Equal(0, map[22]); // {
        Assert.Equal(0, map[34]); // }
    }

    /// <summary>
    /// 実行済みのメソッド短縮構文は赤くならないことを確認する。
    /// カバレッジデータが存在するメソッドは補正の対象外となる。
    /// </summary>
    [Fact]
    public void BuildMap_CalledMethodShorthand_NotMarkedAsUncovered()
    {
        // 実行済みのメソッド短縮構文は赤くならないことを確認する
        const string source = "const obj = { greet() { return 1; } };";

        // greet 全体を実行済み（count=1）にする
        var functions = new List<FunctionCoverage>
        {
            new FunctionCoverage("greet", new List<CoverageRange>
            {
                new CoverageRange(14, 35, 1),
            }),
        };
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);

        // 実行済みなので { } 内は 1（緑）のまま
        Assert.Equal(1, map[22]); // {
        Assert.Equal(1, map[34]); // }
    }

    /// <summary>
    /// if (...) {} がメソッドとして誤検出されないことを確認する。
    /// "if" はコントロールフローキーワードとして除外される必要がある。
    /// </summary>
    [Fact]
    public void BuildMap_IfStatementNotDetectedAsMethod_RemainsNeutral()
    {
        // if (...) {} がメソッドとして誤検出されないことを確認する
        const string source = "if (true) { x = 1; }";
        //                     0         1         2
        //                     012345678901234567890

        // カバレッジデータなし（全体 -1 ニュートラル）
        var functions = new List<FunctionCoverage>();
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);

        // if ブロック内は -1（ニュートラル）のまま
        Assert.Equal(-1, map[10]); // {
        Assert.Equal(-1, map[19]); // }
    }

    // -----------------------------------------------------------------------
    // Task 8: 検証テスト — コメント・テンプレートリテラル内の function キーワード
    // -----------------------------------------------------------------------

    /// <summary>
    /// ブロックコメント内の "function" キーワードが関数として誤検出されないことを確認する。
    /// MarkUncalledFunctionBodiesAsUncovered はブロックコメントをスキップするため、
    /// コメント内の function は補正対象にならず -1（ニュートラル）のままになるべき。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionKeywordInBlockComment_RemainsNeutral()
    {
        // ブロックコメント内の "function" が関数として誤検出されないことを確認する
        const string source = "/* function foo() {} */ var x = 1;";
        //                     0         1         2         3
        //                     01234567890123456789012345678901234

        // カバレッジデータなし（全体 -1 ニュートラル）
        var functions = new List<FunctionCoverage>();
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);

        // ブロックコメント内の "function" は検出されてはいけない
        // index 3 は 'f'（function の先頭）
        Assert.Equal(-1, map[3]);
    }

    /// <summary>
    /// テンプレートリテラル内の "function" キーワードが関数として誤検出されないことを確認する。
    /// MarkUncalledFunctionBodiesAsUncovered はテンプレートリテラルをスキップするため、
    /// バッククォート内の function は補正対象にならず -1（ニュートラル）のままになるべき。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionKeywordInTemplateLiteral_RemainsNeutral()
    {
        // テンプレートリテラル内の "function" が関数として誤検出されないことを確認する
        const string source = "var s = `function foo() {}`;";
        //                     0         1         2
        //                     0123456789012345678901234567

        // カバレッジデータなし（全体 -1 ニュートラル）
        var functions = new List<FunctionCoverage>();
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);

        // テンプレートリテラル内の "function" は検出されてはいけない
        // index 9 は 'f'（function の先頭）
        Assert.Equal(-1, map[9]);
    }

    /// <summary>
    /// 関数本体内にテンプレートリテラル補間 ${...} が含まれ、
    /// その補間式の中に波括弧（オブジェクトリテラル等）が入っている場合、
    /// FindMatchingBrace が関数の閉じ } を正しく見つけることを確認する。
    /// テンプレートリテラルスキャンは ` まで全文字を飛ばすため、
    /// ${ } 内の { } は深さカウントに影響しない。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunctionWithTemplateLiteralInterpolation_CorrectlyMarked()
    {
        // function f() { return `${obj.method({key: 'val'})}`; }
        //  0         1         2         3         4         5
        //  01234567890123456789012345678901234567890123456789012 3
        // function=0, {=13, `=22, ${...}=23-49, `=50, ;=51, ' '=52, }=53
        // source.Length = 54（index 0-53）
        const string source = "function f() { return `${obj.method({key: 'val'})}`; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // function キーワードの先頭は 0（未実行）
        Assert.Equal(0, map[0]); // 'f' of 'function'
        // 関数本体の開き { は 0（未実行）
        Assert.Equal(0, map[13]); // '{'
        // 関数の閉じ } は 0（未実行）
        // FindMatchingBrace のテンプレートリテラルスキャンが ${ } 内の { } を
        // 深さカウントに含めずスキップするため、正しく末尾 } を検出できていることを確認する
        Assert.Equal(0, map[53]); // '}' — 最後の文字（source.Length-1）
    }

    // -----------------------------------------------------------------------
    // ジェネレータ関数・async メソッド・ネスト関数の検出テスト
    // 新観点: function* / async method / 外側呼出し済み+内側未呼出し
    // -----------------------------------------------------------------------

    /// <summary>
    /// ジェネレータ関数 function* が未実行の場合、function キーワードと同様に
    /// 全体が未実行(0)としてマークされることを確認する。
    /// スキャナーは * をスキップしてから関数名・パラメータ・本体を検出する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledGeneratorFunction_AllMarkedAsUncovered()
    {
        // function* ジェネレータ関数。カバレッジデータなしなら全体が 0 になるべき
        const string source = "function* gen() { yield 1; }";
        //                     0         1         2
        //                     0123456789012345678901234567
        // function: 0-7, *: 8, ' ': 9, gen: 10-12, (: 13, ): 14, ' ': 15, {: 16, }: 27

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // function キーワードの先頭も 0（未実行）
        Assert.Equal(0, map[0]);  // 'f' of function*
        // 本体の { } も 0（未実行）
        Assert.Equal(0, map[16]); // {
        Assert.Equal(0, map[27]); // }
    }

    /// <summary>
    /// 外側関数が実行済みで内側関数が V8 未コンパイルの場合の既知の制限を確認する。
    /// 外側の coverage range が内側をカバーするため、MarkUncalledFunctionBodiesAsUncovered は
    /// 内側の map 値が -1 でないと判断して補正できない。内側は 1（緑）のまま残る。
    /// </summary>
    [Fact]
    public void BuildMap_OuterCalledInnerLazyCompiled_InnerBodyCoveredByOuterRange()
    {
        // 外側の range だけが CDP データに含まれ、内側は V8 が未コンパイルで含まれない想定
        const string source = "function outer() { function inner() { return 1; } return 2; }";
        //                     0         1         2         3         4         5         6
        //                     0123456789012345678901234567890123456789012345678901234567890
        // outer { : 17, inner function: 19, inner {: 36, inner }: 48, outer }: 60

        var functions = new List<FunctionCoverage>
        {
            // 外側のみ実行済み（内側は V8 未コンパイルとして CDP データに含まれない）
            new FunctionCoverage("outer", new List<CoverageRange>
            {
                new CoverageRange(0, source.Length, 1),
            }),
        };
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);

        // 外側の本体は 1（緑）
        Assert.Equal(1, map[17]); // { of outer

        // 既知の制限: inner の { は外側の count=1 で覆われるため 1 のまま
        // MarkUncalledFunctionBodiesAsUncovered は map[funcStart] == -1 のみ処理するので
        // inner の未実行は検出できない（false negative）
        Assert.Equal(1, map[36]); // { of inner — 外側 range に含まれるため 1 のまま
    }

    /// <summary>
    /// 未実行の async メソッド短縮構文（async greet() {}）で、
    /// メソッド本体が正しく 0（未実行）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledAsyncMethodShorthand_BodyMarkedAsUncovered()
    {
        // async メソッド short hand: async キーワードに続くメソッド名が未実行の場合
        const string source = "const obj = { async greet() { return 1; } };";
        //                     0         1         2         3         4
        //                     01234567890123456789012345678901234567890123
        // async: 14-18, ' ': 19, greet: 20-24, (: 25, ): 26, ' ': 27, {: 28, }: 40

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // greet の本体（{ } 内）は 0（未実行）になるべき
        Assert.Equal(0, map[20]); // 'g' of greet
        Assert.Equal(0, map[28]); // {
        Assert.Equal(0, map[40]); // }
    }

    /// <summary>
    /// 未実行の async メソッド短縮構文では async キーワードも 0 になるべきだが、
    /// 現在の実装では async キーワードが -1（ニュートラル）のまま残る既知のギャップ。
    /// （async function の場合は修正済みだが、async method shorthand は未対応）
    /// </summary>
    [Fact]
    public void BuildMap_UncalledAsyncMethodShorthand_AsyncKeywordAlsoMarked()
    {
        const string source = "const obj = { async greet() { return 1; } };";
        // async: 14-18, greet: 20-24, {: 28, }: 40

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // async キーワードも 0（未実行・赤）になるべき — 現状は -1 のまま
        Assert.Equal(0, map[14]); // 'a' of async
    }

    /// <summary>
    /// アロー関数のマーク範囲のオフバイワン回帰テスト。
    /// FindMatchingBrace は } の次のインデックスを返す（例: } が index 28 なら戻り値は 29）。
    /// マーク処理のループを "m &lt; braceEnd" とすることで } 自体（index 28）までをマークし、
    /// } の直後の文字（index 29 の ; など）をニュートラル（-1）のまま保つことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledArrowFunction_CharAfterClosingBraceRemainsNeutral()
    {
        const string source = "const f = () => { return 1; };";
        //                     0         1         2
        //                     012345678901234567890123456789
        // => at 13, { at 16, } at 28, ; at 29

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // } 自体は 0（未実行）でなければならない
        Assert.Equal(0, map[28]); // }

        // } の直後の ; は arrow function の外側 → -1（ニュートラル）でなければならない
        // ループを m <= braceEnd にすると map[29] = 0（赤）になってしまうため、回帰防止のアサート
        Assert.Equal(-1, map[29]); // ';' after '}'
    }

    /// <summary>
    /// count が 1 より大きい実行回数も「実行済み（1）」として扱われることを確認する。
    /// 実行回数の多寡は問わず、1回以上なら covered とみなす。
    /// </summary>
    [Fact]
    public void BuildMap_HighCountRange_TreatedAsCovered()
    {
        // count=100 でも「実行済み」として map 値が 1 になるべき
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(0, 3, 100)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("abc", functions);
        Assert.Equal([1, 1, 1], map);
    }

    // -----------------------------------------------------------------------
    // Bug fix: IsRegexStart — return / typeof など JS キーワード後の正規表現誤認識
    // 修正前: キーワードの末尾が識別子文字のため IsRegexStart が false を返し
    //         FindMatchingBrace が正規表現内の } を関数終端と誤認識していた
    // -----------------------------------------------------------------------

    /// <summary>
    /// 未実行の関数本体に "return /[}]/;" が含まれる場合でも、
    /// 正規表現内の '}' を関数の閉じ波括弧と誤認識せず、
    /// 関数全体（function キーワードから最終 '}' まで）が 0（未実行）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_ReturnRegexWithBrace_FullBodyMarkedUncovered()
    {
        // function foo() { return /[}]/; }
        // 修正前は /[}]/ 内の '}' で FindMatchingBrace が早期終了し、
        // "/ }" の部分が -1（ニュートラル）のまま残っていた
        const string source = "function foo() { return /[}]/; }";
        //                     0         1         2         3
        //                     01234567890123456789012345678901
        // function: 0, {: 15 ... }: 31

        // カバレッジデータなし → V8 未コンパイル → 全文字が未実行(0)になるべき
        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 関数全体が未実行（0）になっているか確認する
        Assert.Equal(0, map[0]);  // 'f' of function
        Assert.Equal(0, map[15]); // '{'
        Assert.Equal(0, map[31]); // '}' （最後の閉じ波括弧）
        // 修正前にここが -1 になっていた: 正規表現内の } の直後以降
        Assert.Equal(0, map[29]); // ';' after /[}]/
        Assert.Equal(0, map[30]); // ' ' before final }
    }

    /// <summary>
    /// typeof 演算子の後に正規表現リテラルが来る場合でも、
    /// 正規表現内の '}' を関数終端と誤認識しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_TypeofRegexWithBrace_FullBodyMarkedUncovered()
    {
        // function foo() { if (typeof /[{}]/ === "object") { } }
        const string source = "function foo() { var t = typeof /[{}]/; }";
        //                     0         1         2         3         4
        //                     01234567890123456789012345678901234567890

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 関数本体の最後の } (index 40) が 0 になっていれば、正規表現を正しくスキップできている
        Assert.Equal(0, map[40]); // '}' of function body
    }

    // -----------------------------------------------------------------------
    // Bug fix: SkipBalancedParens — コメント内の括弧による深さカウントのずれ
    // 修正前: // コメントや /* */ コメント内の ( ) をカウントしてしまい、
    //         パラメータリストの終端 ')' を正しく検出できなかった
    // -----------------------------------------------------------------------

    /// <summary>
    /// 関数パラメータリスト内に行コメント // があり、
    /// コメント中に '(' が含まれる場合でも、関数本体が正しく 0（未実行）になることを確認する。
    /// 修正前はコメント内の '(' が深さカウントに加算され、パラメータリストの終端を見失っていた。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_ParamWithLineCommentContainingParen_BodyMarkedUncovered()
    {
        // function foo(a, // default (optional)
        //              b) { return a + b; }
        // 行コメント内の '(' がパラメータ深さカウントに影響してはいけない
        const string source = "function foo(a, // (opt)\n b) { return 1; }";
        //                     0         1         2         3         4
        //                     012345678901234567890123456789012345678901

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 関数本体（{ } 内）が 0（未実行）になっているか確認する
        // 修正前は // (opt) 内の ( でカウントがずれ、} を見失って -1 のままになっていた
        Assert.Equal(0, map[0]);  // 'f' of function
        // { は \n の後なので index を数える: "function foo(a, // (opt)\n b) " = 30 chars → { は 29
        int braceOpen = source.IndexOf('{');
        int braceClose = source.LastIndexOf('}');
        Assert.Equal(0, map[braceOpen]);  // '{'
        Assert.Equal(0, map[braceClose]); // '}'
    }

    /// <summary>
    /// 関数パラメータリスト内にブロックコメント /* */ があり、
    /// コメント中に ')' が含まれる場合でも、関数本体が正しく 0（未実行）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_ParamWithBlockCommentContainingParen_BodyMarkedUncovered()
    {
        // function foo(a /* default: bar() */, b) { return 1; }
        const string source = "function foo(a /* bar() */, b) { return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        int braceOpen  = source.IndexOf('{');
        int braceClose = source.LastIndexOf('}');
        Assert.Equal(0, map[0]);          // 'f' of function
        Assert.Equal(0, map[braceOpen]);  // '{'
        Assert.Equal(0, map[braceClose]); // '}'
    }

    // -----------------------------------------------------------------------
    // CoverageRange 境界値テスト（既存テストが未カバーの観点のみ）
    // -----------------------------------------------------------------------

    /// <summary>
    /// EndOffset &lt; StartOffset という逆順レンジでも例外が発生せず、
    /// クランプ処理により何もマークされないことを確認する。
    /// start=5, end=Math.Min(2, sourceLen) → start &gt;= end のためループ不実行。
    /// </summary>
    [Fact]
    public void BuildMap_InvertedRange_NoExceptionAndNoEffect()
    {
        // EndOffset(2) < StartOffset(5) の逆順レンジ
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(5, 2, 1)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("hello", functions);

        // クランプ後に start(5) >= end(2) になるためループが回らず全文字 -1 のまま
        Assert.Equal([-1, -1, -1, -1, -1], map);
    }

    // -----------------------------------------------------------------------
    // JavaScript 構成・実行パターン観点のテスト
    // クラス構文 / 関数式 / IIFE / アロー関数バリエーション /
    // ジェネレータメソッド / export / 制御構文誤検出 / 壊れたソース
    // -----------------------------------------------------------------------

    /// <summary>
    /// クラス内のメソッド（メソッド短縮構文）が未実行の場合、
    /// メソッド本体が 0（未実行）としてマークされることを確認する。
    /// class キーワードや Foo 識別子は '(' を持たないため誤検出されない。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledClassMethod_MarkedAsUncovered()
    {
        // class Foo { method() {} }
        //  0         1         2
        //  0123456789012345678901234
        // m=12, (=18, {=21, }=22, outer }=24
        const string source = "class Foo { method() {} }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // メソッド本体（m から } まで）が 0（未実行）であることを確認する
        Assert.Equal(0, map[12]); // 'm' of method
        Assert.Equal(0, map[21]); // {
        Assert.Equal(0, map[22]); // }
        // クラスの外側の } はメソッドの範囲外なので -1 のまま
        Assert.Equal(-1, map[24]); // outer '}' of class
    }

    /// <summary>
    /// static メソッドが未実行の場合、static キーワード自体はマークされず、
    /// メソッド名以降の本体が 0（未実行）としてマークされることを確認する。
    /// static は ControlFlowKeywords にないが、直後に '(' がないため誤検出されない。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledStaticMethod_MethodBodyMarkedAsUncovered()
    {
        // class Foo { static run() {} }
        //  0         1         2
        //  01234567890123456789012345678
        // s=12 (static), r=19 (run), (=22, {=25, }=26, outer }=28
        const string source = "class Foo { static run() {} }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // static キーワードは '(' を持たないため誤検出されない → -1 のまま
        Assert.Equal(-1, map[12]); // 's' of static
        // run メソッドの先頭から } まで 0（未実行）になっていることを確認する
        Assert.Equal(0, map[19]); // 'r' of run
        Assert.Equal(0, map[25]); // {
        Assert.Equal(0, map[26]); // }
    }

    /// <summary>
    /// getter 構文（get prop() {}）が未実行の場合、
    /// get キーワード自体はマークされず、プロパティ名以降の本体が 0（未実行）になることを確認する。
    /// get は直後に '(' がないため誤検出されない。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledGetter_BodyMarkedAsUncovered()
    {
        // class Foo { get value() {} }
        //  0         1         2
        //  0123456789012345678901234567
        // g=12 (get), v=16 (value), (=21, {=24, }=25, outer }=27
        const string source = "class Foo { get value() {} }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // get キーワードは '(' を持たないため誤検出されない → -1 のまま
        Assert.Equal(-1, map[12]); // 'g' of get
        // value プロパティの先頭から } まで 0（未実行）になっていることを確認する
        Assert.Equal(0, map[16]); // 'v' of value
        Assert.Equal(0, map[24]); // {
        Assert.Equal(0, map[25]); // }
    }

    /// <summary>
    /// extends を使ったクラスでメソッドが未実行の場合、
    /// extends / Parent 識別子はマークされず、メソッド本体だけが 0（未実行）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledClassWithExtends_MethodMarkedAsUncovered()
    {
        // class Child extends Parent { greet() {} }
        //  0         1         2         3         4
        //  01234567890123456789012345678901234567890
        // e=12 (extends), P=20 (Parent), g=29 (greet), (=34, {=37, }=38, outer }=40
        const string source = "class Child extends Parent { greet() {} }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // extends と Parent は '(' を持たないため誤検出されない → -1 のまま
        Assert.Equal(-1, map[12]); // 'e' of extends
        Assert.Equal(-1, map[20]); // 'P' of Parent
        // greet メソッドの先頭から } まで 0（未実行）になっていることを確認する
        Assert.Equal(0, map[29]); // 'g' of greet
        Assert.Equal(0, map[37]); // {
        Assert.Equal(0, map[38]); // }
    }

    /// <summary>
    /// 名前付き関数式（named function expression）が未実行の場合、
    /// function キーワードの先頭から関数本体 } まで 0（未実行）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledNamedFunctionExpression_MarkedAsUncovered()
    {
        // var f = function named() { return 1; }
        //  0         1         2         3
        //  01234567890123456789012345678901234567
        // function=8, named=17, (=22, {=25, }=37
        const string source = "var f = function named() { return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // function キーワードから } まで 0（未実行）であることを確認する
        Assert.Equal(0, map[8]);  // 'f' of function
        Assert.Equal(0, map[25]); // {
        Assert.Equal(0, map[37]); // }
        // = の前後の空白は範囲外なので -1 のまま
        Assert.Equal(-1, map[7]); // ' ' before function
    }

    /// <summary>
    /// オブジェクトプロパティ値の関数式（function expression as property value）が未実行の場合、
    /// function キーワードの先頭から関数本体 } まで 0（未実行）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledObjectPropertyFunction_MarkedAsUncovered()
    {
        // var o = { foo: function() { return 1; } }
        //  0         1         2         3         4
        //  01234567890123456789012345678901234567890
        // function=15, (=23, {=26, }=38
        const string source = "var o = { foo: function() { return 1; } }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // function キーワードから } まで 0（未実行）であることを確認する
        Assert.Equal(0, map[15]); // 'f' of function
        Assert.Equal(0, map[26]); // {
        Assert.Equal(0, map[38]); // }
        // : の前後（プロパティ名部分）は -1 のまま
        Assert.Equal(-1, map[14]); // ' ' before function
    }

    /// <summary>
    /// IIFE（即時実行関数式）はカバレッジデータがある（実行済み）場合、
    /// MarkUncalledFunctionBodiesAsUncovered による再マークの対象外になることを確認する。
    /// map[funcStart] == 1 のため補正されない。
    /// </summary>
    [Fact]
    public void BuildMap_CalledIIFE_NotReMarkedAsUncovered()
    {
        // (function() { return 1; })()
        //  0         1         2
        //  0123456789012345678901234567
        // function=1, (=9, {=12, }=23
        const string source = "(function() { return 1; })()";

        // 全体を実行済み（count=1）にする
        var functions = new List<FunctionCoverage>
        {
            new FunctionCoverage("", new List<CoverageRange>
            {
                new CoverageRange(0, source.Length, 1),
            }),
        };
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);

        // IIFE は実行済みなので map[funcStart] != -1 → 補正されない → 1 のまま
        Assert.Equal(1, map[1]);  // 'f' of function
        Assert.Equal(1, map[12]); // {
        Assert.Equal(1, map[23]); // }
    }

    /// <summary>
    /// async アロー関数（async () => { }）が未実行の場合、
    /// => の位置からブロック本体 } まで、および async キーワードが 0（未実行）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledAsyncArrowFunction_BlockBodyMarkedFromArrow()
    {
        // const f = async () => { return 1; }
        //  0         1         2         3
        //  01234567890123456789012345678901234
        // async=10, (=16, )=17, =>: ==19 >=20, {=22, }=34
        const string source = "const f = async () => { return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // => から } まで 0（未実行）になっていることを確認する
        Assert.Equal(0, map[19]); // '=' of =>
        Assert.Equal(0, map[22]); // {
        Assert.Equal(0, map[34]); // }
        // async キーワードも 0 になる
        Assert.Equal(0, map[10]); // 'a' of async
    }

    /// <summary>
    /// ネストされたアロー関数が未実行の場合、
    /// 外側アロー関数の => から最後の } まで全体が 0（未実行）になることを確認する。
    /// FindMatchingBrace が外側の対応する } を返すため、内側も一括でマークされる。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledNestedArrowFunctions_BothMarkedAsUncovered()
    {
        // const f = () => { const g = () => { return 1; }; }
        //  0         1         2         3         4
        //  01234567890123456789012345678901234567890123456789
        // outer =>: ==13 >=14, outer {=16, inner =>: ==31 >=32, inner {=34, inner }=46, outer }=49
        const string source = "const f = () => { const g = () => { return 1; }; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 外側 => から最外 } まで（インデックス 13〜49）が 0 になっていることを確認する
        Assert.Equal(0, map[13]); // '=' of outer =>
        Assert.Equal(0, map[16]); // outer {
        Assert.Equal(0, map[31]); // '=' of inner =>
        Assert.Equal(0, map[34]); // inner {
        Assert.Equal(0, map[46]); // inner }
        Assert.Equal(0, map[49]); // outer }
    }

    /// <summary>
    /// 同一行に複数のアロー関数（ブロック本体）がある場合、
    /// それぞれが独立して 0（未実行）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledMultipleArrowsOnSameLine_BothMarkedAsUncovered()
    {
        // const a = () => { return 1; }; const b = () => { return 2; };
        //  0         1         2         3         4         5         6
        //  0123456789012345678901234567890123456789012345678901234567890
        // first =>: ==13, first {=16, first }=28, second =>: ==44, second {=47, second }=59
        const string source = "const a = () => { return 1; }; const b = () => { return 2; };";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 最初のアロー関数（13〜28）が 0 になっていることを確認する
        Assert.Equal(0, map[13]); // '=' of first =>
        Assert.Equal(0, map[16]); // first {
        Assert.Equal(0, map[28]); // first }
        // 2番目のアロー関数（44〜59）が 0 になっていることを確認する
        Assert.Equal(0, map[44]); // '=' of second =>
        Assert.Equal(0, map[47]); // second {
        Assert.Equal(0, map[59]); // second }
        // 2つのアロー関数の間（; とスペース）は -1 のまま
        Assert.Equal(-1, map[29]); // ';' after first }
        Assert.Equal(-1, map[30]); // ' ' before const b
    }

    /// <summary>
    /// ジェネレータメソッド短縮構文（*gen() {}）が未実行の場合、
    /// * 自体は識別子文字でないため -1 のまま残り、
    /// gen の先頭からメソッド本体 } までが 0（未実行）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledGeneratorMethodShorthand_BodyMarkedAsUncovered()
    {
        // const obj = { *gen() { yield 1; } }
        //  0         1         2         3
        //  01234567890123456789012345678901234
        // *=14, g=15 (gen), (=18, {=21, }=32, outer }=34
        const string source = "const obj = { *gen() { yield 1; } }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // * は識別子文字でないため -1 のまま残ることを確認する
        Assert.Equal(-1, map[14]); // '*' of *gen
        // gen の先頭から } まで 0（未実行）になっていることを確認する
        Assert.Equal(0, map[15]); // 'g' of gen
        Assert.Equal(0, map[21]); // {
        Assert.Equal(0, map[32]); // }
    }

    /// <summary>
    /// export function が未実行の場合、
    /// function キーワードの先頭から関数本体 } まで 0（未実行）になることを確認する。
    /// export キーワード自体は '(' を持たないため誤検出されない。
    /// </summary>
    [Fact]
    public void BuildMap_ExportFunction_MarkedAsUncovered()
    {
        // export function foo() { return 1; }
        //  0         1         2         3
        //  01234567890123456789012345678901234
        // function=7, f=16 (foo), (=19, {=22, }=34
        const string source = "export function foo() { return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // function キーワードから } まで 0（未実行）であることを確認する
        Assert.Equal(0, map[7]);  // 'f' of function
        Assert.Equal(0, map[22]); // {
        Assert.Equal(0, map[34]); // }
        // export キーワードは '(' を持たないため -1 のまま
        Assert.Equal(-1, map[6]); // ' ' before function
    }

    /// <summary>
    /// export default function（匿名）が未実行の場合、
    /// function キーワードの先頭から関数本体 } まで 0（未実行）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_ExportDefaultFunction_MarkedAsUncovered()
    {
        // export default function() { return 1; }
        //  0         1         2         3
        //  0123456789012345678901234567890123456789
        // function=15, (=23, {=26, }=38
        const string source = "export default function() { return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // function キーワードから } まで 0（未実行）であることを確認する
        Assert.Equal(0, map[15]); // 'f' of function
        Assert.Equal(0, map[26]); // {
        Assert.Equal(0, map[38]); // }
        // default の直後の空白は -1 のまま
        Assert.Equal(-1, map[14]); // ' ' before function
    }

    /// <summary>
    /// try { } ブロックはパラメータ括弧 '(' を持たないため、
    /// メソッド短縮構文として誤検出されず -1（ニュートラル）のままになることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_TryBlock_RemainsNeutral()
    {
        // try { throw 1; }
        //  0         1
        //  0123456789012345
        // {=4, }=15
        const string source = "try { throw 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // try ブロックは method shorthand でないため -1 のまま
        Assert.Equal(-1, map[4]);  // '{'
        Assert.Equal(-1, map[15]); // '}'
    }

    /// <summary>
    /// else { } ブロックはパラメータ括弧 '(' を持たないため、
    /// メソッド短縮構文として誤検出されず -1（ニュートラル）のままになることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_ElseBlock_RemainsNeutral()
    {
        // if (x) {} else { throw 1; }
        //  0         1         2
        //  012345678901234567890123456
        // else {=15, else }=26
        const string source = "if (x) {} else { throw 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // else ブロックは '(' を持たないため method shorthand でない → -1 のまま
        Assert.Equal(-1, map[15]); // '{' of else block
        Assert.Equal(-1, map[26]); // '}' of else block
    }

    /// <summary>
    /// with 文（with(obj){}）は ControlFlowKeywords に含まれるため、
    /// メソッド短縮構文として誤検出されないことを確認する。
    /// （以前は既知の false positive だったが、ControlFlowKeywords に追加して修正済み）
    /// </summary>
    [Fact]
    public void BuildMap_WithStatement_NotFalsePositive_Fixed()
    {
        // with (obj) { x = 1; }
        //  0         1         2
        //  012345678901234567890
        // w=0 (with), {=11, }=20
        const string source = "with (obj) { x = 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 修正済み: with は ControlFlowKeywords に含まれるため -1（対象外）のまま
        Assert.Equal(-1, map[0]);  // 'w' of with
        Assert.Equal(-1, map[11]); // '{'
        Assert.Equal(-1, map[20]); // '}'
    }

    /// <summary>
    /// 閉じ波括弧 '}' のないソースコード（構文が壊れている場合）でも、
    /// 例外を発生させずに安全に処理されることを確認する。
    /// FindMatchingBrace が -1 を返し条件チェックで除外されるため何もマークされない。
    /// </summary>
    [Fact]
    public void BuildMap_MissingClosingBrace_NoExceptionAllNeutral()
    {
        // 閉じ } がない不完全なソースコード
        const string source = "function foo() { return 1;";

        // 例外が発生しないことと、全文字が -1 のままであることを確認する
        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // FindMatchingBrace が -1 を返し、funcEnd > 0 の条件が偽になるため何もマークされない
        Assert.All(map, v => Assert.Equal(-1, v));
    }

    // -----------------------------------------------------------------------
    // FindMatchingBrace の文字列・コメント内 {} スキップの確認テスト
    // 誤った } でブレースが早期クローズされないことを検証する
    // -----------------------------------------------------------------------

    /// <summary>
    /// 関数本体内の単一引用符文字列に '}' が含まれる場合、
    /// FindMatchingBrace がその '}' を関数終端と誤認識しないことを確認する。
    /// 単一引用符の文字列スキップにより、文字列内の '}' は深さカウントに影響しない。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionBody_SingleQuotedCloseBrace_NotClosedEarly()
    {
        // function foo() { var s = '}'; return 1; }
        //  0         1         2         3         4
        //  01234567890123456789012345678901234567890
        // {=15（開き）, '}'=26（文字列内）, }=40（真の閉じ）
        const string source = "function foo() { var s = '}'; return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 文字列内の '}' は関数終端でないため、全体（0〜40）が 0（未実行）になるべき
        Assert.Equal(0, map[0]);  // 'f' of function
        Assert.Equal(0, map[15]); // opening '{'
        Assert.Equal(0, map[40]); // true closing '}'
    }

    /// <summary>
    /// 関数本体内の二重引用符文字列に '}' が含まれる場合、
    /// FindMatchingBrace がその '}' を関数終端と誤認識しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionBody_DoubleQuotedCloseBrace_NotClosedEarly()
    {
        // function foo() { var s = "}"; return 1; }
        //  0         1         2         3         4
        //  01234567890123456789012345678901234567890
        // {=15（開き）, "}"=25〜27（文字列）, }=40（真の閉じ）
        const string source = "function foo() { var s = \"}\"; return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        Assert.Equal(0, map[0]);  // 'f' of function
        Assert.Equal(0, map[15]); // opening '{'
        Assert.Equal(0, map[40]); // true closing '}'
    }

    /// <summary>
    /// 関数本体内のブロックコメント /* */ に '}' が含まれる場合、
    /// FindMatchingBrace がその '}' を関数終端と誤認識しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionBody_BlockCommentCloseBrace_NotClosedEarly()
    {
        // function foo() { /* } */ return 1; }
        //  0         1         2         3
        //  0123456789012345678901234567890123456
        // {=15, /* } */ にある }=20（コメント内）, }=35（真の閉じ）
        const string source = "function foo() { /* } */ return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        Assert.Equal(0, map[0]);  // 'f' of function
        Assert.Equal(0, map[35]); // true closing '}'
    }

    /// <summary>
    /// 関数本体内の行コメント // に '{' が含まれる場合、
    /// FindMatchingBrace がその '{' を深さカウントに加算しないことを確認する。
    /// 深さカウントが狂うと、関数の真の '}' を見落とす可能性がある。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionBody_LineCommentOpenBrace_DepthNotAffected()
    {
        // "function foo() {\n// {\nreturn 1;\n}"
        //  {=15, // { → コメント内の {、真の閉じ }=32
        const string source = "function foo() {\n// {\nreturn 1;\n}";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        Assert.Equal(0, map[0]);  // 'f' of function
        Assert.Equal(0, map[32]); // true closing '}'
    }

    // -----------------------------------------------------------------------
    // メインスキャナーの文字列スキップ確認テスト（単引用符）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 単一引用符文字列内の "function" キーワードが関数として誤検出されないことを確認する。
    /// 二重引用符のテスト（BuildMap_FunctionInStringLiteral_RemainsNeutral）と対になる。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionInSingleQuoteString_RemainsNeutral()
    {
        // 単引用符文字列内の function はスキップされるため補正の対象にならない
        const string source = "var s = 'function foo() { return 1; }';";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 単引用符内なので全体が -1（ニュートラル）のままであることを確認する
        Assert.All(map, v => Assert.Equal(-1, v));
    }

    /// <summary>
    /// 文字列リテラル内の "=>" が本物のアロー関数と誤認識されないことを確認する。
    /// 文字列スキップにより、文字列内の => は補正対象にならない。
    /// 後続の本物のアロー関数（ブロック本体）は正しく 0（未実行）にマークされる。
    /// </summary>
    [Fact]
    public void BuildMap_ArrowInStringLiteral_RemainsNeutral_RealArrowMarked()
    {
        // const s = "() => {}"; const f = () => { return 1; };
        //  0         1         2         3         4         5
        //  0123456789012345678901234567890123456789012345678901
        // "() => {}" は index 10〜19 の二重引用符内（=14 は文字列内）
        // 本物の => は index 35（= のインデックス）
        const string source = "const s = \"() => {}\"; const f = () => { return 1; };";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 文字列内の '=' は -1 のまま
        Assert.Equal(-1, map[14]); // '=' inside "() => {}" — not processed
        // 本物のアロー関数（=> から } まで）は 0 になる
        Assert.Equal(0, map[35]); // '=' of real =>
        Assert.Equal(0, map[38]); // {
        Assert.Equal(0, map[50]); // }
    }

    // -----------------------------------------------------------------------
    // SkipBalancedParens — 単引用符内の ')' スキップ確認テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 関数パラメータ内の単一引用符文字列に ')' が含まれる場合、
    /// SkipBalancedParens がその ')' をパラメータ終端と誤認識しないことを確認する。
    /// 修正済みの行コメント・ブロックコメントと同様の確認。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_ParamWithSingleQuotedParen_BodyMarkedUncovered()
    {
        // function foo(a = ')', b) { return 1; }
        //  0         1         2         3
        //  01234567890123456789012345678901234567
        // params: a = ')' → ')' は単引用符内なので SkipBalancedParens は無視する
        const string source = "function foo(a = ')', b) { return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        int braceOpen  = source.IndexOf('{');
        int braceClose = source.LastIndexOf('}');
        // 関数全体が 0（未実行）になっていることを確認する
        Assert.Equal(0, map[0]);           // 'f' of function
        Assert.Equal(0, map[braceOpen]);   // '{'
        Assert.Equal(0, map[braceClose]);  // '}'
    }

    // -----------------------------------------------------------------------
    // SkipRegexLiteral のエッジケース確認テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 正規表現リテラル /\// （エスケープされたスラッシュ）を含む関数本体で、
    /// SkipRegexLiteral が \/ をエスケープとして処理し正規表現を正しく終端することを確認する。
    /// エスケープを誤処理した場合、2番目の / で終端してしまい、3番目の / が
    /// 除算または新たな正規表現として誤処理される可能性がある。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionBody_RegexWithEscapedSlash_CorrectlySkipped()
    {
        // function foo() { var re = /\//; return 1; }
        //  0         1         2         3         4
        //  01234567890123456789012345678901234567890123
        // /\// は JS のバックスラッシュエスケープ済み /。}=42 が真の閉じ
        const string source = "function foo() { var re = /\\//; return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // SkipRegexLiteral が \/ を正しくスキップし、真の } で関数が終わる → 全体 0
        Assert.Equal(0, map[0]);                        // 'f' of function
        Assert.Equal(0, map[source.Length - 1]);        // true closing '}'
    }

    /// <summary>
    /// 正規表現リテラル /[/]/ （文字クラス内にスラッシュ）を含む関数本体で、
    /// SkipRegexLiteral が文字クラス内の / を正規表現終端と誤認識しないことを確認する。
    /// inCharClass フラグにより [ ] 内の / は終端として扱われない。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionBody_RegexCharClassWithSlash_CorrectlySkipped()
    {
        // function foo() { var re = /[/]/; return 1; }
        //  0         1         2         3         4
        //  0123456789012345678901234567890123456789012345
        // /[/]/ の文字クラス [/] 内の / は終端ではない。}=43 が真の閉じ
        const string source = "function foo() { var re = /[/]/; return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 文字クラス内の / を正しくスキップし、真の } で関数が終わる → 全体 0
        Assert.Equal(0, map[0]);                        // 'f' of function
        Assert.Equal(0, map[source.Length - 1]);        // true closing '}'
    }

    // -----------------------------------------------------------------------
    // アロー関数の追加パターン確認テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 括弧なし単一パラメータのアロー関数（x => { }）が未実行の場合、
    /// => の位置からブロック本体 } まで 0（未実行）になることを確認する。
    /// 既存テストは全て () => {} 形式のため、括弧なしパラメータは未テスト。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledArrowFunction_SingleBareParam_MarkedAsUncovered()
    {
        // const f = x => { return x; }
        //  0         1         2
        //  012345678901234567890123456789
        // x=10（パラメータ）, =>(==12, >=13), {=15, }=27
        // 代入の = が index 8 にあるが source[9]='x'（'>'でない）なので => でない
        const string source = "const f = x => { return x; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // => の位置から } まで 0（未実行）になっていることを確認する
        Assert.Equal(0, map[12]); // '=' of =>
        Assert.Equal(0, map[15]); // {
        Assert.Equal(0, map[27]); // }
        // パラメータ x と代入の = は補正対象外なので -1 のまま
        Assert.Equal(-1, map[10]); // 'x' (parameter)
        Assert.Equal(-1, map[8]);  // '=' of assignment
    }

    // -----------------------------------------------------------------------
    // メソッド短縮構文の追加パターン確認テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// setter 構文（set prop(v) {}）が未実行の場合、
    /// set キーワード自体は -1 のまま、プロパティ名以降の本体が 0（未実行）になることを確認する。
    /// get と同様に set は '(' を持たないため誤検出されない。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledSetter_BodyMarkedAsUncovered()
    {
        // const obj = { set value(v) {} }
        //  0         1         2         3
        //  0123456789012345678901234567890
        // s=14（set）, v=18（value）, (=23, {=27, }=28, outer }=30
        const string source = "const obj = { set value(v) {} }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // set キーワードは '(' を持たないため -1 のまま
        Assert.Equal(-1, map[14]); // 's' of set
        // value プロパティの先頭から } まで 0（未実行）になっていることを確認する
        Assert.Equal(0, map[18]); // 'v' of value
        Assert.Equal(0, map[27]); // {
        Assert.Equal(0, map[28]); // }
    }

    /// <summary>
    /// オブジェクトのメソッド名が "async" の場合（async キーワードとしてではなくメソッド名として）、
    /// async() {} がメソッド短縮構文として検出され 0（未実行）になることを確認する。
    /// これは "async greet() {}" の async キーワード（SKIP テスト）とは別の確認である。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledMethodNamedAsync_MarkedAsUncovered()
    {
        // const obj = { async() { return 1; } }
        //  0         1         2         3
        //  0123456789012345678901234567890123456
        // a=14（async メソッド名）, (=19, {=22, }=34, outer }=36
        const string source = "const obj = { async() { return 1; } }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // async メソッド名（識別子として）の先頭から } まで 0（未実行）になっていることを確認する
        Assert.Equal(0, map[14]); // 'a' of async (as method name)
        Assert.Equal(0, map[22]); // {
        Assert.Equal(0, map[34]); // }
    }

    /// <summary>
    /// 計算プロパティメソッド（["method"]() {}）は識別子が角括弧で囲まれているため、
    /// TryMarkMethodShorthand の対象にならず -1（ニュートラル）のままになることを確認する。
    /// これは既知の制限（false negative）として文書化する。
    /// </summary>
    [Fact]
    public void BuildMap_ComputedPropertyMethod_RemainsNeutral_KnownLimitation()
    {
        // 計算プロパティメソッド: [ で始まるため識別子の先頭条件を満たさない
        const string source = "const obj = { [\"method\"]() { return 1; } }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 計算プロパティメソッドは検出されないため全体が -1（ニュートラル）のまま
        // 既知の制限: function キーワードや通常の identifier() {} でなければ補正できない
        Assert.All(map, v => Assert.Equal(-1, v));
    }

    // -----------------------------------------------------------------------
    // ネスト関数（両方未呼び出し）の確認テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 外側関数と内側関数の両方がカバレッジデータにない（どちらも未呼び出し）場合、
    /// ソース全体が 0（未実行）としてマークされることを確認する。
    /// 外側関数の FindMatchingBrace が内側の {} も含めた全体を正しくスキャンし、
    /// 外側の funcEnd まで一括でマークする動作を確認する。
    /// </summary>
    [Fact]
    public void BuildMap_BothOuterAndInnerUncalled_EntireSourceMarkedAsUncovered()
    {
        // function outer() { function inner() { return 1; } return 2; }
        //  0         1         2         3         4         5         6
        //  0123456789012345678901234567890123456789012345678901234567890
        // outer {=17, inner function=19, inner {=36, inner }=48, outer }=60
        const string source = "function outer() { function inner() { return 1; } return 2; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 外側の FindMatchingBrace が depth=2 を経由して outer } で終端し全体をマーク
        Assert.All(map, v => Assert.Equal(0, v));
    }

    // -----------------------------------------------------------------------
    // MergeMaps — OR 合成のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 両方が実行済み（1）の場合、合成結果も実行済み（1）になることを確認する。
    /// </summary>
    [Fact]
    public void MergeMaps_BothCovered_ReturnsCovered()
    {
        // 両方 1 → 合成も 1
        var merged = HtmlReportGenerator.MergeMaps([1, 1, 1], [1, 1, 1]);
        Assert.Equal([1, 1, 1], merged);
    }

    /// <summary>
    /// 一方が実行済み（1）、他方が未実行（0）の場合、
    /// OR 合成なので実行済み（1）になることを確認する。
    /// </summary>
    [Fact]
    public void MergeMaps_OneCoveredOneUncovered_ReturnsCovered()
    {
        // OR 合成: どちらかが 1 なら 1
        var merged = HtmlReportGenerator.MergeMaps([1, 0, -1], [0, 1, -1]);
        Assert.Equal([1, 1, -1], merged);
    }

    /// <summary>
    /// 両方が未実行（0）の場合、合成結果も未実行（0）になることを確認する。
    /// </summary>
    [Fact]
    public void MergeMaps_BothUncovered_ReturnsUncovered()
    {
        var merged = HtmlReportGenerator.MergeMaps([0, 0], [0, 0]);
        Assert.Equal([0, 0], merged);
    }

    /// <summary>
    /// 一方が未実行（0）、他方が対象外（-1）の場合、未実行（0）になることを確認する。
    /// 対象外ではなく未実行という情報を優先する。
    /// </summary>
    [Fact]
    public void MergeMaps_OneUncoveredOneNeutral_ReturnsUncovered()
    {
        var merged = HtmlReportGenerator.MergeMaps([0, -1], [-1, 0]);
        Assert.Equal([0, 0], merged);
    }

    /// <summary>
    /// otherMap が baseMap より短い場合、はみ出た部分は -1（対象外）として扱い
    /// baseMap の値をそのまま使うことを確認する。
    /// </summary>
    [Fact]
    public void MergeMaps_OtherMapShorter_TreatedAsNeutral()
    {
        // baseMap = [0,0,0], otherMap = [1] (短い)
        // index0: base=0, other=1 → 1
        // index1: base=0, other=-1(範囲外) → 0
        // index2: base=0, other=-1(範囲外) → 0
        var merged = HtmlReportGenerator.MergeMaps([0, 0, 0], [1]);
        Assert.Equal([1, 0, 0], merged);
    }

    /// <summary>
    /// otherMap が baseMap より長い場合、超過部分は無視して baseMap の長さで結果を返すことを確認する。
    /// CDN スクリプトのバリアント等で長さが異なる場合の仕様として明文化するテスト。
    /// </summary>
    [Fact]
    public void MergeMaps_OtherMapLonger_ExtraElementsIgnored()
    {
        // baseMap = [0, 1], otherMap = [1, 0, 1, 1] (長い)
        // index0: base=0, other=1 → 1
        // index1: base=1, other=0 → 1
        // index2以降: baseMap に存在しないため結果に含まれない（baseMap 長さが上限）
        var merged = HtmlReportGenerator.MergeMaps([0, 1], [1, 0, 1, 1]);
        Assert.Equal(2, merged.Length);
        Assert.Equal([1, 1], merged);
    }

    /// <summary>
    /// 両方が対象外（-1）の場合、結果も対象外（-1）になることを確認する。
    /// </summary>
    [Fact]
    public void MergeMaps_BothNeutral_ReturnsNeutral()
    {
        var merged = HtmlReportGenerator.MergeMaps([-1, -1, -1], [-1, -1, -1]);
        Assert.Equal([-1, -1, -1], merged);
    }

    /// <summary>
    /// `function` をオブジェクトプロパティの「キー名」として使った場合（{ function: 1 }）、
    /// function キーワードとして誤検知せず、マップが変化しないことを確認する。
    /// JavaScript では `function` はプロパティ名として合法であり、関数本体を持たない。
    /// 検出ロジックは function の直後に '(' がなければスキップするため誤検知しない。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionUsedAsPropertyKey_NoFalsePositive()
    {
        // const o = { function: 1 };
        //  0         1         2
        //  0123456789012345678901234
        // 'function' は index 12 から始まるが、直後に ':' が来るため関数として検出しない
        const string source = "const o = { function: 1 };";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // function キーワード部分は -1（ニュートラル）のままであること
        // （誤って 0（赤）にマークされてはならない）
        Assert.Equal(-1, map[12]); // 'f' of 'function'
        Assert.Equal(-1, map[19]); // 'n' (end of 'function')
        // コロンの直後の値部分も -1 のまま
        Assert.Equal(-1, map[22]); // '1'
    }

    // -----------------------------------------------------------------------
    // Issue 3 — SkipBalancedParens / FindMatchingBrace のテンプレートリテラル修正
    // -----------------------------------------------------------------------

    /// <summary>
    /// テンプレートリテラルのデフォルト引数内に ) が含まれる場合、
    /// SkipBalancedParens が括弧カウントを狂わせずに正しくスキップすることを確認する。
    /// 例: function f(x = `${ ')' }`) { } — ${ } 内の ) でパラメータリストが終わると誤認識しないこと。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_TemplateLiteralParamWithParen_CorrectlyDetected()
    {
        // function f(x = `${ ')' }`) { }
        // ${ ')' } 内の ) で SkipBalancedParens が早期終了すると、
        // 関数本体 { } が検出されず -1 のままになる（バグ時）
        // 修正後は全体が 0（未実行）になる
        const string source = "function f(x = `${ ')' }`) { }";
        //                     0         1         2         3
        //                     0123456789012345678901234567890

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 関数全体が 0（未実行）になっているか確認する
        Assert.Equal(0, map[0]);  // 'f' of function
        Assert.Equal(0, map[29]); // '}'（関数本体の閉じ括弧）
    }

    /// <summary>
    /// テンプレートリテラルを含む関数本体で、${ } 内の } が
    /// FindMatchingBrace の波括弧カウントを狂わせないことを確認する。
    /// 例: function f() { return `${a}`; } — ${a} の } で関数終端と誤認識しないこと。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_TemplateLiteralBodyWithBrace_CorrectlyDetected()
    {
        // function foo() { return `${a}`; }
        // 修正前は ${a} 内の } で FindMatchingBrace が早期終了し、
        // "; }" の部分が -1 のままになる
        const string source = "function foo() { return `${a}`; }";
        //                     0         1         2         3
        //                     01234567890123456789012345678901234

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 関数全体が 0（未実行）になっているか確認する
        Assert.Equal(0, map[0]);  // 'f' of function
        Assert.Equal(0, map[32]); // '}'（関数本体の閉じ括弧）
        // "; }" 部分が -1 のままではないことを確認する
        Assert.Equal(0, map[30]); // ';'
        Assert.Equal(0, map[31]); // ' '
    }

    // -----------------------------------------------------------------------
    // Issue 4 — テンプレートリテラル内の未実行関数補正
    // -----------------------------------------------------------------------

    /// <summary>
    /// テンプレートリテラルの ${ } 内に定義された未実行関数が
    /// カバレッジ対象外（-1）ではなく未実行（0）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunctionInsideTemplateLiteral_MarkedAsUncovered()
    {
        // `${ function foo() { } }`
        //  0 12           3  4 56
        //  0123456789012345678901234
        // ${ から function foo() { } が始まる
        const string source = "`${ function foo() { } }`";
        //                     0123456789012345678901234
        //                     function at 4, { at 19, } at 21

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // function キーワードが 0（未実行）になっているか確認する
        Assert.Equal(0, map[4]);  // 'f' of function
        Assert.Equal(0, map[19]); // '{' of function body
        Assert.Equal(0, map[21]); // '}'
    }

    /// <summary>
    /// ${ } 内の関数本体にテンプレートリテラルが含まれる場合でも
    /// FindMatchingBrace + ScanRange が正しく動作することを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunctionInsideTemplateWithNestedTemplate_MarkedAsUncovered()
    {
        // `${ function foo() { return `${x}`; } }`
        //  0 12           3          4       5
        //  01234567890123456789012345678901234567890
        // function at 4, outer { at 19, inner template at 28-33, outer } at 36
        const string source = "`${ function foo() { return `${x}`; } }`";
        //                     0         1         2         3         4
        //                     0123456789012345678901234567890123456789 0

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // function キーワードが 0（未実行）になっているか確認する
        Assert.Equal(0, map[4]);  // 'f' of function
        // 関数の閉じ括弧も 0（未実行）になっているか確認する
        Assert.Equal(0, map[36]); // '}'（外側の関数の閉じ括弧）
    }

    // -----------------------------------------------------------------------
    // Issue 5 — IsRegexStart の ++/-- 誤判定修正
    // -----------------------------------------------------------------------

    /// <summary>
    /// 後置インクリメント（++）の直後に / が来る場合、除算として扱い、
    /// その後に続く function キーワードが正しく検出されることを確認する。
    /// 修正前: x++ /divisor; で IsRegexStart が true を返すため
    ///         SkipRegexLiteral が改行まで読み、"function" をフラグとして消費してしまう。
    /// 修正後: IsRegexStart が false を返し、/ は除算として処理されるため
    ///         function キーワードが正常に検出され 0（未実行）にマークされる。
    /// </summary>
    [Fact]
    public void BuildMap_PostIncrementBeforeDivision_FunctionAfterCorrectlyDetected()
    {
        // "count++ /divisor/ function foo() { }"
        // count++ = 7, space = 1, /divisor/ = 9, space = 1, function = 8
        // 0-6: count++, 7: space, 8-16: /divisor/, 17: space, 18-25: function
        // 修正前: / (pos 8) が正規表現と誤判定 → SkipRegexLiteral が /divisor/ をスキップ
        //         → 次は位置17のspaceで、関数検出に到達
        // 修正後: IsRegexStart が false を返し（++の直後なので）
        //         → function が正常に検出される → map[18] = 0
        const string source = "count++ /divisor/ function foo() { }";
        //                     0123456789012345678901234567890123456789

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // function キーワードが 0（未実行）になっているか確認する
        Assert.Equal(0, map[18]); // 'f' of function
        Assert.Equal(0, map[25]); // 'n' of function
    }

    /// <summary>
    /// 後置デクリメント（--）の直後に / が来る場合、除算として扱い、
    /// その後に続く function キーワードが正しく検出されることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_PostDecrementBeforeDivision_FunctionAfterCorrectlyDetected()
    {
        // "count-- /x/ function bar() { }"
        // 0-5: count--, 6: space, 7-9: /x/, 10: space, 12-19: function, 20: space
        // 修正後: IsRegexStart が false を返し（-- の直後なので / は除算）
        //         function が正常に検出される → map[12] = 0
        const string source = "count-- /x/ function bar() { }";
        //                     0123456789012345678901234567890

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // function キーワードが 0（未実行）になっているか確認する
        Assert.Equal(0, map[12]); // 'f' of function
        Assert.Equal(0, map[19]); // 'n' (最後の文字) of function
    }
    /// <summary>
    /// class内のPrivate field(`#`)を使ったメソッドが未実行の場合、関数シグネチャの一部として正しく処理され
    /// メソッド本体が 0（未実行）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledMethodWithPrivateField_BodyMarkedAsUncovered()
    {
        // class Foo { #priv; method() { this.#priv = 1; } }
        //  0         1         2         3         4
        //  0123456789012345678901234567890123456789012345678
        // m=19, {=28, }=46
        const string source = "class Foo { #priv; method() { this.#priv = 1; } }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // method() の先頭 'm' から } まで 0（未実行）になる
        Assert.Equal(0, map[19]); // 'm' of method
        Assert.Equal(0, map[28]); // {
        Assert.Equal(0, map[46]); // }
    }
    /// <summary>
    /// ネストされたテンプレートリテラル内の波括弧が、関数ボディの終了を誤爆させないかのテスト。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_NestedTemplateLiteral_NotClosedEarly()
    {
        // function foo() { const s = `${`${"// }"}`}`; return 1; }
        //  0         1         2         3         4         5
        //  0123456789012345678901234567890123456789012345678901234
        // {=15, }=54
        const string source = "function foo() { const s = `${`${\"// }\"}`}`; return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        Assert.Equal(0, map[0]);  // 'f'
        Assert.Equal(0, map[54]); // '}'
    }

    /// <summary>
    /// ブロックコメント内に開き波括弧 `{` だけを配置し、深さカウントを狂わせる意地悪ケース。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_DeceptiveBlockComment_DepthNotAffected()
    {
        // function foo() { /* { */ return 1; }
        //  0         1         2         3
        //  0123456789012345678901234567890123456789
        // {=15, }=35
        const string source = "function foo() { /* { */ return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        Assert.Equal(0, map[0]);  // 'f'
        Assert.Equal(0, map[35]); // '}'
    }

    /// <summary>
    /// 関数名に絵文字（サロゲートペア）が含まれる場合でも、正しく関数として認識され
    /// 未実行マーク（0）が適用されるかのテスト。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_WithEmoji_BodyMarkedAsUncovered()
    {
        // function calc😃() { return 1; }
        // emoji is 2 chars in UTF-16
        // f=0, {=18, }=30
        const string source = "function calc😃() { return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        Assert.Equal(0, map[0]);  // 'f'
        Assert.Equal(0, map[30]); // '}'
    }

    /// <summary>
    /// with (obj) { } が関数ではなくコントロールフローとして扱われ、
    /// 誤って未実行（赤）にマークされないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_WithKeyword_NotMisdetectedAsMethod()
    {
        // with (obj) { x = 1; }
        // 0123456789012345678901
        // with=0..3, (=5, )=8, {=10, }=20
        const string source = "with (obj) { x = 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // with はメソッド短縮構文として検出されず、全文字が -1（対象外）のままであるべき
        Assert.Equal(-1, map[0]);  // 'w' of with
        Assert.Equal(-1, map[10]); // '{'
        Assert.Equal(-1, map[20]); // '}'
    }

    /// <summary>
    /// 空の functions リストを渡した場合、全文字が -1（対象外）で初期化されることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_EmptyFunctions_AllMinusOne()
    {
        const string source = "var x = 1;";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 全文字が -1（対象外）であるべき（function キーワードも含まれない）
        for (int i = 0; i < map.Length; i++)
        {
            Assert.Equal(-1, map[i]);
        }
    }

    /// <summary>
    /// ゲッター構文（get name() { }）でパーサーが name をメソッド名として検出し、
    /// メソッド本体が 0（未実行）にマークされることを確認する。
    /// パーサーは `get` 自体は identifier→スペース→identifier の形なので、
    /// `name` のみがメソッド短縮構文として検出される。
    /// </summary>
    [Fact]
    public void BuildMap_GetterShorthand_BodyMarkedAsUncovered()
    {
        // get name() { return 1; }
        // 0123456789012345678901234
        // get=0..2, n=4, (=8, )=9, {=11, }=23
        const string source = "get name() { return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // get は identifier→空白→identifier のためメソッド検出せず -1 のまま
        Assert.Equal(-1, map[0]); // 'g' of get
        // name がメソッド短縮構文として検出され、name() { ... } が 0 にマークされる
        Assert.Equal(0, map[4]);  // 'n' of name
        Assert.Equal(0, map[11]); // '{'
        Assert.Equal(0, map[23]); // '}'
    }
}

/// <summary>
/// ScanRange のテンプレートリテラル処理が、${ } 内の find function を正しく検出できることを確認するテストクラス。
/// </summary>
public class NestedTemplateLiteralTests
{
    /// <summary>
    /// テンプレートリテラルの ${ } 内にネストされた function キーワードが存在する場合、
    /// そのボディが未実行（0）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionInsideTemplateLiteralExpression_MarkedAsUncovered()
    {
        // const x = `value: ${function() { return 1; }}`;
        // ScanRange は ` ... ${ ... } ... ` の ${ } 内を再帰スキャンするため、
        // 内側の function が未実行として検出されるはず
        const string source = "const x = `value: ${function() { return 1; }}`";
        //  pos:              0         1         2         3         4
        //                   0123456789012345678901234567890123456789012345
        // function keyword at 21, { at 31, } at 43

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // function キーワードの先頭が 0（未実行）にマークされているはず
        Assert.Equal(0, map[21]); // 'f' of function
        Assert.Equal(0, map[31]); // '{'
        Assert.Equal(0, map[43]); // '}'
    }

    /// <summary>
    /// テンプレートリテラルの ${ } 内にネストされたバッククォートがある場合でも
    /// 外側の ${ } が正しく処理されることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_NestedBacktickInsideDollarExpression_DoesNotCorruptScan()
    {
        // const x = `outer ${ `inner` } end`;
        // ネストされた ` ... ` が FindMatchingBrace の { } カウントを狂わせないことを確認する
        // 外側のテンプレートリテラルは全体が -1 のまま（function キーワードがないため）
        const string source = "const x = `outer ${ `inner` } end`";

        // クラッシュせず正常に処理されることを確認する
        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 全文字が -1（対象外）のまま — テンプレートリテラル内に function がないため
        Assert.Equal(-1, map[0]); // 'c' of const
        Assert.Equal(-1, map[10]); // '`' of outer template
    }
}

/// <summary>
/// ScanRange の検出対象・非検出対象のエッジケースを確認するテストクラス。
/// プライベートクラスメソッド、Unicode識別子、デストラクチャリングパラメータなど。
/// </summary>
public class ScanRangeEdgeCaseTests
{
    // -----------------------------------------------------------------------
    // プライベートクラスメソッド (#method) の非検出確認
    // -----------------------------------------------------------------------

    /// <summary>
    /// プライベートクラスメソッド構文（#method(){}）が未実行の場合、
    /// '#' は IsIdentifierChar に含まれるため TryMarkMethodShorthand が呼ばれ、
    /// メソッド本体が 0（未実行）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledPrivateClassMethod_MarkedAsUncovered()
    {
        // class Foo { #greet() { return 1; } }
        //  0         1         2         3
        //  0123456789012345678901234567890123456
        // #=12, g=13, (=18, )=19, {=21, }=33
        const string source = "class Foo { #greet() { return 1; } }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // '#' は IsIdentifierChar に含まれるため TryMarkMethodShorthand が呼ばれ 0（未実行）になる
        Assert.Equal(0, map[12]); // '#' of #greet
        Assert.Equal(0, map[13]); // 'g' of greet
        Assert.Equal(0, map[21]); // '{'
        Assert.Equal(0, map[33]); // '}'
    }

    // -----------------------------------------------------------------------
    // Unicode 識別子のメソッド名検出確認
    // -----------------------------------------------------------------------

    /// <summary>
    /// Unicode 文字（日本語など）を含むメソッド名が未実行の場合、
    /// char.IsLetterOrDigit が Unicode 文字を識別子文字として認識するため
    /// TryMarkMethodShorthand がメソッド本体を 0（未実行）としてマークすることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledMethodWithUnicodeName_MarkedAsUncovered()
    {
        // const obj = { 挨拶() { return 1; } };
        // '挨' は char.IsLetterOrDigit が true を返す Unicode 文字
        // 挨拶=14..15, (=16, )=17, {=19, }=31
        const string source = "const obj = { 挨拶() { return 1; } };";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // Unicode メソッド名の先頭から本体 } まで 0（未実行）になるはず
        int identStart = source.IndexOf('挨');
        int braceOpen  = source.IndexOf('{', identStart);
        int braceClose = source.LastIndexOf('}', source.Length - 3); // 外側の } を除く

        Assert.Equal(0, map[identStart]); // '挨' of 挨拶
        Assert.Equal(0, map[braceOpen]);  // '{'
        Assert.Equal(0, map[braceClose]); // '}'
    }

    // -----------------------------------------------------------------------
    // デストラクチャリングパラメータ内の関数デフォルト値の非検出確認
    // -----------------------------------------------------------------------

    /// <summary>
    /// デストラクチャリングパラメータのデフォルト値として関数が指定された場合
    /// （例: function outer({ cb = function() {} } = {}) {}）、
    /// 外側の function outer のバルクマーキング（funcStart〜funcEnd）がパラメータリストも含む範囲を
    /// 一括で 0 にするため、内側の function() も 0（未実行）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_DestructuringDefaultFunction_OuterAndInnerMarked()
    {
        // function outer({ cb = function() {} } = {}) { return 1; }
        //  0         1         2         3         4         5
        //  012345678901234567890123456789012345678901234567890123456789
        // outer function at 0, outer { at 44, outer } at 56
        // inner function at 22, inner { at 33, inner } at 34
        const string source = "function outer({ cb = function() {} } = {}) { return 1; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 外側の outer 関数全体が 0（未実行）になる
        Assert.Equal(0, map[0]);  // 'f' of outer function
        int outerBrace = source.LastIndexOf('}');
        Assert.Equal(0, map[outerBrace]); // '}' of outer body

        // 内側の function() も外側のバルクマーキングにより 0（未実行）になる
        int innerFuncIdx = source.IndexOf("function", 10); // 2番目の "function"
        Assert.Equal(0, map[innerFuncIdx]); // 'f' of inner function
    }

    // -----------------------------------------------------------------------
    // async アロー関数（単一パラメータ）の async キーワードマーク
    // -----------------------------------------------------------------------

    /// <summary>
    /// 括弧なしの単一パラメータを持つ async アロー関数（async x => {}）が未実行の場合、
    /// => から本体、および async キーワードが 0（未実行）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledAsyncArrowSingleParam_AsyncKeywordMarked()
    {
        // const f = async x => { return x; };
        //  0         1         2         3
        //  0123456789012345678901234567890123456
        // async=10, x=16, =>: ==18 >=19, {=21, }=33
        const string source = "const f = async x => { return x; };";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // async キーワードの先頭が 0（未実行）になっているか確認する
        Assert.Equal(0, map[10]); // 'a' of async
        // => から本体 } まで 0（未実行）になっているか確認する
        Assert.Equal(0, map[18]); // '=' of =>
        Assert.Equal(0, map[21]); // '{'
        Assert.Equal(0, map[33]); // '}'
    }

    // -----------------------------------------------------------------------
    // Issue: IsRegexStart — yield/case キーワード後の正規表現誤認識
    // yield と case は識別子文字で終わるが、後に / が来る場合は正規表現の開始である
    // RegexPrecedingKeywords に含まれていないと IsRegexStart が false を返し
    // FindMatchingBrace が正規表現内の } を関数終端と誤判定する
    // -----------------------------------------------------------------------

    /// <summary>
    /// ジェネレータ関数内の yield /regex/ に含まれる } を関数終端と誤認識しないことを確認する。
    /// yield が RegexPrecedingKeywords に含まれないと IsRegexStart が false を返し
    /// FindMatchingBrace が正規表現内の } を関数終端と誤判定して、
    /// 関数末尾の } が -1（ニュートラル）のまま残っていた。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledGenerator_YieldRegexWithBrace_FullBodyMarkedUncovered()
    {
        // function* gen() { yield /[}]/; }
        //  0         1         2         3
        //  01234567890123456789012345678901
        // function*=0, {=17, yield=18, /[}]/=24, }=31
        const string source = "function* gen() { yield /[}]/; }";

        // カバレッジデータなし → V8 未コンパイル → 全文字が未実行(0)になるべき
        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 関数全体が未実行（0）になっているか確認する
        Assert.Equal(0, map[0]);                     // 'f' of function*
        Assert.Equal(0, map[17]);                    // '{'
        // 修正前にここが -1（ニュートラル）になっていた: 正規表現内 } の直後以降
        Assert.Equal(0, map[29]);                    // ';' after /[}]/
        Assert.Equal(0, map[31]);                    // 最後の '}'
    }

    /// <summary>
    /// switch 文内の case /regex/ に含まれる } を関数終端と誤認識しないことを確認する。
    /// case が RegexPrecedingKeywords に含まれないと IsRegexStart が false を返す。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_CaseRegexWithBrace_FullBodyMarkedUncovered()
    {
        // function foo(x) { switch(x){ case /[}]/.test(x): break; } }
        //  0         1         2         3         4         5
        //  0123456789012345678901234567890123456789012345678901234567 8
        // function=0, {=17, switch=18, case=29, /[}]/=34, }=58
        const string source = "function foo(x) { switch(x){ case /[}]/.test(x): break; } }";

        // カバレッジデータなし → V8 未コンパイル → 全文字が未実行(0)になるべき
        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // 関数全体が未実行（0）になっているか確認する
        Assert.Equal(0, map[0]);                     // 'f' of function
        Assert.Equal(0, map[17]);                    // '{'
        // 修正前にここが -1（ニュートラル）になっていた: 正規表現内 } の直後以降
        Assert.Equal(0, map[source.Length - 1]);     // 最後の '}'
    }

    // -----------------------------------------------------------------------
    // await キーワード後の正規表現リテラル対応テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// await キーワードの直後に /[{]/ のような波括弧を含む正規表現がある場合、
    /// FindMatchingBrace が正規表現内の { を実際の波括弧と誤解釈しないことを確認する。
    /// await が RegexPrecedingKeywords に含まれないと IsRegexStart が false を返し、
    /// /[{]/ 内の { が深さカウントに加算されて関数終端の検出に失敗する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_AwaitRegexWithOpenBrace_BodyCorrectlyMarked()
    {
        // function foo() { const x = await /[{]/; return x; }
        // await /[{]/ の { が FindMatchingBrace の深さカウントに影響して
        // 関数末尾の } を正しく検出できなくなるバグを確認するテスト
        const string source = "function foo() { const x = await /[{]/; return x; }";

        // カバレッジデータなし → V8 未コンパイル → 全文字が未実行(0)になるべき
        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // return の位置が未実行（0）になっているか確認する（バグ時は -1 のまま）
        int returnIdx = source.IndexOf("return");
        Assert.Equal(0, map[returnIdx]);

        // 関数の最後の } も未実行になっているか確認する
        Assert.Equal(0, map[source.Length - 1]);
    }

    /// <summary>
    /// await キーワードの後の正規表現に閉じ波括弧 } だけが含まれる場合も
    /// 関数本体全体が正しくマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_AwaitRegexWithCloseBrace_BodyCorrectlyMarked()
    {
        // function bar() { const r = await /[}]/; return r; }
        // /[}]/ の } が FindMatchingBrace の深さカウントを早期終了させないことを確認する
        const string source = "function bar() { const r = await /[}]/; return r; }";

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        int returnIdx = source.IndexOf("return");
        Assert.Equal(0, map[returnIdx]);
        Assert.Equal(0, map[source.Length - 1]);
    }

    // -----------------------------------------------------------------------
    // async アロー関数のデフォルト引数内クォートでの逆走査テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// async アロー関数のパラメータにデフォルト値として文字列 ")" を含む場合、
    /// 逆走査の括弧深さカウンタが文字列内の ')' を実際の括弧と誤認識せず、
    /// async キーワードを正しく未実行（0）としてマークできることを確認する。
    /// </summary>
    [Fact]
    public void MarkUncalledFunctionBodies_AsyncArrowWithStringDefaultContainingCloseParen_AsyncKeywordMarked()
    {
        // async (x = ")") => { doSomething(); }
        // デフォルト引数の文字列 ")" に含まれる ) で逆走査が混乱するバグを確認する
        const string source = "async (x = \")\") => { doSomething(); }";

        var map = new int[source.Length];
        Array.Fill(map, -1);

        HtmlReportGenerator.MarkUncalledFunctionBodiesAsUncovered(source, map);

        // async キーワード（インデックス 0）が未実行（0）になっているか確認する
        // バグ時は逆走査が失敗し、async が -1（ニュートラル）のままになる
        Assert.Equal(0, map[0]); // 'a' of 'async'

        // 関数本体（doSomething）も未実行（0）になっているか確認する
        int bodyIdx = source.IndexOf("doSomething");
        Assert.Equal(0, map[bodyIdx]);
    }

    /// <summary>
    /// async アロー関数のパラメータにデフォルト値として単一引用符を含む文字列がある場合も、
    /// async キーワードを正しくマークできることを確認する。
    /// </summary>
    [Fact]
    public void MarkUncalledFunctionBodies_AsyncArrowWithSingleQuoteDefaultContainingParen_AsyncKeywordMarked()
    {
        // async (x = ')') => { return x; }
        const string source = "async (x = ')') => { return x; }";

        var map = new int[source.Length];
        Array.Fill(map, -1);

        HtmlReportGenerator.MarkUncalledFunctionBodiesAsUncovered(source, map);

        // async キーワードが未実行（0）になっているか確認する
        Assert.Equal(0, map[0]);
        int returnIdx = source.IndexOf("return");
        Assert.Equal(0, map[returnIdx]);
    }

    // -----------------------------------------------------------------------
    // MergeMaps — 空配列テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 両方が空配列の場合、空配列が返されることを確認する。
    /// </summary>
    [Fact]
    public void MergeMaps_BothEmpty_ReturnsEmptyArray()
    {
        var merged = HtmlReportGenerator.MergeMaps([], []);
        Assert.Empty(merged);
    }

    /// <summary>
    /// baseMap が空で otherMap が非空の場合、空配列（baseMap の長さ基準）が返されることを確認する。
    /// </summary>
    [Fact]
    public void MergeMaps_BaseMapEmpty_ReturnsEmpty()
    {
        var merged = HtmlReportGenerator.MergeMaps([], [1, 0, -1]);
        Assert.Empty(merged);
    }
}
