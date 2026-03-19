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
}
