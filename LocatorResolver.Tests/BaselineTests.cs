// 既存機能のベースラインテスト。
// リファクタリング後も従来どおり動くことを保証する。
//
// CS0612: IFrameLocator.Nth は Playwright 1.60 で非推奨だが、
// LocatorResolver は互換のためサポートし続けるのでテストも維持する
#pragma warning disable CS0612
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class BaselineTests
{
    private readonly MockFixture f = new();

    [Fact]
    public void GetByText_文字列リテラル()
    {
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByText", @"""Hello""");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByText("Hello", null);
    }

    [Fact]
    public void GetByRole_Enum引数とOptions()
    {
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByRole",
            @"AriaRole.Button, new() { Name = ""送信"", Exact = true }");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByRole(AriaRole.Button,
            Arg.Is<PageGetByRoleOptions>(o => o.Name == "送信" && o.Exact == true));
    }

    [Fact]
    public void GetByText_Regex引数()
    {
        f.Page.GetByText(Arg.Any<Regex>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByText",
            @"new Regex(""送信|確認"", RegexOptions.IgnoreCase)");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByText(
            Arg.Is<Regex>(r => r.ToString() == "送信|確認" && r.Options == RegexOptions.IgnoreCase),
            null);
    }

    [Fact]
    public void Filter_Optionsのみ()
    {
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Locator, "Filter",
            @"new() { HasText = ""active"", Visible = true }");

        Assert.Same(f.Inner, result);
        f.Locator.Received(1).Filter(
            Arg.Is<LocatorFilterOptions>(o => o.HasText == "active" && o.Visible == true));
    }

    [Fact]
    public void Nth_int引数()
    {
        f.Locator.Nth(Arg.Any<int>()).Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Locator, "Nth", "2");

        Assert.Same(f.Inner, result);
        f.Locator.Received(1).Nth(2);
    }

    [Fact]
    public void First_プロパティ()
    {
        f.Locator.First.Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Locator, "First");

        Assert.Same(f.Inner, result);
    }

    [Fact]
    public void Locator_未クォートセレクタ()
    {
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "Locator", "#main .item");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).Locator("#main .item", null);
    }

    [Fact]
    public void FrameLocator_セレクタ指定()
    {
        f.Page.FrameLocator(Arg.Any<string>()).Returns(f.Frame);

        var result = LocatorResolver.ResolveFrame(f.Page, "FrameLocator", @"""#my-iframe""");

        Assert.Same(f.Frame, result);
        f.Page.Received(1).FrameLocator("#my-iframe");
    }

    [Fact]
    public void ResolveFrame_Nth()
    {
        var frame2 = Substitute.For<IFrameLocator>();
        f.Frame.Nth(Arg.Any<int>()).Returns(frame2);

        var result = LocatorResolver.ResolveFrame(f.Frame, "Nth", "1");

        Assert.Same(frame2, result);
        f.Frame.Received(1).Nth(1);
    }

    [Fact]
    public void メソッド名大文字小文字無視()
    {
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "getbytext", @"""Hello""");

        Assert.Same(f.Locator, result);
    }

    [Fact]
    public void 存在しないメソッド名はArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Page, "NoSuchMethod", @"""x"""));
        Assert.Contains("NoSuchMethod", ex.Message);
    }
}
