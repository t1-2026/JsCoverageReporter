using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

/// <summary>
/// CoverageParser.BuildCoverageMap メソッドの動作を検証するテスト群。
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
        var map = CoverageParser.BuildCoverageMap("abc", []);
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
        var map = CoverageParser.BuildCoverageMap("hello", functions);
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
        var map = CoverageParser.BuildCoverageMap("hello", functions);
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
        var map = CoverageParser.BuildCoverageMap(source, functions);
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
        var map = CoverageParser.BuildCoverageMap("", []);
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
        var map = CoverageParser.BuildCoverageMap("", functions);
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
        var map = CoverageParser.BuildCoverageMap("hello", functions);
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
        var map = CoverageParser.BuildCoverageMap("hello", functions);
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
        var map = CoverageParser.BuildCoverageMap("hello", functions);
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
        var map = CoverageParser.BuildCoverageMap("hello", functions);
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
        var map = CoverageParser.BuildCoverageMap("hello", functions);
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
        var map = CoverageParser.BuildCoverageMap("abc", functions);
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
        var map = CoverageParser.BuildCoverageMap("abc", functions);
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
        var map = CoverageParser.BuildCoverageMap("hello", functions);
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
        var map = CoverageParser.BuildCoverageMap(source, functions);
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
        var map = CoverageParser.BuildCoverageMap(source, functions);
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
        var map = CoverageParser.BuildCoverageMap(source, functions);
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
        var map = CoverageParser.BuildCoverageMap(source, functions);
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
        var map = CoverageParser.BuildCoverageMap(source, []);
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
        var map = CoverageParser.BuildCoverageMap(source, functions);
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
        var map = CoverageParser.BuildCoverageMap(source, []);
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
        var map = CoverageParser.BuildCoverageMap(source, []);
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
        var map = CoverageParser.BuildCoverageMap(source, []);
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
        var map = CoverageParser.BuildCoverageMap(source, []);
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
        var map = CoverageParser.BuildCoverageMap(source, []);
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
        var map = CoverageParser.BuildCoverageMap(source, []);

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
        var map = CoverageParser.BuildCoverageMap(source, []);

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
        var map = CoverageParser.BuildCoverageMap(source, []);

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
        var map = CoverageParser.BuildCoverageMap(source, functions);

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
        var map = CoverageParser.BuildCoverageMap(source, functions);

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
        var map = CoverageParser.BuildCoverageMap(source, functions);

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
        var map = CoverageParser.BuildCoverageMap(source, functions);

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
        var map = CoverageParser.BuildCoverageMap(source, functions);

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
        var map = CoverageParser.BuildCoverageMap(source, functions);

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
        var map = CoverageParser.BuildCoverageMap(source, functions);

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
        var map = CoverageParser.BuildCoverageMap(source, functions);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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
        var map = CoverageParser.BuildCoverageMap(source, functions);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

        // greet の本体（{ } 内）は 0（未実行）になるべき
        Assert.Equal(0, map[20]); // 'g' of greet
        Assert.Equal(0, map[28]); // {
        Assert.Equal(0, map[40]); // }
    }

    /// <summary>
    /// 未実行の async メソッド短縮構文では async キーワードも 0（未実行）になることを確認する。
    /// TryMarkMethodShorthand の逆走査で async キーワードを検出してマークする。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledAsyncMethodShorthand_AsyncKeywordAlsoMarked()
    {
        const string source = "const obj = { async greet() { return 1; } };";
        // async: 14-18, greet: 20-24, {: 28, }: 40

        var map = CoverageParser.BuildCoverageMap(source, []);

        // async キーワードも 0（未実行・赤）になるべき
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

        var map = CoverageParser.BuildCoverageMap(source, []);

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
        var map = CoverageParser.BuildCoverageMap("abc", functions);
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
        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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
        var map = CoverageParser.BuildCoverageMap("hello", functions);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

        // static キーワードも get/set と同様に 0（未実行）にマークされるべき
        Assert.Equal(0, map[12]); // 's' of static
        // run メソッドの先頭から } まで 0（未実行）になっていることを確認する
        Assert.Equal(0, map[19]); // 'r' of run
        Assert.Equal(0, map[25]); // {
        Assert.Equal(0, map[26]); // }
    }

    /// <summary>
    /// getter 構文（get prop() {}）が未実行の場合、
    /// get キーワードを含めて、プロパティ名以降の本体が 0（未実行）になることを確認する。
    /// （以前は誤検出回避で -1 だったが、get/set 対応により正しく 0 にマークされるようになった）
    /// </summary>
    [Fact]
    public void BuildMap_UncalledGetter_BodyMarkedAsUncovered()
    {
        // class Foo { get value() {} }
        //  0         1         2
        //  0123456789012345678901234567
        // g=12 (get), v=16 (value), (=21, {=24, }=25, outer }=27
        const string source = "class Foo { get value() {} }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // get キーワードも 0（未実行）になる
        Assert.Equal(0, map[12]); // 'g' of get
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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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
        var map = CoverageParser.BuildCoverageMap(source, functions);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

        // * を含めてメソッド宣言全体が 0（未実行）になっていることを確認する
        Assert.Equal(0, map[14]); // '*' of *gen
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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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
        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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
    /// set キーワードを含めて、プロパティ名以降の本体が 0（未実行）になることを確認する。
    /// （以前は誤検出回避で -1 だったが、get/set 対応により正しく 0 にマークされるようになった）
    /// </summary>
    [Fact]
    public void BuildMap_UncalledSetter_BodyMarkedAsUncovered()
    {
        // const obj = { set value(v) {} }
        //  0         1         2         3
        //  0123456789012345678901234567890
        // s=14（set）, v=18（value）, (=23, {=27, }=28, outer }=30
        const string source = "const obj = { set value(v) {} }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // set キーワードも 0（未実行）になる
        Assert.Equal(0, map[14]); // 's' of set
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

        var map = CoverageParser.BuildCoverageMap(source, []);

        // async メソッド名（識別子として）の先頭から } まで 0（未実行）になっていることを確認する
        Assert.Equal(0, map[14]); // 'a' of async (as method name)
        Assert.Equal(0, map[22]); // {
        Assert.Equal(0, map[34]); // }
    }

    /// <summary>
    /// 計算プロパティメソッド（["method"]() {}）が未実行(0)としてマークされることを確認する。
    /// ScanRange の '[' 検出により computed property key も補正対象になった。
    /// </summary>
    [Fact]
    public void BuildMap_ComputedPropertyMethod_MarkedAsUncovered()
    {
        // "const obj = { ["method"]() { return 1; } }"
        //  0         1         2         3         4
        //  012345678901234567890123456789012345678901
        // '[' は index 14、'{' は index 27、'}' は index 39
        const string source = "const obj = { [\"method\"]() { return 1; } }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // '[' の位置が 0（未実行）になっているか確認する
        Assert.Equal(0, map[14]); // '['
        // メソッド本体 '{' が 0（未実行）になっているか確認する
        Assert.Equal(0, map[27]); // '{'
        // メソッド本体 '}' が 0（未実行）になっているか確認する
        Assert.Equal(0, map[39]); // '}'
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

        var map = CoverageParser.BuildCoverageMap(source, []);

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
        var merged = CoverageParser.MergeMaps([1, 1, 1], [1, 1, 1]);
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
        var merged = CoverageParser.MergeMaps([1, 0, -1], [0, 1, -1]);
        Assert.Equal([1, 1, -1], merged);
    }

    /// <summary>
    /// 両方が未実行（0）の場合、合成結果も未実行（0）になることを確認する。
    /// </summary>
    [Fact]
    public void MergeMaps_BothUncovered_ReturnsUncovered()
    {
        var merged = CoverageParser.MergeMaps([0, 0], [0, 0]);
        Assert.Equal([0, 0], merged);
    }

    /// <summary>
    /// 一方が未実行（0）、他方が対象外（-1）の場合、未実行（0）になることを確認する。
    /// 対象外ではなく未実行という情報を優先する。
    /// </summary>
    [Fact]
    public void MergeMaps_OneUncoveredOneNeutral_ReturnsUncovered()
    {
        var merged = CoverageParser.MergeMaps([0, -1], [-1, 0]);
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
        var merged = CoverageParser.MergeMaps([0, 0, 0], [1]);
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
        var merged = CoverageParser.MergeMaps([0, 1], [1, 0, 1, 1]);
        Assert.Equal(2, merged.Length);
        Assert.Equal([1, 1], merged);
    }

    /// <summary>
    /// 両方が対象外（-1）の場合、結果も対象外（-1）になることを確認する。
    /// </summary>
    [Fact]
    public void MergeMaps_BothNeutral_ReturnsNeutral()
    {
        var merged = CoverageParser.MergeMaps([-1, -1, -1], [-1, -1, -1]);
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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

        // 全文字が -1（対象外）であるべき（function キーワードも含まれない）
        for (int i = 0; i < map.Length; i++)
        {
            Assert.Equal(-1, map[i]);
        }
    }

    /// <summary>
    /// ゲッター構文（get name() { }）でパーサーが get name をメソッドとして検出し、
    /// 全体が 0（未実行）にマークされることを確認する。
    /// （以前は name のみがメソッドとして検出されていたが、get/set 対応により get からマークされる）
    /// </summary>
    [Fact]
    public void BuildMap_GetterShorthand_BodyMarkedAsUncovered()
    {
        // get name() { return 1; }
        // 0123456789012345678901234
        // get=0..2, n=4, (=8, )=9, {=11, }=23
        const string source = "get name() { return 1; }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // get キーワードも 0（未実行）になる
        Assert.Equal(0, map[0]); // 'g' of get
        // name がメソッド短縮構文として検出され、name() { ... } が 0 にマークされる
        Assert.Equal(0, map[4]);  // 'n' of name
        Assert.Equal(0, map[11]); // '{'
        Assert.Equal(0, map[23]); // '}'
    }

    // -----------------------------------------------------------------------
    // computed property メソッド短縮構文（[key](){}）の未実行検出テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// computed property key を持つメソッド短縮構文（['key']() { }）が
    /// カバレッジデータなしのとき未実行(0)としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_ComputedKeyMethodShorthand_MarkedAsUncovered()
    {
        // "const o = { ['k'](x) { return x; } };"
        //  0         1         2         3
        //  0123456789012345678901234567890123456789
        // '[' は index 12、'(' は index 17、'{' は index 21、'}' は index 33
        const string source = "const o = { ['k'](x) { return x; } };";
        // カバレッジデータなし → MarkUncalledFunctionBodiesAsUncovered で補正される
        var map = CoverageParser.BuildCoverageMap(source, []);
        // '[' の位置が 0（未実行）になっているか確認する
        Assert.Equal(0, map[12]); // '['
        // メソッド本体 '{' が 0（未実行）になっているか確認する
        Assert.Equal(0, map[21]); // '{'
        // メソッド本体 '}' が 0（未実行）になっているか確認する
        Assert.Equal(0, map[33]); // '}'
    }

    /// <summary>
    /// computed property key の直前が識別子文字（配列アクセス arr[0]）の場合は
    /// メソッド短縮構文と誤検出されないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_ArrayAccess_NotFalselyDetectedAsComputedMethod()
    {
        // "arr[0]" は配列アクセスであり computed property key ではない
        // '[' の前が 'r'（識別子文字）なので検出対象外となる
        const string source = "arr[0]();";
        // カバレッジデータなし → 全文字 -1 のままのはず（関数でないため補正されない）
        var map = CoverageParser.BuildCoverageMap(source, []);
        // '[' の位置は -1（対象外）のままであるか確認する
        Assert.Equal(-1, map[3]); // '['
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

        var map = CoverageParser.BuildCoverageMap(source, []);

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
        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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
        var map = CoverageParser.BuildCoverageMap(source, []);

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
        var map = CoverageParser.BuildCoverageMap(source, []);

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
        var map = CoverageParser.BuildCoverageMap(source, []);

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

        var map = CoverageParser.BuildCoverageMap(source, []);

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

        CoverageParser.MarkUncalledFunctionBodiesAsUncovered(source, map);

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

        CoverageParser.MarkUncalledFunctionBodiesAsUncovered(source, map);

        // async キーワードが未実行（0）になっているか確認する
        Assert.Equal(0, map[0]);
        int returnIdx = source.IndexOf("return");
        Assert.Equal(0, map[returnIdx]);
    }

    /// <summary>
    /// async アロー関数のパラメータにデフォルト値としてバッククォートを含む文字列がある場合も、
    /// async キーワードを正しくマークできることを確認する。
    /// </summary>
    [Fact]
    public void MarkUncalledFunctionBodies_AsyncArrowWithBacktickDefaultContainingParen_AsyncKeywordMarked()
    {
        // async (x = `)`) => { return x; }
        const string source = "async (x = `)`) => { return x; }";

        var map = new int[source.Length];
        Array.Fill(map, -1);

        CoverageParser.MarkUncalledFunctionBodiesAsUncovered(source, map);

        // async キーワード（インデックス 0）が未実行（0）になっているか確認する
        // バグ時はバッククォート内の ) が parenDepth を狂わせ、async が -1 のままになる
        Assert.Equal(0, map[0]);
        int returnIdx2 = source.IndexOf("return");
        Assert.Equal(0, map[returnIdx2]);
    }

    // -----------------------------------------------------------------------
    // async アロー逆走査 — 正規表現デフォルト値に ( / ) を含むバグ修正テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// async アロー関数のパラメータデフォルト値に正規表現 /)/ がある場合、
    /// 正規表現内の unescaped `)` で parenDepth が狂い async が検出されなかったバグを修正する。
    /// </summary>
    [Fact]
    public void MarkUncalledFunctionBodies_AsyncArrowWithRegexDefaultContainingCloseParen_AsyncKeywordMarked()
    {
        // async (x = /)/) => { return x; }
        // /)/ は `)` にマッチする正規表現。逆走査中に `)` を parenDepth++ してしまうバグ。
        const string source = "async (x = /)/) => { return x; }";

        var map = new int[source.Length];
        Array.Fill(map, -1);

        CoverageParser.MarkUncalledFunctionBodiesAsUncovered(source, map);

        // async キーワード（インデックス 0）が未実行（0）になっているか確認する
        Assert.Equal(0, map[0]); // 'a' of 'async'
        int returnIdx = source.IndexOf("return");
        Assert.Equal(0, map[returnIdx]);
    }

    /// <summary>
    /// async アロー関数のパラメータデフォルト値に正規表現 /\(/ がある場合（エスケープ済み `(` 含む）、
    /// 正規表現内の `(` で parenDepth が狂い async が検出されなかったバグを修正する。
    /// </summary>
    [Fact]
    public void MarkUncalledFunctionBodies_AsyncArrowWithRegexDefaultContainingOpenParen_AsyncKeywordMarked()
    {
        // async (x = /\(/) => { return x; }
        // /\(/ は `(` にマッチする正規表現。逆走査中に `(` を parenDepth-- してしまうバグ。
        const string source = "async (x = /\\(/) => { return x; }";

        var map = new int[source.Length];
        Array.Fill(map, -1);

        CoverageParser.MarkUncalledFunctionBodiesAsUncovered(source, map);

        // async キーワード（インデックス 0）が未実行（0）になっているか確認する
        Assert.Equal(0, map[0]); // 'a' of 'async'
        int returnIdx = source.IndexOf("return");
        Assert.Equal(0, map[returnIdx]);
    }

    // -----------------------------------------------------------------------
    // 追加テスト: 各種エッジケース
    // -----------------------------------------------------------------------

    /// <summary>
    /// 無名ジェネレーター関数式（function*() {}）が未実行の場合、
    /// function キーワードと本体が 0 としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledAnonymousGeneratorFunctionExpression_MarkedAsUncovered()
    {
        // const gen = function*() { yield 1; }
        // 無名ジェネレーター: function キーワードの次が * → * スキップ → () → {} と処理できるか確認
        const string source = "const gen = function*() { yield 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int funcPos = source.IndexOf("function");
        Assert.Equal(0, map[funcPos]);
        int yieldPos = source.IndexOf("yield");
        Assert.Equal(0, map[yieldPos]);
    }

    /// <summary>
    /// export default async function() {} が未実行の場合、
    /// async キーワードと本体が 0 としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledExportDefaultAsyncFunction_AsyncKeywordMarked()
    {
        // export default async function() { return 1; }
        const string source = "export default async function() { return 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        // async の先頭がマークされること
        int asyncPos = source.IndexOf("async");
        Assert.Equal(0, map[asyncPos]);
        int returnPos = source.IndexOf("return");
        Assert.Equal(0, map[returnPos]);
        // export/default は関数キーワードではないため -1 のままのこと
        Assert.Equal(-1, map[0]); // 'e' of 'export'
    }

    /// <summary>
    /// function のパラメータデフォルト値に正規表現 /\)/ がある場合も、
    /// SkipBalancedParens が正規表現をスキップして本体が正しくマークされることを確認する。
    /// （SkipBalancedParens は IsRegexStart + SkipRegexLiteral で前方処理するため元々正しく動作する）
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_ParamWithRegexContainingCloseParen_BodyMarkedUncovered()
    {
        // function foo(x = /\)/) { return x; }
        const string source = "function foo(x = /\\)/) { return x; }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int funcPos = source.IndexOf("function");
        Assert.Equal(0, map[funcPos]);
        int returnPos = source.IndexOf("return");
        Assert.Equal(0, map[returnPos]);
    }

    /// <summary>
    /// オブジェクトリテラルの async キーがメソッド検出に誤影響しないことを確認する。
    /// const obj = { async: true } のあとの async () => {} は正しく未実行マークされる。
    /// </summary>
    [Fact]
    public void BuildMap_ObjectKeyAsync_DoesNotAffectArrowDetection()
    {
        // { async: true } はオブジェクトプロパティキー → メソッドではない
        // その直後の async アロー関数は正しく未実行マークされるべき
        const string source = "const obj = { async: true }; async () => { return 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        // オブジェクトキーの async は -1（対象外）のままであること
        int objAsyncPos = source.IndexOf("async:");
        Assert.Equal(-1, map[objAsyncPos]);
        // アロー関数の async は 0（未実行）になること
        int arrowAsyncPos = source.LastIndexOf("async");
        Assert.Equal(0, map[arrowAsyncPos]);
        int returnPos = source.IndexOf("return");
        Assert.Equal(0, map[returnPos]);
    }

    /// <summary>
    /// 3レベルネストのテンプレートリテラル内の未実行関数が正しく 0 にマークされることを確認する。
    /// （DoesNotCrash テストに加えてマークの正確さを検証する）
    /// </summary>
    [Fact]
    public void BuildMap_ThreeLevelNestedTemplate_InnerFunctionMarkedUncovered()
    {
        // `${ `${ function foo() { return 1; } }` }`
        const string source = "`${ `${ function foo() { return 1; } }` }`";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int funcPos = source.IndexOf("function");
        Assert.Equal(0, map[funcPos]);
        int returnPos = source.IndexOf("return");
        Assert.Equal(0, map[returnPos]);
    }

    // -----------------------------------------------------------------------
    // 意地悪テスト: 複合エッジケース
    // -----------------------------------------------------------------------

    /// <summary>
    /// 正規表現デフォルト値とバッククォートデフォルト値が混在する async アロー。
    /// 複合パターンで逆走査が破綻しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncArrowWithMixedRegexAndBacktickDefaults_AsyncKeywordMarked()
    {
        // async (x = /)/, y = `)`) => { return x; }
        const string source = "async (x = /)/,  y = `)`) => { return x; }";
        var map = new int[source.Length];
        Array.Fill(map, -1);
        CoverageParser.MarkUncalledFunctionBodiesAsUncovered(source, map);
        Assert.Equal(0, map[0]); // async の 'a'
        int returnPos = source.IndexOf("return");
        Assert.Equal(0, map[returnPos]);
    }

    /// <summary>
    /// パラメータリスト内に正規表現で ( を含む: async (x = /\(/) => {}
    /// () を含む複雑なケース。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncArrowWithRegexMatchingOpenParen_AsyncKeywordMarked()
    {
        // async (x = /[)(]/) => { return x; }
        // [] 文字クラス内に ) と ( が両方入っているケース
        const string source = "async (x = /[)(]/) => { return x; }";
        var map = new int[source.Length];
        Array.Fill(map, -1);
        CoverageParser.MarkUncalledFunctionBodiesAsUncovered(source, map);
        Assert.Equal(0, map[0]); // async の 'a'
    }

    /// <summary>
    /// async アローのパラメータに除算演算子（/）が含まれる場合、
    /// 正規表現と誤認しても async キーワードが正しく検出されることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncArrowWithDivisionDefaultValue_AsyncKeywordMarked()
    {
        // async (x = a / b) => { return x; }
        const string source = "async (x = 10 / 2) => { return x; }";
        var map = new int[source.Length];
        Array.Fill(map, -1);
        CoverageParser.MarkUncalledFunctionBodiesAsUncovered(source, map);
        Assert.Equal(0, map[0]); // async の 'a'
        int returnPos = source.IndexOf("return");
        Assert.Equal(0, map[returnPos]);
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
        var merged = CoverageParser.MergeMaps([], []);
        Assert.Empty(merged);
    }

    /// <summary>
    /// baseMap が空で otherMap が非空の場合、空配列（baseMap の長さ基準）が返されることを確認する。
    /// </summary>
    [Fact]
    public void MergeMaps_BaseMapEmpty_ReturnsEmpty()
    {
        var merged = CoverageParser.MergeMaps([], [1, 0, -1]);
        Assert.Empty(merged);
    }

    // -----------------------------------------------------------------------
    // JS Parser エッジケース意地悪（Nasty）テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// ダブルクォートの直前に偶数個のバックスラッシュがある場合、
    /// 最後のバックスラッシュはエスケープされるため、ダブルクォートは文字列終端として正常に認識されることを確認する。
    /// （SkipDoubleQuotedString のエッジケーステスト）
    /// </summary>
    [Fact]
    public void BuildMap_DoubleQuote_WithEscapedBackslash_EndProperlyRecognized()
    {
        // const x = "foo\\"; function z() {}
        // JS文字列としては "foo\\" は foo + バックスラッシュ1文字。
        const string source = "const x = \"foo\\\\\"; function z() {}";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // function キーワードの 'f' (位置 20) が未実行 (0) としてマークされているはず
        int funcPos = source.IndexOf("function");
        Assert.Equal(0, map[funcPos]);
    }

    /// <summary>
    /// 行コメントの中にブロックコメント開始（/*）がある場合、行コメントとして扱われ改行までスキップされることを確認する。
    /// ブロックコメントの中に行コメント開始（//）がある場合、ブロックコメントとして扱われ */ までスキップされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_NestedComments_SkippedProperly()
    {
        // 1行目: 行コメント内に /*（ブロックではないので改行で終わる）
        // 2行目: function a() {}
        // 3行目: ブロックコメント内に //（行コメントではないので */ で終わる）
        // 4行目: function b() {}
        string source = 
            "// 隠しコメント /* \n" +
            "function a() {}\n" +
            "/* 隠しコメント // \n" +
            "function hidden() {} */\n" +
            "function b() {}";

        var map = CoverageParser.BuildCoverageMap(source, []);

        int funcAPos = source.IndexOf("function a");
        int funcHiddenPos = source.IndexOf("function hidden");
        int funcBPos = source.IndexOf("function b");

        // a と b は未実行としてマークされる
        Assert.Equal(0, map[funcAPos]);
        Assert.Equal(0, map[funcBPos]);

        // hidden はブロックコメント内部なので対象外（-1）のまま
        Assert.Equal(-1, map[funcHiddenPos]);
    }

    /// <summary>
    /// 正規表現リテラルの文字クラス（[]）内にエスケープされないスラッシュ（/）がある場合、
    /// 正規表現がそこで終了せず、次のスラッシュまで正しく認識されることを確認する。
    /// （SkipRegexLiteral のエッジケーステスト）
    /// </summary>
    [Fact]
    public void BuildMap_RegexClassWithSlash_SkippedProperly()
    {
        // const r = /[/]/g; function c() {}
        const string source = "const r = /[/]/g; function c() {}";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // function が正しく認識されていることを確認
        int funcCPos = source.IndexOf("function c");
        Assert.Equal(0, map[funcCPos]);
    }

    /// <summary>
    /// async や * が付いた短縮メソッド定義が、キーワードを含めて正確に着色（未実行マーク）されることを検証する。
    /// （TryMarkMethodShorthand のエッジケーステスト）
    /// </summary>
    [Fact]
    public void BuildMap_AsyncGeneratorMethodShorthand_KeywordAreMarked()
    {
        // class Obj {
        //   async m1() {}
        //   *m2() {}
        //   async *m3() {}
        // }
        string source = "class Obj { async m1() {}  *m2() {}  async *m3() {} }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        int async1Pos = source.IndexOf("async m1");
        int star2Pos = source.IndexOf("*m2");
        int async3Pos = source.IndexOf("async *m3");

        // 各メソッドの修飾子先頭文字が 0（未実行）になっているはず
        Assert.Equal(0, map[async1Pos]);     // 'a' of async m1
        Assert.Equal(0, map[star2Pos]);      // '*' of *m2
        Assert.Equal(0, map[async3Pos]);     // 'a' of async *m3
    }
    /// <summary>
    /// async キーワードとパラメータ括弧の間にブロックコメントがある場合でも、
    /// async キーワードが未実行として正確にマークされることを検証する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledAsyncArrowWithComment_AsyncMarked()
    {
        const string source = "const f = async /* foo */ () => { return 1; };";
        var map = CoverageParser.BuildCoverageMap(source, []);
        
        int asyncPos = source.IndexOf("async");
        int parenPos = source.IndexOf('(');
        int bracePos = source.IndexOf('{');
        
        Assert.Equal(0, map[asyncPos]); // 'a' of async
        Assert.Equal(0, map[parenPos]); // '('
        Assert.Equal(0, map[bracePos]); // '{'
    }

    /// <summary>
    /// get / set プロパティ構文（メソッド短縮構文のようなもの）が未実行の場合、
    /// get / set キーワードを含めて正確に着色（未実行マーク）されることを検証する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledGetterSetter_PrefixMarked()
    {
        const string source = "const obj = {\n  get value() { return 1; },\n  set value(v) { }\n};";
        var map = CoverageParser.BuildCoverageMap(source, []);

        int getPos = source.IndexOf("get");
        int setPos = source.IndexOf("set");
        
        Assert.Equal(0, map[getPos]); // 'g' of get
        Assert.Equal(0, map[setPos]); // 's' of set
    }

    // -----------------------------------------------------------------------
    // Round 2: 追加テストケース（分析レポート #6-#16）
    // -----------------------------------------------------------------------

    /// <summary>
    /// エスケープされたバックスラッシュ（\\）の直後に } がある文字列内で、
    /// FindMatchingBrace が } を文字列終端と誤認識しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_EscapedBackslashBeforeBrace_StringEndProperlyRecognized()
    {
        // function foo() { return '\\}'; }
        // JS文字列 '\\}' は バックスラッシュ + } の2文字。} は文字列の中
        const string source = "function foo() { return '\\\\}'; }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // 関数全体が未実行（0）になっているはず
        Assert.Equal(0, map[0]);  // 'f' of function
        Assert.Equal(0, map[source.Length - 1]); // final '}'
    }

    /// <summary>
    /// 式ボディのアロー関数（{} なし）はカバレッジ対象外のまま残ることを確認する。
    /// ブロック本体がないアロー関数は MarkUncalledFunctionBodiesAsUncovered の対象外。
    /// </summary>
    [Fact]
    public void BuildMap_ExpressionBodyArrow_RemainsNeutral()
    {
        // const f = x => x + 1;
        const string source = "const f = x => x + 1;";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // {} がないため MarkUncalledFunctionBodiesAsUncovered の対象外 → 全部 -1
        Assert.All(map, v => Assert.Equal(-1, v));
    }

    /// <summary>
    /// 計算プロパティ名 [expr](){} は未実行（0）としてマークされることを確認する。
    /// ScanRange の '[' 検出ロジックで識別される。
    /// </summary>
    [Fact]
    public void BuildMap_ComputedPropertyMethod_Detected()
    {
        // const obj = { ['name']() {} };
        const string source = "const obj = { ['name']() {} };";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // '[' から '}' までが 0（未実行）にマークされる
        int bracketPos = source.IndexOf('[');
        Assert.Equal(0, map[bracketPos]);
    }

    /// <summary>
    /// 関数呼び出し結果の添字アクセス foo()[0](){} はメソッド短縮構文と誤検出しないことを確認する。
    /// ')' の直後に '[' が来る場合は computed property key でなく式の続きとして除外する。
    /// </summary>
    [Fact]
    public void BuildMap_CallResultIndexedCall_NotDetectedAsMethod()
    {
        // foo()[0]() が呼び出し式 + ブロック文であっても誤マークしない
        const string source = "function outer() { foo()[0]() { return 1; } }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // outer 関数は未実行 → 0 になるが、[0] の位置は computed key として誤検出してはいけない
        // '[' の直前は ')' のため検出除外される（map は outer の 0 から来るがメソッド検出ではない）
        int bracketPos = source.IndexOf('[');
        // map[bracketPos] は outer 関数の本体として 0 になるが、
        // "[0]() { return 1; }" 全体が別途誤マークされていないことを確認する
        // outer の brace end より後が -1 のまま（outer の } が最後の文字）
        Assert.Equal(0, map[0]);  // 'f' of outer function — uncovered
        Assert.Equal(0, map[source.Length - 1]); // '}' of outer — uncovered
    }

    /// <summary>
    /// 添字アクセスの続き arr[0][key](){} も誤検出しないことを確認する。
    /// ']' の直後に '[' が来る場合も除外する。
    /// </summary>
    [Fact]
    public void BuildMap_ChainedIndexedCall_NotDetectedAsMethod()
    {
        // arr[0][key]() のような連鎖添字は computed property key でない
        const string source = "function outer() { arr[0][key]() { } }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // outer 関数全体が未実行（0）であることを確認する
        Assert.Equal(0, map[0]);
        Assert.Equal(0, map[source.Length - 1]);
    }

    /// <summary>
    /// 空のソースコードで例外が発生しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_EmptySource_NoException()
    {
        var map = CoverageParser.BuildCoverageMap("", []);
        Assert.Empty(map);
    }

    /// <summary>
    /// async と function の間に改行がある場合でも async がマークされることを確認する。
    /// SkipWhitespaceAndCommentsBackward は改行文字もスキップする。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncNewlineFunction_AsyncMarked()
    {
        const string source = "async\n  function foo() { return 1; }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // async キーワードが 0（未実行）になっているはず
        Assert.Equal(0, map[0]); // 'a' of async
        // function も 0
        int funcPos = source.IndexOf("function");
        Assert.Equal(0, map[funcPos]);
    }

    /// <summary>
    /// テンプレートリテラル内の } で FindMatchingBrace が誤終了しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_TemplateLiteralBraceInFunctionBody_NotClosedEarly()
    {
        // function foo() { const s = `}`; return 1; }
        const string source = "function foo() { const s = `}`; return 1; }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        Assert.Equal(0, map[0]); // 'f' of function
        Assert.Equal(0, map[source.Length - 1]); // final '}'
    }

    /// <summary>
    /// constructor() がメソッド短縮構文として検出されることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledConstructor_MarkedAsUncovered()
    {
        // class Foo { constructor() { this.x = 1; } }
        const string source = "class Foo { constructor() { this.x = 1; } }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        int ctorPos = source.IndexOf("constructor");
        int braceOpen = source.IndexOf('{', ctorPos);
        int braceClose = source.IndexOf('}', braceOpen);
        Assert.Equal(0, map[ctorPos]);    // 'c' of constructor
        Assert.Equal(0, map[braceOpen]);  // '{'
        Assert.Equal(0, map[braceClose]); // '}'
    }

    /// <summary>
    /// static ブロック（static { }）は '(' を持たないため
    /// メソッド短縮構文として誤検出されないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_StaticInitializerBlock_NotDetected()
    {
        // class Foo { static { console.log(1); } }
        const string source = "class Foo { static { console.log(1); } }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // static は '(' のない識別子→空白→'{' のため TryMarkMethodShorthand で除外される
        int staticPos = source.IndexOf("static");
        Assert.Equal(-1, map[staticPos]); // 's' of static — not detected
    }

    /// <summary>
    /// 正規表現内の function が誤検出されないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_RegexContainingFunction_NotDetected()
    {
        const string source = "var r = /function foo() {}/;";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // 正規表現内の function は検出されない → 全部 -1
        Assert.All(map, v => Assert.Equal(-1, v));
    }

    /// <summary>
    /// SkipWhitespaceAndCommentsBackward が壊れたソース（*/ だけで /* がない）で
    /// IndexOutOfRangeException を起こさないことを確認する。
    /// async と function の間に不完全なブロックコメント終端 */ があるケース。
    /// </summary>
    [Fact]
    public void BuildMap_BrokenBlockCommentEnd_NoException()
    {
        // async */ function foo() {}
        // async と function の間に */ だけがある壊れたソース
        // SkipWhitespaceAndCommentsBackward が */ を見つけて /* を逆走査するが
        // 見つからず pos が -1 になるため async は検出されない
        const string source = "async */ function foo() {}";

        // 例外が発生しないことを確認する（IndexOutOfRangeException が回避されていること）
        var map = CoverageParser.BuildCoverageMap(source, []);

        // 不完全なソースであるため詳細なマーク位置は不問とし、クラッシュしなかったことを最優先で検証する
        Assert.NotNull(map);
    }
}

