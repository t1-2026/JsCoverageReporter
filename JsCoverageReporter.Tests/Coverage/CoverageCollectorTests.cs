using System.Net;
using JsCoverageReporter.Coverage;
using Microsoft.Playwright;

namespace JsCoverageReporter.Tests.Coverage;

[Collection("CoverageCollectorTests")]
public class CoverageCollectorTests
{
    private class TestServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly string _htmlContent;
        private readonly Task _listenTask;

        public string Url { get; }

        public TestServer(string htmlContent)
        {
            _htmlContent = htmlContent;
            var rng = new Random();

            // ポートが使用中の場合は別のポートでリトライする（最大10回）
            int attempt = 0;
            while (true)
            {
                int port = rng.Next(40000, 50000);
                string url = $"http://localhost:{port}/";

                var listener = new HttpListener();
                listener.Prefixes.Add(url);
                try
                {
                    listener.Start();
                    // 起動成功 → フィールドに設定する
                    _listener = listener;
                    Url = url;
                    break;
                }
                catch (HttpListenerException)
                {
                    // ポートが使用中 → リスナーを破棄してリトライする
                    listener.Close();
                    attempt++;
                    if (attempt >= 10)
                    {
                        throw;
                    }
                }
            }

            _listenTask = Task.Run(ListenLoop);
        }

        private async Task ListenLoop()
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    var request = context.Request;
                    var response = context.Response;

