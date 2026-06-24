using System;                          // InvalidOperationException などの基本型に使用
using System.Collections.Generic;      // Dictionary などのコレクションに使用
using System.Runtime.CompilerServices; // ConditionalWeakTable に使用
using System.Threading.Tasks;          // 非同期処理(Task)に使用
using Microsoft.Playwright;            // Playwright の型(IPage/IBrowser/ICDPSession)に使用

namespace JsCoverageReporter; // 本ソースが属する名前空間

// =====================================================================================
// ViewportFollower — ウィンドウ操作と viewport 追随をまとめた静的ユーティリティ
// -------------------------------------------------------------------------------------
// 【このクラスは何をするか】
//   Playwright(headed) を固定 viewport（ViewportSize.NoViewport ではない）で起動した場合、
//   ウィンドウを広げても HTML は override されたサイズのまま描画され、増えた分はグレー余白になる。
//   本クラスは「ウィンドウのサイズ/表示状態を変えつつ、実際に見えている HTML 領域へ viewport を
//   合わせ直す（追随）」操作を提供する。公開メソッドはすべて『追随付き』に統一してある。
//
// 【呼び出し方の基本】
//   すべて static メソッド。インスタンス生成は不要。別ソースからは型名で直接呼ぶ。
//     await ViewportFollower.SetWindowSizeAndFollowAsync(page, 1280, 800);
//
// 【どのメソッドをいつ使うか（早見表）】
//   ・ウィンドウサイズを変えて viewport も合わせたい（最も普通）
//        → await ViewportFollower.SetWindowSizeAndFollowAsync(page, w, h);
//   ・最大化して HTML も画面いっぱいにしたい
//        → await ViewportFollower.MaximizeAndFollowAsync(page);
//   ・全画面にして HTML も画面いっぱいにしたい
//        → await ViewportFollower.FullscreenAndFollowAsync(page);
//   ・通常状態に戻して HTML も合わせたい
//        → await ViewportFollower.RestoreAndFollowAsync(page);
//   ・外部(Win32 等)や利用者操作でウィンドウが変わった「後」に viewport だけ合わせたい
//        → await ViewportFollower.FollowAsync(page);
//   ・ツールバー高さ等が変わった（ブックマークバー切替・ブラウザ更新）→ 測り直させる
//        → ViewportFollower.ResetCalibration(page);  // 全ブラウザなら ResetAllCalibration();
//
// 【前提・注意】
//   ・単位は DIP(=CSS px)。Chromium 系専用（Chrome / Edge / bundled Chromium で確認済み）。
//   ・全ウィンドウが同一 DPI 前提（異なる DPI モニタ跨ぎは対象外）。
//   ・クロム量(タブ/アドレスバー＋枠)はブラウザ×ウィンドウ種別×状態ごとに実測して共有キャッシュする。
//     サイト発のツールバー無しポップアップにもベストエフォートで対応（数px 誤差を許容）。
//   ・最小化中(minimized)に FollowAsync を呼んでも何もしない。
// =====================================================================================

/// <summary>
/// ウィンドウのサイズ・表示状態の変更と、固定 viewport の追随をまとめた静的クラス。公開 API は追随付きのみ。
/// </summary>
public static class ViewportFollower
{
    // クロム量はウィンドウ(=ブラウザ)の性質で page には依らないため、ブラウザ単位で共有キャッシュする。
    // 同一ブラウザの全 page/タブで「種別:状態」ごと1回だけ実測し、別ブラウザは別途実測する。
    // ConditionalWeakTable によりブラウザ破棄時に自動解放される。値: "種別:状態" -> 実クロム量(DIP)。
    private static readonly ConditionalWeakTable<IBrowser, Dictionary<string, (int W, int H)>> _chromeCache = new();
    // 共有キャッシュへの読み書きを直列化するためのロックオブジェクト
    private static readonly object _gate = new();

    // =================================================================================
    // 公開: ウィンドウ操作 ＋ viewport 追随（これだけ使えばよい）
    // =================================================================================

