# Coverage.cs / Report.cs 移植ガイド

`Coverage/Coverage.cs`（カバレッジ収集）と `Report/Report.cs`（HTML レポート生成）を
他プロジェクトへ移植して使うための注意点・使い方をまとめる。

---

## 1. 全体像：2 つの独立した層

この 2 ファイルは**役割が分かれており、依存方向は一方向**である。

```
[ブラウザ/Playwright] → Coverage.cs → (ScriptCoverage の列) → Report.cs → [HTML/LCOV/JSON]
                          収集層                  DTO              生成層
```

| ファイル | 役割 | 外部依存 | 移植難易度 |
|---|---|---|---|
| `Coverage.cs` | CDP 経由で JS カバレッジを収集 | **Microsoft.Playwright 必須** | 高（Playwright 環境が前提） |
| `Report.cs` | 収集済みデータから HTML レポートを生成 | **入力 DTO とファイル書き込みのみ**（Playwright 非依存） | 低（自己完結） |

**重要:** 2 つをつなぐのは `ScriptCoverage` 等の DTO だけ。Report.cs はブラウザ・ネットワーク・
Playwright を一切知らない。**「レポート生成だけ欲しい」なら Report.cs ＋ DTO だけ移植すればよい。**

---

## 2. 前提環境

- **ターゲットフレームワーク**: `net8.0`（C# の collection expression `[]`、primary constructor、
  raw string literal、`Environment.TickCount64` を使用。net6/7 へ落とすと一部書き換えが必要）。
- **Nullable**: プロジェクトは `<Nullable>enable</Nullable>`。
  - `Coverage.cs` は `#nullable enable annotations`（`ICDPSession?` 等の注釈を使う）。
  - `Report.cs` は先頭で `#nullable disable`（null 合体を避け if/else で明示分岐する方針）。
  - 移植先の Nullable 設定が違っても、この 2 ファイル冒頭の `#nullable` ディレクティブで
    ファイル単位に上書きされるため**そのままコピーして動く**。
- **可視性**: クラス・DTO はすべて `internal`。別アセンブリから呼ぶ場合は次のいずれか。
  - 同一アセンブリに入れる（最も簡単）、
  - `public` へ変更する、
  - `[assembly: InternalsVisibleTo("呼び出し側")]` を付ける。
- **名前空間**: `JsCoverageReporter.Coverage` / `JsCoverageReporter.Report`。移植先に合わせて
  リネーム可。ただし Report.cs は `using JsCoverageReporter.Coverage;` で DTO を参照するので、
  DTO の名前空間とセットで変更すること。

---

## 3. つなぎの契約（DTO）— 移植の核心

Report.cs が依存する入力 DTO は `Coverage.cs` の末尾に定義された 4 つの record だけ。
**この 4 つが移植契約の境界**である（Report.cs 冒頭の「移植契約」コメントを参照）。

```csharp
record PageInfo(int Index, string Url);                 // タブ番号(0始まり) と ページURL
record ScriptCoverage(PageInfo Page, string Url,        // スクリプト1本のカバレッジ
                      string Source, IReadOnlyList<FunctionCoverage> Functions);
record FunctionCoverage(string FunctionName,            // 関数1つ分
                        IReadOnlyList<CoverageRange> Ranges);
record CoverageRange(int StartOffset, int EndOffset,    // 実行範囲（文字オフセット, 実行回数）
                     int Count);
```

オフセットは**バイトではなく文字数**。`Count == 0` は未実行。

### Count の特殊値
`Count == int.MaxValue` は「V8 のホットな関数で実行回数が int を超えた（約 21 億回以上）」
ことを表す慣習値。Report 側はこれを「21 億回以上」と表示する。**移植時にこの 2 ファイルを
セットで使えば整合する**が、自前で DTO を組み立てる場合はこの規約を踏襲すること。

---

## 4. Coverage.cs の使い方と注意点

### 4.1 依存
- **Microsoft.Playwright**（`PackageReference Include="Microsoft.Playwright"`）が必須。
  `IPage` を 1 つ渡してインスタンス化する。
- ブラウザ起動・Playwright のインストール（`playwright install`）は**呼び出し側（ホスト層）の責務**。

### 4.2 ライフサイクル
```csharp
await using var collector = new CoverageCollector(page);
await collector.StartAsync(scriptFilters, scriptExcludes);   // 収集開始（フィルタは任意）
// … ページ操作・ナビゲーション …
// ページを JS 側でなくコード側で閉じる直前には明示的に：
await collector.BeforePageCloseAsync(targetPage);            // close 前スナップショット
var coverages = await collector.StopAsync();                 // 収集停止・データ取得
```

