// 9回目: ネスト式 (Has/HasNot/And/Or) の先頭に書かれた
// "page." / "frame." 等のルート参照プレフィックスを救済的に受け付ける対応のテスト。
// 構文上は冗長だが、page.GetByText(...) のように明示しても動くようにする。
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class NinthPassTests
{
    private readonly MockFixture f = new();

    [Fact]
    public void FilterのHasにpage付きネスト式を書ける()
    {
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        var result = LocatorResolver.Resolve(f.Locator, "Filter",
            @"new() { Has = page.GetByText(""OK"") }");

        Assert.Same(filtered, result);
        f.Page.Received(1).GetByText("OK", null);
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == f.Inner));
    }

    [Fact]
    public void Andにpage付きネスト式を書ける()
    {
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(f.Inner);
        var combined = Substitute.For<ILocator>();
        f.Locator.And(Arg.Any<ILocator>()).Returns(combined);

        var result = LocatorResolver.Resolve(f.Locator, "And", "page.GetByRole(AriaRole.Button)");

        Assert.Same(combined, result);
        f.Locator.Received(1).And(f.Inner);
    }

    [Fact]
    public void Hasにframe付きチェーン式を書いてもpage起点で解決される()
    {
        // frame 指定は構文上正しくないが、剥がして page 起点で解決する (救済)
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        LocatorResolver.Resolve(f.Locator, "Filter", @"new() { Has = frame.Locator(""#x"") }");

        f.Page.Received(1).Locator("#x", null);
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == f.Inner));
    }

    [Fact]
    public void page付きでもOptions入りネスト式を書ける()
    {
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        LocatorResolver.Resolve(f.Locator, "Filter",
            @"new() { Has = page.GetByRole(AriaRole.Button, new() { Name = ""送信"" }) }");

        f.Page.Received(1).GetByRole(AriaRole.Button,
            Arg.Is<PageGetByRoleOptions>(o => o.Name == "送信"));
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == f.Inner));
    }

    [Fact]
    public void pageで始まるクォートセレクタは剥がさずそのまま解決する()
    {
        // "page.foo" は type.class 風の CSS セレクタなので、プレフィックスとして
        // 剥がさず page.Locator("page.foo") として扱う (チェーン式ではないため)
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        LocatorResolver.Resolve(f.Locator, "Filter", @"new() { Has = ""page.foo"" }");

        f.Page.Received(1).Locator("page.foo", null);
    }

    [Fact]
    public void factory名と同じクラスを含むセレクタは分解されない()
    {
        // "div.locator" は <div class="locator"> のセレクタ。factory 名 "locator" を
        // 含むが () が無いのでチェーンではない → 剥がさずそのまま page.Locator に渡す。
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        LocatorResolver.Resolve(f.Locator, "Filter", @"new() { Has = ""div.locator"" }");

        f.Page.Received(1).Locator("div.locator", null);
    }

    // ===== Filter 以外の Locator 指定オプションでも効くことの確認 =====

    [Fact]
    public void FilterのHasNotにもpage付きネスト式を書ける()
    {
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        LocatorResolver.Resolve(f.Locator, "Filter", @"new() { HasNot = page.GetByText(""NG"") }");

        f.Page.Received(1).GetByText("NG", null);
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.HasNot == f.Inner));
    }

    [Fact]
    public void LocatorメソッドのHasオプションにもpage付きネスト式を書ける()
    {
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Inner);
        var rows = Substitute.For<ILocator>();
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(rows);

        var result = LocatorResolver.Resolve(f.Page, "Locator",
            @"""#row"", new() { Has = page.GetByText(""x"") }");

        Assert.Same(rows, result);
        f.Page.Received(1).GetByText("x", null);
        f.Page.Received(1).Locator("#row", Arg.Is<PageLocatorOptions>(o => o.Has == f.Inner));
    }

    // ===== ContentFrame をチェーン中の中継セグメントとして使う =====

    [Fact]
    public void ContentFrameを経由したネスト式が解決できる()
    {
        var iframe = Substitute.For<ILocator>();
        iframe.Page.Returns(f.Page);
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(iframe);
        iframe.ContentFrame.Returns(f.Frame);
        f.Frame.GetByText(Arg.Any<string>(), Arg.Any<FrameLocatorGetByTextOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        var result = LocatorResolver.Resolve(f.Locator, "Filter",
            @"new() { Has = Locator(""#f"").ContentFrame.GetByText(""x"") }");

        Assert.Same(filtered, result);
        f.Frame.Received(1).GetByText("x", null);
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == f.Inner));
    }

    [Fact]
    public void page付きでもContentFrame経由のネスト式が解決できる()
    {
        var iframe = Substitute.For<ILocator>();
        iframe.Page.Returns(f.Page);
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(iframe);
        iframe.ContentFrame.Returns(f.Frame);
        f.Frame.GetByText(Arg.Any<string>(), Arg.Any<FrameLocatorGetByTextOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        LocatorResolver.Resolve(f.Locator, "Filter",
            @"new() { Has = page.Locator(""#f"").ContentFrame.GetByText(""x"") }");

        f.Frame.Received(1).GetByText("x", null);
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == f.Inner));
    }

    // ===== ファクトリ手前まで読み飛ばす方式の追加挙動 =====

    [Fact]
    public void 任意の別名プレフィックスでもファクトリ以降が解決される()
    {
        // page/frame 固定ではなく、最初の Locator ファクトリ手前まで読み飛ばすため
        // "myPage." のような任意の別名でも動く
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        LocatorResolver.Resolve(f.Locator, "Filter", @"new() { Has = myPage.GetByText(""OK"") }");

        f.Page.Received(1).GetByText("OK", null);
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == f.Inner));
    }

    [Fact]
    public void レシーバ必須メンバーを起点にすると明示エラー()
    {
        // ContentFrame は「あるLocatorの」プロパティで、単独の起点にはできない。
        // 黙って捨てず ArgumentException にする。
        var ex = Assert.Throws<ArgumentException>(() =>
            LocatorResolver.Resolve(f.Locator, "Filter",
                @"new() { Has = ContentFrame.GetByText(""x"") }"));

        Assert.Contains("ContentFrame", ex.Message);
    }

    [Fact]
    public void Filterを起点にしたネスト式も明示エラー()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            LocatorResolver.Resolve(f.Locator, "And",
                @"Filter(new() { HasText = ""x"" }).GetByText(""y"")"));

        Assert.Contains("Filter", ex.Message);
    }

    [Fact]
    public void 実在の非Locatorナビゲーションを起点にすると明示エラー()
    {
        // page.MainFrame.GetByText(...) は MainFrame(IFrame)を黙って捨てると
        // 別スコープになるため、剥がさず明示エラーにする
        var ex = Assert.Throws<ArgumentException>(() =>
            LocatorResolver.Resolve(f.Locator, "Filter",
                @"new() { Has = page.MainFrame.GetByText(""x"") }"));

        Assert.Contains("MainFrame", ex.Message);
    }

    [Fact]
    public void locator別名プレフィックスはメソッド名と衝突しても剥がせる()
    {
        // "locator" は Locator メソッド名と衝突するが、ルートを指す別名なので剥がす
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        LocatorResolver.Resolve(f.Locator, "Filter", @"new() { Has = locator.GetByText(""OK"") }");

        f.Page.Received(1).GetByText("OK", null);
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == f.Inner));
    }

    // ===== ネスト式 (Or/Has の中身) はチェーン位置でなく「ページ」起点で解決される =====

    [Fact]
    public void チェーン途中のOrの引数もチェーン位置でなくpage起点で解決される()
    {
        var button = Substitute.For<ILocator>();
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(button);
        var y = Substitute.For<ILocator>();
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(y);
        var combined = Substitute.For<ILocator>();
        button.Or(Arg.Any<ILocator>()).Returns(combined);

        var result = LocatorResolver.Resolve(f.Page,
            @"GetByRole(AriaRole.Button).Or(GetByText(""y""))");

        Assert.Same(combined, result);
        // GetByText("y") は button からではなく page から解決される
        f.Page.Received(1).GetByText("y", null);
        button.Received(1).Or(y);
        button.DidNotReceive().GetByText(Arg.Any<string>(), Arg.Any<LocatorGetByTextOptions>());
    }

    [Fact]
    public void フレーム内のxとyをOrするにはFrameLocatorで都度スコープし直す()
    {
        // Or のオペランドは page 起点なので、フレームに絞るなら operand 側でも
        // FrameLocator(...) を書き直す必要がある。これは正しく動く。
        var frame = Substitute.For<IFrameLocator>();
        f.Page.FrameLocator(Arg.Any<string>()).Returns(frame);
        var x = Substitute.For<ILocator>();
        var y = Substitute.For<ILocator>();
        frame.GetByText("x", null).Returns(x);
        frame.GetByText("y", null).Returns(y);
        var combined = Substitute.For<ILocator>();
        x.Or(Arg.Any<ILocator>()).Returns(combined);

        var result = LocatorResolver.Resolve(f.Page,
            @"FrameLocator(""#f"").GetByText(""x"").Or(FrameLocator(""#f"").GetByText(""y""))");

        Assert.Same(combined, result);
        frame.Received(1).GetByText("x", null);
        frame.Received(1).GetByText("y", null);
        x.Received(1).Or(y);
    }

    // ===== 別名プレフィックスの境界 =====

    [Theory]
    [InlineData("p")]            // 任意の変数別名 (実在メンバーでない)
    [InlineData("el")]
    [InlineData("root")]
    [InlineData("myFrame")]
    [InlineData("frameLocator")] // 実在メンバー名と衝突するがセーフ別名
    public void 実在メンバーでない別名やセーフ別名は剥がせる(string alias)
    {
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Inner);
        var filtered = Substitute.For<ILocator>();
        f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(filtered);

        LocatorResolver.Resolve(f.Locator, "Filter",
            $@"new() {{ Has = {alias}.GetByText(""OK"") }}");

        f.Page.Received(1).GetByText("OK", null);
        f.Locator.Received(1).Filter(Arg.Is<LocatorFilterOptions>(o => o.Has == f.Inner));
    }

    [Theory]
    [InlineData("owner")]   // IFrameLocator.Owner
    [InlineData("first")]   // ILocator.First
    [InlineData("frames")]  // IPage.Frames
    public void 別名が実在メンバーと衝突すると明示エラー(string alias)
    {
        // ルート別名のつもりでも実在ナビゲーションと区別できないため、黙って
        // 捨てず例外にする (page/frame/frameLocator/locator だけは安全に剥がす)
        var ex = Assert.Throws<ArgumentException>(() =>
            LocatorResolver.Resolve(f.Locator, "Filter",
                $@"new() {{ Has = {alias}.GetByText(""x"") }}"));

        Assert.Contains(alias, ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