    /// <summary>
    /// ウィンドウ外形サイズ(DIP)を変更し、続けて viewport を実コンテンツ領域へ追随させる。
    /// 【いつ】自分のコードでウィンドウサイズを変え、HTML 表示も隙間なく合わせたいとき（最も普通）。
    /// 【どう】await ViewportFollower.SetWindowSizeAndFollowAsync(page, 1280, 800);
    /// </summary>
    public static async Task SetWindowSizeAndFollowAsync(IPage page, int width, int height)
    {
        // まず外形サイズを変更する
        await SetWindowSizeAsync(page, width, height);
        // 変更後のサイズに viewport を追随させる
        await FollowAsync(page);
    } // SetWindowSizeAndFollowAsync ここまで

    /// <summary>
    /// ウィンドウを最大化し、viewport を追随させる（最大化状態を保持したまま埋める）。
    /// 【いつ】最大化してかつ HTML を画面いっぱいに表示したいとき。
    /// 【どう】await ViewportFollower.MaximizeAndFollowAsync(page);
    /// </summary>
    public static async Task MaximizeAndFollowAsync(IPage page)
    {
        // ウィンドウを最大化する
        await MaximizeAsync(page);
        // 最大化後の領域に viewport を追随させる
        await FollowAsync(page);
    } // MaximizeAndFollowAsync ここまで

    /// <summary>
    /// ウィンドウを全画面にし、viewport を追随させる（全画面状態を保持したまま埋める）。
    /// 【いつ】フルスクリーン表示で HTML を画面いっぱいにしたいとき。
    /// 【どう】await ViewportFollower.FullscreenAndFollowAsync(page);
    /// </summary>
    public static async Task FullscreenAndFollowAsync(IPage page)
    {
        // ウィンドウを全画面にする
        await FullscreenAsync(page);
        // 全画面後の領域に viewport を追随させる
        await FollowAsync(page);
    } // FullscreenAndFollowAsync ここまで

    /// <summary>
    /// ウィンドウを通常状態に戻し、viewport を追随させる。
    /// 【いつ】最大化/全画面から通常表示に戻し、HTML も合わせたいとき。
    /// 【どう】await ViewportFollower.RestoreAndFollowAsync(page);
    /// </summary>
    public static async Task RestoreAndFollowAsync(IPage page)
    {
        // ウィンドウを通常状態に戻す
        await RestoreAsync(page);
        // 通常化後の領域に viewport を追随させる
        await FollowAsync(page);
    } // RestoreAndFollowAsync ここまで

    /// <summary>
    /// 現在のウィンドウ状態/外形に合わせて viewport を実コンテンツ領域へ追随させる。
    /// 【いつ】外部(Win32 等)や利用者操作でウィンドウが変わった「後」に、viewport だけ合わせ直すとき。
    ///         （自分のコードで変える場合は *AndFollowAsync 系を使う方が簡単。）
    /// 【どう】// 例: SetWindowPos(hwnd, ...); の直後など
    ///         await ViewportFollower.FollowAsync(page);
    /// 【補足】最小化中は何もしない。最大化/全画面/通常いずれの状態でも正しく埋める。
    /// </summary>
    public static async Task FollowAsync(IPage page)
    {
        // 現在のウィンドウ状態を取得する
        string state = await GetWindowStateAsync(page);
        // 最小化中はサイズが意味を持たないので何もしない
        if (state == "minimized")
        {
            // 追随不要なので終了する
            return;
        }

        // 現在状態のクロム量を取得（未計測なら実測）する
        var chrome = await EnsureChromeAsync(page, state);
        // 状態遷移(特に fullscreen 解除)直後は外形が確定するまで時間がかかるので安定を待つ
        var (outerW, outerH) = await WaitForStableOuterAsync(page);

        // 外形からクロム量を引いてコンテンツ幅を求める（最低1px）
        int cssW = Math.Max(1, outerW - chrome.W);
        // 外形からクロム量を引いてコンテンツ高さを求める（最低1px）
        int cssH = Math.Max(1, outerH - chrome.H);
        // viewport(override) を実コンテンツ領域に合わせる
        await page.SetViewportSizeAsync(cssW, cssH);

        // SetViewportSizeAsync は windowState を normal に戻し、さらにウィンドウを Playwright 独自の
        // クロム見積りでリサイズする副作用がある（ポップアップでは外形が毎回育つ runaway になる）。
        // そこで CDP で状態/外形を測定値へ再適用して固定する。override 値は変わらないので、
        // 外形が測定値に戻れば override(=実コンテンツ領域)と一致して埋まる。
        if (state == "maximized")
        {
            // 最大化状態を再適用する（状態を保ったまま埋める）
            await MaximizeAsync(page);
        }
        else if (state == "fullscreen")
        {
            // 全画面状態を再適用する（状態を保ったまま埋める）
            await FullscreenAsync(page);
        }
        else
        {
            // normal 状態（通常窓・ポップアップ）は外形を測定値へ固定し直す（窓の暴走を抑える）
            await SetWindowSizeAsync(page, outerW, outerH);
        } // 状態/外形の再適用ここまで
    } // FollowAsync ここまで

