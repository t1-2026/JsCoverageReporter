using System.Text.Json;
using System.Text.Json.Serialization;

namespace JsCoverageReporter.Config;

/// <summary>
/// シナリオJSONファイル全体の設定を表すクラス。
/// JSONファイルをデシリアライズしてこのオブジェクトに変換する。
/// </summary>
internal class ScenarioConfig
{
    /// <summary>
    /// JSONデシリアライズ時に使用するオプション設定。
    /// PropertyNameCaseInsensitive を true にすることで、JSONキーの大文字小文字を区別せずにマッピングする。
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        // プロパティ名の大文字/小文字を区別しない（例: "URL" でも "url" でも同じプロパティにマッピングされる）
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// カバレッジを計測するページのURL。必須項目。
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    // ScriptFilters のバッキングフィールド（JSON null → 空リストに変換するために使用する）
    private List<string> _scriptFilters = [];

    /// <summary>
    /// スクリプトURLの絞り込み文字列のリスト。
    /// 指定すると、いずれかの文字列を URL に含むスクリプトだけをレポートに含める。
    /// 省略（空リスト）すると全スクリプトが対象になる。
    /// 例: ["app.js", "utils.js"]
    /// JSON で null が渡された場合も空リストとして扱う。
    /// </summary>
    [JsonPropertyName("scriptFilters")]
    public List<string> ScriptFilters
    {
        get { return _scriptFilters; }
        set
        {
            // JSON で null が設定された場合は空リストに変換する
            if (value == null)
            {
                _scriptFilters = [];
            }
            else
            {
                _scriptFilters = value;
            }
        }
    }

    // ScriptExcludes のバッキングフィールド（JSON null → 空リストに変換するために使用する）
    private List<string> _scriptExcludes = [];

    /// <summary>
    /// スクリプトURLの除外文字列のリスト。
    /// 指定すると、いずれかの文字列を URL に含むスクリプトをレポートから除外する。
    /// scriptFilters による絞り込みの後に適用される（最終的な除外として機能する）。
    /// 例: ["__playwright", "pptr:"]
    /// JSON で null が渡された場合も空リストとして扱う。
    /// </summary>
    [JsonPropertyName("scriptExcludes")]
    public List<string> ScriptExcludes
    {
        get { return _scriptExcludes; }
        set
        {
            // JSON で null が設定された場合は空リストに変換する
            if (value == null)
            {
                _scriptExcludes = [];
            }
            else
            {
                _scriptExcludes = value;
            }
        }
    }

    /// <summary>
    /// Playwrightのアクションタイムアウト（ミリ秒単位）。
    /// 省略するとPlaywrightのデフォルト（30秒）が使われる。
    /// </summary>
    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; set; }

    /// <summary>
    /// アクション実行中にエラーが発生しても続行するかどうか。
    /// true にすると、1つのアクションが失敗しても後続のアクションを実行する。
    /// false（デフォルト）にすると、最初のエラーで処理を停止する。
    /// </summary>
    [JsonPropertyName("continueOnError")]
    public bool ContinueOnError { get; set; } = false;

    // Actions のバッキングフィールド（JSON null → 空リストに変換するために使用する）
    private List<ScenarioAction> _actions = [];

    /// <summary>
    /// 実行するアクションのリスト。
    /// JSONの "actions" 配列をデシリアライズしたもの。
    /// JSON で null が渡された場合も空リストとして扱う。
    /// </summary>
    [JsonPropertyName("actions")]
    public List<ScenarioAction> Actions
    {
        get { return _actions; }
        set
        {
            // JSON で null が設定された場合は空リストに変換する
            if (value == null)
            {
                _actions = [];
            }
            else
            {
                _actions = value;
            }
        }
    }
}

/// <summary>
/// シナリオ内の1つのアクションを表すクラス。
/// Type に応じて異なるプロパティを使用する。
/// </summary>
internal class ScenarioAction
{
    /// <summary>
    /// アクションの種類を示す文字列。
    /// 使用可能な値: "click", "fill", "navigate", "waitForSelector", "hover", "press", "wait", "select", "check", "uncheck", "dblclick", "scroll"
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>
    /// 操作対象のCSSセレクター。
    /// click, fill, waitForSelector, hover, press アクションで使用する。
    /// </summary>
    [JsonPropertyName("selector")]
    public string? Selector { get; set; }

    /// <summary>
    /// 入力する値またはキー名。
    /// fill では入力テキスト、press ではキー名（例: "Enter"）として使用する。
    /// </summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>
    /// 遷移先URL。navigate アクションで使用する。
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// 待機時間（ミリ秒）。wait アクションで使用する。
    /// </summary>
    [JsonPropertyName("milliseconds")]
    public int? Milliseconds { get; set; }

    /// <summary>
    /// 水平スクロール量（ピクセル）。scroll アクションでセレクターを指定しない場合に使用する。
    /// 正の値で右方向、負の値で左方向にスクロールする。省略時は 0 として扱う。
    /// </summary>
    [JsonPropertyName("x")]
    public int? X { get; set; }

    /// <summary>
    /// 垂直スクロール量（ピクセル）。scroll アクションでセレクターを指定しない場合に使用する。
    /// 正の値で下方向、負の値で上方向にスクロールする。省略時は 0 として扱う。
    /// </summary>
    [JsonPropertyName("y")]
    public int? Y { get; set; }
}
