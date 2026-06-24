using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace JsCoverageReporter;

/// <summary>
/// Playwright(headed) が起動したブラウザのウィンドウサイズを Win32 API で変更した後、
/// 変更後の表示領域に合わせて viewport を追随させるためのヘルパー。
///
/// 前提：
///   - 起動時に固定 viewport を指定している（ViewportSize.NoViewport ではない）。
///     このとき CDP の Emulation.setDeviceMetricsOverride が効いているため、
///     Win32 でウィンドウを広げても HTML は override されたサイズのまま描画され、
///     window.innerWidth/innerHeight も override 値を返す（実窓サイズを測れない）。
///   - そこで OS 側のクライアント領域(物理px)を測り → CSS px に変換 → SetViewportSizeAsync で追随させる。
///
/// 仕組み：
///   横方向にはクロム(タブ/アドレスバー)が無いことを利用して
///     scale   = clientPxW / innerWidth        … 物理px ↔ CSS px の変換係数
///     chromePx = clientPxH - scale * innerHeight … クロム高さ(物理px)
///   を1回のキャリブレーションで定数化する。以後はリサイズ後に計算だけで viewport を出せる。
///
/// 使い方：
/// <code>
///   var follower = new ViewportFollower();
///
///   // 1) Win32 でリサイズする前に、現在の状態で1回だけ校正する
///   await follower.CalibrateAsync(page, hwnd);
///
///   // 2) Win32 API でウィンドウサイズを変更する
///   SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 1600, 1000, SWP_NOMOVE | SWP_NOZORDER);
///
///   // 3) リサイズ後に呼ぶと viewport が新しい表示領域に追随する
///   await follower.FollowAsync(page, hwnd);
/// </code>
///
/// 注意：
///   - headed では SetViewportSizeAsync がウィンドウもリサイズしようとするが、
///     本クラスは「いまの Win32 窓の中身ぴったり」になる CSS サイズを出すため、
///     結果のウィンドウサイズは Win32 指定値と一致し暴れない。
///   - クロム高さが途中で変わらないことが前提。ブックマークバーの表示切替・F11 全画面・
///     拡張機能のバー追加・別DPIモニタへの移動が起きたら CalibrateAsync を再実行すること。
///   - GetClientRect は物理ピクセル基準。アプリマニフェストで Per-Monitor DPI Aware にしておくとズレない。
/// </summary>
public sealed class ViewportFollower
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    private record Inner(double Iw, double Ih);

    private double _scale = 0;     // 物理px / CSS px
    private double _chromePx = 0;  // クロム高さ（物理px）

    /// <summary>
    /// Win32 リサイズ前に1度だけ呼ぶ。現在の viewport と窓から変換係数・クロム高さを校正する。
    /// クロム構成が変わった場合（ブックマークバー切替・全画面・別DPIモニタ移動など）は再度呼ぶこと。
    /// </summary>
    public async Task CalibrateAsync(IPage page, IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var rc))
            throw new InvalidOperationException("GetClientRect failed.");

        int clientPxW = rc.Right - rc.Left;
        int clientPxH = rc.Bottom - rc.Top;

        // スクロールバーの影響を避けるため documentElement.clientWidth を使う
        var inner = await page.EvaluateAsync<Inner>(@"() => ({
            iw: document.documentElement.clientWidth,
            ih: window.innerHeight
        })");

        _scale    = clientPxW / inner.Iw;
        _chromePx = clientPxH - _scale * inner.Ih;
    }

    /// <summary>
    /// Win32 でウィンドウサイズを変更した後に呼ぶ。新しいクライアント領域から viewport を算出して追随させる。
    /// 事前に CalibrateAsync を呼んでおく必要がある。
    /// </summary>
    public async Task FollowAsync(IPage page, IntPtr hwnd)
    {
        if (_scale <= 0)
            throw new InvalidOperationException("先に CalibrateAsync を呼んでください。");

        if (!GetClientRect(hwnd, out var rc))
            throw new InvalidOperationException("GetClientRect failed.");

        int clientPxW = rc.Right - rc.Left;
        int clientPxH = rc.Bottom - rc.Top;

        int cssW = (int)Math.Round(clientPxW / _scale);
        int cssH = (int)Math.Round((clientPxH - _chromePx) / _scale);

        await page.SetViewportSizeAsync(cssW, Math.Max(1, cssH));
    }
}
