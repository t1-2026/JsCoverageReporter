// 6回目の総点検: 先頭カンマのオプションのみ指定、enum値の空白、
// エラー品質の保証など。
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class SixthPassTests
{
    private readonly MockFixture f = new();

    // ===== 先頭カンマ (プライマリ省略) =====

    [Fact]
    public void 先頭カンマでオプションのみを指定できる()
    {
        // 修正前: 空文字列がプライマリ扱いになり Filter で
        // 「オーバーロードが見つかりません」になっていた
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Locator, "Filter",
            @", new() { HasText = ""x"" }");

        Assert.Same(f.Inner, result);
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.HasText == "x"));
    }

    [Fact]
    public void クォートされた空文字列はプライマリとして残る()
    {
        // "" は「空文字列を渡す」という明示なので null にしない
        f.Page.GetByLabel(Arg.Any<string>(), Arg.Any<PageGetByLabelOptions>()).Returns(f.Locator);

        LocatorResolver.Resolve(f.Page, "GetByLabel", @"""""");

        f.Page.Received(1).GetByLabel("", null);
    }

    // ===== enum値の空白ゆれ =====

    [Fact]
    public void enum値のドット周りの空白を許容する()
    {
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByRole", "AriaRole . Button");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByRole(AriaRole.Button, null);
    }

    [Fact]
    public void プレフィックスなしのenum値も使える()
    {
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByRole", "Button");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByRole(AriaRole.Button, null);
    }

    // ===== エラー品質の保証 =====

    [Fact]
    public void プロパティ間のカンマ漏れは明確なFormatException()
    {
        var ex = Assert.Throws<FormatException>(
            () => ParameterParser.Parse(@"new() { Name = ""a"" Exact = true }"));
        Assert.Contains("Name", ex.Message);
    }

    [Fact]
    public void HasにRegexを渡すと明確なエラー()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Locator, "Filter",
                @"new() { Has = new Regex(""x"") }"));
        Assert.Contains("Has", ex.Message);
        Assert.Contains("ILocator", ex.Message);
    }

    // ===== フレーム往復チェーン =====

    [Fact]
    public void フレームに入ってOwnerで戻るチェーン()
    {
        f.Page.FrameLocator(Arg.Any<string>()).Returns(f.Frame);
        // MockFixture: Frame.Owner = Locator
        f.Locator.Nth(Arg.Any<int>()).Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Page, @"FrameLocator(""#f"").Owner.Nth(0)");

        Assert.Same(f.Inner, result);
        f.Locator.Received(1).Nth(0);
    }
}
