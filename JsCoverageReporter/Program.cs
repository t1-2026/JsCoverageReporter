using JsCoverageReporter.Actions;
using JsCoverageReporter.Config;
using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;
using Microsoft.Playwright;
using System.Text.Json;

// ---- 終了コードの定義 ----
// 0: 正常終了
// 1: 設定エラー（引数・設定ファイルの不備）
// 2: 実行時エラー（ブラウザ操作・ネットワーク・CDP エラー）

// ---- --help の処理 ----
// --help または -h が含まれていればヘルプを表示して終了する
if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0)
{
    Console.WriteLine("""
        使い方:
          JsCoverageReporter --config <scenario.json> [オプション]

        必須オプション:
          --config <path>     シナリオ設定ファイル（JSON）のパス

        任意オプション:
          --output <dir>      レポートの出力先ディレクトリ（デフォルト: ./report）
          --headed            ブラウザウィンドウを表示して実行する（デフォルト: 非表示）
          --verbose           エラー発生時に詳細なスタックトレースを表示する
          --help, -h          このヘルプを表示する

        シナリオ JSON の主なフィールド:
          url             （必須）計測対象ページの URL
          scriptFilters   （任意）絞り込むスクリプト URL のキーワード配列
                          例: ["app.js", "utils.js"]
          scriptExcludes  （任意）除外するスクリプト URL のキーワード配列
                          例: ["__playwright", "pptr:"]
          timeoutMs       （任意）各アクションのタイムアウト（ミリ秒）
          continueOnError （任意）アクション失敗時に続行するか（デフォルト: false）
          actions         （任意）実行するアクションのリスト

        終了コード:
          0   正常終了
          1   設定エラー（引数・設定ファイルの不備）
          2   実行時エラー（ブラウザ操作・ネットワーク・CDP エラー）
        """);
    return 0;
}

// ---- コマンドライン引数の解析 ----

// 設定ファイルのパス（--config で指定された値。未指定なら null）
string? configPath = null;

// 出力先ディレクトリのパス（--output で指定された値。デフォルトは "./report"）
string outputDir = "./report";

// --config が既に指定済みかどうかを示すフラグ（重複指定の検知に使う）
bool configSet = false;

// --output が既に指定済みかどうかを示すフラグ（重複指定の検知に使う）
bool outputSet = false;

// --headed が指定された場合は false（ブラウザを表示）、それ以外は true（非表示）
bool headless = true;

// --verbose が指定された場合は true（スタックトレースを表示）
bool verbose = false;

// コマンドライン引数を1つずつ走査する
for (int i = 0; i < args.Length; i++)
{
    // --config オプションを検出する
    if (args[i] == "--config")
    {
        // 同じオプションが複数回指定された場合は警告する
        if (configSet)
        {
            Console.Error.WriteLine("[Warning] --config が複数回指定されました。最後の値を使用します。");
        }
        // 値が存在しない、または次の引数が別のオプション（--で始まる）の場合はエラーにする
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
        {
            Console.Error.WriteLine("エラー: --config オプションには値が必要です。例: --config scenario.json");
            return 1;
        }
        // 次の引数を設定ファイルのパスとして取得する
        configPath = args[i + 1];
        // 設定済みフラグを立てる
        configSet = true;
        // 値として消費した次の引数をスキップする
        i++;
    }

    // --output オプションを検出する
    else if (args[i] == "--output")
    {
        // 同じオプションが複数回指定された場合は警告する
        if (outputSet)
        {
            Console.Error.WriteLine("[Warning] --output が複数回指定されました。最後の値を使用します。");
        }
        // 値が存在しない、または次の引数が別のオプション（--で始まる）の場合はエラーにする
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
        {
            Console.Error.WriteLine("エラー: --output オプションには値が必要です。例: --output ./report");
            return 1;
        }
        // 次の引数を出力ディレクトリのパスとして取得する
        outputDir = args[i + 1];
        // 設定済みフラグを立てる
        outputSet = true;
        // 値として消費した次の引数をスキップする
        i++;
    }

    // --headed オプションを検出する（値なし・ブラウザを表示して実行する）
    else if (args[i] == "--headed")
    {
        headless = false;
    }

    // --verbose オプションを検出する（値なし・エラー時にスタックトレースを表示する）
    else if (args[i] == "--verbose")
    {
        verbose = true;
    }
}

