using System;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace JsCoverageReporter;

/// <summary>
/// Playwright(headed) が起動したブラウザのウィンドウサイズを Win32 API で変更した後、
/// 変更後の表示領域に合わせて viewport を追随させるためのヘルパー。
///
/// 背景：
///   - 起動時に固定 viewport を指定している（ViewportSize.NoViewport ではない）と
///     CDP の Emulation.setDeviceMetricsOverride が効き、HTML は override されたサイズのまま描画される。
///     Win32 でウィンドウを広げても増えた分はグレーの余白になり、
///     window.innerWidth/innerHeight も override 値を返すため実ウィンドウサイズを測れない。
///
/// 仕組み（クロム高さ・スケール・DPI の計算は一切不要）：
///   1. CDP で Emulation.clearDeviceMetricsOverride を送って override を解除し、
///      実ウィンドウのコンテンツ領域に合わせて再レイアウトさせる。
///   2. その状態の window.innerWidth/innerHeight を測る。これが「実際に見えている HTML 領域」そのもの。
///   3. その値で SetViewportSizeAsync を呼び、override を測定値ぴったりに張り直す。
///      測定値＝現在のコンテンツ領域なので、ウィンドウサイズは Win32 指定値のまま動かない。
///
/// 使い方：
/// <code>
///   var follower = new ViewportFollower();
///
///   // 1) Win32 API でウィンドウサイズを変更する
///   SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 1600, 1000, SWP_NOMOVE | SWP_NOZORDER);
///
///   // 2) リサイズ後に呼ぶと viewport が新しい表示領域に追随する（hwnd は不要）
///   await follower.FollowAsync(page);
/// </code>
///
/// 注意：
///   - override を一旦解除して測るため、クロム高さ・スクロールバー幅・DPI スケールに依存せず正確。
///     ブックマークバーの表示切替・F11 全画面・別DPIモニタへの移動が混ざっても、その都度測り直すので問題ない。
///   - Chromium 専用（CDP を使用）。
/// </summary>
public sealed class ViewportFollower
{
    /// <summary>
    /// Win32 でウィンドウサイズを変更した後に呼ぶ。override を解除して実コンテンツ領域を測り、
    /// その値で viewport を張り直して追随させる。
    /// </summary>
    public async Task FollowAsync(IPage page)
    {
        // override を解除して、実ウィンドウのコンテンツ領域に合わせて再レイアウトさせる
        var cdp = await page.Context.NewCDPSessionAsync(page);
        try
        {
            await cdp.SendAsync("Emulation.clearDeviceMetricsOverride");

            // override 解除後の innerWidth/innerHeight＝実際に見えている HTML 領域(CSS px)。
            // EvaluateAsync<T> の名前マッチを避けるため [幅, 高さ] の配列で受け取る。
            var size = await page.EvaluateAsync<double[]>(
                "() => [window.innerWidth, window.innerHeight]");
            int cssW = (int)Math.Round(size[0]);
            int cssH = (int)Math.Round(size[1]);

            // 測定値ぴったりに override を張り直す（ウィンドウサイズは変わらない）
            await page.SetViewportSizeAsync(Math.Max(1, cssW), Math.Max(1, cssH));
        }
        finally
        {
            await cdp.DetachAsync();
        }
    }
}
