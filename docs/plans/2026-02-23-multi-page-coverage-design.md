# 複数ページ対応カバレッジ収集 — 設計文書

**日付:** 2026-02-23

**目的:** シナリオ実行中にリンクのクリック等で新しいブラウザタブが開いた場合、そのタブのカバレッジも自動で収集し、1つのレポートにまとめて出力する。

---

## 背景・問題

現在の実装は `CoverageCollector` が単一の `IPage`（ブラウザタブ）にのみ紐づいている。シナリオの実行中に新しいタブが開いても、そのタブのカバレッジは収集されない。

---

## ゴール

- 新しいタブが開いたとき自動でカバレッジ収集を開始する（シナリオ JSON の変更不要）
- 全タブ分の収集結果を1つのレポートにまとめる
- レポートで「どのページ（タブ）のスクリプトか」を識別できるようにする

---

## 設計

### 1. データモデル（`CoverageData.cs`）

`PageInfo` レコードを新規追加し、`ScriptCoverage` に組み込む。

```csharp
/// <summary>
/// スクリプトの収集元ページ（タブ）の情報を表すレコード。
/// </summary>
internal record PageInfo(
    int    Index,  // タブ番号（0始まり。開かれた順に採番される）
    string Url     // StopAsync 直前に取得した page.Url
);

/// <summary>（変更）PageInfo フィールドを追加</summary>
internal record ScriptCoverage(
    PageInfo                    Page,      // ← 追加
    string                      Url,
    string                      Source,
    IReadOnlyList<FunctionCoverage> Functions
);
```

### 2. `CoverageCollector.cs`

#### 変更方針

- コンストラクタの引数は `IPage`（初期ページ）のまま変更しない
- 内部で `page.Context.Page` イベントを購読し、新タブを自動検出
- 各ページの CDP セットアップを `SetupPageAsync()` に切り出す
- `StopAsync` で全ページ分のカバレッジを収集・統合する

#### 新規フィールド

```csharp
private readonly List<IPage>              _trackedPages    = [];
private readonly Dictionary<IPage, ICDPSession> _cdpSessions = [];
private readonly List<Task>               _pageSetupTasks  = [];
private int                               _tabCounter      = 0;
```

#### `StartAsync` の変更

```csharp
public async Task StartAsync()
{
    // 初期ページのカバレッジを開始する
    await SetupPageAsync(_initialPage);

    // 新しいタブが開いたとき自動でカバレッジを開始する
    _initialPage.Context.Page += (_, newPage) =>
    {
        var task = SetupPageAsync(newPage);
        lock (_pageSetupTasks)
        {
            _pageSetupTasks.Add(task);
        }
    };
}
```

#### `SetupPageAsync`（新規メソッド）

各ページに CDP セッションを作成し、`Profiler.startPreciseCoverage` を開始する。
現在の `StartAsync` の処理をそのまま抽出・汎用化する。

#### `StopAsync` の変更

1. `_pageSetupTasks` を `await Task.WhenAll(...)` で完了待機する
2. 全追跡ページに対して `Profiler.takePreciseCoverage` を実行する
3. `page.Url` で `PageInfo` を作成し `ScriptCoverage` に含める
4. 全 CDP セッションのクリーンアップを行う（`Profiler.disable`・`Debugger.disable`）

#### `DisposeAsync` の変更

`_cdpSessions` の全セッションを解放する。

### 3. `Program.cs` — 変更なし

`CoverageCollector` のコンストラクタ引数は `IPage` のまま変わらないため、`Program.cs` の変更は不要。

### 4. `HtmlReportGenerator.cs`

#### ファイル命名規則

```
scripts/
  script-{グローバル連番}-tab{タブ番号}.html
```

例:
- `script-0-tab0.html` — tab0（最初のページ）の1番目のスクリプト
- `script-1-tab0.html` — tab0 の2番目のスクリプト
- `script-2-tab1.html` — tab1（2枚目のタブ）の1番目のスクリプト

#### `index.html`（一覧ページ）

テーブルに「ページ URL」列を追加する。

| ページ URL | スクリプト URL | カバー済み | 部分カバー | 対象行数 | カバレッジ率 |
|-----------|--------------|----------|----------|--------|------------|

- `PageInfo.Url` が空文字列の場合は `(tab {Index})` と表示する

#### `scripts/script-N-tabM.html`（詳細ページ）

- ヘッダー（h1 または legend エリア）に「ページ URL / スクリプト URL」を表示する
- `← 一覧に戻る` リンクは変更なし

---

## 変更ファイル一覧

| ファイル | 変更内容 |
|---------|---------|
| `JsCoverageReporter/Coverage/CoverageData.cs` | `PageInfo` レコード追加、`ScriptCoverage` に `Page` フィールド追加 |
| `JsCoverageReporter/Coverage/CoverageCollector.cs` | 複数ページ管理、イベント購読、`SetupPageAsync` 分離 |
| `JsCoverageReporter/Report/HtmlReportGenerator.cs` | ファイル命名、index.html にページ列、詳細ページにページ URL 表示 |
| `JsCoverageReporter.Tests/Report/CoverageMapTests.cs` | `ScriptCoverage` の引数変更に合わせてテスト修正 |
| `JsCoverageReporter.Tests/Report/HtmlOutputTests.cs` | 同上 |
| `JsCoverageReporter.Tests/SampleReportTests.cs` | `PageInfo` を含む `ScriptCoverage` 生成に修正 |

---

## 考慮事項

- **タイミング問題**: 新タブが開いてすぐに JavaScript が実行される場合、`SetupPageAsync` の完了前に一部のスクリプトが実行される可能性がある。これは CDP のイベント駆動モデルの制約であり、初回ロードのスクリプトはほぼ捕捉できる。
- **エラー処理**: `SetupPageAsync` が失敗した場合は警告を出力して継続する（`ContinueOnError` の挙動と同様）。
- **後方互換性**: シナリオ JSON の形式は変更しない。単一タブのシナリオは従来通り動作する。

---

## 却下した代替案

- **アプローチ B（アクション後スキャン）**: タイミングによりスクリプトの取り逃しが発生するため不採用
- **アプローチ C（`switchPage` アクション）**: JSON フォーマットの変更が必要で、自動追跡もできないため不採用