    // =================================================================================
    // 公開: クロム量キャッシュの管理（ツールバー構成が変わったときに使う）
    // =================================================================================

    /// <summary>
    /// 指定 page が属するブラウザの計測済みクロム量を破棄する（次回 Follow 時に測り直す）。
    /// 【いつ】ブックマークバーの表示切替やブラウザ更新でツールバー高さが変わったとき。
    /// 【どう】ViewportFollower.ResetCalibration(page);
    /// </summary>
    public static void ResetCalibration(IPage page)
    {
        // 対象ブラウザを取得する
        var browser = page.Context.Browser;
        // ブラウザが取れない場合は何もしない
        if (browser == null)
        {
            // 破棄対象が無いので終了する
            return;
        }
        // 共有キャッシュから対象ブラウザ分を削除する
        lock (_gate)
        {
            // 該当ブラウザのキャッシュを除去する
            _chromeCache.Remove(browser);
        } // lock ここまで
    } // ResetCalibration ここまで

    /// <summary>
    /// 全ブラウザの計測済みクロム量を破棄する。
    /// 【いつ】プロセス全体でクロム構成が変わった等、まとめて測り直したいとき。
    /// 【どう】ViewportFollower.ResetAllCalibration();
    /// </summary>
    public static void ResetAllCalibration()
    {
        // 共有キャッシュを全消去する
        lock (_gate)
        {
            // すべてのブラウザ分を除去する
            _chromeCache.Clear();
        } // lock ここまで
    } // ResetAllCalibration ここまで

    // =================================================================================
    // 内部: ウィンドウ操作（CDP）。公開はしない（FollowAsync / *AndFollowAsync から利用）。
    // =================================================================================

