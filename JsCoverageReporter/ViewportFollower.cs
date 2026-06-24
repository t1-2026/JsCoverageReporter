using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace JsCoverageReporter;

/// <summary>
/// Playwright(headed) が起動したブラウザのウィンドウサイズ・表示状態を変更した後、
/// 「実際に見えている HTML 領域」に viewport を追随させるためのヘルパー。
///
/// 背景：
///   - 起動時に固定 viewport を指定している（ViewportSize.NoViewport ではない）と
///     CDP の Emulation.setDeviceMetricsOverride が効き、HTML は override されたサイズのまま描画される。
///     ウィンドウを広げても増えた分はグレーの余白になり、window.innerWidth/innerHeight も
///     override 値を返すため実コンテンツ領域を直接測れない。
///
/// 仕組み：
///   1. window.outerWidth/outerHeight(JS) は override の影響を受けず、最大化時の画面外はみ出しも
///      除いた「実際に見えている外形(DIP)」を返す。これを外形の基準にする。
///   2. クロム量(タブ/アドレスバー＋枠) は表示状態ごとに異なる（normal/maximized/fullscreen）。
///      同一ブラウザに NoViewport の一時ウィンドウを開くと innerWidth/innerHeight が実コンテンツになるので、
///        chrome = outer - inner
///      で各状態の実クロム量を測る（状態ごとに一度だけ計測してキャッシュ。fullscreen は常に 0）。
///   3. FollowAsync では現在の状態のクロム量を使い
///        content = outer - chrome
///      を viewport に設定する。
///
/// 使い方：
/// <code>
///   var follower = new ViewportFollower();
///
///   // (A) 外部 Win32 でリサイズした場合：リサイズ後に Follow を呼ぶ
///   //     SetWindowPos(hwnd, ...);
///   await follower.FollowAsync(page);
///
///   // (B) ウィンドウ操作と追随を一括で行う便利メソッド
///   await follower.SetWindowSizeAsync(page, 1600, 1000); // サイズ変更＋追随
///   await follower.MaximizeAsync(page);                  // 最大化＋追随（最大化状態を保持して埋める）
///   await follower.FullscreenAsync(page);                // 全画面＋追随
///   await follower.RestoreAsync(page);                   // 通常化＋追随
/// </code>
///
/// 注意：
///   - SetViewportSizeAsync は windowState を normal に戻す副作用があるため、maximized/fullscreen では
///     override 設定後に状態を再適用して埋める。そのため最大化/全画面の Follow では一瞬通常化→再適用の
///     ちらつきが入る。
///   - クロム量の計測時に NoViewport の一時ウィンドウが一瞬開いて閉じる（状態ごとに初回のみ）。
///     maximized のクロム計測ではその一時ウィンドウが一度最大化される。
///   - クロム量はブラウザ単位で static 共有キャッシュする。同一ブラウザの全 page/タブ/follower で
///     状態ごとに1回だけ実測し、別ブラウザは別途実測する（複数ブラウザ運用に対応）。
///     ブラウザ破棄時にキャッシュは自動解放される（ConditionalWeakTable）。
///   - ブックマークバーの表示切替やブラウザのバージョンアップ等でクロム高さが変わったら
///     ResetCalibration(page)（または ResetAllCalibration()）を呼べば次回 Follow 時に測り直す
///     （値はハードコードしていないので自動追従）。
///   - 最小化中(minimized)に FollowAsync を呼んだ場合は何もしない。
///   - 前提: 全ウィンドウが同一 DPI・同一ツールバー構成であること（異なる DPI モニタを跨ぐ運用は対象外）。
///   - 単位は DIP(=CSS px)。Chromium 系専用。
/// </summary>
public sealed class ViewportFollower
{
    // クロム量はウィンドウ(=ブラウザ)の性質であり page には依らない。よってブラウザ単位で
    // 共有キャッシュする（同一ブラウザの全 page/タブ/follower で状態ごとに1回だけ実測。
    // 別ブラウザは別途実測）。ConditionalWeakTable によりブラウザ破棄時に自動解放される。
    // 値: 表示状態 -> 実クロム量(DIP)。fullscreen は常に (0,0) なので格納しない。
    private static readonly ConditionalWeakTable<IBrowser, Dictionary<string, (int W, int H)>> _chromeCache = new();
    private static readonly object _gate = new();

    /// <summary>
    /// normal 状態のクロム量を事前計測しておく（任意）。呼ばなくても FollowAsync が必要時に計測する。
    /// </summary>
    public async Task CalibrateAsync(IPage page)
    {
        await EnsureChromeAsync(page, "normal");
    }

    /// <summary>指定 page が属するブラウザの計測済みクロム量を破棄する（次回 Follow 時に測り直す）。</summary>
    public void ResetCalibration(IPage page)
    {
        var browser = page.Context.Browser;
        if (browser == null)
            return;
        lock (_gate)
            _chromeCache.Remove(browser);
    }

    /// <summary>全ブラウザの計測済みクロム量を破棄する。</summary>
    public static void ResetAllCalibration()
    {
        lock (_gate)
            _chromeCache.Clear();
    }

