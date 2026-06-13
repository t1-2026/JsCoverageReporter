// ============================================================
// LocatorResolverTests.cs
//
// C:\work\LocatorResolver_TestCases.cs に列挙された全パターンを
// 実際の (ヘッドレス) Playwright ページに対して流し、
// LocatorResolver の結果が「Playwright 本来の書き方」と
// 完全に一致することを保証する。
//
// 保証方法:
//   ・ILocator は ToString() (内部セレクタ) を本来の書き方と比較する。
//     セレクタ文字列が一致すれば、パース・オーバーロード選択・
//     オプション組み立て・型変換が本来の呼び出しと等価であることを意味する。
//   ・IFrameLocator は ToString() に意味がないため、フレーム内の要素を
//     取得してそのセレクタを比較する (= フレームの等価性を間接的に保証)。
//   ・エラーケースは想定どおりの例外型が投げられることを確認する。
//
// 注意: テストケースファイル自体は page=null! の使用例集であり
//       そのままでは実行できないため、同じパターンを実ページで再実行する。
// ============================================================

using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace JsCoverageReporter.Tests;

public class LocatorResolverTests : IAsyncLifetime
{
    private IPlaywright _pw = null!;
    private IBrowser _browser = null!;
    private IPage _page = null!;
    private ILocator _locator = null!;   // チェーン起点となる ILocator
    private IFrameLocator _frame = null!; // フレーム起点

    public async Task InitializeAsync()
    {
        _pw = await Playwright.CreateAsync();
        _browser = await _pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await _browser.NewContextAsync();
        _page = await context.NewPageAsync();

        // ロケーターは遅延評価なので、要素が実在しなくてもセレクタ比較はできる。
        // 最低限の DOM (iframe を含む) を用意しておく。
        await _page.SetContentAsync(
            "<html><body><div id='root'></div>"
            + "<iframe id='outer' src='about:blank'></iframe>"
            + "<iframe id='my-iframe' src='about:blank'></iframe>"
            + "</body></html>");

        _locator = _page.Locator("#root");
        _frame = _page.FrameLocator("#outer");
    }

    public async Task DisposeAsync()
    {
        if (_browser != null)
        {
            await _browser.DisposeAsync();
        }
        _pw?.Dispose();
    }

    // ------------------------------------------------------------
    // ヘルパー
    // ------------------------------------------------------------

    /// <summary>2つの ILocator のセレクタ (ToString) が一致することを確認する。</summary>
    private static void Same(ILocator expected, ILocator actual)
    {
        Assert.Equal(expected.ToString(), actual.ToString());
    }

    /// <summary>
    /// 2つの IFrameLocator が等価であることを、フレーム内の同じ要素を
    /// 取得してセレクタを比較することで確認する。
    /// </summary>
    private static void SameFrame(IFrameLocator expected, IFrameLocator actual)
    {
        var probe = "div.__probe__";
        Assert.Equal(expected.Locator(probe).ToString(), actual.Locator(probe).ToString());
    }

    // ============================================================
    // 1. GetByRole
    // ============================================================

