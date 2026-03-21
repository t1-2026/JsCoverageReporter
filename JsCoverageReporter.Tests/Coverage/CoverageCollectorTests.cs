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
}
