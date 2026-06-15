// 3回目の総点検で見つかった課題のテスト。
// アポストロフィの誤認、複数語の未クォート値、シングルクォート文字列、
// 全角イコール、空セレクタ検出、typo候補提示など。
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class ThirdPassTests
{
    private readonly MockFixture f = new();

    // ===== アポストロフィを含むテキスト (深さ0の ' は文字列扱いしない) =====

    [Fact]
    public void アポストロフィを含むテキストとオプションが正しく分離される()
    {
        // 修正前: ' がシングルクォート文字列の開始と誤認され、
        // ", new" 境界を見逃して全体がテキストになっていた (無言の誤動作)
        var (primary, options) = ParameterParser.Parse(
            @"It's a test, new() { Exact = true }");

        Assert.Equal("It's a test", primary);
        Assert.Equal(true, options["Exact"]);
    }

    [Fact]
    public void アポストロフィが複数あるテキストでも分離される()
    {
        var (primary, options) = ParameterParser.Parse(
            "don't stop, new() { Exact = true }");

        Assert.Equal("don't stop", primary);
        Assert.Equal(true, options["Exact"]);
    }

    [Fact]
    public void 角括弧内のシングルクォートは引き続き保護される()
    {
        // セレクタ内の 'a, new b' は文字列なので境界分割してはいけない
        var (primary, options) = ParameterParser.Parse(
            @"[title='a, new b'], new() { HasText = ""x"" }");

        Assert.Equal("[title='a, new b']", primary);
        Assert.Equal("x", options["HasText"]);
    }

    // ===== シングルクォート文字列 =====

    [Fact]
    public void シングルクォートの文字列リテラルを読める()
    {
        var (primary, _) = ParameterParser.Parse("'Hello'");
        Assert.Equal("Hello", primary);
    }

    [Fact]
    public void Options値のシングルクォート文字列()
    {
        var (_, options) = ParameterParser.Parse("new() { Name = 'こんにちは' }");
        Assert.Equal("こんにちは", options["Name"]);
    }

    // ===== 複数語の未クォート値 =====

    [Fact]
    public void 空白を含む未クォートのOptions値を読める()
    {
        // Excelでクォートを省略して日本語テキストを書くケース
        var (_, options) = ParameterParser.Parse("new() { Name = 送信 ボタン }");
        Assert.Equal("送信 ボタン", options["Name"]);
    }

    [Fact]
    public void 括弧付きの日本語テキスト値を読める()
    {
        var (_, options) = ParameterParser.Parse(
            @"new() { Name = 送信 (注), Exact = true }");
        Assert.Equal("送信 (注)", options["Name"]);
        Assert.Equal(true, options["Exact"]);
    }

    [Fact]
    public void 括弧付きのASCIIテキストはstring型プロパティならテキストになる()
    {
        // 修正前: "Submit (2)" がネストLocator式と誤認されてエラーになっていた
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(f.Locator);

        LocatorResolver.Resolve(f.Page, "GetByRole",
            "AriaRole.Button, new() { Name = Submit (2) }");

        f.Page.Received(1).GetByRole(AriaRole.Button,
            Arg.Is<PageGetByRoleOptions>(o => o.Name == "Submit (2)"));
    }

    // ===== 全角イコール =====

    [Fact]
    public void 全角イコールも受け付ける()
    {
        var (_, options) = ParameterParser.Parse("new() { Exact ＝ true }");
        Assert.Equal(true, options["Exact"]);
    }

    // ===== 空のILocator値の検出 =====

    [Fact]
    public void Hasに空文字列を渡すと定義エラー()
    {
        // 修正前: page.Locator("") が実行時まで素通りしていた
        var ex = Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Locator, "Filter", @"new() { Has = """" }"));
        Assert.Contains("Has", ex.Message);
    }

    // ===== typo候補の提示 =====

    [Fact]
    public void メソッド名typoに正しい候補を提示する()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Page, "GetByTet", @"""x"""));
        Assert.Contains("GetByTet", ex.Message);
        Assert.Contains("GetByText", ex.Message); // もしかして: GetByText
    }

    [Fact]
    public void かけ離れた名前には候補を出さない()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Page, "ZZZZZZ", @"""x"""));
        Assert.DoesNotContain("もしかして", ex.Message);
    }

    // ===== Regexプライマリとオプションの併用 =====

    [Fact]
    public void RegexプライマリとOptionsを併用できる()
    {
        f.Page.GetByText(Arg.Any<Regex>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByText",
            @"new Regex(""送信""), new() { Exact = true }");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByText(
            Arg.Is<Regex>(r => r.ToString() == "送信"),
            Arg.Is<PageGetByTextOptions>(o => o.Exact == true));
    }
}
