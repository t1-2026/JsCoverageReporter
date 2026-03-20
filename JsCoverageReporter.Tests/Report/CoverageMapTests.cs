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
    [Fact(Skip = "gap: async keyword before method shorthand is not marked uncovered (async function case is fixed but async method shorthand is not)")]
    public void BuildMap_UncalledAsyncMethodShorthand_AsyncKeywordAlsoMarked()
    {
        const string source = "const obj = { async greet() { return 1; } };";
        // async: 14-18, greet: 20-24, {: 28, }: 40

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // async キーワードも 0（未実行・赤）になるべき — 現状は -1 のまま
        Assert.Equal(0, map[14]); // 'a' of async
    }

    /// <summary>
    /// アロー関数のマーク範囲に関するオフバイワンバグの確認テスト。
    /// FindMatchingBrace は } の次のインデックスを返すが、
    /// アロー関数のマーク処理で "m &lt;= braceEnd" としているため
    /// } の直後の文字（; など）も誤って 0（赤）にマークされる。
    /// </summary>
    [Fact(Skip = "bug: arrow function marking uses m <= braceEnd but FindMatchingBrace returns index AFTER '}'," +
                 " so the character after '}' is incorrectly marked as uncovered (off-by-one)")]
    public void BuildMap_UncalledArrowFunction_CharAfterClosingBraceRemainsNeutral()
    {
        const string source = "const f = () => { return 1; };";
        //                     0         1         2
        //                     012345678901234567890123456789
        // => at 13, { at 16, } at 28, ; at 29

        var map = HtmlReportGenerator.BuildCoverageMap(source, []);

        // } 自体は 0（未実行）でなければならない
        Assert.Equal(0, map[28]); // }

        // } の直後の ; は arrow function の外側 → -1（ニュートラル）であるべき
        // 現在の実装は m <= braceEnd（= 29）でループするため map[29] = 0 になってしまう
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
}
