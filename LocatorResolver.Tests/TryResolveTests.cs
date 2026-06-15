// TryResolve / TryResolveFrame のテスト。
// Excel全行をテスト実行前に一括検証する用途のため、
// 定義エラー (構文ミス・メソッド名typo等) を例外でなく bool + エラーメッセージで返す。
//
// CS0612: IFrameLocator.Nth は非推奨だが互換のためサポートを継続
#pragma warning disable CS0612
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class TryResolveTests
{
    private readonly MockFixture f = new();

    [Fact]
    public void 成功時はtrueとLocatorを返す()
    {
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Locator);

        var ok = LocatorResolver.TryResolve(f.Page, "GetByText", @"""Hello""",
            out var locator, out var error);

        Assert.True(ok);
        Assert.Same(f.Locator, locator);
        Assert.Null(error);
    }

    [Fact]
    public void メソッド名typoはfalseとエラーメッセージ()
    {
        var ok = LocatorResolver.TryResolve(f.Page, "GetByTextt", @"""Hello""",
            out var locator, out var error);

        Assert.False(ok);
        Assert.Null(locator);
        Assert.Contains("GetByTextt", error);
    }

    [Fact]
    public void 構文エラーはfalseとエラーメッセージ()
    {
        var ok = LocatorResolver.TryResolve(f.Page, "GetByText", @"""Hello",
            out var locator, out var error);

        Assert.False(ok);
        Assert.Null(locator);
        Assert.Contains("閉じ", error);
    }

    [Fact]
    public void Locator起点のTryResolve()
    {
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(f.Inner);

        var ok = LocatorResolver.TryResolve(f.Locator, "Filter",
            @"new() { HasText = ""x"" }", out var locator, out var error);

        Assert.True(ok);
        Assert.Same(f.Inner, locator);
        Assert.Null(error);
    }

    [Fact]
    public void FrameLocator起点のTryResolve()
    {
        f.Frame.GetByText(Arg.Any<string>(), Arg.Any<FrameLocatorGetByTextOptions>()).Returns(f.Inner);

        var ok = LocatorResolver.TryResolve(f.Frame, "GetByText", @"""x""",
            out var locator, out var error);

        Assert.True(ok);
        Assert.Same(f.Inner, locator);
        Assert.Null(error);
    }

    [Fact]
    public void TryResolveFrame成功()
    {
        f.Page.FrameLocator(Arg.Any<string>()).Returns(f.Frame);

        var ok = LocatorResolver.TryResolveFrame(f.Page, "FrameLocator", @"""#f""",
            out var frame, out var error);

        Assert.True(ok);
        Assert.Same(f.Frame, frame);
        Assert.Null(error);
    }

    [Fact]
    public void TryResolveFrame失敗()
    {
        var ok = LocatorResolver.TryResolveFrame(f.Page, "NoSuchFrame", @"""#f""",
            out var frame, out var error);

        Assert.False(ok);
        Assert.Null(frame);
        Assert.Contains("NoSuchFrame", error);
    }

    [Fact]
    public void TryResolveFrame_ILocator起点とIFrameLocator起点()
    {
        var frame2 = Substitute.For<IFrameLocator>();
        f.Locator.FrameLocator(Arg.Any<string>()).Returns(f.Frame);
        f.Frame.Nth(Arg.Any<int>()).Returns(frame2);

        Assert.True(LocatorResolver.TryResolveFrame(f.Locator, "FrameLocator", @"""#f""",
            out var a, out _));
        Assert.Same(f.Frame, a);

        Assert.True(LocatorResolver.TryResolveFrame(f.Frame, "Nth", "1", out var b, out _));
        Assert.Same(frame2, b);
    }
}
