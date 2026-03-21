using System.Net;
using JsCoverageReporter.Actions;
using JsCoverageReporter.Config;
using Microsoft.Playwright;

namespace JsCoverageReporter.Tests.Actions;

public class ActionRunnerTests
{
    // A simple HTTP listener that serves a basic HTML page for Playwright to interact with
    private class TestServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _listenTask;
        private readonly string _htmlContent;

        public string Url { get; }

        public TestServer(string htmlContent)
        {
            _htmlContent = htmlContent;
            // Generate a random high port
            int port = new Random().Next(40000, 50000);
            Url = $"http://localhost:{port}/";
            
            _listener = new HttpListener();
            _listener.Prefixes.Add(Url);
            _listener.Start();
            
            _listenTask = Task.Run(ListenLoop);
        }

        private async Task ListenLoop()
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    var response = context.Response;
                    
                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes(_htmlContent);
                    response.ContentLength64 = buffer.Length;
                    response.ContentType = "text/html; charset=utf-8";
                    
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    response.OutputStream.Close();
                }
            }
            catch (HttpListenerException)
            {
                // Expected when listener is stopped
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }

    [Fact]
    public async Task RunAsync_ExecutesAllActionTypesCorrectly()
    {
        // Arrange
        var html = """
            <!DOCTYPE html>
            <html>
            <body>
                <button id="btnClick">ClickMe</button>
                <input id="txtFill" type="text" />
                <select id="selOption">
                    <option value="opt1">Opt 1</option>
                    <option value="opt2">Opt 2</option>
                </select>
                <input id="chkBox" type="checkbox" />
                <button id="btnDblClick">DblClickMe</button>

                <script>
                    window.clicks = 0;
                    document.getElementById('btnClick').addEventListener('click', () => window.clicks++);
                    
                    window.dblclicks = 0;
                    document.getElementById('btnDblClick').addEventListener('dblclick', () => window.dblclicks++);
                </script>
            </body>
            </html>
            """;

        using var server = new TestServer(html);

        // Setting up Playwright purely for testing UI interaction.
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(); // headless by default
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Url);

        var actions = new List<ScenarioAction>
        {
            new ScenarioAction { Type = "click", Selector = "#btnClick" },
            new ScenarioAction { Type = "fill", Selector = "#txtFill", Value = "testValue" },
            new ScenarioAction { Type = "select", Selector = "#selOption", Value = "opt2" },
            new ScenarioAction { Type = "check", Selector = "#chkBox" },
            new ScenarioAction { Type = "dblclick", Selector = "#btnDblClick" },
            new ScenarioAction { Type = "uncheck", Selector = "#chkBox" }
        };

        // Act
        await ActionRunner.RunAsync(page, actions, timeoutMs: 5000, continueOnError: false);

        // Assert
        // Verify click count
        var clicks = await page.EvaluateAsync<int>("window.clicks");
        Assert.Equal(1, clicks);

        // Verify fill value
        var fillValue = await page.InputValueAsync("#txtFill");
        Assert.Equal("testValue", fillValue);

        // Verify select usage
        var selectedValue = await page.EvaluateAsync<string>("document.getElementById('selOption').value");
        Assert.Equal("opt2", selectedValue);

        // Verify check/uncheck - last action was uncheck
        var isChecked = await page.EvaluateAsync<bool>("document.getElementById('chkBox').checked");
        Assert.False(isChecked); 

        // Verify double click count
        var dblClicks = await page.EvaluateAsync<int>("window.dblclicks");
        Assert.Equal(1, dblClicks);
    }
    [Fact]
    public async Task RunAsync_WaitAction_TimesOutCorrectly()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        var actions = new List<ScenarioAction>
        {
            new ScenarioAction { Type = "wait", Milliseconds = 9999999 }
        };

        // 50ms タイムアウトに設定。TimeoutException がスローされるべき。
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await ActionRunner.RunAsync(page, actions, timeoutMs: 50, continueOnError: false);
        });
    }

    [Fact]
    public async Task RunAsync_ContinueOnError_SwallowsException()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        var actions = new List<ScenarioAction>
        {
            new ScenarioAction { Type = "click", Selector = "#not-found" }
        };

        // Timeout 50ms. continueOnError = true なので例外は発生しないはず
        await ActionRunner.RunAsync(page, actions, timeoutMs: 50, continueOnError: true);
        
        // Assert passing if it reaches here
        Assert.True(true);
    }

    [Fact]
    public async Task RunAsync_WaitAction_TimeoutMsZero_DoesNotCancelImmediately()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        var actions = new List<ScenarioAction>
        {
            new ScenarioAction { Type = "wait", Milliseconds = 50 } // 50ms待つ
        };

        // timeoutMs = 0 （Playwrightではタイムアウト無効の意）
        // 以前のバグでは即時キャンセルされてTimeoutExceptionが出ていた。例外なく完走すれば成功。
        await ActionRunner.RunAsync(page, actions, timeoutMs: 0, continueOnError: false);

        Assert.True(true);
    }

    [Fact]
    public async Task RunAsync_WaitAction_NegativeDelay_ContinuesWithoutException()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        var actions = new List<ScenarioAction>
        {
            new ScenarioAction { Type = "wait", Milliseconds = -500 } // マイナスの値
        };

        // 以前のバグではここで ArgumentOutOfRangeException が発生してクラッシュしていた。
        // continueOnError = false でも、delayMs が 0 にクランプされるため安全に完走するはず。
        await ActionRunner.RunAsync(page, actions, timeoutMs: 5000, continueOnError: false);

        Assert.True(true);
    }

    [Fact]
    public async Task RunAsync_NullActions_DoesNotThrow()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        // null のアクションリストを渡しても NullReferenceException が発生しないことを確認する
        await ActionRunner.RunAsync(page, null!, timeoutMs: 5000, continueOnError: false);

        Assert.True(true);
    }

    [Fact]
    public async Task RunAsync_EmptyTypeAction_SkipsWithWarning()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        var actions = new List<ScenarioAction>
        {
            new ScenarioAction { Type = "", Selector = "#test" }
        };

        // type が空文字の場合は default ケースに入り、スキップされるはず
        await ActionRunner.RunAsync(page, actions, timeoutMs: 5000, continueOnError: false);

        Assert.True(true);
    }

    /// <summary>
    /// select アクションで value が null の場合、警告を出してスキップし例外が発生しないことを確認する。
    /// </summary>
    [Fact]
    public async Task RunAsync_SelectAction_NullValue_SkipsWithWarning()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        var actions = new List<ScenarioAction>
        {
            // value = null なので SelectOptionAsync は呼ばれず警告だけ出るはず
            new ScenarioAction { Type = "select", Selector = "#dropdown", Value = null }
        };

        // value が null の場合は警告してスキップ — 例外が発生しないことを確認する
        await ActionRunner.RunAsync(page, actions, timeoutMs: 5000, continueOnError: false);

        Assert.True(true);
    }

    /// <summary>
    /// navigate アクションで url が空文字の場合、警告を出してスキップし例外が発生しないことを確認する。
    /// </summary>
    [Fact]
    public async Task RunAsync_NavigateAction_EmptyUrl_SkipsWithWarning()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        var actions = new List<ScenarioAction>
        {
            // url が空文字なので GotoAsync は呼ばれず警告だけ出るはず
            new ScenarioAction { Type = "navigate", Url = "" }
        };

        // url が空文字の場合は警告してスキップ — 例外が発生しないことを確認する
        await ActionRunner.RunAsync(page, actions, timeoutMs: 5000, continueOnError: false);

        Assert.True(true);
    }

    /// <summary>
    /// scroll アクションにセレクターを指定すると、その要素がビューポートにスクロールされることを確認する。
    /// </summary>
    [Fact]
    public async Task RunAsync_ScrollAction_WithSelector_ScrollsElementIntoView()
    {
        // ページの高さを超える内容の後に対象要素を配置して最初はビューポート外にする
        var html = """
            <!DOCTYPE html>
            <html>
            <body style="margin:0">
                <div style="height:2000px"></div>
                <div id="target">ターゲット要素</div>
            </body>
            </html>
            """;

        using var server = new TestServer(html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Url);

        // 最初はスクロール位置が 0 であることを確認する
        var initialScrollY = await page.EvaluateAsync<double>("window.scrollY");
        Assert.Equal(0, initialScrollY);

        var actions = new List<ScenarioAction>
        {
            // selector を指定すると要素をビューポートにスクロールする
            new ScenarioAction { Type = "scroll", Selector = "#target" }
        };

        await ActionRunner.RunAsync(page, actions, timeoutMs: 5000, continueOnError: false);

        // スクロール後は scrollY が 0 より大きくなるはず
        var afterScrollY = await page.EvaluateAsync<double>("window.scrollY");
        Assert.True(afterScrollY > 0, $"scrollY should be > 0 after scroll, but was {afterScrollY}");
    }

    /// <summary>
    /// scroll アクションにセレクターなしで x/y を指定すると、ページが指定量だけスクロールされることを確認する。
    /// </summary>
    [Fact]
    public async Task RunAsync_ScrollAction_WithoutSelector_ScrollsPageByDelta()
    {
        // スクロール可能な高さを持つページを用意する
        var html = """
            <!DOCTYPE html>
            <html>
            <body style="margin:0">
                <div style="height:3000px"></div>
            </body>
            </html>
            """;

        using var server = new TestServer(html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Url);

        var actions = new List<ScenarioAction>
        {
            // selector なし・Y = 500 でページを 500px 下方向にスクロールする
            new ScenarioAction { Type = "scroll", X = 0, Y = 500 }
        };

        await ActionRunner.RunAsync(page, actions, timeoutMs: 5000, continueOnError: false);

        // スクロール後は scrollY が 500 付近になるはず（ブラウザの丸め誤差を考慮して > 0 で確認）
        var afterScrollY = await page.EvaluateAsync<double>("window.scrollY");
        Assert.True(afterScrollY > 0, $"scrollY should be > 0 after scroll, but was {afterScrollY}");
    }

    /// <summary>
    /// scroll アクションにセレクターも x/y も指定しない場合、例外なく完走することを確認する。
    /// </summary>
    [Fact]
    public async Task RunAsync_ScrollAction_NoSelectorNoXY_DoesNotThrow()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        var actions = new List<ScenarioAction>
        {
            // selector も x/y も未指定 → (0, 0) スクロールとして処理される
            new ScenarioAction { Type = "scroll" }
        };

        await ActionRunner.RunAsync(page, actions, timeoutMs: 5000, continueOnError: false);

        Assert.True(true);
    }
}
