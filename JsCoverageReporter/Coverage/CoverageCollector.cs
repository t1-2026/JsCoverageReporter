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
    private IReadOnlyList<string> _scriptFilters  = [];
    // スクリプト除外フィルター（StartAsync で設定。中間スナップショット時にも使う）
    private IReadOnlyList<string> _scriptExcludes = [];

    // ページごとの全収集済みスクリプトカバレッジデータ（scriptId -> ScriptCoverage）
    // ナビゲーションキャンセルの際に生存しているスクリプトの実行数を更新するために利用する
    private readonly Dictionary<IPage, Dictionary<string, ScriptCoverage>> _scriptCache = [];
    // 中間スナップショットタスクのリスト（StopAsync で待機する）
    private readonly List<Task>                                 _snapTasks           = [];
    // ページごとのリクエストイベントハンドラー（DisposeAsync / StopAsync で解除する）
    private readonly Dictionary<IPage, EventHandler<IRequest>> _requestHandlers     = [];

    /// <summary>
    /// カバレッジ収集を開始する。
    /// フィルターを保存し、初期ページのCDPセッションを開始し、新しいタブの自動検出を登録する。
    /// </summary>
    /// <param name="scriptFilters">URLにいずれかの文字列を含むスクリプトだけを返す（空リストなら全部）</param>
    /// <param name="scriptExcludes">URLにいずれかの文字列を含むスクリプトを除外する（空リストなら除外なし）</param>
    public async Task StartAsync(IReadOnlyList<string> scriptFilters, IReadOnlyList<string> scriptExcludes)
    {
        // フィルターをインスタンスフィールドに保存する（中間スナップショット時にも参照する）
        _scriptFilters  = scriptFilters;
        _scriptExcludes = scriptExcludes;

        // 最初のページ（コンストラクタで渡されたページ）のカバレッジを開始する
        await SetupPageAsync(page);

        // 新しいタブが開いたとき自動でカバレッジを開始するイベントハンドラを定義する
        _pageEventHandler = (_, newPage) =>
        {
            var task = SetupPageAsync(newPage);
            lock (_lock)
            {
                _pageSetupTasks.Add(task);
            }
        };

        // 新しいタブが開いたときのイベントを購読する
        page.Context.Page += _pageEventHandler;
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
            _trackedPages.Add(targetPage);
            // このページのスクリプトキャッシュを初期化する
            _scriptCache[targetPage] = new Dictionary<string, ScriptCoverage>();
        }

        // ページに接続したCDPセッションを作成する
        var cdp = await targetPage.Context.NewCDPSessionAsync(targetPage);

        lock (_lock)
        {
            _cdpSessions[targetPage] = cdp;
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

        // ナビゲーションリクエストを検出してスナップショットを撮るハンドラーを定義する
        // （ページ遷移前の旧ページのスクリプトを正確なページ URL で記録するため）
        EventHandler<IRequest> reqHandler = (_, req) =>
        {
            // ナビゲーションリクエスト以外は無視する
            if (!req.IsNavigationRequest) { return; }
            lock (_lock) { if (_stopped) { return; } } // StopAsync 中に呼ばれた場合は破棄する

            // ナビゲーション開始直前の現在 URL を取得する（遷移後は変わるため）
            string currentUrl = targetPage.Url;

            // 中間スナップショットタスクを開始して追跡リストに追加する
            // async void イベントハンドラのため、タスクを保存して StopAsync で待機する
            var snapTask = TakeIntermediateSnapshotAsync(targetPage, currentUrl);
            lock (_lock)
            {
                if (_stopped) { return; } // もう一度チェック
                _snapTasks.Add(snapTask);
            }
        };

        targetPage.Request += reqHandler;
        lock (_lock)
        {
            _requestHandlers[targetPage] = reqHandler;
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
        // このページの CDP セッションとタブ番号を取得する
        ICDPSession? cdp;
        int tabIndex;
        lock (_lock)
        {
            _cdpSessions.TryGetValue(targetPage, out cdp);
            tabIndex = _trackedPages.IndexOf(targetPage);
        }
        if (cdp == null) { return; }

        // スナップショットを取得する（Profiler は停止しない）
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

            // scriptFilters に一致するスクリプトのみ処理する
            if (_scriptFilters.Count > 0 && !_scriptFilters.Any(f => url.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // scriptExcludes に一致するスクリプトは除外する
            if (_scriptExcludes.Any(e => url.Contains(e, StringComparison.OrdinalIgnoreCase)))
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
            if (existingCoverage != null)
            {
                // 既にソースを取得済みのスクリプトならソースコードを再利用する
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
                catch (PlaywrightException)
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
                            int start;
                            if (r.TryGetProperty("startOffset", out var sProp)) { start = sProp.GetInt32(); } else { start = 0; }
                            int end;
                            if (r.TryGetProperty("endOffset",   out var eProp)) { end   = eProp.GetInt32(); } else { end   = 0; }
                            int count;
                            if (r.TryGetProperty("count",       out var cProp)) { count = cProp.GetInt32(); } else { count = 0; }
                            ranges.Add(new CoverageRange(start, end, count));
                        }
                    }
                    functions.Add(new FunctionCoverage(funcName, ranges));
                }
            }

            // キャッシュに保存（最新の実行数で上書き更新する。URLのPageInfoは既存のものを優先）
            var finalPageInfo = existingCoverage?.Page ?? pageInfo;
            var newCoverage = new ScriptCoverage(finalPageInfo, url, source, functions);

            lock (_lock)
            {
                scriptCache[scriptId] = newCoverage;
            }
        }
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

        // Context.Page イベントハンドラを解除する
        if (_pageEventHandler != null)
        {
            page.Context.Page -= _pageEventHandler;
            _pageEventHandler  = null;
        }

        // リクエストイベントハンドラをすべて解除する（新たな中間スナップショットを防ぐ）
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

        // 追跡ページがない場合は空リストを返す
        int trackedCount;
        lock (_lock)
        {
            trackedCount = _trackedPages.Count;
        }
        if (trackedCount == 0) { return []; }

        // 新しいタブのセットアップタスクが完了するまで待機する
        List<Task> setupTasks;
        lock (_lock)
        {
            setupTasks = new List<Task>(_pageSetupTasks);
        }
        await Task.WhenAll(setupTasks);

        // 中間スナップショットタスクが完了するまで待機する
        List<Task> snapTasks;
        lock (_lock)
        {
            snapTasks = new List<Task>(_snapTasks);
        }
        await Task.WhenAll(snapTasks);

        // セットアップ待機中に追加されたスナップタスクを二段階目として確認する
        List<Task> snapTasks2;
        lock (_lock)
        {
            snapTasks2 = new List<Task>(_snapTasks);
        }
        if (snapTasks2.Count > snapTasks.Count)
        {
            await Task.WhenAll(snapTasks2);
        }

        // 全ページの最終カバレッジを収集する（全スクリプトの最終状態をマージする）
        List<IPage> pageSnapshot;
        lock (_lock)
        {
            pageSnapshot = new List<IPage>(_trackedPages);
        }

        for (int i = 0; i < pageSnapshot.Count; i++)
        {
            var targetPage = pageSnapshot[i];
            // StopAsync 時点の URL を最終ページ URL として使用する
            var pageInfo = new PageInfo(i, targetPage.Url);

            ICDPSession? cdp;
            Dictionary<string, ScriptCoverage>? scriptCache;
            lock (_lock)
            {
                _cdpSessions.TryGetValue(targetPage, out cdp);
                _scriptCache.TryGetValue(targetPage, out scriptCache);
            }

            if (cdp == null || scriptCache == null)
            {
                Console.Error.WriteLine($"[Warning] No CDP session or cache for tab {i} ('{targetPage.Url}') — skipping.");
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
        System.Text.Json.JsonElement? result = null;
        try
        {
            result = await cdp.SendAsync("Profiler.takePreciseCoverage");
        }
        finally
        {
            // 成功・失敗どちらでも必ず Profiler を停止・無効化する
            // ページがクラッシュしている場合など CDP 側でも例外が出ることがあるため
            // try/catch で囲み、元の例外を飲み込まないようにする
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
        lock (_lock)
        {
            if (_disposed) { return; }
            _disposed = true;
        }

        // Context.Page イベントハンドラを解除する
        if (_pageEventHandler != null)
        {
            page.Context.Page -= _pageEventHandler;
            _pageEventHandler  = null;
        }

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

        // すべての CDP セッションを解放する
        // StopAsync を経由せず Dispose された場合は Profiler / Debugger を明示的に停止する
        List<ICDPSession> sessions;
        lock (_lock)
        {
            sessions = new List<ICDPSession>(_cdpSessions.Values);
        }
        foreach (var cdp in sessions)
        {
            // StopAsync を経ていない場合は Profiler / Debugger が enable のまま残っている
            // 安全に停止を試みる（失敗しても Dispose は続行する）
            if (!_stopped)
            {
                try { await cdp.SendAsync("Profiler.stopPreciseCoverage"); } catch { }
                try { await cdp.SendAsync("Profiler.disable"); } catch { }
                try { await cdp.SendAsync("Debugger.disable"); } catch { }
            }
            await cdp.DisposeAsync();
        }
    }
}
