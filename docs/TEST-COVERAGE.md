# テストカバレッジ一覧

テストファイルと各テストが何を確認しているかの一覧。
次にテストを追加するときに「どこまでカバーされているか」の確認に使う。

**現在のテスト数: 139 件合格、1 件スキップ（既知ギャップ）**

---

## ConfigTests（18 件）

`ScenarioConfig` の JSON デシリアライズ動作を検証する。

| テスト名 | 確認内容 |
|---------|---------|
| `Deserialize_MinimalConfig` | `url` だけの最小 JSON が正常にデシリアライズされる |
| `Deserialize_NoUrl_DefaultsToEmptyString` | `url` フィールドがない場合は空文字になる |
| `Deserialize_AllActionTypes` | click / fill / navigate / waitForSelector / hover / press / wait の全アクションタイプが読める |
| `Deserialize_ActionsEmptyArray_ResultsInEmptyList` | `actions: []` → 空リストになる |
| `Deserialize_ActionsExplicitlyNull_PropertyBecomesNull` | `actions: null` → null になる |
| `Deserialize_MultipleScriptFilters` | `scriptFilters` に複数要素 |
| `Deserialize_ScriptExcludes_WhenSet` | `scriptExcludes` に値あり |
| `Deserialize_ScriptExcludes_DefaultEmpty` | `scriptExcludes` 省略 → 空リスト |
| `Deserialize_TimeoutMs_WhenSet` | `timeoutMs` に値あり |
| `Deserialize_TimeoutMs_NullWhenNotSet` | `timeoutMs` 省略 → null |
| `Deserialize_NegativeTimeoutMs_AcceptedAsIs` | 負数の `timeoutMs` がそのまま受け入れられる |
| `Deserialize_ContinueOnError_WhenTrue` | `continueOnError: true` |
| `Deserialize_ContinueOnError_DefaultFalse` | `continueOnError` 省略 → false |
| `Deserialize_PressActionWithoutValue_ValueIsNull` | press アクションの `value` 省略 → null |
| `Deserialize_UnknownField_IgnoredSilently` | 未知フィールドは無視される |
| `Deserialize_UnicodeUrl_PreservedExactly` | URL に Unicode が含まれてもそのまま読める |
| `Deserialize_UpperCaseFieldName_CaseInsensitiveMatch` | フィールド名の大文字小文字を区別しない |
| `Deserialize_InvalidJson_ThrowsJsonException` | 不正 JSON で `JsonException` が投げられる |

---

## CoverageMapTests（86 件 ＋ スキップ 1 件）

`HtmlReportGenerator.BuildCoverageMap` と
`MarkUncalledFunctionBodiesAsUncovered` の動作を検証する。

マップ値の意味: `-1` = カバレッジ対象外、`0` = 未実行（赤）、`1` = 実行済み（緑）

### 基本動作（5 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_OutOfScope_IsMinusOne` | 関数データ空 → 全文字 -1 |
| `BuildMap_AllCovered` | count=1 の範囲 → 全文字 1 |
| `BuildMap_AllUncovered` | count=0 の範囲 → 全文字 0 |
| `BuildMap_InnerRangeOverridesOuter` | 内側の範囲が外側を上書きする（if/else 分岐）|
| `BuildMap_MultipleFunctions_RangesMerged` | 複数関数の範囲が合わさってマップに書かれる |

### 境界値・異常データ（10 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_EmptySource_ReturnsEmptyArray` | ソース空 → 長さ 0 の配列 |
| `BuildMap_EmptySourceWithRanges_ReturnsEmptyArray` | ソース空でも範囲データあり → 空配列 |
| `BuildMap_ZeroLengthRange_NoEffect` | 開始 = 終了の長さゼロ範囲 → 何も変わらない |
| `BuildMap_RangeExceedingSourceLength_Clamped` | 終了がソース長超え → ソース末尾にクランプ |
| `BuildMap_NegativeStartOffset_ClampedToZero` | 開始が負 → 0 にクランプ |
| `BuildMap_RangeStartBeyondSource_NoEffect` | 開始がソース長以上 → 何も書かれない |
| `BuildMap_PartiallyOutOfBoundsRange_ClampedWrite` | 範囲がソース末尾をはみ出す → ソース内だけ書かれる |
| `BuildMap_CountGreaterThanOne_StillCovered` | count=100 でも 1（実行済み）|
| `BuildMap_NegativeCount_TreatedAsUncovered` | count=-1 → 0（未実行）扱い |
| `BuildMap_InvertedRange_NoExceptionAndNoEffect` | end < start の逆順レンジ → 例外なし・何も変わらない |
| `BuildMap_HighCountRange_TreatedAsCovered` | count=100 → 1（CountGreaterThanOne と同義、重複テスト）|

