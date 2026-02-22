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
                    await page.ClickAsync(action.Selector!);
                    break;
                case "fill":
                    await page.FillAsync(action.Selector!, action.Value ?? "");
                    break;
                case "navigate":
                    await page.GotoAsync(action.Url!);
                    break;
                case "waitForSelector":
                    await page.WaitForSelectorAsync(action.Selector!);
                    break;
                case "hover":
                    await page.HoverAsync(action.Selector!);
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
