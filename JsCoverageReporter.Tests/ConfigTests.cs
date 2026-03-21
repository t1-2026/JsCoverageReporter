using JsCoverageReporter.Config;
using System.Text.Json;

namespace JsCoverageReporter.Tests;

/// <summary>
/// ScenarioConfig クラスの JSON デシリアライズ動作を検証するテスト群。
/// シナリオ設定ファイル（JSON）が正しく C# オブジェクトに変換されることを確認する。
/// </summary>
public class ConfigTests
{
    /// <summary>
    /// url フィールドだけを持つ最小構成の JSON を正しく読み込めることを確認する。
    /// 省略可能なフィールド（actions・scriptFilters など）は空のリストになるはず。
    /// </summary>
    [Fact]
    public void Deserialize_MinimalConfig()
    {
        // url だけを持つ最小構成の JSON を用意する
        var json = """{"url": "https://example.com"}""";

        // JSON を ScenarioConfig オブジェクトに変換する
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // url が正しく読み込まれているか確認する
        Assert.Equal("https://example.com", config.Url);
        // actions を省略した場合、空のリストになっているか確認する
        Assert.Empty(config.Actions);
        // scriptFilters を省略した場合、空のリストになっているか確認する
        Assert.Empty(config.ScriptFilters);
    }

    /// <summary>
    /// 使用可能なすべてのアクション種別（click/fill/navigate/waitForSelector/hover/wait）を
    /// 含む JSON を正しく読み込めることを確認する。
    /// </summary>
    [Fact]
    public void Deserialize_AllActionTypes()
    {
        // 6種類のアクションをすべて含む JSON を用意する
        var json = """
        {
            "url": "https://example.com",
            "scriptFilters": ["app.js"],
            "actions": [
                { "type": "click",           "selector": "#btn" },
                { "type": "fill",            "selector": "input", "value": "hello" },
                { "type": "navigate",        "url": "https://example.com/p2" },
                { "type": "waitForSelector", "selector": ".ready" },
                { "type": "hover",           "selector": ".menu" },
                { "type": "wait",            "milliseconds": 500 }
            ]
        }
        """;

        // JSON を ScenarioConfig オブジェクトに変換する
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // scriptFilters が正しく読み込まれているか確認する
        Assert.Equal(["app.js"], config.ScriptFilters);
        // アクションが6件読み込まれているか確認する
        Assert.Equal(6, config.Actions.Count);
        // 各アクションのフィールドが正しく読み込まれているか確認する
        Assert.Equal("click",   config.Actions[0].Type);
        Assert.Equal("#btn",    config.Actions[0].Selector);
        Assert.Equal("hello",   config.Actions[1].Value);
        Assert.Equal("https://example.com/p2", config.Actions[2].Url);
        Assert.Equal(500,       config.Actions[5].Milliseconds);
    }

    /// <summary>
    /// scriptFilters に複数のキーワードを指定した JSON を正しく読み込めることを確認する。
    /// </summary>
    [Fact]
    public void Deserialize_MultipleScriptFilters()
    {
        // 3つのキーワードを持つ scriptFilters を含む JSON を用意する
        var json = """{"url": "https://example.com", "scriptFilters": ["app.js", "utils.js", "vendor.js"]}""";

        // JSON を ScenarioConfig オブジェクトに変換する
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // キーワードが3件読み込まれているか確認する
        Assert.Equal(3, config.ScriptFilters.Count);
        // 各キーワードが正しい順序で読み込まれているか確認する
        Assert.Equal("app.js",    config.ScriptFilters[0]);
        Assert.Equal("utils.js",  config.ScriptFilters[1]);
        Assert.Equal("vendor.js", config.ScriptFilters[2]);
    }

    /// <summary>
    /// continueOnError フィールドを省略した場合、デフォルト値 false になることを確認する。
    /// </summary>
    [Fact]
    public void Deserialize_ContinueOnError_DefaultFalse()
    {
        // continueOnError を省略した JSON を用意する
        var json = """{"url": "https://example.com"}""";

        // JSON を ScenarioConfig オブジェクトに変換する
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // デフォルト値が false であることを確認する（エラー時は処理を停止する）
        Assert.False(config.ContinueOnError);
    }

    /// <summary>
    /// continueOnError フィールドに true を指定した場合、正しく読み込まれることを確認する。
    /// </summary>
    [Fact]
    public void Deserialize_ContinueOnError_WhenTrue()
    {
        // continueOnError を true に設定した JSON を用意する
        var json = """{"url": "https://example.com", "continueOnError": true}""";

        // JSON を ScenarioConfig オブジェクトに変換する
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // true が正しく読み込まれているか確認する（エラー時に次のアクションに進む）
        Assert.True(config.ContinueOnError);
    }

