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

    [Theory]
    // progressWindow が false なら、wait/OS によらずウィンドウなし・警告なし
    [InlineData(false, false, true,  false, false)]
    [InlineData(false, true,  true,  false, false)]
    [InlineData(false, false, false, false, false)]
    // Windows + デタッチ(wait=false) + progressWindow → ウィンドウを開く
    [InlineData(true,  false, true,  true,  false)]
    // Windows + wait 併用 → ウィンドウは開かず警告
    [InlineData(true,  true,  true,  false, true)]
    // 非 Windows → progressWindow は黙って無視（警告も出さない）
    [InlineData(true,  false, false, false, false)]
    [InlineData(true,  true,  false, false, false)]
    public void ResolveProgressWindow_DecidesWindowAndWarning(
        bool progressWindow, bool wait, bool isWindows,
        bool expectUseNewWindow, bool expectWarnWaitConflict)
    {
        var (useNewWindow, warnWaitConflict) =
            ReportProcess.ResolveProgressWindow(progressWindow, wait, isWindows);

        Assert.Equal(expectUseNewWindow, useNewWindow);
        Assert.Equal(expectWarnWaitConflict, warnWaitConflict);
    }
}