### 実際の JS コードパターン（5 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_UncalledFunction_AllUncovered` | 一度も呼ばれなかった関数 → 全体 0 |
| `BuildMap_TernaryBranches_InnerWins` | 三項演算子の片側だけ実行 |
| `BuildMap_NestedCallback_InnerNeverCalled` | 外側実行済み・コールバック未実行 |
| `BuildMap_SwitchCases_OnlyMatchedCaseCovered` | switch で一致した case のみ実行 |
| `BuildMap_HighCountRange_TreatedAsCovered` | ↑の重複（上記に記載済み）|

### V8 遅延コンパイル補正 — function キーワード検出（9 件）

V8 が未コンパイルのまま CDP データに含めなかった関数を補正する処理。

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_UncalledFunctionDeclaration_MarkedAsUncovered` | `function foo() {}` → 全体 0 |
| `BuildMap_MixedCalledAndUncalledFunctions_CorrectlyMarked` | 呼ばれた関数(1)と呼ばれなかった関数(0)が混在 |
| `BuildMap_FunctionInLineComment_RemainsNeutral` | `// function foo()` → -1（コメント内）|
| `BuildMap_FunctionInStringLiteral_RemainsNeutral` | `"function foo()"` → -1（文字列内）|
| `BuildMap_FunctionAsPartOfIdentifier_RemainsNeutral` | `functionHelper()` → -1（識別子の一部）|
| `BuildMap_FunctionKeywordInBlockComment_RemainsNeutral` | `/* function foo() */` → -1 |
| `BuildMap_FunctionKeywordInTemplateLiteral_RemainsNeutral` | バッククォート内の function → -1 |
| `BuildMap_UncalledGeneratorFunction_AllMarkedAsUncovered` | `function* gen() {}` → 全体 0 |
| `BuildMap_AsyncUncalledFunction_AsyncKeywordAlsoMarkedAsUncovered` | `async function f(){}` → async キーワードも 0 |

### V8 遅延コンパイル補正 — 正規表現による誤検出防止（5 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_FunctionBodyWithRegexContainingBrace_CorrectlyMarked` | 本体内の `/}/` が関数終端と誤認識されない |
| `BuildMap_FunctionBodyWithRegexContainingBraces_CorrectlyMarked` | 本体内の `/[{}]/` でも正しく終端検出 |
| `BuildMap_FunctionParamWithRegexContainingParen_CorrectlyMarked` | パラメータ内の `/a)b/` が括弧終端と誤認識されない |
| `BuildMap_FunctionKeywordInRegexLiteral_RemainsNeutral` | `/function() {}/` → -1（正規表現内）|
| `BuildMap_UncalledFunction_ReturnRegexWithBrace_FullBodyMarkedUncovered` | `return /[}]/` を含む関数本体全体が 0 になる（IsRegexStart バグ修正確認）|
| `BuildMap_UncalledFunction_TypeofRegexWithBrace_FullBodyMarkedUncovered` | `typeof /[{}]/` を含む関数本体全体が 0 になる |

### V8 遅延コンパイル補正 — コメント内括弧（2 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_UncalledFunction_ParamWithLineCommentContainingParen_BodyMarkedUncovered` | `foo(a, // (opt)\n b)` — 行コメント内 `(` がパラメータ深さに影響しない |
| `BuildMap_UncalledFunction_ParamWithBlockCommentContainingParen_BodyMarkedUncovered` | `foo(a /* bar() */, b)` — ブロックコメント内 `)` がパラメータ終端と誤認識されない |