### 4.3 注意点
- **`StartAsync` は初期ページ以外に新規タブも自動追跡**する（`Context.Page` イベント購読）。
  複数タブ・`window.open` も拾う。
- **`BeforePageCloseAsync` を「コードからページを閉じる」前に必ず呼ぶこと**。
  `page.Close` イベントは CDP セッション無効化後に発火するため、イベント任せだと
  閉じる直前のカバレッジを取りこぼす。JS の `window.close()` 経由はルート傍受で自動対応済み。
- **フィルタ（`scriptFilters` / `scriptExcludes`）は URL の部分一致**（大文字小文字無視）。
  `StartAsync` に渡せば中間スナップショットにも一貫適用される。内部で防衛コピーするので、
  呼び出し後に渡したリストを変更しても影響しない。
- **`StopAsync` / `DisposeAsync` は二重呼び出し安全**。`await using` で破棄すれば
  Profiler/Debugger の停止と CDP セッション解放まで自動で行う。`StopAsync` を呼ばずに
  Dispose した場合も Profiler/Debugger を停止する。
- **スレッド安全性**: 内部状態は単一の `_lock` とページ単位 `SemaphoreSlim` で保護済み。
  バックグラウンドのスナップショットタスクと `StopAsync` のレースは設計で吸収している。
  **ロック設計を理解せずに `lock` の範囲を変えないこと。**
- スナップショットのスロットリング間隔は `SnapshotThrottleMs`（既定 500ms）。テストでは 0 に
  設定して無効化できる。

---

## 5. Report.cs の使い方と注意点

### 5.1 依存（あえて少なくしてある）
- 入力 DTO（§3）＋ 注入される `IReadOnlyDictionary<string, SourceMap>` のみ。
- **ネットワーク I/O・ブラウザ操作・Playwright 依存は持ち込まない**（冒頭コメントの移植契約）。
- 出力はファイル書き込み（`outputDir` 配下）と、警告の `Console.Error` 出力のみ。

### 5.2 呼び出し
```csharp
var sourceMaps = /* なければ */ new Dictionary<string, SourceMap>(); // または null
new HtmlReportGenerator().Generate(
    coverages,                                   // IReadOnlyList<ScriptCoverage>
    sourceMaps,                                  // null 可（ソースマップ機能を使わない場合）
    new ReportOptions(
        OutputDir: outputDir,                    // 出力先（自動作成。scripts/ も作る）
        WriteLcov: false,                        // lcov.info を出すか
        WriteJson: false,                        // coverage.json を出すか
        TargetUrl: null));                       // index のメタ情報に出す対象URL（任意）
```
- 旧シグネチャ（`Generate(coverages, sourceMaps, outputDir, writeLcov, writeJson, targetUrl)`）も
  後方互換で残っており、内部で `ReportOptions` に詰めて単一エントリへ委譲する。
  **新規移植では `ReportOptions` 版を使うこと**（オプション集約が単一ソース）。

### 5.3 出力物
- `index.html`（一覧）、`scripts/script-N.html`（合成詳細）、URL 別の `script-N-tabK.html`、
  ソースマップ解決時は `script-N-src-K.html`。
- 任意で `lcov.info`（BOM なし UTF-8）、`coverage.json`。

### 5.4 注意点
- **`outputDir` は自動作成**されるが、**既存ファイルは上書き**する。専用の出力先を渡すこと。
- **BOM（U+FEFF）処理は Report 側で吸収済み**。V8 のオフセットは BOM 除去後位置を指すため、
  Report はソース先頭 BOM を除去し、グループ化キーからも除去する。**移植先で DTO の Source に
  BOM を付けたまま渡してよい**（Report が落とす）。
- **同一 URL でも Source が異なれば別グループ**として扱う（タイムスタンプコメント等で誤結合しない）。
- **1 行スクリプトはスキップ**される（inline eval・極小スクリプト）。ただしソースマップがある場合は
  ミニファイ本番バンドル想定でスキップしない。スキップ時は `Console.Error` に警告。
- **並列処理**: `Generate` 内部でグループ計算を `Parallel.For` で並列化している。
  - 呼び出すヘルパは全て静的な純関数で、可変な共有状態を持たないため**スレッド安全・出力は決定的**。
  - 警告（`Console.Error`）の**出力順だけは非決定的**になりうる（生成物には影響なし）。
  - 1 グループの計算が例外を投げても、そのグループだけスキップして他は生成する
    （全レポート喪失を防ぐ）。
  - メモリ: 集約フェーズで処理済みグループの参照を解放し滞留を抑えているが、`Parallel.For`
    完了直後は全グループの中間結果が同時に存在するため、**巨大バンドル多数のときはピークメモリに注意**。

