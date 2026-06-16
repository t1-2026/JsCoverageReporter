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
          --channel <name>    ブラウザチャンネルを指定する（例: msedge, chrome）
          --lcov              lcov.info（LCOV 形式）も出力する（VSCode Coverage Gutters・CI 連携用）
          --json              coverage.json（機械可読サマリー）も出力する
          --open              レポート完成後に既定ブラウザで index.html を開く
          --wait              レポート生成（別プロセス）の完了を待つ
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

// --headed が既に指定済みかどうかを示すフラグ（重複指定の検知に使う）
bool headedSet = false;

// --verbose が指定された場合は true（スタックトレースを表示）
bool verbose = false;

// --verbose が既に指定済みかどうかを示すフラグ（重複指定の検知に使う）
bool verboseSet = false;

// --channel で指定されたブラウザチャンネル名（例: "msedge", "chrome"）。未指定なら null
string? channel = null;

// --channel が既に指定済みかどうかを示すフラグ（重複指定の検知に使う）
bool channelSet = false;

// --lcov が指定された場合は true（lcov.info を出力する）
bool writeLcov = false;

// --lcov が既に指定済みかどうかを示すフラグ（重複指定の検知に使う）
bool lcovSet = false;

// --json が指定された場合は true（coverage.json を出力する）
bool writeJson = false;

// --json が既に指定済みかどうかを示すフラグ（重複指定の検知に使う）
bool jsonSet = false;

// --open が指定された場合は true（レポート完成後に既定ブラウザで index.html を開く）
bool open = false;

// --open が既に指定済みかどうかを示すフラグ（重複指定の検知に使う）
bool openSet = false;

// --wait が指定された場合は true（親が子プロセスの完了を待つ）
bool wait = false;

// --wait が既に指定済みかどうかを示すフラグ（重複指定の検知に使う）
bool waitSet = false;

// --report-from で指定されたハンドオフデータファイルのパス（内部用・隠しモード）。未指定なら null
string? reportFrom = null;

// --target-url で指定された対象 URL（内部用・report-from 時に引き継ぐ）。未指定なら null
string? targetUrlOverride = null;

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
        // 制限: "--" で始まるファイルパス（例: --scenario.json）は値として指定できない
        // （実用上そのようなファイル名はほぼ使われないため許容する）
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
        // 制限: "--" で始まるディレクトリパスは値として指定できない
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
        // 同じオプションが複数回指定された場合は警告する
        if (headedSet)
        {
            Console.Error.WriteLine("[Warning] --headed が複数回指定されました。");
        }
        headless   = false;
        headedSet  = true;
    }

    // --verbose オプションを検出する（値なし・エラー時にスタックトレースを表示する）
    else if (args[i] == "--verbose")
    {
        // 同じオプションが複数回指定された場合は警告する
        if (verboseSet)
        {
            Console.Error.WriteLine("[Warning] --verbose が複数回指定されました。");
        }
        verbose    = true;
        verboseSet = true;
    }

    // --channel オプションを検出する（ブラウザチャンネルを指定する: msedge, chrome など）
    else if (args[i] == "--channel")
    {
        // 同じオプションが複数回指定された場合は警告する
        if (channelSet)
        {
            Console.Error.WriteLine("[Warning] --channel が複数回指定されました。最後の値を使用します。");
        }
        // 値が存在しない、または次の引数が別のオプション（--で始まる）の場合はエラーにする
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
        {
            Console.Error.WriteLine("エラー: --channel オプションには値が必要です。例: --channel msedge");
            return 1;
        }
        // 次の引数をチャンネル名として取得する
        channel = args[i + 1];
        // 設定済みフラグを立てる
        channelSet = true;
        // 値として消費した次の引数をスキップする
        i++;
    }

    // --lcov オプションを検出する（値なし・LCOV 形式の lcov.info も出力する）
    else if (args[i] == "--lcov")
    {
        // 同じオプションが複数回指定された場合は警告する
        if (lcovSet)
        {
            Console.Error.WriteLine("[Warning] --lcov が複数回指定されました。");
        }
        writeLcov = true;
        lcovSet   = true;
    }

    // --json オプションを検出する（値なし・機械可読サマリーの coverage.json も出力する）
    else if (args[i] == "--json")
    {
        // 同じオプションが複数回指定された場合は警告する
        if (jsonSet)
        {
            Console.Error.WriteLine("[Warning] --json が複数回指定されました。");
        }
        writeJson = true;
        jsonSet   = true;
    }

    // --open オプションを検出する（値なし・レポート完成後に既定ブラウザで index.html を開く）
    else if (args[i] == "--open")
    {
        // 同じオプションが複数回指定された場合は警告する
        if (openSet)
        {
            Console.Error.WriteLine("[Warning] --open が複数回指定されました。");
        }
        open    = true;
        openSet = true;
    }

    // --wait オプションを検出する（値なし・親が子プロセスの完了を待つ）
    else if (args[i] == "--wait")
    {
        // 同じオプションが複数回指定された場合は警告する
        if (waitSet)
        {
            Console.Error.WriteLine("[Warning] --wait が複数回指定されました。");
        }
        wait    = true;
        waitSet = true;
    }

    // --report-from オプションを検出する（内部用・データファイルからレポートのみ生成する隠しモード）
    else if (args[i] == "--report-from")
    {
        // 値が存在しない、または次の引数が別のオプション（--で始まる）の場合はエラーにする
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
        {
            Console.Error.WriteLine("エラー: --report-from オプションには値が必要です。");
            return 1;
        }
        // 次の引数をハンドオフデータファイルのパスとして取得する
        reportFrom = args[i + 1];
        // 値として消費した次の引数をスキップする
        i++;
    }

    // --target-url オプションを検出する（内部用・report-from 時に対象 URL を引き継ぐ）
    else if (args[i] == "--target-url")
    {
        // 値が存在しない、または次の引数が別のオプション（--で始まる）の場合はエラーにする
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
        {
            Console.Error.WriteLine("エラー: --target-url オプションには値が必要です。");
            return 1;
        }
        // 次の引数を対象 URL として取得する
        targetUrlOverride = args[i + 1];
        // 値として消費した次の引数をスキップする
        i++;
    }
}

