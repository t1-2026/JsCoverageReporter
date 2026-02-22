using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

public class HtmlOutputTests
{
    [Fact]
    public void HtmlEncode_EscapesSpecialChars()
    {
        Assert.Equal("&lt;div&gt;", HtmlReportGenerator.HtmlEncode("<div>"));
        Assert.Equal("a&amp;b",    HtmlReportGenerator.HtmlEncode("a&b"));
        Assert.Equal("&quot;",     HtmlReportGenerator.HtmlEncode("\""));
    }

    [Fact]
    public void BuildLines_SingleCoveredLine()
    {
        var lines = HtmlReportGenerator.BuildLines("hello", [1, 1, 1, 1, 1]);
        Assert.Single(lines);
        Assert.Contains("class=\"covered\"", lines[0].Html);
        Assert.Contains("hello", lines[0].Html);
        Assert.Equal(LineCoverageStatus.Covered, lines[0].Status);
    }

    [Fact]
    public void BuildLines_SingleUncoveredLine()
    {
        var lines = HtmlReportGenerator.BuildLines("hello", [0, 0, 0, 0, 0]);
        Assert.Single(lines);
        Assert.Contains("class=\"uncovered\"", lines[0].Html);
        Assert.Equal(LineCoverageStatus.Uncovered, lines[0].Status);
    }

    [Fact]
    public void BuildLines_PartialLine_ContainsBothSpans()
    {
        // "AB": A is covered, B is uncovered → Partial
        var lines = HtmlReportGenerator.BuildLines("AB", [1, 0]);
        Assert.Single(lines);
        Assert.Equal(LineCoverageStatus.Partial, lines[0].Status);
        Assert.Contains("class=\"covered\"",   lines[0].Html);
        Assert.Contains("class=\"uncovered\"", lines[0].Html);
    }

    [Fact]
    public void BuildLines_NeutralLine_AllOutOfScope()
    {
        var lines = HtmlReportGenerator.BuildLines("//comment", [-1, -1, -1, -1, -1, -1, -1, -1, -1]);
        Assert.Single(lines);
        Assert.Equal(LineCoverageStatus.Neutral, lines[0].Status);
    }

    [Fact]
    public void BuildLines_MultiLine_SplitsCorrectly()
    {
        // source = "A\nB", map: A=covered, \n=out-of-scope, B=uncovered
        var lines = HtmlReportGenerator.BuildLines("A\nB", [1, -1, 0]);
        Assert.Equal(2, lines.Count);
        Assert.Equal(LineCoverageStatus.Covered,   lines[0].Status);
        Assert.Equal(LineCoverageStatus.Uncovered, lines[1].Status);
    }
}