    /// <summary>
    /// ウィンドウサイズ／表示状態を変更した後に呼ぶ。現在の状態の実クロム量を使って
    /// 実コンテンツ領域を算出し、viewport に設定して追随させる。
    /// </summary>
    public async Task FollowAsync(IPage page)
    {
        string state = await WindowSizer.GetWindowStateAsync(page);
        if (state == "minimized")
            return; // 最小化中はサイズが意味を持たないので何もしない

        var chrome = await EnsureChromeAsync(page, state);
        // 状態遷移(特に fullscreen 解除)直後は外形が確定するまで時間がかかるため、安定を待つ
        var (outerW, outerH) = await WaitForStableOuterAsync(page);

        int cssW = Math.Max(1, outerW - chrome.W);
        int cssH = Math.Max(1, outerH - chrome.H);
        await page.SetViewportSizeAsync(cssW, cssH);

        // SetViewportSizeAsync は windowState を normal に戻し、ウィンドウを縮める。
        // 最大化/全画面では override 設定後に状態を再適用すると、状態を保ったまま
        // ウィンドウが再び広がり override(=コンテンツ領域)と一致して埋まる。
        if (state == "maximized")
            await WindowSizer.MaximizeAsync(page);
        else if (state == "fullscreen")
            await WindowSizer.FullscreenAsync(page);
    }

    // ---- ウィンドウ操作 + 追随 を1呼び出しで行う便利メソッド ----
    // （外部 Win32 でリサイズする場合は、リサイズ後に FollowAsync を直接呼ぶこと）

    /// <summary>ウィンドウ外形サイズ(DIP)を変更し、続けて viewport を追随させる。</summary>
    public async Task SetWindowSizeAsync(IPage page, int width, int height)
    {
        await WindowSizer.SetWindowSizeAsync(page, width, height);
        await FollowAsync(page);
    }

    /// <summary>ウィンドウを最大化し、viewport を追随させる。</summary>
    public async Task MaximizeAsync(IPage page)
    {
        await WindowSizer.MaximizeAsync(page);
        await FollowAsync(page);
    }

    /// <summary>ウィンドウを全画面にし、viewport を追随させる。</summary>
    public async Task FullscreenAsync(IPage page)
    {
        await WindowSizer.FullscreenAsync(page);
        await FollowAsync(page);
    }

    /// <summary>ウィンドウを通常状態に戻し、viewport を追随させる。</summary>
    public async Task RestoreAsync(IPage page)
    {
        await WindowSizer.RestoreAsync(page);
        await FollowAsync(page);
    }

    /// <summary>対象状態のクロム量を返す（ブラウザ単位で未計測なら計測してキャッシュ）。</summary>
    private async Task<(int W, int H)> EnsureChromeAsync(IPage page, string state)
    {
        if (state == "fullscreen")
            return (0, 0); // 全画面はクロムが無い

        var browser = page.Context.Browser;
        if (browser == null)
            throw new InvalidOperationException("Browser を取得できません（Connect 経由などで未公開）。");
        var cache = GetBrowserCache(browser);

        lock (_gate)
        {
            if (cache.TryGetValue(state, out var cached))
                return cached;
        }

        // 実測は await を伴うので lock の外で行う（同時実行で二重計測しても結果は同じ＝無害）。
        var measured = await ProbeChromeAsync(page, state);

        lock (_gate)
        {
            if (cache.TryGetValue(state, out var existing))
                return existing; // 競合した場合は先に格納された値を優先
            cache[state] = measured;
            return measured;
        }
    }

    /// <summary>ブラウザ単位のクロム量キャッシュを取得（無ければ作成）。</summary>
    private static Dictionary<string, (int W, int H)> GetBrowserCache(IBrowser browser)
    {
        lock (_gate)
        {
            if (!_chromeCache.TryGetValue(browser, out var dict))
            {
                dict = new Dictionary<string, (int W, int H)>();
                _chromeCache.Add(browser, dict);
            }
            return dict;
        }
    }

    /// <summary>
    /// 同一ブラウザに NoViewport の一時ウィンドウを開き、対象状態の実クロム量(outer - inner)を計測する。
    /// </summary>
    private static async Task<(int W, int H)> ProbeChromeAsync(IPage page, string state)
    {
        var browser = page.Context.Browser;
        if (browser == null)
            throw new InvalidOperationException("Browser を取得できません（Connect 経由などで未公開）。");

        var probeContext = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = ViewportSize.NoViewport,
        });
        try
        {
            var probe = await probeContext.NewPageAsync();
            await probe.GotoAsync("about:blank");

            if (state == "maximized")
                await WindowSizer.MaximizeAsync(probe);
            await probe.WaitForTimeoutAsync(300);

            var m = await probe.EvaluateAsync<double[]>(
                "() => [window.outerWidth - window.innerWidth, window.outerHeight - window.innerHeight]");
            return ((int)Math.Round(m[0]), (int)Math.Round(m[1]));
        }
        finally
        {
            await probeContext.CloseAsync();
        }
    }

    /// <summary>実際に見えているウィンドウ外形(DIP)を JS から取得する（override 非依存）。</summary>
    private static async Task<(int Width, int Height)> GetOuterAsync(IPage page)
    {
        var o = await page.EvaluateAsync<double[]>("() => [window.outerWidth, window.outerHeight]");
        return ((int)Math.Round(o[0]), (int)Math.Round(o[1]));
    }

    /// <summary>
    /// ウィンドウ外形が安定する（2回連続で同値になる）まで待って返す。
    /// 状態遷移（最大化/全画面の解除など）直後の過渡的なサイズを避けるため。
    /// </summary>
    private static async Task<(int Width, int Height)> WaitForStableOuterAsync(IPage page)
    {
        const int maxAttempts = 20;   // 約 20 * 80ms = 1.6s 上限
        const int intervalMs = 80;

        var prev = await GetOuterAsync(page);
        for (int i = 0; i < maxAttempts; i++)
        {
            await page.WaitForTimeoutAsync(intervalMs);
            var cur = await GetOuterAsync(page);
            if (cur.Width == prev.Width && cur.Height == prev.Height)
                return cur;
            prev = cur;
        }
        return prev;
    }
}