// ---- report-from モード（別プロセスのレポート生成専用・隠しモード）----
// このモードでは config ファイルもブラウザ操作も使わず、ハンドオフ JSON からレポートのみを生成する。
if (reportFrom != null)
{
    try
    {
        string json = await File.ReadAllTextAsync(reportFrom);
        var (handoffUrl, coverages) = CoverageHandoff.Deserialize(json);

        // ソースマップは子側で取得する（並列）
        var sourceMaps = await SourceMapLoader.LoadAllAsync(coverages);

        string effectiveTarget = targetUrlOverride ?? handoffUrl;
        new HtmlReportGenerator().Generate(
            coverages, sourceMaps,
            new ReportOptions(outputDir, writeLcov, writeJson, effectiveTarget));

        var indexPath = Path.Combine(outputDir, "index.html");
        Console.WriteLine($"Report: {indexPath}");
        if (open) { ReportProcess.OpenInBrowser(indexPath); }

        // 読み終えたハンドオフファイルを削除する（残骸を残さない）
        try { File.Delete(reportFrom); } catch { }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"レポート生成に失敗しました: {ex.Message}");
        return 2;
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

    // 不明なフィールド名を検出して警告する（タイポ検出: "scriptfilter" → "scriptFilters" など）
    // デシリアライズは不明フィールドを無言で無視するため、先に検査してユーザーに知らせる
    var unknownFields = ScenarioConfig.FindUnknownProperties(json);
    foreach (var fieldName in unknownFields)
    {
        Console.Error.WriteLine($"[Warning] 設定ファイルに不明なフィールドがあります: \"{fieldName}\" — スペルミスの可能性があります。");
    }

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
catch (FileNotFoundException)
{
    // ファイルが見つからない場合は日本語でパスを明示する
    Console.Error.WriteLine($"設定ファイルが見つかりません: {configPath}");
    return 1;
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

// ---- 出力ディレクトリの書き込み可能チェック ----

// ブラウザ操作を開始する前に出力先が書き込み可能かを確認する。
// 書き込み不可のまま進むと、シナリオ実行後の最終ステップでエラーになり
// カバレッジデータが捨てられてしまうため、早期検出してフィードバックを返す。
try
{
    // ディレクトリが存在しない場合は作成を試みる
    Directory.CreateDirectory(outputDir);

    // 実際に書き込めるかどうかを一時ファイルで確認する
    string probeFile = Path.Combine(outputDir, ".write_check_" + Path.GetRandomFileName());
    File.WriteAllText(probeFile, "");
    // 削除失敗は書き込み成功とは無関係のため、誤ったエラーメッセージにならないよう例外を握りつぶす
    // （プローブファイルが残ってもレポート生成には影響しない）
    try { File.Delete(probeFile); } catch { }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"エラー: 出力ディレクトリ '{outputDir}' に書き込みできません: {ex.Message}");
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
    // --channel msedge を指定すると Edge が起動する。null の場合は Playwright 組み込みの Chromium を使う
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        // --headed が指定された場合は false になり、ブラウザウィンドウが表示される
        Headless = headless,
        // --channel が指定された場合はそのチャンネルを使う（例: "msedge", "chrome"）
        Channel = channel,
    });

    // 新しいページ（タブ）を開く
    var page = await browser.NewPageAsync();

    // カバレッジ収集を開始する（CDPでV8 Profilerを有効にする）
    // フィルターを StartAsync に渡すことで中間スナップショット時にも正しく適用される
    await using var collector = new CoverageCollector(page);
    await collector.StartAsync(scenario.ScriptFilters, scenario.ScriptExcludes);

    // timeoutMs が負値の場合は警告して null（Playwright デフォルト: 30秒）に変換する
    // 負値をそのまま Playwright に渡すと ArgumentException が発生する可能性があるため
    int? validTimeoutMs = scenario.TimeoutMs;
    if (validTimeoutMs.HasValue && validTimeoutMs.Value < 0)
    {
        Console.Error.WriteLine($"[Warning] 'timeoutMs' is negative ({validTimeoutMs.Value}) — using Playwright default (30s).");
        validTimeoutMs = null;
    }

    // シナリオで指定されたURLに移動する
    await page.GotoAsync(scenario.Url, new PageGotoOptions { Timeout = validTimeoutMs });

    // シナリオで定義されたアクション（クリックなど）を順番に実行する
    // onBeforeClose: "close" アクション実行直前に CDP スナップショットを取得するコールバック
    // （page.Close イベントは CDP セッション無効化後に発火するため、閉じる前に明示的に取得する必要がある）
    await ActionRunner.RunAsync(page, scenario.Actions, validTimeoutMs, scenario.ContinueOnError,
        onBeforeClose: p => collector.BeforePageCloseAsync(p));

    // カバレッジデータを収集して取得する
    Console.WriteLine("Collecting coverage...");
    // カバレッジデータを取得する（フィルターは StartAsync で渡し済み）
    var coverages = await collector.StopAsync();
    // 取得したスクリプト数を表示する
    Console.WriteLine($"  {coverages.Count} script(s) captured.");

    // ソースマップ（//# sourceMappingURL）の取得を試みる
    // 取得できたスクリプトは元ファイル（TypeScript 等）別の行カバレッジもレポートに表示される
    // 取得・解析の失敗は警告のみでレポート生成は続行する
    var sourceMaps = await SourceMapLoader.LoadAllAsync(coverages);
    if (sourceMaps.Count > 0)
    {
        Console.WriteLine($"  {sourceMaps.Count} source map(s) resolved.");
    }

    // HTMLレポートを生成して出力ディレクトリに書き出す（--lcov / --json 指定時は機械可読形式も出力する）
    // 対象 URL を渡してインデックスのメタ情報（いつ・何に対する計測か）に表示する
    new HtmlReportGenerator().Generate(coverages, outputDir, sourceMaps, writeLcov, writeJson, scenario.Url);
    // 生成したレポートのパスを表示する
    Console.WriteLine($"Report: {Path.Combine(outputDir, "index.html")}");
    if (writeLcov)
    {
        Console.WriteLine($"LCOV:   {Path.Combine(outputDir, "lcov.info")}");
    }
    if (writeJson)
    {
        Console.WriteLine($"JSON:   {Path.Combine(outputDir, "coverage.json")}");
    }

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