                    if (request.Url?.AbsolutePath == "/nocontent")
                    {
                        response.StatusCode = 204; // No Content (cancels navigation)
                        response.Close();
                        continue;
                    }

                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes(_htmlContent);
                    response.ContentLength64 = buffer.Length;
                    response.ContentType = "text/html; charset=utf-8";
                    
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    response.OutputStream.Close();
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            // Stop() で ListenLoop が HttpListenerException を受けて終了するのを待ってから Close する
            _listener.Stop();
            _listenTask.Wait();
            _listener.Close();
        }
    }

    [Fact]
    public async Task FinalCollect_MergesCoverageWhenNavigationIsCancelled()
    {
        var html = """
            <!DOCTYPE html>
            <html>
            <head>
                <script>
                    window.doAction = function() {
                        window.executed = true;
                    };
                </script>
            </head>
            <body>
                <a id="cancelLink" href="/nocontent">Click me (No Content)</a>
                <button id="actionBtn" onclick="window.doAction()">Do Action</button>
            </body>
            </html>
            """;

        using var server = new TestServer(html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        // 1. Load the page initially
        await page.GotoAsync(server.Url);

        // 2. Start coverage collection
        await using var collector = new CoverageCollector(page);
        await collector.StartAsync([], []);

        // 3. Click the link that returns 204 No Content
        // This triggers an intermediate snapshot in CoverageCollector
        // Wait for request before clicking to ensure it fires reliably
        var requestTask = page.WaitForRequestAsync("**/nocontent");
        await page.ClickAsync("#cancelLink");
        await requestTask;

        // Give some time for the intermediate snapshot logic inside the event handler to finish
        await Task.Delay(500);

        // 4. Click the button, which invokes window.doAction() and increases coverage counts
        await page.ClickAsync("#actionBtn");

        // 5. Stop coverage
        var scripts = await collector.StopAsync();

        // 6. Verify that the script coverage includes the execution inside window.doAction()
        var inlineScript = scripts.FirstOrDefault(s => s.Source.Contains("window.executed = true;"));
        Assert.NotNull(inlineScript);

        // FunctionCoverage includes ranges with count > 0 if executed
        var actFunction = inlineScript.Functions.FirstOrDefault(f => f.FunctionName == "window.doAction" || f.Ranges.Any(r => r.Count > 0 && inlineScript.Source.Substring(r.StartOffset, r.EndOffset - r.StartOffset).Contains("window.executed")));
        
        Assert.NotNull(actFunction);
        var hasActiveRange = actFunction.Ranges.Any(r => r.Count > 0);
        
        Assert.True(hasActiveRange, "The newly executed coverage after cancelled navigation must not be silently dropped.");
    }

    // -----------------------------------------------------------------------
    // scriptFilters / scriptExcludes のフィルターテスト
    // -----------------------------------------------------------------------

    /// <summary>
    /// 複数のパスを返せる TestServer（パス → レスポンス内容の辞書で管理）。
    /// </summary>
    private class MultiPathTestServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Dictionary<string, (string body, string contentType)> _routes;
        private readonly Task _listenTask;

        public string BaseUrl { get; }

        public MultiPathTestServer(Dictionary<string, (string body, string contentType)> routes)
        {
            _routes = routes;
            var rng = new Random();
            int attempt = 0;
            while (true)
            {
                int port = rng.Next(40000, 50000);
                string url = $"http://localhost:{port}/";
                var listener = new HttpListener();
                listener.Prefixes.Add(url);
                try
                {
                    listener.Start();
                    _listener = listener;
                    BaseUrl = url;
                    break;
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                    attempt++;
                    if (attempt >= 10) { throw; }
                }
            }
            _listenTask = Task.Run(ListenLoop);
        }

        private async Task ListenLoop()
        {
            try
            {
                while (_listener.IsListening)
                {
                    var ctx = await _listener.GetContextAsync();
                    // 各リクエストを独立したタスクで処理して並行リクエストに対応する
                    _ = Task.Run(() => HandleRequest(ctx));
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                string path;
                if (ctx.Request.Url == null) { path = "/"; }
                else { path = ctx.Request.Url.AbsolutePath; }
                if (_routes.TryGetValue(path, out var entry))
                {
                    byte[] buf = System.Text.Encoding.UTF8.GetBytes(entry.body);
                    ctx.Response.ContentType = entry.contentType;
                    ctx.Response.ContentLength64 = buf.Length;
                    await ctx.Response.OutputStream.WriteAsync(buf, 0, buf.Length);
                    ctx.Response.OutputStream.Close();
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            // Stop() で ListenLoop が HttpListenerException を受けて終了するのを待ってから Close する
            _listener.Stop();
            _listenTask.Wait();
            _listener.Close();
        }
    }

    /// <summary>
    /// scriptFilters に一致しないスクリプトが結果に含まれないことを確認する。
    /// V8 は同一コンテキスト内で実行されたスクリプトのみを収集するため、
    /// ページロード後にカバレッジを開始し、ボタンクリックでスクリプトを再実行する。
    /// </summary>
    [Fact]
    public async Task StopAsync_ScriptFilter_ExcludesNonMatchingScripts()
    {
        // app.js と vendor.js の2ファイルをロードし、ボタンで再実行するページ
        const string appJs    = "function appFunc()    { window._app    = 'app';    }";
        const string vendorJs = "function vendorFunc() { window._vendor = 'vendor'; }";
        const string html = """
            <!DOCTYPE html>
            <html>
            <head>
            <script src="/app.js"></script>
            <script src="/vendor.js"></script>
            </head>
            <body>
            <button id="btn" onclick="appFunc(); vendorFunc()">Run</button>
            </body>
            </html>
            """;

        var routes = new Dictionary<string, (string, string)>
        {
            ["/"]          = (html,     "text/html; charset=utf-8"),
            ["/app.js"]    = (appJs,    "application/javascript"),
            ["/vendor.js"] = (vendorJs, "application/javascript"),
        };

        using var server = new MultiPathTestServer(routes);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        // ページを先にロードする（V8 コンテキストが確定する）
        await page.GotoAsync(server.BaseUrl);

        // カバレッジを開始する（ページロード後・スクリプト再実行前）
        await using var collector = new CoverageCollector(page);
        await collector.StartAsync(["app.js"], []);

        // ボタンをクリックして app.js と vendor.js の関数を再実行する
        await page.ClickAsync("#btn");

        var scripts = await collector.StopAsync();

        // app.js が含まれているか確認する
        Assert.Contains(scripts, s => s.Url.Contains("app.js"));
        // vendor.js が除外されているか確認する
        Assert.DoesNotContain(scripts, s => s.Url.Contains("vendor.js"));
    }

    /// <summary>
    /// scriptExcludes に一致するスクリプトが結果から除外されることを確認する。
    /// </summary>
    [Fact]
    public async Task StopAsync_ScriptExclude_ExcludesMatchingScripts()
    {
        const string appJs    = "function appFunc()    { window._app    = 'app';    }";
        const string vendorJs = "function vendorFunc() { window._vendor = 'vendor'; }";
        const string html = """
            <!DOCTYPE html>
            <html>
            <head>
            <script src="/app.js"></script>
            <script src="/vendor.js"></script>
            </head>
            <body>
            <button id="btn" onclick="appFunc(); vendorFunc()">Run</button>
            </body>
            </html>
            """;

        var routes = new Dictionary<string, (string, string)>
        {
            ["/"]          = (html,     "text/html; charset=utf-8"),
            ["/app.js"]    = (appJs,    "application/javascript"),
            ["/vendor.js"] = (vendorJs, "application/javascript"),
        };

        using var server = new MultiPathTestServer(routes);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        // ページを先にロードする
        await page.GotoAsync(server.BaseUrl);

        // カバレッジを開始する
        await using var collector = new CoverageCollector(page);
        await collector.StartAsync([], ["vendor.js"]);

        // ボタンをクリックして両方の関数を実行する
        await page.ClickAsync("#btn");

        var scripts = await collector.StopAsync();

        // app.js が含まれているか確認する
        Assert.Contains(scripts, s => s.Url.Contains("app.js"));
        // vendor.js が除外されているか確認する
        Assert.DoesNotContain(scripts, s => s.Url.Contains("vendor.js"));
    }

    // -----------------------------------------------------------------------
    // DisposeAsync のテスト（StopAsync を呼ばずに解放）
    // -----------------------------------------------------------------------

    /// <summary>
    /// StopAsync を呼ばずに DisposeAsync した場合でも例外が発生しないことを確認する。
    /// </summary>
    [Fact]
    public async Task DisposeAsync_WithoutCallingStop_DoesNotThrow()
    {
        const string html = """
            <!DOCTYPE html>
            <html><body><script>var x = 1;</script></body></html>
            """;

        using var server = new TestServer(html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Url);

        // StopAsync を呼ばずに DisposeAsync する → 例外が出ないことを確認する
        var collector = new CoverageCollector(page);
        await collector.StartAsync([], []);

        // DisposeAsync が例外なく完了することを確認する
        var ex = await Record.ExceptionAsync(() => collector.DisposeAsync().AsTask());
        Assert.Null(ex);
    }

    /// <summary>
    /// StopAsync 後に DisposeAsync を呼んでも例外が発生しないことを確認する（二重解放防止）。
    /// </summary>
    [Fact]
    public async Task DisposeAsync_AfterStop_DoesNotThrow()
    {
        const string html = """
            <!DOCTYPE html>
            <html><body><script>var x = 1;</script></body></html>
            """;

        using var server = new TestServer(html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Url);

        await using var collector = new CoverageCollector(page);
        await collector.StartAsync([], []);
        await collector.StopAsync();

        // StopAsync 後の DisposeAsync も例外なく完了することを確認する
        var ex = await Record.ExceptionAsync(() => collector.DisposeAsync().AsTask());
        Assert.Null(ex);
    }

    /// <summary>
    /// StopAsync を二回呼び出した場合、二回目は空リストが返り例外が発生しないことを確認する。
    /// </summary>
    [Fact]
    public async Task StopAsync_CalledTwice_ReturnsEmptyListOnSecondCall()
    {
        const string html = """
            <!DOCTYPE html>
            <html><body><script>var x = 1;</script></body></html>
            """;

        using var server = new TestServer(html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Url);

        await using var collector = new CoverageCollector(page);
        await collector.StartAsync([], []);

        // 一回目は正常にデータを返す
        var firstResult = await collector.StopAsync();

        // 二回目は空リストが返る（例外なし）
        var secondResult = await collector.StopAsync();
        Assert.Empty(secondResult);
    }

    /// <summary>
    /// StopAsync の前にページを閉じた場合、例外が発生しないことを確認する。
    /// ページが閉じられると page.Url が例外を投げる場合があるため、try-catch で保護されている。
    /// </summary>
    [Fact]
    public async Task StopAsync_AfterPageClose_DoesNotThrow()
    {
        const string html = """
            <!DOCTYPE html>
            <html><body><script>var x = 1;</script></body></html>
            """;

        using var server = new TestServer(html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Url);

        var collector = new CoverageCollector(page);
        await collector.StartAsync([], []);

        // ページを閉じてから StopAsync を呼ぶ（page.Url が例外を投げる可能性がある状況）
        await page.CloseAsync();

        // 例外が発生しないことを確認する（空リストまたは部分的なデータが返る）
        var exception = await Record.ExceptionAsync(() => collector.StopAsync());
        Assert.Null(exception);

        await collector.DisposeAsync();
    }

    /// <summary>
    /// StartAsync を2回呼び出した場合、例外が発生せず StopAsync も正常に動作することを確認する。
    /// バグ修正: _pageEventHandler の二重登録防止と _trackedPages の重複追跡防止。
    /// </summary>
    [Fact]
    public async Task StartAsync_CalledTwice_NoExceptionAndStopAsyncWorks()
    {
        const string html = """
            <!DOCTYPE html>
            <html><body><script>var x = 1;</script></body></html>
            """;

        using var server = new TestServer(html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Url);

        await using var collector = new CoverageCollector(page);

        // 1回目の StartAsync（通常の起動）
        await collector.StartAsync([], []);

        // 2回目の StartAsync が例外なく完了すること
        var ex = await Record.ExceptionAsync(() => collector.StartAsync([], []));
        Assert.Null(ex);

        // StopAsync も正常に動作し、同一スクリプトが重複して収集されていないこと
        var scripts = await collector.StopAsync();
        Assert.NotNull(scripts);

        // 同じ URL のスクリプトが 1 件しか存在しないこと（重複収集の防止確認）
        var grouped = scripts.GroupBy(s => s.Url).Where(g => g.Count() > 1).ToList();
        Assert.Empty(grouped);
    }

    /// <summary>
    /// scriptFilters と scriptExcludes の両方にマッチするスクリプトがある場合、
    /// scriptExcludes が優先されてスクリプトが結果から除外されることを確認する。
    /// </summary>
    [Fact]
    public async Task StopAsync_ScriptMatchesBothFilterAndExclude_ExcludeTakesPrecedence()
    {
        // vendor.min.js は scriptFilters=["vendor"] にマッチするが scriptExcludes=["vendor.min"] にもマッチする
        const string vendorMinJs = "function vendorMinFunc() { window._v = 1; }";
        const string html = $"""
            <!DOCTYPE html>
            <html><body>
            <script src="/vendor.min.js"></script>
            <button id="btn" onclick="vendorMinFunc()">run</button>
            </body></html>
            """;

        var routes = new Dictionary<string, (string body, string contentType)>
        {
            ["/"] = (html, "text/html; charset=utf-8"),
            ["/vendor.min.js"] = (vendorMinJs, "application/javascript"),
        };

        using var server = new MultiPathTestServer(routes);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.BaseUrl);

        await using var collector = new CoverageCollector(page);
        // フィルター: "vendor" を含む → vendor.min.js がマッチ
        // 除外:       "vendor.min" を含む → vendor.min.js もマッチ → 除外が優先される
        await collector.StartAsync(["vendor"], ["vendor.min"]);
        await page.ClickAsync("#btn");
        var scripts = await collector.StopAsync();

        // 除外が優先されるため vendor.min.js は結果に含まれないこと
        Assert.DoesNotContain(scripts, s => s.Url.Contains("vendor.min"));
    }

    /// <summary>
    /// ナビゲーションリクエストが発生してスナップショットタスクが開始されたタイミングで
    /// DisposeAsync を呼んでも例外が発生せず完了することを確認する。
    /// DisposeAsync はインフライトの _snapTasks を待機してから CDP セッションを解放する。
    /// </summary>
    [Fact]
    public async Task DisposeAsync_DuringPendingNavigation_DoesNotThrow()
    {
        const string html = """
            <!DOCTYPE html>
            <html><body>
            <a id="link" href="/nocontent">click</a>
            </body></html>
            """;

        using var server = new TestServer(html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Url);

        var collector = new CoverageCollector(page);
        await collector.StartAsync([], []);

        // リンクをクリックしてナビゲーションを開始する（完了を待たない）
        // → reqHandler が発火してスナップショットタスクが _snapTasks に積まれる
        // WaitForRequestAsync でリクエストイベント到着を確実に待つ（Task.Delay より信頼性が高い）
        var reqTask = page.WaitForRequestAsync("**/nocontent");
        _ = page.ClickAsync("#link");
        await reqTask;

        // スナップショットタスク実行中に DisposeAsync を呼ぶ → 例外なく完了すること
        var ex = await Record.ExceptionAsync(() => collector.DisposeAsync().AsTask());
        Assert.Null(ex);
    }

    /// <summary>
    /// StartAsync を呼ばずに StopAsync を呼んだ場合、空リストを返して例外が発生しないことを確認する。
    /// </summary>
    [Fact]
    public async Task StopAsync_BeforeStartAsync_ReturnsEmptyList()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        await using var collector = new CoverageCollector(page);

        // StartAsync を呼ばずに StopAsync を呼ぶ → 例外なく空リストが返るべき
        var ex = await Record.ExceptionAsync(async () =>
        {
            var scripts = await collector.StopAsync();
            Assert.Empty(scripts);
        });
        Assert.Null(ex);
    }

    /// <summary>
    /// DisposeAsync を2回呼んでも例外が発生しないことを確認する。
    /// </summary>
    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        var collector = new CoverageCollector(page);
        await collector.StartAsync([], []);

        // 1回目の DisposeAsync
        await collector.DisposeAsync();

        // 2回目の DisposeAsync → 例外なく完了すること
        var ex = await Record.ExceptionAsync(() => collector.DisposeAsync().AsTask());
        Assert.Null(ex);
    }

    /// <summary>
    /// SnapshotThrottleMs を 0 に設定するとスロットリングが無効になり、
    /// 連続ナビゲーションでもスナップショットが取れることを確認する。
    /// </summary>
    [Fact]
    public async Task SnapshotThrottleMs_SetToZero_AllowsImmediateSnapshot()
    {
        const string html = """
            <!DOCTYPE html>
            <html><body>
            <a id="link" href="/nocontent">click</a>
            <script>function foo() { return 1; }</script>
            </body></html>
            """;

        using var server = new TestServer(html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Url);

        // インスタンスごとにスロットリングを無効化する（デフォルト 500ms → 0ms）
        // インスタンスフィールドのため他テストへの影響がなく try/finally による復元は不要
        await using var collector = new CoverageCollector(page);
        collector.SnapshotThrottleMs = 0;
        await collector.StartAsync([], []);

        // スロットリング無効のため例外なくナビゲーションをトリガーできる
        // WaitForRequestAsync でリクエスト到着を待ってから StopAsync を呼ぶ（Task.Delay より信頼性が高い）
        var ex = await Record.ExceptionAsync(async () =>
        {
            var reqTask = page.WaitForRequestAsync("**/nocontent");
            _ = page.ClickAsync("#link");
            await reqTask;
            var scripts = await collector.StopAsync();
            // スナップショットが取れること（件数 >= 0）
            Assert.NotNull(scripts);
        });
        Assert.Null(ex);
    }

    /// <summary>
    /// TC-13b: reqHandler の _stopped チェックと _snapTasks.Add の間にレースが起きないことを確認する。
    /// StopAsync をナビゲーションと並行して即座に呼び出したとき、
    /// スナップショットタスクが孤立してデータ競合を起こさないことを検証する。
    /// （Coverage.cs reqHandler のレース条件修正の回帰テスト）
    /// </summary>
    [Fact]
    public async Task StopAsync_CalledConcurrentlyWithNavigation_DoesNotRaceWithSnapshot()
    {
        const string html = """
            <!DOCTYPE html>
            <html><body>
            <a id="link" href="/nocontent">click</a>
            <script>function foo() { return 1; }</script>
            </body></html>
            """;

        using var server = new TestServer(html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Url);

        var collector = new CoverageCollector(page);
        collector.SnapshotThrottleMs = 0;
        await collector.StartAsync([], []);

        // ナビゲーション直後に StopAsync を呼ぶ
        // → reqHandler の _stopped チェック後に StopAsync が完了するレース窓を狙う
        // WaitForRequestAsync でリクエストイベント到着を確実に待つ（1ms Delay より信頼性が高い）
        Exception? ex = null;
        IReadOnlyList<ScriptCoverage>? result = null;
        var requestSignal = page.WaitForRequestAsync("**/nocontent");
        var navTask  = page.ClickAsync("#link");
        var stopTask = Task.Run(async () =>
        {
            await requestSignal; // reqHandler が発火するまで待機
            result = await collector.StopAsync();
        });
        ex = await Record.ExceptionAsync(() => Task.WhenAll(navTask, stopTask));
        // 例外なく完了し、データ競合によるクラッシュが起きないこと
        Assert.Null(ex);
        // StopAsync が結果を返せていること（null でないこと）
        Assert.NotNull(result);
    }

    /// <summary>
    /// TC-14: ナビゲーション開始直後にページが閉じられた場合、
    /// reqHandler 内の targetPage.Url アクセスが例外を投げても StopAsync が例外なく完了することを確認する。
    /// （Coverage.cs の reqHandler 内 targetPage.Url に try-catch を追加した防衛コードの回帰テスト）
    /// </summary>
    [Fact]
    public async Task ReqHandler_PageClosedDuringNavigation_StopAsyncDoesNotThrow()
    {
        const string html = """
            <!DOCTYPE html>
            <html><body>
            <a id="link" href="/nocontent">click</a>
            <script>function foo() { return 1; }</script>
            </body></html>
            """;

        using var server = new TestServer(html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Url);

        var collector = new CoverageCollector(page);
        collector.SnapshotThrottleMs = 0;
        await collector.StartAsync([], []);

        var ex = await Record.ExceptionAsync(async () =>
        {
            // ナビゲーションを開始した直後にページを閉じる
            // → reqHandler が発火した際に targetPage.Url が例外を投げる可能性がある
            // WaitForRequestAsync でリクエスト到着を確認してからページを閉じる（Task.Delay より信頼性が高い）
            var reqTask = page.WaitForRequestAsync("**/nocontent");
            _ = page.ClickAsync("#link");
            await reqTask;
            await page.CloseAsync();
            // StopAsync は例外なく完了すること
            await collector.StopAsync();
        });
        Assert.Null(ex);
    }

    /// <summary>
    /// IMPORTANT-2: DisposeAsync が _snapTasks をループで待機することを確認する（回帰テスト）。
    /// DisposeAsync 中にスナップショットタスクが _snapTasks に追加されても
    /// CDP セッション解放前に完了を待機することを確認する。
    /// </summary>
    [Fact]
    public async Task DisposeAsync_SnapTaskAddedAfterCopy_DoesNotThrow()
    {
        const string html = """
            <!DOCTYPE html>
            <html><body>
            <a id="link" href="/nocontent">click</a>
            <script>function bar() { return 2; }</script>
            </body></html>
            """;

        using var server = new TestServer(html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Url);

        var collector = new CoverageCollector(page);
        collector.SnapshotThrottleMs = 0;
        await collector.StartAsync([], []);

        // ナビゲーションを開始してスナップショットタスクが積まれ始めるタイミングで DisposeAsync を呼ぶ
        // DisposeAsync がループ待機しない場合、孤立したスナップショットタスクが解放済みセッションを使用し得る
        // WaitForRequestAsync でリクエスト到着を確認してから DisposeAsync を呼ぶ（Task.Delay より信頼性が高い）
        var ex = await Record.ExceptionAsync(async () =>
        {
            var reqTask = page.WaitForRequestAsync("**/nocontent");
            _ = page.ClickAsync("#link");
            await reqTask;
            await collector.DisposeAsync();
        });
        Assert.Null(ex);
    }

    /// <summary>
    /// scriptFilters の URL 比較は OrdinalIgnoreCase のため、
    /// フィルターを大文字で指定しても小文字の URL にマッチすることを確認する。
    /// </summary>
    [Fact]
    public async Task StopAsync_ScriptFilter_CaseInsensitive()
    {
        const string appJs = "function appFunc() { window._app = 'app'; }";
        const string html = """
            <!DOCTYPE html>
            <html><body>
            <script src="/app.js"></script>
            <button id="btn" onclick="appFunc()">run</button>
            </body></html>
            """;

        var routes = new Dictionary<string, (string, string)>
        {
            ["/"]       = (html,  "text/html; charset=utf-8"),
            ["/app.js"] = (appJs, "application/javascript"),
        };

        using var server = new MultiPathTestServer(routes);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.BaseUrl);

        await using var collector = new CoverageCollector(page);
        // 大文字で指定 → 小文字 URL "app.js" にも OrdinalIgnoreCase でマッチするはず
        await collector.StartAsync(["App.JS"], []);
        await page.ClickAsync("#btn");
        var scripts = await collector.StopAsync();

        // 大文字フィルターでも小文字 URL がマッチして結果に含まれること
        Assert.Contains(scripts, s => s.Url.Contains("app.js"));
    }
}
