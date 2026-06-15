// メソッドチェーン構文のテスト。
// Excelの1セルに "GetByRole(AriaRole.Button).Nth(2)" のような
// チェーン全体を書けるようにする。
//
// CS0612: IFrameLocator.Nth は非推奨だが互換のためサポートを継続
#pragma warning disable CS0612
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class ChainTests
{
    private readonly MockFixture f = new();

    [Fact]
    public void Pageからの2段チェーン()
    {
        var buttons = Substitute.For<ILocator>();
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(buttons);
        buttons.Nth(Arg.Any<int>()).Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Page,
            @"GetByRole(AriaRole.Button, new() { Name = ""送信"" }).Nth(2)");

        Assert.Same(f.Inner, result);
        f.Page.Received(1).GetByRole(AriaRole.Button,
            Arg.Is<PageGetByRoleOptions>(o => o.Name == "送信"));
        buttons.Received(1).Nth(2);
    }

    [Fact]
    public void チェーン末尾にプロパティ()
    {
        var list = Substitute.For<ILocator>();
        var filtered = Substitute.For<ILocator>();
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(list);
        list.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);
        filtered.First.Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Page,
            @"Locator(""#list"").Filter(new() { HasText = ""x"" }).First");

        Assert.Same(f.Inner, result);
        f.Page.Received(1).Locator("#list", null);
        list.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.HasText == "x"));
    }

    [Fact]
    public void フレームをまたぐチェーン()
    {
        f.Page.FrameLocator(Arg.Any<string>()).Returns(f.Frame);
        f.Frame.GetByText(Arg.Any<string>(), Arg.Any<FrameLocatorGetByTextOptions>()).Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Page,
            @"FrameLocator(""#f"").GetByText(""x"")");

        Assert.Same(f.Inner, result);
        f.Page.Received(1).FrameLocator("#f");
        f.Frame.Received(1).GetByText("x", null);
    }

    [Fact]
    public void Locator起点のチェーン()
    {
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);
        filtered.Last.Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Locator,
            @"Filter(new() { HasText = ""x"" }).Last");

        Assert.Same(f.Inner, result);
    }

    [Fact]
    public void ResolveFrameでのチェーン()
    {
        var frame2 = Substitute.For<IFrameLocator>();
        f.Page.FrameLocator(Arg.Any<string>()).Returns(f.Frame);
        f.Frame.Nth(Arg.Any<int>()).Returns(frame2);

        var result = LocatorResolver.ResolveFrame(f.Page, @"FrameLocator(""#f"").Nth(1)");

        Assert.Same(frame2, result);
    }

    [Fact]
    public void チェーン結果の型が合わなければArgumentException()
    {
        f.Page.FrameLocator(Arg.Any<string>()).Returns(f.Frame);

        // FrameLocator(...) は IFrameLocator を返すので Resolve (ILocator) ではエラー
        var ex = Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Page, @"FrameLocator(""#f"")"));
        Assert.Contains("IFrameLocator", ex.Message);
    }

    [Fact]
    public void チェーン構文とparameters併用はArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Page, @"GetByText(""x"")", @"""y"""));
    }

    [Fact]
    public void チェーン内のネスト式も解決される()
    {
        var list = Substitute.For<ILocator>();
        var filtered = Substitute.For<ILocator>();
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(list);
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Inner);
        list.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        var result = LocatorResolver.Resolve(f.Page,
            @"Locator(""#list"").Filter(new() { Has = GetByText(""OK"") })");

        Assert.Same(filtered, result);
        list.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == f.Inner));
    }
}