### V8 遅延コンパイル補正 — アロー関数（5 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_UncalledArrowFunctionWithBlock_MarkedAsUncovered` | `() => { }` → `=>` から `}` まで 0 |
| `BuildMap_CalledArrowFunctionWithBlock_NotMarkedAsUncovered` | カバレッジデータあり → 補正されない（1 のまま）|
| `BuildMap_UncalledArrowFunctionWithExpression_NotMarkedAsUncovered` | `x => x + 1`（式本体）→ 補正対象外 |
| `BuildMap_UncalledArrowFunction_CharAfterClosingBraceRemainsNeutral` | `}` の直後の `;` は -1 のまま（オフバイワンバグ修正確認）|
| `BuildMap_UncalledAsyncArrowFunction_BlockBodyMarkedFromArrow` | `async () => { }` → `=>` 以降が 0、async は -1 のまま |

### V8 遅延コンパイル補正 — アロー関数バリエーション（2 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_UncalledNestedArrowFunctions_BothMarkedAsUncovered` | ネスト `() => { () => {} }` → 外側の FindMatchingBrace が両方まとめて 0 にする |
| `BuildMap_UncalledMultipleArrowsOnSameLine_BothMarkedAsUncovered` | 同一行の複数アロー関数がそれぞれ独立して 0 になる |

### V8 遅延コンパイル補正 — メソッド短縮構文（4 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_UncalledMethodShorthand_MarkedAsUncovered` | `{ greet() {} }` → greet から `}` まで 0 |
| `BuildMap_CalledMethodShorthand_NotMarkedAsUncovered` | カバレッジデータあり → 補正されない（1 のまま）|
| `BuildMap_IfStatementNotDetectedAsMethod_RemainsNeutral` | `if (true) {}` → -1（ControlFlowKeywords で除外）|
| `BuildMap_UncalledAsyncMethodShorthand_BodyMarkedAsUncovered` | `async greet() {}` → greet から `}` まで 0 |

### V8 遅延コンパイル補正 — メソッド短縮構文バリエーション（5 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_UncalledClassMethod_MarkedAsUncovered` | `class Foo { method() {} }` → method 本体が 0 |
| `BuildMap_UncalledStaticMethod_MethodBodyMarkedAsUncovered` | `static run()` → `static` は -1 のまま、`run` 本体が 0 |
| `BuildMap_UncalledGetter_BodyMarkedAsUncovered` | `get value()` → `get` は -1 のまま、`value` 本体が 0 |
| `BuildMap_UncalledClassWithExtends_MethodMarkedAsUncovered` | `class Child extends Parent { greet() {} }` → `extends`/`Parent` は -1、greet が 0 |
| `BuildMap_UncalledGeneratorMethodShorthand_BodyMarkedAsUncovered` | `{ *gen() {} }` → `*` は -1 のまま、`gen` 本体が 0 |

### V8 遅延コンパイル補正 — 関数式バリエーション（2 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_UncalledNamedFunctionExpression_MarkedAsUncovered` | `function named() {}` を変数に代入 → function キーワードから `}` まで 0 |
| `BuildMap_UncalledObjectPropertyFunction_MarkedAsUncovered` | `{ foo: function() {} }` → function から `}` まで 0 |

### V8 遅延コンパイル補正 — IIFE（1 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_CalledIIFE_NotReMarkedAsUncovered` | `(function() { })()` にカバレッジデータあり → `map[funcStart] != -1` のため補正されない |

### V8 遅延コンパイル補正 — export（2 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_ExportFunction_MarkedAsUncovered` | `export function foo() {}` → function から `}` まで 0 |
| `BuildMap_ExportDefaultFunction_MarkedAsUncovered` | `export default function() {}` → 匿名 function から `}` まで 0 |

### V8 遅延コンパイル補正 — 制御構文の誤検出確認（3 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_TryBlock_RemainsNeutral` | `try {}` → `(` がないため -1 のまま（false positive なし）|
| `BuildMap_ElseBlock_RemainsNeutral` | `else {}` → `(` がないため -1 のまま（false positive なし）|
| `BuildMap_WithStatement_FalsePositive_KnownLimitation` | `with(obj){}` → **既知の false positive**: 0 になる（`with` は ControlFlowKeywords 未登録）|