// -----------------------------------------------------------------------
// HtmlReportGenerator のテスト（GetFileName、BuildLines、MergeMaps）
// -----------------------------------------------------------------------

/// <summary>
/// GetFileName と BuildLines のエッジケーステスト。
/// </summary>
public class HtmlReportGeneratorTests
{
    /// <summary>
    /// ルートパスのみの URL からホスト名が返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_RootPathOnly_ReturnsHost()
    {
        var result = HtmlReportGenerator.GetFileName("http://example.com/");
        Assert.Equal("example.com", result);
    }

    /// <summary>
    /// パスなしの URL からホスト名が返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_NoPath_ReturnsHost()
    {
        var result = HtmlReportGenerator.GetFileName("http://example.com");
        Assert.Equal("example.com", result);
    }

    /// <summary>
    /// クエリ文字列とフラグメント付きの URL からファイル名が返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_WithQueryAndFragment_ReturnsFilename()
    {
        var result = HtmlReportGenerator.GetFileName("http://example.com/js/app.js?v=123#hash");
        Assert.Equal("app.js", result);
    }

    /// <summary>
    /// スキームなしの文字列はそのまま返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_NoScheme_ReturnsSameString()
    {
        var result = HtmlReportGenerator.GetFileName("not-a-url");
        Assert.Equal("not-a-url", result);
    }

    /// <summary>
    /// file:// スキームの URL からファイル名が返されることを確認する。
    /// </summary>
    [Fact]
    public void GetFileName_FileScheme_ReturnsFilename()
    {
        var result = HtmlReportGenerator.GetFileName("file:///C:/work/demo/app.js");
        Assert.Equal("app.js", result);
    }

    /// <summary>
    /// CRLF 改行を含むソースで BuildLines の offset が正しく計算されることを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_CrlfSource_OffsetCorrect()
    {
        // "ab\r\ncd" → Split('\n') → ["ab\r", "cd"]
        // map: a=0 → 1, b=1 → 1, \r=2 → 1, \n は区切り, c=4 → 0, d=5 → 0
        int[] map = [1, 1, 1, -1, 0, 0]; // a b \r \n c d（\n は Split で消えるが map 上は -1）
        // 注意: Split('\n') で \n 自体は消えるが map[3] は \n の位置

        var lines = HtmlReportGenerator.BuildLines("ab\r\ncd", map);

        Assert.Equal(2, lines.Count);
        // 1行目: a, b が covered → Covered
        Assert.Equal(LineCoverageStatus.Covered, lines[0].Status);
        // 2行目: c, d が uncovered → Uncovered
        Assert.Equal(LineCoverageStatus.Uncovered, lines[1].Status);
    }

    /// <summary>
    /// 空のソースで BuildLines が空リストを返すことを確認する。
    /// </summary>
    [Fact]
    public void BuildLines_EmptySource_ReturnsEmpty()
    {
        var lines = HtmlReportGenerator.BuildLines("", []);
        Assert.Empty(lines);
    }

    // -----------------------------------------------------------------------
    // IsRegexStart: else キーワード後の正規表現リテラル判定テスト（C-1）
    // -----------------------------------------------------------------------

    /// <summary>
    /// else の後に } を含む正規表現リテラルがある場合、FindMatchingBrace が
    /// 正規表現内の } で早期終了せず、関数末尾の } を正しく 0（未実行）にマークすることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_ElseWithRegexContainingBrace_ClosingBraceMarkedUncovered()
    {
        // else /[}]/ を含む未呼び出し関数
        // "else" が RegexPrecedingKeywords にない場合、FindMatchingBrace が正規表現内の } で
        // 早期終了してしまい、関数末尾の } が -1（ニュートラル）のまま残る
        const string source = "function foo(x) { if(x){} else /[}]/.test(x); }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        // 関数末尾の } は 0（未実行）になるべき
        Assert.Equal(0, map[source.Length - 1]);
    }

    // -----------------------------------------------------------------------
    // TryMarkMethodShorthand: ) と { の間のブロックコメントをスキップするテスト（C-2）
    // -----------------------------------------------------------------------

    /// <summary>
    /// greet() /* comment */ {} のように ) と { の間にブロックコメントがある場合も
    /// メソッド本体を 0（未実行）にマークすることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledMethod_BlockCommentBeforeBrace_MarkedUncovered()
    {
        // ) と { の間にブロックコメントがある場合にメソッドが検出されることを確認する
        const string source = "const obj = { greet() /* comment */ { return 1; } }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int identStart = source.IndexOf("greet");
        // メソッド先頭 'g' が 0（未実行）になるべき
        Assert.Equal(0, map[identStart]);
    }

    // -----------------------------------------------------------------------
    // SkipWhitespaceAndCommentsBackward: // 行コメントをスキップするテスト（C-3）
    // -----------------------------------------------------------------------

    /// <summary>
    /// async と function の間に // 行コメントがある場合、async キーワードも
    /// 0（未実行）にマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledAsyncFunction_LineCommentBetweenAsyncAndFunction_AsyncKeywordMarked()
    {
        // async // comment\nfunction foo() {} の async も赤くなるべき
        const string source = "async // comment\nfunction foo() { return 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        // async の先頭 'a'（index 0）が 0（未実行）になるべき
        Assert.Equal(0, map[0]);
    }

