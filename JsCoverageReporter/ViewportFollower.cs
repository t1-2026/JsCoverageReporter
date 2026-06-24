using System;                          // InvalidOperationException などの基本型に使用
using System.Collections.Generic;      // Dictionary などのコレクションに使用
using System.Runtime.CompilerServices; // ConditionalWeakTable に使用
using System.Threading.Tasks;          // 非同期処理(Task)に使用
using Microsoft.Playwright;            // Playwright の型(IPage/IBrowser/ICDPSession)に使用

namespace JsCoverageReporter; // 本ソースが属する名前空間

// =====================================================================================
// 使い方（このファイルは WindowSizer と ViewportFollower の2クラスを含む）
// -------------------------------------------------------------------------------------
// 【WindowSizer】ブラウザウィンドウの外形サイズ・位置・表示状態を操作する（静的クラス）。
//   await WindowSizer.SetWindowSizeAsync(page, 1200, 800); // 外形サイズを変更(DIP)
//   await WindowSizer.SetWindowPositionAsync(page, 50, 40); // 位置を変更(DIP)
//   await WindowSizer.MaximizeAsync(page);                  // 最大化
//   await WindowSizer.MinimizeAsync(page);                  // 最小化
//   await WindowSizer.FullscreenAsync(page);               // 全画面
//   await WindowSizer.RestoreAsync(page);                  // 通常状態へ戻す
//   var b  = await WindowSizer.GetWindowBoundsAsync(page); // 現在の外形(left,top,width,height)
//   var st = await WindowSizer.GetWindowStateAsync(page);  // 現在の状態(normal/minimized/maximized/fullscreen)
//
// 【ViewportFollower】固定 viewport で起動した headed ブラウザで、ウィンドウサイズ・表示状態の
//   変更後に「実際に見えている HTML 領域」へ viewport を追随させる。
//   var follower = new ViewportFollower();
//   // (A) 外部 Win32 でリサイズした場合：リサイズ後に Follow を呼ぶ
//   await follower.FollowAsync(page);
//   // (B) ウィンドウ操作と追随を一括で行う便利メソッド
//   await follower.SetWindowSizeAsync(page, 1600, 1000); // サイズ変更＋追随
//   await follower.MaximizeAsync(page);                  // 最大化＋追随（状態を保持して埋める）
//   await follower.FullscreenAsync(page);                // 全画面＋追随
//   await follower.RestoreAsync(page);                   // 通常化＋追随
//
// 共通の前提・注意：
//   - 単位は DIP(=CSS px)。Chromium 系専用（Chrome / Edge / bundled Chromium で動作確認済み）。
//   - 全ウィンドウが同一 DPI・同一ツールバー構成であること（異なる DPI モニタ跨ぎは対象外）。
//   - ツールバー構成が異なる窓（サイト発のツールバー無しポップアップ等）は別途 NoViewport で開くこと。
// =====================================================================================

/// <summary>
/// Page が属するブラウザウィンドウの外形サイズ・位置・表示状態を CDP(Browser.setWindowBounds) で操作するヘルパー。
/// 設定対象は「ウィンドウ外形」（タイトルバー＋枠＋タブ/アドレスバー込み）で、HTML 表示領域(content)ではない。
/// </summary>
public static class WindowSizer
{
    /// <summary>Page が属するブラウザウィンドウの外形サイズ(DIP)を指定値に変更する。</summary>
    public static async Task SetWindowSizeAsync(IPage page, int width, int height)
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

