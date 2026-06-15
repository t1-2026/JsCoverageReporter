// 意地悪テスト。
// Excelデータ駆動で実際に起きる「汚い入力」と、パーサーの境界条件を突く。
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class AdversarialTests
{
    private readonly MockFixture f = new();

    // ===== 不正な new 式は黙って無視しない =====

    [Fact]
    public void 第2引数のnew_RegexはFormatException()
    {
        // 修正前: 初期化子読み取りが '{' を探して全部読み飛ばし、黙って無視していた
        var ex = Assert.Throws<FormatException>(
            () => ParameterParser.Parse(@"""x"", new Regex(""y"")"));
        Assert.Contains("Regex", ex.Message);
    }

    [Fact]
    public void 第2引数の不明なnew式はFormatException()
    {
        Assert.Throws<FormatException>(
            () => ParameterParser.Parse(@"""x"", new Foo(123)"));
    }

    [Fact]
    public void newの後の括弧内に引数があるとFormatException()
    {
        Assert.Throws<FormatException>(
            () => ParameterParser.Parse(@"""x"", new(42) { Exact = true }"));
    }

    // ===== Regex まわり =====

    [Fact]
    public void Regexパターンに逐語的文字列を書ける()
    {
        var (primary, _) = ParameterParser.Parse(@"new Regex(@""\d+"")");

        var r = Assert.IsType<Regex>(primary);
        Assert.Equal(@"\d+", r.ToString());
    }

    [Fact]
    public void 完全修飾のRegexも書ける()
    {
        var (primary, _) = ParameterParser.Parse(
            @"new System.Text.RegularExpressions.Regex(""abc"")");

        var r = Assert.IsType<Regex>(primary);
        Assert.Equal("abc", r.ToString());
    }

    [Fact]
    public void Regexに似た別の型名はRegex扱いしない()
    {
        // 修正前: "Regex" の前方一致判定だったので "Regexx" も Regex 扱いされ破綻していた
        Assert.Throws<FormatException>(
            () => ParameterParser.Parse(@"new Regexx(""y"")"));
    }

    [Fact]
    public void RegexOptionsのtypoはFormatException()
    {
        // 修正前: Enum.TryParse 失敗が黙って無視され RegexOptions.None になっていた
        var ex = Assert.Throws<FormatException>(
            () => ParameterParser.Parse(@"new Regex(""x"", RegexOptions.IgnoreCases)"));
        Assert.Contains("IgnoreCases", ex.Message);
    }

    [Fact]
    public void Regexの閉じ括弧がないとFormatException()
    {
        Assert.Throws<FormatException>(
            () => ParameterParser.Parse(@"new Regex(""x"""));
    }

    // ===== Excel由来の汚い文字 =====

    [Fact]
    public void スマートクォートを通常のクォートとして扱う()
    {
        // Excel/Wordのオートコレクトで " が “ ” に化けるケース
        var (primary, _) = ParameterParser.Parse("“Hello”");
        Assert.Equal("Hello", primary);
    }

    [Fact]
    public void 全角ダブルクォートも扱える()
    {
        var (primary, _) = ParameterParser.Parse("＂Hello＂");
        Assert.Equal("Hello", primary);
    }

    [Fact]
    public void スマートクォートのOptions値も扱える()
    {
        var (_, options) = ParameterParser.Parse("new() { Name = “送信” }");
        Assert.Equal("送信", options["Name"]);
    }

    [Fact]
    public void スマートクォートでもLocatorが解決できる()
    {
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByText", "“Hello”");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByText("Hello", null);
    }

    [Fact]
    public void ゼロ幅スペースとBOMは除去される()
    {
        var (primary, _) = ParameterParser.Parse("\uFEFF\"x\"\u200B");
        Assert.Equal("x", primary);
    }

    [Fact]
    public void メソッド名にゼロ幅スペースが混入しても動く()
    {
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "\u200BGetByText\uFEFF", @"""Hello""");

        Assert.Same(f.Locator, result);
    }

    [Fact]
    public void 全角スペースは空白として扱われる()
    {
        var (_, options) = ParameterParser.Parse("new()　{　Exact　=　true　}");
        Assert.Equal(true, options["Exact"]);
    }

    // ===== null リテラル =====

    [Fact]
    public void Optionsのnull値はプロパティをnullのままにする()
    {
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByRole",
            @"AriaRole.Button, new() { Name = null, Exact = true }");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByRole(AriaRole.Button,
            Arg.Is<PageGetByRoleOptions>(o => o != null && o.Name == null && o.Exact == true));
    }

    [Fact]
    public void primaryのnullリテラルは引数なし扱い()
    {
        f.Page.GetByLabel(Arg.Any<string>(), Arg.Any<PageGetByLabelOptions>()).Returns(f.Locator);

        var result = LocatorResolver.Resolve(f.Page, "GetByLabel", "null");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByLabel("", null);
    }

    [Fact]
    public void クォートされたnullは文字列のまま()
    {
        var (primary, _) = ParameterParser.Parse(@"""null""");
        Assert.Equal("null", primary);
    }

    // ===== 数値の境界 =====

    [Fact]
    public void intオーバーフローはFormatException()
    {
        Assert.Throws<FormatException>(
            () => ParameterParser.Parse("99999999999999999999"));
    }

    [Fact]
    public void 小数点が複数ある値は未クォート文字列として扱う()
    {
        // "2.5.3" のようなバージョン番号風のテキストを GetByText 等に渡せるように
        var (primary, _) = ParameterParser.Parse("2.5.3");
        Assert.Equal("2.5.3", primary);
    }

    // ===== 寛容に受けるべき書式ゆれ =====

    [Fact]
    public void 初期化子の末尾カンマは許容()
    {
        var (_, options) = ParameterParser.Parse(@"new() { Name = ""a"", }");
        Assert.Equal("a", options["Name"]);
    }

    [Fact]
    public void トップレベルの末尾カンマは許容()
    {
        var (primary, options) = ParameterParser.Parse(@"""x"" ,");
        Assert.Equal("x", primary);
        Assert.Empty(options);
    }

    // ===== チェーンの意地悪 =====

    [Fact]
    public void プロパティ始まりのチェーン()
    {
        var first = Substitute.For<ILocator>();
        f.Locator.First.Returns(first);
        first.Nth(Arg.Any<int>()).Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Locator, "First.Nth(2)");

        Assert.Same(f.Inner, result);
        first.Received(1).Nth(2);
    }

    [Fact]
    public void 空白だらけのチェーン()
    {
        var buttons = Substitute.For<ILocator>();
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(buttons);
        buttons.First.Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Page,
            "GetByRole ( AriaRole.Button ) . First");

        Assert.Same(f.Inner, result);
    }

    [Fact]
    public void 小文字だけのチェーン()
    {
        var buttons = Substitute.For<ILocator>();
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(buttons);
        buttons.First.Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Page, "getbyrole(AriaRole.Button).first");

        Assert.Same(f.Inner, result);
    }

    [Fact]
    public void チェーン内の空括弧引数()
    {
        // GetByLabel() は空文字列ラベル、First() はプロパティとして許容
        f.Page.GetByLabel(Arg.Any<string>(), Arg.Any<PageGetByLabelOptions>()).Returns(f.Locator);
        f.Locator.First.Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Page, "GetByLabel().First()");

        Assert.Same(f.Inner, result);
        f.Page.Received(1).GetByLabel("", null);
    }

    [Fact]
    public void 閉じていない括弧のチェーンはFormatException()
    {
        Assert.Throws<FormatException>(
            () => LocatorResolver.Resolve(f.Page, @"GetByText(""x"""));
    }

    [Fact]
    public void 深いネスト_Has内のチェーンに初期化子()
    {
        var rows = Substitute.For<ILocator>();
        var rowsFiltered = Substitute.For<ILocator>();
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(rows);
        rows.Filter(Arg.Any<LocatorFilterOptions>()).Returns(rowsFiltered);
        rowsFiltered.First.Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        var result = LocatorResolver.Resolve(f.Locator, "Filter",
            @"new() { Has = Locator(""#a"").Filter(new() { HasText = ""x"" }).First }");

        Assert.Same(filtered, result);
        rows.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.HasText == "x"));
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == f.Inner));
    }
}