### V8 遅延コンパイル補正 — 既知の制限・エッジケース（3 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_OuterCalledInnerLazyCompiled_InnerBodyCoveredByOuterRange` | 外側が実行済みで内側が V8 未コンパイルの場合、内側は外側の range に覆われるため補正できない（false negative・既知制限）|
| `BuildMap_UncalledAsyncMethodShorthand_AsyncKeywordAlsoMarked` | **SKIP**: `async greet()` の async キーワードが 0 にならない（既知ギャップ）|
| `BuildMap_MissingClosingBrace_NoExceptionAllNeutral` | `}` のない壊れたソース → `FindMatchingBrace` が -1 を返しクラッシュなし・全 -1 のまま |

### FindMatchingBrace — 文字列・コメント内 `{}` のスキップ確認（4 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_FunctionBody_SingleQuotedCloseBrace_NotClosedEarly` | 本体内の `'}'` が関数終端と誤認識されない（単引用符スキップ）|
| `BuildMap_FunctionBody_DoubleQuotedCloseBrace_NotClosedEarly` | 本体内の `"}"` が関数終端と誤認識されない（二重引用符スキップ）|
| `BuildMap_FunctionBody_BlockCommentCloseBrace_NotClosedEarly` | 本体内の `/* } */` が関数終端と誤認識されない（ブロックコメントスキップ）|
| `BuildMap_FunctionBody_LineCommentOpenBrace_DepthNotAffected` | 本体内の `// {` が深さカウントに影響しない（行コメントスキップ）|

### メインスキャナーの文字列スキップ確認（2 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_FunctionInSingleQuoteString_RemainsNeutral` | `'function foo() {}'` → -1（単引用符内。二重引用符は既存テスト済み）|
| `BuildMap_ArrowInStringLiteral_RemainsNeutral_RealArrowMarked` | 文字列内の `=>` は無視され、後続の本物のアロー関数は 0 になる |

### SkipBalancedParens — 単引用符内 `)` のスキップ確認（1 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_UncalledFunction_ParamWithSingleQuotedParen_BodyMarkedUncovered` | `foo(a = ')', b)` — 単引用符内の `)` がパラメータ終端と誤認識されない |

### SkipRegexLiteral のエッジケース確認（2 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_FunctionBody_RegexWithEscapedSlash_CorrectlySkipped` | `/\//` のバックスラッシュエスケープが正しく処理される |
| `BuildMap_FunctionBody_RegexCharClassWithSlash_CorrectlySkipped` | `/[/]/` の文字クラス内 `/` が正規表現終端と誤認識されない |

### アロー関数の追加パターン（1 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_UncalledArrowFunction_SingleBareParam_MarkedAsUncovered` | `x => { }` 括弧なし単一パラメータ → `=>` から `}` まで 0 |

### メソッド短縮構文の追加パターン（3 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_UncalledSetter_BodyMarkedAsUncovered` | `set value(v) {}` — `set` は -1 のまま、`value` 本体が 0 |
| `BuildMap_UncalledMethodNamedAsync_MarkedAsUncovered` | `{ async() {} }` — `async` がメソッド名（キーワードではなく）の場合 → 0 |
| `BuildMap_ComputedPropertyMethod_RemainsNeutral_KnownLimitation` | `{ ["method"]() {} }` — 計算プロパティメソッドは識別子先頭条件を満たさず -1 のまま（既知 false negative）|

### ネスト関数（両方未呼び出し）（1 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildMap_BothOuterAndInnerUncalled_EntireSourceMarkedAsUncovered` | `function outer() { function inner() {} }` — 外側の FindMatchingBrace が全体をまとめて 0 にする |

---

## HtmlOutputTests（47 件）

`HtmlReportGenerator` の HTML 生成関連メソッドを検証する。

### HtmlEncode（6 件）

| テスト名 | 確認内容 |
|---------|---------|
| `HtmlEncode_EscapesSpecialChars` | `<`, `>`, `"`, `'`, `&` がエスケープされる |
| `HtmlEncode_NoSpecialChars_ReturnsUnchanged` | 特殊文字なし → そのまま返る |
| `HtmlEncode_EmptyString_ReturnsEmpty` | 空文字 → 空文字 |
| `HtmlEncode_AllSpecialCharsInOneString` | すべての特殊文字が一文字列に混在 |
| `HtmlEncode_AlreadyEncodedString_AmpersandNotDoubleEncoded` | `&amp;` の二重エスケープを防ぐ（`&` を最初に処理）|
| `HtmlEncode_AmpersandEscapedFirst_PreventDoubleEncoding` | ↑の確認（重複観点）|

