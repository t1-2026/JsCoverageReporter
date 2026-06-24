using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace JsCoverageReporter;

/// <summary>
/// Page が属するブラウザウィンドウの外形サイズ・位置を CDP(Browser.setWindowBounds) で変更するヘルパー。
///
/// 設定対象は「ウィンドウ外形」（タイトルバー＋枠＋タブ/アドレスバー込み）であり、
/// HTML 表示領域(content)ではない点に注意。content を狙った大きさにしたい場合は
/// 外形 = content + クロム量 で計算するか、本メソッドの後に <see cref="ViewportFollower"/> を使う。
///
/// 単位は DIP(=CSS px)。Chromium 系専用（Chrome / Edge / bundled Chromium で動作確認済み）。
/// Firefox / WebKit では使用不可。
/// </summary>
public static class WindowSizer
{
    /// <summary>Page が属するブラウザウィンドウの外形サイズ(DIP)を指定値に変更する。</summary>
    public static async Task SetWindowSizeAsync(IPage page, int width, int height)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        int windowId = await GetWindowIdAsync(cdp);

        // 最大化/最小化中は幅・高さが効かないので normal に戻しつつサイズ指定する
        await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
        {
            ["windowId"] = windowId,
            ["bounds"] = new Dictionary<string, object>
            {
                ["windowState"] = "normal",
                ["width"] = width,
                ["height"] = height,
            }
        });
    }

    /// <summary>Page が属するブラウザウィンドウの位置(左上座標, DIP)を変更する。</summary>
    public static async Task SetWindowPositionAsync(IPage page, int left, int top)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        int windowId = await GetWindowIdAsync(cdp);

        await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
        {
            ["windowId"] = windowId,
            ["bounds"] = new Dictionary<string, object>
            {
                ["windowState"] = "normal",
                ["left"] = left,
                ["top"] = top,
            }
        });
    }

    /// <summary>Page が属するブラウザウィンドウの現在の外形(DIP)を取得する。</summary>
    public static async Task<(int Left, int Top, int Width, int Height)> GetWindowBoundsAsync(IPage page)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var result = await cdp.SendAsync("Browser.getWindowForTarget");
        if (result == null)
            throw new InvalidOperationException("Browser.getWindowForTarget が null を返しました。");
        var b = result.Value.GetProperty("bounds");
        return (
            b.GetProperty("left").GetInt32(),
            b.GetProperty("top").GetInt32(),
            b.GetProperty("width").GetInt32(),
            b.GetProperty("height").GetInt32());
    }

    private static async Task<int> GetWindowIdAsync(ICDPSession cdp)
    {
        var result = await cdp.SendAsync("Browser.getWindowForTarget");
        if (result == null)
            throw new InvalidOperationException("Browser.getWindowForTarget が null を返しました。");
        return result.Value.GetProperty("windowId").GetInt32();
    }
}
