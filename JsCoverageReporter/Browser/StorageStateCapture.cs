// =============================================================================
// StorageStateCapture.cs
//
// 【概要】
//   インストール済みの Microsoft Edge を「毎回まっさらな一時状態」で起動し、
//   指定 URL を開いてユーザーに手動操作（ログイン等）をさせる。
//   ブラウザが閉じられた時点で、Cookie と localStorage を含む StorageState を
//   指定パスへ JSON 保存する。保存した JSON は Playwright 標準形式なので、
//   NewContextAsync の StorageStatePath にそのまま渡して再利用できる。
//
// 【使い方】
//   1) 取得（保存）する:
//        bool ok = await StorageStateCapture.OpenAndSaveStateOnCloseAsync(
//            "https://example.com/login",    // 開く URL
//            @"C:\work\state\example.json",   // 保存先パス
//            "SESSIONID",                     // 目的の Cookie 名（不要なら null）
//            "example.com");                  // 目的 Cookie のドメイン（不要なら null）
//      戻り値 true  : 目的 Cookie を含む状態を保存できた
//      戻り値 false : 目的 Cookie を観測できず、ファイルを書かなかった
//
//   2) 復元（再利用）する:
//        var ctx = await StorageStateCapture.NewContextFromStateAsync(
//            browser, @"C:\work\state\example.json");
//        // ctx は Cookie / localStorage 復元済み
//
//   3) 延命運用（任意）:
//        // 復元したコンテキストで操作したあと、終了前に保存し直すと、
//        // リフレッシュされた Cookie に更新され寿命が延びる。
//        await ctx.StorageStateAsync(new BrowserContextStorageStateOptions
//        {
//            Path = @"C:\work\state\example.json"
//        });
//
// 【制約】
//   - 保存物は「その時点のスナップショット」。認証が無効化されたら再ログイン＆再保存が必要。
//   - リフレッシュトークンが含まれていれば実質寿命は延びる（上記 3 の運用で延命可能）。
//   - ブラウザが閉じられるまでメソッドは戻らない（ハングは仕様として許容）。
//
// 【コーディング規約】
//   - ? は型注釈(string?)のみ可 / ?? と三項は使用しない / 中括弧 {} は省略しない /
//     { の後で改行する / 各行にコメントを付ける
// =============================================================================

using Microsoft.Playwright;            // Playwright 本体（ImplicitUsings に含まれないため明示）

namespace JsCoverageReporter.Browser;

// StorageState（Cookie＋localStorage）の取得と復元をまとめたクラス
public static class StorageStateCapture
{
    // -------------------------------------------------------------------------
    // インストール済み Edge を一時状態で開き、閉じられたら StorageState を保存する
    // -------------------------------------------------------------------------
    public static async Task<bool> OpenAndSaveStateOnCloseAsync(
        string url,                          // 開く URL
        string stateFilePath,                // StorageState の保存先（フルパス）
        string? requiredCookieName = null,   // 目的 Cookie 名（不要なら null）
        string? requiredCookieDomain = null) // 目的 Cookie のドメイン（不要なら null）
    {
        // 保存先を「ブラウザを開く前」に検証する（操作後の保存失敗を防ぐ）
        EnsureWritable(stateFilePath);

        // Playwright を初期化する
        using var playwright = await Playwright.CreateAsync();

        // 起動オプションを組み立てる
        var launchOptions = new BrowserTypeLaunchOptions();

        // インストール済みの Edge を使う
        launchOptions.Channel = "msedge";

        // 画面を表示してユーザー操作を可能にする
        launchOptions.Headless = false;

        // Edge を起動する（スコープを抜けたら自動破棄）
        await using var browser = await playwright.Chromium.LaunchAsync(launchOptions);

        // 永続プロファイルを使わないメモリ内コンテキストを作る（毎回まっさら）
        var context = await browser.NewContextAsync();

        // ページを1枚開く
        var page = await context.NewPageAsync();

        // 指定 URL へ遷移する
        await page.GotoAsync(url);

        // 切断（ユーザーがウィンドウを閉じた）を待つためのタスクを用意する
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 切断イベントでタスクを完了させる（継続は非同期で実行される）
        browser.Disconnected += (sender, args) =>
        {
            // 切断を通知する
            closed.TrySetResult();
        };

        // 採用済みの StorageState(JSON文字列)。未取得のうちは null
        string? lastGoodState = null;

        // 目的 Cookie を一度でも観測したかどうか
        bool found = false;

        // ブラウザが閉じられるまで繰り返す
        while (closed.Task.IsCompleted == false)
        {
            // 切断中に取得すると例外になり得るので try で囲う
            try
            {
                // gate 判定のため全ドメインの Cookie を取得する
                var cookies = await context.CookiesAsync();

                // 目的 Cookie が揃っているか判定する
                bool hasTarget = HasTargetCookie(cookies, requiredCookieName, requiredCookieDomain);

                // 揃っているときだけ state を採用する
                if (hasTarget)
                {
                    // Cookie＋localStorage を JSON 文字列としてスナップショットする
                    lastGoodState = await context.StorageStateAsync();

                    // 目的状態を観測したことを記録する
                    found = true;
                }
            }
            catch
            {
                // 既に切断済みとみなしてループを抜ける
                break;
            }

            // 0.5 秒待つか、閉じられたら即座に抜ける
            await Task.WhenAny(closed.Task, Task.Delay(500));
        }

        // 目的 Cookie を観測できず state も無い場合は、不完全な上書きを避ける
        if (found == false || lastGoodState == null)
        {
            // 取得失敗を警告し、ファイルは書かない
            Console.Error.WriteLine("[警告] 目的の Cookie '" + requiredCookieName + "' を観測できませんでした。保存しません: " + stateFilePath);

            // 失敗を呼び出し元へ通知する
            return false;
        }

        // 採用済みの state をそのままファイルへ書き出す
        File.WriteAllText(stateFilePath, lastGoodState);

        // 成功を呼び出し元へ通知する
        return true;
    }

