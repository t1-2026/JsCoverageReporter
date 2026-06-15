// 2回目の総点検で見つかった課題のテスト。
// 数字始まりの未クォート文字列、不正なnew式の検出、
// フレーム起点のネスト式、ContentFrame、引数ガード等。
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class SecondPassTests
{
    private readonly MockFixture f = new();

    // ===== 数字で始まる未クォート文字列 =====

    [Fact]
    public void 数字で始まる未クォートテキストは文字列として扱う()
    {
        // 修正前: "2件" は数値2を読んだ後の "件" が解釈できずエラーになっていた
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByText", "2件");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByText("2件", null);
    }

    [Fact]
    public void 純粋な数値は引き続きintとして扱う()
    {
        f.Locator.Nth(Arg.Any<int>()).Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Locator, "Nth", "2");

        Assert.Same(f.Inner, result);
        f.Locator.Received(1).Nth(2);
    }

    [Fact]
    public void Options値の数字始まりテキストも文字列として扱う()
    {
        var (_, options) = ParameterParser.Parse("new() { Name = 2件 }");
        Assert.Equal("2件", options["Name"]);
    }

    [Fact]
    public void 数値の後にOptionsが続くのは数値のまま()
    {
        var (primary, options) = ParameterParser.Parse("2, new() { Exact = true }");
        Assert.Equal(2, primary);
        Assert.Equal(true, options["Exact"]);
    }

    // ===== 不正な new 式の検出漏れ =====

    [Fact]
    public void 括弧も波括弧もないnew式はFormatException()
    {
        // 修正前: "new b" が型名と解釈され、黙って空オプションになっていた
        Assert.Throws<FormatException>(
            () => ParameterParser.Parse(@"""x"", new b"));
    }

    [Fact]
    public void newキーワード単独はFormatException()
    {
        Assert.Throws<FormatException>(
            () => ParameterParser.Parse("new"));
    }

    [Fact]
    public void new括弧なし波括弧ありは許容()
    {
        // C#では new PageGetByRoleOptions { ... } のように () を省略できる
        var (_, options) = ParameterParser.Parse(
            @"new PageGetByRoleOptions { Name = ""x"" }");
        Assert.Equal("x", options["Name"]);
    }

    // ===== 引数ガード =====

    [Fact]
    public void メソッド名が空だとArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Page, "  "));
        Assert.Contains("メソッド名", ex.Message);
    }

    // ===== スマートクォートの逐語的文字列 =====

    [Fact]
    public void スマートクォートの逐語的文字列も読める()
    {
        var (primary, _) = ParameterParser.Parse("@“a”");
        Assert.Equal("a", primary);
    }

    // ===== ネスト式の追加保証 =====

    [Fact]
    public void フレーム起点でもHasのネスト式が解決できる()
    {
        // ルートページは frame.Owner.Page 経由で辿る
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Inner);
        var found = Substitute.For<ILocator>();
        f.Frame.Locator(Arg.Any<string>(), Arg.Any<FrameLocatorLocatorOptions>()).Returns(found);

        var result = LocatorResolver.Resolve(f.Frame, "Locator",
            @"""#x"", new() { Has = GetByText(""OK"") }");

        Assert.Same(found, result);
        f.Frame.Received(1).Locator("#x",
            Arg.Is<FrameLocatorLocatorOptions>(o => o.Has == f.Inner));
    }

    [Fact]
    public void ネスト式の引数にRegexを書ける()
    {
        f.Page.GetByText(Arg.Any<Regex>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Inner);
        var combined = Substitute.For<ILocator>();
        f.Locator.And(Arg.Any<ILocator>()).Returns(combined);

        var result = LocatorResolver.Resolve(f.Locator, "And",
            @"GetByText(new Regex(""送信|確認""))");

        Assert.Same(combined, result);
        f.Page.Received(1).GetByText(
            Arg.Is<Regex>(r => r.ToString() == "送信|確認"), null);
        f.Locator.Received(1).And(f.Inner);
    }

    // ===== ContentFrame (FrameLocatorの新推奨API) =====

    [Fact]
    public void ContentFrameプロパティでIFrameLocatorを取得できる()
    {
        f.Locator.ContentFrame.Returns(f.Frame);

        var result = LocatorResolver.ResolveFrame(f.Locator, "ContentFrame");

        Assert.Same(f.Frame, result);
    }

    [Fact]
    public void ContentFrameを含むチェーン()
    {
        var iframeLoc = Substitute.For<ILocator>();
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(iframeLoc);
        iframeLoc.ContentFrame.Returns(f.Frame);
        f.Frame.GetByText(Arg.Any<string>(), Arg.Any<FrameLocatorGetByTextOptions>()).Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Page,
            @"Locator(""iframe.app"").ContentFrame.GetByText(""x"")");

        Assert.Same(f.Inner, result);
        f.Frame.Received(1).GetByText("x", null);
    }

    // ===== Regexプロパティへの文字列指定 =====

    [Fact]
    public void Regex型プロパティに文字列を渡すとRegexに変換される()
    {
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(f.Locator);

        LocatorResolver.Resolve(f.Page, "GetByRole",
            @"AriaRole.Button, new() { NameRegex = ""送信|確認"" }");

        f.Page.Received(1).GetByRole(AriaRole.Button,
            Arg.Is<PageGetByRoleOptions>(o =>
                o.NameRegex != null && o.NameRegex.ToString() == "送信|確認"));
    }
}