---

## 6. ソースマップ機能（任意）

- `SourceMapLoader.LoadAllAsync(coverages)` が各スクリプトの `//# sourceMappingURL` を解決し、
  マップ JSON を**並列取得・解析**して `Dictionary<string, SourceMap>` を返す。
- **このローダはネットワーク／ファイル取得を行う**ため、Report.cs の「I/O 非依存」契約の**外側**。
  ソースマップ機能が不要なら **`Generate` に `null` を渡せばよい**（ローダごと移植しなくてよい）。
- `SourceMap.Parse` は Source Map v3 のみ対応。**`sections`（インデックスマップ）形式は非対応で
  `null` を返す**。`sourcesContent` は `sources` と同じ長さに null パディングされる。
- 壊れたマッピング（範囲外の source index 等）は内部で破棄され、添字アクセスは安全。

---

## 7. エンドツーエンドの使い方

### パターン A: 同一プロセスで収集 → 生成
```csharp
await using var collector = new CoverageCollector(page);
await collector.StartAsync(filters, excludes);
// … 操作 …
var coverages = await collector.StopAsync();

var sourceMaps = await SourceMapLoader.LoadAllAsync(coverages); // 任意
new HtmlReportGenerator().Generate(
    coverages, sourceMaps, new ReportOptions(outputDir));
```

### パターン B: 別プロセスへハンドオフ（本プロジェクトの既定構成）
収集プロセスとレポート生成プロセスを分離したい場合、`CoverageHandoff` で JSON 受け渡しする。

```csharp
// 収集側プロセス
var coverages = await collector.StopAsync();
string json = CoverageHandoff.Serialize(targetUrl, coverages);
await File.WriteAllTextAsync(handoffPath, json);
// → 別プロセスを起動して handoffPath を渡す（ReportProcess.SpawnReport 等）

// レポート側プロセス
var (handoffUrl, coverages) = CoverageHandoff.Deserialize(json);
var sourceMaps = await SourceMapLoader.LoadAllAsync(coverages); // 子側で取得
new HtmlReportGenerator().Generate(
    coverages, sourceMaps, new ReportOptions(outputDir, targetUrl: handoffUrl));
```
- `CoverageHandoff` の JSON フォーマット自体も移植契約を兼ねる。DTO に手を入れたら
  シリアライズ互換が崩れる点に注意（収集側とレポート側のバージョンを揃える）。
- プロセス起動の段取り（`Environment.ProcessPath` 再実行・引数組み立て）は `ReportProcess.cs`
  にあるが、これは**ホスト層**であり Report.cs の自己完結性には含まれない。

---

## 8. 実行形態別の組み込み例（GUI / CUI）

収集（Playwright）はホスト責務。下記はいずれも「収集 → `Generate` → 結果表示」を
**同一プロセス**で行う最小形（プロセス分離が要るほど巨大でない一般的ケース向け）。
`coverages` の取得は §4 の収集雛形（`StartAsync` は移動前 → 操作 → `StopAsync`）を使う。

### 8.1 GUI 組み込み（同一プロセス・推奨）

GUI から「ボタン → テスト実行 → レポート生成 → 結果確認」を行うケース。
ポイントは **(a) 重い `Generate` を UI スレッドで実行しない、(b) 完了後に index.html を開く**。

```csharp
private async void OnRunTestsClicked(object sender, EventArgs e)
{
    runButton.IsEnabled = false;
    statusLabel.Text = "テスト実行中...";
    try
    {
        // 1) 収集（§4 の雛形。await でUIは固まらない）
        IReadOnlyList<ScriptCoverage> coverages = await RunPlaywrightAndCollectAsync();

        // 2) ソースマップ取得（任意。不要なら null を渡してこの行を省く）
        statusLabel.Text = "レポート生成中...";
        var sourceMaps = await SourceMapLoader.LoadAllAsync(coverages);

        // 3) ★重い Generate はバックグラウンドスレッドへ（UIスレッドを塞がない）
        var outputDir = Path.Combine(AppContext.BaseDirectory, "report");
        await Task.Run(() =>
            new HtmlReportGenerator().Generate(
                coverages, sourceMaps, new ReportOptions(outputDir)));

        // 4) 結果確認：既定ブラウザで開く（or WebView2 に index.html をロード）
        var index = Path.Combine(outputDir, "index.html");
        Process.Start(new ProcessStartInfo(index) { UseShellExecute = true });
        statusLabel.Text = "完了";
    }
    catch (Exception ex)
    {
        statusLabel.Text = "失敗: " + ex.Message;   // GUIなので例外をそのまま表示できる
    }
    finally { runButton.IsEnabled = true; }
}
```

