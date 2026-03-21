using JsCoverageReporter.Config;
using Microsoft.Playwright;

namespace JsCoverageReporter.Actions;

/// <summary>
/// シナリオで定義されたアクションを順番に実行する静的クラス。
/// Playwright の IPage を通じてブラウザを操作する。
/// </summary>
internal static class ActionRunner
{
    /// <summary>
    /// アクションのリストをページに対して順番に実行する。
    /// </summary>
    /// <param name="page">操作対象のPlaywrightページ</param>
    /// <param name="actions">実行するアクションのコレクション</param>
    /// <param name="timeoutMs">各アクションのタイムアウト（ミリ秒）。null の場合は Playwright のデフォルト（30秒）を使用する</param>
    /// <param name="continueOnError">true にすると、アクションが失敗しても後続のアクションを続行する</param>
    internal static async Task RunAsync(IPage page, IEnumerable<ScenarioAction> actions, int? timeoutMs = null, bool continueOnError = false)
    {
        // すべてのアクションを順番に実行する
        foreach (var action in actions)
        {
            // continueOnError が true のときは Playwright の例外をキャッチして次へ進む
            try
            {

            // アクションの種類に応じて処理を分岐する
            switch (action.Type)
            {
                // 要素をクリックする
                case "click":
                    // selector が指定されていない場合は警告してスキップする
                    if (action.Selector is null)
                    {
                        Console.Error.WriteLine("[Warning] 'click' action missing 'selector' — skipping.");
                        break;
                    }
                    // 指定されたセレクターの要素をクリックする
                    await page.ClickAsync(action.Selector, new PageClickOptions { Timeout = timeoutMs });
                    break;

                // テキストボックスなどに文字を入力する
                case "fill":
                    // selector が指定されていない場合は警告してスキップする
                    if (action.Selector is null)
                    {
                        Console.Error.WriteLine("[Warning] 'fill' action missing 'selector' — skipping.");
                        break;
                    }
                    // 入力する文字列を決める
                    // value が指定されている場合はその値を使う
                    string fillValue;
                    if (action.Value == null)
                    {
                        // value が null（未指定）の場合は空文字を入力する
                        fillValue = "";
                    }
                    else
                    {
                        // value が指定されている場合はその値を使う
                        fillValue = action.Value;
                    }
                    // 決定した文字列を入力する
                    await page.FillAsync(action.Selector, fillValue, new PageFillOptions { Timeout = timeoutMs });
                    break;

                // 別のURLへ移動する
                case "navigate":
                    // url が指定されていない場合は警告してスキップする
                    if (action.Url is null)
                    {
                        Console.Error.WriteLine("[Warning] 'navigate' action missing 'url' — skipping.");
                        break;
                    }
                    // 指定されたURLへページを遷移する
                    await page.GotoAsync(action.Url, new PageGotoOptions { Timeout = timeoutMs });
                    break;

                // 指定した要素が画面に現れるまで待つ
                case "waitForSelector":
                    // selector が指定されていない場合は警告してスキップする
                    if (action.Selector is null)
                    {
                        Console.Error.WriteLine("[Warning] 'waitForSelector' action missing 'selector' — skipping.");
                        break;
                    }
                    // 指定されたセレクターの要素が DOM に現れるまで待機する
                    await page.WaitForSelectorAsync(action.Selector, new PageWaitForSelectorOptions { Timeout = timeoutMs });
                    break;

                // 要素の上にマウスを移動する（ホバー）
                case "hover":
                    // selector が指定されていない場合は警告してスキップする
                    if (action.Selector is null)
                    {
                        Console.Error.WriteLine("[Warning] 'hover' action missing 'selector' — skipping.");
                        break;
                    }
                    // 指定されたセレクターの要素にマウスカーソルを合わせる
                    await page.HoverAsync(action.Selector, new PageHoverOptions { Timeout = timeoutMs });
                    break;

                // キーを押す（例: Enter, Tab）
                case "press":
                    // selector が指定されていない場合は警告してスキップする
                    if (action.Selector is null)
                    {
                        Console.Error.WriteLine("[Warning] 'press' action missing 'selector' — skipping.");
                        break;
                    }
                    // 押すキーの名前を決める
                    string keyName;
                    if (action.Value == null)
                    {
                        // value が null（未指定）の場合はデフォルトで "Enter" キーを押す
                        keyName = "Enter";
                    }
                    else
                    {
                        // value が指定されている場合はその値をキー名として使う
                        keyName = action.Value;
                    }
                    // 決定したキーを押す
                    await page.PressAsync(action.Selector, keyName, new PagePressOptions { Timeout = timeoutMs });
                    break;

                // 指定したミリ秒だけ待機する
                case "wait":
                    // 待機するミリ秒数を決める
                    int delayMs;
                    if (action.Milliseconds == null)
                    {
                        // milliseconds が null（未指定）の場合は 0 ミリ秒（待機なし）とする
                        delayMs = 0;
                    }
                    else
                    {
                        // milliseconds が指定されている場合はその値を使う
                        delayMs = action.Milliseconds.Value;
                    }
                    // 決定したミリ秒だけ待機する
                    await Task.Delay(delayMs);
                    break;

                // 未知のアクション種別はスキップして警告する
                default:
                    Console.Error.WriteLine($"[Warning] Unknown action type '{action.Type}' — skipping.");
                    break;
            }

            } // try ブロックの終わり
            catch (PlaywrightException ex)
            {
                // continueOnError が false の場合は例外を再スローして処理を止める
                if (!continueOnError)
                {
                    throw;
                }
                // continueOnError が true の場合は警告を出して次のアクションに進む
                Console.Error.WriteLine($"[Warning] Action '{action.Type}' failed: {ex.Message} — continuing.");
            }
        }
    }
}
