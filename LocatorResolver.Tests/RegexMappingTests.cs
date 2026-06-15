// Regex値の自動マッピングのテスト。
// .NET版Playwrightでは Name=Regex は NameRegex プロパティに分かれているため、
// string型プロパティにRegexが来たら "<名前>Regex" プロパティへ振り替える。
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class RegexMappingTests
{
    private readonly MockFixture f = new();

    [Fact]
    public void GetByRoleのNameにRegexを渡すとNameRegexに設定される()
    {
        // 修正前: Convert.ChangeType 失敗 → bare catch で黙って捨てられていた
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByRole",
            @"AriaRole.Button, new() { Name = new Regex(""送信|確認"") }");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByRole(AriaRole.Button,
            Arg.Is<PageGetByRoleOptions>(o =>
                o.Name == null && o.NameRegex != null && o.NameRegex.ToString() == "送信|確認"));
    }

    [Fact]
    public void FilterのHasTextにRegexを渡すとHasTextRegexに設定される()
    {
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Locator, "Filter",
            @"new() { HasText = new Regex(""active\d+"") }");

        Assert.Same(f.Inner, result);
        f.Locator.Received(1).Filter(
            Arg.Is<LocatorFilterOptions>(o =>
                o.HasText == null && o.HasTextRegex != null && o.HasTextRegex.ToString() == @"active\d+"));
    }

    [Fact]
    public void NameRegexを直接指定しても動く()
    {
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(f.Locator);

        LocatorResolver.Resolve(f.Page, "GetByRole",
            @"AriaRole.Button, new() { NameRegex = new Regex(""送信"") }");

        f.Page.Received(1).GetByRole(AriaRole.Button,
            Arg.Is<PageGetByRoleOptions>(o => o.NameRegex != null && o.NameRegex.ToString() == "送信"));
    }
}
