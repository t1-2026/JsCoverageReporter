using System;
using System.Collections.Generic;
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
///   await follower.CalibrateAsync(page);     // 任意。呼ばなくても Follow 時に自動計測される
///
///   await WindowSizer.SetWindowSizeAsync(page, 1600, 1000);  // または Win32 / 最大化 / 全画面
///   await follower.FollowAsync(page);        // 現在の状態に合わせて viewport を追随
/// </code>
///
/// 注意：
///   - クロム量の計測時に NoViewport の一時ウィンドウが一瞬開いて閉じる（状態ごとに初回のみ）。
///     maximized のクロム計測ではその一時ウィンドウが一度最大化される。
///   - ブックマークバーの表示切替など恒久的にクロム高さが変わったら ResetCalibration() で測り直させる。
///   - 最小化中(minimized)に FollowAsync を呼んだ場合は何もしない。
///   - 単位は DIP(=CSS px)。Chromium 系専用。
/// </summary>
public sealed class ViewportFollower
{
    // 表示状態 -> 実クロム量(DIP)。fullscreen は常に (0,0) なので計測しない。
    private readonly Dictionary<string, (int W, int H)> _chromeByState = new();

    /// <summary>
    /// normal 状態のクロム量を事前計測しておく（任意）。呼ばなくても FollowAsync が必要時に計測する。
    /// </summary>
    public async Task CalibrateAsync(IPage page)
    {
        await EnsureChromeAsync(page, "normal");
    }

    /// <summary>計測済みクロム量を破棄する。クロム構成（ブックマークバー等）が変わった後に呼ぶ。</summary>
    public void ResetCalibration() => _chromeByState.Clear();

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
        var (outerW, outerH) = await GetOuterAsync(page);

        int cssW = Math.Max(1, outerW - chrome.W);
        int cssH = Math.Max(1, outerH - chrome.H);
        await page.SetViewportSizeAsync(cssW, cssH);
    }

    /// <summary>対象状態のクロム量を返す（未計測なら計測してキャッシュ）。</summary>
    private async Task<(int W, int H)> EnsureChromeAsync(IPage page, string state)
    {
        if (state == "fullscreen")
            return (0, 0); // 全画面はクロムが無い

        if (_chromeByState.TryGetValue(state, out var cached))
            return cached;

        var measured = await ProbeChromeAsync(page, state);
        _chromeByState[state] = measured;
        return measured;
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
}
