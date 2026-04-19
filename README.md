# JsCoverageReporter

JsCoverageReporter は、Playwright と Chrome DevTools Protocol (CDP) を使って Web ページの JavaScript カバレッジを計測し、見やすい HTML レポートを生成するツールです。

## 特徴
- Playwright を用いたエンドツーエンド（E2E）ブラウザ操作シナリオの実行
- V8 Profiler (CDP) による正確なカバレッジ計測
- 複数ページ間・複数タブ間にまたがる同一スクリプトのカバレッジマージ機能
- 視覚的で分かりやすい HTML カバレッジレポートの出力

⚠️ **注意点**: 本ツールはアクセスしたウェブサイトを操作し、カバレッジを収集します。必ず**テスト対象サイトのオーナーの許可のもと**使用してください。

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
```
インストールされていない場合: https://dotnet.microsoft.com/download/dotnet/8.0

### Node.js のインストール確認
```bash
node --version
```
インストールされていない場合: https://nodejs.org/

---

## 2. セットアップとビルド

リポジトリをクローンした直後のルートディレクトリで実行します。

```bash
# NuGet パッケージの復元とビルド
dotnet build
```

---

## 3. Playwright ブラウザのインストール

Playwright は Chromium などのテスト用ブラウザを別途ダウンロードする必要があります。

```bash
# ビルド後に生成されたスクリプトでブラウザをインストールする
dotnet run --project JsCoverageReporter -- install chromium
```
> **注意**: インターネット接続が必要です（約 200MB のダウンロードが発生します）。

---

## 4. 動作確認

```bash
# ヘルプを表示して動作確認する
dotnet run --project JsCoverageReporter -- --help
```

---

## 5. シナリオ JSON の作成

計測したいページと自動操作の内容を JSON ファイルに記述します。

### 最小構成の例

```json
{
  "url": "https://example.com"
}
```

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

### サポートされているアクション (`actions`)
- `click` (selector)
- `fill` (selector, value)
- `navigate` (url)
- `waitForSelector` (selector)
- `hover` (selector)
- `press` (selector, value)
- `wait` (milliseconds)

---

## 6. カバレッジレポートの生成

```bash
dotnet run --project JsCoverageReporter -- \
  --config scenario.json \
  --output ./report
```

実行後、`--output` で指定したディレクトリに `index.html` などの HTML 形式のレポートが生成されます。`index.html` をブラウザで開いて確認してください。

---

## ライセンス

本プロジェクトは **MIT License** で提供されています。詳細は `LICENSE` ファイルを参照してください。
また、本ツールが依存する OSS のライセンス情報については `docs/LICENSES.md` をご確認ください。