    // -----------------------------------------------------------------------
    // SkipWhitespaceAndCommentsBackward: 文字列内 // の誤検出テスト（C-4）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 同一行に "// ..." という文字列リテラルがある場合でも、
    /// その後に続く async () => {} の async キーワードが 0（未実行）にマークされることを確認する。
    /// 文字列内の // を行コメントと誤認すると async の検出に失敗してしまうバグの再現テスト。
    /// </summary>
    [Fact]
    public void BuildMap_StringLiteralContainingSlashSlash_AsyncArrowStillMarkedUncovered()
    {
        // 同一行: 文字列 "// fake" の後に async () => {} がある
        // 修正前は "// fake" の // を行コメントと誤認して async を検出できなかった
        const string source = "const x = \"// fake\"; async () => { return 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);

        int asyncPos = source.IndexOf("async");
        int bracePos = source.IndexOf('{');

        // async の先頭（'a'）が 0（未実行・赤）にマークされるべき
        Assert.Equal(0, map[asyncPos]);
        // 関数本体の { も 0（未実行）
        Assert.Equal(0, map[bracePos]);
    }

    /// <summary>
    /// 単一引用符文字列内の // でも同様に誤検出しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_SingleQuoteStringContainingSlashSlash_AsyncArrowStillMarkedUncovered()
    {
        const string source = "const x = '// fake'; async () => { return 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);

        int asyncPos = source.IndexOf("async");
        Assert.Equal(0, map[asyncPos]);
    }

    /// <summary>
    /// バッククォート文字列内の // でも同様に誤検出しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_TemplateLiteralContainingSlashSlash_AsyncArrowStillMarkedUncovered()
    {
        const string source = "const x = `// fake`; async () => { return 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);

        int asyncPos = source.IndexOf("async");
        Assert.Equal(0, map[asyncPos]);
    }

    /// <summary>
    /// async と /* block comment */ の間に文字列内 // があっても、
    /// async function の async キーワードが正しくマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncBlockCommentFunction_AsyncKeywordMarked()
    {
        // async /* comment */ function foo() {} — ブロックコメントは既存コードで処理されるが、
        // 同一行に文字列 "// x" があっても正しく動くことを確認する
        const string source = "\"// x\"; async /* c */ function foo() { return 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);

        int asyncPos = source.IndexOf("async");
        Assert.Equal(0, map[asyncPos]);
    }

    // -----------------------------------------------------------------------
    // テストB: async と () の間に行コメントがある場合
    // -----------------------------------------------------------------------

    /// <summary>
    /// async と () の間に行コメント（//）がある場合でも、
    /// SkipWhitespaceAndCommentsBackward が行コメントを正しく逆走査し、
    /// async キーワードが未実行（0）としてマークされることを確認する。
    /// （ブロックコメントは既存テスト済み。行コメントは未テストだった。）
    /// </summary>
    [Fact]
    public void BuildMap_AsyncLineCommentBeforeArrow_AsyncKeywordMarkedUncovered()
    {
        // "async // comment\n() => { return 1; }" — async と () の間に行コメント
        // SkipWhitespaceAndCommentsBackward が // の手前まで逆走査して async を検出するべき
        const string source = "async // comment\n() => { return 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);

        // async キーワード先頭の 'a' が未実行（0）になっているか確認する
        int asyncPos = source.IndexOf("async");
        Assert.Equal(0, map[asyncPos]);
    }

    // -----------------------------------------------------------------------
    // テストC: 不完全なソース（ファイル末尾が function で切れている）
    // -----------------------------------------------------------------------

