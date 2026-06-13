// annotations コンテキストのみ有効化する:
// このファイルは ICDPSession? のような null 許容注釈を使用しており、
// #nullable disable のままだと CS8632 警告がビルドのたびに12件出ていた。
// 警告分析 (warnings) は従来どおり無効のため、既存コードの挙動・警告は変わらない。
#nullable enable annotations

using Microsoft.Playwright;

namespace JsCoverageReporter.Coverage;

/// <summary>
/// Playwright の CDP（Chrome DevTools Protocol）を使って JavaScript カバレッジを収集するクラス。
/// StartAsync でカバレッジ収集を開始し、StopAsync でデータを取得して返す。
/// 複数タブが開いた場合も Context.Page イベントで自動的に追跡する。
/// page.Request イベントでナビゲーション直前にスナップショットを撮り、
/// 各スクリプトに正確なページ URL を記録する。
/// </summary>
internal class CoverageCollector(IPage page) : IAsyncDisposable
{
    // 追跡する全ページのリスト（最初のページから順に追加される。追加順がタブ番号になる）
    private readonly List<IPage>                    _trackedPages   = [];
    // ページごとの CDP セッション（ページ → CDP セッションのマッピング）
    private readonly Dictionary<IPage, ICDPSession> _cdpSessions    = [];
    // 新しいタブの SetupPageAsync タスクを格納するリスト（StopAsync で待機する）
    private readonly List<Task>                     _pageSetupTasks = [];
    // _trackedPages / _cdpSessions 等へのアクセスを同期するためのロックオブジェクト
    private readonly object                         _lock           = new();
    // DisposeAsync が既に呼ばれたかどうかを示すフラグ（二重解放を防ぐ）
    private bool _disposed = false;
    // StopAsync が既に呼ばれたかどうかを示すフラグ（二重呼び出しによる CDP エラーを防ぐ）
    private bool _stopped  = false;
    // Context.Page イベントハンドラの参照（DisposeAsync / StopAsync で解除するために保持する）
    private EventHandler<IPage>? _pageEventHandler = null;

    // スクリプトフィルター（StartAsync で設定。中間スナップショット時にも使う）
    // volatile: バックグラウンドタスクが古いキャッシュを読まないようにメモリ可視性を保証する
    private volatile IReadOnlyList<string> _scriptFilters  = [];
    // スクリプト除外フィルター（StartAsync で設定。中間スナップショット時にも使う）
    private volatile IReadOnlyList<string> _scriptExcludes = [];

    // ページごとの全収集済みスクリプトカバレッジデータ（scriptId -> ScriptCoverage）
    // ナビゲーションキャンセルの際に生存しているスクリプトの実行数を更新するために利用する
    private readonly Dictionary<IPage, Dictionary<string, ScriptCoverage>> _scriptCache = [];
    // 中間スナップショットタスクのリスト（StopAsync で待機する）
    private readonly List<Task>                                 _snapTasks           = [];
    // ページごとのリクエストイベントハンドラー（DisposeAsync / StopAsync で解除する）
    private readonly Dictionary<IPage, EventHandler<IRequest>> _requestHandlers     = [];
    // ページごとのページ閉鎖イベントハンドラー（StopAsync 前にページが閉じられた場合でも収集できるようにする）
    private readonly Dictionary<IPage, EventHandler<IPage>>    _closeHandlers       = [];
    // ページごとの最終スナップショット時刻（スロットリング用。iframe ナビゲーションの過剰スナップショットを防ぐ）
    private readonly Dictionary<IPage, long>                   _lastSnapshotTick    = [];
    // ページごとのタブ番号（SetupPageAsync で登録順に付番。O(1) で取得するため Dictionary を使う）
    private readonly Dictionary<IPage, int>                    _tabIndices          = [];
    // ページごとのスナップショット直列化セマフォ（同一ページの CDP 操作の並行実行を防ぐ）。
    // reqHandler/closeHandler の中間スナップショットと BeforePageCloseAsync が同一 CDP セッションに
    // 対して Profiler.takePreciseCoverage / Debugger.getScriptSource を並行発行すると、
    // 同一 scriptId の二重処理（無駄な往復・キャッシュの TOCTOU）が起きうるため、ページ単位で直列化する。
    // ページが異なれば別 CDP セッションのため並行実行を許す（グローバルロックにしない）。
    private readonly Dictionary<IPage, SemaphoreSlim>          _pageSnapshotLocks   = [];
    // スナップショットのスロットリング間隔（ミリ秒）（この間隔内の連続スナップショットはスキップする）
    // テストからインスタンスごとに 0 に設定することでスロットリングを無効化できる（internal にしてテスト性を確保）
    // volatile: reqHandler の _lock 内で読まれるためメモリ可視性を保証する。
    //           C# では long に volatile を付けられないため int を使用（500ms は int で十分）
    internal volatile int SnapshotThrottleMs = 500;

    // window.close() オーバーライドが fetch を送る特殊 URL。
    // Playwright がこの URL をインターセプトしてスナップショットを取り、応答後に close を実行させる。
    // ブラウザからは到達できない偽ドメインを使って他の通信と混在しないようにする。
    private const string BeforeCloseRouteUrl = "https://jscoverage.internal/__before_close__";

    // window.close() をオーバーライドする JS スクリプト（SetupPageAsync で各ページに注入する）。
    // static readonly にすることで SetupPageAsync が呼ばれるたびに文字列を再生成しない。
    // BeforeCloseRouteUrl は const なので連結しても定数折り畳みと同等の効果がある。
    private static readonly string CloseOverrideScript =
        "(function() {" +
        "  if (window.__jsCoverageCloseOverridden) { return; }" +
        "  window.__jsCoverageCloseOverridden = true;" +
        "  var _orig = window.close.bind(window);" +
        "  window.close = async function() {" +
        "    try { await fetch('" + BeforeCloseRouteUrl + "', { method: 'POST', keepalive: true }); } catch (e) {}" +
        "    _orig();" +
        "  };" +
        "})();";