### BuildLines（31 件）

`BuildLines` — ソースコードと map から行データ（HTML・行ステータス）を生成する。

| テスト名 | 確認内容 |
|---------|---------|
| `BuildLines_EmptySource_ReturnsNoLines` | ソース空 → 行なし |
| `BuildLines_SingleCharSource_ProducesOneLine` | 1文字 → 1行 |
| `BuildLines_OnlyNewline_ReturnsOneNeutralLine` | `\n` だけ → 1行（ニュートラル）|
| `BuildLines_SingleCoveredLine` | 1行が全部 1 → 行ステータス = covered |
| `BuildLines_SingleUncoveredLine` | 1行が全部 0 → 行ステータス = uncovered |
| `BuildLines_NeutralLine_AllOutOfScope` | 1行が全部 -1 → 行ステータス = neutral |
| `BuildLines_MultiLine_SplitsCorrectly` | 複数行が正しく分割される |
| `BuildLines_ThreeLineSource_CorrectStatusPerLine` | 3行それぞれのステータスが正しい |
| `BuildLines_ThreeLinesWithDifferentStatuses` | covered / partial / uncovered の3行が混在 |
| `BuildLines_TrailingNewline_DoesNotCreateExtraLine` | 末尾の `\n` は余分な行を作らない |
| `BuildLines_SourceEndingWithNewline_TrailingEmptyLineExcluded` | ↑の確認（重複観点）|
| `BuildLines_BlankLineBetweenCodeLines_IsNeutral` | コード行の間の空行 → neutral |
| `BuildLines_AllNeutralChars_StatusIsNeutral` | 全文字 -1 → neutral |
| `BuildLines_FunctionDeclarationPartlyCovered` | 関数宣言行が partial（covered + uncovered 混在）|
| `BuildLines_PartialLine_ContainsBothSpans` | partial 行に covered / uncovered 両 span が含まれる |
| `BuildLines_MultipleSpanTransitionsInOneLine` | 1行内で covered→uncovered→covered と複数回切り替わる |
| `BuildLines_HtmlSpecialCharsInSource_AreEscaped` | `<`, `>`, `&` などが HTML エスケープされる |
| `BuildLines_AmpersandOperator_EscapedInHtml` | `&&` が `&amp;&amp;` になる |
| `BuildLines_TypeAnnotationAngles_EscapedInHtml` | TypeScript 型注釈 `<T>` がエスケープされる |
| `BuildLines_UnicodeCharsInSource_RenderedCorrectly` | 日本語など Unicode 文字が壊れずに出力される |
| `BuildLines_EmptyMap_AllCharsNeutral` | map が空 → 全文字 neutral |
| `BuildLines_EmptyMapWithSource_AllCharsNeutral` | ↑の確認（重複観点）|
| `BuildLines_MapShorterThanSource_ExtraCharsAreNeutral` | map がソースより短い → はみ出た文字は neutral |
| `BuildLines_MapShorterThanSource_ExtraCharsNeutral` | ↑の確認（重複観点）|
| `BuildLines_CrlfEnding_CarriageReturnNotInHtml` | CRLF 行末の `\r` は HTML に含まれない |
| `BuildLines_CrInMiddleOfLine_NotInHtml` | 行中の `\r` は HTML に含まれない |
| `BuildLines_MultiLineCrlf_AllCrRemoved` | 複数行 CRLF → `\r` がすべて除去される |
| `BuildLines_CrCoveredButCodeUncovered_StatusIsUncovered` | `\r` が covered でも実際のコードが uncovered なら uncovered |
| `BuildLines_OffsetContinuesCorrectlyAcrossLines` | 改行をまたいで map のオフセットが正しく引き継がれる |
| `BuildLines_NulCharUncovered_NotCountedInCoverage` | NUL 文字（`\0`）は covered でも行ステータスに影響しない |
| `BuildLines_BlankLineBetweenCodeLines_IsNeutral` | 空行は neutral（上記に記載済み）|

### BuildScriptPage / BuildIndexPage（10 件）

