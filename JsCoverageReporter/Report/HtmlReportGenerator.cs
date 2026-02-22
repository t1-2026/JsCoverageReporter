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
}