    /// <summary>ウィンドウ外形サイズ(DIP)を指定値に変更する（内部用）。</summary>
    private static async Task SetWindowSizeAsync(IPage page, int width, int height)
    {
        // 対象 page 用の CDP セッションを生成する
        var cdp = await page.Context.NewCDPSessionAsync(page);
        // 操作対象ウィンドウの windowId を取得する
        int windowId = await GetWindowIdAsync(cdp);

        // 最大化/最小化中は幅・高さが効かないので normal に戻しつつサイズを指定する
        await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
        {
            // 対象ウィンドウを指定する
            ["windowId"] = windowId,
            // 変更する境界（状態＋サイズ）を指定する
            ["bounds"] = new Dictionary<string, object>
            {
                // 通常状態に戻す（サイズ指定を有効にするため）
                ["windowState"] = "normal",
                // 外形の幅(DIP)
                ["width"] = width,
                // 外形の高さ(DIP)
                ["height"] = height,
            } // bounds 辞書ここまで
        }); // setWindowBounds 呼び出しここまで
    } // SetWindowSizeAsync ここまで

    /// <summary>ウィンドウを最大化する（内部用）。</summary>
    private static async Task MaximizeAsync(IPage page)
    {
        // 状態を maximized にする
        await SetWindowStateAsync(page, "maximized");
    } // MaximizeAsync ここまで

    /// <summary>ウィンドウを全画面(フルスクリーン)にする（内部用）。</summary>
    private static async Task FullscreenAsync(IPage page)
    {
        // 状態を fullscreen にする
        await SetWindowStateAsync(page, "fullscreen");
    } // FullscreenAsync ここまで

    /// <summary>ウィンドウを通常状態に戻す（内部用）。</summary>
    private static async Task RestoreAsync(IPage page)
    {
        // 状態を normal にする
        await SetWindowStateAsync(page, "normal");
    } // RestoreAsync ここまで

    /// <summary>現在のウィンドウ状態を返す（normal / minimized / maximized / fullscreen）（内部用）。</summary>
    private static async Task<string> GetWindowStateAsync(IPage page)
    {
        // 対象 page 用の CDP セッションを生成する
        var cdp = await page.Context.NewCDPSessionAsync(page);
        // ウィンドウ情報を取得する（戻り値は JsonElement?）
        var result = await cdp.SendAsync("Browser.getWindowForTarget");
        // 取得失敗（null）なら例外にする
        if (result == null)
        {
            // 想定外なので明示的に失敗させる
            throw new InvalidOperationException("Browser.getWindowForTarget が null を返しました。");
        }
        // windowState 文字列を取り出す（無い場合は null になり得るので string? で受ける）
        string? windowState = result.Value.GetProperty("bounds").GetProperty("windowState").GetString();
        // null の場合は既定値 normal とみなす
        if (windowState == null)
        {
            // 既定の状態名を返す
            return "normal";
        }
        // 取得できた状態名を返す
        return windowState;
    } // GetWindowStateAsync ここまで

    /// <summary>
    /// windowState を変更する。CDP は非normal状態どうしの直接遷移を許さないため、
    /// 目的が非normalで現在も非normalの場合は一度 normal を経由する（内部用）。
    /// </summary>
    private static async Task SetWindowStateAsync(IPage page, string state)
    {
        // 対象 page 用の CDP セッションを生成する
        var cdp = await page.Context.NewCDPSessionAsync(page);
        // 操作対象ウィンドウの windowId を取得する
        int windowId = await GetWindowIdAsync(cdp);

        // 目的が非normal状態の場合のみ、現在状態を確認して必要なら normal を経由する
        if (state != "normal")
        {
            // 現在のウィンドウ状態を取得する
            string current = await GetWindowStateAsync(page);
            // 既に非normalなら、まず normal に戻す（直接遷移は不可のため）
            if (current != "normal")
            {
                // 一度 normal 状態へ遷移する
                await SetBoundsStateAsync(cdp, windowId, "normal");
            } // current が非normal の場合の処理ここまで
        } // 非normalへの遷移準備ここまで

        // 目的の状態へ遷移する
        await SetBoundsStateAsync(cdp, windowId, state);
    } // SetWindowStateAsync ここまで

    /// <summary>windowState 単独で Browser.setWindowBounds を送る（非normal状態指定時の必須形式）（内部用）。</summary>
    private static async Task SetBoundsStateAsync(ICDPSession cdp, int windowId, string state)
    {
        // windowState のみを指定して境界を更新する
        await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
        {
            // 対象ウィンドウを指定する
            ["windowId"] = windowId,
            // 非normal状態の指定時は windowState 単独で送る必要がある
            ["bounds"] = new Dictionary<string, object>
            {
                // 目的の表示状態
                ["windowState"] = state,
            } // bounds 辞書ここまで
        }); // setWindowBounds 呼び出しここまで
    } // SetBoundsStateAsync ここまで

    /// <summary>対象セッションのウィンドウ windowId を取得する（内部用）。</summary>
    private static async Task<int> GetWindowIdAsync(ICDPSession cdp)
    {
        // ウィンドウ情報を取得する（戻り値は JsonElement?）
        var result = await cdp.SendAsync("Browser.getWindowForTarget");
        // 取得失敗（null）なら例外にする
        if (result == null)
        {
            // 想定外なので明示的に失敗させる
            throw new InvalidOperationException("Browser.getWindowForTarget が null を返しました。");
        }
        // windowId を整数で返す
        return result.Value.GetProperty("windowId").GetInt32();
    } // GetWindowIdAsync ここまで

    // =================================================================================
    // 内部: クロム量の計測とキャッシュ
    // =================================================================================

    /// <summary>対象状態のクロム量を返す（ブラウザ×種別×状態 単位で未計測なら計測してキャッシュ）（内部用）。</summary>
    private static async Task<(int W, int H)> EnsureChromeAsync(IPage page, string state)
    {
        // 全画面はクロムが無いので常に (0,0) を返す（種別に依らない）
        if (state == "fullscreen")
        {
            // 計測不要
            return (0, 0);
        }

        // 対象ブラウザを取得する
        var browser = page.Context.Browser;
        // ブラウザが取れない場合は失敗させる
        if (browser == null)
        {
            // Connect 経由などで Browser 未公開のケース
            throw new InvalidOperationException("Browser を取得できません（Connect 経由などで未公開）。");
        }
        // ウィンドウ種別(normal/popup)を判定する（ツールバー有無でクロムが異なるため）
        string kind = await DetectWindowKindAsync(page);
        // キャッシュキーは「種別:状態」（例 "popup:normal"）
        string key = kind + ":" + state;
        // 対象ブラウザのキャッシュ辞書を取得する
        var cache = GetBrowserCache(browser);

        // 既に計測済みならその値を返す
        lock (_gate)
        {
            // キーに対応する値が有ればそれを返す
            if (cache.TryGetValue(key, out var cached))
            {
                // キャッシュヒット
                return cached;
            } // TryGetValue 真の処理ここまで
        } // lock ここまで

        // 実測は await を伴うので lock の外で行う（同時実行で二重計測しても結果は同じ＝無害）
        var measured = await ProbeChromeAsync(page, state, kind);

        // 計測結果をキャッシュへ格納する（競合時は先勝ち）
        lock (_gate)
        {
            // 競合で既に格納されていればそちらを優先する
            if (cache.TryGetValue(key, out var existing))
            {
                // 先に格納された値を返す
                return existing;
            } // 競合検出時の処理ここまで
            // 計測値を格納する
            cache[key] = measured;
            // 計測値を返す
            return measured;
        } // lock ここまで
    } // EnsureChromeAsync ここまで

    /// <summary>
    /// ウィンドウ種別を判定して返す（"normal" / "popup"）。
    /// アドレスバー(locationbar)の有無で判定する。override 下でも正しく取得できる（内部用）。
    /// </summary>
    private static async Task<string> DetectWindowKindAsync(IPage page)
    {
        // アドレスバーが見えているか（通常窓=true / ツールバー無しポップアップ=false）
        bool hasLocationBar = await page.EvaluateAsync<bool>("() => window.locationbar.visible");
        // アドレスバーがあれば通常窓とみなす
        if (hasLocationBar)
        {
            // 通常窓
            return "normal";
        }
        // 無ければツールバー無しポップアップとみなす（ベストエフォート）
        return "popup";
    } // DetectWindowKindAsync ここまで

    /// <summary>ブラウザ単位のクロム量キャッシュを取得する（無ければ作成）（内部用）。</summary>
    private static Dictionary<string, (int W, int H)> GetBrowserCache(IBrowser browser)
    {
        // キャッシュ取得/生成を直列化する
        lock (_gate)
        {
            // 対象ブラウザの辞書が無ければ新規作成する
            if (!_chromeCache.TryGetValue(browser, out var dict))
            {
                // 空の辞書を生成する
                dict = new Dictionary<string, (int W, int H)>();
                // テーブルへ登録する
                _chromeCache.Add(browser, dict);
            } // 新規作成の処理ここまで
            // 取得/作成した辞書を返す
            return dict;
        } // lock ここまで
    } // GetBrowserCache ここまで

    /// <summary>
    /// 同一ブラウザに NoViewport の一時ウィンドウを開き、対象状態・種別の実クロム量(outer - inner)を計測する。
    /// kind が "popup" のときはツールバー無しポップアップを開いて計測する（ベストエフォート）（内部用）。
    /// </summary>
    private static async Task<(int W, int H)> ProbeChromeAsync(IPage page, string state, string kind)
    {
        // 対象ブラウザを取得する
        var browser = page.Context.Browser;
        // ブラウザが取れない場合は失敗させる
        if (browser == null)
        {
            // Connect 経由などで Browser 未公開のケース
            throw new InvalidOperationException("Browser を取得できません（Connect 経由などで未公開）。");
        }

        // 計測用に NoViewport の一時コンテキストを作る（inner が実コンテンツになる）
        var probeContext = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            // override を無効化して実ウィンドウに追随（自動フィット）させる
            ViewportSize = ViewportSize.NoViewport,
        }); // NewContextAsync ここまで
        // 計測後に必ずコンテキストを閉じる
        try
        {
            // 計測対象のページ（通常窓 or ポップアップ）
            IPage probe;
            // 種別がポップアップなら、同種(ツールバー無し)の窓を開いて測る
            if (kind == "popup")
            {
                // ポップアップを開くための元ページを用意する
                var probeOpener = await probeContext.NewPageAsync();
                // 空ページへ遷移する
                await probeOpener.GotoAsync("about:blank");
                // 開かれるポップアップを待ち受ける
                var popupWait = probeContext.WaitForPageAsync();
                // ツールバー/メニュー/アドレスバー無しのポップアップを開く
                await probeOpener.EvaluateAsync(
                    "() => window.open('about:blank','_blank','width=600,height=400,toolbar=no,menubar=no,location=no')");
                // 開いたポップアップを計測対象にする
                probe = await popupWait;
            }
            else
            {
                // 通常窓は普通にページを開いて測る
                probe = await probeContext.NewPageAsync();
                // 空ページへ遷移する
                await probe.GotoAsync("about:blank");
            } // 種別ごとの probe 準備ここまで

            // maximized のクロムを測る場合は計測ウィンドウも最大化する
            if (state == "maximized")
            {
                // 計測ウィンドウを最大化する
                await MaximizeAsync(probe);
            } // maximized 時の処理ここまで
            // レイアウト確定を少し待つ
            await probe.WaitForTimeoutAsync(300);

            // outer-inner で実クロム量(幅・高さ)を取得する
            var m = await probe.EvaluateAsync<double[]>(
                "() => [window.outerWidth - window.innerWidth, window.outerHeight - window.innerHeight]");
            // 整数(DIP)に丸めてタプルで返す
            return ((int)Math.Round(m[0]), (int)Math.Round(m[1]));
        } // try ここまで
        finally
        {
            // 一時コンテキストを閉じてウィンドウを片付ける
            await probeContext.CloseAsync();
        } // finally ここまで
    } // ProbeChromeAsync ここまで

    // =================================================================================
    // 内部: 外形(JS)の取得と安定待ち
    // =================================================================================

    /// <summary>
    /// ウィンドウ外形が安定する（2回連続で同値になる）まで待って返す。
    /// 状態遷移（最大化/全画面の解除など）直後の過渡的なサイズを避けるため（内部用）。
    /// </summary>
    private static async Task<(int Width, int Height)> WaitForStableOuterAsync(IPage page)
    {
        // 監視の最大回数（約 20 * 80ms = 1.6s 上限）
        const int maxAttempts = 20;
        // 監視の間隔(ミリ秒)
        const int intervalMs = 80;

        // まず現在の外形を取得する
        var prev = await GetOuterAsync(page);
        // 安定するまで一定回数ポーリングする
        for (int i = 0; i < maxAttempts; i++)
        {
            // 次の観測まで待つ
            await page.WaitForTimeoutAsync(intervalMs);
            // 改めて外形を取得する
            var cur = await GetOuterAsync(page);
            // 前回と同値なら安定とみなして返す
            if (cur.Width == prev.Width && cur.Height == prev.Height)
            {
                // 安定した外形を返す
                return cur;
            } // 安定判定ここまで
            // 今回値を次回比較用に保持する
            prev = cur;
        } // for ここまで
        // 上限まで安定しなければ最後の値を返す
        return prev;
    } // WaitForStableOuterAsync ここまで

    /// <summary>実際に見えているウィンドウ外形(DIP)を JS から取得する（override 非依存）（内部用）。</summary>
    private static async Task<(int Width, int Height)> GetOuterAsync(IPage page)
    {
        // window.outerWidth/outerHeight を取得する
        var o = await page.EvaluateAsync<double[]>("() => [window.outerWidth, window.outerHeight]");
        // 整数(DIP)に丸めてタプルで返す
        return ((int)Math.Round(o[0]), (int)Math.Round(o[1]));
    } // GetOuterAsync ここまで
} // ViewportFollower ここまで