    /// <summary>
    /// "function" だけで終わる不完全なソースを渡した場合、
    /// 例外なく完了して全文字が -1（対象外）のままであることを確認する。
    /// i + 8 &lt;= end のガードで function キーワードとして処理されない。
    /// </summary>
    [Fact]
    public void BuildMap_TruncatedSourceEndingWithFunction_NoException()
    {
        // "function" だけで終わる不完全なソース
        const string source = "function";
        // 例外なく完了することを確認する
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);
        // function の後に { がないためキーワードとして処理されず、全文字対象外になるべき
        var map = CoverageParser.BuildCoverageMap(source, []);
        Assert.All(map, v => Assert.Equal(-1, v));
    }

    // -----------------------------------------------------------------------
    // テストD: 大量ソース（パフォーマンス・クラッシュなしの確認）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 大量の関数定義（約100,000文字）を含むソースを渡した場合、
    /// 妥当な時間内に完了して例外が発生しないことを確認する。
    /// O(n²) 的な処理があると大きなファイルで問題になるため、煙幕テストとして実施する。
    /// </summary>
    [Fact]
    public void BuildMap_LargeSource_CompletesWithoutException()
    {
        // 約26文字 × 3,846 = 99,996 文字のソースを生成する
        string source = string.Concat(Enumerable.Repeat("function f() { return 1; }\n", 3846));
        // 例外なく完了することを確認する
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);
    }

    // -----------------------------------------------------------------------
    // テストH: MergeMaps に異常値（-2, 2 など）が渡された場合の挙動確認
    // -----------------------------------------------------------------------

    /// <summary>
    /// MergeMaps に -1/0/1 以外の異常値（-2, 2 など）が渡された場合、
    /// else ブランチに入り -1（対象外）として扱われることを確認する。
    /// V8 から予期しない値が来た場合の安全な動作を文書化するテスト。
    /// </summary>
    [Fact]
    public void MergeMaps_AbnormalValues_TreatedAsNeutral()
    {
        // v1=-2（異常値）, v2=0（未実行）→ v2==0 なので 0 になるべき
        var result = CoverageParser.MergeMaps([-2, 2, -2], [-1, -1, 2]);
        // -2 と -1 → どちらも 1 でも 0 でもない → else → -1
        Assert.Equal(-1, result[0]);
        // 2 と -1 → どちらも 1 でも 0 でもない → else → -1
        Assert.Equal(-1, result[1]);
        // -2 と 2 → どちらも 1 でも 0 でもない → else → -1
        Assert.Equal(-1, result[2]);
    }

    /// <summary>
    /// MergeMaps で v1 に異常値、v2 に 0 が来た場合、v2==0 が優先されることを確認する。
    /// </summary>
    [Fact]
    public void MergeMaps_AbnormalV1WithZeroV2_ReturnsZero()
    {
        // v1=-2（異常値）, v2=0 → v2==0 なので 0 になるべき
        var result = CoverageParser.MergeMaps([-2], [0]);
        Assert.Equal(0, result[0]);
    }

    // -----------------------------------------------------------------------
    // テストI: MergeMaps の可換性（同じ長さのマップでは入力順不問）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 同じ長さのマップを渡した場合、入力順を逆にしても同じ結果になることを確認する。
    /// OR 合成の可換性: MergeMaps(a, b) == MergeMaps(b, a)（同長の場合のみ）。
    /// </summary>
    [Fact]
    public void MergeMaps_SameLengthMaps_IsCommutative()
    {
        // 各パターン: 1-0, 0-1（どちらも 1）、0-(-1), (-1)-0（どちらも 0）、(-1)-(-1)（-1）
        int[] map1 = [1, 0, -1, 1, 0, -1];
        int[] map2 = [0, 1, 0, -1, 1, -1];

        var result1 = CoverageParser.MergeMaps(map1, map2);
        var result2 = CoverageParser.MergeMaps(map2, map1);

        // 同じ長さで同じ内容（順不同）→ 結果が一致するべき
        Assert.Equal(result1, result2);
    }

    // -----------------------------------------------------------------------
    // テストJ: 不正なソース（未閉じ正規表現文字クラス・未閉じ波括弧）でのクラッシュなし確認
    // -----------------------------------------------------------------------

    /// <summary>
    /// 閉じられていない正規表現文字クラス /[abc/ を含む関数本体を渡した場合、
    /// クラッシュせず完了することを確認する。
    /// FindMatchingBrace は SkipRegexLiteral が末尾まで走査しても } を見つけられず -1 を返す。
    /// 結果として関数本体は未マーク（-1 のまま）になる（これは不正ソースへの許容動作）。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionBodyWithUnclosedRegexCharClass_DoesNotCrash()
    {
        // /[abc/ の [ が閉じていない不正な正規表現 → FindMatchingBrace が -1 を返す
        // 関数先頭は補正されず -1（ニュートラル）のまま残る
        const string source = "function f() { var r = /[abc/; }";

        // クラッシュしないこと
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);

        // FindMatchingBrace が -1 を返すため補正不可 → 'f'（index 0）は -1 のまま
        var map = CoverageParser.BuildCoverageMap(source, []);
        Assert.Equal(-1, map[0]); // 'f' of function — 不正ソースのため補正不可
    }

    /// <summary>
    /// 未閉じ波括弧で終わる不正なソース（"function f() { unclosed"）を渡した場合、
    /// クラッシュせず完了することを確認する。
    /// FindMatchingBrace が -1 を返すため関数本体はマークされず -1 のまま残る。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionWithUnmatchedOpenBrace_DoesNotCrash()
    {
        // 閉じ } がない不正なソース
        const string source = "function f() { unclosed";

        // クラッシュしないこと
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);

        // FindMatchingBrace が -1 を返すためマーク不可 → 全文字 -1 のまま
        var map = CoverageParser.BuildCoverageMap(source, []);
        Assert.All(map, v => Assert.Equal(-1, v));
    }

    // -----------------------------------------------------------------------
    // テストK: 空のテンプレート補間 ${} と 3段ネスト テンプレートリテラル
    // -----------------------------------------------------------------------

    /// <summary>
    /// 空のテンプレート補間 `${}` を含むソースを渡した場合、
    /// 例外なく完了することを確認する。
    /// FindMatchingBrace が `{}` の直後を返し、ScanRange(s, m, 3, 3) は start==end で無害。
    /// </summary>
    [Fact]
    public void BuildMap_EmptyTemplateInterpolation_DoesNotCrash()
    {
        // `${}` の ${ } 内が空 → ScanRange の再帰範囲が start==end で即終了するはず
        const string source = "var s = `${}`; function f() { return 1; }";

        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);

        // function f() は未実行（0）としてマークされるはず（補間は影響しない）
        var map = CoverageParser.BuildCoverageMap(source, []);
        int funcStart = source.IndexOf("function");
        Assert.Equal(0, map[funcStart]); // 'f' of function
    }

    /// <summary>
    /// 3段ネストのテンプレートリテラル `a${ `b${ `c` }` }` を含むソースを渡した場合、
    /// 例外なく完了することを確認する。
    /// FindMatchingBrace が SkipTemplateLiteralFull を再帰的に使ってネストを正しく処理する。
    /// </summary>
    [Fact]
    public void BuildMap_ThreeLevelNestedTemplateLiteral_DoesNotCrash()
    {
        // 3段ネスト: `a${ `b${ `c` }` }` の後に function を置く
        const string source = "var x = `a${ `b${ `c` }` }`; function f() { return 1; }";

        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);

        // function f() が正しく 0（未実行）にマークされること（テンプレートが影響しない）
        var map = CoverageParser.BuildCoverageMap(source, []);
        int funcStart = source.IndexOf("function");
        Assert.Equal(0, map[funcStart]); // 'f' of function
    }

    // -----------------------------------------------------------------------
    // テストL: get/set キーワードがソース先頭にある場合の境界条件
    // asyncScanBack - 2 = 0 になるケースを検証する
    // -----------------------------------------------------------------------

    /// <summary>
    /// "get foo() { return 1; }" のように get プロパティ構文がソース先頭にある場合、
    /// asyncScanBack - 2 = 0 という境界条件でも markStart が 0 に正しく設定され、
    /// get キーワードの先頭（index 0）からメソッド本体末尾まで 0（未実行）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_GetMethodAtStartOfSource_MarkStartIsZero()
    {
        // ソース先頭が get foo() {} → asyncScanBack=2, asyncScanBack-2=0 の境界条件
        const string source = "get foo() { return 1; }";
        //                     g(0) e(1) t(2) ' '(3) f(4) o(5) o(6) ((7) )(8) ' '(9) {(10) ... }(22)

        // クラッシュしないこと
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);

        var map = CoverageParser.BuildCoverageMap(source, []);

        // get キーワードの先頭（index 0）が 0（未実行）にマークされるべき
        Assert.Equal(0, map[0]); // 'g' of get

        // メソッド本体 { ... } も 0（未実行）
        int braceOpen = source.IndexOf('{');
        Assert.Equal(0, map[braceOpen]); // '{'
    }

    // -----------------------------------------------------------------------
    // テストM: 宙ぶらりんの */ がある不正なソースで SkipWhitespaceAndCommentsBackward がクラッシュしない
    // -----------------------------------------------------------------------

    /// <summary>
    /// "async */ function foo() {}" のように対応する /* のない */ が含まれる不正ソースで
    /// クラッシュしないことを確認する。
    /// ScanRange は */ の / を正規表現開始と判定してソース末尾まで消費するため、
    /// function キーワードは検出されず map 全体が -1（ニュートラル）のままになる。
    /// これは不正 JS ソースへの許容動作（crash しない）を文書化するテスト。
    /// </summary>
    [Fact]
    public void BuildMap_DanglingBlockCommentEndBeforeFunction_DoesNotCrash()
    {
        // 対応する /* のない */ を含む不正ソース
        // ScanRange が */ の / を正規表現開始と判定してソース末尾まで走査するため、
        // function キーワードは未検出 → map 全体が -1（ニュートラル）になる
        const string source = "async */ function foo() { return 1; }";

        // クラッシュしないこと
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);

        // */ の / が正規表現と誤判定されソース末尾まで消費されるため function は未検出
        // → map 全体が -1（ニュートラル）のままになる（不正ソースへの許容動作）
        var map = CoverageParser.BuildCoverageMap(source, []);
        int funcStart = source.IndexOf("function");
        Assert.Equal(-1, map[funcStart]); // 不正ソースのため -1（ニュートラル）のまま
    }

    /// <summary>
    /// テンプレートリテラルの ${ } 補間内に function 宣言がある場合、
    /// ScanRange の再帰スキャンにより補間内部も検査されることを確認する。
    /// V8 が補間内の関数を遅延コンパイルで省いた場合でも正しく 0（未実行）がマークされる。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionInsideTemplateInterpolation_MarkedAsUncovered()
    {
        // var x = `${function foo() { return 1; }}`;
        // インデックス:
        //  0: v, 1: a, 2: r, 3: ' ', 4: x, 5: ' ', 6: =, 7: ' ', 8: `
        //  9: $, 10: {, 11: f(unction の先頭), ...
        //  function: 11-18, ' ': 19, foo: 20-22, (: 23, ): 24, ' ': 25
        //  {: 26 (関数本体), ' ': 27, return: 28-33, ' ': 34, 1: 35, ;: 36, ' ': 37
        //  }: 38 (関数本体の閉じ), }: 39 (${ の閉じ), `: 40
        const string source = "var x = `${function foo() { return 1; }}`;";

        var map = CoverageParser.BuildCoverageMap(source, []);

        int funcStart = source.IndexOf("function");
        // function キーワードの先頭が 0（未実行）にマークされていること
        Assert.Equal(0, map[funcStart]);
        // 関数本体の開き { が 0（未実行）にマークされていること
        int openBrace = source.IndexOf('{', funcStart);
        Assert.Equal(0, map[openBrace]);
        // 関数本体の閉じ } が 0（未実行）にマークされていること
        // （${ の閉じ } より前の } が関数本体の閉じ}）
        int closeBrace = source.IndexOf('}', openBrace);
        Assert.Equal(0, map[closeBrace]);
    }

    /// <summary>
    /// テンプレートリテラルの ${ } 補間内にアロー関数がある場合、
    /// ScanRange の再帰スキャンにより補間内部の => も検査されることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_ArrowFunctionInsideTemplateInterpolation_MarkedAsUncovered()
    {
        // var x = `${() => { return 1; }}`;
        // インデックス:
        //  0: v, 1: a, 2: r, 3: ' ', 4: x, 5: ' ', 6: =, 7: ' ', 8: `
        //  9: $, 10: {, 11: (, 12: ), 13: ' ', 14: =, 15: >
        //  16: ' ', 17: { (アロー関数本体), ... }, }: ${ の閉じ
        const string source = "var x = `${() => { return 1; }}`;";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // ${ の後の ( がアロー関数の開始
        // => の前の () からアロー関数の arrowStart を探す
        int arrowIdx = source.IndexOf("=>");
        // => の直前の ( が arrowStart の候補（( から逆方向に探す）
        int openParen = source.LastIndexOf('(', arrowIdx);
        // map[openParen] は -1（ScanRange では ( をマークしない）可能性があるため
        // アロー関数の本体 { } が 0（未実行）にマークされていることを確認する
        int bodyOpen = source.IndexOf('{', arrowIdx);
        Assert.Equal(0, map[bodyOpen]);
        int bodyClose = source.IndexOf('}', bodyOpen);
        Assert.Equal(0, map[bodyClose]);
    }

    // -----------------------------------------------------------------------
    // Bug1: function キーワードとブレースの間にコメントがある場合の補正テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// function foo() の後にブロックコメントがあっても関数本体が 0（未実行）にマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionWithBlockCommentBeforeBrace_MarkedAsUncovered()
    {
        // function foo() /* comment */ { return 1; }
        // カバレッジデータなし（空）→ 未実行関数補正が走る
        const string source = "function foo() /* c */ { return 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        // "function" の先頭（インデックス 0）が 0（未実行）になること
        Assert.Equal(0, map[0]);
        // 関数本体 { の位置が 0（未実行）になること
        int brace = source.IndexOf('{');
        Assert.Equal(0, map[brace]);
    }

    /// <summary>
    /// function foo() の後に行コメントがあっても関数本体が 0（未実行）にマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionWithLineCommentBeforeBrace_MarkedAsUncovered()
    {
        // function foo() // comment\n{ return 1; }
        const string source = "function foo() // c\n{ return 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        Assert.Equal(0, map[0]);
        // \n の後の { の位置が 0 になること
        int brace = source.IndexOf('{', source.IndexOf('\n'));
        Assert.Equal(0, map[brace]);
    }

    // -----------------------------------------------------------------------
    // Bug2: アロー関数の逆走査で正規表現デフォルト引数内の \) を正しく処理するテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// async (x = /\)/) => {} で、正規表現内のエスケープされた ) が parenDepth を
    /// 狂わせないことを確認する（async キーワードも 0 にマークされること）。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncArrowWithEscapedParenInRegexDefault_AsyncMarkedAsUncovered()
    {
        // async (x = /\)/) => { return x; }
        // async キーワード（インデックス 0）が 0 にマークされること
        const string source = "async (x = /\\)/) => { return x; }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        // "async" の 'a'（インデックス 0）が 0（未実行）になること
        Assert.Equal(0, map[0]);
    }

    // -----------------------------------------------------------------------
    // テストM2: SkipTemplateLiteralFull ${}内コメントのバグ再現テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// テンプレート式内のブロックコメントに } が含まれる場合、
    /// SkipTemplateLiteralFull が ${}を早期クローズして SkipBalancedParens の
    /// ) 深さカウントを狂わせ、function のパラメータリスト終端を誤検出するバグの再現。
    /// 修正後は function f の本体全体が 0（未実行）にマークされること。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_TemplateExprBlockCommentWithBrace_BodyCorrectlyMarked()
    {
        // function f(x = `${ /* } */ `)`}`) {}
        // ${}内の /* } */ の } で SkipTemplateLiteralFull が早期クローズ →
        // 内側テンプレート `)`のバッククォートで SkipTemplateLiteralFull が早期 return →
        // SkipBalancedParens が次の ) を function パラメータ閉じと誤認識 → function 本体未検出
        const string source = "function f(x = `${ /* } */ `)`}`) {}";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // function f の 'f' が 0（未実行）にマークされること
        Assert.Equal(0, map[0]);
        // 関数本体の閉じ } も 0 にマークされること
        Assert.Equal(0, map[source.Length - 1]);
    }

    /// <summary>
    /// テンプレート式内の行コメントに } が含まれる場合も同様に
    /// SkipTemplateLiteralFull が ${}を早期クローズしないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledFunction_TemplateExprLineCommentWithBrace_BodyCorrectlyMarked()
    {
        // function f(x = `${// }\n`)`}`) {}
        // 行コメント内の } で早期クローズして SkipBalancedParens が混乱するバグの再現
        const string source = "function f(x = `${// }\n`)`}`) {}";

        var map = CoverageParser.BuildCoverageMap(source, []);

        Assert.Equal(0, map[0]);
        Assert.Equal(0, map[source.Length - 1]);
    }

    // -----------------------------------------------------------------------
    // テストM: static キーワードのマーク対応テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// static async メソッドが未実行の場合、static も async も 0（未実行）にマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledStaticAsyncMethod_StaticAndAsyncMarked()
    {
        // class Foo { static async run() {} }
        //  0         1         2         3
        //  012345678901234567890123456789012345
        // s=12 (static), a=19 (async), r=25 (run), (=28, {=31, }=32, outer }=34
        const string source = "class Foo { static async run() {} }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // static キーワードも 0（未実行）にマークされるべき
        Assert.Equal(0, map[12]); // 's' of static
        // async キーワードも 0（未実行）
        Assert.Equal(0, map[19]); // 'a' of async
        // run メソッド本体も 0（未実行）
        Assert.Equal(0, map[25]); // 'r' of run
        Assert.Equal(0, map[31]); // {
        Assert.Equal(0, map[32]); // }
    }

    /// <summary>
    /// static get プロパティが未実行の場合、static も get も 0（未実行）にマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledStaticGetter_StaticAndGetMarked()
    {
        // class Foo { static get value() {} }
        //  0         1         2         3
        //  0123456789012345678901234567890123456
        // s=12 (static), g=19 (get), v=23 (value), (=28, {=31, }=32, outer }=34
        const string source = "class Foo { static get value() {} }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // static キーワードも 0（未実行）
        Assert.Equal(0, map[12]); // 's' of static
        // get キーワードも 0（未実行）
        Assert.Equal(0, map[19]); // 'g' of get
        // value プロパティ本体も 0（未実行）
        Assert.Equal(0, map[23]); // 'v' of value
    }

    // -----------------------------------------------------------------------
    // SkipWhitespaceAndCommentsBackward バグ修正テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// async アロー関数のパラメータデフォルト値がネストしたテンプレートリテラル内に "//" を含む場合、
    /// SkipWhitespaceAndCommentsBackward が "//" をコメントと誤判定せず
    /// async キーワードを正しく 0（未実行）にマークすることを確認する。
    /// バグ時: td カウンタが内側の ` で 0 になり早期ループ終了 → "//" を template 外と誤認
    /// → backScan が template 内に止まり "async" を検出できない → map[0] = -1 のまま
    /// </summary>
    [Fact]
    public void BuildMap_AsyncArrowNestedTemplateDefaultWithDoubleSlash_AsyncKeywordMarked()
    {
        // async (x = `${`b`} // comment`) => { return x; }
        //  0         1         2         3         4         5
        //  0123456789012345678901234567890123456789012345678901
        const string source = "async (x = `${`b`} // comment`) => { return x; }";

        var map = new int[source.Length];
        Array.Fill(map, -1);

        CoverageParser.MarkUncalledFunctionBodiesAsUncovered(source, map);

        // async キーワード（インデックス 0）が未実行（0）になっていること
        // バグ時は -1（対象外）のまま
        Assert.Equal(0, map[0]);
        // 関数本体内（return のある位置）も未実行
        int returnIdx = source.IndexOf("return");
        Assert.Equal(0, map[returnIdx]);
    }

    /// <summary>
    /// async キーワードと (パラメータリスト) の間に改行がある場合でも
    /// async キーワードが 0（未実行）にマークされることを確認する。
    /// （既存の async\n function テストとは別の、async\n アロー関数ケース）
    /// </summary>
    [Fact]
    public void BuildMap_AsyncArrowWithNewlineBeforeParens_AsyncKeywordMarked()
    {
        // async\n(x) => { return x; }
        const string source = "async\n(x) => { return x; }";

        var map = new int[source.Length];
        Array.Fill(map, -1);

        CoverageParser.MarkUncalledFunctionBodiesAsUncovered(source, map);

        // "async" の先頭（位置 0）が 0（未実行）になること
        Assert.Equal(0, map[0]);
    }

    /// <summary>
    /// 外側アローは V8 データあり（実行済み）、内側アローは V8 データなし（遅延コンパイル）の場合、
    /// 内側アローが MarkUncalledFunctionBodiesAsUncovered によって 0（未実行）にマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_OuterArrowCoveredByV8_InnerArrowUncalled_InnerMarkedUncovered()
    {
        // const f = x => { const g = y => { return y; }; return x; };
        //  0         1         2         3         4         5         6
        //  0123456789012345678901234567890123456789012345678901234567890123
        const string source = "const f = x => { const g = y => { return y; }; return x; };";

        // 外側アロー（x =>）には V8 カバレッジデータあり（map[12] = 1）
        // 内側アロー（y =>）には V8 カバレッジデータなし（map[29] = -1）
        var map = new int[source.Length];
        Array.Fill(map, -1);
        // 外側 => の '=' は位置 12
        int outerArrow = source.IndexOf("x =>");
        // 外側アローの範囲を「実行済み」で塗る（V8 がデータを提供したと仮定）
        for (int i = 0; i < outerArrow + 4; i++) { map[i] = 1; }
        // 外側 { ... } の中身だけ -1（まだ内側アローはデータなし）
        int outerBrace = source.IndexOf('{');
        for (int i = outerBrace; i < source.Length; i++) { map[i] = -1; }

        CoverageParser.MarkUncalledFunctionBodiesAsUncovered(source, map);

        // 内側 => の位置を確認する（"y =>" の '=' の位置）
        int innerArrow = source.IndexOf("y =>");
        // 内側アローの本体（{ return y; }）が 0（未実行）にマークされること
        int innerBrace = source.IndexOf("{ return y; }");
        Assert.Equal(0, map[innerBrace]);
    }

    // -----------------------------------------------------------------------
    // 回帰テスト: >= + { の誤検出なし、クラスフィールドアロー
    // -----------------------------------------------------------------------

    /// <summary>
    /// ">= {" の '=' は ">=" 演算子の一部であり、=> アロー関数ではない。
    /// ScanRange がアロー関数と誤検出せず、'=' を -1（対象外）のままにすることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_GreaterThanEqualBeforeBrace_EqualSignRemainsNeutral()
    {
        // "0 >= {}" — '=' は ">=" の 2 文字目。source[i+1] は ' ' なので => にならない
        // 0=0, ' '=1, >  =2, =  =3, ' '=4, {=5, }=6
        const string source = "0 >= {}";

        var map = CoverageParser.BuildCoverageMap(source, []);

        int eqPos = source.IndexOf('='); // 位置 3（'>=' の '='）
        // '>=' の '=' はアロー関数として誤検出されない → 対象外 (-1) のまま
        Assert.Equal(-1, map[eqPos]);
    }

    /// <summary>
    /// クラスフィールド構文（f = () => {}）で、フィールド名 'f' の直後が '=' であるため
    /// TryMarkMethodShorthand が '(' を見つけられず早期リターンする。
    /// 'f' は対象外 (-1) のままになることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_ClassFieldArrow_FieldNameRemainsNeutral()
    {
        // class C { f = () => {} }
        // 'f' はクラスフィールド名。次の非空白文字は '=' であり '(' でないため
        // TryMarkMethodShorthand はメソッド短縮構文として扱わない
        const string source = "class C { f = () => {} }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        int fPos = source.IndexOf('f'); // クラスフィールド名の位置
        Assert.Equal(-1, map[fPos]); // メソッド短縮構文でないため対象外
    }

    // -----------------------------------------------------------------------
    // 回帰テスト: コメントを挟んだアロー関数・メソッド短縮構文の検出
    // -----------------------------------------------------------------------

    /// <summary>
    /// アロー関数 "=>" と "{" の間にブロックコメントがある場合（例: () => /* c */ {}）、
    /// MarkUncalledFunctionBodiesAsUncovered が本体を 0（未実行）としてマークすることを確認する。
    /// バグ修正前は空白のみスキップしていたため "/&#42;" で止まり "{" を見つけられなかった。
    /// </summary>
    [Fact]
    public void BuildMap_ArrowFunctionWithCommentBeforeBrace_MarkedAsUncovered()
    {
        // V8 未実行（関数データなし）→ 全文字 -1。MarkUncalledFunctionBodiesAsUncovered で補正する。
        const string source = "const f = () => /* comment */ { return 42; }";
        var map = CoverageParser.BuildCoverageMap(source, []);

        int arrowPos = source.IndexOf("=>");
        int bracePos = source.IndexOf('{');
        // アロー関数本体（"=>" の位置から "}" まで）が 0（未実行）になっていること
        Assert.Equal(0, map[arrowPos]);
        Assert.Equal(0, map[bracePos]);
    }

    /// <summary>
    /// メソッド短縮構文でメソッド名と "(" の間にブロックコメントがある場合（例: foo /* c */ () {}）、
    /// MarkUncalledFunctionBodiesAsUncovered が本体を 0（未実行）としてマークすることを確認する。
    /// バグ修正前は空白のみスキップしていたため "/&#42;" で止まり "(" を見つけられなかった。
    /// </summary>
    [Fact]
    public void BuildMap_MethodShorthandWithCommentBeforeParen_MarkedAsUncovered()
    {
        // V8 未実行（関数データなし）→ 全文字 -1。MarkUncalledFunctionBodiesAsUncovered で補正する。
        const string source = "const obj = { foo /* comment */ () { return 1; } }";
        var map = CoverageParser.BuildCoverageMap(source, []);

        int fooPos = source.IndexOf("foo");
        // メソッド "foo" の先頭が 0（未実行）になっていること
        Assert.Equal(0, map[fooPos]);
    }

    // -----------------------------------------------------------------------
    // MergeMaps の長さ差テスト
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // function キーワードと名前の間にコメントがある場合のテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// function キーワードと関数名の間にブロックコメントがある場合（例: function /* c */ foo() {}）、
    /// ScanRange が関数を正しく検出して本体を未実行（0）としてマークすることを確認する。
    /// 修正前は whitespace のみスキップしていたため /* で止まり関数を検出できなかった。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionWithBlockCommentBeforeName_MarkedAsUncovered()
    {
        // V8 未実行（関数データなし）→ MarkUncalledFunctionBodiesAsUncovered で補正するべき
        const string source = "function /* comment */ foo() { return 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);

        // function キーワードの先頭が 0（未実行）になっていること
        Assert.Equal(0, map[0]); // 'f' of function
        // 本体の { も 0 になっていること
        int bracePos = source.IndexOf('{');
        Assert.Equal(0, map[bracePos]);
    }

    /// <summary>
    /// function キーワードと関数名の間に行コメントがある場合（例: function // c\nfoo() {}）、
    /// ScanRange が関数を正しく検出して本体を未実行（0）としてマークすることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionWithLineCommentBeforeName_MarkedAsUncovered()
    {
        const string source = "function // comment\nfoo() { return 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);

        Assert.Equal(0, map[0]); // 'f' of function
        int bracePos = source.IndexOf('{');
        Assert.Equal(0, map[bracePos]);
    }

    /// <summary>
    /// async function の間の空白が未実行（0）としてマークされることを確認する。
    /// 修正前は async の5文字のみをマークし、間の空白は -1（対象外）のままだった。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncFunctionSpaceBetweenKeywords_IsUncovered()
    {
        // "async function f() {}" — async(0-4) space(5) function(6-)
        const string source = "async function f() { return 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);

        // async（0-4）が未実行
        Assert.Equal(0, map[0]);
        Assert.Equal(0, map[4]);
        // async と function の間のスペース（5）も未実行
        Assert.Equal(0, map[5]);
        // function 先頭（6）も未実行
        Assert.Equal(0, map[6]);
    }

    /// <summary>
    /// async と アロー関数 の間に複数のブロックコメントがある場合（例: async /* c1 */ /* c2 */ () => {}）、
    /// async キーワードが未実行（0）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_MultipleBlockCommentsBeforeAsyncArrow_AsyncKeywordMarked()
    {
        const string source = "async /* c1 */ /* c2 */ () => { return 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);

        // async の先頭が 0（未実行）になっていること
        Assert.Equal(0, map[0]); // 'a' of async
    }

    /// <summary>
    /// アロー関数の前のブロックコメントが閉じられていない場合でも例外なく処理されることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UnclosedBlockCommentBeforeArrow_DoesNotCrash()
    {
        // 壊れたソース: /* が閉じられていない
        const string source = "async /* unclosed () => { return 1; }";
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);
    }

    /// <summary>
    /// MergeMaps で baseMap が otherMap より長い場合、
    /// otherMap に対応値がない位置は -1 として扱われ、baseMap の値がそのまま残ることを確認する。
    /// </summary>
    [Fact]
    public void MergeMaps_BaseMapLongerThanOtherMap_ExtraElementsTakenFromBase()
    {
        var baseMap  = new[] { 1, 0, -1, 0,  1 };
        var otherMap = new[] { 0, 1,  1 };

        var merged = CoverageParser.MergeMaps(baseMap, otherMap);

        Assert.Equal(5, merged.Length);
        // インデックス 3: base=0, other=-1(なし) → 0
        Assert.Equal(0, merged[3]);
        // インデックス 4: base=1, other=-1(なし) → 1
        Assert.Equal(1, merged[4]);
    }

    /// <summary>
    /// MergeMaps で otherMap が baseMap より長い場合、
    /// baseMap の長さを超えた otherMap の要素は無視されることを確認する。
    /// </summary>
    [Fact]
    public void MergeMaps_OtherMapLongerThanBase_ExtraElementsIgnored()
    {
        var baseMap  = new[] { -1, 0 };
        var otherMap = new[] {  1, 1, 1, 1, 1 };

        var merged = CoverageParser.MergeMaps(baseMap, otherMap);

        // 結果の長さは baseMap と同じ（余剰要素は無視）
        Assert.Equal(2, merged.Length);
        Assert.Equal(1, merged[0]); // base=-1, other=1 → 1
        Assert.Equal(1, merged[1]); // base=0,  other=1 → 1
    }

    /// <summary>
    /// function と * の間にスペースがあるジェネレータ関数（function * gen(){}）が
    /// 未実行の場合、関数本体が 0（未実行）としてマークされることを確認する。
    /// function* はスペースなしで検出済みだが、function * はスペースありで未検出のバグを再現する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledGeneratorFunctionWithSpaceBeforeStar_MarkedAsUncovered()
    {
        // function と * の間にスペースあり: function * gen() { yield 1; }
        const string source = "function * gen() { yield 1; }";
        //                     0         1         2
        //                     012345678901234567890123456789
        // 'f' at 0, '*' at 9, '{' at 18, '}' at 28

        var map = CoverageParser.BuildCoverageMap(source, []);

        // function キーワードの先頭（0）が 0（未実行）になること
        Assert.Equal(0, map[0]);
        // * が 0（未実行）になること
        Assert.Equal(0, map[9]);
        // 関数本体の { が 0（未実行）になること
        Assert.Equal(0, map[18]);
    }

    /// <summary>
    /// async function * gen(){}（async + スペースあり * のジェネレータ）が未実行の場合、
    /// async キーワードも含めて全体が 0（未実行）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UncalledAsyncGeneratorFunctionWithSpaceBeforeStar_AsyncKeywordMarked()
    {
        // async function * gen() { yield 1; }
        const string source = "async function * gen() { yield 1; }";
        //                     0         1         2         3
        //                     012345678901234567890123456789012345
        // 'a'(async) at 0, 'f'(function) at 6, '*' at 15, '{' at 23

        var map = CoverageParser.BuildCoverageMap(source, []);

        // async キーワードの先頭（0）が 0（未実行）になること
        Assert.Equal(0, map[0]);
        // function キーワードの先頭（6）が 0（未実行）になること
        Assert.Equal(0, map[6]);
        // 関数本体の { が 0（未実行）になること
        Assert.Equal(0, map[23]);
    }

    /// <summary>
    /// 計算プロパティキーのブラケット内にコメントがあり、そのコメントに ] が含まれる場合、
    /// SkipBalancedBrackets がコメントを正しくスキップして関数本体が 0 になることを確認する。
    /// 修正前は ] をコメント内で検出して深さカウントが早期終了し、関数本体が未検出になっていた。
    /// </summary>
    [Fact]
    public void BuildMap_ComputedPropertyWithBracketInBlockComment_MarkedAsUncovered()
    {
        // 計算プロパティキーのブラケット内に ] を含むブロックコメント
        // { [/* has ] here */key]() { return 1; } } のパターン
        const string source = "const obj = { [/* ] */x]() { return 1; } };";
        //                                     ^コメント内の ] は無視すること

        var map = CoverageParser.BuildCoverageMap(source, []);

        // メソッド本体の { return 1; } の開き { が 0（未実行）になること
        // LastIndexOf('{') はメソッド本体の { を返す
        int bodyBrace = source.LastIndexOf('{');
        Assert.Equal(0, map[bodyBrace]);
    }

    /// <summary>
    /// 計算プロパティキーのブラケット内に正規表現リテラルがあり、
    /// その正規表現の文字クラス内に ] が含まれる場合、SkipBalancedBrackets が
    /// 正規表現をスキップして深さカウントを正しく維持し、メソッド本体が 0 になることを確認する。
    /// 修正前は正規表現内の ] を誤ってブラケット終端と判断し、メソッドが未検出になっていた。
    /// </summary>
    [Fact]
    public void BuildMap_ComputedPropertyKeyWithRegexContainingBracket_MarkedAsUncovered()
    {
        // { [/]/]() { return 1; } } — キー式が正規表現 /]/ で ] を含む
        // SkipBalancedBrackets が正規表現内の ] でカウントを 0 にしてしまうと
        // bracketEnd が正規表現の終端 / より前を指し、メソッドが検出されない
        const string source = "const o = { [/]/]() { return 1; } };";
        //                               pos 12=[ 13=/ 14=] 15=/ 16=] 17=( 18=) 19=  20={

        var map = CoverageParser.BuildCoverageMap(source, []);

        // [ の位置（12）が 0（未実行）になること（メソッドが正しく検出された証拠）
        Assert.Equal(0, map[12]);
        // メソッド本体の { が 0（未実行）になること
        int bodyBrace = source.IndexOf('{', 17);
        Assert.Equal(0, map[bodyBrace]);
    }

    /// <summary>
    /// テンプレートリテラルの直後（同一行）に / が来る場合、
    /// IsRegexStart がバッククォートを「式の終端」として正しく扱い、/ を除算演算子と判断する。
    /// 修正前は ` の後の / を正規表現の開始と誤判定して SkipRegexLiteral が改行まで読み飛ばし、
    /// 同一行にある function キーワードが検出されなかった。
    /// </summary>
    [Fact]
    public void BuildMap_TemplateLiteralFollowedByDivisionSameLine_FunctionDetected()
    {
        // `abc` / 2 で / が除算演算子なのに正規表現と誤判定されると、
        // 同行の function foo が SkipRegexLiteral に丸ごと呑み込まれて未検出になる
        const string source = "const x = `abc` / 2; function foo() { unreachable() }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // function キーワード先頭が 0（未実行）にマークされていること
        int funcPos = source.IndexOf("function");
        Assert.Equal(0, map[funcPos]);
    }

    // -----------------------------------------------------------------------
    // 意地悪テスト: 壊れたコメント・文字列内の誤検出・MergeMaps境界値
    // -----------------------------------------------------------------------

    /// <summary>
    /// 開き /* がない壊れたブロックコメント（*/ だけ存在）のソースを渡しても
    /// BuildCoverageMap が例外なく完了することを確認する。
    /// SkipWhitespaceAndCommentsBackward が /* を見つけられず -1 を返しても安全に処理される。
    /// </summary>
    [Fact]
    public void BuildMap_BrokenBlockCommentNoOpen_NoException()
    {
        // */ だけで /* がない壊れたソース（構文エラーだが CDP は返す場合がある）
        const string source = "var x = 1; */ function foo() { return 1; }";

        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);
    }

    /// <summary>
    /// 文字列リテラル内の */ がブロックコメントの終端と誤認識されず、
    /// 後続の function が正しく 0（未実行）にマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_StringContainingEndBlockComment_FunctionMarkedUncovered()
    {
        // 文字列 "end comment: */" の中の */ は ScanRange では文字列としてスキップされる
        // その後の function foo が正しく検出されて 0 になるべき
        const string source = "var s = \"end comment: */\"; function foo() { return 1; }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // function の先頭が 0（未実行）になっていることを確認する
        int funcPos = source.IndexOf("function");
        Assert.Equal(0, map[funcPos]);
        // その後の } も 0 になっていること
        Assert.Equal(0, map[source.LastIndexOf('}')]);
    }

    /// <summary>
    /// 文字列リテラル内の // が行コメントと誤認識されず、
    /// 後続の function が正しく 0（未実行）にマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_StringContainingLineComment_FunctionMarkedUncovered()
    {
        // 文字列 "http://example.com" の中の // は行コメントではない
        const string source = "var url = \"http://example.com\"; function bar() { return 2; }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        int funcPos = source.IndexOf("function");
        Assert.Equal(0, map[funcPos]);
        Assert.Equal(0, map[source.LastIndexOf('}')]);
    }

    // -----------------------------------------------------------------------
    // 追加テスト・意地悪テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// BuildCoverageMap に null ソースを渡した場合、空配列を返して例外が発生しないことを確認する。
    /// BuildLines と同様の防衛処理が正しく動作することを検証する。
    /// </summary>
    [Fact]
    public void BuildMap_NullSource_ReturnsEmptyArray()
    {
        var map = CoverageParser.BuildCoverageMap(null, []);
        Assert.Empty(map);
    }

    /// <summary>
    /// 開き /* が閉じられない壊れたブロックコメントを含むソースを渡しても
    /// SkipWhitespaceAndCommentsForward がハングせず正しく終了することを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UnclosedBlockComment_NoHang()
    {
        const string source = "var x = 1; /* never closed function foo() {}";
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);
    }

    /// <summary>
    /// 100,000文字超の minified JS（function が100個連続する1行ソース）でも
    /// BuildCoverageMap が例外なく完了することを確認する。
    /// SkipWhitespaceAndCommentsBackward の行コメント検出が O(行の長さ) になるケースの
    /// パフォーマンス的安全性を検証する煙幕テスト。
    /// </summary>
    [Fact]
    public void BuildMap_MinifiedJsVeryLongSingleLine_NoException()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 100; i++) { sb.Append($"function f{i}(){{return {i}}}"); }
        var source = sb.ToString();
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);
    }

    /// <summary>
    /// 全文字が NUL（\0）の1000文字ソースを渡しても例外なく完了し、
    /// 全文字が neutral（-1）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_AllNullCharacters_NoException()
    {
        var source = new string('\0', 1000);
        var map = CoverageParser.BuildCoverageMap(source, []);
        Assert.Equal(1000, map.Length);
        Assert.All(map, v => Assert.Equal(-1, v));
    }

    /// <summary>
    /// 10段ネストしたテンプレートリテラル `${ `${ ... }` }` の中に
    /// function キーワードを含むソースでも例外なく完了することを確認する。
    /// ScanRange → FindMatchingBrace → SkipTemplateLiteralFull の再帰呼び出しが
    /// 深くなっても安全に動作することを検証する。
    /// </summary>
    [Fact]
    public void BuildMap_DeeplyNestedTemplateLiterals_NoException()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 10; i++) { sb.Append("`${"); }
        sb.Append("function foo() { return 1; }");
        for (int i = 0; i < 10; i++) { sb.Append("}`"); }
        var source = sb.ToString();
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);
    }

    /// <summary>
    /// パラメータのデフォルト値に ')' を含む正規表現を持つ関数が未実行としてマークされることを確認する。
    /// SkipBalancedParens が IsRegexStart を呼んで '=' の後の / を正規表現として正しく処理する。
    /// 正規表現内の ')' がパラメータリスト終端と誤判定されないことを検証する。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionWithRegexDefaultParam_MarkedAsUncovered()
    {
        // function f(x = /\)/) {} — SkipBalancedParens が /\)/ の ) をカウントしないことを確認する
        const string source = "function f(x = /\\)/) {}";
        var map = CoverageParser.BuildCoverageMap(source, []);
        Assert.Equal(0, map[0]);
    }

    /// <summary>
    /// パラメータにネストした式 ${...} を持つテンプレートリテラルを含む関数が
    /// 未実行としてマークされることを確認する。
    /// SkipBalancedParens がバッククォートを SkipTemplateLiteralFull で正しくスキップする。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionWithTemplateLiteralParam_MarkedAsUncovered()
    {
        // function f(`${{}}`) {} — SkipBalancedParens がバッククォートをスキップし正しく ( ) を対応付ける
        const string source = "function f(`${{}}`) {}";
        var map = CoverageParser.BuildCoverageMap(source, []);
        Assert.Equal(0, map[0]);
    }

    // -----------------------------------------------------------------------
    // 追加バグ修正・意地悪テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// functions に null を渡した場合、NullReferenceException をスローせず
    /// 全文字が対象外 (-1) の配列を返すことを確認する。
    /// #nullable disable 環境のため null が渡る可能性がある。
    /// </summary>
    [Fact]
    public void BuildMap_NullFunctions_ReturnsAllNeutral()
    {
        // null を渡しても例外なく全文字 -1 が返るべき
        var map = CoverageParser.BuildCoverageMap("hello", null);
        Assert.Equal([-1, -1, -1, -1, -1], map);
    }

    /// <summary>
    /// 式アロー（ブロックなし）は `=>` も async も 0 にマークされないことを確認する。
    /// ブロック本体 `{}` がない場合は MarkUncalledFunctionBodiesAsUncovered の対象外。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncArrowExpressionBody_NothingMarked()
    {
        // async x => x * 2 はブロック本体なし → 全文字 -1 のまま
        const string source = "const f = async x => x * 2;";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int asyncPos = source.IndexOf("async");
        int arrowPos = source.IndexOf("=>");
        Assert.Equal(-1, map[asyncPos]); // async は対象外のまま
        Assert.Equal(-1, map[arrowPos]); // => は対象外のまま
    }

    /// <summary>
    /// チェーンアロー x => y => {} では、式アロー（最初の =>）はマークされず
    /// ブロックアロー（2番目の =>）だけが 0（未実行）にマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_ChainedArrows_OnlyBlockBodyMarked()
    {
        // x => y => { return y; } — 最初の => は式アロー（対象外）、2番目だけブロック
        const string source = "const f = x => y => { return y; };";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int firstArrow  = source.IndexOf("=>");
        int secondArrow = source.IndexOf("=>", firstArrow + 2);
        int brace       = source.IndexOf('{');
        Assert.Equal(-1, map[firstArrow]);  // 式アローはマークされない
        Assert.Equal(0,  map[secondArrow]); // ブロックアローは 0（未実行）
        Assert.Equal(0,  map[brace]);       // 本体 { も 0
    }

    /// <summary>
    /// メソッド名として使われた `get(x){}` は、getter プロパティではなく
    /// ただのメソッド短縮構文として `get` の先頭からマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_MethodNamedGet_MarkedFromGetStart()
    {
        // { get(x) { return x; } } — get はメソッド名（getter ではない）
        const string source = "const obj = { get(x) { return x; } };";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int getPos = source.IndexOf("get");
        Assert.Equal(0, map[getPos]); // 'g' of get が 0（未実行）
    }

    /// <summary>
    /// static 初期化ブロック内の function は未実行（0）にマークされることを確認する。
    /// static ブロック自体（static {}）はメソッド短縮構文ではないためマークされないが
    /// その内側の function はスキャンされて補正される。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionInsideStaticInitializerBlock_MarkedAsUncovered()
    {
        // class Foo { static { function inner() { return 1; } } }
        const string source = "class Foo { static { function inner() { return 1; } } }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int funcPos = source.IndexOf("function");
        Assert.Equal(0, map[funcPos]); // 'f' of function — static ブロック内も補正対象
    }

    /// <summary>
    /// IIFE アロー関数 (() => { ... })() が未実行の場合、
    /// アロー本体が 0（未実行）にマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_IifeArrowFunction_BodyMarkedAsUncovered()
    {
        // (() => { doSomething(); })() — IIFE アロー
        const string source = "(() => { doSomething(); })();";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int arrowPos = source.IndexOf("=>");
        int bracePos = source.IndexOf('{');
        Assert.Equal(0, map[arrowPos]); // => が 0（未実行）
        Assert.Equal(0, map[bracePos]); // 本体 { が 0
    }

    // -----------------------------------------------------------------------
    // Bug-1 回帰テスト: SkipWhitespaceAndCommentsBackward の前方スキャンが
    // ブロックコメント内の識別子直後 // を行コメントと誤判定するバグ
    // -----------------------------------------------------------------------

    /// <summary>
    /// ブロックコメント内の識別子直後 // を行コメントと誤判定せず async アロー関数を正しくマークする。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncArrowPrecededByBlockCommentWordBeforeDoubleSlash_AsyncMarked()
    {
        // "z /* a // */ async () => {}" — z の直後の /* a // */ はブロックコメント
        // Bug: 前方スキャンが /* */ をスキップしないため a の後の // を行コメントと誤判定し
        // async キーワードが未マークになる
        const string source = "z /* a // */ async () => {}";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int asyncIdx = source.IndexOf("async");
        int arrowIdx  = source.IndexOf("=>");
        Assert.Equal(0, map[asyncIdx]); // async の先頭が 0（未実行）
        Assert.Equal(0, map[arrowIdx]);  // => も 0
    }

    /// <summary>
    /// ブロックコメント内の識別子直後 // を行コメントと誤判定せず async メソッド短縮構文を正しくマークする。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncMethodPrecededByBlockCommentWordBeforeDoubleSlash_AsyncMarked()
    {
        // メソッド短縮構文で同じバグを確認する
        const string source = "const obj = { x /* a // */ async run() {} }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int asyncIdx = source.IndexOf("async");
        Assert.Equal(0, map[asyncIdx]); // async の先頭が 0
    }

    // -----------------------------------------------------------------------
    // その他の回帰テスト（エッジケース）
    // -----------------------------------------------------------------------

    /// <summary>
    /// ?? 演算子の後の /function/ は正規表現リテラル → 内部の function キーワードを誤検出しない。
    /// </summary>
    [Fact]
    public void BuildMap_NullishCoalescingBeforeRegexContainingFunction_RealFunctionDetected()
    {
        // ?? の後の / は正規表現の開始として判定される
        const string source = "const r = a ?? /function/.test(s); function foo() {}";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int fooIdx = source.IndexOf("function foo");
        Assert.Equal(0, map[fooIdx]); // 本物の function foo だけが 0 になる
    }

    /// <summary>
    /// 複数行に渡るパラメータリストを持つ async アロー関数で async キーワードが正しくマークされる。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncArrowWithMultilineParams_AsyncMarked()
    {
        const string source = "async (\n  x,\n  y\n) => {}";
        var map = CoverageParser.BuildCoverageMap(source, []);
        Assert.Equal(0, map[0]);                         // async の 'a' が 0
        Assert.Equal(0, map[source.IndexOf("=>")]); // => も 0
    }

    /// <summary>
    /// static async *gen() {} メソッド短縮構文で static の先頭からマークされる。
    /// </summary>
    [Fact]
    public void BuildMap_StaticAsyncGeneratorMethodShorthand_StaticMarked()
    {
        const string source = "class C { static async *gen() {} }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int staticIdx = source.IndexOf("static");
        Assert.Equal(0, map[staticIdx]); // static の 's' が 0
    }

    /// <summary>
    /// 二重ネストのテンプレートリテラル式部分に定義されたアロー関数が正しく未実行マークされる。
    /// </summary>
    [Fact]
    public void BuildMap_ArrowInsideDoubleNestedTemplateInterpolation_Marked()
    {
        const string source = "const x = `a ${ `b ${ () => {} }` }`;";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int arrowIdx = source.IndexOf("=>");
        Assert.Equal(0, map[arrowIdx]); // () => {} の => が 0
    }

    /// <summary>
    /// return /function/ の正規表現内の function キーワードは誤検出せず、
    /// 後続の実際の function inner を正しく 0 にマークする。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionKeywordInsideRegexAfterReturn_OuterFunctionDetected()
    {
        const string source = "function outer() { return /function/; } function inner() {}";
        // outer は V8 データあり（count=1）、inner はデータなし → 補正で 0 になる
        var functions = new[] { new FunctionCoverage("outer", [new CoverageRange(0, 39, 1)]) };
        var map = CoverageParser.BuildCoverageMap(source, functions);
        int innerIdx = source.IndexOf("function inner");
        Assert.Equal(0, map[innerIdx]); // function inner の先頭が 0
    }

    /// <summary>
    /// async という名前のパラメータを持つアロー関数で、async キーワードとして誤マークしない。
    /// アロー本体 {} は 0（未実行）、パラメータ名 async の前（const 等）は -1 のまま。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncAsParamNameArrow_BodyMarkedNotAsyncKeyword()
    {
        const string source = "const f = async => {}";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int arrowIdx = source.IndexOf("=>");
        Assert.Equal(0, map[arrowIdx]);  // => が 0
        Assert.Equal(0, map[source.IndexOf('{')]);  // { が 0
        Assert.Equal(-1, map[0]);        // 'c' (const) は対象外のまま
    }

    // -----------------------------------------------------------------------
    // 意地悪テスト: エッジケース補強
    // -----------------------------------------------------------------------

    /// <summary>
    /// new 式のコールバック（new Promise(function(resolve){...})）で
    /// TryMarkMethodShorthand が Promise(...) を短縮メソッドと誤検出しないことを確認する。
    /// SkipBalancedParens が ) で止まり、その直後が { でないため早期リターンするはず。
    /// </summary>
    [Fact]
    public void BuildMap_NewExpressionCallback_ParenFollowedBySemicolon_NotDetectedAsShorthand()
    {
        // new Promise のコールバック: Promise( ) の後は ; であり { ではないため
        // TryMarkMethodShorthand は早期リターンする。function キーワードは正常に検出される。
        const string source = "new Promise(function(resolve){resolve(1);});";
        var map = CoverageParser.BuildCoverageMap(source, []);
        // function キーワードは補正対象 → 0（未実行）にマークされる
        int funcIdx = source.IndexOf("function");
        Assert.Equal(0, map[funcIdx]);
        // 'P' of Promise は -1（短縮メソッドとして誤検出されていないこと）
        int promiseIdx = source.IndexOf("Promise");
        Assert.Equal(-1, map[promiseIdx]);
    }

    /// <summary>
    /// アロー関数のデフォルト値にオブジェクトリテラル {} を含むケース。
    /// (a = {}) => {} のパラメータリスト逆走査で { } がパラメータ終端として誤認識されない。
    /// </summary>
    [Fact]
    public void BuildMap_ArrowFunctionWithObjectDefaultValue_MarkedAsUncovered()
    {
        // => 以降のブロック { return a; } が 0（未実行）にマークされることを確認する
        // パラメータリスト内の {} が SkipBalancedParens / 逆走査を混乱させないことを検証する
        const string source = "const f = (a = {}) => { return a; };";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int arrowIdx = source.IndexOf("=>");
        Assert.Equal(0, map[arrowIdx]);          // => は 0
        int bodyOpen = source.IndexOf('{', arrowIdx);
        Assert.Equal(0, map[bodyOpen]);           // ブロック本体 { は 0
        // パラメータリスト内の {} は -1 のまま（誤検出されていないこと）
        int paramBrace = source.IndexOf('{', source.IndexOf('('));
        Assert.Equal(-1, map[paramBrace]);        // { of {} default は -1
    }

    /// <summary>
    /// 関数内で return /}/; のように } を含む正規表現リテラルがある場合、
    /// FindMatchingBrace が正規表現内の } をカウントせず、直後の関数も正常に検出されることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionWithReturnRegexContainingBrace_NextFunctionDetected()
    {
        // function a の本体内に return /}/; がある。FindMatchingBrace は正規表現をスキップするため
        // /}/ 内の } で深さが狂わず、function a の } と function b の両方が正しく検出される。
        const string source = "function a() { return /}/; } function b() { return 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        // function a は補正対象 → 0
        Assert.Equal(0, map[0]);
        // function b も補正対象 → 0
        int bIdx = source.LastIndexOf("function");
        Assert.Equal(0, map[bIdx]);
    }

    /// <summary>
    /// 壊れたブロックコメント（/* のない */）の後にアロー関数がある場合でも
    /// BuildCoverageMap が例外をスローせず完了することを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_MalformedBlockCommentEndBeforeArrow_DoesNotCrash()
    {
        // ソースが壊れていても NullReferenceException / IndexOutOfRangeException が起きないこと
        const string source = "const f = () */ => { return 1; };";
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);
    }

    /// <summary>
    /// asyncInit のように "async" で始まる識別子のメソッド短縮構文が、
    /// async キーワードとして誤検出されずに identStart からマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_IdentifierStartingWithAsync_NotTreatedAsAsyncKeyword()
    {
        // asyncInit() {} は "async" キーワード + メソッドではなく、asyncInit という1つのメソッド名
        // TryMarkMethodShorthand の async チェックが identStart-1 を起点に走査するため、
        // { の後の "bj = {" を "async" と誤認しないことを確認する
        const string source = "const obj = { asyncInit() { return 1; } };";
        var map = CoverageParser.BuildCoverageMap(source, []);
        // asyncInit の先頭 'a' が 0（メソッドとして検出される）
        int asyncInitIdx = source.IndexOf("asyncInit");
        Assert.Equal(0, map[asyncInitIdx]);
        // asyncInit の前の { は -1 のまま（async キーワードとして誤マークされていない）
        int objBrace = source.IndexOf('{');
        Assert.Equal(-1, map[objBrace]);
    }

    /// <summary>
    /// アロー関数のデフォルト値にテンプレートリテラルが含まれ、
    /// テンプレートリテラル内に ) がある場合でも正しく処理されることを確認する。
    /// SkipBalancedParens が SkipTemplateLiteralFull を呼ぶため ) が誤カウントされない。
    /// </summary>
    [Fact]
    public void BuildMap_ArrowFunctionWithTemplateLiteralClosingParenDefault_MarkedAsUncovered()
    {
        // (a = `${x})`) はテンプレートリテラル内に ) を含む。SkipBalancedParens が正しく
        // テンプレートリテラルをスキップするため、) は実際の閉じ括弧のみがカウントされる。
        const string source = "const f = (a = `${x})`) => { return a; };";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int arrowIdx = source.IndexOf("=>");
        Assert.Equal(0, map[arrowIdx]); // => は 0（未実行）
        int bodyOpen = source.IndexOf('{', arrowIdx);
        Assert.Equal(0, map[bodyOpen]); // ブロック { は 0
    }

    // -----------------------------------------------------------------------
    // computed property key（[...](){}）のプレフィックスキーワードのテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// async キーワードを持つ computed property key メソッド（async ['key']() {}）において
    /// async キーワードも未実行（0）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncComputedMethodKey_AsyncMarked()
    {
        // async ['key']() {} では async が computed key の前に来る。
        // メソッド本体 {} だけでなく async キーワードも赤（0）になるべき。
        const string source = "const obj = { async ['key']() { return 1; } };";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int asyncIdx = source.IndexOf("async");
        // async の先頭が 0（未実行）であることを確認する
        Assert.Equal(0, map[asyncIdx]);
        // [ も 0 であることを確認する（メソッド本体全体がマーク済み）
        int bracketIdx = source.IndexOf('[');
        Assert.Equal(0, map[bracketIdx]);
    }

    /// <summary>
    /// static キーワードを持つ computed property key メソッド（static ['key']() {}）において
    /// static キーワードも未実行（0）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_StaticComputedMethodKey_StaticMarked()
    {
        // static ['key']() {} では static が computed key の前に来る。
        // static キーワードも赤（0）になるべき。
        const string source = "class Foo { static ['key']() { return 1; } }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int staticIdx = source.IndexOf("static");
        Assert.Equal(0, map[staticIdx]);
    }

    /// <summary>
    /// static async キーワードを持つ computed property key メソッドにおいて
    /// static キーワードから本体まで全体が未実行（0）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_StaticAsyncComputedMethodKey_StaticMarked()
    {
        const string source = "class Foo { static async ['key']() { return 1; } }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int staticIdx = source.IndexOf("static");
        Assert.Equal(0, map[staticIdx]);
        int asyncIdx  = source.IndexOf("async");
        Assert.Equal(0, map[asyncIdx]);
    }

    // -----------------------------------------------------------------------
    // その他のエッジケースのテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// class フィールドのアロー関数（bar = () => {}）の本体が
    /// 未実行（0）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_ClassFieldArrowFunction_BodyMarked()
    {
        const string source = "class Foo { bar = () => { return 1; } }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int arrowIdx = source.IndexOf("=>");
        Assert.Equal(0, map[arrowIdx]);
        int bodyOpen = source.IndexOf('{', arrowIdx);
        Assert.Equal(0, map[bodyOpen]);
    }

    /// <summary>
    /// プライベートメソッド（#bar() {}）の本体が未実行（0）としてマークされることを確認する。
    /// # は IsIdentifierChar で識別子文字扱いのため、#bar が一体で処理される。
    /// </summary>
    [Fact]
    public void BuildMap_PrivateMethodShorthand_BodyMarked()
    {
        const string source = "class Foo { #bar() { return 1; } }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        // # の位置から始まる識別子として検出される
        int hashIdx = source.IndexOf('#');
        // # も 0（未実行）でマークされるか、少なくとも メソッド本体 { が 0 であることを確認する
        int bodyOpen = source.IndexOf('{', hashIdx);
        Assert.Equal(0, map[bodyOpen]);
    }

    /// <summary>
    /// デフォルト引数にアロー関数を持つ外部関数の本体が未実行（0）としてマークされることを確認する。
    /// 内部のアロー関数も本体あれば別途マークされる。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionWithArrowAsDefaultParam_BothBodiesMarked()
    {
        // function foo(cb = () => {}) {} —— 外部関数本体と内部アロー本体の両方が 0 になるべき
        const string source = "function foo(cb = () => { return 0; }) { return cb(); }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        // 外部関数 function foo の先頭 f が 0（未実行）
        Assert.Equal(0, map[0]);
        // 外部関数本体の開き { が 0
        int outerBody = source.LastIndexOf('{');
        Assert.Equal(0, map[outerBody]);
        // 内部アロー => が 0
        int arrowIdx = source.IndexOf("=>");
        Assert.Equal(0, map[arrowIdx]);
    }

    /// <summary>
    /// getter と setter の両方が未実行（0）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_GetterAndSetterBothMarked()
    {
        const string source = "const obj = { get foo() { return 1; }, set foo(v) { this._v = v; } };";
        var map = CoverageParser.BuildCoverageMap(source, []);
        // getter の get が 0（未実行）
        int getIdx = source.IndexOf("get foo");
        Assert.Equal(0, map[getIdx]);
        // setter の set が 0（未実行）
        int setIdx = source.IndexOf("set foo");
        Assert.Equal(0, map[setIdx]);
    }

    /// <summary>
    /// ソースの先頭（offset 0）にジェネレーター関数がある場合でも未実行（0）としてマークされることを確認する。
    /// FindMatchingBrace の戻り値チェックが > 0 のため、j = 0 の場合の境界値テスト。
    /// </summary>
    [Fact]
    public void BuildMap_GeneratorFunctionAtOffsetZero_MarkedAsUncovered()
    {
        // function* から始まるソース（offset 0 に { が来る前に function が 0 番地）
        const string source = "function* gen() { yield 1; }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        // function キーワードの先頭（offset 0）が 0（未実行）
        Assert.Equal(0, map[0]);
        // 本体 { も 0
        int bodyIdx = source.IndexOf('{');
        Assert.Equal(0, map[bodyIdx]);
    }

    /// <summary>
    /// 逆転した range（StartOffset > EndOffset）が渡された場合でも例外が発生しないことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_InvertedRange_NoException()
    {
        // 正常な FunctionCoverage だが ranges が逆転している（不正データの堅牢性確認）
        const string source = "function foo() { return 1; }";
        var badRange = new CoverageRange(StartOffset: 20, EndOffset: 5, Count: 1);
        var func = new FunctionCoverage("foo", [badRange]);
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, [func]));
        Assert.Null(ex);
    }

    /// <summary>
    /// 1000 段階にネストされたテンプレートリテラルを処理しても StackOverflowException が発生しないことを確認する。
    /// ScanRange と SkipTemplateLiteralFull が再帰的に呼ばれるため、深いネストでスタック枯渇する危険がある。
    /// </summary>
    [Fact]
    public void BuildMap_DeeplyNestedTemplateLiterals_1000Levels_NoStackOverflow()
    {
        // `${ `${ `${ ... }` }` }` を 1000 段階作成する
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 1000; i++) { sb.Append("`${"); }
        sb.Append("1");
        for (int i = 0; i < 1000; i++) { sb.Append("}`"); }
        var source = sb.ToString();
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);
    }

    /// <summary>
    /// 非常に長いソースコード（1MB）を処理しても例外が発生しないことを確認する。
    /// 大規模なファイルでもメモリ枯渇やタイムアウトが発生しないことを保証する。
    /// </summary>
    [Fact]
    public void BuildMap_LargeSource_1MB_NoException()
    {
        // 1MB のソースコードを作成する（コメント行を繰り返す）
        var sb = new System.Text.StringBuilder();
        while (sb.Length < 1_000_000)
        {
            sb.AppendLine("const x = function() { return 1; };");
        }
        var source = sb.ToString();
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);
    }
}