    /// <summary>
    /// scriptExcludes フィールドを省略した場合、空のリストになることを確認する。
    /// </summary>
    [Fact]
    public void Deserialize_ScriptExcludes_DefaultEmpty()
    {
        // scriptExcludes を省略した JSON を用意する
        var json = """{"url": "https://example.com"}""";

        // JSON を ScenarioConfig オブジェクトに変換する
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // 省略した場合は空のリストになっているか確認する（除外なし）
        Assert.Empty(config.ScriptExcludes);
    }

    /// <summary>
    /// scriptExcludes に複数の除外キーワードを指定した JSON を正しく読み込めることを確認する。
    /// </summary>
    [Fact]
    public void Deserialize_ScriptExcludes_WhenSet()
    {
        // 2つの除外キーワードを含む JSON を用意する
        var json = """{"url": "https://example.com", "scriptExcludes": ["__playwright", "pptr:"]}""";

        // JSON を ScenarioConfig オブジェクトに変換する
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // 除外キーワードが2件読み込まれているか確認する
        Assert.Equal(2, config.ScriptExcludes.Count);
        // 各キーワードが正しい順序で読み込まれているか確認する
        Assert.Equal("__playwright", config.ScriptExcludes[0]);
        Assert.Equal("pptr:",        config.ScriptExcludes[1]);
    }

    /// <summary>
    /// timeoutMs フィールドを指定した JSON を正しく読み込めることを確認する。
    /// </summary>
    [Fact]
    public void Deserialize_TimeoutMs_WhenSet()
    {
        // タイムアウトを 10000ms に設定した JSON を用意する
        var json = """{"url": "https://example.com", "timeoutMs": 10000}""";

        // JSON を ScenarioConfig オブジェクトに変換する
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // 10000 が正しく読み込まれているか確認する
        Assert.Equal(10000, config.TimeoutMs);
    }

    /// <summary>
    /// timeoutMs フィールドを省略した場合、null になることを確認する。
    /// null の場合は Playwright のデフォルトタイムアウト（30秒）が使われる。
    /// </summary>
    [Fact]
    public void Deserialize_TimeoutMs_NullWhenNotSet()
    {
        // timeoutMs を省略した JSON を用意する
        var json = """{"url": "https://example.com"}""";

        // JSON を ScenarioConfig オブジェクトに変換する
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // 省略した場合は null になっているか確認する
        Assert.Null(config.TimeoutMs);
    }

    // -----------------------------------------------------------------------
    // 追加テスト — 堅牢性・エッジケース観点
    // 未知フィールド・不正 JSON・大文字小文字・特殊値の挙動を確認する
    // -----------------------------------------------------------------------

    /// <summary>
    /// url フィールドが JSON に存在しない場合、デフォルト値の空文字列になることを確認する。
    /// Program.cs は IsNullOrEmpty で検証するため、空文字列は「URL 未指定」として検出される。
    /// </summary>
    [Fact]
    public void Deserialize_NoUrl_DefaultsToEmptyString()
    {
        // url フィールドなし（空オブジェクト）
        var json = """{}""";
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // url が省略された場合は初期値の空文字列になるべき
        Assert.Equal("", config.Url);
    }

    /// <summary>
    /// JSON に未知のフィールドが含まれていても例外を投げず、
    /// 既知フィールドが正しく読み込まれることを確認する。
    /// System.Text.Json のデフォルト動作では未知フィールドは無視される。
    /// </summary>
    [Fact]
    public void Deserialize_UnknownField_IgnoredSilently()
    {
        // 存在しない "unknownField" を含む JSON を用意する
        var json = """{"url": "https://example.com", "unknownField": "someValue", "anotherUnknown": 42}""";

        // 例外なしにデシリアライズできるはず
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // 既知のフィールドは正しく読み込まれているか確認する
        Assert.Equal("https://example.com", config.Url);
    }

