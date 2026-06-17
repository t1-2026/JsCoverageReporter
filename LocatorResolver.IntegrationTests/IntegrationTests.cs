// 実ブラウザ (headless Chromium) での統合テスト。
//
// 単体テスト (LocatorResolver.Tests) はNSubstituteのモック経由のため、
// 実際の Microsoft.Playwright 実装クラスに対する
//   - インターフェース MethodInfo のリフレクション呼び出し
//   - Options オブジェクトの組み立てと実DOMでのマッチング
//   - NameRegex 振り替え / ネストHas / チェーン / フレーム
// はここで初めて検証される。
//
// 事前準備: playwright.ps1 install chromium (初回のみブラウザDL)
using Microsoft.Playwright;

namespace LocatorResolverIntegrationTests;

/// <summary>
/// クラス内の全テストで1つのブラウザ/ページを共有する
/// (各テストは読み取り専用クエリなので干渉しない)。
/// </summary>
public class PageFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public IPage Page { get; private set; } = null!;

    private const string Html = """
        <!DOCTYPE html>
        <html><body>
          <h1>テストページ</h1>
          <h2>サブ見出し</h2>
          <button>送信</button>
          <button>キャンセル</button>
          <a href='#'>詳細</a>
          <label>ユーザー名 <input type='text' placeholder='入力してください'></label>
          <input type='checkbox' checked>
          <ul id='list' data-testid='main-list'>
            <li class='item'>Product 1 <span class='badge'>OK</span></li>
            <li class='item active'>Product 2</li>
            <li class='item'>Product 3 <span class='badge'>OK</span></li>
            <li class='item' hidden>Hidden 4</li>
          </ul>
          <iframe srcdoc="<button>中のボタン</button><p>フレーム内テキスト</p>"></iframe>
        </body></html>
        """;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
        Page = await _browser.NewPageAsync();
        await Page.SetContentAsync(Html);
    }

    public async Task DisposeAsync()
    {
        if (_browser != null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
    }
}

public class IntegrationTests : IClassFixture<PageFixture>
{
    private readonly IPage page;

    public IntegrationTests(PageFixture fixture)
    {
        page = fixture.Page;
    }

    // ===== GetBy系 + Options =====

    [Fact]
    public async Task GetByRole_NameオプションでDOMにマッチする()
    {
        var locator = LocatorResolver.Resolve(page, "GetByRole",
            @"AriaRole.Button, new() { Name = ""送信"" }");

        Assert.Equal(1, await locator.CountAsync());
        Assert.Equal("送信", await locator.InnerTextAsync());
    }

    [Fact]
    public async Task GetByRole_NameにRegexを渡すとNameRegex経由でマッチする()
    {
        // 振り替えたNameRegexが実際にPlaywright側で機能することの確認
        var locator = LocatorResolver.Resolve(page, "GetByRole",
            @"AriaRole.Button, new() { Name = new Regex(""送信|キャンセル"") }");

        Assert.Equal(2, await locator.CountAsync());
    }

    [Fact]
    public async Task GetByRole_Checkedオプション()
    {
        var locator = LocatorResolver.Resolve(page, "GetByRole",
            "AriaRole.Checkbox, new() { Checked = true }");

        Assert.Equal(1, await locator.CountAsync());
    }

    [Fact]
    public async Task GetByRole_Levelオプション()
    {
        var locator = LocatorResolver.Resolve(page, "GetByRole",
            "AriaRole.Heading, new() { Level = 2 }");

        Assert.Equal("サブ見出し", await locator.InnerTextAsync());
    }

    [Fact]
    public async Task GetByText_Regexで複数マッチ()
    {
        var locator = LocatorResolver.Resolve(page, "GetByText",
            @"new Regex(""Product \d"")");

        Assert.Equal(3, await locator.CountAsync());
    }

    [Fact]
    public async Task GetByLabelとGetByPlaceholder()
    {
        var byLabel = LocatorResolver.Resolve(page, "GetByLabel", @"""ユーザー名""");
        var byPlaceholder = LocatorResolver.Resolve(page, "GetByPlaceholder", @"""入力してください""");

        Assert.Equal(1, await byLabel.CountAsync());
        Assert.Equal(1, await byPlaceholder.CountAsync());
    }

    // ===== ネストしたLocator (Has / And / Or) =====

