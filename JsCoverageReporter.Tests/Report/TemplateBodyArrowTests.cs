using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

/// <summary>
/// テンプレートリテラルを式本体に持つアロー関数（() => `...`）の未実行マーク検証。
/// 修正前: TryMarkArrowFunction が '{' ブロック本体のみを対象としていたため、
/// テンプレートリテラル本体のアロー関数は未実行マークされなかった
/// （BuildMap_ArrowFunctionWithTemplateContainingRegexInterpolation_MarkedAsUncovered が
/// この実装漏れにより失敗していた）。
/// </summary>
public class TemplateBodyArrowTests
{
    /// <summary>
    /// 単純なテンプレートリテラル本体のアロー関数が、
    /// 矢印からテンプレート末尾まで未実行（0）にマークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_ArrowWithPlainTemplateBody_MarkedAsUncovered()
    {
        const string source = "const f = () => `hello`";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int arrowPos = source.IndexOf("=>");
        int backtickPos = source.IndexOf('`');
        int lastChar = source.Length - 1; // 閉じ ` の位置
        Assert.Equal(0, map[arrowPos]);
        Assert.Equal(0, map[backtickPos]);
        Assert.Equal(0, map[lastChar]);
    }

    /// <summary>
    /// async アロー関数のテンプレートリテラル本体で、
    /// async キーワードも含めて未実行マークされることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_AsyncArrowWithTemplateBody_AsyncMarked()
    {
        const string source = "const f = async () => `hello`";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int asyncPos = source.IndexOf("async");
        int arrowPos = source.IndexOf("=>");
        Assert.Equal(0, map[asyncPos]);
        Assert.Equal(0, map[arrowPos]);
    }

    /// <summary>
    /// 実行済み（カバレッジデータあり）のテンプレート本体アロー関数は
    /// 上書きされず実行済み（1）のままであることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_CoveredArrowWithTemplateBody_StaysCovered()
    {
        const string source = "const f = () => `hello`";
        var functions = new[]
        {
            new FunctionCoverage("f", [new CoverageRange(0, source.Length, 1)]),
        };
        var map = CoverageParser.BuildCoverageMap(source, functions);
        int arrowPos = source.IndexOf("=>");
        Assert.Equal(1, map[arrowPos]);
    }

    /// <summary>
    /// テンプレート本体の補間に別のアロー関数がネストしている場合、
    /// 外側のアロー関数のマークで内側も含めて未実行になることを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_NestedArrowInsideTemplateBody_BothMarked()
    {
        const string source = "const f = () => `${() => `inner`}`";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int outerArrow = source.IndexOf("=>");
        int innerArrow = source.IndexOf("=>", outerArrow + 2);
        Assert.Equal(0, map[outerArrow]);
        Assert.Equal(0, map[innerArrow]);
    }

    /// <summary>
    /// 閉じられていないテンプレート本体（構文エラーソース）でも
    /// 例外なくソース末尾までマークして完了することを確認する。
    /// </summary>
    [Fact]
    public void BuildMap_UnterminatedTemplateBody_NoException()
    {
        const string source = "const f = () => `oops";
        var map = CoverageParser.BuildCoverageMap(source, []);
        int arrowPos = source.IndexOf("=>");
        Assert.Equal(0, map[arrowPos]);
    }
}