    [Fact]
    public void GetByRole_AllPatterns()
    {
        Same(_page.GetByRole(AriaRole.Button),
            LocatorResolver.Resolve(_page, "GetByRole", "AriaRole.Button"));

        Same(_page.GetByRole(AriaRole.Button, new() { Name = "送信" }),
            LocatorResolver.Resolve(_page, "GetByRole", @"AriaRole.Button, new() { Name = ""送信"" }"));

        Same(_page.GetByRole(AriaRole.Link, new() { Name = "詳細", Exact = true }),
            LocatorResolver.Resolve(_page, "GetByRole", @"AriaRole.Link, new() { Name = ""詳細"", Exact = true }"));

        Same(_page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("送信|確認") }),
            LocatorResolver.Resolve(_page, "GetByRole", @"AriaRole.Button, new() { NameRegex = new Regex(""送信|確認"") }"));

        // Name = new Regex(...) は NameRegex へ自動振り替え
        Same(_page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("送信|確認") }),
            LocatorResolver.Resolve(_page, "GetByRole", @"AriaRole.Button, new() { Name = new Regex(""送信|確認"") }"));

        Same(_page.GetByRole(AriaRole.Checkbox, new() { Checked = true }),
            LocatorResolver.Resolve(_page, "GetByRole", @"AriaRole.Checkbox, new() { Checked = true }"));

        Same(_page.GetByRole(AriaRole.Checkbox, new() { Checked = false }),
            LocatorResolver.Resolve(_page, "GetByRole", @"AriaRole.Checkbox, new() { Checked = false }"));

        Same(_page.GetByRole(AriaRole.Button, new() { Disabled = true }),
            LocatorResolver.Resolve(_page, "GetByRole", @"AriaRole.Button, new() { Disabled = true }"));

        Same(_page.GetByRole(AriaRole.Button, new() { Expanded = true }),
            LocatorResolver.Resolve(_page, "GetByRole", @"AriaRole.Button, new() { Expanded = true }"));

        Same(_page.GetByRole(AriaRole.Button, new() { Pressed = true }),
            LocatorResolver.Resolve(_page, "GetByRole", @"AriaRole.Button, new() { Pressed = true }"));

        Same(_page.GetByRole(AriaRole.Option, new() { Selected = true }),
            LocatorResolver.Resolve(_page, "GetByRole", @"AriaRole.Option, new() { Selected = true }"));

        Same(_page.GetByRole(AriaRole.Heading, new() { Level = 2 }),
            LocatorResolver.Resolve(_page, "GetByRole", @"AriaRole.Heading, new() { Level = 2 }"));

        Same(_page.GetByRole(AriaRole.Button, new() { IncludeHidden = true }),
            LocatorResolver.Resolve(_page, "GetByRole", @"AriaRole.Button, new() { IncludeHidden = true }"));

        Same(_page.GetByRole(AriaRole.Heading, new() { Name = "タイトル", Level = 1, Exact = true }),
            LocatorResolver.Resolve(_page, "GetByRole", @"AriaRole.Heading, new() { Name = ""タイトル"", Level = 1, Exact = true }"));

        // Enum 名のみ / 大文字小文字混在
        Same(_page.GetByRole(AriaRole.Button),
            LocatorResolver.Resolve(_page, "GetByRole", "Button"));
        Same(_page.GetByRole(AriaRole.Button),
            LocatorResolver.Resolve(_page, "getbyrole", "ariarole.button"));

        // 起点違い
        Same(_locator.GetByRole(AriaRole.Link, new() { Name = "次へ" }),
            LocatorResolver.Resolve(_locator, "GetByRole", @"AriaRole.Link, new() { Name = ""次へ"" }"));
    }

    [Theory]
    [InlineData("Alert")]
    [InlineData("Button")]
    [InlineData("Checkbox")]
    [InlineData("Heading")]
    [InlineData("Link")]
    [InlineData("Textbox")]
    [InlineData("Treeitem")]
    public void GetByRole_EnumValues(string role)
    {
        var aria = Enum.Parse<AriaRole>(role);
        Same(_page.GetByRole(aria),
            LocatorResolver.Resolve(_page, "GetByRole", "AriaRole." + role));
    }

    // ============================================================
    // 2-7. GetByText / Label / Placeholder / AltText / Title / TestId
    // ============================================================

    [Fact]
    public void GetByText_AllPatterns()
    {
        Same(_page.GetByText("ログイン"),
            LocatorResolver.Resolve(_page, "GetByText", @"""ログイン"""));
        Same(_page.GetByText("ログイン", new() { Exact = true }),
            LocatorResolver.Resolve(_page, "GetByText", @"""ログイン"", new() { Exact = true }"));
        Same(_page.GetByText("ログイン", new() { Exact = false }),
            LocatorResolver.Resolve(_page, "GetByText", @"""ログイン"", new() { Exact = false }"));
        Same(_page.GetByText(new Regex("ログ(イン|アウト)")),
            LocatorResolver.Resolve(_page, "GetByText", @"new Regex(""ログ(イン|アウト)"")"));
        Same(_page.GetByText(new Regex("Hello"), new() { Exact = true }),
            LocatorResolver.Resolve(_page, "GetByText", @"new Regex(""Hello""), new() { Exact = true }"));
        Same(_page.GetByText("Hello World"),
            LocatorResolver.Resolve(_page, "GetByText", @"""Hello World"""));
        Same(_page.GetByText("He said \"Hi\""),
            LocatorResolver.Resolve(_page, "GetByText", @"""He said \""Hi\"""""));
        Same(_locator.GetByText("子要素テキスト"),
            LocatorResolver.Resolve(_locator, "GetByText", @"""子要素テキスト"""));
    }

    [Fact]
    public void GetByLabel_AllPatterns()
    {
        Same(_page.GetByLabel("メールアドレス"),
            LocatorResolver.Resolve(_page, "GetByLabel", @"""メールアドレス"""));
        Same(_page.GetByLabel("パスワード", new() { Exact = true }),
            LocatorResolver.Resolve(_page, "GetByLabel", @"""パスワード"", new() { Exact = true }"));
        Same(_page.GetByLabel(new Regex("メール.*")),
            LocatorResolver.Resolve(_page, "GetByLabel", @"new Regex(""メール.*"")"));
        // パラメータ未指定の string 引数は空文字列で埋まる
        Same(_page.GetByLabel(""),
            LocatorResolver.Resolve(_page, "GetByLabel"));
        Same(_page.GetByLabel("", new() { Exact = true }),
            LocatorResolver.Resolve(_page, "GetByLabel", "new() { Exact = true }"));
    }

    [Fact]
    public void GetByPlaceholder_AltText_Title_TestId()
    {
        Same(_page.GetByPlaceholder("検索..."),
            LocatorResolver.Resolve(_page, "GetByPlaceholder", @"""検索..."""));
        Same(_page.GetByPlaceholder(new Regex("検索.*")),
            LocatorResolver.Resolve(_page, "GetByPlaceholder", @"new Regex(""検索.*"")"));

        Same(_page.GetByAltText("ロゴ画像"),
            LocatorResolver.Resolve(_page, "GetByAltText", @"""ロゴ画像"""));
        Same(_page.GetByAltText(new Regex("ロゴ|バナー")),
            LocatorResolver.Resolve(_page, "GetByAltText", @"new Regex(""ロゴ|バナー"")"));

        Same(_page.GetByTitle("閉じる"),
            LocatorResolver.Resolve(_page, "GetByTitle", @"""閉じる"""));
        Same(_page.GetByTitle(new Regex("閉じ.*")),
            LocatorResolver.Resolve(_page, "GetByTitle", @"new Regex(""閉じ.*"")"));

        Same(_page.GetByTestId("submit-btn"),
            LocatorResolver.Resolve(_page, "GetByTestId", @"""submit-btn"""));
        Same(_page.GetByTestId(new Regex("btn-.*")),
            LocatorResolver.Resolve(_page, "GetByTestId", @"new Regex(""btn-.*"")"));
    }

    // ============================================================
    // 8. Locator (Has/HasNot ネスト式含む)
    // ============================================================

    [Fact]
    public void Locator_AllPatterns()
    {
        Same(_page.Locator("div.container"),
            LocatorResolver.Resolve(_page, "Locator", @"""div.container"""));
        Same(_page.Locator("xpath=//button[@type='submit']"),
            LocatorResolver.Resolve(_page, "Locator", @"""xpath=//button[@type='submit']"""));
        Same(_page.Locator("div.card", new() { HasText = "重要" }),
            LocatorResolver.Resolve(_page, "Locator", @"""div.card"", new() { HasText = ""重要"" }"));
        Same(_page.Locator("div.card", new() { HasTextRegex = new Regex("重要|緊急") }),
            LocatorResolver.Resolve(_page, "Locator", @"""div.card"", new() { HasTextRegex = new Regex(""重要|緊急"") }"));
        Same(_page.Locator("li", new() { HasNotText = "完了" }),
            LocatorResolver.Resolve(_page, "Locator", @"""li"", new() { HasNotText = ""完了"" }"));
        Same(_page.Locator("li", new() { HasNotTextRegex = new Regex("完了|削除済") }),
            LocatorResolver.Resolve(_page, "Locator", @"""li"", new() { HasNotTextRegex = new Regex(""完了|削除済"") }"));

        // Has / HasNot にネスト式
        Same(_page.Locator("div.card", new() { Has = _page.GetByText("詳細") }),
            LocatorResolver.Resolve(_page, "Locator", @"""div.card"", new() { Has = GetByText(""詳細"") }"));
        Same(_page.Locator("div.card", new() { HasNot = _page.GetByText("削除済") }),
            LocatorResolver.Resolve(_page, "Locator", @"""div.card"", new() { HasNot = GetByText(""削除済"") }"));
        // Has にクォート文字列 (セレクタ扱い)
        Same(_page.Locator("div.card", new() { Has = _page.Locator("#badge") }),
            LocatorResolver.Resolve(_page, "Locator", @"""div.card"", new() { Has = ""#badge"" }"));

        Same(_page.Locator("tr", new() { HasText = "アクティブ", HasNotText = "期限切れ" }),
            LocatorResolver.Resolve(_page, "Locator", @"""tr"", new() { HasText = ""アクティブ"", HasNotText = ""期限切れ"" }"));

        Same(_locator.Locator("span.badge"),
            LocatorResolver.Resolve(_locator, "Locator", @"""span.badge"""));
    }

    // ============================================================
    // 9. Filter (ネスト式含む)
    // ============================================================

    [Fact]
    public void Filter_AllPatterns()
    {
        Same(_locator.Filter(new() { HasText = "アクティブ" }),
            LocatorResolver.Resolve(_locator, "Filter", @"new() { HasText = ""アクティブ"" }"));
        Same(_locator.Filter(new() { HasTextRegex = new Regex("完了|進行中") }),
            LocatorResolver.Resolve(_locator, "Filter", @"new() { HasTextRegex = new Regex(""完了|進行中"") }"));
        Same(_locator.Filter(new() { HasNotText = "削除済" }),
            LocatorResolver.Resolve(_locator, "Filter", @"new() { HasNotText = ""削除済"" }"));
        Same(_locator.Filter(new() { Visible = true }),
            LocatorResolver.Resolve(_locator, "Filter", @"new() { Visible = true }"));
        Same(_locator.Filter(new() { Visible = false }),
            LocatorResolver.Resolve(_locator, "Filter", @"new() { Visible = false }"));
        Same(_locator.Filter(new() { HasText = "警告", Visible = true }),
            LocatorResolver.Resolve(_locator, "Filter", @"new() { HasText = ""警告"", Visible = true }"));

        // Has / HasNot にネスト式・チェーン
        Same(_locator.Filter(new() { Has = _page.GetByRole(AriaRole.Button) }),
            LocatorResolver.Resolve(_locator, "Filter", @"new() { Has = GetByRole(AriaRole.Button) }"));
        Same(_locator.Filter(new() { Has = _page.GetByRole(AriaRole.Button, new() { Name = "送信" }) }),
            LocatorResolver.Resolve(_locator, "Filter", @"new() { Has = GetByRole(AriaRole.Button, new() { Name = ""送信"" }) }"));
        Same(_locator.Filter(new() { HasNot = _page.Locator("#row").First }),
            LocatorResolver.Resolve(_locator, "Filter", @"new() { HasNot = Locator(""#row"").First }"));
        Same(_locator.Filter(new() { Has = _page.Locator("#sel") }),
            LocatorResolver.Resolve(_locator, "Filter", @"new() { Has = ""#sel"" }"));

        // 空白多め / 大文字小文字混在
        Same(_locator.Filter(new() { HasText = "テスト", Visible = true }),
            LocatorResolver.Resolve(_locator, "Filter", @"new  ()  {  HasText  =  ""テスト""  ,  Visible  =  true  }"));
        Same(_locator.Filter(new() { HasText = "テスト", Visible = true }),
            LocatorResolver.Resolve(_locator, "FILTER", @"NEW() { hastext = ""テスト"", VISIBLE = TRUE }"));
    }

    // ============================================================
    // 10-13. Nth / First / Last / Describe
    // ============================================================

    [Fact]
    public void Nth_First_Last_Describe()
    {
        Same(_locator.Nth(0), LocatorResolver.Resolve(_locator, "Nth", "0"));
        Same(_locator.Nth(1), LocatorResolver.Resolve(_locator, "Nth", "1"));
        Same(_locator.Nth(-1), LocatorResolver.Resolve(_locator, "Nth", "-1"));
        Same(_locator.Nth(-2), LocatorResolver.Resolve(_locator, "Nth", "-2"));

        Same(_locator.First, LocatorResolver.Resolve(_locator, "First"));
        Same(_locator.First, LocatorResolver.Resolve(_locator, "First", null));
        Same(_locator.First, LocatorResolver.Resolve(_locator, "First", ""));
        Same(_locator.First, LocatorResolver.Resolve(_locator, "First", "   "));
        Same(_locator.First, LocatorResolver.Resolve(_locator, "first"));
        Same(_locator.First, LocatorResolver.Resolve(_locator, "FIRST"));

        Same(_locator.Last, LocatorResolver.Resolve(_locator, "Last"));
        Same(_locator.Last, LocatorResolver.Resolve(_locator, "last"));

        Same(_locator.Describe("送信ボタン"),
            LocatorResolver.Resolve(_locator, "Describe", @"""送信ボタン"""));
    }

    // ============================================================
    // 14-15. And / Or (ネスト式・セレクタ文字列)
    // ============================================================

    [Fact]
    public void And_Or_Patterns()
    {
        Same(_locator.And(_page.GetByRole(AriaRole.Button)),
            LocatorResolver.Resolve(_locator, "And", "GetByRole(AriaRole.Button)"));
        Same(_locator.And(_page.GetByText("送信", new() { Exact = true })),
            LocatorResolver.Resolve(_locator, "And", @"GetByText(""送信"", new() { Exact = true })"));
        Same(_locator.And(_page.Locator("[data-active]")),
            LocatorResolver.Resolve(_locator, "And", @"""[data-active]"""));

        Same(_locator.Or(_page.GetByRole(AriaRole.Link)),
            LocatorResolver.Resolve(_locator, "Or", "GetByRole(AriaRole.Link)"));
        Same(_locator.Or(_page.Locator("#alt")),
            LocatorResolver.Resolve(_locator, "Or", @"""#alt"""));
    }

    // ============================================================
    // 16. ResolveFrame (IFrameLocator)
    // ============================================================

    [Fact]
    public void ResolveFrame_Patterns()
    {
        SameFrame(_page.FrameLocator("#my-iframe"),
            LocatorResolver.ResolveFrame(_page, "FrameLocator", @"""#my-iframe"""));
        SameFrame(_page.FrameLocator("iframe.video-player"),
            LocatorResolver.ResolveFrame(_page, "FrameLocator", @"""iframe.video-player"""));
        SameFrame(_page.FrameLocator("#my-iframe"),
            LocatorResolver.ResolveFrame(_page, "framelocator", @"""#my-iframe"""));

        // ILocator 起点
        SameFrame(_locator.FrameLocator("iframe"),
            LocatorResolver.ResolveFrame(_locator, "FrameLocator", @"""iframe"""));

        // IFrameLocator 起点: ネスト / First / Last / Nth
        SameFrame(_frame.FrameLocator("#inner-iframe"),
            LocatorResolver.ResolveFrame(_frame, "FrameLocator", @"""#inner-iframe"""));
        SameFrame(_frame.First, LocatorResolver.ResolveFrame(_frame, "First"));
        SameFrame(_frame.First, LocatorResolver.ResolveFrame(_frame, "First", null));
        SameFrame(_frame.Last, LocatorResolver.ResolveFrame(_frame, "Last"));
        SameFrame(_frame.Nth(0), LocatorResolver.ResolveFrame(_frame, "Nth", "0"));
        SameFrame(_frame.Nth(2), LocatorResolver.ResolveFrame(_frame, "Nth", "2"));
        SameFrame(_frame.Nth(-1), LocatorResolver.ResolveFrame(_frame, "Nth", "-1"));
        SameFrame(_frame.First, LocatorResolver.ResolveFrame(_frame, "first"));
        SameFrame(_frame.Nth(1), LocatorResolver.ResolveFrame(_frame, "nth", "1"));

        // IFrameLocator.Owner は ILocator を返すので Resolve で取得
        Same(_frame.Owner, LocatorResolver.Resolve(_frame, "Owner"));

        // フレーム内要素 (Resolve が起点 IFrameLocator を受け付ける)
        Same(_page.FrameLocator("#editor-iframe").GetByRole(AriaRole.Textbox, new() { Name = "本文" }),
            LocatorResolver.Resolve(
                LocatorResolver.ResolveFrame(_page, "FrameLocator", @"""#editor-iframe"""),
                "GetByRole", @"AriaRole.Textbox, new() { Name = ""本文"" }"));
    }

    // ============================================================
    // 17-19. 空白 / 大文字小文字 / エッジケース
    // ============================================================

    [Fact]
    public void Whitespace_Variations()
    {
        var expected = _page.GetByText("Hello", new() { Exact = true });

        Same(expected, LocatorResolver.Resolve(_page, "GetByText", @"""Hello"",new(){Exact=true}"));
        Same(expected, LocatorResolver.Resolve(_page, "GetByText", @"""Hello""  ,  new()  {  Exact  =  true  }"));
        Same(expected, LocatorResolver.Resolve(_page, "GetByText", @"""Hello"", new ()  { Exact = true }"));
        Same(expected, LocatorResolver.Resolve(_page, "GetByText", "\"Hello\",\tnew()\t{\tExact\t=\ttrue\t}"));
        Same(expected, LocatorResolver.Resolve(_page, "GetByText", "\"Hello\",\nnew() {\n  Exact = true\n}"));

        Same(_page.GetByText("Hello"),
            LocatorResolver.Resolve(_page, "GetByText", @"  ""Hello""  "));
        Same(_page.GetByText(new Regex("pattern")),
            LocatorResolver.Resolve(_page, "GetByText", @"new  Regex  (  ""pattern""  )"));
    }

    [Fact]
    public void CaseSensitivity_Variations()
    {
        Same(_page.GetByText("Hello"), LocatorResolver.Resolve(_page, "getbytext", @"""Hello"""));
        Same(_page.GetByText("Hello"), LocatorResolver.Resolve(_page, "GETBYTEXT", @"""Hello"""));
        Same(_page.GetByText("Hello"), LocatorResolver.Resolve(_page, "GetByTEXT", @"""Hello"""));
        Same(_page.GetByText("Hello", new() { Exact = true }),
            LocatorResolver.Resolve(_page, "GetByText", @"""Hello"", NEW() { Exact = true }"));
        Same(_page.GetByText(new Regex("Hello")),
            LocatorResolver.Resolve(_page, "GetByText", @"new regex(""Hello"")"));
        Same(_page.GetByText("Hello", new() { Exact = true }),
            LocatorResolver.Resolve(_page, "GetByText", @"""Hello"", new() { Exact = TRUE }"));
        Same(_locator.Filter(new() { Visible = false }),
            LocatorResolver.Resolve(_locator, "Filter", @"new() { Visible = FALSE }"));
        Same(_page.GetByRole(AriaRole.Button),
            LocatorResolver.Resolve(_page, "GetByRole", "ariarole.BUTTON"));
        Same(_page.GetByRole(AriaRole.Button, new() { Name = "送信", Exact = true }),
            LocatorResolver.Resolve(_page, "GetByRole", @"AriaRole.Button, new() { name = ""送信"", EXACT = true }"));
    }

    [Fact]
    public void EdgeCases()
    {
        Same(_locator.First, LocatorResolver.Resolve(_locator, "First", null));
        Same(_locator.First, LocatorResolver.Resolve(_locator, "First", ""));
        Same(_locator.First, LocatorResolver.Resolve(_locator, "First", "   "));

        // 空の Options → Options 引数なしと等価
        Same(_page.GetByText("Hello"),
            LocatorResolver.Resolve(_page, "GetByText", @"""Hello"", new() {}"));
        Same(_page.GetByText("Hello"),
            LocatorResolver.Resolve(_page, "GetByText", @"""Hello"", new() {  }"));

        Same(_page.GetByText("Hello, World"),
            LocatorResolver.Resolve(_page, "GetByText", @"""Hello, World"""));
        Same(_page.GetByText("value = {test}"),
            LocatorResolver.Resolve(_page, "GetByText", @"""value = {test}"""));
        Same(_page.GetByText("He said \"Hi\""),
            LocatorResolver.Resolve(_page, "GetByText", @"""He said \""Hi\"""""));

        var longText = new string('A', 1000);
        Same(_page.GetByText(longText),
            LocatorResolver.Resolve(_page, "GetByText", @"""" + longText + @""""));
        Same(_page.GetByText("こんにちは世界"),
            LocatorResolver.Resolve(_page, "GetByText", @"""こんにちは世界"""));
        Same(_page.Locator("div > span.class-name:nth-child(2)"),
            LocatorResolver.Resolve(_page, "Locator", @"""div > span.class-name:nth-child(2)"""));
    }

    // ============================================================
    // 20. チェーン構文
    // ============================================================

    [Fact]
    public void ChainSyntax()
    {
        Same(_page.GetByRole(AriaRole.Button).Filter(new() { HasText = "x" }).First,
            LocatorResolver.Resolve(_page, @"GetByRole(AriaRole.Button).Filter(new() { HasText = ""x"" }).First"));

        Same(_page.Locator("table tbody tr").Filter(new() { HasText = "アクティブ" }).First,
            LocatorResolver.Resolve(_page, @"Locator(""table tbody tr"").Filter(new() { HasText = ""アクティブ"" }).First"));

        SameFrame(_page.FrameLocator("#f").Nth(1),
            LocatorResolver.ResolveFrame(_page, @"FrameLocator(""#f"").Nth(1)"));
    }

    // ============================================================
    // エラーケース (例外で報告されること)
    // ============================================================

    [Fact]
    public void ErrorCases_ThrowExpectedExceptions()
    {
        // 起点が null
        Assert.Throws<ArgumentNullException>(() =>
            LocatorResolver.Resolve((IPage)null!, "GetByText", @"""x"""));

        // 存在しないメソッド名
        Assert.Throws<ArgumentException>(() =>
            LocatorResolver.Resolve(_page, "GetByFoo", @"""test"""));

        // 存在しない Options プロパティ
        Assert.Throws<ArgumentException>(() =>
            LocatorResolver.Resolve(_page, "GetByText", @"""Hello"", new() { NonExistent = true }"));

        // 型不一致 (int に文字)
        Assert.Throws<ArgumentException>(() =>
            LocatorResolver.Resolve(_page, "GetByRole", @"AriaRole.Heading, new() { Level = abc }"));

        // enum 値の typo
        Assert.Throws<ArgumentException>(() =>
            LocatorResolver.Resolve(_page, "GetByRole", "AriaRole.NotARole"));

        // 文字列の閉じ忘れ
        Assert.Throws<FormatException>(() =>
            LocatorResolver.Resolve(_page, "GetByText", @"""Hello"));

        // 初期化子の閉じ忘れ
        Assert.Throws<FormatException>(() =>
            LocatorResolver.Resolve(_page, "GetByText", @"""Hello"", new() { Exact = true"));

        // チェーン構文 + parameters の併用
        Assert.Throws<ArgumentException>(() =>
            LocatorResolver.Resolve(_page, "GetByRole(AriaRole.Button)", "AriaRole.Button"));

        // ResolveFrame のエラーケース
        Assert.Throws<ArgumentException>(() =>
            LocatorResolver.ResolveFrame(_page, "First"));
        Assert.Throws<ArgumentException>(() =>
            LocatorResolver.ResolveFrame(_page, "Nth", "0"));
        Assert.Throws<ArgumentException>(() =>
            LocatorResolver.ResolveFrame(_locator, "First"));
        Assert.Throws<ArgumentException>(() =>
            LocatorResolver.ResolveFrame(_frame, "GetByRole", "AriaRole.Button"));
    }

    // ============================================================
    // TryResolve / TryResolveFrame (非例外版)
    // ============================================================

    [Fact]
    public void TryResolve_ReportsErrorsWithoutThrowing()
    {
        Assert.True(LocatorResolver.TryResolve(_page, "GetByText", @"""x""", out var loc, out var err));
        Assert.NotNull(loc);
        Assert.Null(err);

        Assert.False(LocatorResolver.TryResolve(_page, "GetByFoo", @"""x""", out var loc2, out var err2));
        Assert.Null(loc2);
        Assert.NotNull(err2);

        Assert.True(LocatorResolver.TryResolveFrame(_page, "FrameLocator", @"""#f""", out var f, out var fe));
        Assert.NotNull(f);
        Assert.Null(fe);

        Assert.False(LocatorResolver.TryResolveFrame(_page, "First", null, out var f2, out var fe2));
        Assert.Null(f2);
        Assert.NotNull(fe2);
    }

    // ============================================================
    // EscapeText
    // ============================================================

    [Fact]
    public void EscapeText_RoundTrips()
    {
        var raw = "He said \"Hi\" \\ \n end";
        var escaped = LocatorResolver.EscapeText(raw);
        // EscapeText で囲んだリテラルをそのまま GetByText に渡すと、元テキストと等価になる
        Same(_page.GetByText(raw),
            LocatorResolver.Resolve(_page, "GetByText", escaped));
    }
}
