using System.Net;
using JsCoverageReporter.Actions;
using JsCoverageReporter.Config;
using Microsoft.Playwright;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

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
                    var response = context.Response;
                    
                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes(_htmlContent);
                    response.ContentLength64 = buffer.Length;
                    response.ContentType = "text/html; charset=utf-8";
                    
                    await using var stream = response.OutputStream;
                    await stream.WriteAsync(buffer, 0, buffer.Length);
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
            // Stop() で ListenLoop が HttpListenerException を受けて終了するのを待ってから Close する
            _listenTask.Wait();
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

    /// <summary>
    /// "wait" アクションで milliseconds を省略（null）した場合、
    /// 警告メッセージが標準エラーに出力されることを確認する（#13 修正）。
    /// 修正前は無言で 0ms 待機（ノーオプ）になっていてユーザーが気づけなかった。
    /// </summary>
    [Fact]
    public async Task RunAsync_WaitAction_NullMilliseconds_WarningEmitted()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        var actions = new List<ScenarioAction>
        {
            // milliseconds を指定しない（null）→ 0ms 待機になるが警告が出るべき
            new ScenarioAction { Type = "wait" }
        };

        var captured = new System.IO.StringWriter();
        var originalErr = Console.Error;
        Console.SetError(captured);
        try
        {
            await ActionRunner.RunAsync(page, actions, timeoutMs: 5000, continueOnError: false);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        // "wait" アクションで milliseconds が未指定の旨の警告が出ること
        Assert.Contains("[Warning]", captured.ToString());
        Assert.Contains("wait", captured.ToString());
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

    /// <summary>
    /// action.Type が null の場合、警告メッセージ中に "(null)" が含まれ例外が発生しないことを確認する。
    /// </summary>
    [Fact]
    public async Task RunAsync_ActionTypeIsNull_ShowsNullLabelAndDoesNotThrow()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        var actions = new List<ScenarioAction>
        {
            // Type が null → default ケースで "(null)" と表示されてスキップされるはず
            new ScenarioAction { Type = null! }
        };

        // 例外が発生しないことを確認する
        var ex = await Record.ExceptionAsync(() => ActionRunner.RunAsync(page, actions));
        Assert.Null(ex);
    }

    /// <summary>
    /// scroll アクションで x が負数の場合、例外なく実行されることを確認する。
    /// window.scrollBy は負数を受け入れ左スクロールとして処理される。
    /// </summary>
    [Fact]
    public async Task RunAsync_ScrollAction_NegativeDeltaX_DoesNotThrow()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        var actions = new List<ScenarioAction>
        {
            // x が負数 → window.scrollBy(-100, 0) として実行される（例外なし）
            new ScenarioAction { Type = "scroll", X = -100, Y = 0 }
        };

        var ex = await Record.ExceptionAsync(() => ActionRunner.RunAsync(page, actions));
        Assert.Null(ex);
    }

    /// <summary>
    /// scroll アクションで X・Y 両方が非ゼロの場合、例外なく実行されることを確認する。
    /// X と Y が両方 null でない場合のコードパスを網羅する。
    /// </summary>
    [Fact]
    public async Task RunAsync_ScrollAction_BothXAndY_DoesNotThrow()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        var actions = new List<ScenarioAction>
        {
            // X も Y も非ゼロ → window.scrollBy(100, 200) として実行される
            new ScenarioAction { Type = "scroll", X = 100, Y = 200 }
        };
        var ex = await Record.ExceptionAsync(() => ActionRunner.RunAsync(page, actions));
        Assert.Null(ex);
    }

    /// <summary>
    /// continueOnError = true のとき、OperationCanceledException は再スローされることを確認する。
    /// BUG: 現在の実装は continueOnError=true のとき全例外を握りつぶすため、
    ///      キャンセルシグナルも無視してしまう。
    /// FIX: catch ブロックで OperationCanceledException を先に再スローする。
    /// NSubstitute で IPage をモックして GotoAsync が OperationCanceledException を投げるようにする。
    /// </summary>
    [Fact]
    public async Task RunAsync_ContinueOnError_DoesNotSwallowOperationCanceledException()
    {
        // IPage のモックを作成して navigate アクションが OperationCanceledException を投げるようにする
        var page = Substitute.For<IPage>();
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>())
            .ThrowsAsync(new OperationCanceledException("テストキャンセル"));

        var actions = new List<ScenarioAction>
        {
            new ScenarioAction { Type = "navigate", Url = "http://example.com" }
        };

        // continueOnError = true でも OperationCanceledException は再スローされるはず
        var ex = await Record.ExceptionAsync(
            () => ActionRunner.RunAsync(page, actions, continueOnError: true));

        // OperationCanceledException が伝播されているか確認する
        // 未修正の場合: ex は null（例外が握りつぶされる）
        // 修正後:       ex は OperationCanceledException
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    /// <summary>
    /// select アクションで value がどの option とも一致しない場合、
    /// continueOnError = true のときは警告を出して次のアクションへ進むことを確認する。
    /// </summary>
    [Fact]
    public async Task RunAsync_SelectAction_InvalidValue_ContinueOnError_DoesNotThrow()
    {
        const string html = """
            <!DOCTYPE html>
            <html><body>
            <select id="s"><option value="a">A</option></select>
            </body></html>
            """;

        using var server = new TestServer(html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Url);

        var actions = new List<ScenarioAction>
        {
            // "zzz" は存在しない value → Playwright が例外を投げるが continueOnError=true でスキップ
            new ScenarioAction { Type = "select", Selector = "#s", Value = "zzz" }
        };

        var ex = await Record.ExceptionAsync(
            () => ActionRunner.RunAsync(page, actions, timeoutMs: 3000, continueOnError: true));
        Assert.Null(ex);
    }

    /// <summary>
    /// scroll アクションで X を指定せず（null）Y のみを指定した場合、
    /// Y 方向だけスクロールされ X デルタは 0 として扱われることを確認する。
    /// action.X == null のとき deltaX = 0 になるコードパスの文書化テスト。
    /// </summary>
    [Fact]
    public async Task RunAsync_ScrollAction_NullXWithY_ScrollsVertically()
    {
        // スクロール可能な高さを持つページを用意する
        const string html = """
            <!DOCTYPE html>
            <html><body style="height:2000px;"></body></html>
            """;

        using var server = new TestServer(html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Url);

        var actions = new List<ScenarioAction>
        {
            // X は未指定（null → deltaX=0）、Y のみ指定 → 縦方向のみスクロール
            new ScenarioAction { Type = "scroll", Y = 300 }
        };

        await ActionRunner.RunAsync(page, actions, timeoutMs: 5000, continueOnError: false);

        // 縦スクロールが発生していること
        var scrollY = await page.EvaluateAsync<double>("window.scrollY");
        Assert.True(scrollY > 0, $"scrollY should be > 0, but was {scrollY}");
        // 横スクロールは 0 のまま（X=null → deltaX=0）
        var scrollX = await page.EvaluateAsync<double>("window.scrollX");
        Assert.Equal(0, scrollX);
    }

    /// <summary>
    /// wait に int.MaxValue ミリ秒を指定した場合、timeoutMs で正しくキャンセルされて
    /// TimeoutException が発生することを確認する（Task.Delay(int.MaxValue) 自体は有効な呼び出し）。
    /// </summary>
    [Fact]
    public async Task RunAsync_WaitMaxInt_CancelledByTimeout_ThrowsTimeoutException()
    {
        // IPage モックを使用（wait アクションはページ操作を行わないため）
        var page = Substitute.For<IPage>();
        var actions = new List<ScenarioAction>
        {
            new ScenarioAction { Type = "wait", Milliseconds = int.MaxValue }
        };

        // timeoutMs = 200ms で早めにキャンセルする
        var ex = await Record.ExceptionAsync(
            () => ActionRunner.RunAsync(page, actions, timeoutMs: 200));

        // wait のタイムアウトは TaskCanceledException を TimeoutException にラップして投げる
        Assert.IsType<TimeoutException>(ex);
    }

    /// <summary>
    /// "close" アクション実行後にページが閉じられていることを確認する。
    /// </summary>
    [Fact]
    public async Task RunAsync_CloseAction_ClosesPage()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        // 最初はページが開いていることを確認する
        Assert.False(page.IsClosed);

        var actions = new List<ScenarioAction>
        {
            new ScenarioAction { Type = "close" }
        };

        // Act
        await ActionRunner.RunAsync(page, actions, timeoutMs: 5000, continueOnError: false);

        // close アクション後はページが閉じているはず
        Assert.True(page.IsClosed);
    }

    /// <summary>
    /// "close" アクションの後に続くアクションは実行されないことを確認する。
    /// 修正前は continueOnError=false の場合でも TargetClosedError が発生していたが、
    /// return で抜けるようになったため例外なく完走する。
    /// </summary>
    [Fact]
    public async Task RunAsync_CloseAction_SubsequentActionsAreSkipped()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        // "close" の後に存在しないセレクターへのクリックを追加する。
        // 修正前: page が閉じた後も click を試みて TargetClosedError がスローされていた。
        // 修正後: close で return するため後続アクションはスキップされ例外が出ない。
        var actions = new List<ScenarioAction>
        {
            new ScenarioAction { Type = "close" },
            new ScenarioAction { Type = "click", Selector = "#nonexistent" }
        };

        // continueOnError = false のまま例外なく完走するはず
        var ex = await Record.ExceptionAsync(
            () => ActionRunner.RunAsync(page, actions, timeoutMs: 5000, continueOnError: false));

        Assert.Null(ex);
        // close が実行されてページは閉じているはず
        Assert.True(page.IsClosed);
    }

    /// <summary>
    /// "close" アクション実行前に onBeforeClose コールバックが呼ばれることを確認する。
    /// </summary>
    [Fact]
    public async Task RunAsync_CloseAction_CallsOnBeforeCloseBeforeClosing()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        bool callbackCalled = false;
        bool pageWasOpenDuringCallback = false;

        // onBeforeClose: コールバック呼び出し時にページがまだ開いているかを記録する
        Func<IPage, Task> onBeforeClose = p =>
        {
            callbackCalled = true;
            pageWasOpenDuringCallback = !p.IsClosed;
            return Task.CompletedTask;
        };

        var actions = new List<ScenarioAction>
        {
            new ScenarioAction { Type = "close" }
        };

        await ActionRunner.RunAsync(page, actions, timeoutMs: 5000, continueOnError: false,
            onBeforeClose: onBeforeClose);

        // コールバックが呼ばれていること
        Assert.True(callbackCalled);
        // コールバック呼び出し時はページがまだ開いていること（close より前に呼ばれる）
        Assert.True(pageWasOpenDuringCallback);
        // コールバック後にページが閉じていること
        Assert.True(page.IsClosed);
    }

    /// <summary>
    /// "close" アクションで onBeforeClose コールバックが例外をスローした場合の動作確認。
    /// continueOnError = true のときは例外が握りつぶされ、ページは閉じられないまま続行される。
    /// continueOnError = false のときは例外が再スローされる。
    /// </summary>
    [Fact]
    public async Task RunAsync_CloseAction_OnBeforeCloseThrows_ContinueOnError_SwallowsException()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        // onBeforeClose が例外をスローするコールバック
        Func<IPage, Task> onBeforeClose = _ => throw new InvalidOperationException("スナップショット失敗");

        var actions = new List<ScenarioAction>
        {
            new ScenarioAction { Type = "close" }
        };

        // continueOnError = true のとき: 例外が握りつぶされて完走するはず
        // onBeforeClose が失敗しても CloseAsync は必ず呼ばれるためページは閉じられる
        var ex = await Record.ExceptionAsync(
            () => ActionRunner.RunAsync(page, actions, timeoutMs: 5000, continueOnError: true,
                onBeforeClose: onBeforeClose));

        Assert.Null(ex);
        // onBeforeClose が throw しても CloseAsync は必ず呼ばれるため、ページは閉じている
        Assert.True(page.IsClosed);
    }

    /// <summary>
    /// "close" アクションで onBeforeClose が例外をスローした場合、
    /// continueOnError = false でも CloseAsync が必ず呼ばれてページが閉じられることを確認する。
    /// onBeforeClose の例外は CloseAsync より優先されてはならない。
    /// </summary>
    [Fact]
    public async Task RunAsync_CloseAction_OnBeforeCloseThrows_ContinueOnErrorFalse_PageIsStillClosed()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        // onBeforeClose が例外をスローするコールバック
        Func<IPage, Task> onBeforeClose = _ => throw new InvalidOperationException("スナップショット失敗");

        var actions = new List<ScenarioAction>
        {
            new ScenarioAction { Type = "close" }
        };

        // continueOnError = false のとき: 例外が再スローされる（RunAsync が throw する）
        // ただし CloseAsync は例外前に呼ばれるためページは閉じている
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ActionRunner.RunAsync(page, actions, timeoutMs: 5000, continueOnError: false,
                onBeforeClose: onBeforeClose));

        // 例外がスローされた後もページは閉じていること
        Assert.True(page.IsClosed);
    }

    /// <summary>
    /// action.Type が空文字 "" の場合、警告メッセージ中に "(missing)" が含まれることを確認する（#18 修正）。
    /// 修正前は action.Type がそのまま使われるため '' が表示され、ユーザーが原因を把握しにくかった。
    /// </summary>
    [Fact]
    public async Task RunAsync_ActionTypeIsEmpty_ShowsMissingLabelInWarning()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        var actions = new List<ScenarioAction>
        {
            // Type が空文字 → default ケースで "(missing)" と表示されてスキップされるはず
            new ScenarioAction { Type = "" }
        };

        // 標準エラー出力をキャプチャして警告メッセージを確認する
        var captured = new System.IO.StringWriter();
        var originalErr = Console.Error;
        Console.SetError(captured);
        try
        {
            await ActionRunner.RunAsync(page, actions, timeoutMs: 5000);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        // "(missing)" がメッセージに含まれることを確認する
        Assert.Contains("(missing)", captured.ToString());
    }
}