    // -------------------------------------------------------------------------
    // 保存した StorageState を使ってログイン済みコンテキストを生成する
    // -------------------------------------------------------------------------
    public static async Task<IBrowserContext> NewContextFromStateAsync(IBrowser browser, string stateFilePath)
    {
        // コンテキスト生成オプションを用意する
        var options = new BrowserNewContextOptions();

        // 保存済み state ファイルのパスを指定する
        options.StorageStatePath = stateFilePath;

        // state を適用してコンテキストを生成する
        var context = await browser.NewContextAsync(options);

        // ログイン済みコンテキストを返す
        return context;
    }

    // -------------------------------------------------------------------------
    // 目的 Cookie（名前＋必要ならドメイン）が揃っているかを判定する
    // -------------------------------------------------------------------------
    private static bool HasTargetCookie(
        IReadOnlyList<BrowserContextCookiesResult> cookies,  // 取得済み Cookie 一覧
        string? requiredCookieName,                          // 目的 Cookie 名（null 可）
        string? requiredCookieDomain)                        // 目的 Cookie ドメイン（null 可）
    {
        // 目的 Cookie 名が指定されていない場合は常に成立とみなす
        if (requiredCookieName == null)
        {
            // 条件なしなので即 true
            return true;
        }

        // すべての Cookie を1つずつ確認する
        foreach (var cookie in cookies)
        {
            // 名前が一致しなければ次へ進む
            if (cookie.Name != requiredCookieName)
            {
                // 不一致なのでスキップ
                continue;
            }

            // 値が空なら未確定とみなして次へ進む
            if (string.IsNullOrEmpty(cookie.Value))
            {
                // 値が無いのでスキップ
                continue;
            }

            // ドメイン指定が無ければ、名前＋値が揃った時点で成立
            if (requiredCookieDomain == null)
            {
                // 条件を満たしたので true
                return true;
            }

            // 比較用に先頭ドットを除去した Cookie 側ドメインを作る
            var cookieDomain = cookie.Domain.TrimStart('.');

            // 比較用に先頭ドットを除去した期待ドメインを作る
            var wantDomain = requiredCookieDomain.TrimStart('.');

            // 末尾一致でドメインを照合する（サブドメインを許容）
            if (cookieDomain.EndsWith(wantDomain, StringComparison.OrdinalIgnoreCase))
            {
                // ドメインも一致したので true
                return true;
            }
        }

        // 最後まで見つからなければ false
        return false;
    }

    // -------------------------------------------------------------------------
    // 保存先フォルダを作成し、実際に書き込めるかを事前確認する
    // -------------------------------------------------------------------------
    private static void EnsureWritable(string path)
    {
        // 相対パスでも扱えるよう絶対パスへ正規化する
        var full = Path.GetFullPath(path);

        // 親フォルダのパスを取り出す
        var dir = Path.GetDirectoryName(full);

        // 親フォルダを特定できなければ例外にする
        if (string.IsNullOrEmpty(dir))
        {
            // 不正なパスとして通知する
            throw new ArgumentException("保存先のフォルダを特定できません: " + path);
        }

        // 親フォルダを作成する（既にあれば何もしない）
        Directory.CreateDirectory(dir);

        // 書き込みテスト用の一時ファイル名を作る
        var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N") + ".tmp");

        // 空ファイルを書いて書き込み権限を確認する
        File.WriteAllText(probe, "");

        // 確認用の一時ファイルを削除する
        File.Delete(probe);
    }
}
