// 網羅性テスト。
// IPage / ILocator / IFrameLocator が持つ「Locatorを返す全メンバー」を
// リフレクションで列挙し、LocatorResolver が構造的に扱える形
// (引数型・Optionsプロパティ型がすべて変換可能) であることを検証する。
//
// Playwrightのバージョンアップで新しいメソッドやオプションが増えた場合、
// 未対応の型があればこのテストが落ちて検知できる。
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class CoverageTests
{
    private static readonly Type[] RootInterfaces =
        [typeof(IPage), typeof(ILocator), typeof(IFrameLocator)];

    private static bool ReturnsLocator(Type t) =>
        typeof(ILocator).IsAssignableFrom(t) || typeof(IFrameLocator).IsAssignableFrom(t);

    private static bool IsOptionsType(Type t) =>
        t.IsClass && t != typeof(string) && t != typeof(Regex)
        && t.Name.EndsWith("Options", StringComparison.Ordinal);

    /// <summary>文字列からの変換 (CoerceTo) がサポートしている型か。</summary>
    private static bool IsSupportedValueType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t == typeof(string)
            || t == typeof(int)
            || t == typeof(double)
            || t == typeof(float)
            || t == typeof(decimal)
            || t == typeof(bool)
            || t == typeof(Regex)
            || t.IsEnum
            || typeof(ILocator).IsAssignableFrom(t);
    }

    [Fact]
    public void 全Locator返却メソッドの引数型がサポート範囲内()
    {
        var violations = new List<string>();

        foreach (var iface in RootInterfaces)
        {
            var methods = iface.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => ReturnsLocator(m.ReturnType));

            foreach (var m in methods)
            {
                var required = m.GetParameters()
                    .Where(p => !IsOptionsType(p.ParameterType))
                    .ToArray();

                // primary引数は最大1個 (それ以上は文字列から組み立てられない)
                if (required.Length > 1)
                {
                    violations.Add($"{iface.Name}.{m.Name}: 必須引数が{required.Length}個");
                    continue;
                }

                if (required.Length == 1 && !IsSupportedValueType(required[0].ParameterType))
                {
                    violations.Add(
                        $"{iface.Name}.{m.Name}: 引数型 {required[0].ParameterType.Name} が未サポート");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void 全Options型の全プロパティがサポート範囲内()
    {
        var optionTypes = RootInterfaces
            .SelectMany(i => i.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => ReturnsLocator(m.ReturnType))
            .SelectMany(m => m.GetParameters())
            .Select(p => p.ParameterType)
            .Where(IsOptionsType)
            .Distinct();

        var violations = new List<string>();

        foreach (var ot in optionTypes)
        {
            foreach (var prop in ot.GetProperties().Where(p => p.CanWrite))
            {
                if (!IsSupportedValueType(prop.PropertyType))
                {
                    violations.Add($"{ot.Name}.{prop.Name}: 型 {prop.PropertyType.Name} が未サポート");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    // ----- 残りのGetBy系メソッドの実呼び出し確認 -----

    [Theory]
    [InlineData("GetByLabel")]
    [InlineData("GetByPlaceholder")]
    [InlineData("GetByAltText")]
    [InlineData("GetByTitle")]
    public void GetBy系_文字列とExactオプション(string methodName)
    {
        var f = new MockFixture();
        ConfigureCatchAll(f.Page, f.Locator);

        var result = LocatorResolver.Resolve(f.Page, methodName,
            @"""ラベル"", new() { Exact = true }");

        Assert.Same(f.Locator, result);
    }

    [Theory]
    [InlineData("GetByLabel")]
    [InlineData("GetByText")]
    public void GetBy系_Regexオーバーロード(string methodName)
    {
        var f = new MockFixture();
        ConfigureCatchAll(f.Page, f.Locator);

        var result = LocatorResolver.Resolve(f.Page, methodName, @"new Regex(""パタ.ン"")");

        Assert.Same(f.Locator, result);
    }

    [Fact]
    public void GetByTestId()
    {
        var f = new MockFixture();
        f.Page.GetByTestId(Arg.Any<string>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByTestId", @"""submit-button""");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByTestId("submit-button");
    }

    [Fact]
    public void Describe()
    {
        var f = new MockFixture();
        f.Locator.Describe(Arg.Any<string>()).Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Locator, "Describe", @"""送信ボタン""");

        Assert.Same(f.Inner, result);
        f.Locator.Received(1).Describe("送信ボタン");
    }

    [Fact]
    public void Owner_FrameLocatorからILocatorへ()
    {
        var f = new MockFixture();

        var result = LocatorResolver.Resolve(f.Frame, "Owner");

        Assert.Same(f.Locator, result); // MockFixtureで Frame.Owner = Locator に設定済み
    }

    [Fact]
    public void Last_プロパティ()
    {
        var f = new MockFixture();
        f.Locator.Last.Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Locator, "Last");

        Assert.Same(f.Inner, result);
    }

    [Fact]
    public void Locator_セレクタとHasオプション()
    {
        var f = new MockFixture();
        var inner = Substitute.For<ILocator>();
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(inner);
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "Locator",
            @"""#list"", new() { Has = GetByText(""OK"") }");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).Locator("#list",
            Arg.Is<PageLocatorOptions>(o => o.Has == inner));
    }

    /// <summary>
    /// GetBy系メソッドをまとめて差し替える (Theory用)。
    /// NSubstituteの個別設定の代わりにリフレクションで全GetBy*を構成する。
    /// </summary>
    private static void ConfigureCatchAll(IPage page, ILocator returns)
    {
        page.GetByLabel(Arg.Any<string>(), Arg.Any<PageGetByLabelOptions>()).Returns(returns);
        page.GetByLabel(Arg.Any<Regex>(), Arg.Any<PageGetByLabelOptions>()).Returns(returns);
        page.GetByPlaceholder(Arg.Any<string>(), Arg.Any<PageGetByPlaceholderOptions>()).Returns(returns);
        page.GetByAltText(Arg.Any<string>(), Arg.Any<PageGetByAltTextOptions>()).Returns(returns);
        page.GetByTitle(Arg.Any<string>(), Arg.Any<PageGetByTitleOptions>()).Returns(returns);
        page.GetByText(Arg.Any<Regex>(), Arg.Any<PageGetByTextOptions>()).Returns(returns);
    }
}
