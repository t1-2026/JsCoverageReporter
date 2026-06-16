using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

/// <summary>
/// 子プロセスへ渡す --report-from 引数の組み立てを検証する（プロセスは起動しない）。
/// </summary>
public class ReportProcessTests
{
    [Fact]
    public void BuildReportArgs_IncludesEnabledFlagsOnly()
    {
        var args = ReportProcess.BuildReportArgs(
            dataFile: @"C:\tmp\data.json",
            outputDir: @"C:\out",
            writeLcov: true,
            writeJson: false,
            open: true,
            targetUrl: "https://example.com");

        Assert.Equal(new[]
        {
            "--report-from", @"C:\tmp\data.json",
            "--output", @"C:\out",
            "--lcov",
            "--open",
            "--target-url", "https://example.com",
        }, args);
    }

    [Fact]
    public void BuildReportArgs_OmitsDisabledFlags()
    {
        var args = ReportProcess.BuildReportArgs(
            dataFile: "d.json", outputDir: "out",
            writeLcov: false, writeJson: false, open: false, targetUrl: null);

        Assert.Equal(new[] { "--report-from", "d.json", "--output", "out" }, args);
    }
}
