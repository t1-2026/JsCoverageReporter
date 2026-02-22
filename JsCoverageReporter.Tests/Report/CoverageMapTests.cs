using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests.Report;

public class CoverageMapTests
{
    [Fact]
    public void BuildMap_OutOfScope_IsMinusOne()
    {
        var map = HtmlReportGenerator.BuildCoverageMap("abc", []);
        Assert.Equal([-1, -1, -1], map);
    }

    [Fact]
    public void BuildMap_AllCovered()
    {
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(0, 5, 1)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("hello", functions);
        Assert.Equal([1, 1, 1, 1, 1], map);
    }

    [Fact]
    public void BuildMap_AllUncovered()
    {
        var functions = new[] { new FunctionCoverage("f", [new CoverageRange(0, 5, 0)]) };
        var map = HtmlReportGenerator.BuildCoverageMap("hello", functions);
        Assert.Equal([0, 0, 0, 0, 0], map);
    }

    [Fact]
    public void BuildMap_InnerRangeOverridesOuter()
    {
        // Outer: whole source covered (count=3)
        // Inner: chars 13-16 are the else-branch, NOT executed (count=0)
        var source = "if(x){A}else{B}";
        //            0123456789012345
        var functions = new[]
        {
            new FunctionCoverage("f", [
                new CoverageRange(0,  16, 3),   // outer function — covered
                new CoverageRange(13, 16, 0),   // else branch — uncovered
            ])
        };
        var map = HtmlReportGenerator.BuildCoverageMap(source, functions);
        Assert.All(map[..13], v => Assert.Equal(1, v));   // if-branch: covered
        Assert.All(map[13..], v => Assert.Equal(0, v));   // else-branch: uncovered
    }
}
