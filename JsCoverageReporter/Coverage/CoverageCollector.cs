using Microsoft.Playwright;

namespace JsCoverageReporter.Coverage;

/// <summary>
/// Playwright の CDP（Chrome DevTools Protocol）を使って JavaScript カバレッジを収集するクラス。
/// StartAsync でカバレッジ収集を開始し、StopAsync でデータを取得して返す。
/// 複数タブ（ページ）が開いた場合も Context.Page イベントで自動的に追跡する。
/// </summary>
internal class CoverageCollector(IPage page) : IAsyncDisposable
{
    // 追跡する全ページのリスト（最初のページから順に追加される。追加順がタブ番号になる）
    private readonly List<IPage>                    _trackedPages   = [];
    // ページごとの CDP セッション（ページ → CDP セッションのマッピング）
    private readonly Dictionary<IPage, ICDPSession> _cdpSessions    = [];
    // 新しいタブの SetupPageAsync タスクを格納するリスト（StopAsync で待機する）
    private readonly List<Task>                     _pageSetupTasks = [];
    // _trackedPages / _cdpSessions / _pageSetupTasks へのアクセスを同期するためのロックオブジェクト
    private readonly object                         _lock           = new();
    // DisposeAsync が既に呼ばれたかどうかを示すフラグ（二重解放を防ぐ）
    private bool _disposed = false;
    // StopAsync が既に呼ばれたかどうかを示すフラグ（二重呼び出しによる CDP エラーを防ぐ）
    private bool _stopped  = false;
    // Context.Page イベントハンドラの参照（DisposeAsync / StopAsync で解除するために保持する）
    private EventHandler<IPage>? _pageEventHandler = null;

