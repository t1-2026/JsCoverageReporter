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

        // 同一 URL のスクリプト（ナビゲーション・タブをまたいだ重複）は1回だけ試行する。
        // 挿入順を保つため対象をリストに集める。
        var attempted = new HashSet<string>();
        var targets = new List<(string url, string mapRef)>();
        foreach (var script in coverages)
        {
            if (string.IsNullOrEmpty(script.Url)) { continue; }
            if (!attempted.Add(script.Url)) { continue; }

            // ソース末尾の //# sourceMappingURL= コメントを抽出する
            string mapRef = SourceMapUrlExtractor.Extract(script.Source);
            if (string.IsNullOrEmpty(mapRef)) { continue; }

            targets.Add((script.Url, mapRef));
        }

        // 取得・解析を並列実行する。結果は targets のインデックスで保持し順序を崩さない。
        var maps = new SourceMap[targets.Count];
        var tasks = new Task[targets.Count];
        for (int t = 0; t < targets.Count; t++)
        {
            int idx = t;
            tasks[idx] = Task.Run(async () =>
            {
                var (url, mapRef) = targets[idx];

                // マップ JSON を取得する（失敗は警告のみでレポート生成は続行する）
                string json;
                try
                {
                    json = await LoadMapJsonAsync(url, mapRef);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Warning] ソースマップの取得に失敗しました ({url}): {ex.Message}");
                    return;
                }
                if (string.IsNullOrEmpty(json)) { return; }

                // JSON を解析する（sections 形式・壊れたマップは null が返る）
                var sourceMap = SourceMap.Parse(json);
                if (sourceMap == null)
                {
                    Console.Error.WriteLine($"[Warning] ソースマップを解析できませんでした ({url}) — sections 形式または不正な JSON の可能性があります。");
                    return;
                }
                maps[idx] = sourceMap;
            });
        }
        await Task.WhenAll(tasks);

        // 挿入順で辞書へ集約する（決定的）。
        for (int t = 0; t < targets.Count; t++)
        {
            if (maps[t] != null) { result[targets[t].url] = maps[t]; }
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
