// ParameterParser 単体のテスト。
// LocatorResolver.cs はテストプロジェクトに直接コンパイルされるため
// internal の ParameterParser をそのまま呼べる。
using System.Globalization;

namespace LocatorResolverTests;

public class ParserTests
{
    // ----- 逐語的文字列リテラル @"..." -----

    [Fact]
    public void 逐語的文字列_基本()
    {
        var (primary, _) = ParameterParser.Parse(@"@""xpath=//div""");
        Assert.Equal("xpath=//div", primary);
    }

    [Fact]
    public void 逐語的文字列_二重クォートエスケープ()
    {
        // C#の逐語的文字列では "" が " 1文字を表す
        var (primary, _) = ParameterParser.Parse(@"@""He said """"hi""""""");
        Assert.Equal(@"He said ""hi""", primary);
    }

    [Fact]
    public void 逐語的文字列_バックスラッシュはそのまま()
    {
        var (primary, _) = ParameterParser.Parse(@"@""a\nb""");
        Assert.Equal(@"a\nb", primary);
    }

    [Fact]
    public void 逐語的文字列_後ろにOptions()
    {
        var (primary, options) = ParameterParser.Parse(@"@""#id"", new() { Exact = true }");
        Assert.Equal("#id", primary);
        Assert.Equal(true, options["Exact"]);
    }

    [Fact]
    public void 逐語的文字列_Options値()
    {
        var (_, options) = ParameterParser.Parse(@"""x"", new() { Name = @""a""""b"" }");
        Assert.Equal(@"a""b", options["Name"]);
    }

    // ----- 数値リテラル -----

    [Fact]
    public void 小数はdoubleとして読む()
    {
        var (primary, _) = ParameterParser.Parse("2.5");
        Assert.Equal(2.5d, primary);
    }

    [Fact]
    public void 負の小数()
    {
        var (primary, _) = ParameterParser.Parse("-0.75");
        Assert.Equal(-0.75d, primary);
    }

    [Fact]
    public void 整数はintのまま()
    {
        var (primary, _) = ParameterParser.Parse("2");
        Assert.Equal(2, primary);
    }

    [Fact]
    public void 小数はカルチャ非依存でパースされる()
    {
        // ドイツ語カルチャでは小数点が ',' だが、入力はC#構文なので '.' 固定
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var (primary, _) = ParameterParser.Parse("2.5");
            Assert.Equal(2.5d, primary);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ----- true/false の語境界 -----

    [Fact]
    public void trueで始まる識別子はboolにしない()
    {
        var (_, options) = ParameterParser.Parse(@"new() { Name = trueblue }");
        Assert.Equal("trueblue", options["Name"]);
    }

    [Fact]
    public void falseで始まる識別子はboolにしない()
    {
        var (_, options) = ParameterParser.Parse(@"new() { Name = falsetto }");
        Assert.Equal("falsetto", options["Name"]);
    }

    [Fact]
    public void 通常のtrueはbool()
    {
        var (_, options) = ParameterParser.Parse(@"new() { Exact = true }");
        Assert.Equal(true, options["Exact"]);
    }

    // ----- 不正入力はエラーにする (沈黙スキップしない) -----

    [Fact]
    public void 閉じていない文字列はFormatException()
    {
        var ex = Assert.Throws<FormatException>(
            () => ParameterParser.Parse(@"""Hello"));
        Assert.Contains("閉じ", ex.Message);
    }

    [Fact]
    public void イコールのないプロパティ設定はFormatException()
    {
        Assert.Throws<FormatException>(
            () => ParameterParser.Parse(@"new() { Name ""x"" }"));
    }

    // ----- 従来の寛容パース (互換性維持) -----

    [Fact]
    public void 未クォートセレクタはそのまま文字列()
    {
        var (primary, _) = ParameterParser.Parse("div.item:nth-child(2)");
        Assert.Equal("div.item:nth-child(2)", primary);
    }

    [Fact]
    public void 未クォートセレクタとOptionsの境界()
    {
        var (primary, options) = ParameterParser.Parse(
            @"xpath=//a[contains(text(),""x"")], new() { HasText = ""y"" }");
        Assert.Equal(@"xpath=//a[contains(text(),""x"")]", primary);
        Assert.Equal("y", options["HasText"]);
    }
}
