#nullable disable

using JsCoverageReporter.Coverage;

namespace JsCoverageReporter.Report;

/// <summary>
/// 収集済みスクリプトのソースマップを取得・解析する静的クラス。
/// スクリプト末尾の //# sourceMappingURL コメントを抽出し、
/// http(s) / file / data URL からマップ JSON を取得して SourceMap.Parse に渡す。
/// 取得・解析に失敗したスクリプトは警告を出してスキップする（レポート生成は止めない）。
/// </summary>
internal static class SourceMapLoader
{
    // ソースマップ取得用の HTTP クライアント（プロセス内で共有する）
    // ローカル開発サーバー相手が主用途のためタイムアウトは短めに設定する
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// すべてのスクリプトのソースマップ取得を試み、スクリプト URL → SourceMap の辞書を返す。
    /// sourceMappingURL コメントがないスクリプトは黙ってスキップする。
    /// </summary>
    /// <param name="coverages">収集したスクリプトカバレッジデータのリスト</param>
    /// <returns>スクリプト URL → 解析済みソースマップの辞書（取得できたものだけ）</returns>
    public static async Task<Dictionary<string, SourceMap>> LoadAllAsync(IReadOnlyList<ScriptCoverage> coverages)
    {
        var result = new Dictionary<string, SourceMap>();
        if (coverages == null) { return result; }

        // 同一 URL のスクリプト（ナビゲーション・タブをまたいだ重複）は1回だけ試行する
        var attempted = new HashSet<string>();
        foreach (var script in coverages)
        {
            if (string.IsNullOrEmpty(script.Url)) { continue; }
            if (!attempted.Add(script.Url)) { continue; }

            // ソース末尾の //# sourceMappingURL= コメントを抽出する
            string mapRef = SourceMapUrlExtractor.Extract(script.Source);
            if (string.IsNullOrEmpty(mapRef)) { continue; }

            // マップ JSON を取得する（失敗は警告のみでレポート生成は続行する）
            string json;
            try
            {
                json = await LoadMapJsonAsync(script.Url, mapRef);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Warning] ソースマップの取得に失敗しました ({script.Url}): {ex.Message}");
                continue;
            }
            if (string.IsNullOrEmpty(json)) { continue; }

            // JSON を解析する（sections 形式・壊れたマップは null が返る）
            var sourceMap = SourceMap.Parse(json);
            if (sourceMap == null)
            {
                Console.Error.WriteLine($"[Warning] ソースマップを解析できませんでした ({script.Url}) — sections 形式または不正な JSON の可能性があります。");
                continue;
            }
            result[script.Url] = sourceMap;
        }
        return result;
    }

    /// <summary>
    /// sourceMappingURL の値からマップ JSON を取得する。
    /// data: URL はインラインデコード、相対 URL はスクリプト URL を基準に解決する。
    /// http(s) / file 以外のスキームは取得せず null を返す。
    /// </summary>
    /// <param name="scriptUrl">スクリプトの URL（相対 URL の解決基準）</param>
    /// <param name="mapRef">sourceMappingURL コメントの値</param>
    private static async Task<string> LoadMapJsonAsync(string scriptUrl, string mapRef)
    {
        // インラインソースマップ（data: URL）はネットワークアクセスなしでデコードする
        if (mapRef.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return TryDecodeDataUrl(mapRef);
        }

        // 絶対 URL ならそのまま、相対 URL ならスクリプト URL を基準に解決する
        Uri mapUri;
        if (Uri.TryCreate(mapRef, UriKind.Absolute, out var absolute))
        {
            mapUri = absolute;
        }
        else
        {
            if (!Uri.TryCreate(scriptUrl, UriKind.Absolute, out var baseUri)) { return null; }
            if (!Uri.TryCreate(baseUri, mapRef, out mapUri)) { return null; }
        }

        if (mapUri.Scheme == Uri.UriSchemeHttp || mapUri.Scheme == Uri.UriSchemeHttps)
        {
            return await Http.GetStringAsync(mapUri);
        }
        if (mapUri.IsFile)
        {
            return await File.ReadAllTextAsync(mapUri.LocalPath);
        }
        // 未対応スキーム（webpack:// など）は取得しない
        return null;
    }

    /// <summary>
    /// data: URL からマップ JSON をデコードする。
    /// 形式: data:[mediatype][;base64],&lt;payload&gt;
    /// base64 指定があれば Base64 デコード、なければパーセントデコードする。
    /// </summary>
    /// <param name="dataUrl">data: で始まる URL</param>
    /// <returns>デコードした JSON 文字列。デコードできない場合は null</returns>
    internal static string TryDecodeDataUrl(string dataUrl)
    {
        int comma = dataUrl.IndexOf(',');
        if (comma < 0) { return null; }

        // "data:" とカンマの間のメタ部分（mediatype と base64 指定）
        string meta    = dataUrl.Substring(5, comma - 5);
        string payload = dataUrl.Substring(comma + 1);

        if (meta.Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(payload);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // base64 でない場合はパーセントエンコードされた JSON として扱う
        try
        {
            return Uri.UnescapeDataString(payload);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
