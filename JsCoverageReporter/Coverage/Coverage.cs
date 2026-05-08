#nullable disable

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
    // スナップショットのスロットリング間隔（ミリ秒）（この間隔内の連続スナップショットはスキップする）
    // テストからインスタンスごとに 0 に設定することでスロットリングを無効化できる（internal にしてテスト性を確保）
    internal long SnapshotThrottleMs = 500;

    /// <summary>
    /// カバレッジ収集を開始する。
    /// フィルターを保存し、初期ページのCDPセッションを開始し、新しいタブの自動検出を登録する。
    /// </summary>
    /// <param name="scriptFilters">URLにいずれかの文字列を含むスクリプトだけを返す（空リストなら全部）</param>
    /// <param name="scriptExcludes">URLにいずれかの文字列を含むスクリプトを除外する（空リストなら除外なし）</param>
    public async Task StartAsync(IReadOnlyList<string> scriptFilters, IReadOnlyList<string> scriptExcludes)
    {
        // フィルターをインスタンスフィールドに保存する（中間スナップショット時にも参照する）
        // 参照型の代入は .NET のメモリモデル上アトミックなため、バックグラウンドタスクとのロック不要
        _scriptFilters  = scriptFilters;
        _scriptExcludes = scriptExcludes;

        // 最初のページ（コンストラクタで渡されたページ）のカバレッジを開始する
        await SetupPageAsync(page);

        // 既存のイベントハンドラがあれば先に解除する（StartAsync の二重呼び出し対策）
        if (_pageEventHandler != null)
        {
            page.Context.Page -= _pageEventHandler;
            _pageEventHandler = null;
        }

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
            // 既に追跡中のページはスキップする（StartAsync の二重呼び出し対策）
            if (_trackedPages.Contains(targetPage)) { return; }
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

        // ページが閉じられたときにスナップショットを撮るハンドラーを定義する
        // （StopAsync より先にページが閉じられた場合でも、実行済みスクリプトを取得できるようにする）
        EventHandler<IPage> closeHandler = (_, _) =>
        {
            // ページが閉じられる直前の URL を取得する（取得できない場合は空文字列にする）
            string currentUrl;
            try { currentUrl = targetPage.Url; }
            catch (Exception) { currentUrl = ""; }

            // ページ閉鎖直後は CDP コマンドが失敗する可能性があるが、
            // TakeIntermediateSnapshotAsync が内部で try-catch しているため安全
            var snapTask = TakeIntermediateSnapshotAsync(targetPage, currentUrl);
            lock (_lock)
            {
                // タスク開始後に _stopped になった場合は追加しない（孤立タスクは CDP try-catch で保護済み）
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
            // scriptId が空の場合は Debugger.getScriptSource を呼べないためスキップする
            if (string.IsNullOrEmpty(scriptId)) { continue; }

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
                        //   パース失敗(文字列等) → int.MaxValue（範囲をクランプで無効化して安全にスキップする）
                        int start;
                            if (r.TryGetProperty("startOffset", out var sProp)) { if (!sProp.TryGetInt32(out start)) { start = int.MaxValue; } } else { start = 0; }
                            int end;
                            if (r.TryGetProperty("endOffset",   out var eProp)) { if (!eProp.TryGetInt32(out end))   { end   = int.MaxValue; } } else { end   = 0; }
                            int count;
                            if (r.TryGetProperty("count",       out var cProp)) { if (!cProp.TryGetInt32(out count)) { count = 1;           } } else { count = 0; }
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
            // StopAsync 時点の URL を最終ページ URL として使用する
            // ページが既に閉じられている場合 page.Url が例外を投げることがあるため try-catch で保護する
            string pageUrl;
            try { pageUrl = targetPage.Url; }
            catch (Exception) { pageUrl = ""; }
            var pageInfo = new PageInfo(i, pageUrl);

            ICDPSession? cdp;
            Dictionary<string, ScriptCoverage>? scriptCache;
            lock (_lock)
            {
                _cdpSessions.TryGetValue(targetPage, out cdp);
                _scriptCache.TryGetValue(targetPage, out scriptCache);
            }

            if (cdp == null || scriptCache == null)
            {
                Console.Error.WriteLine($"[Warning] No CDP session or cache for tab {i} — skipping.");
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
        List<ICDPSession> sessions;
        lock (_lock)
        {
            sessions = new List<ICDPSession>(_cdpSessions.Values);
        }
        foreach (var cdp in sessions)
        {
            // StopAsync を経ていない場合は Profiler / Debugger が enable のまま残っている
            // 安全に停止を試みる（失敗しても Dispose は続行する）
            if (needsCleanup)
            {
                try { await cdp.SendAsync("Profiler.stopPreciseCoverage"); } catch (Exception ex) { Console.Error.WriteLine($"[Warning] DisposeAsync: Profiler.stopPreciseCoverage failed: {ex.Message}"); }
                try { await cdp.SendAsync("Profiler.disable"); } catch (Exception ex) { Console.Error.WriteLine($"[Warning] DisposeAsync: Profiler.disable failed: {ex.Message}"); }
                try { await cdp.SendAsync("Debugger.disable"); } catch (Exception ex) { Console.Error.WriteLine($"[Warning] DisposeAsync: Debugger.disable failed: {ex.Message}"); }
            }
            // ページが既に閉じられている場合 DisposeAsync が例外を投げることがあるため保護する
            try { await cdp.DisposeAsync(); }
            catch (Exception ex) { Console.Error.WriteLine($"[Warning] DisposeAsync: CDPSession.DisposeAsync failed: {ex.Message}"); }
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