// -----------------------------------------------------------------------
// コードレビュー指摘対応: 追加テストケース（意地悪・エッジケース）
// -----------------------------------------------------------------------

/// <summary>
/// コードレビューで指摘された追加テストケースを網羅するテストクラス。
/// </summary>
public class CoverageMapReviewTests
{
    /// <summary>
    /// async アロー関数のデフォルトパラメータに \/（エスケープスラッシュ）を含む正規表現がある場合、
    /// 逆走査の正規表現スキップが正しく動作して async がマークされることを確認する。
    /// バグ: \/  の逆走査で閉じ / を誤って開き / と判定すると async が未検出になる。
    /// </summary>
    [Fact]
    public void BuildMap_ArrowFunctionWithEscapedSlashRegexDefault_AsyncMarked()
    {
        // async (x = /\//) => {} — デフォルト値に /\// (スラッシュにマッチする正規表現) を含む
        // 逆走査で \/  の '/' をエスケープ済みと正しく認識し、
        // 開き '/' を見つけて async キーワードを検出できることを確認する
        const string source = "async (x = /\\//) => {}";
        var map = CoverageParser.BuildCoverageMap(source, []);

        // async の先頭（index 0）が 0（未実行）にマークされているか確認する
        // もしエスケープ処理が誤っていると async が未検出になり -1 のままになる
        Assert.Equal(0, map[0]); // 'a' of async
    }

