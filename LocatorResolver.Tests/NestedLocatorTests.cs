// ILocator型の値 (Has / HasNot / And / Or) のテスト。
// 文字列内にネストしたLocator式を書けるようにする。
// ネスト式はルートの IPage を起点に解決される。
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class NestedLocatorTests
{
    private readonly MockFixture f = new();

    [Fact]
    public void FilterのHasにネスト式を書ける()
    {
        // 修正前: ILocator型プロパティは黙ってスキップされていた (⚠️)
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        var result = LocatorResolver.Resolve(f.Locator, "Filter",
            @"new() { Has = GetByText(""OK"") }");

        Assert.Same(filtered, result);
        f.Page.Received(1).GetByText("OK", null);
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == f.Inner));
    }

    [Fact]
    public void HasNotにチェーン式を書ける()
    {
        var rows = Substitute.For<ILocator>();
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(rows);
        rows.First.Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        var result = LocatorResolver.Resolve(f.Locator, "Filter",
            @"new() { HasNot = Locator(""#row"").First }");

        Assert.Same(filtered, result);
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.HasNot == f.Inner));
    }

    [Fact]
    public void Hasにクォート文字列を書くとセレクタとして解決される()
    {
        // 省略記法: Has = "#sel" は page.Locator("#sel") と同じ
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        LocatorResolver.Resolve(f.Locator, "Filter", @"new() { Has = ""#sel"" }");

        f.Page.Received(1).Locator("#sel", null);
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == f.Inner));
    }

    [Fact]
    public void Andにネスト式を書ける()
    {
        // 修正前: And/Or は非対応 (❌)
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(f.Inner);
        var combined = Substitute.For<ILocator>();
        f.Locator.And(Arg.Any<ILocator>()).Returns(combined);

        var result = LocatorResolver.Resolve(f.Locator, "And", "GetByRole(AriaRole.Button)");

        Assert.Same(combined, result);
        f.Locator.Received(1).And(f.Inner);
    }

    [Fact]
    public void Orにクォートセレクタを書ける()
    {
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(f.Inner);
        var combined = Substitute.For<ILocator>();
        f.Locator.Or(Arg.Any<ILocator>()).Returns(combined);

        var result = LocatorResolver.Resolve(f.Locator, "Or", @"""#alt""");

        Assert.Same(combined, result);
        f.Page.Received(1).Locator("#alt", null);
        f.Locator.Received(1).Or(f.Inner);
    }

    [Fact]
    public void ネスト式にOptionsも書ける()
    {
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        LocatorResolver.Resolve(f.Locator, "Filter",
            @"new() { Has = GetByRole(AriaRole.Button, new() { Name = ""送信"" }) }");

        f.Page.Received(1).GetByRole(AriaRole.Button,
            Arg.Is<PageGetByRoleOptions>(o => o.Name == "送信"));
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == f.Inner));
    }
}
