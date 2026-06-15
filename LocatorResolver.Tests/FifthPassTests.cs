// 5回目の総点検: プロセスを殺す系・黙って飲み込む系の残りを潰す。
// 未ペアクォート(インチ記号)、ネスト深度ガード、サロゲートペア等。
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class FifthPassTests
{
    private readonly MockFixture f = new();

    // ===== 未ペアのクォート (インチ記号等) =====

    [Fact]
    public void 未ペアのダブルクォートを含むテキストとオプションが分離される()
    {
        // 修正前: " が文字列開始と誤認され、閉じがないため末尾まで読み飛ばし、
        // options ごと全体がテキストになっていた (無言の誤動作)
        var (primary, options) = ParameterParser.Parse(
            @"5"" tall, new() { Exact = true }");

        Assert.Equal(@"5"" tall", primary);
        Assert.Equal(true, options["Exact"]);
    }

    [Fact]
    public void ペアになったクォートは引き続き保護される()
    {
        // クォート内の ", new y" は境界分割してはいけない
        var (primary, options) = ParameterParser.Parse(
            @"a ""x, new y"" b, new() { Exact = true }");

        Assert.Equal(@"a ""x, new y"" b", primary);
        Assert.Equal(true, options["Exact"]);
    }

    [Fact]
    public void 角括弧内の未ペアシングルクォートでも境界を見失わない()
    {
        var (primary, options) = ParameterParser.Parse(
            "[data-x='broken], new() { Exact = true }");

        Assert.Equal("[data-x='broken]", primary);
        Assert.Equal(true, options["Exact"]);
    }

    // ===== ネスト深度ガード (StackOverflow防止) =====

    [Fact]
    public void 深すぎるネスト式は明示的なエラーになる()
    {
        // StackOverflowException は catch 不能でプロセスごと落ちるため、
        // 上限を超えたら ArgumentException で止める
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(f.Inner);
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(f.Inner);

        var expr = @"Locator(""#leaf"")";
        for (var i = 0; i < 50; i++)
        {
            expr = $@"Locator(""#x"", new() {{ Has = {expr} }})";
        }

        var ex = Assert.Throws<ArgumentException>(
            () => LocatorResolver.Resolve(f.Locator, "Filter", $"new() {{ Has = {expr} }}"));
        Assert.Contains("深すぎ", ex.Message);
    }

    [Fact]
    public void 常識的な深さのネストは引き続き動く()
    {
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        // 5段のネスト
        var expr = @"Locator(""#leaf"")";
        for (var i = 0; i < 5; i++)
        {
            expr = $@"Locator(""#x"", new() {{ Has = {expr} }})";
        }

        var result = LocatorResolver.Resolve(f.Locator, "Filter", $"new() {{ Has = {expr} }}");

        Assert.Same(filtered, result);
    }

    // ===== サロゲートペア (絵文字) =====

    [Fact]
    public void 絵文字を含むテキストが壊れない()
    {
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByText", @"""🎉 完了 ✅""");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByText("🎉 完了 ✅", null);
    }
}