注意点:
- **UIスレッドを塞がない。** `Generate` は `Parallel.For` ＋ファイル書き込みで重い → `Task.Run` で隔離。
- 長寿命の GUI プロセスで**巨大レポートを繰り返し生成**してメモリ残留が問題になったら、§7 パターンB
  （別プロセス）へ切り替える。通常規模では同一プロセスで十分。
- 結果表示は index.html を開くだけ。埋め込み表示したいなら WebView2 に `file://.../index.html` をロード。

### 8.2 CUI（コンソール）から実行・同一プロセス

最小のコンソールアプリ。収集 → 生成 → （任意で）ブラウザを開くまでを1プロセスで完結する。

```csharp
// Program.cs（最小CLI・同一プロセス）
using Microsoft.Playwright;
using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

string url       = args.Length > 0 ? args[0] : "https://example.com/";
string outputDir = args.Length > 1 ? args[1] : "report";

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(
    new BrowserTypeLaunchOptions { Headless = true });
var page = await browser.NewPageAsync();

await using var collector = new CoverageCollector(page);
await collector.StartAsync();                       // ★移動より前
await page.GotoAsync(url);
// … 必要な操作 …
var coverages = await collector.StopAsync();

var sourceMaps = await SourceMapLoader.LoadAllAsync(coverages);   // 任意（null可）
new HtmlReportGenerator().Generate(
    coverages, sourceMaps, new ReportOptions(outputDir, TargetUrl: url));

var index = Path.Combine(outputDir, "index.html");
Console.WriteLine($"Report: {index}");
// 開きたい場合のみ:
// Process.Start(new ProcessStartInfo(index) { UseShellExecute = true });
return 0;
```

実行: `mytool https://example.com/ ./report`

### 8.3 CUI（コンソール）から実行・プロセス分離

本プロジェクトの既定構成。**収集プロセスがレポート生成を別プロセスへ委譲**する。
巨大レポート・バックグラウンド生成・障害分離が要る場合向け（§5・§7 パターンB を参照）。

- 収集側: `coverages` を `CoverageHandoff.Serialize` でファイルに書き出し、`ReportProcess.SpawnReport`
  で**自分自身の実行ファイルを `report-from` 隠しモードで再起動**する。
  実装は本家 [`Program.cs`](JsCoverageReporter/Program.cs) の収集パスがそのまま雛形になる。
- レポート側: 同じ exe が `report-from` 引数で起動され、`CoverageHandoff.Deserialize` →
  `SourceMapLoader.LoadAllAsync` → `Generate` を実行する（[`Program.cs:306-321`](JsCoverageReporter/Program.cs:306)）。
- 同期/デタッチ・終了コード伝播・進捗ウィンドウ判定は `ReportProcess.cs` を流用する。

```text
[mytool run url ...]   収集 → handoff.json → SpawnReport(自exe report-from ...)
[mytool report-from h] handoff.json → Generate → report/index.html
```

> 同一実行ファイルに2つの入口（通常モード / `report-from` 隠しモード）を持たせる点に注意。
> GUI アプリに同梱する場合は、GUI exe を再起動するのではなく**レポート生成用の小さな別 console exe**
> を用意して起動する方が素直。

---

## 9. 移植チェックリスト

- [ ] **レポートだけ欲しい** → `Report/Report.cs` ＋ DTO 4 種（`Coverage.cs` 末尾の record）を移植。
      `sourceMaps` は `null` でよい。Playwright 不要。
- [ ] **収集も欲しい** → さらに `Coverage/Coverage.cs` ＋ `Microsoft.Playwright` を追加。
      ブラウザ起動・`playwright install` はホスト側で用意。
- [ ] **ソースマップ解決も欲しい** → `SourceMapLoader.cs` を追加（ネットワーク／ファイル取得が発生）。
- [ ] **別プロセス分離したい** → `CoverageHandoff.cs`（＋必要なら `ReportProcess.cs`）を追加。
- [ ] `internal` のまま使えるか確認（別アセンブリなら `public` 化 or `InternalsVisibleTo`）。
- [ ] 名前空間を変える場合は DTO と Report.cs の `using` を合わせる。
- [ ] ターゲットが net8.0 未満なら `[]` / primary constructor / raw string literal を書き換える。
- [ ] 出力先 `outputDir` は専用ディレクトリ（既存ファイルを上書きするため）。