    /// <summary>
    /// カバレッジ収集を開始する。
    /// フィルターを保存し、初期ページのCDPセッションを開始し、新しいタブの自動検出を登録する。
    /// </summary>
    /// <param name="scriptFilters">URLにいずれかの文字列を含むスクリプトだけを返す（null または省略で全部）</param>
    /// <param name="scriptExcludes">URLにいずれかの文字列を含むスクリプトを除外する（null または省略で除外なし）</param>
    public async Task StartAsync(IReadOnlyList<string>? scriptFilters = null, IReadOnlyList<string>? scriptExcludes = null)
    {
        // null の場合はフィルターなし（全スクリプト対象）として扱う
        // フィルターをインスタンスフィールドに保存する（中間スナップショット時にも参照する）
        // 参照型の代入は .NET のメモリモデル上アトミックなため、バックグラウンドタスクとのロック不要
        // ToArray で防衛コピーする（呼び出し元が渡したリストを後から変更しても
        // バックグラウンドのスナップショットタスクが読むフィルターは変わらないようにする）
        if (scriptFilters == null) { _scriptFilters = []; } else { _scriptFilters = scriptFilters.ToArray(); }
        if (scriptExcludes == null) { _scriptExcludes = []; } else { _scriptExcludes = scriptExcludes.ToArray(); }

        // 既存のイベントハンドラがあれば先に解除する（StartAsync の二重呼び出し対策）
        if (_pageEventHandler != null)
        {
            page.Context.Page -= _pageEventHandler;
            _pageEventHandler = null;
        }

        // 新しいタブが開いたとき自動でカバレッジを開始するイベントハンドラを定義する
        _pageEventHandler = (_, newPage) =>
        {
            // StopAsync / DisposeAsync 完了後に in-flight のイベントが届いた場合は何もしない
            // （停止後に SetupPageAsync が走ると Profiler が有効なまま残り、
            //   ハンドラが解除パスの後に登録されて永久にリークするため）
            lock (_lock)
            {
                if (_stopped) { return; }
            }
            var task = SetupPageAsync(newPage);
            lock (_lock)
            {
                // タスク開始後に StopAsync が完了していた場合は追加しない。
                // 追加しても既にドレイン済みのループには届かず、
                // SetupPageAsync 先頭の _stopped チェックが早期リターンするため実害は
                // 最小だが、不要な登録によるリークを避ける。
                if (_stopped) { return; }
                _pageSetupTasks.Add(task);
            }
        };

        // 初期ページのセットアップより先にイベントを購読する。
        // 後から購読すると、初期ページのスクリプトがセットアップ中（CDP セッション作成等の await 中）に
        // window.open で開いたタブの Page イベントを取りこぼし、そのタブが追跡されない。
        // SetupPageAsync は追跡済みページをスキップするため、先に購読しても二重セットアップは起きない。
        page.Context.Page += _pageEventHandler;

        // 最初のページ（コンストラクタで渡されたページ）のカバレッジを開始する
        await SetupPageAsync(page);
    }