    /// <summary>Page が属するブラウザウィンドウの位置(左上座標, DIP)を変更する。</summary>
    public static async Task SetWindowPositionAsync(IPage page, int left, int top)
    {
        // 対象 page 用の CDP セッションを生成する
        var cdp = await page.Context.NewCDPSessionAsync(page);
        // 操作対象ウィンドウの windowId を取得する
        int windowId = await GetWindowIdAsync(cdp);

        // ウィンドウの左上座標を変更する
        await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
        {
            // 対象ウィンドウを指定する
            ["windowId"] = windowId,
            // 変更する境界（状態＋位置）を指定する
            ["bounds"] = new Dictionary<string, object>
            {
                // 通常状態に戻す（座標指定を有効にするため）
                ["windowState"] = "normal",
                // 左端の X 座標(DIP)
                ["left"] = left,
                // 上端の Y 座標(DIP)
                ["top"] = top,
            } // bounds 辞書ここまで
        }); // setWindowBounds 呼び出しここまで
    } // SetWindowPositionAsync ここまで

    /// <summary>Page が属するブラウザウィンドウの現在の外形(DIP)を取得する。</summary>
    public static async Task<(int Left, int Top, int Width, int Height)> GetWindowBoundsAsync(IPage page)
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
        // bounds オブジェクトを取り出す
        var b = result.Value.GetProperty("bounds");
        // (left, top, width, height) のタプルで返す
        return (
            // 左端の X 座標
            b.GetProperty("left").GetInt32(),
            // 上端の Y 座標
            b.GetProperty("top").GetInt32(),
            // 外形の幅
            b.GetProperty("width").GetInt32(),
            // 外形の高さ
            b.GetProperty("height").GetInt32());
    } // GetWindowBoundsAsync ここまで

    /// <summary>ウィンドウを最大化する。</summary>
    public static async Task MaximizeAsync(IPage page)
    {
        // 状態を maximized にする
        await SetWindowStateAsync(page, "maximized");
    } // MaximizeAsync ここまで

    /// <summary>ウィンドウを最小化する。</summary>
    public static async Task MinimizeAsync(IPage page)
    {
        // 状態を minimized にする
        await SetWindowStateAsync(page, "minimized");
    } // MinimizeAsync ここまで

    /// <summary>ウィンドウを全画面(フルスクリーン)にする。</summary>
    public static async Task FullscreenAsync(IPage page)
    {
        // 状態を fullscreen にする
        await SetWindowStateAsync(page, "fullscreen");
    } // FullscreenAsync ここまで

    /// <summary>ウィンドウを通常状態に戻す（最大化/最小化/全画面の解除）。</summary>
    public static async Task RestoreAsync(IPage page)
    {
        // 状態を normal にする
        await SetWindowStateAsync(page, "normal");
    } // RestoreAsync ここまで

    /// <summary>Page が属するブラウザウィンドウの現在の状態を返す（normal/minimized/maximized/fullscreen）。</summary>
    public static async Task<string> GetWindowStateAsync(IPage page)
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
        string? state = result.Value.GetProperty("bounds").GetProperty("windowState").GetString();
        // null の場合は既定値 normal とみなす
        if (state == null)
        {
            // 既定の状態名を返す
            return "normal";
        }
        // 取得できた状態名を返す
        return state;
    } // GetWindowStateAsync ここまで

    /// <summary>
    /// windowState を変更する。CDP は非normal状態どうしの直接遷移を許さないため、
    /// 目的が非normalで現在も非normalの場合は一度 normal を経由する。
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

    /// <summary>windowState 単独で Browser.setWindowBounds を送る（非normal状態指定時の必須形式）。</summary>
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

    /// <summary>対象セッションのウィンドウ windowId を取得する。</summary>
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
} // WindowSizer ここまで

/// <summary>
/// 固定 viewport で起動した headed ブラウザで、ウィンドウサイズ・表示状態の変更後に
/// 「実際に見えている HTML 領域」へ viewport を追随させるヘルパー。
///
/// 背景：固定 viewport（NoViewport ではない）だと CDP の Emulation.setDeviceMetricsOverride が効き、
///       ウィンドウを広げても HTML は override サイズのまま＝グレー余白が出る。window.innerWidth も
///       override 値を返すので実コンテンツ領域を直接測れない。
/// 仕組み：window.outerWidth/outerHeight(JS, override非依存) を外形基準にし、表示状態ごとに実測した
///        クロム量を引いて content を算出し SetViewportSizeAsync する。クロム量はブラウザ単位で共有キャッシュ。
/// </summary>
public sealed class ViewportFollower
{
    // クロム量はウィンドウ(=ブラウザ)の性質で page には依らないため、ブラウザ単位で共有キャッシュする。
    // 同一ブラウザの全 page/タブ/follower で状態ごと1回だけ実測し、別ブラウザは別途実測する。
    // ConditionalWeakTable によりブラウザ破棄時に自動解放される。値: 状態 -> 実クロム量(DIP)。
    private static readonly ConditionalWeakTable<IBrowser, Dictionary<string, (int W, int H)>> _chromeCache = new();
    // 共有キャッシュへの読み書きを直列化するためのロックオブジェクト
    private static readonly object _gate = new();

    /// <summary>normal 状態のクロム量を事前計測しておく（任意）。呼ばなくても Follow 時に計測する。</summary>
    public async Task CalibrateAsync(IPage page)
    {
        // normal 状態のクロム量を計測してキャッシュに載せる
        await EnsureChromeAsync(page, "normal");
    } // CalibrateAsync ここまで

    /// <summary>指定 page が属するブラウザの計測済みクロム量を破棄する（次回 Follow 時に測り直す）。</summary>
    public void ResetCalibration(IPage page)
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