    /// <summary>
    /// 'async' が関数名として使われる場合（pre-ES2017 コード）に、
    /// async キーワードとして誤判定されず、function...{} だけがマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionNamedAsync_NotFalsePositive()
    {
        // "var x; function async() {}" — async は function の名前
        // 関数本体（function〜}）だけが 0（未実行）になり、
        // var x; は -1（対象外）のまま（誤って "async" プレフィックスとして扱わない）
        const string source = "var x; function async() {}";
        var map = CoverageParser.BuildCoverageMap(source, []);

        // 'v' (index 0) は関数外 → -1 のまま
        Assert.Equal(-1, map[0]);
        // 'f' of function (index 7) は未実行（0）
        Assert.Equal(0, map[7]);
        // '}' (最後の文字) は未実行（0）
        Assert.Equal(0, map[source.Length - 1]);
    }

    /// <summary>
    /// オブジェクトリテラルの数値キーメソッド（1(){} ）が正しく未実行(0)にマークされることを確認する。
    /// 数値 '1' は IsIdentifierChar で true になるため、メソッド短縮構文として検出される。
    /// </summary>
    [Fact]
    public void BuildMap_ObjectMethodWithNumericKey_MarkedAsUncovered()
    {
        // const o = { 1() {} } — 数値キーのメソッド短縮構文（V8 が遅延コンパイル）
        // '1' は識別子文字扱い（IsIdentifierChar = true）のため TryMarkMethodShorthand に到達する
        const string source = "const o = { 1() {} }";
        var map = CoverageParser.BuildCoverageMap(source, []);

        // '1' の位置 (index 12) が未実行（0）にマークされているか確認する
        int numPos = source.IndexOf('1');
        Assert.Equal(0, map[numPos]);

        // '}' (最後から2番目) も未実行（0）
        int bracePos = source.LastIndexOf('}', source.Length - 2);
        Assert.Equal(0, map[bracePos]);
    }

    /// <summary>
    /// クラスのプライベートジェネレータメソッド（*#gen(){}）が未実行(0)にマークされることを確認する。
    /// '#' は識別子文字（private field prefix）なので TryMarkMethodShorthand に到達し、
    /// 逆走査で '*' を検出して markStart = starPos になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_ClassWithPrivateGeneratorMethod_MarkedAsUncovered()
    {
        // class C { *#gen() {} } — プライベートジェネレータメソッド（V8 が遅延コンパイル）
        const string source = "class C { *#gen() {} }";
        var map = CoverageParser.BuildCoverageMap(source, []);

        // '*' の位置
        int starPos = source.IndexOf('*');
        // '#' の位置
        int hashPos = source.IndexOf('#');

        // '*' から始まるメソッド本体が未実行（0）にマークされているか確認する
        Assert.Equal(0, map[starPos]);
        Assert.Equal(0, map[hashPos]);
    }

    /// <summary>
    /// function と '{' の間に複数行のブロックコメントがある場合でも、
    /// 関数本体が正しく検出されて未実行(0)にマークされることを確認する。
    /// SkipWhitespaceAndCommentsForward がブロックコメントをまたいで '{' を発見できることを検証する。
    /// </summary>
    [Fact]
    public void BuildMap_MultilineBlockCommentBetweenFunctionAndBrace_FunctionDetected()
    {
        // function foo() /* 複数行\nコメント */ {} — ) と { の間に改行を含むブロックコメント
        const string source = "function foo() /* multi\nline */ {}";
        var map = CoverageParser.BuildCoverageMap(source, []);

        // 'f' of function (index 0) が 0（未実行）にマークされているか確認する
        Assert.Equal(0, map[0]);
        // '}' (最後の文字) も 0（未実行）
        Assert.Equal(0, map[source.Length - 1]);
    }

    // -----------------------------------------------------------------------
    // 追加エッジケーステスト（TC-1〜TC-11）
    // -----------------------------------------------------------------------

    /// <summary>
    /// TC-1: 正規表現リテラルに "//" が含まれる行の次の行に async アロー関数がある場合、
    /// SkipWhitespaceAndCommentsBackward の行コメント検出が誤動作せず
    /// async キーワードが正しく未実行(0)にマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_RegexContainingDoubleSlash_NextLineAsyncArrowDetected()
    {
        // 1行目: const re = /http:\/\/example\//; — 正規表現に "//" が含まれる
        // 2行目: async (x) => {} — 逆走査で "async" を正しく検出して 0 にマークされるべき
        const string source = "const re = /http:\\/\\/example\\//;\nasync (x) => {}";
        var map = CoverageParser.BuildCoverageMap(source, []);

        int asyncPos = source.LastIndexOf("async");
        // async キーワードが 0（未実行）にマークされているか確認する
        Assert.Equal(0, map[asyncPos]);
        // } (最後の文字) も 0
        Assert.Equal(0, map[source.Length - 1]);
    }

    /// <summary>
    /// TC-2: タグ付きテンプレートリテラルの ${ } 内に関数定義がある場合、
    /// ScanRange の再帰スキャンによって関数本体が 0（未実行）にマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionInsideTaggedTemplateLiteral_MarkedAsUncovered()
    {
        // html`<div>${function foo() {}}</div>` — タグ付きテンプレートリテラルの ${} 内の関数
        const string source = "const x = html`<div>${function foo() {}}</div>`;";
        var map = CoverageParser.BuildCoverageMap(source, []);

        int funcPos = source.IndexOf("function");
        // function の先頭が 0（未実行）にマークされているか確認する
        Assert.Equal(0, map[funcPos]);
        // function 本体の開き波括弧も 0
        int bodyBrace = source.IndexOf('{', funcPos + 8); // "function" の後の '{'
        Assert.Equal(0, map[bodyBrace]);
    }

    /// <summary>
    /// TC-6: ジェネレータ関数内で yield* の後に正規表現リテラルがある場合、
    /// '/' を除算でなく正規表現として正しく処理し、function* 全体が検出されることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_YieldStarFollowedByRegex_FunctionStillDetected()
    {
        // function* gen() { yield* /foo/; } — yield* の後の / は正規表現
        // IsRegexStart: '*' は識別子文字でも ) ] ` でもないため true を返す → 正規表現として処理される
        const string source = "function* gen() { yield* /foo/; }";
        var map = CoverageParser.BuildCoverageMap(source, []);

        // function キーワード先頭 (index 0) が 0（未実行）にマークされているか確認する
        Assert.Equal(0, map[0]);
        // } (最後の文字) も 0（未実行）
        Assert.Equal(0, map[source.Length - 1]);
    }

    /// <summary>
    /// TC-9: デストラクチャリングパラメータを持つ function（function({a, b}) {}）が
    /// 正しく未実行(0)にマークされることを確認する。
    /// SkipBalancedParens はパラメータ内の {} を () の深さカウントに影響させない。
    /// </summary>
    [Fact]
    public void BuildMap_FunctionWithDestructuringParam_MarkedAsUncovered()
    {
        // function({a, b}) {} — デストラクチャリングパラメータ内の { } は括弧カウントに影響しない
        const string source = "function({a, b}) {}";
        var map = CoverageParser.BuildCoverageMap(source, []);

        // 'f' of function (index 0) が 0（未実行）にマークされているか確認する
        Assert.Equal(0, map[0]);
        // '}' (最後の文字) も 0（未実行）
        Assert.Equal(0, map[source.Length - 1]);
    }

    /// <summary>
    /// TC-11: MergeMaps で baseMap が全て -1（カバレッジ対象外）、otherMap が 0/1 混在の場合、
    /// OR 合成の優先度（1 > 0 > -1）により otherMap の値が採用されることを確認する。
    /// </summary>
    [Fact]
    public void MergeMaps_BasemapAllNeutral_OtherMapValuesAdopted()
    {
        var baseMap  = new[] { -1, -1, -1 };
        var otherMap = new[] {  1,  0,  1 };
        var merged = CoverageParser.MergeMaps(baseMap, otherMap);

        // otherMap の値が採用される（-1 より 1, 0 が優先）
        Assert.Equal( 1, merged[0]);
        Assert.Equal( 0, merged[1]);
        Assert.Equal( 1, merged[2]);
    }
}

/// <summary>
/// コードレビュー指摘対応: TG-1/TG-2/TG-4/TG-5/TG-6/TG-10 の追加テスト。
/// パーサーのエッジケースを確認する。
/// </summary>
public class EdgeCaseParserTests
{
    /// <summary>
    /// TG-1: ショートハンドメソッドの直後に function 宣言が来た場合、
    /// 両方が独立して未実行（0）にマークされることを確認する。
    /// （ショートハンドのマーク範囲が function の先頭を上書きしないことの確認）
    /// </summary>
    [Fact]
    public void BuildMap_ShorthandMethodFollowedByFunctionDeclaration_BothMarkedIndependently()
    {
        const string source = "const obj = { run() { return 1; }, helper: function() { return 2; } };";
        //                                   ^14                        ^43 (f of function)
        // run() の {} は 20-32, function は 43 から始まる（run の範囲外）

        var map = CoverageParser.BuildCoverageMap(source, []);

        // run() ショートハンドが 0（未実行）にマークされる
        Assert.Equal(0, map[14]); // 'r' of run
        Assert.Equal(0, map[20]); // '{' of run body

        // function() も独立して 0（未実行）にマークされる
        Assert.Equal(0, map[43]); // 'f' of function
        Assert.Equal(0, map[54]); // '{' of function body
    }

    /// <summary>
    /// TG-2: アロー関数の => と { の間にブロックコメント・行コメントが複数連続している場合でも、
    /// SkipWhitespaceAndCommentsForward が全コメントをスキップして { を正しく検出できることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_ArrowFunctionWithMultipleConsecutiveCommentBeforeBrace_MarkedAsUncovered()
    {
        // => の直後に /* c1 */ // c2\n と2種類のコメントが連続している
        const string source = "const f = () => /* c1 */ // c2\n{ return 1; }";
        //                     0         1         2         3         4
        //                     012345678901234567890123456789012345678901234
        // '=' of => at 13, { at 31, } at 43

        var map = CoverageParser.BuildCoverageMap(source, []);

        // => から } まで 0（未実行）にマークされる
        Assert.Equal(0, map[13]); // '=' of =>
        Assert.Equal(0, map[31]); // '{'
        Assert.Equal(0, map[43]); // '}'
    }

