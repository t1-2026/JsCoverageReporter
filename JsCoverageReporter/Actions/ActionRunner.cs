using JsCoverageReporter.Config;
using Microsoft.Playwright;

namespace JsCoverageReporter.Actions;

internal static class ActionRunner
{
    internal static async Task RunAsync(IPage page, IEnumerable<ScenarioAction> actions)
    {
        foreach (var action in actions)
        {
            switch (action.Type)
            {
                case "click":
                    if (action.Selector is null) { Console.Error.WriteLine("[Warning] 'click' action missing 'selector' — skipping."); break; }
                    await page.ClickAsync(action.Selector);
                    break;
                case "fill":
                    if (action.Selector is null) { Console.Error.WriteLine("[Warning] 'fill' action missing 'selector' — skipping."); break; }
                    await page.FillAsync(action.Selector, action.Value ?? "");
                    break;
                case "navigate":
                    if (action.Url is null) { Console.Error.WriteLine("[Warning] 'navigate' action missing 'url' — skipping."); break; }
                    await page.GotoAsync(action.Url);
                    break;
                case "waitForSelector":
                    if (action.Selector is null) { Console.Error.WriteLine("[Warning] 'waitForSelector' action missing 'selector' — skipping."); break; }
                    await page.WaitForSelectorAsync(action.Selector);
                    break;
                case "hover":
                    if (action.Selector is null) { Console.Error.WriteLine("[Warning] 'hover' action missing 'selector' — skipping."); break; }
                    await page.HoverAsync(action.Selector);
                    break;
                case "wait":
                    await Task.Delay(action.Milliseconds ?? 0);
                    break;
                default:
                    Console.Error.WriteLine($"[Warning] Unknown action type '{action.Type}' — skipping.");
                    break;
            }
        }
    }
}
