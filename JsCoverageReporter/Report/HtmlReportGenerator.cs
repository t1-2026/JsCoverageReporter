using System.Text;
using JsCoverageReporter.Coverage;

namespace JsCoverageReporter.Report;

internal enum LineCoverageStatus { Neutral, Covered, Uncovered, Partial }

internal record LineData(string Html, LineCoverageStatus Status);

internal class HtmlReportGenerator
{
    /// <summary>
    /// Builds a per-character coverage map from V8 range data.
    /// Values: -1 = out of scope, 0 = not executed, 1 = executed.
    /// Processes ranges largest-first so inner branch ranges override the outer function range.
    /// </summary>
    internal static int[] BuildCoverageMap(string source, IEnumerable<FunctionCoverage> functions)
    {
        var map = new int[source.Length];
        Array.Fill(map, -1);

        var allRanges = functions
            .SelectMany(f => f.Ranges)
            .OrderByDescending(r => r.EndOffset - r.StartOffset);

        foreach (var range in allRanges)
        {
            int val = range.Count > 0 ? 1 : 0;
            int end = Math.Min(range.EndOffset, source.Length);
            for (int i = range.StartOffset; i < end; i++)
                map[i] = val;
        }

        return map;
    }

    internal static string HtmlEncode(string text) =>
        text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

    internal static List<LineData> BuildLines(string source, int[] map)
    {
        var result = new List<LineData>();
        var rawLines = source.Split('\n');
        int offset = 0;

        foreach (var rawLine in rawLines)
        {
            var sb = new StringBuilder();
            int coveredCount = 0, uncoveredCount = 0;
            int currentState = -2; // sentinel: "no span open yet"

            for (int i = 0; i < rawLine.Length; i++)
            {
                int idx = offset + i;
                int coverage = idx < map.Length ? map[idx] : -1;

                if (coverage != currentState)
                {
                    if (currentState != -2) sb.Append("</span>");
                    currentState = coverage;
                    string cls = coverage switch { 1 => "covered", 0 => "uncovered", _ => "neutral" };
                    sb.Append($"<span class=\"{cls}\">");
                }

                sb.Append(HtmlEncode(rawLine[i].ToString()));

                if (coverage == 1) coveredCount++;
                else if (coverage == 0) uncoveredCount++;
            }

            if (currentState != -2) sb.Append("</span>");

            var status = (coveredCount, uncoveredCount) switch
            {
                (0, 0)    => LineCoverageStatus.Neutral,
                ( > 0, 0) => LineCoverageStatus.Covered,
                (0, > 0)  => LineCoverageStatus.Uncovered,
                _         => LineCoverageStatus.Partial,
            };

            result.Add(new LineData(sb.ToString(), status));
            offset += rawLine.Length + 1; // +1 for the '\n' we split on
        }

        return result;
    }

    internal void Generate(IReadOnlyList<ScriptCoverage> coverages, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var scriptsDir = Path.Combine(outputDir, "scripts");
        Directory.CreateDirectory(scriptsDir);

        var summaryRows = new List<(string url, int covered, int total, string filename)>();

        for (int i = 0; i < coverages.Count; i++)
        {
            var script = coverages[i];
            var filename = $"script-{i}.html";

            var map   = BuildCoverageMap(script.Source, script.Functions);
            var lines = BuildLines(script.Source, map);

            int covered = lines.Count(l => l.Status is LineCoverageStatus.Covered or LineCoverageStatus.Partial);
            int total   = lines.Count(l => l.Status != LineCoverageStatus.Neutral);

            File.WriteAllText(
                Path.Combine(scriptsDir, filename),
                BuildScriptPage(script.Url, lines),
                Encoding.UTF8);

            summaryRows.Add((script.Url, covered, total, filename));
        }

        File.WriteAllText(
            Path.Combine(outputDir, "index.html"),
            BuildIndexPage(summaryRows),
            Encoding.UTF8);
    }

    private static string BuildScriptPage(string url, List<LineData> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            <!DOCTYPE html><html><head><meta charset="utf-8">
            <title>JS Coverage</title>
            <style>
            body{font-family:monospace;font-size:13px;margin:0;background:#fff}
            h1{padding:8px 12px;background:#2d2d2d;color:#fff;margin:0;font-size:13px;word-break:break-all}
            .source{white-space:pre}
            .line{display:flex;line-height:1.6}
            .gutter{min-width:48px;padding:0 8px;text-align:right;user-select:none;
                    background:#f5f5f5;color:#aaa;border-right:2px solid #e0e0e0}
            .code{padding:0 8px;flex:1;overflow-x:auto}
            .line-covered   .gutter{background:#c6efc6;color:#3a7d3a;border-color:#8fc98f}
            .line-uncovered .gutter{background:#f0c6c6;color:#7d3a3a;border-color:#c98f8f}
            .line-partial   .gutter{background:#f0e8a0;color:#6b6000;border-color:#c9b800}
            span.covered  {background:#d4f8d4}
            span.uncovered{background:#f8d4d4}
            span.neutral  {}
            </style></head><body>
            """);
        sb.AppendLine($"<h1>{HtmlEncode(url)}</h1><div class=\"source\">");

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            string cls = line.Status switch
            {
                LineCoverageStatus.Covered   => "line line-covered",
                LineCoverageStatus.Uncovered => "line line-uncovered",
                LineCoverageStatus.Partial   => "line line-partial",
                _                            => "line",
            };
            sb.AppendLine($"<div class=\"{cls}\"><span class=\"gutter\">{i + 1}</span><span class=\"code\">{line.Html}</span></div>");
        }

        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    private static string BuildIndexPage(
        List<(string url, int covered, int total, string filename)> rows)
    {
        int totalCovered = rows.Sum(r => r.covered);
        int totalLines   = rows.Sum(r => r.total);
        double overallPct = totalLines > 0 ? 100.0 * totalCovered / totalLines : 0;

        var sb = new StringBuilder();
        sb.AppendLine("""
            <!DOCTYPE html><html><head><meta charset="utf-8">
            <title>JS Coverage Report</title>
            <style>
            body{font-family:sans-serif;padding:24px;color:#333}
            h1{font-size:20px}
            table{border-collapse:collapse;width:100%;margin-top:16px}
            th,td{border:1px solid #ddd;padding:8px 12px;text-align:left}
            th{background:#f5f5f5;font-weight:600}
            td.num{text-align:right;font-variant-numeric:tabular-nums}
            a{color:#1a7a4a;text-decoration:none}
            a:hover{text-decoration:underline}
            </style></head><body>
            <h1>JS Coverage Report</h1>
            """);
        sb.AppendLine($"<p>Overall coverage: <strong>{overallPct:F1}%</strong> ({totalCovered} / {totalLines} lines)</p>");
        sb.AppendLine("""
            <table>
            <tr><th>Script</th><th class="num">Covered</th><th class="num">Total</th><th class="num">%</th></tr>
            """);

        foreach (var (url, covered, total, filename) in rows)
        {
            double pct = total > 0 ? 100.0 * covered / total : 0;
            sb.AppendLine($"<tr><td><a href=\"scripts/{filename}\">{HtmlEncode(url)}</a></td>" +
                          $"<td class=\"num\">{covered}</td><td class=\"num\">{total}</td>" +
                          $"<td class=\"num\">{pct:F1}%</td></tr>");
        }

        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }
}