    /// <summary>
    /// 指定ページのCDPセッションを作成し、カバレッジ収集を開始する。
    /// また page.Request イベントを購読してナビゲーション直前のスナップショットを撮る準備をする。
    /// </summary>
    /// <param name="targetPage">カバレッジを開始するページ</param>
    private async Task SetupPageAsync(IPage targetPage)
    {
        // 追跡ページリストにこのページを追加する（追加順がタブ番号になる）
        lock (_lock)
        {
            // StopAsync / DisposeAsync 完了後に呼ばれた場合は何もしない
            // （停止後にセットアップすると Profiler が停止されないまま残り、
            //   登録したイベントハンドラも解除されずリークするため）
            if (_stopped) { return; }
            // 既に追跡中のページはスキップする（_scriptCache は _trackedPages と同時追加なので O(1) で判定できる）
            if (_scriptCache.ContainsKey(targetPage)) { return; }
            _trackedPages.Add(targetPage);
            // このページのスクリプトキャッシュを初期化する
            _scriptCache[targetPage] = new Dictionary<string, ScriptCoverage>();
            // タブ番号を Dictionary に記録する（_trackedPages.IndexOf より O(1) で取得できる）
            _tabIndices[targetPage] = _trackedPages.Count - 1;
            // このページのスナップショット直列化セマフォを初期化する（同一ページの CDP 操作の並行実行を防ぐ）
            _pageSnapshotLocks[targetPage] = new SemaphoreSlim(1, 1);
        }

        // ページに接続したCDPセッションを作成する
        var cdp = await targetPage.Context.NewCDPSessionAsync(targetPage);

        // await 中に StopAsync / DisposeAsync が完了していた場合、Profiler の開始を省略して返る。
        // _trackedPages / _scriptCache への追加は既に完了しているが、
        // Profiler/Debugger を有効化しないことでリソースリークを防ぐ。
        // イベントハンドラも登録しないため StopAsync 後のハンドラリークが起きない。
        bool stoppedAfterCdp;
        lock (_lock)
        {
            stoppedAfterCdp = _stopped;
            if (!_stopped)
            {
                _cdpSessions[targetPage] = cdp;
            }
        }
        if (stoppedAfterCdp)
        {
            try { await cdp.DisposeAsync(); } catch (Exception) { }
            return;
        }

        // V8 Profilerを有効にする
        await cdp.SendAsync("Profiler.enable");
        // Debuggerを有効にする（ソースコード取得に必要）
        await cdp.SendAsync("Debugger.enable");
        // 精密カバレッジの記録を開始する
        await cdp.SendAsync("Profiler.startPreciseCoverage", new Dictionary<string, object>
        {
            ["callCount"]            = true,
            ["detailed"]             = true,
            ["allowTriggeredUpdates"] = true,
        });

        // window.close() をオーバーライドするスクリプトを注入する。
        // ページが JS 側から window.close() で閉じられる直前に BeforeCloseRouteUrl へ fetch することで
        // Playwright がスナップショットを取ってから元の close を呼べるようにする。
        // （page.Close イベントは CDP セッション無効化後に発火するため、イベント内でのスナップショットは失敗する）

        // すでに読み込まれている現在のページに即座に注入する
        try
        {
            await targetPage.EvaluateAsync(CloseOverrideScript);
        }
        catch (Exception)
        {
            // ページが既に閉じているなどの場合は無視する
        }

        // 以降のナビゲーション後にも自動的に注入するよう登録する
        // 注: Playwright に RemoveInitScript per-script API がないため StopAsync/DisposeAsync では除去できない。
        // StopAsync 後に window.close() が呼ばれても fetch が失敗して catch(e){} に吸収されるため実害はない。
        await targetPage.AddInitScriptAsync(CloseOverrideScript);

        // BeforeCloseRouteUrl へのリクエストをインターセプトしてスナップショットを取るルートを登録する。
        // fetch が await されている間は window.close() の元の処理が止まっているため、
        // CDP セッションはまだ有効でありスナップショットを安全に取得できる。
        await targetPage.RouteAsync(BeforeCloseRouteUrl, async route =>
        {
            // _stopped チェックをアトミックに行う
            bool shouldProcess;
            lock (_lock)
            {
                if (_stopped)
                {
                    shouldProcess = false;
                }
                else
                {
                    shouldProcess = true;
                }
            }

            if (shouldProcess)
            {
                string closeRouteUrl;
                try { closeRouteUrl = targetPage.Url; }
                catch (Exception) { closeRouteUrl = ""; }

                // ルートを完了させる前にスナップショットを取る
                // （完了させると window.close() の元の処理が再開されページが閉じてしまう）
                await TakeIntermediateSnapshotAsync(targetPage, closeRouteUrl);
                // スナップショット完了後に tick をセットする。
                // 完了前にセットすると、スナップショット失敗時に closeHandler のスロットリング判定を
                // 誤って抑制し、フォールバックも失われてカバレッジが欠落する可能性がある。
                lock (_lock) { _lastSnapshotTick[targetPage] = Environment.TickCount64; }
            }

            // fetch を完了させて window.close() のオーバーライド関数が _orig() を呼べるようにする。
            // Access-Control-Allow-Origin を付けないと CORS エラーで fetch が reject されるため追加する。
            // reject されてもスナップショット取得済みのため実害はないが、余計な例外を発生させないようにする。
            try
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status  = 200,
                    Body    = "",
                    Headers = new Dictionary<string, string>
                    {
                        ["Access-Control-Allow-Origin"] = "*",
                    },
                });
            }
            catch (Exception)
            {
                // ページが既に閉じていた場合は無視する
            }
        });

        // ナビゲーションリクエストを検出してスナップショットを撮るハンドラーを定義する
        // （ページ遷移前の旧ページのスクリプトを正確なページ URL で記録するため）
        // メインフレームだけでなく iframe のナビゲーションも検出する
        // （iframe 内のリンクで画面遷移する場合のカバレッジ漏れを防ぐ）
        EventHandler<IRequest> reqHandler = (_, req) =>
        {
            // ナビゲーションリクエスト以外は無視する
            if (!req.IsNavigationRequest) { return; }

            // スロットリング: 前回のスナップショットから 500ms 以内なら無視する
            // _stopped チェックをスロットリングロックに統合して最初の確認を アトミックに行う
            // （別ロックにすると _stopped チェック後に StopAsync が完了してしまうレース窓が残る）
            // Environment.TickCount64 は単調増加でシステム時計変更の影響を受けない
            lock (_lock)
            {
                if (_stopped) { return; } // StopAsync/DisposeAsync 完了後の呼び出しをスキップする
                long now = Environment.TickCount64;
                if (_lastSnapshotTick.TryGetValue(targetPage, out long lastTick))
                {
                    if (now - lastTick < SnapshotThrottleMs)
                    {
                        return; // スロットリングによりスキップする
                    }
                }
                _lastSnapshotTick[targetPage] = now;
            }

            // ナビゲーション開始直前の現在 URL を取得する（遷移後は変わるため）
            // ページが閉じられた直後に reqHandler が発火した場合 Url アクセスが例外を投げる可能性があるため
            // try-catch で保護し、取得できない場合は空文字列にして処理を継続する
            string currentUrl;
            try { currentUrl = targetPage.Url; }
            catch (Exception) { currentUrl = ""; }

            // スロットリングロック解放後に StopAsync が完了していた場合はタスク開始自体をスキップする
            lock (_lock) { if (_stopped) { return; } }

            // 中間スナップショットタスクを開始してから、_stopped チェックと _snapTasks.Add を
            // 同一ロックでアトミックに行う（チェックと追加の間に StopAsync が完了するレースを排除する）
            // TakeIntermediateSnapshotAsync は _lock を内部で取得するためロック外で呼ぶ必要がある
            var snapTask = TakeIntermediateSnapshotAsync(targetPage, currentUrl);
            lock (_lock)
            {
                // タスク開始後に _stopped になった場合は追加しない（孤立タスクは CDP try-catch で保護済み）
                if (_stopped) { return; }
                _snapTasks.Add(snapTask);
            }
        };

        targetPage.Request += reqHandler;
        lock (_lock)
        {
            _requestHandlers[targetPage] = reqHandler;
        }

        // ページが閉じられたときにスナップショットを撮るハンドラーを定義する。
        // window.close() ルート経由でスナップショット済みの場合はスロットリングによりスキップする。
        // （page.Close イベントは CDP セッション無効化後に発火するため、スナップショットは通常失敗する。
        //   ルート未経由の予期しない close（ブラウザ強制終了など）のフォールバックとして残している）
        EventHandler<IPage> closeHandler = (_, _) =>
        {
            // スロットリング: ルート経由でスナップショットが取られた直後なら重複試行を避ける
            // （CDP は既に無効なため試みても失敗し、不要な警告が出るだけになる）
            lock (_lock)
            {
                if (_stopped) { return; }
                long now = Environment.TickCount64;
                if (_lastSnapshotTick.TryGetValue(targetPage, out long lastTick))
                {
                    if (now - lastTick < SnapshotThrottleMs) { return; }
                }
                _lastSnapshotTick[targetPage] = now;
            }

            string currentUrl;
            try { currentUrl = targetPage.Url; }
            catch (Exception) { currentUrl = ""; }

            var snapTask = TakeIntermediateSnapshotAsync(targetPage, currentUrl);
            lock (_lock)
            {
                // タスク開始後に _stopped になった場合は追加しない（孤立タスクは CDP try-catch で保護済み）
                // reqHandler と同じパターン — _stopped 後の CDP エラーは内部で警告ログに変換される
                if (_stopped) { return; }
                _snapTasks.Add(snapTask);
            }
        };
        targetPage.Close += closeHandler;
        lock (_lock)
        {
            _closeHandlers[targetPage] = closeHandler;
        }
    }

    /// <summary>
    /// ナビゲーション直前に CDP スナップショットを取り、未収集スクリプトを現在の URL で記録する。
    /// Profiler は停止しない（収集は継続する）。
    /// </summary>
    /// <param name="targetPage">スナップショット対象のページ</param>
    /// <param name="pageUrl">スナップショット時点のページ URL（ナビゲーション前の URL）</param>
    private async Task TakeIntermediateSnapshotAsync(IPage targetPage, string pageUrl)
    {
        // このページの CDP セッション・タブ番号・直列化セマフォを取得する
        ICDPSession? cdp;
        int tabIndex;
        SemaphoreSlim? snapshotLock;
        lock (_lock)
        {
            _cdpSessions.TryGetValue(targetPage, out cdp);
            // _tabIndices は SetupPageAsync で O(1) で登録済み（_trackedPages.IndexOf より高速）
            if (!_tabIndices.TryGetValue(targetPage, out tabIndex)) { tabIndex = -1; }
            _pageSnapshotLocks.TryGetValue(targetPage, out snapshotLock);
        }
        if (cdp == null) { return; }

        // 同一ページの CDP 操作を直列化する（reqHandler/closeHandler/BeforePageCloseAsync の
        // スナップショットが同一セッションへ並行発行されるのを防ぐ）。
        // snapshotLock は cdp が非 null なら SetupPageAsync で必ず初期化済みだが、防御的に null も許容する。
        if (snapshotLock != null) { await snapshotLock.WaitAsync(); }
        try
        {
            // スナップショットを取得する（Profiler は停止しない）
            // takePreciseCoverage はカウントをリセットしないため、複数回呼んでも実行数は累積される。
            // StopAsync の最終収集（FinalCollectFromPageAsync）でも同じ API を呼ぶが、
            // キャッシュ更新時に最新の functions で上書きするため問題ない。
            // 中間スナップショットの目的は「ナビゲーション前に確定したスクリプトを正確な URL で記録する」ことであり、
            // 実行数の精度より URL の正確性を優先している。
            System.Text.Json.JsonElement? result = null;
            try
            {
                result = await cdp.SendAsync("Profiler.takePreciseCoverage");
            }
            catch (Exception ex)
            {
                // ナビゲーション中の CDP エラーは警告として記録してスキップする
                Console.Error.WriteLine($"[Warning] Intermediate snapshot failed for tab {tabIndex}: {ex.Message}");
                return;
            }

            if (result == null) { return; }

            // この時点でのページ情報（ナビゲーション前の URL）でスクリプトを記録する
            var pageInfo = new PageInfo(tabIndex, pageUrl);

            // このページのキャッシュマップを取得する
            Dictionary<string, ScriptCoverage>? scriptCache;
            lock (_lock)
            {
                _scriptCache.TryGetValue(targetPage, out scriptCache);
            }
            if (scriptCache == null) { return; }

            await ProcessNewScriptsAsync(result.Value, cdp, pageInfo, scriptCache);
        }
        finally
        {
            if (snapshotLock != null) { snapshotLock.Release(); }
        }
    }

    /// <summary>
    /// CDP スナップショット結果からスクリプトカバレッジデータを処理する。
    /// キャッシュ（scriptCache）に存在しない新規スクリプトは Debugger.getScriptSource でソースを取得し、
    /// 既存のものはソースコードを再利用しつつ最終実行数（functions）のみ最新状態に更新（上書き）する。
    /// </summary>
    private async Task ProcessNewScriptsAsync(
        System.Text.Json.JsonElement root,
        ICDPSession cdp,
        PageInfo pageInfo,
        Dictionary<string, ScriptCoverage> scriptCache)
    {
        // CDP レスポンスの "result" 配列を取り出す
        if (!root.TryGetProperty("result", out var resultArray))
        {
            return;
        }

        // volatile フィールドをローカルに1回だけ読む。
        // ループ中に StartAsync が再呼び出しされても、このスナップショット処理内では
        // 同一のフィルター・除外リストで一貫して判定する。
        IReadOnlyList<string> scriptFilters  = _scriptFilters;
        IReadOnlyList<string> scriptExcludes = _scriptExcludes;

        foreach (var entry in resultArray.EnumerateArray())
        {
            // スクリプトの URL と ID を取得する
            string url;
            if (entry.TryGetProperty("url", out var urlProp))
            {
                string? urlTmp = urlProp.GetString();
                if (urlTmp == null) { url = ""; } else { url = urlTmp; }
            }
            else
            {
                url = "";
            }
            string scriptId;
            if (entry.TryGetProperty("scriptId", out var sidProp))
            {
                string? sidTmp = sidProp.GetString();
                if (sidTmp == null) { scriptId = ""; } else { scriptId = sidTmp; }
            }
            else
            {
                scriptId = "";
            }

            // URL が空のスクリプト（内部スクリプトなど）はスキップする
            if (string.IsNullOrEmpty(url)) { continue; }
            // scriptId が空の場合は Debugger.getScriptSource を呼べないためスキップする
            if (string.IsNullOrEmpty(scriptId)) { continue; }

            // scriptFilters に一致するスクリプトのみ処理する
            if (scriptFilters.Count > 0 && !scriptFilters.Any(f => url.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // scriptExcludes に一致するスクリプトは除外する
            if (scriptExcludes.Any(e => url.Contains(e, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // キャッシュから既存のスクリプトデータを取得する
            ScriptCoverage? existingCoverage = null;
            lock (_lock)
            {
                scriptCache.TryGetValue(scriptId, out existingCoverage);
            }

            string source = "";
            if (existingCoverage != null && !string.IsNullOrEmpty(existingCoverage.Source))
            {
                // 既にソースを取得済みのスクリプトならソースコードを再利用する
                // （キャッシュのソースが空の場合は再取得を試みる）
                source = existingCoverage.Source;
            }
            else
            {
                // Debugger.getScriptSource でスクリプトのソースコードを新規取得する
                try
                {
                    var srcResult = await cdp.SendAsync("Debugger.getScriptSource", new Dictionary<string, object>
                    {
                        ["scriptId"] = scriptId,
                    });
                    if (srcResult.HasValue && srcResult.Value.TryGetProperty("scriptSource", out var srcProp))
                    {
                        string? srcTmp = srcProp.GetString();
                        if (srcTmp == null) { source = ""; } else { source = srcTmp; }
                    }
                }
                catch (Exception)
                {
                    Console.Error.WriteLine($"[Warning] Could not retrieve source for '{url}' — skipping.");
                    continue;
                }
            }

            if (string.IsNullOrEmpty(source)) { continue; }

            // 関数カバレッジデータを組み立てる
            var functions = new List<FunctionCoverage>();
            if (entry.TryGetProperty("functions", out var funcsArray))
            {
                foreach (var func in funcsArray.EnumerateArray())
                {
                    string funcName;
                    if (func.TryGetProperty("functionName", out var fnProp))
                    {
                        string? fnTmp = fnProp.GetString();
                        if (fnTmp == null) { funcName = ""; } else { funcName = fnTmp; }
                    }
                    else
                    {
                        funcName = "";
                    }
                    var ranges = new List<CoverageRange>();
                    if (func.TryGetProperty("ranges", out var rangesArray))
                    {
                        foreach (var r in rangesArray.EnumerateArray())
                        {
                            // フォールバック値の設計:
                            //   プロパティ不在 → 0（デフォルト値として安全。BuildCoverageMap でクランプされる）
                            //   start/end のパース失敗(文字列等) → 範囲を空(start==end==0)にして無効化する。
                            //     旧実装は int.MaxValue をフォールバックしていたが、(a) endOffset だけ失敗した場合
                            //     Math.Min(end, source.Length) によってソース末尾までの範囲に化け「無効化」にならず、
                            //     (b) 片方を int.MaxValue にすると size = EndOffset - StartOffset の計算が
                            //     オーバーフローしてソート比較が不安定になる恐れがあった。両方 0 にそろえれば
                            //     size 0 の空範囲となり、書き込みもオーバーフローも起きず安全。
                            //   count のパース失敗 → int.MaxValue（V8 のホットな関数は呼び出し回数が int の範囲
                            //     (約21億) を超えることがあり TryGetInt32 が失敗する。0 にすると「未実行」と
                            //     誤判定されるため、実行済み (count > 0) を保ちつつ最大回数として扱う。
                            //     この int.MaxValue は実行回数ツールチップで「21億回以上」と表示される（Report 側で対応）。
                            int start = 0;
                            int end   = 0;
                            bool offsetsValid = true;
                            if (r.TryGetProperty("startOffset", out var sProp)) { if (!sProp.TryGetInt32(out start)) { offsetsValid = false; } }
                            if (r.TryGetProperty("endOffset",   out var eProp)) { if (!eProp.TryGetInt32(out end))   { offsetsValid = false; } }
                            // オフセットがパースできない異常データは範囲を空にして無効化する
                            if (!offsetsValid) { start = 0; end = 0; }
                            int count;
                            if (r.TryGetProperty("count",       out var cProp)) { if (!cProp.TryGetInt32(out count)) { count = int.MaxValue; } } else { count = 0; }
                            ranges.Add(new CoverageRange(start, end, count));
                        }
                    }
                    functions.Add(new FunctionCoverage(funcName, ranges));
                }
            }

            // キャッシュに保存（最新の実行数で上書き更新する。URLのPageInfoは既存のものを優先）
            PageInfo finalPageInfo;
            if (existingCoverage != null)
            {
                finalPageInfo = existingCoverage.Page;
            }
            else
            {
                finalPageInfo = pageInfo;
            }
            var newCoverage = new ScriptCoverage(finalPageInfo, url, source, functions);

            lock (_lock)
            {
                // 並行処理で別のスナップショットが先にキャッシュした場合はそのページ情報を優先する
                // （ロック外での読み取りと書き込みの間に別の処理が書き込んだ場合の TOCTOU 対策）
                ScriptCoverage? currentInCache;
                if (scriptCache.TryGetValue(scriptId, out currentInCache) && currentInCache != null)
                {
                    finalPageInfo = currentInCache.Page;
                }
                scriptCache[scriptId] = new ScriptCoverage(finalPageInfo, url, source, functions);
            }
        }
    }

    /// <summary>
    /// ページが閉じられる直前に呼ぶ。スナップショットを取得してから close イベントハンドラを解除する。
    /// ActionRunner の "close" アクションから呼ばれることを想定している。
    /// （page.Close イベントは CDP セッション無効化後に発火するため、イベント内でのスナップショットは失敗する。
    ///   そのためアクション実行中に明示的に呼ぶ必要がある）
    /// </summary>
    /// <param name="targetPage">閉じられようとしているページ</param>
    public async Task BeforePageCloseAsync(IPage targetPage)
    {
        // StopAsync 完了後に呼ばれた場合は解放済み CDP セッションへの無用な試行を避けて早期リターンする
        lock (_lock)
        {
            if (_stopped) { return; }
        }

        // close イベントハンドラを解除する（ページ閉鎖後に二重スナップショットを試みてエラーになるのを防ぐ）
        EventHandler<IPage>? closeHandler;
        lock (_lock)
        {
            _closeHandlers.TryGetValue(targetPage, out closeHandler);
            _closeHandlers.Remove(targetPage);
        }
        if (closeHandler != null)
        {
            targetPage.Close -= closeHandler;
        }

        // CDP セッションがまだ有効なうちにスナップショットを取得する
        string currentUrl;
        try { currentUrl = targetPage.Url; }
        catch (Exception) { currentUrl = ""; }

        // スナップショットタスクを _snapTasks に登録してから await する。
        // StopAsync は _snapTasks をドレインしてから _scriptCache を読み出すため、
        // 登録することで並走する StopAsync がドレイン中ならスナップショット完了を確実に待てる。
        // ドレイン済みの場合でも常に await することで _scriptCache への書き込みが
        // allScripts 組み立て前に完了することを保証する。
        // （_stopped が true でもタスクは既に開始済みのため await が必要。
        //   CDP セッションが解放済みの場合は TakeIntermediateSnapshotAsync 内の
        //   try-catch で吸収される）
        var snapTask = TakeIntermediateSnapshotAsync(targetPage, currentUrl);
        lock (_lock)
        {
            // _stopped が false なら StopAsync のドレインループがこのタスクを拾う。
            // _stopped が true（StopAsync が既にドレイン済み）なら登録不要だが、
            // 下の await で書き込みを保証するため追加しない。
            if (!_stopped)
            {
                _snapTasks.Add(snapTask);
            }
        }
        await snapTask;
    }

    /// <summary>
    /// カバレッジ収集を停止してデータを返す。
    /// 中間スナップショットのデータと最終収集のデータを合わせて返す。
    /// </summary>
    /// <returns>収集したスクリプトカバレッジデータのリスト（全タブ・全ナビゲーション分）</returns>
    public async Task<IReadOnlyList<ScriptCoverage>> StopAsync()
    {
        // 二重呼び出しを防ぐ（lock 内でアトミックにチェック＆セットする）
        lock (_lock)
        {
            if (_stopped) { return []; }
            _stopped = true;
        }

        // Context.Page イベントハンドラを解除する（新しいタブの SetupPageAsync が始まらないようにする）
        if (_pageEventHandler != null)
        {
            page.Context.Page -= _pageEventHandler;
            _pageEventHandler  = null;
        }

        // 新しいタブのセットアップタスクが完了するまで先に待機する
        // （リクエストハンドラを解除する前に待機しないと、SetupPageAsync が途中でハンドラを登録した場合に
        //   そのハンドラが _requestHandlers.Clear() 後に追加されて永久に解除されないリークが起きる）
        // リスト件数が増えなくなるまでループして、インフライトなタスクを漏れなく回収する
        int prevSetupCount = 0;
        while (true)
        {
            List<Task> setupTasks;
            lock (_lock) { setupTasks = new List<Task>(_pageSetupTasks); }
            // SetupPageAsync の失敗（CDPセッション作成エラー等）を警告ログに変換する。
            // 1つのタブのセットアップが失敗しても、他タブの収集データを失わないようにする。
            try { await Task.WhenAll(setupTasks); }
            catch (Exception ex) { Console.Error.WriteLine($"[Warning] StopAsync: page setup task failed: {ex.Message}"); }
            int newCount;
            lock (_lock) { newCount = _pageSetupTasks.Count; }
            if (newCount == prevSetupCount) { break; }
            prevSetupCount = newCount;
        }
        // 完了したセットアップタスクを解放してメモリを返す（長時間実行時のリスト肥大化を防ぐ）
        lock (_lock) { _pageSetupTasks.Clear(); }

        // リクエストイベントハンドラをすべて解除する（新たな中間スナップショットを防ぐ）
        // setupTasks 完了後に実施するため、SetupPageAsync が登録したハンドラも漏れなく解除できる
        List<(IPage p, EventHandler<IRequest> handler)> reqPairs = [];
        lock (_lock)
        {
            foreach (var kv in _requestHandlers)
            {
                reqPairs.Add((kv.Key, kv.Value));
            }
            _requestHandlers.Clear();
        }
        foreach (var (p, handler) in reqPairs)
        {
            p.Request -= handler;
        }

        // ページ閉鎖イベントハンドラをすべて解除する（StopAsync 後に Close イベントが発火しないようにする）
        List<(IPage p, EventHandler<IPage> handler)> closePairs = [];
        lock (_lock)
        {
            foreach (var kv in _closeHandlers)
            {
                closePairs.Add((kv.Key, kv.Value));
            }
            _closeHandlers.Clear();
        }
        foreach (var (p, handler) in closePairs)
        {
            p.Close -= handler;
        }

        // window.close() 用ルートハンドラを解除する（StopAsync 後の fetch インターセプトを防ぐ）
        List<IPage> routePages;
        lock (_lock) { routePages = new List<IPage>(_trackedPages); }
        foreach (var routePage in routePages)
        {
            if (!routePage.IsClosed)
            {
                try { await routePage.UnrouteAsync(BeforeCloseRouteUrl); }
                catch (Exception ex) { Console.Error.WriteLine($"[Warning] StopAsync: UnrouteAsync failed: {ex.Message}"); }
            }
        }

        // 追跡ページがない場合は空リストを返す（setupTasks 待機後は trackedPages が安定している）
        int trackedCount;
        lock (_lock)
        {
            trackedCount = _trackedPages.Count;
        }
        if (trackedCount == 0) { return []; }

        // 中間スナップショットタスクが完了するまで待機する
        // ハンドラー解除直前に発火した reqHandler が _snapTasks.Add を呼ぶまでのタイミング差を吸収するため、
        // setupTasks と同様にタスク数が安定するまでループして漏れなく回収する
        int prevSnapCount = 0;
        while (true)
        {
            List<Task> snapTasksCopy;
            lock (_lock) { snapTasksCopy = new List<Task>(_snapTasks); }
            // TakeIntermediateSnapshotAsync の失敗を警告ログに変換する（setupTasks ループと対称的に try-catch する）。
            // 内部ではすべての CDP 操作が try-catch 済みだが、将来の変更で例外が伝播した場合でも
            // FinalCollectFromPageAsync が実行されなくなるリスクを防ぐ。
            try { await Task.WhenAll(snapTasksCopy); }
            catch (Exception ex) { Console.Error.WriteLine($"[Warning] StopAsync: snap task failed: {ex.Message}"); }
            int newSnapCount;
            lock (_lock) { newSnapCount = _snapTasks.Count; }
            if (newSnapCount == prevSnapCount) { break; }
            prevSnapCount = newSnapCount;
        }
        // 完了したタスクを解放してメモリを返す（長時間実行時のリスト肥大化を防ぐ）
        lock (_lock) { _snapTasks.Clear(); }

        // 全ページの最終カバレッジを収集する（全スクリプトの最終状態をマージする）
        List<IPage> pageSnapshot;
        lock (_lock)
        {
            pageSnapshot = new List<IPage>(_trackedPages);
        }

        for (int i = 0; i < pageSnapshot.Count; i++)
        {
            var targetPage = pageSnapshot[i];

            // ページが既に閉じられている場合、CDP セッションは無効なので最終収集をスキップする。
            // BeforePageCloseAsync でスナップショット済みのため、キャッシュ内のデータを使う。
            if (targetPage.IsClosed)
            {
                continue;
            }

            // StopAsync 時点の URL を最終ページ URL として使用する
            // ページが既に閉じられている場合 page.Url が例外を投げることがあるため try-catch で保護する
            string pageUrl;
            try { pageUrl = targetPage.Url; }
            catch (Exception) { pageUrl = ""; }
            // _tabIndices から O(1) でタブ番号を取得する（フォールバックとしてループ添字 i を使う）
            int tabIdx;
            lock (_lock) { if (!_tabIndices.TryGetValue(targetPage, out tabIdx)) { tabIdx = i; } }
            var pageInfo = new PageInfo(tabIdx, pageUrl);

            ICDPSession? cdp;
            Dictionary<string, ScriptCoverage>? scriptCache;
            lock (_lock)
            {
                _cdpSessions.TryGetValue(targetPage, out cdp);
                _scriptCache.TryGetValue(targetPage, out scriptCache);
            }

            // cdp == null は「SetupPageAsync が NewCDPSessionAsync の await 中に StopAsync に追い越され、
            // Profiler を開始せずに返った」正常系のレースで起こりうる。この場合 Profiler/Debugger は
            // 有効化されておらず収集すべきデータもないため、警告を出さず静かにスキップする。
            if (cdp == null)
            {
                continue;
            }
            // scriptCache == null は _scriptCache への追加（SetupPageAsync 冒頭）が漏れた異常系のため警告を残す。
            if (scriptCache == null)
            {
                Console.Error.WriteLine($"[Warning] No script cache for tab {i} — skipping.");
                continue;
            }

            // 最終収集（Profiler と Debugger を停止しながら残りのスクリプトを収集する）
            await FinalCollectFromPageAsync(cdp, pageInfo, scriptCache);
        }

        // キャッシュに蓄積されたすべてのスクリプトデータをフラットなリストにする
        var allScripts = new List<ScriptCoverage>();
        lock (_lock)
        {
            foreach (var cache in _scriptCache.Values)
            {
                allScripts.AddRange(cache.Values);
            }
            // CDP セッションは DisposeAsync で解放するため、ここでは参照をクリアしない。
            // 以前は _cdpSessions.Clear() していたが、そうすると DisposeAsync 時に辞書が空になり
            // 各セッションの DisposeAsync() が一度も呼ばれずに滞留していた（リーク）。
            // StopAsync 後は新たなスナップショットが走らないため、参照を保持し続けても安全で、
            // DisposeAsync が確実にセッションを解放できる（Profiler/Debugger は FinalCollect で停止済み）。
            // StopAsync 後は不要になるページ単位の補助マップも解放する（長時間プロセスでの滞留を防ぐ）
            // _scriptCache は戻り値生成に使い終えているが、DisposeAsync 等の再参照に備えて保持する
            _tabIndices.Clear();
            _lastSnapshotTick.Clear();
        }

        return allScripts;
    }

    /// <summary>
    /// 1ページ分の最終カバレッジ収集。Profiler と Debugger を停止しながら最終スクリプト実行状況を取得する。
    /// </summary>
    private async Task FinalCollectFromPageAsync(
        ICDPSession cdp,
        PageInfo pageInfo,
        Dictionary<string, ScriptCoverage> scriptCache)
    {
        // スナップショットを取得する
        // ページが閉じられた場合など CDP エラーは警告として記録し result を null のままにする
        System.Text.Json.JsonElement? result = null;
        try
        {
            result = await cdp.SendAsync("Profiler.takePreciseCoverage");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Warning] Profiler.takePreciseCoverage failed: {ex.Message}");
        }
        finally
        {
            // 成功・失敗どちらでも必ず Profiler を停止・無効化する
            try { await cdp.SendAsync("Profiler.stopPreciseCoverage"); }
            catch (Exception ex) { Console.Error.WriteLine($"[Warning] Profiler.stopPreciseCoverage failed: {ex.Message}"); }
            try { await cdp.SendAsync("Profiler.disable"); }
            catch (Exception ex) { Console.Error.WriteLine($"[Warning] Profiler.disable failed: {ex.Message}"); }
        }

        if (result is null) { return; }

        // スクリプトカバレッジデータを処理する（Debugger は ProcessNewScriptsAsync の後に無効化する）
        try
        {
            await ProcessNewScriptsAsync(result.Value, cdp, pageInfo, scriptCache);
        }
        finally
        {
            // 成功・失敗どちらでも必ず Debugger を無効化する
            try { await cdp.SendAsync("Debugger.disable"); }
            catch (Exception ex) { Console.Error.WriteLine($"[Warning] Debugger.disable failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// 保持している全リソース（CDP セッション・イベントハンドラ）を解放する。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // 二重呼び出しを防ぐ（lock 内でアトミックにチェック＆セットする）
        // StopAsync を経由したかどうかを needsCleanup に記録してから _stopped を立てる
        bool needsCleanup;
        lock (_lock)
        {
            if (_disposed) { return; }
            _disposed = true;
            // StopAsync 未呼び出しなら Profiler/Debugger の停止が必要
            needsCleanup = !_stopped;
            // _stopped = true を設定して未完了の snapTask が解放済み CDP セッションを使わないようにする
            // StopAsync を経由せずに Dispose された場合のレース条件を防ぐ
            _stopped = true;
        }

        // Context.Page イベントハンドラを解除する
        if (_pageEventHandler != null)
        {
            page.Context.Page -= _pageEventHandler;
            _pageEventHandler  = null;
        }

        // SetupPageAsync タスクが完了するまで待機する（StopAsync と同様にループで安定するまで待つ）
        // リクエストハンドラを解除する前に待機しないと、SetupPageAsync が途中でハンドラを登録した場合に
        // そのハンドラが _requestHandlers.Clear() 後に追加されて永久に解除されないリークが起きる
        int prevDisposeSetupCount = 0;
        while (true)
        {
            List<Task> pendingSetup;
            lock (_lock) { pendingSetup = new List<Task>(_pageSetupTasks); }
            // SetupPageAsync の失敗を警告ログに変換する（StopAsync と同じ理由）
            try { await Task.WhenAll(pendingSetup); }
            catch (Exception ex) { Console.Error.WriteLine($"[Warning] DisposeAsync: page setup task failed: {ex.Message}"); }
            int newDisposeSetupCount;
            lock (_lock) { newDisposeSetupCount = _pageSetupTasks.Count; }
            if (newDisposeSetupCount == prevDisposeSetupCount) { break; }
            prevDisposeSetupCount = newDisposeSetupCount;
        }
        lock (_lock) { _pageSetupTasks.Clear(); }

        // StopAsync を経由せずに Dispose された場合もリクエストハンドラーを解除する
        List<(IPage p, EventHandler<IRequest> handler)> reqPairs = [];
        lock (_lock)
        {
            foreach (var kv in _requestHandlers)
            {
                reqPairs.Add((kv.Key, kv.Value));
            }
            _requestHandlers.Clear();
        }
        foreach (var (p, handler) in reqPairs)
        {
            p.Request -= handler;
        }

        // StopAsync を経由せずに Dispose された場合もページ閉鎖ハンドラーを解除する
        List<(IPage p, EventHandler<IPage> handler)> closePairs = [];
        lock (_lock)
        {
            foreach (var kv in _closeHandlers)
            {
                closePairs.Add((kv.Key, kv.Value));
            }
            _closeHandlers.Clear();
        }
        foreach (var (p, handler) in closePairs)
        {
            p.Close -= handler;
        }

        // window.close() 用ルートハンドラを解除する（DisposeAsync 後の fetch インターセプトを防ぐ）
        List<IPage> disposeRoutePages;
        lock (_lock) { disposeRoutePages = new List<IPage>(_trackedPages); }
        foreach (var routePage in disposeRoutePages)
        {
            if (!routePage.IsClosed)
            {
                try { await routePage.UnrouteAsync(BeforeCloseRouteUrl); }
                catch (Exception ex) { Console.Error.WriteLine($"[Warning] DisposeAsync: UnrouteAsync failed: {ex.Message}"); }
            }
        }

        // インフライトのスナップショットタスクが完了するまでループ待機してから CDP セッションを解放する
        // （解放済みセッションをスナップショットタスクが使用しないようにするための保護）
        // リクエストハンドラ解除直前に発火した reqHandler が _snapTasks.Add を呼ぶまでの
        // タイミング差を吸収するため、StopAsync と同様にタスク数が安定するまでループする
        int prevDisposeSnapCount = 0;
        while (true)
        {
            List<Task> pendingSnaps;
            lock (_lock) { pendingSnaps = new List<Task>(_snapTasks); }
            // TakeIntermediateSnapshotAsync の失敗を警告ログに変換する（StopAsync と同じ理由）
            try { await Task.WhenAll(pendingSnaps); }
            catch (Exception ex) { Console.Error.WriteLine($"[Warning] DisposeAsync: snap task failed: {ex.Message}"); }
            int newDisposeSnapCount;
            lock (_lock) { newDisposeSnapCount = _snapTasks.Count; }
            if (newDisposeSnapCount == prevDisposeSnapCount) { break; }
            prevDisposeSnapCount = newDisposeSnapCount;
        }
        // 完了したタスクを解放してメモリを返す（StopAsync と対称的に Clear() する）
        lock (_lock) { _snapTasks.Clear(); }

        // すべての CDP セッションを解放する
        // StopAsync を経由せず Dispose された場合は Profiler / Debugger を明示的に停止する
        List<(IPage p, ICDPSession cdp)> sessionPairs;
        lock (_lock)
        {
            sessionPairs = [];
            foreach (var kv in _cdpSessions)
            {
                sessionPairs.Add((kv.Key, kv.Value));
            }
        }
        foreach (var (targetPage, cdp) in sessionPairs)
        {
            // ページが既に閉じられている場合は CDP セッションも無効なので解放をスキップする
            if (targetPage.IsClosed)
            {
                continue;
            }
            // StopAsync を経ていない場合は Profiler / Debugger が enable のまま残っている
            // 安全に停止を試みる（失敗しても Dispose は続行する）
            if (needsCleanup)
            {
                try { await cdp.SendAsync("Profiler.stopPreciseCoverage"); } catch (Exception ex) { Console.Error.WriteLine($"[Warning] DisposeAsync: Profiler.stopPreciseCoverage failed: {ex.Message}"); }
                try { await cdp.SendAsync("Profiler.disable"); } catch (Exception ex) { Console.Error.WriteLine($"[Warning] DisposeAsync: Profiler.disable failed: {ex.Message}"); }
                try { await cdp.SendAsync("Debugger.disable"); } catch (Exception ex) { Console.Error.WriteLine($"[Warning] DisposeAsync: Debugger.disable failed: {ex.Message}"); }
            }
            try { await cdp.DisposeAsync(); }
            catch (Exception ex) { Console.Error.WriteLine($"[Warning] DisposeAsync: CDPSession.DisposeAsync failed: {ex.Message}"); }
        }

        // スナップショット直列化セマフォを解放する。
        // ここに到達する時点で全 snapTask は上のループで待機済みのため、セマフォを保持しているタスクはない。
        List<SemaphoreSlim> snapshotLocks;
        lock (_lock)
        {
            snapshotLocks = new List<SemaphoreSlim>(_pageSnapshotLocks.Values);
            _pageSnapshotLocks.Clear();
        }
        foreach (var snapshotLock in snapshotLocks)
        {
            snapshotLock.Dispose();
        }
    }
}

/// <summary>
/// スクリプトの収集元ページ（タブ）の情報を表すレコード。
/// Index はタブが開いた順番（0始まり）、Url は StopAsync 直前に取得した page.Url。
/// </summary>
internal record PageInfo(
    int    Index,  // タブ番号（0始まり。最初のページが 0、次に開いたタブが 1 ...）
    string Url     // StopAsync 直前に取得した page.Url
);

/// <summary>
/// スクリプト1つ分のカバレッジデータ全体をまとめるレコード型。
/// Playwright CDP から取得したデータをそのまま保持する。
/// </summary>
/// <param name="Page">スクリプトの収集元ページ（タブ）情報</param>
/// <param name="Url">スクリプトのURL（ファイルパスや https:// で始まるアドレス）</param>
/// <param name="Source">スクリプトのソースコード全文</param>
/// <param name="Functions">関数ごとのカバレッジ情報のリスト</param>
internal record ScriptCoverage(
    PageInfo                        Page,      // 収集元ページ（タブ）情報
    string                          Url,       // スクリプトのURL（ファイルパスや https:// で始まるアドレス）
    string                          Source,    // スクリプトのソースコード全文
    IReadOnlyList<FunctionCoverage> Functions  // 関数ごとのカバレッジ情報のリスト
);

/// <summary>
/// 関数1つ分のカバレッジデータを保持するレコード型。
/// 関数名と、その関数内に含まれるカバレッジ範囲のリストを持つ。
/// </summary>
/// <param name="FunctionName">関数名（無名関数は空文字になることがある）</param>
/// <param name="Ranges">この関数内のカバレッジ範囲のリスト</param>
internal record FunctionCoverage(
    string FunctionName,                   // 関数名（無名関数は空文字になることがある）
    IReadOnlyList<CoverageRange> Ranges    // この関数内のカバレッジ範囲のリスト
);

/// <summary>
/// ソースコード内のある範囲が何回実行されたかを表すレコード型。
/// StartOffset と EndOffset はソースコード先頭からの文字数（バイト数ではなく文字数）。
/// </summary>
/// <param name="StartOffset">範囲の開始位置（ソースコード先頭からの文字数）</param>
/// <param name="EndOffset">範囲の終了位置（ソースコード先頭からの文字数）</param>
/// <param name="Count">実行された回数（0 = 未実行）</param>
internal record CoverageRange(
    int StartOffset,  // 範囲の開始位置（ソースコード先頭からの文字数）
    int EndOffset,    // 範囲の終了位置（ソースコード先頭からの文字数）
    int Count         // 実行された回数（0 = 未実行）
);