| テスト名 | 確認内容 |
|---------|---------|
| `BuildScriptPage_XssInScriptUrl_IsHtmlEncoded` | スクリプト URL の XSS が HTML エスケープされる |
| `BuildScriptPage_ScriptTagInSource_IsHtmlEncoded` | ソース内の `<script>` タグが HTML エスケープされる |
| `BuildScriptPage_ContainsBothPageUrlAndScriptUrl` | ページ URL とスクリプト URL が両方出力に含まれる |
| `BuildScriptPage_EmptyPageUrl_ShowsTabIndex` | ページ URL が空のときはタブ番号が表示される |
| `BuildIndexPage_IncludesPageUrlColumnHeader` | インデックスページにページ URL 列ヘッダーが含まれる |
| `BuildIndexPage_XssInUrl_IsHtmlEncoded` | インデックスページの URL の XSS が HTML エスケープされる |
| `BuildIndexPage_EmptyRows_ReturnsValidHtml` | スクリプトなし（空リスト）でも有効な HTML が返される |
| `BuildIndexPage_MultipleRows_AllIncluded` | 複数スクリプトの行がすべて出力に含まれる |

---

## 未テスト（既知のギャップ）

テスト追加の候補として把握しておく観点。

| 観点 | 理由 |
|-----|------|
| `async greet()` の async キーワードが 0 にならない | `async function` は対応済みだが async method shorthand は未対応。スキップテストとして文書化済み |
| 外側実行済み・内側 V8 未コンパイルのネスト関数で内側が補正できない | 外側の range が内側を覆うため `map[funcStart] != -1` になり補正不可。テストで既知制限として文書化済み |
| `with(obj){}` が false positive になる | `with` が ControlFlowKeywords 未登録のためメソッドと誤判定。テストで既知制限として文書化済み |
| `CoverageCollector` のユニットテスト | Playwright の IPage / ICDPSession をモックする必要があり、現状テストなし |
| `ActionRunner` のユニットテスト | 同上 |
| `BuildLines` の map が null のケース | 現実装では null を渡すと NullReferenceException。ガードが必要かどうか未決定 |
| `async greet()` の async キーワードが 0 にならない | `async function` は対応済みだが async method shorthand は未対応。スキップテストとして文書化済み |
| 計算プロパティメソッド `["method"](){}` の検出 | 識別子先頭条件（`IsIdentifierChar && !prevIsIdentChar`）を満たさず未検出。`ComputedPropertyMethod_RemainsNeutral` で既知制限として文書化済み |
| テンプレートリテラル内の `${}` ネスト（簡易版の限界） | `FindMatchingBrace` / `SkipBalancedParens` のテンプレートリテラル処理は `${}` のネストを追跡しない。`` `${`inner`}` `` のような多重ネストでは誤動作の可能性あり |

---

## 実装の制約メモ（テスト設計時の参考）

### MarkUncalledFunctionBodiesAsUncovered の動作ルール

補正の前提条件: `map[funcStart] == -1`（カバレッジデータが一切ない文字位置）

- **function キーワード**: `function` の前後が識別子文字でないことを確認
- **async function**: `function` の直前に `async` があれば async キーワードも 0 にする
- **function***: `*` はスキップして通常の function と同様に処理
- **アロー関数**: `=>` の直後が `{` の場合のみ。式本体（`x => x+1`）は対象外。`=>` 位置から `}` まで 0。async は対象外
- **メソッド短縮構文**: `identifier(...){}` のパターンを検出。`function` / ControlFlowKeywords（if/for/while/switch/catch/do）は除外
- **ControlFlowKeywords 外の `with`**: `with(obj){}` は誤検出される（既知 false positive）
- **FindMatchingBrace の戻り値**: `}` の次のインデックス（`}` 自体は `braceEnd - 1`）。見つからない場合は `-1`
- **スキップ対象**: `'...'` / `"..."` / `` `...` `` / `// ...` / `/* ... */` / 正規表現リテラル

### IsRegexStart の判定ルール

- 直前が `)` または `]` → **除算**（false）
- 直前が識別子文字 → トークン全体を読んで RegexPrecedingKeywords に含まれるか確認
  - `return`, `typeof`, `void`, `delete`, `throw`, `new`, `in`, `instanceof` → **正規表現**（true）
  - それ以外の識別子 → **除算**（false）
- それ以外 → **正規表現**（true）
