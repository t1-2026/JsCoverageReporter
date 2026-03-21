using System.Net;
using JsCoverageReporter.Coverage;
using Microsoft.Playwright;

namespace JsCoverageReporter.Tests.Coverage;

public class CoverageCollectorTests
{
    private class TestServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly string _htmlContent;

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

            Task.Run(ListenLoop);
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
            _listener.Stop();
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
            Task.Run(ListenLoop);
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
                string path = ctx.Request.Url?.AbsolutePath ?? "/";
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
            _listener.Stop();
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
}
