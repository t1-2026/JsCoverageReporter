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
    internal static async Task RunAsync(IPage page, IEnumerable<ScenarioAction> actions, int? timeoutMs = null, bool continueOnError = false, Func<IPage, Task>? onBeforeClose = null)
    {
        if (actions == null)
        {
            return;
        }

        // すべてのアクションを順番に実行する
        foreach (var action in actions)
        {
            // リスト内の null 要素はスキップする
            if (action == null) { continue; }

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
                    // url が指定されていない、または空文字の場合は警告してスキップする
                    if (string.IsNullOrEmpty(action.Url))
                    {
                        Console.Error.WriteLine("[Warning] 'navigate' action missing or empty 'url' — skipping.");
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
                        // milliseconds が未指定の場合は警告して 0 ミリ秒（待機なし）とする
                        // ユーザーが意図せず何も待機しないシナリオを設定してしまうのを防ぐための警告
                        Console.Error.WriteLine("[Warning] 'wait' action missing 'milliseconds' — waiting 0ms (no-op).");
                        delayMs = 0;
                    }
                    else
                    {
                        // milliseconds が指定されている場合はその値を使う
                        // 負の待機時間が指定された場合は0にクランプして ArgumentOutOfRangeException を防ぐ
                        delayMs = Math.Max(0, action.Milliseconds.Value);
                    }
                    // 決定したミリ秒だけ待機する。タイムアウト機構に対応するため CancellationToken を使う
                    using (var cts = new CancellationTokenSource())
                    {
                        // timeoutMs が 0 の場合は Playwright では「無限待機」を意味するため、CancelAfter を呼ばない
                        if (timeoutMs.HasValue && timeoutMs.Value > 0)
                        {
                            cts.CancelAfter(timeoutMs.Value);
                        }
                        try
                        {
                            await Task.Delay(delayMs, cts.Token);
                        }
                        catch (TaskCanceledException)
                        {
                            throw new TimeoutException($"Action 'wait' timed out after {timeoutMs}ms.");
                        }
                    }
                    break;

                // ドロップダウンの選択肢を選ぶ
                case "select":
                    if (action.Selector is null)
                    {
                        Console.Error.WriteLine("[Warning] 'select' action missing 'selector' — skipping.");
                        break;
                    }
                    // value が null（未指定）の場合は選択肢が特定できないため警告してスキップする
                    if (action.Value is null)
                    {
                        Console.Error.WriteLine("[Warning] 'select' action missing 'value' — skipping.");
                        break;
                    }
                    await page.SelectOptionAsync(action.Selector, new[] { action.Value }, new PageSelectOptionOptions { Timeout = timeoutMs });
                    break;

                // チェックボックスやラジオボタンをオンにする
                case "check":
                    if (action.Selector is null)
                    {
                        Console.Error.WriteLine("[Warning] 'check' action missing 'selector' — skipping.");
                        break;
                    }
                    await page.CheckAsync(action.Selector, new PageCheckOptions { Timeout = timeoutMs });
                    break;

                // チェックボックスやラジオボタンをオフにする
                case "uncheck":
                    if (action.Selector is null)
                    {
                        Console.Error.WriteLine("[Warning] 'uncheck' action missing 'selector' — skipping.");
                        break;
                    }
                    await page.UncheckAsync(action.Selector, new PageUncheckOptions { Timeout = timeoutMs });
                    break;

                // 要素をダブルクリックする
                case "dblclick":
                    if (action.Selector is null)
                    {
                        Console.Error.WriteLine("[Warning] 'dblclick' action missing 'selector' — skipping.");
                        break;
                    }
                    await page.DblClickAsync(action.Selector, new PageDblClickOptions { Timeout = timeoutMs });
                    break;

                // タブ（ページ）を閉じる
                // 注意: page は RunAsync に渡された初期ページであり、新規タブ（window.open 等）は閉じられない
                case "close":
                {
                    // 閉じる前にコールバックを呼ぶ（カバレッジスナップショット取得などに使う）
                    // page.Close イベントは CDP セッション無効化後に発火するため、
                    // 閉じる前にスナップショットを取る必要がある
                    // コールバックが例外をスローしても CloseAsync は必ず呼ぶ（ページを開いたまま放置しない）
                    Exception? callbackException = null;
                    if (onBeforeClose != null)
                    {
                        try
                        {
                            await onBeforeClose(page);
                        }
                        catch (Exception cbEx)
                        {
                            // コールバック例外を保存して CloseAsync を続行する。
                            // CloseAsync 後に再スローすることで外側の continueOnError ハンドラーに委ねる。
                            callbackException = cbEx;
                            Console.Error.WriteLine($"[Warning] 'close' action: onBeforeClose failed: {cbEx.Message}");
                        }
                    }
                    await page.CloseAsync();
                    // ページが閉じられたため後続のアクションはすべて実行不可能。
                    // 直ちに返して残りのアクションをスキップする（continueOnError でも同様）。
                    // コールバック例外があれば再スローして外側の continueOnError ハンドラーに処理させる。
                    if (callbackException != null)
                    {
                        throw callbackException;
                    }
                    return;
                }

                // 要素またはページをスクロールする
                case "scroll":
                    // selector がない場合は x/y のデルタ量だけページをホイールスクロールする
                    if (action.Selector is null)
                    {
                        // X または Y が null の場合は 0 として扱う
                        int deltaX;
                        if (action.X == null)
                        {
                            deltaX = 0;
                        }
                        else
                        {
                            deltaX = action.X.Value;
                        }
                        int deltaY;
                        if (action.Y == null)
                        {
                            deltaY = 0;
                        }
                        else
                        {
                            deltaY = action.Y.Value;
                        }
                        // window.scrollBy を使うとヘッドレスモードでも確実にスクロール位置が変わる
                        // 引数をパラメータ化して渡す（文字列補間による潜在的な注入リスクを排除する）
                        await page.EvaluateAsync("([dx, dy]) => window.scrollBy(dx, dy)", new[] { deltaX, deltaY });
                    }
                    else
                    {
                        // selector が指定されている場合はその要素をビューポートにスクロールする
                        await page.Locator(action.Selector).ScrollIntoViewIfNeededAsync(
                            new LocatorScrollIntoViewIfNeededOptions { Timeout = timeoutMs });
                    }
                    break;

                // 未知のアクション種別はスキップして警告する
                default:
                    string actionTypeLabel;
                    if (action.Type == null) { actionTypeLabel = "(null)"; }
                    else if (action.Type == "") { actionTypeLabel = "(missing)"; }
                    else { actionTypeLabel = action.Type; }
                    Console.Error.WriteLine($"[Warning] Unknown action type '{actionTypeLabel}' — skipping.");
                    break;
            }

            } // try ブロックの終わり
            catch (Exception ex)
            {
                // OperationCanceledException（TaskCanceledException を含む）は
                // continueOnError に関わらず必ず再スローする。
                // キャンセルシグナルやタイムアウトを握りつぶすとプロセスが正しく停止できなくなるため。
                if (ex is OperationCanceledException)
                {
                    throw;
                }
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
