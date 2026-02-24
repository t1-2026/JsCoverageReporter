# 意地悪テストケース追加 設計書

**日付**: 2026-02-24
**対象**: JsCoverageReporter.Tests
**目的**: 既存テストのカバレッジを強化し、境界条件・異常入力・パーサー誤動作を検証する

---

## 方針

- Playwright モック不要・単体テスト可能なメソッドに集中
- 既存テストファイルに追記（新規ファイル不要）
- 日本語コメント・if/else のみ・1メソッド200行以内の既存規約を維持

---

## 1. CoverageMapTests.cs への追加 (+12 テスト)

### 1-A. MarkUncalledFunctionBodiesAsUncovered 重点攻略 (+9)

| # | テスト名 | 入力 | 期待結果 |
|---|---------|------|---------|
| 1 | `BuildMap_FunctionInBlockComment_RemainsNeutral` | `/* function foo() {} */` のみ | 全 -1 |
| 2 | `BuildMap_FunctionInTemplateLiteral_RemainsNeutral` | `` `${function foo() {}}` `` | 全 -1 |
| 3 | `BuildMap_GeneratorFunction_MarkedAsUncovered` | `function* gen() { yield 1; }` (CDP に含まれない) | body を 0 にマーク |
| 4 | `BuildMap_AsyncFunction_MarkedAsUncovered` | `async function fetch() { return 1; }` (CDP に含まれない) | body を 0 にマーク |
| 5 | `BuildMap_NestedFunctions_BothMarkedAsUncovered` | `function outer() { function inner() {} }` | outer / inner 両 body を 0 |
| 6 | `BuildMap_FunctionAtFileStart_MarkedAsUncovered` | ファイル先頭が `function foo() {}` | body を 0 にマーク |
| 7 | `BuildMap_FunctionWithNoBrace_NoCrash` | `function foo()` (波括弧なし、malformed) | クラッシュしない |
| 8 | `BuildMap_FunctionInLineComment_SingleSlash_RemainsNeutral` | `// function bar() {}` | 全 -1 |
| 9 | `BuildMap_UrlStringDoesNotConfuseCommentDetection` | `"http://example.com"; function foo() {}` | URL 文字列の `//` を comment と誤認せず、後続の function を 0 にマーク |

### 1-B. BuildCoverageMap 境界条件 (+3)

| # | テスト名 | 入力 | 期待結果 |
|---|---------|------|---------|
| 10 | `BuildMap_StartGreaterThanEnd_NoEffect` | start=5, end=2 の逆転レンジ | 範囲は無視、-1 のまま（クラッシュなし） |
| 11 | `BuildMap_FullSourceRange_AllCovered` | start=0, end=source.Length で count=1 | 全 1 |
| 12 | `BuildMap_OverlappingRangesAtSameLevel_LastApplied` | 同一関数内に [0,5,count=1] と [2,4,count=0] の 2 レンジ | [2,4) が 0（内側優先で上書き）|

---

## 2. HtmlOutputTests.cs への追加 (+12 テスト)

### 2-A. BuildLines 意地悪ケース (+6)

| # | テスト名 | 入力 | 期待結果 |
|---|---------|------|---------|
| 1 | `BuildLines_OnlyCarriageReturn_TreatedAsSingleLine` | `"\r"` のみ | 1行、Neutral |
| 2 | `BuildLines_MixedLineEndings_AllCrRemoved` | `"a\r\nb\nc"` | 3 行、`\r` が出力 HTML に含まれない |
| 3 | `BuildLines_MapLongerThanSource_ExtraEntriesIgnored` | ソース長 3, map 長 10 | クラッシュなし、ソース範囲のみ使用 |
| 4 | `BuildLines_MapShorterByOne_LastCharNeutral` | ソース `"abc"`, map 長 2 | 3 文字目が Neutral |
| 5 | `BuildLines_WhitespaceOnlyLine_IsNeutral` | `"   \t   "` (タブ・スペースのみ) | 1 行 Neutral |
| 6 | `BuildLines_VeryLongLine_NoCrash` | 1000 文字の行 | クラッシュなし、行数 1 |

### 2-B. HtmlEncode 意地悪ケース (+2)

| # | テスト名 | 入力 | 期待結果 |
|---|---------|------|---------|
| 7 | `HtmlEncode_MultipleAmpersands_AllEscaped` | `"&&&"` | `"&amp;&amp;&amp;"` |
| 8 | `HtmlEncode_EntityLikeString_PreventsDoubleEncoding` | `"&lt;"` | `"&amp;lt;"` (& を先にエスケープ) |

### 2-C. BuildIndexPage 意地悪ケース (+3)

| # | テスト名 | 入力 | 期待結果 |
|---|---------|------|---------|
| 9 | `BuildIndexPage_EmptyList_NoCrash` | 空リスト | クラッシュなし、適切な HTML 出力 |
| 10 | `BuildIndexPage_UrlWithHtmlChars_Escaped` | URL = `"<script>alert(1)</script>"` | href・表示の両方でエスケープ |
| 11 | `BuildIndexPage_ZeroTotalLines_NoDivisionByZero` | total=0, covered=0 の行 | クラッシュなし |

### 2-D. BuildScriptPage 意地悪ケース (+1)

| # | テスト名 | 入力 | 期待結果 |
|---|---------|------|---------|
| 12 | `BuildScriptPage_ScriptUrlWithHtmlChars_Escaped` | scriptUrl = `"https://example.com/<api>&v=1"` | HTML にエスケープ済みで出力 |

---

## 3. ConfigTests.cs への追加 (+6 テスト)

| # | テスト名 | 入力 | 期待結果 |
|---|---------|------|---------|
| 1 | `Deserialize_UnknownFields_Ignored` | 未知フィールド `"unknownProp": 42` を含む JSON | エラーなしでデシリアライズ |
| 2 | `Deserialize_ActionWithAllNullOptionalFields` | `{"type":"click"}` のみのアクション | type="click", selector=null, value=null 等 |
| 3 | `Deserialize_EmptyActionsArray` | `"actions": []` | 空リスト (Count=0) |
| 4 | `Deserialize_EmptyUrl_DeserializesOk` | `"url": ""` | Url="" (バリデーションなし) |
| 5 | `Deserialize_TimeoutMs_LargeValue` | `"timeoutMs": 2147483647` (int.MaxValue) | TimeoutMs = int.MaxValue |
| 6 | `Deserialize_CaseInsensitiveKeys_Works` | `"URL"`, `"ACTIONS"` と大文字キー | Url, Actions が正しく読める |

---

## テスト数サマリー

| ファイル | 現在 | 追加 | 合計 |
|---------|------|------|------|
| CoverageMapTests.cs | 25 | +12 | 37 |
| HtmlOutputTests.cs | 34 | +12 | 46 |
| ConfigTests.cs | 8 | +6 | 14 |
| **合計** | **62** | **+30** | **~97** |

---

## 実装注意点

- `BuildMap_StartGreaterThanEnd_NoEffect`: `BuildCoverageMap` が `start > end` でどう動くか先に確認（クランプ処理の挙動次第でテスト期待値が変わる）
- `BuildMap_FunctionWithNoBrace_NoCrash`: `FindMatchingBrace` が -1 を返したとき `MarkUncalledFunctionBodiesAsUncovered` がどう処理するか確認してからアサートを書く
- `BuildIndexPage_EmptyList_NoCrash`: `BuildIndexPage` の overall percentage 計算でゼロ除算しないか確認