    /// <summary>
    /// 不正な JSON を渡した場合、JsonException がスローされることを確認する。
    /// Program.cs はこれをキャッチして終了コード 1（設定エラー）を返す。
    /// </summary>
    [Fact]
    public void Deserialize_InvalidJson_ThrowsJsonException()
    {
        // 閉じ括弧がない不正な JSON を用意する
        var json = """{ "url": "https://example.com" """;

        // JsonException がスローされるはず
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)
        );
    }

    /// <summary>
    /// フィールド名が大文字（"URL"）で記述された JSON が、
    /// PropertyNameCaseInsensitive = true の設定により正しく読み込まれることを確認する。
    /// </summary>
    [Fact]
    public void Deserialize_UpperCaseFieldName_CaseInsensitiveMatch()
    {
        // "URL"（大文字）でフィールド名を記述した JSON を用意する
        var json = """{"URL": "https://example.com", "SCRIPTFILTERS": ["app.js"]}""";
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // 大文字フィールド名でも正しく読み込まれているか確認する
        Assert.Equal("https://example.com", config.Url);
        Assert.Equal(["app.js"], config.ScriptFilters);
    }

    /// <summary>
    /// timeoutMs に負の値（-1）を指定した場合、デシリアライズは成功することを確認する。
    /// 値の妥当性チェックは Program.cs 側の責務であり、ScenarioConfig は値をそのまま保持する。
    /// </summary>
    [Fact]
    public void Deserialize_NegativeTimeoutMs_AcceptedAsIs()
    {
        // 負のタイムアウト値を含む JSON を用意する
        var json = """{"url": "https://example.com", "timeoutMs": -1}""";
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // デシリアライズ自体は成功し、-1 がそのまま保持されるべき
        Assert.Equal(-1, config.TimeoutMs);
    }

    /// <summary>
    /// actions フィールドに JSON の null を指定した場合の挙動を確認する。
    /// System.Text.Json はデフォルトで null をプロパティに代入するため、
    /// List の初期値 "= []" がオーバーライドされて null になる。
    /// ActionRunner で NullReferenceException が発生するリスクがある既知の挙動。
    /// </summary>
    [Fact]
    public void Deserialize_ActionsExplicitlyNull_PropertyBecomesEmptyList()
    {
        // "actions": null を含む JSON を用意する
        var json = """{"url": "https://example.com", "actions": null}""";
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // Configuration sets Actions to empty array when null is provided
        Assert.Empty(config.Actions);
    }

    /// <summary>
    /// actions フィールドに明示的に空配列 [] を指定した場合、
    /// 空のリストが正しく読み込まれることを確認する。
    /// </summary>
    [Fact]
    public void Deserialize_ActionsEmptyArray_ResultsInEmptyList()
    {
        // 空の actions 配列を含む JSON を用意する
        var json = """{"url": "https://example.com", "actions": []}""";
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // 空配列が空リストとして読み込まれているか確認する
        Assert.NotNull(config.Actions);
        Assert.Empty(config.Actions);
    }

    /// <summary>
    /// URL に日本語などのマルチバイト文字が含まれる場合でも
    /// 正しく読み込まれることを確認する。
    /// </summary>
    [Fact]
    public void Deserialize_UnicodeUrl_PreservedExactly()
    {
        // 日本語を含む URL を持つ JSON を用意する
        var json = """{"url": "https://example.com/検索?q=テスト"}""";
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // マルチバイト文字が正確に保持されているか確認する
        Assert.Equal("https://example.com/検索?q=テスト", config.Url);
    }

    /// <summary>
    /// press アクションの value フィールドが省略された場合、
    /// Selector を持ちながら Value は null になることを確認する。
    /// ActionRunner は value が null の場合 "Enter" をデフォルトとして使う。
    /// </summary>
    [Fact]
    public void Deserialize_PressActionWithoutValue_ValueIsNull()
    {
        // value を省略した press アクションを持つ JSON を用意する
        var json = """
        {
            "url": "https://example.com",
            "actions": [
                {"type": "press", "selector": "input"}
            ]
        }
        """;
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        // アクションが1件読み込まれているか確認する
        Assert.Single(config.Actions);
        // type と selector は設定されているか確認する
        Assert.Equal("press",  config.Actions[0].Type);
        Assert.Equal("input",  config.Actions[0].Selector);
        // value を省略した場合は null になるべき（ActionRunner が "Enter" にフォールバックする）
        Assert.Null(config.Actions[0].Value);
    }

    /// <summary>
    /// scriptFilters に明示的に null を指定した場合、空リストになることを確認する。
    /// </summary>
    [Fact]
    public void Deserialize_ScriptFiltersExplicitlyNull_PropertyBecomesEmptyList()
    {
        var json = """{"url": "https://example.com", "scriptFilters": null}""";
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        Assert.NotNull(config.ScriptFilters);
        Assert.Empty(config.ScriptFilters);
    }

    /// <summary>
    /// scriptExcludes に明示的に null を指定した場合、空リストになることを確認する。
    /// </summary>
    [Fact]
    public void Deserialize_ScriptExcludesExplicitlyNull_PropertyBecomesEmptyList()
    {
        var json = """{"url": "https://example.com", "scriptExcludes": null}""";
        var config = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions)!;

        Assert.NotNull(config.ScriptExcludes);
        Assert.Empty(config.ScriptExcludes);
    }
}
