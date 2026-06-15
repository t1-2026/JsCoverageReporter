// 4回目の総点検で見つかった課題のテスト。
// 未クォートの記号始まり値、シングルクォートRegexパターン、
// オプション名のtypo候補、二重ネストHas等。
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class FourthPassTests
{
    private readonly MockFixture f = new();

    // ===== 未クォートの記号始まり値 =====

    [Fact]
    public void Hasに未クォートのセレクタを書ける()
    {
        // 修正前: '#' が識別子として読めず「値が読み取れません」になっていた
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        var result = LocatorResolver.Resolve(f.Locator, "Filter", "new() { Has = #sel }");

        Assert.Same(filtered, result);
        f.Page.Received(1).Locator("#sel", null);
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == f.Inner));
    }

    [Fact]
    public void ハイフン始まりの未クォート値を読める()
    {
        var (_, options) = ParameterParser.Parse("new() { Name = -x- }");
        Assert.Equal("-x-", options["Name"]);
    }

    [Fact]
    public void 値が本当に空ならFormatException()
    {
        // "Name = ," のような書き間違いは引き続きエラー
        Assert.Throws<FormatException>(
            () => ParameterParser.Parse("new() { Name = , Exact = true }"));
    }

    // ===== シングルクォートまわりの残課題 =====

    [Fact]
    public void シングルクォートのプライマリとオプション併用()
    {
        var (primary, options) = ParameterParser.Parse("'Hello', new() { Exact = true }");
        Assert.Equal("Hello", primary);
        Assert.Equal(true, options["Exact"]);
    }

    [Fact]
    public void スマート単引用符も文字列として読める()
    {
        var (primary, _) = ParameterParser.Parse("‘Hello’");
        Assert.Equal("Hello", primary);
    }

    [Fact]
    public void シングルクォート内のエスケープ()
    {
        var (primary, _) = ParameterParser.Parse(@"'It\'s'");
        Assert.Equal("It's", primary);
    }

    [Fact]
    public void Regexパターンをシングルクォートで書ける()
    {
        var (primary, _) = ParameterParser.Parse("new Regex('送信|確認')");

        var r = Assert.IsType<Regex>(primary);
        Assert.Equal("送信|確認", r.ToString());
    }

    [Fact]
    public void Regexのパターンがないと明確なFormatException()
    {
        var ex = Assert.Throws<FormatException>(
            () => ParameterParser.Parse("new Regex()"));
        Assert.Contains("パターン", ex.Message);
    }

    // ===== オプション名のtypo候補 =====

    [Fact]
    public void オプション名typoに候補を提示する()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Page, "GetByRole",
                @"AriaRole.Button, new() { Exsact = true }"));
        Assert.Contains("Exsact", ex.Message);
        Assert.Contains("もしかして", ex.Message);
        Assert.Contains("Exact", ex.Message);
    }

    // ===== 二重ネストの Has =====

    [Fact]
    public void Hasの中のFilterの中のHasも解決できる()
    {
        var locA = Substitute.For<ILocator>();
        var locB = Substitute.For<ILocator>();
        var innerFiltered = Substitute.For<ILocator>();
        f.Page.Locator("#a", Arg.Any<PageLocatorOptions>()).Returns(locA);
        f.Page.Locator("#b", Arg.Any<PageLocatorOptions>()).Returns(locB);
        locA.Filter(Arg.Any<LocatorFilterOptions>()).Returns(innerFiltered);
        var outerFiltered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(outerFiltered);

        var result = LocatorResolver.Resolve(f.Locator, "Filter",
            @"new() { Has = Locator(""#a"").Filter(new() { Has = Locator(""#b"") }) }");

        Assert.Same(outerFiltered, result);
        locA.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == locB));
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == innerFiltered));
    }

    // ===== 不可視文字の追加分 =====

    [Fact]
    public void ワードジョイナーとソフトハイフンも除去される()
    {
        var (primary, _) = ParameterParser.Parse("⁠\"x\"­");
        Assert.Equal("x", primary);
    }
}
