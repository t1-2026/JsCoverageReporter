namespace JsCoverageReporter.Coverage;

internal record ScriptCoverage(
    string Url,
    string Source,
    IReadOnlyList<FunctionCoverage> Functions
);

internal record FunctionCoverage(
    string FunctionName,
    IReadOnlyList<CoverageRange> Ranges
);

internal record CoverageRange(
    int StartOffset,
    int EndOffset,
    int Count
);