// --config が指定されていなければ使い方を表示して終了する（終了コード 1 = 設定エラー）
if (configPath is null)
{
    Console.Error.WriteLine("エラー: --config オプションが必要です。");
    Console.Error.WriteLine("使い方を確認するには: JsCoverageReporter --help");
    return 1;
}

// ---- シナリオJSONの読み込み ----

// デシリアライズ後の設定オブジェクトを格納する変数
ScenarioConfig scenario;
try
{
    // JSONファイルをテキストとして読み込む
    string json = File.ReadAllText(configPath);

    // JSONテキストを ScenarioConfig オブジェクトに変換する
    ScenarioConfig? deserializedScenario = JsonSerializer.Deserialize<ScenarioConfig>(json, ScenarioConfig.JsonOptions);

    // デシリアライズ結果が null の場合はエラーにする
    if (deserializedScenario == null)
    {
        throw new InvalidOperationException("設定ファイルが空または無効です。");
    }

    // null でないことが確認できたので scenario に代入する
    scenario = deserializedScenario;
}
catch (Exception ex)
{
    // ファイル読み込みや JSON 解析に失敗した場合はエラーメッセージを出力して終了する（終了コード 1 = 設定エラー）
    Console.Error.WriteLine($"設定ファイルの読み込みに失敗しました: {ex.Message}");
    return 1;
}

// URLが設定されているか確認する（空文字や null は不正な設定とみなす）
if (string.IsNullOrEmpty(scenario.Url))
{
    Console.Error.WriteLine("エラー: 設定ファイルに \"url\" フィールドが必要です。");
    return 1;
}

// 対象URLをコンソールに表示する
Console.WriteLine($"Opening: {scenario.Url}");

// ---- ブラウザの起動・操作・カバレッジ収集 ----

try
{
    // Playwrightを初期化する（ブラウザドライバの準備）
    using var playwright = await Playwright.CreateAsync();

    // Chromiumブラウザを起動する（headless=true: 画面なし / false: ウィンドウ表示）
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        // --headed が指定された場合は false になり、ブラウザウィンドウが表示される
        Headless = headless,
    });

    // 新しいページ（タブ）を開く
    var page = await browser.NewPageAsync();

    // カバレッジ収集を開始する（CDPでV8 Profilerを有効にする）
    // フィルターを StartAsync に渡すことで中間スナップショット時にも正しく適用される
    await using var collector = new CoverageCollector(page);
    await collector.StartAsync(scenario.ScriptFilters, scenario.ScriptExcludes);

    // シナリオで指定されたURLに移動する
    await page.GotoAsync(scenario.Url, new PageGotoOptions { Timeout = scenario.TimeoutMs });

    // シナリオで定義されたアクション（クリックなど）を順番に実行する
    await ActionRunner.RunAsync(page, scenario.Actions, scenario.TimeoutMs, scenario.ContinueOnError);

    // カバレッジデータを収集して取得する
    Console.WriteLine("Collecting coverage...");
    // カバレッジデータを取得する（フィルターは StartAsync で渡し済み）
    var coverages = await collector.StopAsync();
    // 取得したスクリプト数を表示する
    Console.WriteLine($"  {coverages.Count} script(s) captured.");

    // HTMLレポートを生成して出力ディレクトリに書き出す
    new HtmlReportGenerator().Generate(coverages, outputDir);
    // 生成したレポートのパスを表示する
    Console.WriteLine($"Report: {Path.Combine(outputDir, "index.html")}");

    // 正常終了を示す 0 を返す
    return 0;
}
catch (Exception ex)
{
    // ブラウザ操作やカバレッジ収集に失敗した場合はエラーメッセージを出力して終了する（終了コード 2 = 実行時エラー）
    if (verbose)
    {
        // --verbose が指定されている場合はスタックトレースも表示する
        Console.Error.WriteLine($"実行時エラー: {ex}");
    }
    else
    {
        // 通常はメッセージのみ表示する（詳細は --verbose で確認できる）
        Console.Error.WriteLine($"実行時エラー: {ex.Message}");
        Console.Error.WriteLine("詳細を確認するには --verbose オプションを付けて再実行してください。");
    }
    return 2;
}
