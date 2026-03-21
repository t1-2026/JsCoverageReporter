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

    // ページごとの収集済みスクリプト ID セット（二重収集を防ぐ）
    private readonly Dictionary<IPage, HashSet<string>>        _processedScriptIds  = [];
    // 中間スナップショットで収集したスクリプトのリスト（最終的に StopAsync で返す）
    private readonly List<ScriptCoverage>                       _intermediateScripts = [];
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
            // このページの収集済みスクリプト ID セットを初期化する
            _processedScriptIds[targetPage] = new HashSet<string>();
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

            // ナビゲーション開始直前の現在 URL を取得する（遷移後は変わるため）
            string currentUrl = targetPage.Url;

            // 中間スナップショットタスクを開始して追跡リストに追加する
            // async void イベントハンドラのため、タスクを保存して StopAsync で待機する
            var snapTask = TakeIntermediateSnapshotAsync(targetPage, currentUrl);
            lock (_lock)
            {
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

        // このページの収集済みIDセットを取得する
        HashSet<string>? processedIds;
        lock (_lock)
        {
            _processedScriptIds.TryGetValue(targetPage, out processedIds);
        }
        // SetupPageAsync が完了する前に到達することは通常ないが、念のため早期リターンする
        // （ローカルな HashSet を作ると _processedScriptIds と切り離されて二重集計になるため）
        if (processedIds == null) { return; }

        // 新しいスクリプト（未収集のもの）を処理して中間リストに追加する
        var newScripts = await ProcessNewScriptsAsync(result.Value, cdp, pageInfo, processedIds);
        lock (_lock)
        {
            _intermediateScripts.AddRange(newScripts);
        }
    }

    /// <summary>
    /// CDP スナップショット結果から未収集スクリプトのみを処理して返す。
    /// processedIds に含まれるスクリプト ID はスキップし、新規のものは processedIds に追加する。
    /// Profiler や Debugger の停止は行わない（最終収集時のみ停止する）。
    /// </summary>
    private async Task<List<ScriptCoverage>> ProcessNewScriptsAsync(
        System.Text.Json.JsonElement root,
        ICDPSession cdp,
        PageInfo pageInfo,
        HashSet<string> processedIds)
    {
        var scripts = new List<ScriptCoverage>();

        // CDP レスポンスの "result" 配列を取り出す
        if (!root.TryGetProperty("result", out var resultArray))
        {
            return scripts;
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

            // 収集済みスクリプト ID はスキップする（二重収集防止）
            // lock の中で Add を呼ぶことでスレッドセーフに「初回のみ処理」を保証する
            bool isNew;
            lock (_lock)
            {
                isNew = processedIds.Add(scriptId);
            }
            if (!isNew) { continue; }

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

            // Debugger.getScriptSource でスクリプトのソースコードを取得する
            string source = "";
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

            scripts.Add(new ScriptCoverage(pageInfo, url, source, functions));
        }

        return scripts;
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

        // 中間スナップショットで収集済みのスクリプトをまとめリストに追加する
        var allScripts = new List<ScriptCoverage>();
        lock (_lock)
        {
            allScripts.AddRange(_intermediateScripts);
        }

        // 全ページの最終カバレッジを収集する（中間スナップショット以降の新規スクリプトのみ）
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
            HashSet<string>? processedIds;
            lock (_lock)
            {
                _cdpSessions.TryGetValue(targetPage, out cdp);
                _processedScriptIds.TryGetValue(targetPage, out processedIds);
            }

            if (cdp == null)
            {
                Console.Error.WriteLine($"[Warning] No CDP session for tab {i} ('{targetPage.Url}') — skipping.");
                continue;
            }

            // SetupPageAsync が完了する前に到達することは通常ないが、念のため早期スキップする
            // （ローカルな HashSet を作ると _processedScriptIds と切り離されて二重集計になるため）
            if (processedIds == null)
            {
                Console.Error.WriteLine($"[Warning] No processedIds for tab {i} ('{targetPage.Url}') — skipping.");
                continue;
            }

            // 最終収集（Profiler と Debugger を停止しながら残りのスクリプトを収集する）
            var scripts = await FinalCollectFromPageAsync(cdp, pageInfo, processedIds);
            allScripts.AddRange(scripts);
        }

        return allScripts;
    }

    /// <summary>
    /// 1ページ分の最終カバレッジ収集。Profiler と Debugger を停止しながら未収集スクリプトを取得する。
    /// </summary>
    private async Task<List<ScriptCoverage>> FinalCollectFromPageAsync(
        ICDPSession cdp,
        PageInfo pageInfo,
        HashSet<string> processedIds)
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
            await cdp.SendAsync("Profiler.stopPreciseCoverage");
            await cdp.SendAsync("Profiler.disable");
        }

        if (result is null) { return []; }

        // 未収集スクリプトを処理する（Debugger は ProcessNewScriptsAsync の後に無効化する）
        List<ScriptCoverage> scripts;
        try
        {
            scripts = await ProcessNewScriptsAsync(result.Value, cdp, pageInfo, processedIds);
        }
        finally
        {
            // 成功・失敗どちらでも必ず Debugger を無効化する
            await cdp.SendAsync("Debugger.disable");
        }

        return scripts;
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
        List<ICDPSession> sessions;
        lock (_lock)
        {
            sessions = new List<ICDPSession>(_cdpSessions.Values);
        }
        foreach (var cdp in sessions)
        {
            await cdp.DisposeAsync();
        }
    }
}