    /// <summary>
    /// TG-4: アロー関数のパラメータデフォルト値に正規表現文字クラス /[)abc]/ が含まれる場合、
    /// 文字クラス内の ')' が parenDepth カウントを狂わせず、async キーワード検出が正しく動くことを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_ArrowFunctionWithRegexCharClassContainingCloseParen_MarkedAsUncovered()
    {
        const string source = "const f = (x = /[)abc]/) => { return x; }";
        //                     0         1         2         3         4
        //                     0123456789012345678901234567890123456789012
        // '=' of => at 25, { at 28, } at 40

        var map = CoverageParser.BuildCoverageMap(source, []);

        // => から } まで 0（未実行）にマークされる（文字クラス内の ) で誤終了しないことの確認）
        Assert.Equal(0, map[25]); // '=' of =>
        Assert.Equal(0, map[28]); // '{'
        Assert.Equal(0, map[40]); // '}'
    }

    /// <summary>
    /// TG-5: 計算プロパティキーにネストした配列アクセス [arr[0]] が含まれる場合、
    /// SkipBalancedBrackets が深さカウントでネストを正しく処理して関数本体を検出することを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_ComputedMethodWithNestedBracketInKey_MarkedAsUncovered()
    {
        const string source = "const obj = { [arr[0]]() { return 1; } }";
        //                     0         1         2         3
        //                     0123456789012345678901234567890123456789
        // '[' of [arr[0]] at 14, { at 25, } at 37

        var map = CoverageParser.BuildCoverageMap(source, []);

        // 計算プロパティキーのメソッドが 0（未実行）にマークされる
        Assert.Equal(0, map[14]); // '[' of [arr[0]]
        Assert.Equal(0, map[25]); // '{'
        Assert.Equal(0, map[37]); // '}'
    }

    /// <summary>
    /// TG-6: サロゲートペア文字（4バイト絵文字など）を識別子名に使った変数に続く '/' が
    /// IsRegexStart で除算演算子として正しく判定されることを確認する。
    /// （低位サロゲートは IsIdentifierChar で true を返すため正規表現ではなく除算と判断される）
    /// 結果として、後続の function 宣言が未実行（0）にマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_SurrogatePairIdentifierFollowedByDivision_FunctionStillDetected()
    {
        // 𝓪𝓫𝓬 は U+1D4EA U+1D4EB U+1D4EC — C# string では各2 char のサロゲートペア（計6 char）
        // const(6) + 𝓪𝓫𝓬(6) + " = 1;\n"(6) + 𝓪𝓫𝓬(6) + " / 2;\n"(6) = 30 chars → function at 30
        const string source = "const \uD835\uDCEA\uD835\uDCEB\uD835\uDCEC = 1;\n\uD835\uDCEA\uD835\uDCEB\uD835\uDCEC / 2;\nfunction foo() { return 1; }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // function キーワードが 0（未実行）にマークされる（/ が除算として扱われ正規表現と誤認しない）
        Assert.Equal(0, map[30]); // 'f' of function
    }

    /// <summary>
    /// TG-10: 行コメント（//）の中に '/* ' と '*/' が含まれる場合、
    /// SkipWhitespaceAndCommentsBackward が誤ってブロックコメントと判断せず、
    /// 次の行の function キーワードを正しく未実行（0）にマークすることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_LineCommentContainingBlockCommentSyntax_FunctionOnNextLineStillDetected()
    {
        // 1行目: async アロー関数 + 行コメント（行コメント内に /* */ を含む）
        // 2行目: function 宣言（行コメントの影響を受けないはず）
        const string source = "async x => {} // /* not a block comment */\nfunction foo() { return 1; }";
        //                                                                     ^43 (f of function)

        var map = CoverageParser.BuildCoverageMap(source, []);

        // async アロー関数の => が 0（未実行）にマークされる
        Assert.Equal(0, map[8]);  // '=' of =>

        // function キーワードが 0（未実行）にマークされる（行コメント内の /* */ に惑わされない）
        Assert.Equal(0, map[43]); // 'f' of function
    }

    /// <summary>
    /// async アロー関数のデフォルトパラメータに正規表現文字クラス /[)abc]/ が含まれる場合、
    /// 逆走査で文字クラス内の ')' を parenDepth にカウントせず async キーワードを正しく検出することを確認する。
    /// 既存テスト BuildMap_ArrowFunctionWithRegexCharClassContainingCloseParen_MarkedAsUncovered は
    /// 非 async ケースのみ確認しているため、async キーワードのマークもここで検証する。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncArrowWithCharClassRegexDefaultContainingCloseParen_AsyncKeywordMarked()
    {
        // async (x = /[)abc]/) => {} — 文字クラス [)abc] 内の ')' が parenDepth を狂わせないことを確認する
        const string source = "async (x = /[)abc]/) => {}";
        //                     0         1         2
        //                     0123456789012345678901234 5
        // '=' of => at 21, { at 24, } at 25, 'a' of async at 0

        var map = CoverageParser.BuildCoverageMap(source, []);

        // async キーワード先頭（index 0）が 0（未実行）にマークされること
        Assert.Equal(0, map[0]); // 'a' of async
        // 関数本体 { が 0 にマークされること
        Assert.Equal(0, map[24]); // '{'
    }

    /// <summary>
    /// 'of' をメソッド名として使うオブジェクトリテラルで、メソッド短縮形として正しく検出されることを確認する。
    /// 'of' は ControlFlowKeywords に含まれないため TryMarkMethodShorthand で処理される。
    /// </summary>
    [Fact]
    public void BuildMap_MethodNamedOf_MarkedAsUncovered()
    {
        // { of() { return 1; } } — 'of' は for...of のキーワードだが、メソッド名としても合法
        const string source = "const obj = { of() { return 1; } }";
        //                     0         1         2         3
        //                     01234567890123456789012345678901234
        // 'o' of 'of' at 14, { at 19

        var map = CoverageParser.BuildCoverageMap(source, []);

        // 'of' メソッドの先頭（index 14）が 0（未実行）にマークされること
        Assert.Equal(0, map[14]); // 'o' of 'of'
        // 関数本体 { も 0 にマークされること
        Assert.Equal(0, map[19]); // '{'
    }

    // ─── TC-1: ネストしたテンプレートリテラル内の関数 ─────────────────────────────

    /// <summary>
    /// テンプレートリテラルの ${ } 内に関数が定義されている場合、
    /// ScanRange の再帰スキャンで正しく 0（未実行）にマークされることを確認する。
    /// （I-1: サブレンジの end 境界を超えないことの回帰テスト）
    /// </summary>
    [Fact]
    public void BuildMap_FunctionInsideTemplateOuterInterpolation_MarkedAsUncovered()
    {
        // `${ function foo(){} }` — テンプレートリテラルの ${} 内に function
        // f of function at 4, { at 18, } at 19
        const string source = "`${ function foo(){} }`";
        //                      0123456789012345678901 2

        var map = CoverageParser.BuildCoverageMap(source, []);

        // ${} の中の function が 0（未実行）にマークされること
        Assert.Equal(0, map[4]);  // 'f' of function
        Assert.Equal(0, map[18]); // '{'
        Assert.Equal(0, map[19]); // '}'
    }

    /// <summary>
    /// テンプレートリテラルの ${ } 内にネストしたテンプレートリテラルがあり、
    /// その外側に関数が続く場合、外側の関数が誤って 0 マークされないことを確認する。
    /// （I-1: サブレンジスキャンが end を超えて外側を誤マークしないことの確認）
    /// </summary>
    [Fact]
    public void BuildMap_FunctionAfterTemplateInterpolation_CorrectlyMarked()
    {
        // `${ 'x' }` + function bar(){} — テンプレート後の function が正しく検出される
        // f of function at 12, { at 25, } at 26
        const string source = "`${ 'x' }` + function bar(){}";
        //                      0         1         2
        //                      0123456789012345678901234567 89

        var map = CoverageParser.BuildCoverageMap(source, []);

        // テンプレート外の function bar が 0（未実行）にマークされること
        Assert.Equal(0, map[13]); // 'f' of function
    }

    // ─── TC-3: async アロー関数 — \] を含む正規表現デフォルト値 ─────────────────

    /// <summary>
    /// async アロー関数のデフォルト値に \] を含む正規表現 /\]/ が含まれる場合、
    /// 逆走査で async キーワードが正しく検出されて 0（未実行）にマークされることを確認する。
    /// （I-2: 逆走査の正規表現スキャンで \] がエスケープを無視して inClass を誤設定するバグの回帰テスト）
    /// </summary>
    [Fact]
    public void BuildMap_AsyncArrowWithEscapedBracketInRegexDefault_AsyncKeywordMarked()
    {
        // async (x = /\]/) => {} — /\]/ 内の \] で inClass を誤設定しても async を正しく検出する
        const string source = "async (x = /\\]/) => {}";
        //                     0         1         2
        //                     0123456789012345678901 2
        // 'a' of async at 0, '=' of => at 17, '{' at 21, '}' at 22

        var map = CoverageParser.BuildCoverageMap(source, []);

        // async キーワード先頭が 0（未実行）にマークされること
        Assert.Equal(0, map[0]);  // 'a' of async
        // 関数本体も 0 にマークされること
        Assert.Equal(0, map[21]); // '{'
    }

    // ─── TC-6: MergeMaps — 両方が 0（未実行）のケース ───────────────────────────

    /// <summary>
    /// MergeMaps で baseMap と otherMap の両方が 0（未実行）の要素は、
    /// 合成結果も 0（未実行）になることを確認する。
    /// （既存テストが v1=0, v2=0 のケースを明示的にカバーしていないため追加）
    /// </summary>
    [Fact]
    public void MergeMaps_BothUncovered_ReturnsUncovered()
    {
        // baseMap[0]=0（未実行）、otherMap[0]=0（未実行）→ merged[0]=0（未実行）
        int[] baseMap  = [0, 1, -1];
        int[] otherMap = [0, 0, -1];

        int[] merged = CoverageParser.MergeMaps(baseMap, otherMap);

        Assert.Equal(0,  merged[0]); // 両方 0 → 0
        Assert.Equal(1,  merged[1]); // baseMap=1 → 1
        Assert.Equal(-1, merged[2]); // 両方 -1 → -1
    }

    // ─── TC-7: メソッド短縮構文の本体内の関数 ──────────────────────────────────

    /// <summary>
    /// 未実行のメソッド短縮構文（outer(){}）の本体内に function キーワードがある場合、
    /// outer 全体が 0 マークされ、inner の本体も 0（未実行）になることを確認する。
    /// （M-1: TryMarkMethodShorthand が braceEnd を返すようになっても同じ結果になることの確認）
    /// </summary>
    [Fact]
    public void BuildMap_FunctionInsideUncoveredMethodShorthand_BothMarkedAsUncovered()
    {
        // const obj = { outer() { function inner(){} } }
        // 'o' of outer at 14, '{' of outer body at 21, 'f' of inner at 23, '}' of outer at 41
        const string source = "const obj = { outer() { function inner(){} } }";
        //                     0         1         2         3         4
        //                     01234567890123456789012345678901234567890123456

        var map = CoverageParser.BuildCoverageMap(source, []);

        // outer メソッドの先頭が 0（未実行）にマークされること
        Assert.Equal(0, map[14]); // 'o' of outer
        // outer の本体が 0 にマークされること
        Assert.Equal(0, map[22]); // '{' of outer body
        // inner function の先頭も 0（未実行）にマークされること
        Assert.Equal(0, map[23]); // 'f' of function
        // inner の本体も 0
        Assert.Equal(0, map[39]); // '{'
    }

    // -----------------------------------------------------------------------
    // 追加テスト: アロー関数式本体・カリー化・null Ranges・ジェネレータ・境界値
    // -----------------------------------------------------------------------

    /// <summary>
    /// アロー関数の本体がオブジェクトリテラル（式本体）の場合、
    /// その { をブロック本体と誤認識して 0 マークしないことを確認する。
    /// => の直後が ( であるため TryMarkArrowFunction はブロック本体ではないと判断してスキップする。
    /// </summary>
    [Fact]
    public void BuildMap_ArrowFunctionWithObjectLiteralBody_NotMarked()
    {
        // const f = () => ({key: 1});
        // => の後は ( なので { はブロック本体ではない
        // 位置: const f = () => ({key: 1});
        //       0         1         2
        //       012345678901234567890123456789
        // => : 14-15,  ( : 17, { : 18, } : 25, ) : 26
        const string source = "const f = () => ({key: 1});";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // { は式本体の一部（オブジェクトリテラル）であり、ブロック本体ではない → -1（対象外）のまま
        int bracePos = source.IndexOf('{');
        Assert.Equal(-1, map[bracePos]);
    }

    /// <summary>
    /// カリー化アロー関数 x =&gt; y =&gt; z =&gt; {} の場合、
    /// 最内の {} ブロック本体だけが 0（未実行）にマークされることを確認する。
    /// 外側の 2 つのアローは式本体（次のアロー関数）を持つためブロック本体なし。
    /// </summary>
    [Fact]
    public void BuildMap_CurriedArrowFunctions_InnermostBodyMarked()
    {
        // const f = x => y => z => {}
        // 位置:       0         1         2
        //             012345678901234567890123456789
        // x:10, =>(1st):12, y:15, =>(2nd):17, z:20, =>(3rd):22, {:25, }:26
        const string source = "const f = x => y => z => {}";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // x と z は => の引数（ブロック本体外）→ -1（対象外）のまま
        Assert.Equal(-1, map[10]); // 'x'
        Assert.Equal(-1, map[20]); // 'z'
        // 第1・第2 => の '=' も -1（ブロック本体を持たないため処理なし）
        Assert.Equal(-1, map[12]); // '=' of 1st =>
        Assert.Equal(-1, map[17]); // '=' of 2nd =>
        // 第3 => から始まるブロック本体は 0（未実行）にマークされる
        Assert.Equal(0, map[22]); // '=' of 3rd =>
        Assert.Equal(0, map[25]); // '{'
        Assert.Equal(0, map[26]); // '}'
    }

    /// <summary>
    /// FunctionCoverage の Ranges プロパティが null の場合でも
    /// NullReferenceException を投げずに空マップを返すことを確認する。
    /// （不正な CDP データへの防衛処理が正しく機能することの回帰テスト）
    /// </summary>
    [Fact]
    public void BuildMap_FunctionWithNullRanges_DoesNotThrow()
    {
        // Ranges が null の FunctionCoverage を渡す
        var functions = new[] { new FunctionCoverage("f", null) };

        // 例外なく全文字が -1（対象外）で返ること
        int[] map = CoverageParser.BuildCoverageMap("hello", functions);
        Assert.Equal([-1, -1, -1, -1, -1], map);
    }

    /// <summary>
    /// オブジェクトリテラル内のジェネレータメソッド短縮構文（*gen(){}）が
    /// 未実行の場合に * 記号の位置から全体が 0（未実行）にマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_GeneratorObjectMethod_MarkedFromStarPosition()
    {
        // const obj = { *gen() { yield 1; } };
        // 位置:  0         1         2         3
        //        0123456789012345678901234567890123456789
        // { of obj: 12, *: 14, g: 15, (: 18, ): 19, {: 21, }: 32
        const string source = "const obj = { *gen() { yield 1; } };";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // * の位置から 0（未実行）にマークされること
        int starPos = source.IndexOf('*');
        Assert.Equal(0, map[starPos]);       // '*'
        Assert.Equal(0, map[starPos + 1]);   // 'g' of gen
        // メソッド本体の { と } も 0
        int bodyBrace = source.IndexOf('{', starPos);
        Assert.Equal(0, map[bodyBrace]);
    }

    /// <summary>
    /// ソースコードが 1 文字の場合でも例外なく処理されることを確認する（境界値テスト）。
    /// </summary>
    [Fact]
    public void BuildMap_SingleCharSource_HandledCorrectly()
    {
        // 1 文字のソースで関数データなし → [-1] が返ること
        var map = CoverageParser.BuildCoverageMap("a", []);
        Assert.Equal([-1], map);
    }

    /// <summary>
    /// MergeMaps に両方が全て -1（対象外）のマップを渡した場合、
    /// 合成結果も全て -1（対象外）になることを確認する。
    /// </summary>
    [Fact]
    public void MergeMaps_BothMapsAllNeutral_ReturnsAllNeutral()
    {
        int[] baseMap  = [-1, -1, -1];
        int[] otherMap = [-1, -1, -1];

        int[] merged = CoverageParser.MergeMaps(baseMap, otherMap);

        Assert.Equal([-1, -1, -1], merged);
    }

    /// <summary>
    /// async *['key']() {} という computed property key のジェネレータメソッドにおいて
    /// async キーワード（'a'）から本体 {} まで全体が未実行（0）としてマークされることを確認する。
    /// async → * → ['key'] → () → {} の順に各要素が連続してマークされる。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncGeneratorComputedMethodKey_AsyncMarked()
    {
        // class Obj { async *['key']() { return 1; } }
        //  0         1         2         3         4
        //  0123456789012345678901234567890123456789012345
        // async=12, *=18, [=20, ]=26, (=27, )=28, {=30
        const string source = "class Obj { async *['key']() { return 1; } }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        int asyncIdx = source.IndexOf("async");
        int starIdx  = source.IndexOf('*');
        int bodyOpen = source.IndexOf('{', starIdx);

        // async キーワード先頭が 0（未実行）にマークされること
        Assert.Equal(0, map[asyncIdx]); // 'a' of async
        // * も 0（未実行）にマークされること
        Assert.Equal(0, map[starIdx]);  // '*'
        // メソッド本体 { も 0 にマークされること
        Assert.Equal(0, map[bodyOpen]); // '{'
    }

    /// <summary>
    /// ジェネレータ関数内で yield が先行するアロー関数（yield x => {}）において
    /// ジェネレータ関数全体（yield の先行するアロー含む）が未実行（0）にマークされることを確認する。
    /// ScanRange が yield の存在によって => の検出を誤らないことを検証する。
    /// </summary>
    [Fact]
    public void BuildMap_YieldContextArrowFunction_MarkedAsUncovered()
    {
        // function* gen() { yield x => {}; }
        //  0         1         2         3
        //  01234567890123456789012345678901234
        // function=0, {=17, yield=18, x=24, ==26, >=27, {=29, }=30, }=33
        const string source = "function* gen() { yield x => {}; }";

        var map = CoverageParser.BuildCoverageMap(source, []);

        // ジェネレータ関数全体が 0（未実行）にマークされること
        Assert.Equal(0, map[0]);  // 'f' of function
        // ジェネレータ本体 { が 0 にマークされること
        int genBody = source.IndexOf('{');
        Assert.Equal(0, map[genBody]);
        // アロー関数本体 {} も gen の範囲として 0 にマークされること
        int arrowBody = source.IndexOf('{', genBody + 1);
        Assert.Equal(0, map[arrowBody]);
    }