    [Fact]
    public async Task Locator_Hasのネスト式が実DOMで絞り込む()
    {
        var locator = LocatorResolver.Resolve(page, "Locator",
            @""".item"", new() { Has = GetByText(""OK"") }");

        Assert.Equal(2, await locator.CountAsync()); // OKバッジを持つのは2件
    }

    [Fact]
    public async Task Filter_Hasのセレクタ省略記法()
    {
        var items = LocatorResolver.Resolve(page, "Locator", @""".item""");
        var filtered = LocatorResolver.Resolve(items, "Filter", "new() { Has = .badge }");

        Assert.Equal(2, await filtered.CountAsync());
    }

    [Fact]
    public async Task And条件()
    {
        var items = LocatorResolver.Resolve(page, "Locator", @""".item""");
        var combined = LocatorResolver.Resolve(items, "And", @"GetByText(""Product 2"")");

        Assert.Equal(1, await combined.CountAsync());
        Assert.Contains("Product 2", await combined.InnerTextAsync());
    }

    [Fact]
    public async Task Or条件()
    {
        var send = LocatorResolver.Resolve(page, "GetByRole",
            @"AriaRole.Button, new() { Name = ""送信"" }");
        var either = LocatorResolver.Resolve(send, "Or", @"GetByText(""キャンセル"")");

        Assert.Equal(2, await either.CountAsync());
    }

    // ===== チェーン構文 =====

    [Fact]
    public async Task チェーンで絞り込んでNth()
    {
        var locator = LocatorResolver.Resolve(page, @"Locator(""#list .item"").Nth(1)");

        Assert.Contains("Product 2", await locator.InnerTextAsync());
    }

    [Fact]
    public async Task チェーンのFilterとFirst()
    {
        var locator = LocatorResolver.Resolve(page,
            @"Locator("".item"").Filter(new() { HasText = ""Product"" }).First");

        Assert.Contains("Product 1", await locator.InnerTextAsync());
    }

    // ===== フレーム =====

    [Fact]
    public async Task FrameLocatorチェーンでiframe内の要素を取得()
    {
        var locator = LocatorResolver.Resolve(page,
            @"FrameLocator(""iframe"").GetByRole(AriaRole.Button)");

        Assert.Equal("中のボタン", await locator.InnerTextAsync());
    }

    [Fact]
    public async Task ResolveFrameからの2段呼び出し()
    {
        var frame = LocatorResolver.ResolveFrame(page, "FrameLocator", @"""iframe""");
        var text = LocatorResolver.Resolve(frame, "GetByText", @"""フレーム内テキスト""");

        Assert.Equal(1, await text.CountAsync());
    }

    [Fact]
    public async Task ContentFrame経由でもiframe内に入れる()
    {
        var locator = LocatorResolver.Resolve(page,
            @"Locator(""iframe"").ContentFrame.GetByText(""フレーム内テキスト"")");

        Assert.Equal(1, await locator.CountAsync());
    }

    // ===== 追加カバレッジ (実DOM未検証だったパターン) =====

    [Fact]
    public async Task GetByTestId()
    {
        var locator = LocatorResolver.Resolve(page, "GetByTestId", @"""main-list""");

        Assert.Equal(1, await locator.CountAsync());
    }

    [Fact]
    public async Task GetByText_Exactオプション()
    {
        var exact = LocatorResolver.Resolve(page, "GetByText", @"""詳細"", new() { Exact = true }");
        var partial = LocatorResolver.Resolve(page, "GetByText", @"""詳"", new() { Exact = true }");

        Assert.Equal(1, await exact.CountAsync());
        Assert.Equal(0, await partial.CountAsync());
    }

    [Fact]
    public async Task Filter_HasNotText()
    {
        var items = LocatorResolver.Resolve(page, "Locator", @""".item""");
        var filtered = LocatorResolver.Resolve(items, "Filter",
            @"new() { HasNotText = ""Product 2"" }");

        Assert.Equal(3, await filtered.CountAsync()); // 4件中 Product 2 以外
    }

    [Fact]
    public async Task Filter_Visibleオプション()
    {
        var items = LocatorResolver.Resolve(page, "Locator", @""".item""");
        var visible = LocatorResolver.Resolve(items, "Filter", "new() { Visible = true }");

        Assert.Equal(4, await items.CountAsync());   // hidden 含む
        Assert.Equal(3, await visible.CountAsync()); // hidden 除外
    }

    [Fact]
    public async Task Locatorメソッドへのネスト式が実DOMで機能する()
    {
        // Locator(ILocator, options) オーバーロード経由
        var list = LocatorResolver.Resolve(page, "Locator", @"""#list""");
        var scoped = LocatorResolver.Resolve(list, "Locator", @"GetByText(""Product 2"")");

        Assert.Equal(1, await scoped.CountAsync());
    }

    [Fact]
    public async Task スマートクォートが実DOMでも機能する()
    {
        var locator = LocatorResolver.Resolve(page, "GetByText", "“詳細”");

        Assert.Equal(1, await locator.CountAsync());
    }

    [Fact]
    public async Task 未クォートセレクタが実DOMでも機能する()
    {
        var locator = LocatorResolver.Resolve(page, "Locator", "#list .item");

        Assert.Equal(4, await locator.CountAsync());
    }

    [Fact]
    public async Task Lastプロパティ()
    {
        var locator = LocatorResolver.Resolve(page, @"Locator(""#list .item"").Last");

        Assert.Contains("Hidden 4", await locator.TextContentAsync());
    }

    [Fact]
    public async Task Describeを挟んでも検索結果は変わらない()
    {
        var locator = LocatorResolver.Resolve(page,
            @"GetByRole(AriaRole.Link).Describe(""詳細リンク"")");

        Assert.Equal(1, await locator.CountAsync());
    }

    [Fact]
    public async Task TryResolveFrameが実ページでも動く()
    {
        var ok = LocatorResolver.TryResolveFrame(page, "FrameLocator", @"""iframe""",
            out var frame, out var error);

        Assert.True(ok);
        Assert.Null(error);
        var inner = LocatorResolver.Resolve(frame!, "GetByText", @"""フレーム内テキスト""");
        Assert.Equal(1, await inner.CountAsync());
    }

    // ===== TryResolve =====

    [Fact]
    public async Task TryResolveが実ページでも動く()
    {
        var ok = LocatorResolver.TryResolve(page, "GetByText", @"""詳細""",
            out var locator, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(1, await locator!.CountAsync());
    }

    [Fact]
    public void TryResolveの定義エラー検出も実ページで動く()
    {
        var ok = LocatorResolver.TryResolve(page, "GetByTet", @"""x""",
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("GetByText", error); // もしかして候補
    }
}
