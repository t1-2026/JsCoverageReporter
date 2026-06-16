#nullable disable

using System.Text.Json;
using System.Text.Json.Serialization;
using JsCoverageReporter.Coverage;

namespace JsCoverageReporter.Report;

/// <summary>
/// レポート生成を別プロセスで行うためのデータ受け渡し（ハンドオフ）契約。
/// このフォーマットが他プロジェクトからの移植契約も兼ねる。
/// </summary>
internal static class CoverageHandoff
{
    // ハンドオフのルート DTO。targetUrl と収集スクリプトを束ねる。
    private sealed record Envelope(
        string TargetUrl,
        IReadOnlyList<ScriptCoverage> Coverages
    );

    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>収集データを JSON 文字列へシリアライズする。</summary>
    public static string Serialize(string targetUrl, IReadOnlyList<ScriptCoverage> coverages)
    {
        return JsonSerializer.Serialize(new Envelope(targetUrl, coverages), Options);
    }

    /// <summary>JSON 文字列を (targetUrl, coverages) へ復元する。</summary>
    public static (string TargetUrl, IReadOnlyList<ScriptCoverage> Coverages) Deserialize(string json)
    {
        var env = JsonSerializer.Deserialize<Envelope>(json, Options);
        if (env == null) { return (null, new List<ScriptCoverage>()); }
        var coverages = env.Coverages ?? new List<ScriptCoverage>();
        return (env.TargetUrl, coverages);
    }
}
