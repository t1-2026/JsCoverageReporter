# JsCoverageReporter セットアップ手順書

## 概要

JsCoverageReporter は、Playwright と Chrome DevTools Protocol (CDP) を使って Web ページの JavaScript カバレッジを計測し、HTML レポートを生成するツールです。

---

## 1. 動作環境

| 項目 | バージョン | 備考 |
|------|-----------|------|
| OS | Windows 10/11、macOS 12+、Ubuntu 20.04+ | |
| .NET SDK | **8.0 以上** | ビルドおよび実行に必要 |
| Node.js | **18 以上** | Playwright のブラウザダウンロードスクリプトに必要 |

### .NET SDK のインストール確認

```bash
dotnet --version
# 出力例: 8.0.xxx
```

インストールされていない場合: https://dotnet.microsoft.com/download/dotnet/8.0

### Node.js のインストール確認

```bash
node --version
# 出力例: v18.x.x または v20.x.x
```

インストールされていない場合: https://nodejs.org/

---

## 2. ソースコードのコピー

### コピーが必要なファイル・フォルダ

以下のファイル・フォルダを新しい環境のフォルダへコピーしてください。

```
JsCoverageReporter/           ← メインプロジェクト
    JsCoverageReporter.csproj
    Program.cs
    Actions/
    Config/
    Coverage/
    Report/
JsCoverageReporter.Tests/     ← テストプロジェクト（任意）
    JsCoverageReporter.Tests.csproj
    ...
JsCoverageReporter.sln        ← ソリューションファイル
```

### コピー不要なファイル・フォルダ（除外してよいもの）

```
*/bin/         ← ビルド成果物（自動生成される）
*/obj/         ← 中間ファイル（自動生成される）
*.user         ← Visual Studio のユーザー設定
.vs/           ← Visual Studio のワークスペース情報
dist/          ← 発行済みバイナリ
*-report/      ← レポート出力例
*-scenario.json ← サンプルシナリオ（任意でコピー）
```

---

## 3. ビルド

コピーしたフォルダのルート（`JsCoverageReporter.sln` があるディレクトリ）で実行します。

```bash
# NuGet パッケージの復元とビルド
dotnet build
```

ビルドが成功すると以下のように表示されます。

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

---

## 4. Playwright ブラウザのインストール

Playwright は Chromium などのブラウザを別途ダウンロードする必要があります。

```bash
# ビルド後に生成されたスクリプトでブラウザをインストールする
dotnet run --project JsCoverageReporter -- install chromium
```

> **注意**: インターネット接続が必要です（約 200MB のダウンロードが発生します）。

インストールされたブラウザは OS のユーザーフォルダ内（例: `%USERPROFILE%\AppData\Local\ms-playwright\`）に保存されます。

### オフライン環境の場合

既にブラウザがインストール済みの別マシンから、以下のフォルダをコピーします。

| OS | パス |
|----|------|
| Windows | `%USERPROFILE%\AppData\Local\ms-playwright\` |
| macOS | `~/Library/Caches/ms-playwright/` |
| Linux | `~/.cache/ms-playwright/` |

---

## 5. 動作確認

```bash
# ヘルプを表示して動作確認する
dotnet run --project JsCoverageReporter -- --help
```

以下のように表示されれば正常です。

```
使い方:
  JsCoverageReporter --config <scenario.json> [オプション]
...
```

---

## 6. シナリオ JSON の作成

計測したいページと操作内容を JSON ファイルに記述します。

### 最小構成の例

```json
{
  "url": "https://example.com"
}
```

### フィールド一覧

| フィールド | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `url` | string | **必須** | 計測対象ページの URL |
| `scriptFilters` | string[] | 任意 | レポートに含めるスクリプト URL のキーワード配列（省略時は全スクリプト） |
| `scriptExcludes` | string[] | 任意 | レポートから除外するスクリプト URL のキーワード配列 |
| `timeoutMs` | number | 任意 | 各アクションのタイムアウト（ミリ秒）。省略時は Playwright のデフォルト（30000ms） |
| `continueOnError` | boolean | 任意 | アクション失敗時に続行するか（デフォルト: `false`） |
| `actions` | array | 任意 | 実行するアクションのリスト |

### アクションの種類

| `type` | 必要なフィールド | 説明 |
|--------|----------------|------|
| `click` | `selector` | 要素をクリックする |
| `fill` | `selector`, `value` | テキストボックスに値を入力する |
| `navigate` | `url` | 別の URL へ遷移する |
| `waitForSelector` | `selector` | 要素が表示されるまで待機する |
| `hover` | `selector` | 要素にマウスホバーする |
| `press` | `selector`, `value` | キーを押下する（例: `"Enter"`） |
| `wait` | `milliseconds` | 指定ミリ秒待機する |

### 実用的な例

```json
{
  "url": "https://your-app.example.com/",
  "scriptFilters": ["app.js", "main.js"],
  "scriptExcludes": ["vendor.js", "polyfill.js"],
  "timeoutMs": 10000,
  "continueOnError": false,
  "actions": [
    { "type": "waitForSelector", "selector": "#login-button" },
    { "type": "fill", "selector": "#username", "value": "testuser" },
    { "type": "fill", "selector": "#password", "value": "testpass" },
    { "type": "click", "selector": "#login-button" },
    { "type": "wait", "milliseconds": 1000 },
    { "type": "click", "selector": "#menu-item-top" }
  ]
}
```

---

## 7. レポートの生成

```bash
dotnet run --project JsCoverageReporter -- \
  --config scenario.json \
  --output ./report
```

### オプション一覧

| オプション | デフォルト | 説明 |
|-----------|-----------|------|
| `--config <path>` | （必須）| シナリオ JSON ファイルのパス |
| `--output <dir>` | `./report` | レポートの出力先ディレクトリ |
| `--headed` | （なし）| ブラウザウィンドウを表示して実行する |
| `--verbose` | （なし）| エラー時に詳細なスタックトレースを表示する |

実行後、`--output` で指定したディレクトリに以下のファイルが生成されます。

```
report/
    index.html          ← スクリプト一覧ページ（ここを開く）
    script-0.html       ← 各スクリプトの詳細ページ
    script-1.html
    ...
```

`index.html` をブラウザで開くとカバレッジレポートが確認できます。

---

## 8. 終了コード

| コード | 意味 |
|--------|------|
| `0` | 正常終了 |
| `1` | 設定エラー（引数・設定ファイルの不備） |
| `2` | 実行時エラー（ブラウザ操作・ネットワーク・CDP エラー） |

---

## 9. トラブルシューティング

### ブラウザが見つからないエラー

```
Error: browserType.launch: Failed to launch chromium
```

→ 手順 4 の Playwright ブラウザインストールを再実行してください。

### タイムアウトエラー

```
[Error] アクション実行中にエラーが発生しました: Timeout ...
```

→ シナリオ JSON の `timeoutMs` を大きな値（例: `30000`）に変更してください。

### dotnet コマンドが見つからない

→ .NET 8 SDK がインストールされていない、またはパスが通っていません。
インストール後にターミナルを再起動してください。