    /// <summary>全ブラウザの計測済みクロム量を破棄する。</summary>
    public static void ResetAllCalibration()
    {
        // 共有キャッシュを全消去する
        lock (_gate)
        {
            // すべてのブラウザ分を除去する
            _chromeCache.Clear();
        } // lock ここまで
    } // ResetAllCalibration ここまで

    /// <summary>
    /// ウィンドウサイズ／表示状態を変更した後に呼ぶ。現在の状態の実クロム量を使って
    /// 実コンテンツ領域を算出し、viewport に設定して追随させる。
    /// </summary>
    public async Task FollowAsync(IPage page)
    {
        // 現在のウィンドウ状態を取得する
        string state = await WindowSizer.GetWindowStateAsync(page);
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
        // そこで CDP(WindowSizer) で状態/外形を測定値へ再適用して固定する。override 値は変わらないので
        // 外形が測定値に戻れば override(=実コンテンツ領域)と一致して埋まる。
        if (state == "maximized")
        {
            // 最大化状態を再適用する（状態を保ったまま埋める）
            await WindowSizer.MaximizeAsync(page);
        }
        else if (state == "fullscreen")
        {
            // 全画面状態を再適用する（状態を保ったまま埋める）
            await WindowSizer.FullscreenAsync(page);
        }
        else
        {
            // normal 状態（通常窓・ポップアップ）は外形を測定値へ固定し直す（窓の暴走を抑える）
            await WindowSizer.SetWindowSizeAsync(page, outerW, outerH);
        } // 状態/外形の再適用ここまで
    } // FollowAsync ここまで

    // ---- ウィンドウ操作 + 追随 を1呼び出しで行う便利メソッド ----
    // （外部 Win32 でリサイズする場合は、リサイズ後に FollowAsync を直接呼ぶこと）

    /// <summary>ウィンドウ外形サイズ(DIP)を変更し、続けて viewport を追随させる。</summary>
    public async Task SetWindowSizeAsync(IPage page, int width, int height)
    {
        // 外形サイズを変更する
        await WindowSizer.SetWindowSizeAsync(page, width, height);
        // 変更後のサイズに viewport を追随させる
        await FollowAsync(page);
    } // SetWindowSizeAsync ここまで

    /// <summary>ウィンドウを最大化し、viewport を追随させる。</summary>
    public async Task MaximizeAsync(IPage page)
    {
        // ウィンドウを最大化する
        await WindowSizer.MaximizeAsync(page);
        // 最大化後の領域に viewport を追随させる
        await FollowAsync(page);
    } // MaximizeAsync ここまで

    /// <summary>ウィンドウを全画面にし、viewport を追随させる。</summary>
    public async Task FullscreenAsync(IPage page)
    {
        // ウィンドウを全画面にする
        await WindowSizer.FullscreenAsync(page);
        // 全画面後の領域に viewport を追随させる
        await FollowAsync(page);
    } // FullscreenAsync ここまで

    /// <summary>ウィンドウを通常状態に戻し、viewport を追随させる。</summary>
    public async Task RestoreAsync(IPage page)
    {
        // ウィンドウを通常状態に戻す
        await WindowSizer.RestoreAsync(page);
        // 通常化後の領域に viewport を追随させる
        await FollowAsync(page);
    } // RestoreAsync ここまで

    /// <summary>対象状態のクロム量を返す（ブラウザ×種別×状態 単位で未計測なら計測してキャッシュ）。</summary>
    private async Task<(int W, int H)> EnsureChromeAsync(IPage page, string state)
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
    /// アドレスバー(locationbar)の有無で判定する。override 下でも正しく取得できる。
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

    /// <summary>ブラウザ単位のクロム量キャッシュを取得する（無ければ作成）。</summary>
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
    /// kind が "popup" のときはツールバー無しポップアップを開いて計測する（ベストエフォート）。
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
            // override を無効化して実ウィンドウに追随させる
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
                await WindowSizer.MaximizeAsync(probe);
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

    /// <summary>
    /// ウィンドウ外形が安定する（2回連続で同値になる）まで待って返す。
    /// 状態遷移（最大化/全画面の解除など）直後の過渡的なサイズを避けるため。
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

    /// <summary>実際に見えているウィンドウ外形(DIP)を JS から取得する（override 非依存）。</summary>
    private static async Task<(int Width, int Height)> GetOuterAsync(IPage page)
    {
        // window.outerWidth/outerHeight を取得する
        var o = await page.EvaluateAsync<double[]>("() => [window.outerWidth, window.outerHeight]");
        // 整数(DIP)に丸めてタプルで返す
        return ((int)Math.Round(o[0]), (int)Math.Round(o[1]));
    } // GetOuterAsync ここまで
} // ViewportFollower ここまで