    /// <summary>
    /// カバレッジ収集を開始する。
    /// 初期ページのCDPセッションを開始し、新しいタブの自動検出を登録する。
    /// </summary>
    public async Task StartAsync()
    {
        // 最初のページ（コンストラクタで渡されたページ）のカバレッジを開始する
        await SetupPageAsync(page);

        // 新しいタブが開いたとき自動でカバレッジを開始するイベントハンドラを定義する
        // ハンドラ参照を保持して StopAsync / DisposeAsync で解除できるようにする
        _pageEventHandler = (_, newPage) =>
        {
            // 新タブのセットアップタスクを開始する（await はしない — イベントハンドラのため）
            var task = SetupPageAsync(newPage);
            // _pageSetupTasks と _trackedPages / _cdpSessions は複数スレッドから触れるためロックする
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
    /// StartAsync（初期ページ）と Context.Page イベント（新タブ）の両方から呼ばれる。
    /// </summary>
    /// <param name="targetPage">カバレッジを開始するページ</param>
    private async Task SetupPageAsync(IPage targetPage)
    {
        // 追跡ページリストにこのページを追加する（追加順がタブ番号になる）
        // Context.Page イベントハンドラから並行して呼ばれる可能性があるためロックする
        lock (_lock)
        {
            _trackedPages.Add(targetPage);
        }

        // ページに接続したCDPセッションを作成する（await 中はロックを保持できないため lock の外で行う）
        var cdp = await targetPage.Context.NewCDPSessionAsync(targetPage);

        // 作成したCDPセッションをページに対応させて保存する（Dictionary も lock で保護する）
        lock (_lock)
        {
            _cdpSessions[targetPage] = cdp;
        }

        // V8 Profilerを有効にする（カバレッジ計測に必要）
        await cdp.SendAsync("Profiler.enable");

        // Debuggerを有効にする（ソースコード取得に必要）
        await cdp.SendAsync("Debugger.enable");

        // 精密カバレッジの記録を開始する
        // callCount: 実行回数を記録する / detailed: 細かいブロック単位で記録する
        await cdp.SendAsync("Profiler.startPreciseCoverage", new Dictionary<string, object>
        {
            // 各範囲の実行回数を記録するフラグ（true にするとカウント情報が得られる）
            ["callCount"] = true,
            // 関数レベルだけでなくブロック（if/else など）単位でも記録するフラグ
            ["detailed"] = true,
            // 自動トリガーによるカバレッジ更新を許可するフラグ
            ["allowTriggeredUpdates"] = true,
        });
    }

    /// <summary>
    /// カバレッジ収集を停止してデータを返す。
    /// 新タブのセットアップ完了を待ってから全ページのデータを収集する。
    /// </summary>
    /// <param name="scriptFilters">URLにいずれかの文字列を含むスクリプトだけを返す（空リストなら全部返す）</param>
    /// <param name="scriptExcludes">URLにいずれかの文字列を含むスクリプトを除外する（空リストなら除外なし）</param>
    /// <returns>収集したスクリプトカバレッジデータのリスト（全タブ分）</returns>
    public async Task<IReadOnlyList<ScriptCoverage>> StopAsync(
        IReadOnlyList<string> scriptFilters,
        IReadOnlyList<string> scriptExcludes)
    {
        // 二重呼び出しを防ぐ（2回目以降は空リストを返す）
        // _stopped フラグはメインスレッドからのみ操作されることを前提とする
        if (_stopped)
        {
            return [];
        }
        _stopped = true;

        // イベントハンドラをまず解除する（以降に開く新タブは追跡対象外にする）
        if (_pageEventHandler != null)
        {
            page.Context.Page -= _pageEventHandler;
            _pageEventHandler  = null;
        }

        // StartAsync が呼ばれていない場合（追跡ページなし）は空リストを返す
        int trackedCount;
        lock (_lock)
        {
            trackedCount = _trackedPages.Count;
        }
        if (trackedCount == 0)
        {
            return [];
        }

        // 新タブのセットアップタスクが完了するまで待機する
        // lock の中では await できないので、先にリストのコピーを取得する
        List<Task> setupTasks;
        lock (_lock)
        {
            setupTasks = new List<Task>(_pageSetupTasks);
        }
        await Task.WhenAll(setupTasks);

        // 全ページのカバレッジを収集してまとめる
        var allScripts = new List<ScriptCoverage>();

        // _trackedPages をスナップショットして反復する（反復中に別スレッドが変更しないよう保護）
        List<IPage> pageSnapshot;
        lock (_lock)
        {
            pageSnapshot = new List<IPage>(_trackedPages);
        }

        // スナップショットの順番がそのままタブ番号（Index）になる
        for (int i = 0; i < pageSnapshot.Count; i++)
        {
            // このページ（タブ）の情報を作成する（Url は収集直前の値を使う）
            var targetPage = pageSnapshot[i];
            var pageInfo = new PageInfo(i, targetPage.Url);

            // このページの CDP セッションが存在する場合のみ収集する
            ICDPSession? cdp;
            lock (_lock)
            {
                _cdpSessions.TryGetValue(targetPage, out cdp);
            }
            if (cdp == null)
            {
                // セットアップが失敗して CDP セッションが作れなかった場合はスキップする
                Console.Error.WriteLine($"[Warning] No CDP session for tab {i} ('{targetPage.Url}') — skipping.");
                continue;
            }

            // このページのカバレッジデータを収集する
            var scripts = await CollectFromPageAsync(cdp, pageInfo, scriptFilters, scriptExcludes);
            allScripts.AddRange(scripts);
        }

        return allScripts;
    }

    /// <summary>
    /// 1ページ分のCDPセッションからカバレッジデータを収集して返す。
    /// </summary>
    /// <param name="cdp">対象ページのCDPセッション</param>
    /// <param name="pageInfo">対象ページのタブ情報（ファイル名とレポートのページ列に使う）</param>
    /// <param name="scriptFilters">包含フィルタ</param>
    /// <param name="scriptExcludes">除外フィルタ</param>
    /// <returns>このページで収集したスクリプトカバレッジのリスト</returns>
    private static async Task<List<ScriptCoverage>> CollectFromPageAsync(
        ICDPSession cdp,
        PageInfo pageInfo,
        IReadOnlyList<string> scriptFilters,
        IReadOnlyList<string> scriptExcludes)
    {
        // CDPからカバレッジデータを取得する（null は未取得を示す）
        System.Text.Json.JsonElement? result = null;
        try
        {
            // 精密カバレッジのスナップショットを取得する
            result = await cdp.SendAsync("Profiler.takePreciseCoverage");
        }
        finally
        {
            // 成功・失敗どちらでも必ずProfilerを停止・無効化する
            await cdp.SendAsync("Profiler.stopPreciseCoverage");
            await cdp.SendAsync("Profiler.disable");
        }

        // データが取得できなかった場合は空リストを返す
        if (result is null)
        {
            return [];
        }

        // CDPレスポンスのJSONから "result" 配列を取り出す
        var root = result.Value;
        // "result" プロパティが存在しない場合は空リストを返す
        if (!root.TryGetProperty("result", out var resultArray))
        {
            return [];
        }

        // 各スクリプトのカバレッジデータを処理してリストに格納する
        var scripts = new List<ScriptCoverage>();

        // ループ中に例外が起きても必ず Debugger.disable を呼ぶために try/finally で囲む
        try
        {

        // CDPから返されたスクリプトエントリを1件ずつ処理する
        foreach (var entry in resultArray.EnumerateArray())
        {
            // スクリプトの URL を取得する（プロパティがない場合は空文字にする）
            var url = entry.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
            // スクリプトIDを取得する（ソースコード取得に使う）
            var scriptId = entry.TryGetProperty("scriptId", out var sidProp) ? sidProp.GetString() ?? "" : "";

            // URL が空のスクリプト（内部スクリプトなど）はスキップする
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }

            // scriptFilters が指定されている場合、いずれかのフィルタ文字列を URL に含むスクリプトのみ収集する
            if (scriptFilters.Count > 0 && !scriptFilters.Any(f => url.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // scriptExcludes に一致する URL のスクリプトは除外する
            if (scriptExcludes.Any(e => url.Contains(e, StringComparison.OrdinalIgnoreCase)))
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
                    source = srcProp.GetString() ?? "";
                }
            }
            catch (PlaywrightException)
            {
                // ソースコードの取得に失敗した場合は警告を出してこのスクリプトをスキップする
                Console.Error.WriteLine($"[Warning] Could not retrieve source for '{url}' — skipping.");
                continue;
            }

            // ソースコードが空のスクリプトはスキップする
            if (string.IsNullOrEmpty(source))
            {
                continue;
            }

            // functions 配列から関数カバレッジデータを組み立てる
            var functions = new List<FunctionCoverage>();

            if (entry.TryGetProperty("functions", out var funcsArray))
            {
                foreach (var func in funcsArray.EnumerateArray())
                {
                    // 関数名を取得する（無名関数は空文字になることがある）
                    var funcName = func.TryGetProperty("functionName", out var fnProp) ? fnProp.GetString() ?? "" : "";
                    var ranges = new List<CoverageRange>();

                    if (func.TryGetProperty("ranges", out var rangesArray))
                    {
                        foreach (var r in rangesArray.EnumerateArray())
                        {
                            // 範囲の開始位置・終了位置・実行回数を取得する
                            var start = r.TryGetProperty("startOffset", out var sProp) ? sProp.GetInt32() : 0;
                            var end = r.TryGetProperty("endOffset", out var eProp) ? eProp.GetInt32() : 0;
                            var count = r.TryGetProperty("count", out var cProp) ? cProp.GetInt32() : 0;
                            ranges.Add(new CoverageRange(start, end, count));
                        }
                    }

                    functions.Add(new FunctionCoverage(funcName, ranges));
                }
            }

            // スクリプトカバレッジデータをリストに追加する（pageInfo でどのタブのスクリプトかを記録する）
            scripts.Add(new ScriptCoverage(pageInfo, url, source, functions));
        }

        } // try ブロックの終わり
        finally
        {
            // 成功・失敗どちらでも必ず Debugger を無効化する
            await cdp.SendAsync("Debugger.disable");
        }

        // 収集したスクリプトのカバレッジデータリストを返す
        return scripts;
    }

    /// <summary>
    /// 保持している全リソース（CDP セッション）を解放する。
    /// IAsyncDisposable の実装。using await 構文で自動的に呼ばれる。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // 既に解放済みの場合は何もしない（二重解放を防ぐ）
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // StopAsync が呼ばれずに Dispose された場合でもイベントハンドラを解除する
        // （コンテキストが長命な場合のハンドラリークを防ぐ）
        if (_pageEventHandler != null)
        {
            page.Context.Page -= _pageEventHandler;
            _pageEventHandler  = null;
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
