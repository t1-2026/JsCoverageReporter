// 7回目の総点検: Locator(ILocator) オーバーロードへのネスト式対応。
// チェーン形の文字列 ("GetByText(...)" 等) は ILocator 引数を優先し、
// セレクタ形の文字列 ("#sel" 等) は従来どおり string 引数を使う。
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class SeventhPassTests
{
    private readonly MockFixture f = new();

    [Fact]
    public void Locatorメソッドにネスト式を渡すとILocatorオーバーロードが選ばれる()
    {
        // 修正前: string版が常に勝ち、"GetByText(""x"")" が
        // 無効なセレクタ文字列としてそのまま渡っていた
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Inner);
        var scoped = Substitute.For<ILocator>();
        f.Locator.Locator(Arg.Any<ILocator>(), Arg.Any<LocatorLocatorOptions>()).Returns(scoped);

        var result = LocatorResolver.Resolve(f.Locator, "Locator", @"GetByText(""x"")");

        Assert.Same(scoped, result);
        f.Locator.Received(1).Locator(f.Inner, null);
    }

    [Fact]
    public void Locatorメソッドにネスト式とオプションを併用できる()
    {
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Inner);
        var scoped = Substitute.For<ILocator>();
        f.Locator.Locator(Arg.Any<ILocator>(), Arg.Any<LocatorLocatorOptions>()).Returns(scoped);

        var result = LocatorResolver.Resolve(f.Locator, "Locator",
            @"GetByText(""x""), new() { HasText = ""y"" }");

        Assert.Same(scoped, result);
        f.Locator.Received(1).Locator(f.Inner,
            Arg.Is<LocatorLocatorOptions>(o => o.HasText == "y"));
    }

    [Fact]
    public void Locatorメソッドにセレクタを渡すと従来どおりstringオーバーロード()
    {
        var scoped = Substitute.For<ILocator>();
        f.Locator.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions>()).Returns(scoped);

        var result = LocatorResolver.Resolve(f.Locator, "Locator", @"""#sel""");

        Assert.Same(scoped, result);
        f.Locator.Received(1).Locator("#sel", null);
    }

    [Fact]
    public void 括弧を含むテキストはGetByTextでは従来どおり文字列のまま()
    {
        // GetByText には string/Regex 版しかないので、チェーン形に見える
        // テキストでも従来どおり文字列として渡る (回帰防止)
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByText", "Hello (world)");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByText("Hello (world)", null);
    }
}
