using System;
using System.Collections.Generic;
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
///   - override を別 CDP セッションから clearDeviceMetricsOverride しても解除できない（検証済み）ため、
///     override に依存しないウィンドウ外形から逆算する。
///
/// 仕組み：
///   1. CDP の Browser.getWindowForTarget はウィンドウ外形を DIP(=CSS px) 単位で返す。これは override の影響を受けない。
///   2. 起動直後は Playwright がウィンドウを viewport ぴったり(無余白)に合わせているので、
///        chrome = outer - viewport
///      でブラウザのクロム量(タブ/アドレスバー＋枠, DIP)を確定できる（CalibrateAsync）。
///   3. Win32 でリサイズした後は
///        content = outer - chrome
///      を viewport に設定すればウィンドウいっぱいに追随する（FollowAsync）。
///
/// 使い方：
/// <code>
///   var follower = new ViewportFollower();
///
///   // 1) 起動・初期表示の直後（まだ Win32 リサイズしていない無余白の状態）で1回だけ校正する
///   await follower.CalibrateAsync(page);
///
///   // 2) Win32 API でウィンドウサイズを変更する
///   SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 1600, 1000, SWP_NOMOVE | SWP_NOZORDER);
///
///   // 3) リサイズ後に呼ぶと viewport が新しい表示領域に追随する
///   await follower.FollowAsync(page);
/// </code>
///
/// 注意：
///   - CalibrateAsync は「ウィンドウに余白が無い状態」で呼ぶこと（＝Win32 リサイズ前）。
///     余白がある状態で校正するとクロム量を過大に見積もり、以降の高さが足りなくなる。
///   - クロム量が変わる操作（ブックマークバー表示切替・F11 全画面・別DPIモニタへの移動）の後は
///     CalibrateAsync を呼び直すこと。
///   - Browser.getWindowForTarget の bounds は DIP 前提。実機の DPI スケールでズレる場合は教えてください。
///   - Chromium 専用（CDP を使用）。
/// </summary>
public sealed class ViewportFollower
{
    private ICDPSession? _cdp;
    private int _chromeW = -1;  // ウィンドウ外形 - viewport（横, DIP）
    private int _chromeH = -1;  // ウィンドウ外形 - viewport（縦, DIP）

    /// <summary>
    /// 起動・初期表示の直後（Win32 リサイズ前の無余白状態）に1回だけ呼ぶ。
    /// 現在のウィンドウ外形と viewport からクロム量を確定する。
    /// クロム構成が変わった場合（ブックマークバー切替・全画面・別DPIモニタ移動など）は呼び直すこと。
    /// </summary>
    public async Task CalibrateAsync(IPage page)
    {
        _cdp ??= await page.Context.NewCDPSessionAsync(page);

        var vp = page.ViewportSize
            ?? throw new InvalidOperationException("viewport が未設定です（NoViewport ではこのヘルパーは不要）。");
        var (outerW, outerH) = await GetOuterAsync(_cdp);

        _chromeW = outerW - vp.Width;
        _chromeH = outerH - vp.Height;
    }

    /// <summary>
    /// Win32 でウィンドウサイズを変更した後に呼ぶ。ウィンドウ外形からクロム量を引いた
    /// 実コンテンツ領域を viewport に設定して追随させる。事前に CalibrateAsync が必要。
    /// </summary>
    public async Task FollowAsync(IPage page)
    {
        if (_chromeH < 0)
            throw new InvalidOperationException("先に CalibrateAsync を呼んでください。");

        _cdp ??= await page.Context.NewCDPSessionAsync(page);

        var (outerW, outerH) = await GetOuterAsync(_cdp);
        int cssW = Math.Max(1, outerW - _chromeW);
        int cssH = Math.Max(1, outerH - _chromeH);

        await page.SetViewportSizeAsync(cssW, cssH);
    }

    /// <summary>CDP からウィンドウ外形(DIP)を取得する。</summary>
    private static async Task<(int Width, int Height)> GetOuterAsync(ICDPSession cdp)
    {
        var result = await cdp.SendAsync("Browser.getWindowForTarget")
            ?? throw new InvalidOperationException("Browser.getWindowForTarget が null を返しました。");
        var bounds = result.GetProperty("bounds");
        return (bounds.GetProperty("width").GetInt32(), bounds.GetProperty("height").GetInt32());
    }
}
