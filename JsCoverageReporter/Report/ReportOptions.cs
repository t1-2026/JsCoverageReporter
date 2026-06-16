#nullable disable

namespace JsCoverageReporter.Report;

/// <summary>
/// レポート生成中核（HtmlReportGenerator.Generate）の全オプションを集約する。
/// 外部呼び出し側はこのオブジェクトだけで生成挙動を制御する。
/// ブラウザ起動（--open）は実行環境依存のためここには含めず、ホスト層が担当する。
/// </summary>
/// <param name="OutputDir">レポート出力先ディレクトリ</param>
/// <param name="WriteLcov">lcov.info を出力するか</param>
/// <param name="WriteJson">coverage.json を出力するか</param>
/// <param name="TargetUrl">インデックスのメタ情報に表示する対象 URL（null なら非表示）</param>
internal sealed record ReportOptions(
    string OutputDir,
    bool   WriteLcov = false,
    bool   WriteJson = false,
    string TargetUrl = null
);
