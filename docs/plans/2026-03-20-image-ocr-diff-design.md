# Image OCR Diff ツール 設計ドキュメント

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:writing-plans to create an implementation plan from this design.

**Goal:** 2つのディレクトリ内の画像をペアリングし、OCR でテキスト抽出・差分比較して HTML レポートを生成する CLI ツール。

**Architecture:** C# CLI が NDLOCR-Lite（Python）をサブプロセスで呼び出して OCR を行い、DiffPlex で差分計算、HTML レポートを生成する。Python Embedded を同梱してオフライン・Python インストール不要で配布可能にする。

**Tech Stack:** C# (.NET 8, self-contained)、NDLOCR-Lite（Python、CC BY 4.0）、DiffPlex NuGet（Apache 2.0）、Python Embedded

---

## セクション 1 — アーキテクチャ全体像

```
[入力]
  before/  after/  （ディレクトリ2つ）
       ↓
[C# CLI エントリポイント]
  MyOcrDiff.exe before/ after/ --output diff-report/
       ↓
[ファイルペアリング]
  ファイル名が一致するものをペアにする
  どちらかにしかないファイルは「追加」「削除」として記録
       ↓
[OCR サブプロセス × 2（各ペアを並列実行）]
  python/python.exe ndlocr-lite/src/ocr.py --sourceimg img.png --output tmp/<guid>/
       ↓
[テキスト読み込み]
  tmp/<guid>/img.txt → string text
       ↓
[差分計算（DiffPlex）]
  ① 行単位 diff（行の追加・削除・変更を検出）
  ② 変更行内の単語単位 diff（変更行のみ）
       ↓
[HTML レポート生成]
  diff-report/index.html          ← サマリー（全ペア一覧）
  diff-report/details/imgXXX.html ← ペアごとの差分詳細
```

---

## セクション 2 — CLI インターフェース

```bash
# 基本
MyOcrDiff.exe before/ after/

# 出力先指定
MyOcrDiff.exe before/ after/ --output ./diff-report

# ヘルプ
MyOcrDiff.exe --help
```

### 引数仕様

| 引数 | 必須 | 内容 |
|---|---|---|
| `dir1` `dir2` | ✅ | 比較するディレクトリパス（位置引数） |
| `--output <dir>` | 任意 | 出力先（デフォルト: `./report`） |
| `--help` | 任意 | ヘルプ表示 |

### 終了コード

| コード | 意味 |
|---|---|
| 0 | 正常終了 |
| 1 | 引数・設定エラー |
| 2 | 実行時エラー（全ペア失敗など） |

### ペアリング仕様

- `dir1/` と `dir2/` でファイル名が一致するものを自動ペアリング
- どちらかにしかないファイルはサマリーに「追加」「削除」として記録（詳細ページは生成しない）
- 対応拡張子: `.png` `.jpg` `.jpeg` `.tiff` `.bmp`

---

## セクション 3 — OCR 統合・差分計算

### OCR 呼び出し

- 各ペアの2枚を `Task.WhenAll` で並列実行
- 一時フォルダは `%TEMP%/myocrdiff_<guid>/` に作成し、完了後に削除
- `python/python.exe` のパスは実行ファイルからの相対パスで解決

```csharp
Task ocr1 = RunOcrAsync(img1, tmpDir1);
Task ocr2 = RunOcrAsync(img2, tmpDir2);
await Task.WhenAll(ocr1, ocr2);
string text1 = File.ReadAllText(Path.Combine(tmpDir1, stem1 + ".txt"));
string text2 = File.ReadAllText(Path.Combine(tmpDir2, stem2 + ".txt"));
```

### 差分計算（DiffPlex）

**① 行単位 diff**（`InlineDiffBuilder`）

| 状態 | 意味 |
|---|---|
| Equal | 同じ行 |
| Inserted | 追加行 |
| Deleted | 削除行 |
| Modified | 変更行 → ② へ |

**② 変更行内の単語単位 diff**（`WordDiffBuilder`）

- 変更行のみに適用
- 単語区切り: スペース・句読点・改行
- 変わった単語だけをインラインハイライト

---

## セクション 4 — HTML レポート構成

### ファイル構成

```
diff-report/
├── index.html          ← サマリー（全ペア一覧）
└── details/
    ├── img001.html
    ├── img002.html
    └── img003.html
```

### index.html（サマリー）

- ヘッダー: `before/ ↔ after/ 全 N ペア`
- テーブル列: ファイル名 / 追加行数 / 削除行数 / 変更行数 / 状態
- 状態: 「同一」「差分あり」「追加のみ（after のみ）」「削除のみ（before のみ）」
- 差分ありの行はファイル名がリンクになり詳細ページへ遷移

### details/imgXXX.html（個別詳細）

- ページ上部に「← 一覧に戻る」リンク
- 左右2カラム表示（before | after）
- 同じ行番号を横に並べる

### 色分け

| 状態 | 背景色 | 単語ハイライト |
|---|---|---|
| 同じ | 白 | なし |
| 変更（before） | 薄黄 `#fff3cd` | 変わった単語を濃黄 `#f0c800` |
| 変更（after） | 薄緑 `#e6ffed` | 変わった単語を濃緑 `#8fc98f` |
| 削除 | 薄赤 `#ffeef0` | なし |
| 追加 | 薄緑 `#e6ffed` | なし |

---

## セクション 5 — 配布構成・エラー処理

### 配布ディレクトリ構成

```
MyOcrDiff/
├── MyOcrDiff.exe              ← C# 本体（self-contained 単一 exe）
├── ndlocr-lite/
│   └── src/
│       ├── ocr.py
│       ├── model/
│       │   ├── deim-s-1024x1024.onnx
│       │   └── parseq-ndl-*.onnx
│       └── （その他ソース）
├── python/                    ← Python Embedded（zip 展開済み）
│   ├── python.exe
│   └── Lib/
│       └── site-packages/     ← ndlocr-lite 依存ライブラリ配置済み
├── NOTICE.txt                 ← 帰属表示（NDLOCR-Lite CC BY 4.0 など）
└── README.txt
```

- `python/python.exe` と `ndlocr-lite/src/ocr.py` のパスは実行ファイルからの相対パスで解決
- Python Embedded は pip 非対応のため、依存ライブラリは事前に `Lib/site-packages/` に配置してセットアップ済み状態で同梱

### エラー処理

| ケース | 動作 |
|---|---|
| ディレクトリが存在しない | エラーメッセージ → 終了コード 1 |
| ペアが1つも見つからない | エラーメッセージ → 終了コード 1 |
| ペアが一部見つからない | 警告表示してスキップ、他のペアは続行 |
| OCR 失敗（Python 異常終了） | そのペアをエラー扱いでサマリーに記録、続行 |
| 出力ディレクトリ作成失敗 | エラーメッセージ → 終了コード 1 |
| 全ペア失敗 | 終了コード 2 |

---

## ライセンス帰属表示（NOTICE.txt 記載内容）

```
This software uses NDLOCR-Lite.
  Copyright (c) 2023 National Diet Library, Japan
  Licensed under CC BY 4.0: https://creativecommons.org/licenses/by/4.0/

This software uses DEIMv2 (model weights).
  Licensed under Apache 2.0

This software uses PARSeq (model weights).
  Licensed under Apache 2.0

This software uses DiffPlex.
  Licensed under Apache 2.0
```