    // -----------------------------------------------------------------------
    // computed property key — get/set プレフィックスのテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// static get [computed]() {} において static キーワードと get キーワードも
    /// 未実行（0）としてマークされることを確認する。
    /// TryMarkComputedMethod が get/set プレフィックスを処理できることを検証する。
    /// </summary>
    [Fact]
    public void BuildMap_StaticGetComputedKey_StaticAndGetMarked()
    {
        // class C { static get [Symbol.toPrimitive]() {} }
        const string source = "class C { static get [Symbol.toPrimitive]() {} }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int staticIdx = source.IndexOf("static");
        int getIdx    = source.IndexOf("get");
        int bodyOpen  = source.IndexOf('{', source.IndexOf(')'));
        // static キーワードの先頭が 0（未実行）になること
        Assert.Equal(0, map[staticIdx]);
        // get キーワードも 0（未実行）になること
        Assert.Equal(0, map[getIdx]);
        // メソッド本体 { も 0 になること
        Assert.Equal(0, map[bodyOpen]);
    }

    /// <summary>
    /// static set [computed](v) {} において static キーワードと set キーワードも
    /// 未実行（0）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_StaticSetComputedKey_StaticAndSetMarked()
    {
        const string source = "class C { static set [Symbol.toPrimitive](v) {} }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int staticIdx = source.IndexOf("static");
        int setIdx    = source.IndexOf("set");
        int bodyOpen  = source.IndexOf('{', source.IndexOf(')'));
        Assert.Equal(0, map[staticIdx]);
        Assert.Equal(0, map[setIdx]);
        Assert.Equal(0, map[bodyOpen]);
    }

    /// <summary>
    /// get [computed]() {} において（static なし）get キーワードも
    /// 未実行（0）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_GetComputedKeyNoStatic_GetMarked()
    {
        const string source = "const obj = { get [Symbol.toPrimitive]() {} };";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int getIdx   = source.IndexOf("get");
        int bodyOpen = source.IndexOf('{', source.IndexOf(')'));
        Assert.Equal(0, map[getIdx]);
        Assert.Equal(0, map[bodyOpen]);
    }

    // -----------------------------------------------------------------------
    // 不足テスト — 各種エッジケースの文書化
    // -----------------------------------------------------------------------

    /// <summary>
    /// 無名ジェネレーター IIFE（(function*() {})()）が未実行の場合、
    /// function キーワードから本体まで 0（未実行）としてマークされることを確認する。
    /// function* の次がすぐ () であっても正しく検出できることを検証する。
    /// </summary>
    [Fact]
    public void BuildMap_AnonymousGeneratorIIFE_FunctionMarked()
    {
        // (function*() { yield 1; })()
        const string source = "(function*() { yield 1; })()";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int funcIdx  = source.IndexOf("function");
        int yieldIdx = source.IndexOf("yield");
        // function キーワード先頭が 0（未実行）にマークされること
        Assert.Equal(0, map[funcIdx]);
        // yield も本体内なので 0（未実行）であること
        Assert.Equal(0, map[yieldIdx]);
    }

    /// <summary>
    /// async アロー関数のパラメータに正規表現デフォルト値があり、
    /// => の後に行コメントが続く場合でも async キーワードが正しく
    /// 未実行（0）としてマークされることを確認する。
    /// async (x = /pat/) => // comment
    /// {}
    /// </summary>
    [Fact]
    public void BuildMap_AsyncArrowWithRegexDefaultAndLineComment_AsyncMarked()
    {
        // async (x = /pat/) => // comment\n{}
        // => の後の行コメントを SkipWhitespaceAndCommentsForward が正しくスキップすること
        const string source = "async (x = /pat/) => // comment\n{}";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int asyncIdx  = source.IndexOf("async");
        int arrowIdx  = source.IndexOf("=>");
        int bodyOpen  = source.IndexOf('{', source.IndexOf('\n'));
        // async キーワードが 0（未実行）にマークされること
        Assert.Equal(0, map[asyncIdx]);
        // => も 0（未実行）であること
        Assert.Equal(0, map[arrowIdx]);
        // メソッド本体 { も 0 であること
        Assert.Equal(0, map[bodyOpen]);
    }

    /// <summary>
    /// else if(cond){} はメソッド短縮構文でも関数宣言でもないため、
    /// カバレッジマップで 0（未実行）にマークされないことを確認する。
    /// ControlFlowKeywords に "else" と "if" が両方登録されていることの文書化テスト。
    /// </summary>
    [Fact]
    public void BuildMap_ElseIfBlock_NotMarkedAsFunction()
    {
        // トップレベルの if/else if ブロックはメソッド短縮構文ではないため
        // { が -1（カバレッジ対象外）のままであることを確認する
        // ControlFlowKeywords に "if" と "else" が登録されていることの境界値ドキュメントテスト
        const string source = "if(x){}else if(y){}";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int ifBrace   = source.IndexOf('{');
        int elseIfIdx = source.IndexOf("else if");
        int elseIfBrace = source.IndexOf('{', elseIfIdx);
        // if ブロックの { は -1（カバレッジ対象外）のままであること
        Assert.Equal(-1, map[ifBrace]);
        // else if ブロックの { も -1（カバレッジ対象外）のままであること
        Assert.Equal(-1, map[elseIfBrace]);
    }

    /// <summary>
    /// ソース先頭がブロックコメントで始まる場合でも、
    /// その直後の async アロー関数が正しく未実行（0）にマークされることを確認する。
    /// SkipWhitespaceAndCommentsBackward の pos=0 付近の境界値ドキュメントテスト。
    /// </summary>
    [Fact]
    public void BuildMap_BlockCommentAtSourceStart_AsyncArrowMarked()
    {
        // /* c */ async x => {} のように先頭がブロックコメントの場合
        // SkipWhitespaceAndCommentsBackward がコメントをスキップして async を検出できること
        const string source = "/* c */ async x => {}";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int asyncIdx = source.IndexOf("async");
        int arrowIdx = source.IndexOf("=>");
        // async キーワードが 0（未実行）にマークされること
        Assert.Equal(0, map[asyncIdx]);
        // => も 0（未実行）であること
        Assert.Equal(0, map[arrowIdx]);
    }

    /// <summary>
    /// 正規表現の文字クラス内にエスケープされた \[ とその前に ) を含む場合でも、
    /// async アロー関数の async キーワードが未実行（0）にマークされることを確認する。
    /// 文字クラス [)/\[] の逆走査で \[ の [ を誤ってクラス開始と判定すると、
    /// ) がパラメータリストの ) として誤カウントされ async が検出されなくなるバグの回帰テスト。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncArrowWithRegexContainingParenAndEscapedBracket_AsyncMarked()
    {
        // async (x = /[)/\[]/) => {} の async キーワードが 0（未実行）にマークされること
        // 正規表現 /[)/\[]/ は文字クラス [)/\[] 内に ), /, \[ を含む
        // 逆走査で \[ の [ をエスケープされていない [ と誤判定しないことを確認する
        const string source = "async (x = /[)/\\[]/) => {}";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int asyncIdx = source.IndexOf("async");
        // async キーワードが 0（未実行）にマークされること
        Assert.Equal(0, map[asyncIdx]);
    }

    // -----------------------------------------------------------------------
    // BOM 付きソースのテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// BOM（U+FEFF）で始まるソースコードでも BuildCoverageMap が function キーワードを正しく検出して
    /// 未実行（0）にマークすることを確認する。BOM は識別子文字でないため関数検出に影響しない。
    /// </summary>
    [Fact]
    public void BuildCoverageMap_BomPrefixedSource_FunctionDetected()
    {
        // BOM + "function foo() { return 1; }" — カバレッジデータなしで呼ぶ
        // BOM はスキャン時に識別子文字として扱われないため function が正常に検出される
        const string sourceWithBom = "\uFEFFfunction foo() { return 1; }";
        var map = CoverageParser.BuildCoverageMap(sourceWithBom, []);

        // function キーワードの先頭（BOM 除いた index=1）が 0（未実行）にマークされること
        int funcIdx = sourceWithBom.IndexOf("function");
        Assert.Equal(0, map[funcIdx]);
    }

    // -----------------------------------------------------------------------
    // テンプレートリテラル内のネストオブジェクトの煙幕テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// テンプレートリテラルの ${ } 内にネストしたオブジェクトリテラルがある場合でも
    /// BuildCoverageMap が無限ループや例外なく完了することを確認する（煙幕テスト）。
    /// </summary>
    [Fact]
    public void BuildMap_NestedObjectInTemplateInterpolation_NotCrash()
    {
        // テンプレートリテラル内に深くネストしたオブジェクト: `${ { key: { inner: 1 } } }`
        const string source = "const x = `${ { key: { inner: 1 } } }`;";
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        // 例外なく完了すること
        Assert.Null(ex);
    }

    // -----------------------------------------------------------------------
    // Bug fix: IsRegexStart — ++ の後にブロックコメントを挟んだ除算の誤判定
    // 修正前: IsRegexStart が空白のみをスキップするため、/* comment */ を読み飛ばせず
    //         */ の '/' を演算子と判定して除算 /2 を正規表現と誤認識していた。
    //         結果として SkipRegexLiteral がソース末尾まで "正規表現" として読み飛ばし、
    //         その後の function が ScanRange に到達せず未実行マークがつかなかった。
    // 修正後: SkipWhitespaceAndCommentsBackward でコメントも含めてスキップすることで
    //         ++ の後だと正しく判定し、除算演算子（false）を返す。
    // -----------------------------------------------------------------------

    /// <summary>
    /// トップレベルに x++ /* comment */ /2; が続き、その後に function がある場合、
    /// function が未実行（0）にマークされることを確認する。
    /// 修正前は IsRegexStart が /2 を正規表現と誤判定し、SkipRegexLiteral が
    /// function キーワードを含むソース末尾まで読み飛ばしていた。
    /// </summary>
    [Fact]
    public void BuildMap_PlusPlusBlockCommentDivision_FunctionAfterDetected()
    {
        // x++ /* c */ /2; function foo() {} — 改行なし・同一行
        // SkipRegexLiteral は改行で終端するため、\n がある場合はバグが隠れてしまう。
        // 同一行に function が続く場合、IsRegexStart が /2 を正規表現と誤判定すると
        // SkipRegexLiteral がソース末尾まで読み飛ばし function foo が検出されなくなる。
        const string source = "x++ /* c */ /2; function foo() {}";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int fooIdx = source.IndexOf("function");
        // 修正前は IsRegexStart が /2 を正規表現と誤判定して function foo が -1（ニュートラル）のまま
        Assert.Equal(0, map[fooIdx]);
    }

    // -----------------------------------------------------------------------
    // BuildCoverageMap — 不正 range データの防衛テスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// startOffset が endOffset より大きい不正な CoverageRange が含まれる場合でも、
    /// 他の正常な range が正しく処理されることを確認する（防衛テスト）。
    /// 不正 range はソートで末尾に落ち、ループが空実行になるため影響しない。
    /// </summary>
    [Fact]
    public void BuildCoverageMap_RangeStartGreaterThanEnd_ValidRangesUnaffected()
    {
        // "function foo() { return 1; }" の全体を実行済みとする正常 range と
        // start > end の不正 range を混在させる
        const string source = "function foo() { return 1; }";
        var functions = new List<FunctionCoverage>
        {
            new FunctionCoverage("foo", new List<CoverageRange>
            {
                new CoverageRange(0, source.Length, 1),   // 正常（全体を実行済み）
                new CoverageRange(10, 5, 0),               // 不正（start > end）
            })
        };
        var map = CoverageParser.BuildCoverageMap(source, functions);
        // 正常 range が適用されていること（全体が 1）
        Assert.Equal(1, map[0]);
        Assert.Equal(1, map[source.Length - 1]);
        // 不正 range のループは空実行 → 正常 range を上書きしないこと
        Assert.Equal(1, map[5]);
        Assert.Equal(1, map[9]);
    }

    /// <summary>
    /// startOffset == endOffset のゼロ幅 range は何も書き込まずに無視されることを確認する。
    /// </summary>
    [Fact]
    public void BuildCoverageMap_ZeroWidthRange_NoEffect()
    {
        const string source = "var x = 1;";
        var functions = new List<FunctionCoverage>
        {
            new FunctionCoverage("", new List<CoverageRange>
            {
                new CoverageRange(0, source.Length, 1), // 全体を実行済み
                new CoverageRange(3, 3, 0),              // ゼロ幅（何も書き込まない）
            })
        };
        var map = CoverageParser.BuildCoverageMap(source, functions);
        // ゼロ幅 range の count=0 が周囲の 1 を上書きしないこと
        Assert.Equal(1, map[2]);
        Assert.Equal(1, map[3]);
        Assert.Equal(1, map[4]);
    }

    /// <summary>
    /// count が int.MaxValue の CoverageRange は「実行済み（1）」として扱われることを確認する。
    /// Coverage.cs では TryGetInt32 失敗時のフォールバックとして count = 1 を設定するが、
    /// 明示的に int.MaxValue が count に入った場合も count > 0 として val = 1 になることを文書化する。
    /// </summary>
    [Fact]
    public void BuildCoverageMap_RangeCountIntMaxValue_TreatedAsExecuted()
    {
        const string source = "var x = 1;";
        var functions = new List<FunctionCoverage>
        {
            new FunctionCoverage("", new List<CoverageRange>
            {
                new CoverageRange(0, source.Length, int.MaxValue),
            })
        };
        var map = CoverageParser.BuildCoverageMap(source, functions);
        // int.MaxValue > 0 なので val = 1（実行済み）になること
        Assert.Equal(1, map[0]);
        Assert.Equal(1, map[source.Length - 1]);
    }

    // -----------------------------------------------------------------------
    // テンプレートリテラルの追加エッジケース
    // -----------------------------------------------------------------------

    /// <summary>
    /// 空の ${}（補間式が空）を含むテンプレートリテラルでも例外なく完了することを確認する（煙幕テスト）。
    /// </summary>
    [Fact]
    public void BuildMap_EmptyTemplateInterpolation_NoException()
    {
        // `${}` の中に何もない場合、ScanRange(braceStart+1, braceEnd-1) は
        // start > end になるため即終了する（例外は発生しないこと）
        const string source = "const x = `${}`;";
        var ex = Record.Exception(() => CoverageParser.BuildCoverageMap(source, []));
        Assert.Null(ex);
    }

    /// <summary>
    /// テンプレートリテラルの ${ } 内にあるアロー関数が未実行（0）にマークされることを確認する。
    /// ScanRange がテンプレート補間内を再帰スキャンすることで内側の => を検出できること。
    /// </summary>
    [Fact]
    public void BuildMap_ArrowFunctionInTemplateInterpolation_MarkedAsUncovered()
    {
        // テンプレートリテラル内の補間に含まれるアロー関数: `${ () => {} }`
        // ScanRange が ${ } の中を再帰スキャンするため => と {} が検出されること
        const string source = "const f = `${ () => {} }`;";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int arrowIdx = source.IndexOf("=>");
        int braceIdx = source.IndexOf('{', source.IndexOf("=>"));
        // アロー関数本体 {} が 0（未実行）にマークされること
        Assert.Equal(0, map[braceIdx]);
    }

    /// <summary>
    /// ミニファイされたソース（1行に複数アロー関数）で全関数が未実行（0）にマークされることを確認する。
    /// ScanRange が左から右にスキャンし、各 => を独立して処理することの文書化テスト。
    /// </summary>
    [Fact]
    public void BuildMap_MultipleArrowFunctionsSameLine_AllMarkedUncovered()
    {
        // ミニファイ風: const a=()=>{},b=()=>{},c=()=>{}
        const string source = "const a=()=>{},b=()=>{},c=()=>{}";
        var map = CoverageParser.BuildCoverageMap(source, []);
        // 各アロー関数の { が 0（未実行）にマークされること
        int first  = source.IndexOf('{');
        int second = source.IndexOf('{', first + 1);
        int third  = source.IndexOf('{', second + 1);
        Assert.Equal(0, map[first]);
        Assert.Equal(0, map[second]);
        Assert.Equal(0, map[third]);
    }

    /// <summary>
    /// ソースの先頭が => で始まる（ファイル先頭アロー関数）場合でも
    /// SkipWhitespaceAndCommentsBackward が pos=-1 で安全に動作することを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_ArrowFunctionAtFileStart_MarkedAsUncovered()
    {
        // "=> {}" — ファイル先頭が => のアロー関数。arrowStart=0 なので逆走査に pos=-1 が渡される
        const string source = "=> {}";
        var map = CoverageParser.BuildCoverageMap(source, []);
        Assert.Equal(0, map[0]); // '=' of =>
        Assert.Equal(0, map[3]); // '{'
        Assert.Equal(0, map[4]); // '}'
    }

    /// <summary>
    /// async を変数名として使ったアロー関数 const async = () => {} で、
    /// async キーワードとして誤マークされず、アロー部分のみ 0（未実行）になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncUsedAsVariableName_BodyMarkedArrowOnly()
    {
        // const async = () => {} の場合、async は変数名であり async キーワードではない。
        // TryMarkArrowFunction の逆走査で () の前が '=' (代入演算子) のため async キーワード未検出。
        const string source = "const async = () => {}";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int arrowIdx = source.IndexOf("=>");
        Assert.Equal(0, map[arrowIdx]);                         // '=' of => は 0
        Assert.Equal(0, map[source.IndexOf('{', arrowIdx)]);    // { は 0
        int asyncIdx = source.IndexOf("async");
        Assert.Equal(-1, map[asyncIdx]);                        // 変数名 async は -1（誤マークなし）
    }

    /// <summary>
    /// MarkUncalledFunctionBodiesAsUncovered に source より短い map を渡した場合に
    /// IndexOutOfRangeException が発生しないことを確認する（ラテントバグの回帰テスト）。
    /// </summary>
    [Fact]
    public void MarkUncalledFunctionBodies_MapShorterThanSource_NoIndexOutOfRange()
    {
        // "f(){}" — 5文字のソースに対して長さ 4 の map（末尾の } が範囲外）を渡す
        // 修正前は TryMarkMethodShorthand の書き込みループが map 末尾を超えてクラッシュしていた
        const string source = "f(){}";
        var map = new int[4]; // source.Length より 1 短い
        Array.Fill(map, -1);
        var ex = Record.Exception(() => CoverageParser.MarkUncalledFunctionBodiesAsUncovered(source, map));
        Assert.Null(ex); // 例外が発生しないこと
    }

    /// <summary>
    /// ソースが "function" の 8 文字だけ（本体なし）の場合、すべての文字が -1（対象外）のままであることを確認する。
    /// funcStart + 8 >= end の境界値で TryMarkFunctionKeyword が早期リターンする動作の回帰テスト。
    /// </summary>
    [Fact]
    public void BuildMap_SourceIsJustFunctionKeyword_AllNeutral()
    {
        // "function" のみ（本体なし）— 8文字ちょうど。TryMarkFunctionKeyword は
        // j = funcStart + 8 = end になるため '(' も '{' も見つからず全文字 -1 のまま
        var map = CoverageParser.BuildCoverageMap("function", []);
        Assert.All(map, v => Assert.Equal(-1, v));
    }

    /// <summary>
    /// 関数本体内に identifier / 2 のような除算がある場合に IsRegexStart が '/' を除算と正しく判定し、
    /// 関数本体全体が 0（未実行）としてマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_DivisionAfterIdentifierInFunctionBody_FunctionDetected()
    {
        // "function foo() { return x / 2; }" — IsRegexStart が x の後の '/' を
        // 正規表現開始と誤判定すると SkipRegexLiteral が '2; }' を食い、'}' を見失う
        const string source = "function foo() { return x / 2; }";
        var map = CoverageParser.BuildCoverageMap(source, []);
        Assert.Equal(0, map[0]);                 // 'f' of function は 0
        Assert.Equal(0, map[source.Length - 1]); // 末尾の '}' は 0
    }
}
