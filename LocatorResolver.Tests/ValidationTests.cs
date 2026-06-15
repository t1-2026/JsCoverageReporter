// エラー検証のテスト。
// 「黙って間違ったLocatorになる」のではなく、明確な例外を投げることを保証する。
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace LocatorResolverTests;

public class ValidationTests
{
    private readonly MockFixture f = new();

    [Fact]
    public void 必須のenum引数がないとArgumentException()
    {
        // 修正前: AriaRole の default値 (Alert) で黙って実行されていた
        var ex = Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Page, "GetByRole"));
        Assert.Contains("GetByRole", ex.Message);
    }

    [Fact]
    public void string引数のメソッドはパラメータ空なら空文字列で呼ばれる()
    {
        // GetByLabel等、文字列引数のメソッドはExcelのセルが空でも動くようにする
        f.Page.GetByLabel(Arg.Any<string>(), Arg.Any<PageGetByLabelOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByLabel");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByLabel("", null);
    }

    [Fact]
    public void string引数のメソッドは空白だけのパラメータでも空文字列で呼ばれる()
    {
        f.Page.GetByLabel(Arg.Any<string>(), Arg.Any<PageGetByLabelOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByLabel", "   ");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByLabel("", null);
    }

    [Fact]
    public void string引数のメソッドはOptionsのみの指定でも動く()
    {
        // ラベル未指定で Exact だけ指定するケース
        f.Page.GetByLabel(Arg.Any<string>(), Arg.Any<PageGetByLabelOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByLabel", "new() { Exact = true }");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByLabel("",
            Arg.Is<PageGetByLabelOptions>(o => o.Exact == true));
    }

    [Fact]
    public void 不明なOptionsプロパティ名はArgumentException()
    {
        // 修正前: typo は黙ってスキップされていた
        var ex = Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Page, "GetByRole",
                @"AriaRole.Button, new() { Naame = ""x"" }"));
        Assert.Contains("Naame", ex.Message);
    }

    [Fact]
    public void Optionsプロパティの型変換失敗はArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Page, "GetByRole",
                @"AriaRole.Heading, new() { Level = abc }"));
        Assert.Contains("Level", ex.Message);
    }

    [Fact]
    public void enum値のtypoはArgumentExceptionにメッセージ付き()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Page, "GetByRole", "AriaRole.Buttooon"));
        Assert.Contains("Buttooon", ex.Message);
    }

    [Fact]
    public void Regexオーバーロードがないメソッドは明確なエラー()
    {
        // 修正前: "Sequence contains no matching element" という意味不明な例外
        var ex = Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Locator, "Nth", @"new Regex(""x"")"));
        Assert.Contains("Nth", ex.Message);
        Assert.Contains("Regex", ex.Message);
    }

    [Fact]
    public void プロパティに引数を渡すとArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Locator, "First", "123"));
        Assert.Contains("First", ex.Message);
    }

    [Fact]
    public void Playwright側の例外はTargetInvocationExceptionに包まずそのまま伝える()
    {
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>())
            .Throws(new InvalidOperationException("boom"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => LocatorResolver.Resolve(f.Page, "GetByText", @"""Hello"""));
        Assert.Equal("boom", ex.Message);
    }
}
