#nullable disable

using System.Text;
using JsCoverageReporter.Coverage;

namespace JsCoverageReporter.Report;

/// <summary>
/// V8のカバレッジデータを処理し、文字単位のカバレッジマップを構築するASTパーサークラス。
/// </summary>
internal static class CoverageParser
{
    /// <summary>
    /// これらのキーワードは identifier(...) { } の形でも関数定義ではないため除外する。
    /// 例: if (cond) { } や for (;;) { } を誤検出しないようにする。
    /// </summary>
    private static readonly HashSet<string> ControlFlowKeywords = new HashSet<string>
    {
        "if", "for", "while", "switch", "catch", "do", "with", "else",
    };

    /// <summary>
    /// '/' の直前に現れたとき正規表現リテラルの開始と判断すべき JavaScript キーワードの集合。
    /// これらのキーワードは識別子文字で終わるため IsRegexStart の素朴な実装では除算と誤判定される。
    /// 例: return /regex/ は正規表現だが、'n' が識別子文字のため特別扱いが必要。
    /// </summary>
    private static readonly HashSet<string> RegexPrecedingKeywords = new HashSet<string>
    {
        "return", "typeof", "void", "delete", "throw",
        "new", "in", "instanceof",
        "yield",  // ジェネレータ関数内: yield /regex/ のパターン
        "case",   // switch 文内: case /regex/.test(x) のパターン
        "await",  // 非同期関数内: await /regex/ のパターン
        "else",   // else /regex/.test(x) のパターン
        "of",     // for...of ループ内: for (x of /regex/) のパターン（I-1 修正）
    };
    /// <summary>
    /// ソースコードの各文字に対してカバレッジ値を記録した配列を作成する。
    /// 値の意味: -1 = カバレッジ対象外, 0 = 未実行, 1 = 実行済み。
    /// 処理順: 範囲の大きいものから小さいものの順に処理することで、
    /// 内側の細かい分岐範囲が外側の関数範囲を正しく上書きできる。
    /// </summary>
    /// <param name="source">スクリプトのソースコード全文</param>
    /// <param name="functions">関数カバレッジデータのコレクション</param>
    /// <returns>各文字のカバレッジ値を格納した配列（インデックス = 文字位置）</returns>
    internal static int[] BuildCoverageMap(string source, IEnumerable<FunctionCoverage> functions)
    {
        // null ソースは空配列を返す（BuildLines と同様の防衛処理）
        if (source == null) { return []; }

        // まず全文字を「カバレッジ対象外(-1)」で初期化する
        var map = new int[source.Length];
        Array.Fill(map, -1);

        // functions が null の場合は空として扱う（#nullable disable 環境の防衛処理）
        if (functions == null) { return map; }

        // 全関数の範囲を1つのリストにまとめる
        var allRanges = new List<CoverageRange>();
        // 各関数のカバレッジ範囲を走査してリストに追加する
        foreach (FunctionCoverage func in functions)
        {
            // func または func.Ranges が null の場合はスキップする（不正な CDP データへの防衛処理）
            if (func == null || func.Ranges == null) { continue; }
            // 各関数が持つすべての範囲をまとめてリストに追加する
            foreach (CoverageRange range in func.Ranges)
            {
                allRanges.Add(range);
            }
        }

        // 大きい範囲が先に処理されるよう降順にソートする
        // 比較: B の範囲サイズ - A の範囲サイズ（降順なら B > A が先）
        allRanges.Sort((a, b) =>
        {
            // 範囲 a のサイズ（文字数）を計算する
            int sizeA = a.EndOffset - a.StartOffset;
            // 範囲 b のサイズ（文字数）を計算する
            int sizeB = b.EndOffset - b.StartOffset;
            // 降順比較：大きい範囲が先に来るように b と a を逆に比較する
            return sizeB.CompareTo(sizeA);
        });

        // 各範囲をマップに書き込む（小さい範囲が後から上書きすることで正確な分岐情報を反映する）
        foreach (CoverageRange range in allRanges)
        {
            // 実行回数が1以上なら「実行済み(1)」、0なら「未実行(0)」とする
            int val;
            if (range.Count > 0)
            {
                // 実行済み（1回以上実行された）
                val = 1;
            }
            else
            {
                // 未実行（1回も実行されていない）
                val = 0;
            }

            // 配列の範囲内に収まるようにインデックスをクランプする（範囲外アクセスを防ぐ）
            int start = Math.Max(range.StartOffset, 0);
            int end = Math.Min(range.EndOffset, source.Length);

            // 範囲内の全文字にカバレッジ値を書き込む
            for (int i = start; i < end; i++)
            {
                map[i] = val;
            }
        }

        // V8 の遅延コンパイルにより未実行関数がカバレッジデータに含まれなかった場合の補正を行う
        MarkUncalledFunctionBodiesAsUncovered(source, map);

        // 各文字のカバレッジ値が格納された配列を返す
        return map;
    }

    /// <summary>
    /// V8 の遅延コンパイルにより未実行関数がカバレッジデータに含まれなかった場合の補正処理。
    /// ソースコード全体を ScanRange でスキャンして未実行関数本体を 0（未実行）としてマークする。
    /// テンプレートリテラルの ${ } 内も再帰的にスキャンするため、
    /// そこに定義された関数も正しく補正される。
    /// </summary>
    /// <param name="source">スクリプトのソースコード全文</param>
    /// <param name="map">BuildCoverageMap で作成したカバレッジマップ（内容を書き換える）</param>
    internal static void MarkUncalledFunctionBodiesAsUncovered(string source, int[] map)
    {
        if (map == null || map.Length == 0) { return; }
        // map が source より短い場合は map の範囲内だけをスキャンする（範囲外読み取りを防ぐ）
        int scanEnd = Math.Min(source.Length, map.Length);
        ScanRange(source, map, 0, scanEnd);
    }

    /// <summary>
    /// ソースコードの指定範囲 [start, end) をスキャンして未実行関数本体を探し、
    /// カバレッジ対象外（-1）の部分を未実行（0）としてマークする。
    /// テンプレートリテラルの ${ } 内は FindMatchingBrace で対応する } を見つけ、
    /// その中を再帰的にスキャンすることで内側の function キーワードも検出できる。
    /// </summary>
    /// <param name="source">スクリプトのソースコード全文</param>
    /// <param name="map">BuildCoverageMap で作成したカバレッジマップ（内容を書き換える）</param>
    /// <param name="start">スキャン開始インデックス（含む）</param>
    /// <param name="end">スキャン終了インデックス（含まない）</param>
    private static void ScanRange(string source, int[] map, int start, int end)
    {
        int i = start;

        while (i < end)
        {
            char c = source[i];

            // 単一引用符の文字列をスキップする（中の function は無視する）
            // サブレンジ再帰時に end を超えた場合はクランプして安全に終了する
            if (c == '\'') { i = SkipSingleQuotedString(source, i); if (i > end) { i = end; } continue; }

            // 二重引用符の文字列をスキップする（中の function は無視する）
            // サブレンジ再帰時に end を超えた場合はクランプして安全に終了する
            if (c == '"') { i = SkipDoubleQuotedString(source, i); if (i > end) { i = end; } continue; }

            // テンプレートリテラルをスキップし、${ } の中は再帰スキャンする
            if (c == '`')
            {
                i++; // 開き ` をスキップする
                while (i < end)
                {
                    char t = source[i];
                    // バックスラッシュエスケープの次の文字を読み飛ばす
                    if (t == '\\') { i += 2; continue; }
                    // 閉じ ` でテンプレートリテラル終了
                    if (t == '`') { i++; break; }
                    // ${ を発見したら、対応する } を FindMatchingBrace で探して再帰スキャンする
                    if (t == '$' && i + 1 < end && source[i + 1] == '{')
                    {
                        int braceStart = i + 1; // { の位置
                        int braceEnd = FindMatchingBrace(source, braceStart);
                        if (braceEnd > braceStart)
                        {
                            // ${ と } の間を再帰的にスキャンする（braceEnd は } の直後）
                            ScanRange(source, map, braceStart + 1, braceEnd - 1);
                            i = braceEnd; // } の直後へ進む
                            // サブレンジ再帰時に end を超えた場合はクランプして安全に終了する
                            if (i > end) { i = end; }
                        }
                        else
                        {
                            i += 2; // 対応する } が見つからない場合は ${ をスキップして続ける
                        }
                        continue;
                    }
                    i++;
                }
                continue;
            }

            // 行コメント // をスキップする（改行まで読み飛ばす）
            if (c == '/' && i + 1 < end && source[i + 1] == '/')
            {
                while (i < end && source[i] != '\n') { i++; }
                continue;
            }

            // ブロックコメント /* */ をスキップする
            if (c == '/' && i + 1 < end && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < end && !(source[i] == '*' && source[i + 1] == '/')) { i++; }
                i += 2; // */ の2文字をスキップする
                if (i > end) { i = end; } // サブレンジ境界をまたいだ場合はクランプする
                continue;
            }

            // 正規表現リテラルをスキップして function キーワードを誤検出しないようにする
            // サブレンジ再帰時に end を超えた場合はクランプして安全に終了する
            if (c == '/' && IsRegexStart(source, i))
            {
                i = SkipRegexLiteral(source, i);
                if (i > end) { i = end; }
                continue;
            }

            // アロー関数 => の検出（ブロック本体 {} を持つ場合のみ）— 詳細は TryMarkArrowFunction を参照
            if (c == '=' && i + 1 < end && source[i + 1] == '>')
            {
                i = TryMarkArrowFunction(source, map, i, end);
                continue;
            }

            // function キーワードの検出— 詳細は TryMarkFunctionKeyword を参照
            if (c == 'f' && i + 8 <= end && source.AsSpan(i, 8).SequenceEqual("function"))
            {
                i = TryMarkFunctionKeyword(source, map, i, end);
                continue;
            }

            // computed property key ([...](){}) の検出— 詳細は TryMarkComputedMethod を参照
            // 識別子文字でない文字の直後に '[' が来た場合のみ処理する（配列アクセス arr[0] は除外）
            // ')' や ']' の直後は呼び出し・添字アクセス後の続き（foo()[0]() {} など）なので除外する
            if (c == '[' && (i == 0 || (!IsIdentifierChar(source[i - 1]) && source[i - 1] != ')' && source[i - 1] != ']')))
            {
                i = TryMarkComputedMethod(source, map, i, end);
                continue;
            }

            // メソッド短縮構文（例: greet() { }）の検出
            bool prevIsIdentChar;
            if (i == 0) { prevIsIdentChar = false; }
            else { prevIsIdentChar = IsIdentifierChar(source[i - 1]); }

            if (IsIdentifierChar(c) && !prevIsIdentChar)
            {
                // TryMarkMethodShorthand がメソッド本体直後のインデックスを返すため
                // 本体内の二重スキャンを避けてそこから走査を再開する
                i = TryMarkMethodShorthand(source, map, i, end);
                continue;
            }

            i++;
        }
    }

    /// <summary>
    /// アロー関数 =&gt; の検出と未実行マーク処理。
    /// ブロック本体 {} を持つ場合のみ =&gt; から } まで 0（未実行）にマークする。
    /// async アロー関数の場合は async キーワードもマークする。
    /// </summary>
    /// <param name="source">スクリプトのソースコード全文</param>
    /// <param name="map">BuildCoverageMap で作成したカバレッジマップ（内容を書き換える）</param>
    /// <param name="arrowStart">=&gt; の '=' のインデックス</param>
    /// <param name="end">スキャン範囲の終端インデックス（含まない）</param>
    /// <returns>処理後の次のスキャン位置（処理しない場合は arrowStart + 2）</returns>
    private static int TryMarkArrowFunction(string source, int[] map, int arrowStart, int end)
    {
        // => の後の空白・コメントをスキップする
        int afterArrow = SkipWhitespaceAndCommentsForward(source, arrowStart + 2, end);
        // 次の文字が { でない、またはカバレッジデータがある場合はスキップ（式本体やカバー済みは対象外）
        if (afterArrow >= end || source[afterArrow] != '{' || map[arrowStart] != -1)
        {
            return arrowStart + 2; // => の次の位置へ進む
        }

        int braceEnd = FindMatchingBrace(source, afterArrow);
        if (braceEnd <= afterArrow)
        {
            return arrowStart + 2; // 対応する } が見つからない場合はスキップ
        }

        // => から } まで未実行（0）にマークする（map の長さを超えないようにクランプする）
        int arrowWriteEnd = Math.Min(braceEnd, map.Length);
        for (int m = arrowStart; m < arrowWriteEnd; m++)
        {
            if (map[m] == -1) { map[m] = 0; }
        }

        // async アロー関数の async キーワード未実行マーク
        // arrowStart の直前を逆走査してパラメータリストまたは単一識別子を飛ばし、
        // さらに前に async キーワードがあればそこからマークする
        int backScan = SkipWhitespaceAndCommentsBackward(source, arrowStart - 1);

        // パラメータリスト ')' のケース: async (x, y) => {}
        if (backScan >= 0 && source[backScan] == ')')
        {
            // 対応する '(' まで逆走査する
            // 文字列リテラル内の括弧（例: async (x = ")") => {}）を除外するため
            // クォートに当たった場合は文字列全体を逆方向にスキップする
            int parenDepth = 1;
            backScan--;
            while (backScan >= 0 && parenDepth > 0)
            {
                char bc = source[backScan];
                // 文字列リテラルの閉じクォートに当たった場合は開きクォートまで逆走査する
                // バッククォート（テンプレートリテラル）も同様にスキップする
                if (bc == '"' || bc == '\'' || bc == '`')
                {
                    char quote = bc;
                    backScan--;
                    while (backScan >= 0)
                    {
                        if (source[backScan] == quote)
                        {
                            // 直前の連続するバックスラッシュをカウントしてエスケープ判定する
                            int esc = 0;
                            int tmp = backScan - 1;
                            while (tmp >= 0 && source[tmp] == '\\') { esc++; tmp--; }
                            if (esc % 2 == 0) { break; } // 偶数個なら実際の開きクォート
                        }
                        backScan--;
                    }
                    // ループ末尾の backScan-- で開きクォートの前に進む
                }
                else if (bc == '/')
                {
                    // '/' は正規表現リテラルの終端の可能性がある
                    // [] 文字クラスと \ エスケープを考慮しながら逆走査して開始 '/' を探す
                    bool inClass  = false;
                    int savedScan = backScan;
                    backScan--;
                    bool foundOpen = false;
                    while (backScan >= 0)
                    {
                        char rc = source[backScan];
                        // JS の正規表現リテラルは改行をまたがないため、改行が見つかったら逆走査を中断する
                        if (rc == '\n' || rc == '\r') { break; }
                        if (rc == ']')
                        {
                            // \] はエスケープされた ] のため文字クラスの終端ではない
                            // 直前の連続するバックスラッシュ数が奇数ならエスケープ済み
                            int escB = 0; int tmpB = backScan - 1;
                            while (tmpB >= 0 && source[tmpB] == '\\') { escB++; tmpB--; }
                            if (escB % 2 == 0) { inClass = true; }
                        }
                        else if (rc == '[' && inClass)
                        {
                            // \[ はエスケープされた [ のため文字クラスの開始ではない
                            // 直前のバックスラッシュ数が偶数のときだけクラス開始と判定する
                            int escL = 0; int tmpL = backScan - 1;
                            while (tmpL >= 0 && source[tmpL] == '\\') { escL++; tmpL--; }
                            if (escL % 2 == 0) { inClass = false; }
                        }
                        else if (rc == '/')
                        {
                            int esc = 0;
                            int tmp = backScan - 1;
                            while (tmp >= 0 && source[tmp] == '\\') { esc++; tmp--; }
                            if (esc % 2 == 0 && !inClass) { foundOpen = true; break; }
                        }
                        backScan--;
                    }
                    if (!foundOpen) { backScan = savedScan; } // ロールバック
                }
                else if (bc == ')') { parenDepth++; }
                else if (bc == '(') { parenDepth--; }
                backScan--;
            }
            // ループ終了時 backScan は '(' の 1 つ前の位置にある
        }
        else if (backScan >= 0 && IsIdentifierChar(source[backScan]))
        {
            // 単一パラメータ識別子のケース: async x => {}
            while (backScan >= 0 && IsIdentifierChar(source[backScan])) { backScan--; }
            // ループ終了時 backScan は識別子の 1 つ前の位置にある
        }

        // '(' または識別子の直前の空白やコメントをスキップする
        backScan = SkipWhitespaceAndCommentsBackward(source, backScan);

        // "async" キーワードを確認する（backScan が 'c' の位置を指しているはず）
        if (backScan >= 4 && source.AsSpan(backScan - 4, 5).SequenceEqual("async"))
        {
            bool asyncStandalone;
            if (backScan - 5 < 0) { asyncStandalone = true; }
            else { asyncStandalone = !IsIdentifierChar(source[backScan - 5]); }

            if (asyncStandalone)
            {
                // async キーワードの先頭（'a' の位置）から => の直前までをマークする
                int asyncStart = backScan - 4;
                for (int a = asyncStart; a < arrowStart; a++)
                {
                    if (map[a] == -1) { map[a] = 0; }
                }
            }
        }

        return braceEnd;
    }

    /// <summary>
    /// function キーワードの検出と未実行マーク処理。
    /// async function の場合は async キーワードもマークする。
    /// </summary>
    /// <param name="source">スクリプトのソースコード全文</param>
    /// <param name="map">BuildCoverageMap で作成したカバレッジマップ（内容を書き換える）</param>
    /// <param name="funcStart">function キーワードの 'f' のインデックス</param>
    /// <param name="end">スキャン範囲の終端インデックス（含まない）</param>
    /// <returns>処理後の次のスキャン位置（処理しない場合は funcStart + 1）</returns>
    private static int TryMarkFunctionKeyword(string source, int[] map, int funcStart, int end)
    {
        // function の前が識別子文字でないことを確認する（別の単語の一部を誤検出しない）
        bool prevOk = funcStart == 0 || !IsIdentifierChar(source[funcStart - 1]);
        // function の直後が識別子文字でないことを確認する（例: functionCall を除外する）
        bool nextOk = funcStart + 8 >= end || !IsIdentifierChar(source[funcStart + 8]);

        // 識別子の途中（例: myFunctionX の中の 'f'）または既に CDP でマーク済み → スキップ
        if (!prevOk || map[funcStart] != -1)
        {
            return funcStart + 1;
        }
        // "function" で始まる識別子（例: functionX(){}）→ メソッド短縮構文として委譲する
        if (!nextOk)
        {
            return TryMarkMethodShorthand(source, map, funcStart, end);
        }

        // async function の場合、async キーワードも未実行（赤）としてマークする
        // 注意: SkipWhitespaceAndCommentsBackward は end パラメータを持たずソース全体を逆走査する。
        // ScanRange のサブレンジ再帰（例: テンプレートリテラル ${ } 内の再帰呼び出し）から
        // この関数が呼ばれた場合、逆走査が $ より前のコンテキストに入り込む可能性がある。
        // ただし async の直前が ${…} の { や $ になるケースは実用上ほぼ起きないため、
        // 誤検出のリスクは許容範囲と判断し、現状の実装を維持する。
        int scanBack = SkipWhitespaceAndCommentsBackward(source, funcStart - 1);
        if (scanBack >= 4 && source.AsSpan(scanBack - 4, 5).SequenceEqual("async"))
        {
            bool isStandaloneAsync;
            if (scanBack - 5 < 0) { isStandaloneAsync = true; }
            else { isStandaloneAsync = !IsIdentifierChar(source[scanBack - 5]); }
            if (isStandaloneAsync)
            {
                // async キーワード先頭から function キーワード直前（空白・コメント含む）を未実行としてマークする
                int asyncStart = scanBack - 4;
                for (int a = asyncStart; a < funcStart; a++)
                {
                    if (map[a] == -1) { map[a] = 0; }
                }
            }
        }

        int j = funcStart + 8; // "function" の次の文字へ進む
        // 空白・コメントをスキップする（function * gen や function /* c */ * gen のケース）
        j = SkipWhitespaceAndCommentsForward(source, j, end);
        // ジェネレータ関数の * をスキップする（function* / function * どちらも対応）
        if (j < end && source[j] == '*') { j++; }
        // * の後の空白・コメントをスキップする（function* gen や function * /* c */ gen のケース）
        j = SkipWhitespaceAndCommentsForward(source, j, end);
        // 関数名（識別子）をスキップする（無名関数の場合はスキップなし）
        while (j < end && IsIdentifierChar(source[j])) { j++; }
        // 空白・コメントをスキップする（function foo /* c */ () のケースも含む）
        j = SkipWhitespaceAndCommentsForward(source, j, end);
        // パラメータリスト ( ... ) をスキップする
        if (j < end && source[j] == '(') { j = SkipBalancedParens(source, j); }
        // 空白・コメントをスキップする（function foo() /* c */ {} や function foo() // c\n{} のケース）
        j = SkipWhitespaceAndCommentsForward(source, j, end);

        if (j < end && source[j] == '{')
        {
            int funcEnd = FindMatchingBrace(source, j);
            if (funcEnd > j) // j は '{' の位置なので funcEnd > j で有効な対応 '}' を確認する
            {
                // 関数本体内でカバレッジ対象外(-1)の部分を未実行(0)としてマークする（map の長さを超えないようにクランプする）
                int funcWriteEnd = Math.Min(funcEnd, map.Length);
                for (int k = funcStart; k < funcWriteEnd; k++)
                {
                    if (map[k] == -1) { map[k] = 0; }
                }
                return funcEnd;
            }
        }

        return funcStart + 1; // 関数本体が見つからない場合は 1 文字進む
    }

    /// <summary>
    /// computed property key（[...](){}）の検出と未実行マーク処理。
    /// async / static / * などのプレフィックスキーワードも含めてマークする。
    /// </summary>
    /// <param name="source">スクリプトのソースコード全文</param>
    /// <param name="map">BuildCoverageMap で作成したカバレッジマップ（内容を書き換える）</param>
    /// <param name="bracketStart">'[' のインデックス</param>
    /// <param name="end">スキャン範囲の終端インデックス（含まない）</param>
    /// <returns>処理後の次のスキャン位置（処理しない場合は bracketStart + 1）</returns>
    private static int TryMarkComputedMethod(string source, int[] map, int bracketStart, int end)
    {
        int bracketEnd   = SkipBalancedBrackets(source, bracketStart);
        int afterBracket = SkipWhitespaceAndCommentsForward(source, bracketEnd, end);

        // [...]() { } の形でなければメソッドではない。map[bracketStart] が -1 でなければカバー済み
        if (afterBracket >= end || source[afterBracket] != '(' || map[bracketStart] != -1)
        {
            return bracketStart + 1;
        }

        int afterParens = SkipBalancedParens(source, afterBracket);
        int afterBody   = SkipWhitespaceAndCommentsForward(source, afterParens, end);

        if (afterBody >= end || source[afterBody] != '{')
        {
            return bracketStart + 1;
        }

        int braceEnd = FindMatchingBrace(source, afterBody);
        if (braceEnd <= afterBody)
        {
            return bracketStart + 1;
        }

        // プレフィックスキーワード（get/set / async / * / static）を含めたマーク開始位置を決定する
        // TryMarkMethodShorthand と同じロジックで bracketStart の直前を逆走査する
        int markStart = bracketStart;
        int scanBack  = SkipWhitespaceAndCommentsBackward(source, bracketStart - 1);

        // get / set の確認（例: get [Symbol.toPrimitive]() {} / set [Symbol.toPrimitive](v) {}）
        // TryMarkMethodShorthand と同様に * / async より先に確認する（get/set は * や async と共存不可）
        if (scanBack >= 2)
        {
            string maybeGetOrSet = source.Substring(scanBack - 2, 3);
            if (maybeGetOrSet == "get" || maybeGetOrSet == "set")
            {
                bool getSetOk;
                if (scanBack - 3 < 0) { getSetOk = true; }
                else { getSetOk = !IsIdentifierChar(source[scanBack - 3]); }
                if (getSetOk)
                {
                    markStart = scanBack - 2; // get/set の先頭 'g'/'s' の位置からマークする
                    scanBack  = SkipWhitespaceAndCommentsBackward(source, scanBack - 3);
                }
            }
        }

        // * ジェネレーター記号の確認（例: *['key']() {}）
        if (scanBack >= 0 && source[scanBack] == '*')
        {
            markStart = scanBack; // * の位置からマークを開始する
            scanBack  = SkipWhitespaceAndCommentsBackward(source, scanBack - 1);
        }

        // async キーワードの確認（例: async ['key']() {}）
        if (scanBack >= 4 && source.AsSpan(scanBack - 4, 5).SequenceEqual("async"))
        {
            bool asyncPrevOk;
            if (scanBack - 5 < 0) { asyncPrevOk = true; }
            else { asyncPrevOk = !IsIdentifierChar(source[scanBack - 5]); }
            if (asyncPrevOk) { markStart = scanBack - 4; } // async の 'a' の位置からマーク
        }

        // static キーワードの確認（例: static async ['key']() {}）
        int staticScan = SkipWhitespaceAndCommentsBackward(source, markStart - 1);
        if (staticScan >= 5 && source.AsSpan(staticScan - 5, 6).SequenceEqual("static"))
        {
            bool staticPrevOk;
            if (staticScan - 6 < 0) { staticPrevOk = true; }
            else { staticPrevOk = !IsIdentifierChar(source[staticScan - 6]); }
            if (staticPrevOk) { markStart = staticScan - 5; } // static の 's' の位置からマーク
        }

        // markStart（static先頭 または async先頭 または * または bracketStart）から } までを未実行（0）にマークする（map の長さを超えないようにクランプする）
        int computedWriteEnd = Math.Min(braceEnd, map.Length);
        for (int m = markStart; m < computedWriteEnd; m++) { if (map[m] == -1) { map[m] = 0; } }
        return braceEnd;
    }

    /// <summary>
    /// メソッド短縮構文（例: greet() { }）を検出し、
    /// カバレッジデータにない（未実行）場合は本体全体を未実行（0）としてマークする。
    /// function キーワードおよびコントロールフローキーワードは除外する。
    /// </summary>
    /// <param name="source">スクリプトのソースコード全文</param>
    /// <param name="map">BuildCoverageMap で作成したカバレッジマップ（内容を書き換える）</param>
    /// <param name="identStart">identifier の先頭インデックス</param>
    /// <param name="len">スキャン範囲の終端インデックス（含まない）</param>
    private static int TryMarkMethodShorthand(string source, int[] map, int identStart, int len)
    {
        // identifier 名を収集する
        int identEnd = identStart;
        while (identEnd < len && IsIdentifierChar(source[identEnd]))
        {
            identEnd++;
        }
        string identName = source.Substring(identStart, identEnd - identStart);

        // function キーワードとコントロールフローキーワードは除外する
        // （function は上の処理で別途扱うため、ここでは処理しない）
        bool isExcluded;
        if (identName == "function")
        {
            // function キーワードは上の処理で別途扱うため除外する
            isExcluded = true;
        }
        else if (ControlFlowKeywords.Contains(identName))
        {
            // if / for / while / switch / catch / do は関数でないため除外する
            isExcluded = true;
        }
        else
        {
            // その他の identifier はメソッド短縮構文の候補とする
            isExcluded = false;
        }

        if (isExcluded)
        {
            // 除外対象なので何もしない（identEnd を返してキーワード全体をスキップする）
            return identEnd;
        }

        // identifier の後の空白・コメントをスキップする（function と同様に SkipWhitespaceAndCommentsForward を使う）
        int afterIdent = SkipWhitespaceAndCommentsForward(source, identEnd, len);

        // 次の文字が ( でなければメソッド短縮構文でない
        if (afterIdent >= len || source[afterIdent] != '(')
        {
            return identStart + 1;
        }

        // SkipBalancedParens でパラメータ括弧をスキップする
        // SkipBalancedParens は閉じ括弧 ) の直後のインデックスを返す
        int afterParens = SkipBalancedParens(source, afterIdent);

        // パラメータの後の空白・コメントをスキップする（greet() /* c */ {} / greet() // c\n{} のケース）
        afterParens = SkipWhitespaceAndCommentsForward(source, afterParens, len);

        // 次の文字が { であり、かつカバレッジデータにない場合のみ処理する
        if (afterParens >= len || source[afterParens] != '{' || map[identStart] != -1)
        {
            return identStart + 1;
        }

        // 対応する } を探す（FindMatchingBrace は } の直後のインデックスを返す）
        int braceEnd = FindMatchingBrace(source, afterParens);
        if (braceEnd <= afterParens)
        {
            // 対応する } が見つからなかった場合は何もしない
            return identStart + 1;
        }

        // FindMatchingBrace は } の次の位置を返すため、} 自体は braceEnd - 1
        // async / ジェネレーター（*）メソッド短縮構文の場合、これらのキーワードも未実行（赤）としてマークする
        // 例: async greet() {} → async の先頭からマークする
        // 例: *gen() {}        → * からマークする
        // 例: async *gen() {}  → async の先頭からマークする
        // まず identStart を基準のマーク開始位置として設定する
        int markStart = identStart;
        // identStart の直前を逆走査して * や async や get/set キーワードを探す
        int asyncScanBack = SkipWhitespaceAndCommentsBackward(source, identStart - 1);

        // get / set プロパティ構文の確認 (例: get myProp() {})
        if (asyncScanBack >= 2)
        {
            string maybeGetSet = source.Substring(asyncScanBack - 2, 3);
            if (maybeGetSet == "get" || maybeGetSet == "set")
            {
                bool prefixOk = asyncScanBack - 3 < 0 || !IsIdentifierChar(source[asyncScanBack - 3]);
                if (prefixOk)
                {
                    markStart = asyncScanBack - 2;
                    // asyncScanBack - 3 が負になる場合（get/set がファイル先頭）は
                    // -1 を直接設定して SkipWhitespaceAndCommentsBackward を不要に呼ばない
                    if (asyncScanBack - 3 >= 0)
                    {
                        asyncScanBack = SkipWhitespaceAndCommentsBackward(source, asyncScanBack - 3);
                    }
                    else
                    {
                        asyncScanBack = -1;
                    }
                }
            }
        }

        // ジェネレーターメソッドの * プレフィックスを確認する（例: *gen() {} / async *gen() {}）
        bool hasStar = false;
        int starPos = -1;
        if (asyncScanBack >= 0 && source[asyncScanBack] == '*')
        {
            hasStar = true;
            starPos = asyncScanBack;
            // * を消費してさらに後ろを走査する（async *gen の 'async' を検出するため）
            asyncScanBack = SkipWhitespaceAndCommentsBackward(source, asyncScanBack - 1);
        }

        // 5文字以上あり "async" であれば async キーワードとして検出する
        if (asyncScanBack >= 4)
        {
            if (source.AsSpan(asyncScanBack - 4, 5).SequenceEqual("async"))
            {
                // async の前が識別子文字でないことを確認する（notasync などを除外する）
                bool asyncPrevOk;
                if (asyncScanBack - 5 < 0)
                {
                    // ファイル先頭なので前に文字がない → standalone async
                    asyncPrevOk = true;
                }
                else
                {
                    asyncPrevOk = !IsIdentifierChar(source[asyncScanBack - 5]);
                }
                if (asyncPrevOk)
                {
                    // async キーワードの先頭（'a' の位置）をマーク開始位置にする
                    markStart = asyncScanBack - 4;
                }
            }
        }

        // ジェネレーターの * があり、かつ markStart より前にある場合は * まで延ばす
        // 例: *gen() {}       → markStart = starPos
        // 例: async *gen() {} → async の 'a' が markStart のため延ばさない（* は async より後ろ）
        if (hasStar && starPos < markStart)
        {
            markStart = starPos;
        }

        // "static" キーワードを確認する（markStart の直前に static があるかチェック）
        // 例: static run() {}       → static の先頭からマークする
        // 例: static async run() {} → static の先頭からマークする
        // 例: static get value() {} → static の先頭からマークする
        int staticScan = SkipWhitespaceAndCommentsBackward(source, markStart - 1);
        if (staticScan >= 5 && source.AsSpan(staticScan - 5, 6).SequenceEqual("static"))
        {
            bool staticPrevOk;
            if (staticScan - 6 < 0) { staticPrevOk = true; }
            else { staticPrevOk = !IsIdentifierChar(source[staticScan - 6]); }
            if (staticPrevOk) { markStart = staticScan - 5; }
        }

        // markStart（static先頭 または async先頭 または * または identStart）から } までを未実行（0）にマークする（map の長さを超えないようにクランプする）
        int shorthandWriteEnd = Math.Min(braceEnd, map.Length);
        for (int m = markStart; m < shorthandWriteEnd; m++)
        {
            if (map[m] == -1)
            {
                map[m] = 0;
            }
        }
        // メソッド本体の直後（braceEnd）を返し、ScanRange がメソッド本体内を二重スキャンしないようにする
        return braceEnd;
    }

    /// <summary>
    /// 文字が JavaScript の識別子として有効な文字かどうかを判定する。
    /// function キーワードの前後チェックおよび関数名のスキップに使う。
    /// </summary>
    /// <param name="c">判定対象の文字</param>
    /// <returns>識別子文字なら true</returns>
    private static bool IsIdentifierChar(char c)
    {
        if (char.IsLetterOrDigit(c)) { return true; }
        if (c == '_') { return true; }
        if (c == '$') { return true; }
        if (c == '#') { return true; } // private fields in JS
        if (char.IsSurrogate(c)) { return true; } // サロゲートペア（高位・低位サロゲート、絵文字など）を識別子の一部として許容する
        return false;
    }

    /// <summary>
    /// ソースコードの位置 pos にある '/' が正規表現リテラルの開始かどうかを判定する。
    /// 直前の非空白文字が識別子末尾・数字・) ] でなければ正規表現と判断する（簡易ヒューリスティック）。
    /// </summary>
    /// <param name="source">ソースコード全文</param>
    /// <param name="pos">'/' のインデックス</param>
    /// <returns>正規表現リテラルの開始なら true、除算演算子なら false</returns>
    private static bool IsRegexStart(string source, int pos)
    {
        // pos の直前の非空白・非コメント文字を探す（ブロックコメントもスキップする）
        int i = SkipWhitespaceAndCommentsBackward(source, pos - 1);
        // ファイル先頭または文の先頭なら正規表現
        if (i < 0) { return true; }
        char prev = source[i];
        // 閉じ括弧・閉じ角括弧・テンプレートリテラル閉じの後は除算演算子（式の末尾）
        if (prev == ')' || prev == ']' || prev == '`') { return false; }
        // 後置インクリメント（++）の直後の / は除算演算子（正規表現ではない）
        // 例: x++ /2 の / は "x++ を評価した後に /2 で割る" 除算
        // prev（i番目の文字）が + で、その直前（i-1番目）も + であれば ++ と判断する
        if (prev == '+' && i > 0 && source[i - 1] == '+') { return false; }
        // 後置デクリメント（--）の直後の / も同様に除算演算子
        if (prev == '-' && i > 0 && source[i - 1] == '-') { return false; }
        // 識別子文字で終わる場合は、直前のトークン全体を確認する
        if (IsIdentifierChar(prev))
        {
            // 直前のトークン（識別子またはキーワード）を取り出す
            int j = i;
            while (j >= 0 && IsIdentifierChar(source[j])) { j--; }
            string token = source.Substring(j + 1, i - j);
            // return / typeof / void / delete / throw / new / in / instanceof の後は正規表現
            // （これらはオペランドを期待するキーワードなので、直後の / は除算でなく正規表現の開始）
            if (RegexPrecedingKeywords.Contains(token)) { return true; }
            // それ以外の識別子・変数名の後は除算演算子
            return false;
        }
        // 演算子・区切り文字・文字列終端クォートの後は正規表現
        // 注: SkipWhitespaceAndCommentsBackward は文字列内容を読み飛ばさないため "str"/2 のような除算を正規表現と誤判定する可能性があるが、実用上は許容範囲
        return true;
    }

    /// <summary>
    /// 単一引用符の文字列 '...' をスキップして閉じ ' の直後の位置を返す。
    /// バックスラッシュによるエスケープを考慮する。
    /// </summary>
    /// <param name="source">ソースコード全文</param>
    /// <param name="start">開き ' のインデックス</param>
    /// <returns>閉じ ' の直後のインデックス（閉じ ' が見つからない場合はソース末尾）</returns>
    private static int SkipSingleQuotedString(string source, int start)
    {
        int i = start + 1; // 開き ' の次の文字から走査する
        int len = source.Length;
        while (i < len && source[i] != '\'')
        {
            // バックスラッシュエスケープの次の文字を読み飛ばす
            if (source[i] == '\\') { i++; }
            i++;
        }
        // 閉じ ' をスキップする（見つからない場合は末尾のまま）
        if (i < len) { i++; }
        return i;
    }

    /// <summary>
    /// 二重引用符の文字列 "..." をスキップして閉じ " の直後の位置を返す。
    /// バックスラッシュによるエスケープを考慮する。
    /// </summary>
    /// <param name="source">ソースコード全文</param>
    /// <param name="start">開き " のインデックス</param>
    /// <returns>閉じ " の直後のインデックス（閉じ " が見つからない場合はソース末尾）</returns>
    private static int SkipDoubleQuotedString(string source, int start)
    {
        int i = start + 1; // 開き " の次の文字から走査する
        int len = source.Length;
        while (i < len && source[i] != '"')
        {
            // バックスラッシュエスケープの次の文字を読み飛ばす
            if (source[i] == '\\') { i++; }
            i++;
        }
        // 閉じ " をスキップする（見つからない場合は末尾のまま）
        if (i < len) { i++; }
        return i;
    }

    /// <summary>
    /// テンプレートリテラル `...` をスキップして閉じ ` の直後の位置を返す。
    /// ${ } 内の {} ネスト・文字列・ネストされたテンプレートリテラルを再帰的に処理する。
    /// これにより `${ {key: 'val'} }` や `${ `inner` }` のような複雑なケースも正しく処理できる。
    /// </summary>
    /// <param name="source">ソースコード全文</param>
    /// <param name="start">開き ` のインデックス</param>
    /// <returns>閉じ ` の直後のインデックス（閉じ ` が見つからない場合はソース末尾）</returns>
    private static int SkipTemplateLiteralFull(string source, int start)
    {
        int i = start + 1; // 開き ` の次の文字から走査する
        int len = source.Length;
        while (i < len)
        {
            char c = source[i];
            // バックスラッシュエスケープの次の文字を読み飛ばす
            if (c == '\\') { i += 2; continue; }
            // 閉じ ` でテンプレートリテラル終了
            if (c == '`') { return i + 1; }
            // ${ の開始 → 式部分を深さカウントでスキップする
            if (c == '$' && i + 1 < len && source[i + 1] == '{')
            {
                i += 2; // ${ の2文字をスキップする
                int depth = 1; // ${ の { を深さ1として開始する
                while (i < len && depth > 0)
                {
                    char t = source[i];
                    // バックスラッシュエスケープをスキップする
                    if (t == '\\') { i += 2; continue; }
                    // 行コメント // をスキップする（コメント内の } が深さカウントを狂わせないようにする）
                    if (t == '/' && i + 1 < len && source[i + 1] == '/')
                    {
                        while (i < len && source[i] != '\n') { i++; }
                        continue;
                    }
                    // ブロックコメント /* */ をスキップする（コメント内の } が深さカウントを狂わせないようにする）
                    if (t == '/' && i + 1 < len && source[i + 1] == '*')
                    {
                        i += 2;
                        while (i + 1 < len && !(source[i] == '*' && source[i + 1] == '/')) { i++; }
                        if (i + 1 < len) { i += 2; }
                        continue;
                    }
                    // 文字列リテラルをスキップする（中の } が深さカウントを狂わせないようにする）
                    if (t == '\'') { i = SkipSingleQuotedString(source, i); continue; }
                    if (t == '"')  { i = SkipDoubleQuotedString(source, i); continue; }
                    // ネストされたテンプレートリテラルを再帰的にスキップする
                    if (t == '`')  { i = SkipTemplateLiteralFull(source, i); continue; }
                    // { で深さが増える、} で深さが減る
                    if (t == '{') { depth++; }
                    else if (t == '}')
                    {
                        depth--;
                        // 深さが 0 になったら ${ } の閉じ } を消費して次へ進む
                        if (depth == 0) { i++; continue; }
                    }
                    i++;
                }
                continue;
            }
            i++;
        }
        // 閉じ ` が見つからなかった場合（構文エラーのソース）はソース末尾を返す
        return i;
    }

    /// <summary>
    /// '/' から始まる正規表現リテラル /pattern/flags をスキップして直後の位置を返す。
    /// 文字クラス [] の中では '/' をエスケープなしに使える点を考慮する。
    /// </summary>
    /// <param name="source">ソースコード全文</param>
    /// <param name="start">'/' のインデックス（正規表現の開始 /）</param>
    /// <returns>フラグ（g/i/m/s/u/y）を含む全体をスキップした直後の位置</returns>
    private static int SkipRegexLiteral(string source, int start)
    {
        int i = start + 1; // 開始の '/' の次から走査する
        int len = source.Length;
        // [] の中かどうかを追跡する（文字クラス内では '/' が終端にならない）
        bool inCharClass = false;
        while (i < len)
        {
            char c = source[i];
            // バックスラッシュエスケープの次の文字を読み飛ばす
            if (c == '\\') { i += 2; continue; }
            // 文字クラスの開始・終了を追跡する
            if (c == '[') { inCharClass = true; }
            else if (c == ']') { inCharClass = false; }
            // 文字クラス外の '/' が正規表現の終端
            else if (c == '/' && !inCharClass) { i++; break; }
            // 改行が来たら正規表現は終了（JS の正規表現は改行をまたがない）
            else if (c == '\n') { break; }
            i++;
        }
        // フラグ部分（g, i, m, s, u, y など識別子文字）をスキップする
        while (i < len && IsIdentifierChar(source[i])) { i++; }
        return i;
    }

    /// <summary>
    /// 開き括弧 '(' の位置から対応する閉じ括弧 ')' の直後の位置を返す。
    /// 括弧の中にある文字列・ネストした括弧を考慮する。
    /// </summary>
    /// <param name="source">ソースコード全文</param>
    /// <param name="start">開き括弧 '(' のインデックス</param>
    /// <returns>対応する閉じ括弧 ')' の直後のインデックス</returns>
    private static int SkipBalancedParens(string source, int start)
    {
        int i = start + 1; // '(' の次の文字から開始する
        int depth = 1;
        int len = source.Length;

        while (i < len && depth > 0)
        {
            char c = source[i];
            if (c == '(') { depth++; }
            else if (c == ')') { depth--; }
            else if (c == '\'')
            {
                // 単一引用符の文字列をスキップする（ヘルパーを使用）
                i = SkipSingleQuotedString(source, i);
                continue;
            }
            else if (c == '"')
            {
                // 二重引用符の文字列をスキップする（ヘルパーを使用）
                i = SkipDoubleQuotedString(source, i);
                continue;
            }
            else if (c == '`')
            {
                // テンプレートリテラルをスキップする（${ } ネスト対応の完全版）
                i = SkipTemplateLiteralFull(source, i);
                continue;
            }
            else if (c == '/' && i + 1 < len && source[i + 1] == '/')
            {
                // 行コメントをスキップする（コメント内の ( ) が深さカウントに影響しないようにする）
                // FindMatchingBrace と同様に continue で外側の i++ をスキップする
                while (i < len && source[i] != '\n') { i++; }
                continue;
            }
            else if (c == '/' && i + 1 < len && source[i + 1] == '*')
            {
                // ブロックコメントをスキップする（コメント内の ( ) が深さカウントに影響しないようにする）
                i += 2;
                while (i + 1 < len && !(source[i] == '*' && source[i + 1] == '/')) { i++; }
                i++; // '*' をスキップする（直後の '/' は外側の i++ で処理される）
            }
            else if (c == '/' && IsRegexStart(source, i))
            {
                // 正規表現リテラルをスキップして括弧のカウントがずれないようにする
                i = SkipRegexLiteral(source, i);
                continue;
            }
            i++;
        }

        // depth が 0 になった時点で i は ')' の次の位置を指している
        return i;
    }

    /// <summary>
    /// 開き角括弧 '[' の位置から対応する閉じ角括弧 ']' の直後の位置を返す。
    /// 文字列リテラル・ネストした角括弧を考慮する（computed property key のスキップに使用）。
    /// </summary>
    private static int SkipBalancedBrackets(string source, int start)
    {
        int i = start + 1;
        int depth = 1;
        int len = source.Length;
        while (i < len && depth > 0)
        {
            char c = source[i];
            if      (c == '[')  { depth++; }
            else if (c == ']')  { depth--; }
            else if (c == '\'') { i = SkipSingleQuotedString(source, i); continue; }
            else if (c == '"')  { i = SkipDoubleQuotedString(source, i); continue; }
            else if (c == '`')  { i = SkipTemplateLiteralFull(source, i); continue; }
            else if (c == '/' && i + 1 < len && source[i + 1] == '/')
            {
                // 行コメントをスキップする（コメント内の ] が深さカウントに影響しないようにする）
                while (i < len && source[i] != '\n') { i++; }
                continue;
            }
            else if (c == '/' && i + 1 < len && source[i + 1] == '*')
            {
                // ブロックコメントをスキップする（コメント内の ] が深さカウントに影響しないようにする）
                i += 2;
                while (i + 1 < len && !(source[i] == '*' && source[i + 1] == '/')) { i++; }
                i++; // '*' をスキップする（直後の '/' は外側の i++ で処理される）
            }
            else if (c == '/' && IsRegexStart(source, i))
            {
                // 正規表現リテラルをスキップする（正規表現内の ] が深さカウントを狂わせないようにする）
                i = SkipRegexLiteral(source, i);
                continue;
            }
            i++;
        }
        return i;
    }

    /// <summary>
    /// 開き波括弧 '{' の位置から対応する閉じ波括弧 '}' の直後の位置を返す。
    /// 波括弧の中にある文字列・コメント・ネストした波括弧を考慮する。
    /// </summary>
    /// <param name="source">ソースコード全文</param>
    /// <param name="start">開き波括弧 '{' のインデックス</param>
    /// <returns>対応する閉じ波括弧 '}' の直後のインデックス。見つからない場合は -1</returns>
    private static int FindMatchingBrace(string source, int start)
    {
        int i = start + 1; // '{' の次の文字から開始する
        int depth = 1;
        int len = source.Length;

        while (i < len)
        {
            char c = source[i];
            if (c == '{') { depth++; }
            else if (c == '}')
            {
                depth--;
                // 対応する '}' が見つかった場合はその直後の位置を返す
                if (depth == 0) { return i + 1; }
            }
            else if (c == '\'')
            {
                // 単一引用符の文字列をスキップする（ヘルパーを使用）
                i = SkipSingleQuotedString(source, i);
                continue;
            }
            else if (c == '"')
            {
                // 二重引用符の文字列をスキップする（ヘルパーを使用）
                i = SkipDoubleQuotedString(source, i);
                continue;
            }
            else if (c == '`')
            {
                // テンプレートリテラルをスキップする（${ } ネスト対応の完全版）
                i = SkipTemplateLiteralFull(source, i);
                continue;
            }
            else if (c == '/')
            {
                if (i + 1 < len && source[i + 1] == '/')
                {
                    // 行コメントをスキップする（改行まで読み飛ばす）
                    while (i < len && source[i] != '\n') { i++; }
                    continue;
                }
                else if (i + 1 < len && source[i + 1] == '*')
                {
                    // ブロックコメントをスキップする
                    i += 2;
                    while (i + 1 < len && !(source[i] == '*' && source[i + 1] == '/')) { i++; }
                    i++; // '*' をスキップする（直後の '/' は外側の i++ で処理される）
                }
                else if (IsRegexStart(source, i))
                {
                    // 正規表現リテラルをスキップする（/regex/ 内の } が深さカウントを狂わせないようにする）
                    i = SkipRegexLiteral(source, i);
                    continue; // SkipRegexLiteral が終端の次の位置を返すので i++ をスキップする
                }
            }
            i++;
        }

        // 対応する '}' が見つからなかった場合（構文エラーのソース）
        return -1;
    }

    /// <summary>
    /// 2つのカバレッジマップを OR 合成して返す。
    /// どちらかのマップで実行済み（1）なら合成結果も実行済み（1）にする。
    /// baseMap の長さを基準とし、otherMap が短い場合は対象外（-1）として扱う。
    /// </summary>
    /// <param name="baseMap">基準となるカバレッジマップ（カノニカルソースの長さに合わせる）</param>
    /// <param name="otherMap">OR 合成するカバレッジマップ</param>
    /// <returns>OR 合成したカバレッジマップ（baseMap と同じ長さ）</returns>
    internal static int[] MergeMaps(int[] baseMap, int[] otherMap)
    {
        // null ガード
        if (baseMap == null)
        {
            throw new ArgumentNullException(nameof(baseMap));
        }
        if (otherMap == null)
        {
            throw new ArgumentNullException(nameof(otherMap));
        }
        // baseMap の長さを基準に合成結果の配列を作る
        var merged = new int[baseMap.Length];
        for (int i = 0; i < baseMap.Length; i++)
        {
            int v1 = baseMap[i];
            // otherMap が baseMap より短い場合は対象外（-1）として扱う
            int v2;
            if (i < otherMap.Length)
            {
                v2 = otherMap[i];
            }
            else
            {
                v2 = -1;
            }

            // いずれかが実行済み（1）なら実行済みにする（OR 合成）
            if (v1 == 1 || v2 == 1)
            {
                merged[i] = 1;
            }
            // 両方が実行済みでなく、少なくとも一方が未実行（0）なら未実行とする（対象外 -1 より優先）
            else if (v1 == 0 || v2 == 0)
            {
                merged[i] = 0;
            }
            // 両方が対象外（-1）なら対象外にする
            else
            {
                merged[i] = -1;
            }
        }
        return merged;
    }

    /// <summary>
    /// 指定された位置から前方に向かって、空白文字・ブロックコメント・行コメントを読み飛ばす。
    /// </summary>
    private static int SkipWhitespaceAndCommentsForward(string source, int pos, int end)
    {
        while (pos < end)
        {
            if (char.IsWhiteSpace(source[pos])) { pos++; continue; }
            if (pos + 1 < end && source[pos] == '/' && source[pos + 1] == '*')
            {
                pos += 2;
                while (pos + 1 < end && !(source[pos] == '*' && source[pos + 1] == '/')) { pos++; }
                // */ がサブレンジ境界を跨いでいる場合（pos が * で pos+1 が end 外）は end まで進める
                if (pos + 1 < end) { pos += 2; } else { pos = end; }
                continue;
            }
            if (pos + 1 < end && source[pos] == '/' && source[pos + 1] == '/')
            {
                while (pos < end && source[pos] != '\n') { pos++; }
                continue;
            }
            break;
        }
        return pos;
    }

    /// <summary>
    /// 指定された位置から逆方向（前）に向かって、空白文字・ブロックコメント (/* ... */)・行コメント (//) を読み飛ばす。
    /// </summary>
    private static int SkipWhitespaceAndCommentsBackward(string source, int pos)
    {
        while (pos >= 0)
        {
            if (char.IsWhiteSpace(source[pos]))
            {
                pos--;
            }
            else if (pos - 1 >= 0 && source[pos] == '/' && source[pos - 1] == '*')
            {
                // ブロックコメントの終端 */ を検出した → 開始 /* まで逆走査する
                pos -= 2;
                while (pos - 1 >= 0 && !(source[pos] == '*' && source[pos - 1] == '/'))
                {
                    pos--;
                }
                // 開始 /* が見つかった場合は /* の2文字をスキップする
                // 見つからなかった場合（壊れたソース）は pos を -1 にして終了する
                if (pos - 1 >= 0)
                {
                    pos -= 2;
                }
                else
                {
                    pos = -1;
                }
            }
            else
            {
                // 行コメント（//）の末尾にいるかチェックする
                // LastIndexOf で行頭を O(1) に近い速さで取得する（SIMD 最適化が効く）
                int prevNl = -1;
                if (pos > 0) { prevNl = source.LastIndexOf('\n', pos - 1); }
                int lp = 0;
                if (prevNl >= 0) { lp = prevNl + 1; }
                bool inLineComment = false;
                int k = lp;
                while (k < pos)
                {
                    char kc = source[k];
                    // 文字列リテラル（シングル・ダブルクォート）をスキップする
                    if (kc == '\'' || kc == '"')
                    {
                        k++;
                        while (k < pos && source[k] != kc)
                        {
                            if (source[k] == '\\') { k++; }
                            k++;
                        }
                        if (k < pos) { k++; }
                        continue;
                    }
                    // テンプレートリテラル（バッククォート）をスキップする
                    // SkipTemplateLiteralFull はネストした ${ `...` } も正確に処理する
                    if (kc == '`')
                    {
                        k = SkipTemplateLiteralFull(source, k);
                        continue;
                    }
                    // ブロックコメント /* */ をスキップする（コメント内の // を行コメントと誤判定しないようにする）
                    if (kc == '/' && k + 1 < source.Length && source[k + 1] == '*')
                    {
                        k += 2;
                        while (k + 1 < source.Length && !(source[k] == '*' && source[k + 1] == '/')) { k++; }
                        if (k + 1 < source.Length) { k += 2; }
                        continue;
                    }
                    // 正規表現リテラルをスキップする（/\/\// のような正規表現内の // を行コメントと誤判定しないようにする）
                    if (kc == '/' && IsRegexStart(source, k))
                    {
                        k = SkipRegexLiteral(source, k);
                        continue;
                    }
                    if (source[k] == '/' && k + 1 < source.Length && source[k + 1] == '/')
                    {
                        pos = k - 1;
                        inLineComment = true;
                        break;
                    }
                    k++;
                }
                if (!inLineComment) { break; }
            }
        }
        return pos;
    }
}


/// <summary>
/// 行のカバレッジ状態を表す列挙型。
/// HTML出力時のCSSクラス選択やカウント集計に使用する。
/// </summary>
internal enum LineCoverageStatus
{
    /// <summary>カバレッジ対象外（空行・コメントなど実行されないコード）</summary>
    Neutral,
    /// <summary>完全に実行された行（行内のすべての文字が実行済み）</summary>
    Covered,
    /// <summary>全く実行されなかった行（行内のすべての文字が未実行）</summary>
    Uncovered,
    /// <summary>実行された部分と未実行の部分が混在する行（if/else の分岐など）</summary>
    Partial,
}

/// <summary>
/// 1行分のHTMLと状態をまとめるレコード型。
/// BuildLines メソッドが生成し、BuildScriptPage メソッドが利用する。
/// </summary>
/// <param name="Html">行のHTMLコンテンツ（span タグでカバレッジ状態を色付けしたもの）</param>
/// <param name="Status">行全体のカバレッジ状態</param>
internal record LineData(string Html, LineCoverageStatus Status);

/// <summary>
/// HTMLカバレッジレポートを生成するクラス。
/// index.html（サマリー）と scripts/script-N.html（詳細）を生成する。
/// </summary>
internal class HtmlReportGenerator
{
    /// <summary>
    /// HTMLの特殊文字をエスケープする。
    /// ブラウザがHTMLタグとして解釈しないように変換する。
    /// </summary>
    /// <param name="text">エスケープ対象の文字列</param>
    /// <returns>HTMLエスケープ済みの文字列</returns>
    internal static string HtmlEncode(string text)
    {
        // 1文字ずつスキャンして HTML 特殊文字をエスケープする
        // sequential Replace では中間文字列が4つ生成されるため StringBuilder を使う（1パスで完結）
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '&')
            {
                // & は &amp; にエスケープする（他の変換結果の & を二重変換しないよう最初に処理）
                sb.Append("&amp;");
            }
            else if (c == '<')
            {
                // < は &lt; にエスケープする（HTML タグの開始文字として解釈されないようにする）
                sb.Append("&lt;");
            }
            else if (c == '>')
            {
                // > は &gt; にエスケープする（HTML タグの終了文字として解釈されないようにする）
                sb.Append("&gt;");
            }
            else if (c == '"')
            {
                // " は &quot; にエスケープする（HTML 属性値の中で使用できるようにする）
                sb.Append("&quot;");
            }
            else
            {
                // その他の文字はそのまま追加する
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// ソースコードを行ごとに分割し、各行のHTMLと状態を返す。
    /// 各文字にカバレッジ値に応じた span タグを付けてHTMLを構築する。
    /// </summary>
    /// <param name="source">スクリプトのソースコード全文</param>
    /// <param name="map">BuildCoverageMap が返したカバレッジ値の配列</param>
    /// <returns>行ごとの LineData オブジェクトのリスト</returns>
    /// <summary>
    /// ソースコードを改行文字（\n、\r\n、\r のみ）で行に分割する。
    /// \r\n の場合は \r を行末に含め（Split('\n') と同じ）、\r のみの場合は \r を区切り文字として除く。
    /// オフセット計算の整合性は保たれる（各行の区切り文字が1文字のため +1 が正しく適用される）。
    /// </summary>
    private static string[] SplitOnNewlines(string source)
    {
        var lines = new List<string>();
        int lineStart = 0;
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '\n')
            {
                // LF: Split('\n') と同じ動作（\r\n なら \r が rawLine に残る）
                lines.Add(source.Substring(lineStart, i - lineStart));
                lineStart = i + 1;
            }
            else if (c == '\r' && (i + 1 >= source.Length || source[i + 1] != '\n'))
            {
                // CR のみ（後ろに \n がない場合）: \r を区切り文字として行分割する
                lines.Add(source.Substring(lineStart, i - lineStart));
                lineStart = i + 1;
            }
            i++;
        }
        // 最後の行（末尾に改行がない場合も含む）
        lines.Add(source.Substring(lineStart));
        return lines.ToArray();
    }

    internal static List<LineData> BuildLines(string source, int[] map)
    {
        // 行データを格納する結果リスト
        var result = new List<LineData>();

        // 空文字列・null ソースは空リストを返す（CDP データ取得失敗などで null になった場合の防衛）
        if (string.IsNullOrEmpty(source)) { return result; }

        // ソースコードを改行文字で行に分割する（\n、\r\n、\r のみ に対応）
        var rawLines = SplitOnNewlines(source);

        // ソースが改行文字で終わる場合、末尾に必ず空の要素が生まれるので除く
        int lineCount = rawLines.Length;
        if (lineCount > 0 && (source.EndsWith('\n') || source.EndsWith('\r')))
        {
            // 末尾の空要素を処理対象から除外するためカウントを1減らす
            lineCount--;
        }

        // map 配列内の現在位置（ソースコード先頭からの文字インデックス）
        int offset = 0;

        // 各行を順番に処理する
        for (int li = 0; li < lineCount; li++)
        {
            // 現在処理中の生の行テキスト
            var rawLine = rawLines[li];
            // 行のHTMLを構築するための文字列ビルダー
            var sb = new StringBuilder();

            // 行内で実行済みの文字数（行の状態判定に使う）
            int coveredCount = 0;
            // 行内で未実行の文字数（行の状態判定に使う）
            int uncoveredCount = 0;

            // 現在開いている <span> のカバレッジ状態（-2 = まだ <span> を開いていない）
            int currentState = -2;

            // 行内の各文字を順番に処理する
            for (int i = 0; i < rawLine.Length; i++)
            {
                // \r（CRLF 改行の CR 部分）はカバレッジカウントに含めない（オフセット値には含まれるので idx の計算に影鈷しない）。
                // \0（ヌル文字）は理論上 JS ソースに含まれる可能性があるが、
                // HTML 出力時にブラウザが無視するため表示上の問題がなく、カバレッジカウントに含めない。
                char chSkip = rawLine[i];
                if (chSkip == '\r' || chSkip == '\0')
                {
                    // offset は進めない（後で offset += rawLine.Length + 1 でまとめて加算する）
                    continue;
                }

                // この文字のソースコード全体でのインデックス
                int idx = offset + i;

                // map の範囲内ならマップの値を使い、範囲外なら -1（対象外）とする
                int coverage;
                if (idx < map.Length)
                {
                    // マップからカバレッジ値（-1/0/1）を取得する
                    coverage = map[idx];
                }
                else
                {
                    // マップの範囲外の文字はカバレッジ対象外とする
                    coverage = -1;
                }

                // カバレッジ状態が変わったとき、<span> を閉じて新しい <span> を開く
                if (coverage != currentState)
                {
                    // 既にスパンが開いていれば閉じる
                    if (currentState != -2)
                    {
                        sb.Append("</span>");
                    }

                    // 現在のカバレッジ状態を更新する
                    currentState = coverage;

                    // カバレッジ状態に応じてCSSクラス名を決める
                    string cls;
                    if (coverage == 1)
                    {
                        // 実行済みの文字に緑色背景を付けるクラス
                        cls = "covered";
                    }
                    else if (coverage == 0)
                    {
                        // 未実行の文字に赤色背景を付けるクラス
                        cls = "uncovered";
                    }
                    else
                    {
                        // カバレッジ対象外の文字（背景なし）
                        cls = "neutral";
                    }

                    // 新しい span タグを開始する
                    sb.Append($"<span class=\"{cls}\">");
                }

                // 文字をHTMLエスケープして追加する（\r・\0 は上の continue で既にスキップ済み）
                char ch = rawLine[i];
                if (ch == '&')
                {
                    // アンパサンドをエスケープする
                    sb.Append("&amp;");
                }
                else if (ch == '<')
                {
                    // 小なり記号をエスケープする
                    sb.Append("&lt;");
                }
                else if (ch == '>')
                {
                    // 大なり記号をエスケープする
                    sb.Append("&gt;");
                }
                else if (ch == '"')
                {
                    // ダブルクォートをエスケープする
                    sb.Append("&quot;");
                }
                else
                {
                    // その他の文字はそのまま文字列に追加する（' はテキストノード内でエスケープ不要）
                    sb.Append(ch);
                }

                // 実行済み・未実行の文字数を集計する（行の状態判定に使う）
                if (coverage == 1)
                {
                    // 実行済み文字のカウントを増やす
                    coveredCount++;
                }
                else if (coverage == 0)
                {
                    // 未実行文字のカウントを増やす
                    uncoveredCount++;
                }
            }

            // 行末でスパンを閉じる（開いているスパンがある場合のみ）
            if (currentState != -2)
            {
                sb.Append("</span>");
            }

            // covered/uncovered の文字数に基づいて行のカバレッジ状態を決める
            LineCoverageStatus status;
            if (coveredCount == 0 && uncoveredCount == 0)
            {
                // カバレッジ対象外の行（空行やコメントなど）
                status = LineCoverageStatus.Neutral;
            }
            else if (coveredCount > 0 && uncoveredCount == 0)
            {
                // 全体が実行された行
                status = LineCoverageStatus.Covered;
            }
            else if (coveredCount == 0 && uncoveredCount > 0)
            {
                // 全体が未実行の行
                status = LineCoverageStatus.Uncovered;
            }
            else
            {
                // 実行済みと未実行が混在する行（分岐の一部だけ通った場合など）
                status = LineCoverageStatus.Partial;
            }

            // 行のHTMLと状態を結果リストに追加する
            result.Add(new LineData(sb.ToString(), status));

            // 次の行の offset を計算する（rawLine.Length + 1 は分割に使った \n の1文字分を加える）
            // CRLF ファイルの場合 rawLine には末尾の \r が含まれるため rawLine.Length は "\r" 込みの長さになる。
            // よって offset += rawLine.Length + 1 = (本文字数 + 1) + 1 で \r\n の2文字分が正しく加算される。
            offset += rawLine.Length + 1;
        }

        return result;
    }

    /// <summary>
    /// カバレッジデータからHTMLレポートを生成してファイルに書き出す。
    /// 同じスクリプトURLのデータは OR 合成して1件にまとめる。
    /// 複数タブで読み込まれた場合は合成ページに加え、タブ別の詳細ページも生成する。
    /// </summary>
    /// <param name="coverages">収集したスクリプトカバレッジデータのリスト</param>
    /// <param name="outputDir">レポートを出力するディレクトリのパス</param>
    internal void Generate(IReadOnlyList<ScriptCoverage> coverages, string outputDir)
    {
        // 出力ディレクトリを作成する（既に存在しても問題ない）
        Directory.CreateDirectory(outputDir);
        // スクリプト詳細ページを格納するサブディレクトリのパス
        var scriptsDir = Path.Combine(outputDir, "scripts");
        Directory.CreateDirectory(scriptsDir);

        // スクリプト URL と Source でグループ化する（同じ URL かつ同じソースコードのものを OR 合成する）
        // URL だけでなく Source もキーに含める理由: 同じ URL でもサーバーがわずかに異なるソースを返した
        // 場合（タイムスタンプコメント等）に誤って結合するのを防ぐ。Dictionary で O(1) 検索する。
        // BOM（U+FEFF）をグループキーから除去する（ここで一元管理することで、BOM 有無で同一スクリプトが別グループになる問題を防ぐ）
        var scriptGroupMap = new Dictionary<(string url, string source), List<ScriptCoverage>>();
        var scriptGroups = new List<List<ScriptCoverage>>();
        foreach (var script in coverages)
        {
            var key = (script.Url, script.Source.TrimStart('\uFEFF'));
            List<ScriptCoverage> existingGroup;
            if (scriptGroupMap.TryGetValue(key, out existingGroup))
            {
                existingGroup.Add(script);
            }
            else
            {
                var newGroup = new List<ScriptCoverage> { script };
                scriptGroupMap[key] = newGroup;
                scriptGroups.Add(newGroup);
            }
        }

        // インデックスページに表示するサマリー行のリスト
        var summaryRows = new List<(
            IReadOnlyList<(string label, string pageUrl, string tabFilename)> tabs,
            string url, int covered, int partial, int total, string mergedFilename)>();

        int i = 0;
        foreach (var group in scriptGroups)
        {
            if (group.Count == 0) continue;
            string scriptUrl = group[0].Url;

            // カノニカル（基準）スクリプトは最初のエントリとする
            var canonical = group[0];

            // BOM（U+FEFF）をソースから除去する
            // V8 は BOM を JavaScript の文字としてカウントしないため、CDP のオフセット値は
            // BOM 除去後の位置を指している。BOM を除去せずに渡すとオフセットが1ずれて
            // 末尾の文字がカバレッジ対象外（neutral）になるバグが発生する。
            string canonicalSource = canonical.Source.TrimStart('\uFEFF');

            // 全タブ分のカバレッジマップを OR 合成する
            var mergedMap = CoverageParser.BuildCoverageMap(canonicalSource, canonical.Functions);
            for (int g = 1; g < group.Count; g++)
            {
                // タブごとのソースも BOM を除去してから BuildCoverageMap に渡す
                string otherSource = group[g].Source.TrimStart('\uFEFF');
                var otherMap = CoverageParser.BuildCoverageMap(otherSource, group[g].Functions);
                mergedMap = CoverageParser.MergeMaps(mergedMap, otherMap);
            }

            // OR 合成したマップから行データを生成する（BOM 除去済みのソースを使う）
            var mergedLines = BuildLines(canonicalSource, mergedMap);

            // 1行しかないスクリプトはレポート対象外としてスキップする
            // （インライン eval や最小化された1行スクリプトなど、有意な情報が得られないため）
            // i はインクリメントしない → スキップしてもファイル番号に欠番が生じない
            if (mergedLines.Count <= 1) { continue; }

            // 合成ページのファイル名（全タブの OR 合成カバレッジを表示する）
            var mergedFilename = $"script-{i}.html";

            // 合成ページに表示する画面情報リスト（重複なし・URL 空文字除外）
            // 画面ラベルは "画面N"（N = タブインデックス + 1）
            var pageInfos = new List<(string label, string url)>();
            var seenPageInfos = new HashSet<(string, string)>();
            foreach (var s in group)
            {
                string screenLabel = $"画面{s.Page.Index + 1}";
                if (!string.IsNullOrEmpty(s.Page.Url))
                {
                    if (seenPageInfos.Add((screenLabel, s.Page.Url)))
                    {
                        pageInfos.Add((screenLabel, s.Page.Url));
                    }
                }
            }

            // 合成カバレッジの詳細ページを生成する
            File.WriteAllText(
                Path.Combine(scriptsDir, mergedFilename),
                BuildScriptPage(pageInfos, scriptUrl, mergedLines),
                Encoding.UTF8);

            // タブ情報リストを構築する（展開 UI 用）
            var tabs = new List<(string label, string pageUrl, string tabFilename)>();
            if (group.Count > 1)
            {
                // 複数タブの場合: 各タブ別の詳細ページも生成する
                for (int g = 0; g < group.Count; g++)
                {
                    var script = group[g];
                    // タブ別ページのファイル名（グループ内インデックス g を使用して衝突を防ぐ）
                    var tabFilename = $"script-{i}-tab{g}.html";
                    string tabLabel = $"画面{script.Page.Index + 1}";

                    // このタブ単独のカバレッジマップを生成する（BOM を除去してから渡す）
                    string tabSource = script.Source.TrimStart('\uFEFF');
                    var tabMap = CoverageParser.BuildCoverageMap(tabSource, script.Functions);
                    var tabLines = BuildLines(tabSource, tabMap);

                    // タブ別詳細ページを生成する（このタブの画面ラベルと URL を渡す）
                    var tabPageInfos = new List<(string label, string url)>();
                    if (!string.IsNullOrEmpty(script.Page.Url))
                    {
                        tabPageInfos.Add((tabLabel, script.Page.Url));
                    }
                    File.WriteAllText(
                        Path.Combine(scriptsDir, tabFilename),
                        BuildScriptPage(tabPageInfos, scriptUrl, tabLines),
                        Encoding.UTF8);

                    tabs.Add((tabLabel, script.Page.Url, tabFilename));
                }
            }
            else
            {
                // 単一タブの場合: タブ別ページは合成ページと同じ（別ファイルは生成しない）
                string singleLabel = $"画面{group[0].Page.Index + 1}";
                tabs.Add((singleLabel, group[0].Page.Url, mergedFilename));
            }

            // 合成データの行数を集計する
            int covered = 0, partial = 0, total = 0;
            foreach (var line in mergedLines)
            {
                if (line.Status == LineCoverageStatus.Covered)
                {
                    covered++;
                    total++;
                }
                else if (line.Status == LineCoverageStatus.Partial)
                {
                    partial++;
                    total++;
                }
                else if (line.Status == LineCoverageStatus.Uncovered)
                {
                    total++;
                }
            }

            summaryRows.Add((tabs, scriptUrl, covered, partial, total, mergedFilename));
            i++;
        }

        // インデックスページを生成してファイルに書き出す
        File.WriteAllText(
            Path.Combine(outputDir, "index.html"),
            BuildIndexPage(summaryRows),
            Encoding.UTF8);
    }

    /// <summary>
    /// スクリプト詳細ページ（行ごとに色付けされたソースコード表示）のHTMLを生成する。
    /// </summary>
    /// <param name="pageInfos">このスクリプトが読み込まれた画面情報のリスト（label="画面N", url=ページURL）</param>
    /// <param name="scriptUrl">スクリプトのURL（ページタイトルと見出しに使用）</param>
    /// <param name="lines">BuildLines が返した行データのリスト</param>
    /// <returns>スクリプト詳細ページの完全なHTML文字列</returns>
    internal static string BuildScriptPage(IReadOnlyList<(string label, string url)> pageInfos, string scriptUrl, List<LineData> lines)
    {
        // HTMLを構築するための文字列ビルダー
        var sb = new StringBuilder();

        // HTMLヘッダーとスタイルシートを出力する
        sb.AppendLine(HtmlTemplates.ScriptPageHeader);

        // 画面ラベルと URL の表示文字列を決める
        // 形式: "画面N (URL)" — URL が空の場合は "画面N" のみ
        string pageDisplay;
        if (pageInfos.Count == 0)
        {
            // 画面情報が取得できなかった場合のフォールバック表示
            pageDisplay = "(不明)";
        }
        else if (pageInfos.Count == 1)
        {
            // 1画面の場合: "画面N (URL)" または URL なしなら "画面N"
            string lbl = HtmlEncode(pageInfos[0].label);
            if (string.IsNullOrEmpty(pageInfos[0].url))
            {
                pageDisplay = lbl;
            }
            else
            {
                pageDisplay = $"{lbl} ({HtmlEncode(pageInfos[0].url)})";
            }
        }
        else
        {
            // 複数画面の場合: "画面N (URL), 画面M (URL)" カンマ区切り
            var parts = new List<string>();
            foreach (var (lbl, u) in pageInfos)
            {
                string encodedLbl = HtmlEncode(lbl);
                if (string.IsNullOrEmpty(u))
                {
                    parts.Add(encodedLbl);
                }
                else
                {
                    parts.Add($"{encodedLbl} ({HtmlEncode(u)})");
                }
            }
            pageDisplay = string.Join(", ", parts);
        }
        // 画面情報とスクリプトファイル名をページ見出しとして出力する
        sb.AppendLine($"<h1>{pageDisplay} / {HtmlEncode(GetFileName(scriptUrl))}</h1>");

        // 各色の意味を説明する凡例バーを出力する
        sb.AppendLine(HtmlTemplates.ScriptPageLegend);

        // ソースコード表示エリアを開く
        sb.AppendLine("<div class=\"source\">");

        // 各行をHTMLとして出力する
        for (int i = 0; i < lines.Count; i++)
        {
            // 現在処理中の行データ
            var line = lines[i];

            // 行のカバレッジ状態に応じてCSSクラス名を決める
            string cls;
            if (line.Status == LineCoverageStatus.Covered)
            {
                // 完全実行行は緑色の行番号を表示する
                cls = "line line-covered";
            }
            else if (line.Status == LineCoverageStatus.Uncovered)
            {
                // 未実行行は赤色の行番号を表示する
                cls = "line line-uncovered";
            }
            else if (line.Status == LineCoverageStatus.Partial)
            {
                // 部分実行行は黄色の行番号を表示する
                cls = "line line-partial";
            }
            else
            {
                // 対象外行は背景なしで表示する
                cls = "line";
            }

            // 行番号（i+1, 1始まり）と行のHTMLを div タグで囲んで出力する
            sb.AppendLine($"<div class=\"{cls}\"><span class=\"gutter\">{i + 1}</span><span class=\"code\">{line.Html}</span></div>");
        }

        // ページの閉じタグを出力する
        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// インデックスページ（スクリプト一覧とカバレッジ率の表）のHTMLを生成する。
    /// 複数タブで同じスクリプトが読み込まれた場合は展開ボタン付きで表示する。
    /// </summary>
    /// <param name="rows">各スクリプトのサマリー情報のリスト</param>
    /// <returns>インデックスページの完全なHTML文字列</returns>
    internal static string BuildIndexPage(
        List<(IReadOnlyList<(string label, string pageUrl, string tabFilename)> tabs,
              string url, int covered, int partial, int total, string mergedFilename)> rows)
    {
        // 全スクリプトの実行済み行数の合計
        int totalCovered = 0;
        // 全スクリプトの部分実行行数の合計
        int totalPartial = 0;
        // 全スクリプトのカバレッジ対象行数の合計
        int totalLines = 0;

        // 各スクリプトの行数を合計する（タプル要素名が変わるため分割代入を使う）
        foreach (var (_, _, covered, partial, total, _) in rows)
        {
            totalCovered += covered;
            totalPartial += partial;
            totalLines   += total;
        }

        // 全体のカバレッジ率を計算する（ゼロ除算を避けるため行数が0のときは0%にする）
        double overallPct;
        if (totalLines > 0)
        {
            // Partial 行は 0.5 行換算 — 部分実行は完全実行より低く評価する
            overallPct = 100.0 * (totalCovered + totalPartial * 0.5) / totalLines;
        }
        else
        {
            // 対象行がない場合は 0% とする
            overallPct = 0;
        }

        // HTMLを構築するための文字列ビルダー
        var sb = new StringBuilder();

        // HTMLヘッダーとスタイルシートを出力する
        sb.AppendLine(HtmlTemplates.IndexPageHeader);

        // レポートの見方・凡例セクションを出力する（HtmlTemplates 定数を使用して重複を避ける）
        sb.AppendLine(HtmlTemplates.IndexPageGuide);

        // 全体カバレッジ率のサマリー行を出力する（小数点以下1桁で表示）
        sb.AppendLine($"<p>全体カバレッジ: <strong>{overallPct:F1}%</strong>（実行済み {totalCovered} 行、部分実行 {totalPartial} 行 / 対象 {totalLines} 行）</p>");

        // スクリプト一覧テーブルのヘッダー行を出力する
        sb.AppendLine("""
            <table class="data">
            <tr><th>ページ URL</th><th>スクリプト</th><th class="num">実行済み</th><th class="num">部分実行</th><th class="num">対象行数</th><th class="num">カバレッジ率<br><small style="font-weight:normal;font-size:11px">※部分実行は0.5行換算</small></th></tr>
            """);

        // スクリプトごとのデータ行を出力する
        foreach (var (tabs, url, covered, partial, total, mergedFilename) in rows)
        {
            // このスクリプトのカバレッジ率を計算する（ゼロ除算を避ける）
            double pct;
            if (total > 0)
            {
                // Partial 行は 0.5 行換算 — 部分実行は完全実行より低く評価する
                pct = 100.0 * (covered + partial * 0.5) / total;
            }
            else
            {
                pct = 0;
            }

            // ページ URL セルの表示を決める
            // タブが1件: "画面N — URL" を直接表示する
            // タブが2件以上: <details>/<summary> で展開表示する
            string pageUrlCell;
            if (tabs.Count <= 1)
            {
                // 単一タブ: "画面N — URL" を直接表示する（XSS 対策のため HTML エスケープする）
                string singleDisplay;
                if (tabs.Count == 0)
                {
                    singleDisplay = "(不明)";
                }
                else
                {
                    string lbl = HtmlEncode(tabs[0].label);
                    if (string.IsNullOrEmpty(tabs[0].pageUrl))
                    {
                        singleDisplay = lbl;
                    }
                    else
                    {
                        singleDisplay = $"{lbl} — {HtmlEncode(tabs[0].pageUrl)}";
                    }
                }
                pageUrlCell = singleDisplay;
            }
            else
            {
                // 複数タブ: <details>/<summary> で展開できるようにする
                var sbDetails = new StringBuilder();
                sbDetails.Append($"<details><summary>複数ページ ({tabs.Count})</summary><ul>");
                foreach (var (label, pageUrl, tabFilename) in tabs)
                {
                    // リンクテキストは "画面N — URL" 形式（URL が空の場合は画面ラベルのみ）
                    string displayText;
                    if (string.IsNullOrEmpty(pageUrl))
                    {
                        displayText = HtmlEncode(label);
                    }
                    else
                    {
                        displayText = $"{HtmlEncode(label)} — {HtmlEncode(pageUrl)}";
                    }
                    sbDetails.Append($"<li><a href=\"scripts/{tabFilename}\">{displayText}</a></li>");
                }
                sbDetails.Append("</ul></details>");
                pageUrlCell = sbDetails.ToString();
            }

            // ページ URL・スクリプトファイル名（合成ページへのリンク付き）・各行数・カバレッジ率を出力する
            sb.AppendLine($"<tr><td>{pageUrlCell}</td>" +
                          $"<td><a href=\"scripts/{mergedFilename}\">{HtmlEncode(GetFileName(url))}</a></td>" +
                          $"<td class=\"num\">{covered}</td><td class=\"num\">{partial}</td>" +
                          $"<td class=\"num\">{total}</td><td class=\"num\">{pct:F1}%</td></tr>");
        }

        // テーブルの閉じタグを出力する
        sb.AppendLine("</table>");

        // 制約・計測対象外パターンのセクションを出力する（レポート末尾に配置）
        sb.AppendLine(HtmlTemplates.IndexPageConstraints);
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// URLのパスからファイル名部分を取得する。
    /// 例: http://localhost:3000/js/app.js    → app.js
    ///     file:///C:/work/demo/app.js        → app.js
    /// http:// / https:// / file:// で始まる URL のみパス解析を行い、それ以外はそのまま返す。
    /// これにより、XSS テスト用の偽 URL（"&lt;evil&gt;name&lt;/evil&gt;" など）も安全に扱える。
    /// </summary>
    /// <param name="url">スクリプトのURL</param>
    /// <returns>URLの最後のパスセグメント（取得できない場合はurlそのものを返す）</returns>
    internal static string GetFileName(string url)
    {
        // null は空文字として扱う（呼び出し元は CDP データ経由でガード済みだが念のため）
        if (url == null) { return ""; }
        // data: URL は巨大な Base64 文字列になりうるため表示用ラベルを返す
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) { return "(data URL)"; }

        // HTTP / HTTPS / FILE URL のみパス解析を行う。それ以外はそのまま返す。
        bool isHttp  = url.StartsWith("http://",  StringComparison.OrdinalIgnoreCase);
        bool isHttps = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        bool isFile  = url.StartsWith("file://",  StringComparison.OrdinalIgnoreCase);
        if (!isHttp && !isHttps && !isFile)
        {
            return url;
        }

        // スキーム部分の長さ（"http://" = 7, "https://" = 8, "file://" = 7）
        int schemeLength;
        if (isHttps)
        {
            schemeLength = 8;
        }
        else
        {
            schemeLength = 7;
        }

        // ホスト部の後のパス開始スラッシュを探す
        int pathStart = url.IndexOf('/', schemeLength);
        if (pathStart < 0)
        {
            // パス部分がない場合はホスト名部分を返す（認証情報・クエリ・フラグメントは除去する）
            string hostOnly = url.Substring(schemeLength);
            // user:pass@ のような認証情報が含まれる場合は @ 以降のホスト名だけを使う
            int atIdx = hostOnly.IndexOf('@');
            if (atIdx >= 0) { hostOnly = hostOnly.Substring(atIdx + 1); }
            int qIdx2 = hostOnly.IndexOf('?');
            if (qIdx2 >= 0) { hostOnly = hostOnly.Substring(0, qIdx2); }
            int hIdx2 = hostOnly.IndexOf('#');
            if (hIdx2 >= 0) { hostOnly = hostOnly.Substring(0, hIdx2); }
            return hostOnly;
        }

        // パス部分を取り出す
        string path = url.Substring(pathStart);

        // クエリ文字列（? 以降）を除去する
        int queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = path.Substring(0, queryIndex);
        }

        // フラグメント（# 以降）を除去する
        int hashIndex = path.IndexOf('#');
        if (hashIndex >= 0)
        {
            path = path.Substring(0, hashIndex);
        }

        // 末尾のスラッシュを除去する
        path = path.TrimEnd('/');

        // TrimEnd で空文字になった場合（ルートパスのみ: "/"）はホスト名を返す
        if (string.IsNullOrEmpty(path))
        {
            // schemeLength 以降からホスト名部分だけを切り出す（/, ?, # の前まで）
            string hostPortion = url.Substring(schemeLength);
            int slashIdx = hostPortion.IndexOf('/');
            if (slashIdx >= 0) { hostPortion = hostPortion.Substring(0, slashIdx); }
            int qIdx = hostPortion.IndexOf('?');
            if (qIdx >= 0) { hostPortion = hostPortion.Substring(0, qIdx); }
            int hIdx = hostPortion.IndexOf('#');
            if (hIdx >= 0) { hostPortion = hostPortion.Substring(0, hIdx); }
            // user:pass@ のような認証情報が含まれる場合は @ 以降のホスト名だけを使う
            int atIdx2 = hostPortion.IndexOf('@');
            if (atIdx2 >= 0) { hostPortion = hostPortion.Substring(atIdx2 + 1); }
            return hostPortion;
        }

        // 最後の '/' 以降をファイル名として取り出す
        int lastSlash = path.LastIndexOf('/');
        string rawName;
        if (lastSlash >= 0 && lastSlash < path.Length - 1)
        {
            rawName = path.Substring(lastSlash + 1);
        }
        else
        {
            // パスにスラッシュがない場合は path をそのまま使う
            rawName = path;
        }

        // パーセントエンコード（%20 など）をデコードして返す（不正シーケンスは例外なくそのまま返す）
        try { return Uri.UnescapeDataString(rawName); }
        catch (UriFormatException) { return rawName; }
    }
}

/// <summary>
/// HTMLレポート生成に使用するテンプレート文字列を管理する静的クラス。
/// </summary>
internal static class HtmlTemplates
{
    public const string ScriptPageHeader = """
        <!DOCTYPE html><html><head><meta charset="utf-8">
        <title>JS カバレッジ</title>
        <style>
        body{font-family:monospace;font-size:13px;margin:0;background:#fff}
        h1{padding:8px 12px;background:#2d2d2d;color:#fff;margin:0;font-size:13px;word-break:break-all}
        .legend{display:flex;flex-wrap:wrap;gap:8px 24px;padding:8px 12px;
                background:#f7f7f7;border-bottom:1px solid #ddd;
                font-size:12px;font-family:sans-serif;align-items:center}
        .legend-item{display:inline-flex;align-items:center;gap:5px;color:#444}
        .swatch{display:inline-block;width:16px;height:12px;
                border:1px solid rgba(0,0,0,.18);border-radius:2px;flex-shrink:0}
        .back-link{color:#1a7a4a;text-decoration:none;white-space:nowrap}
        .back-link:hover{text-decoration:underline}
        .source{white-space:pre}
        .line{display:flex;line-height:1.6}
        .gutter{min-width:48px;padding:0 8px;text-align:right;user-select:none;
                background:#f5f5f5;color:#aaa;border-right:2px solid #e0e0e0}
        .code{padding:0 8px;flex:1;overflow-x:auto}
        .line-covered   .gutter{background:#c6efc6;color:#3a7d3a;border-color:#8fc98f}
        .line-uncovered .gutter{background:#f0c6c6;color:#7d3a3a;border-color:#c98f8f}
        .line-partial   .gutter{background:#f0e8a0;color:#6b6000;border-color:#c9b800}
        span.covered  {background:#d4f8d4}
        span.uncovered{background:#f8d4d4}
        span.neutral  {}
        </style></head><body>
        """;

    public const string ScriptPageLegend = """
        <div class="legend">
          <a class="back-link" href="../index.html">← 一覧に戻る</a>
          <span class="legend-item"><span class="swatch" style="background:#c6efc6"></span>実行済み — 行内すべてのブロックが実行された</span>
          <span class="legend-item"><span class="swatch" style="background:#f0e8a0"></span>部分実行 — 実行済みと未実行が混在（if/else の片側など）</span>
          <span class="legend-item"><span class="swatch" style="background:#f0c6c6"></span>未実行 — 一度も実行されなかった</span>
          <span class="legend-item"><span class="swatch" style="background:#e8e8e8"></span>対象外 — コメント・空行・変数宣言のみの行など</span>
        </div>
        """;

    public const string IndexPageHeader = """
        <!DOCTYPE html><html><head><meta charset="utf-8">
        <title>JS カバレッジレポート</title>
        <style>
        body{font-family:sans-serif;padding:24px;color:#333}
        h1{font-size:20px;margin-bottom:16px}
        .guide{background:#f9f9f9;border:1px solid #e0e0e0;border-radius:6px;
               padding:16px 20px;margin-bottom:24px;font-size:14px;color:#444}
        .guide h2{font-size:15px;margin:0 0 10px;color:#222}
        .guide p{margin:4px 0 10px;line-height:1.6}
        .guide .formula{display:inline-block;background:#fff;border:1px solid #ddd;
                        border-radius:4px;padding:4px 12px;font-family:monospace;font-size:13px;color:#333}
        .legend-table{border-collapse:collapse;margin-top:4px}
        .legend-table td{padding:5px 12px 5px 0;vertical-align:middle;font-size:13px;border:none}
        .swatch{display:inline-block;width:18px;height:13px;
                border:1px solid rgba(0,0,0,.18);border-radius:2px;
                vertical-align:middle;margin-right:6px}
        table.data{border-collapse:collapse;width:100%;margin-top:16px}
        table.data th,table.data td{border:1px solid #ddd;padding:8px 12px;text-align:left}
        table.data th{background:#f5f5f5;font-weight:600}
        td.num{text-align:right;font-variant-numeric:tabular-nums}
        a{color:#1a7a4a;text-decoration:none}
        a:hover{text-decoration:underline}
        details > summary { cursor:pointer; color:#1a7a4a; list-style:none }
        details > summary::before { content:"▶ " }
        details[open] > summary::before { content:"▼ " }
        details ul { margin:4px 0 0;padding-left:16px;list-style:disc;font-size:12px }
        details ul li { margin:2px 0 }
        </style></head><body>
        <h1>JS カバレッジレポート</h1>
        """;

    public const string IndexPageGuide = """
        <div class="guide">
          <h2>レポートの見方</h2>
          <p>このレポートは、JavaScript ファイルの各行が実際に実行されたかどうかを記録したカバレッジレポートです。<br>
          スクリプト名をクリックすると、行ごとの実行状況を色分け表示で確認できます。</p>
          <p><strong>カバレッジ率の計算式</strong><br>
          <span class="formula">（実行済み行数 ＋ 部分実行行数 × 0.5）÷ 対象行数 × 100</span><br>
          ※ 対象行数にはコメント・空行・実装を持たない行など（対象外）は含みません。</p>
          <table class="legend-table">
            <tr>
              <td><span class="swatch" style="background:#c6efc6"></span><strong>実行済み</strong></td>
              <td>行内のすべてのブロックが実行された</td>
            </tr>
            <tr>
              <td><span class="swatch" style="background:#f0e8a0"></span><strong>部分実行</strong></td>
              <td>if / else など、実行された部分と未実行の部分が混在する（分岐の片側だけ通った場合など）</td>
            </tr>
            <tr>
              <td><span class="swatch" style="background:#f0c6c6"></span><strong>未実行</strong></td>
              <td>行内のコードが一度も実行されなかった</td>
            </tr>
            <tr>
              <td><span class="swatch" style="background:#e8e8e8;border-color:#ccc"></span><strong>対象外</strong></td>
              <td>コメント・空行・実装を持たない行など（カバレッジ計測の対象外）</td>
            </tr>
          </table>
        </div>
        """;

    public const string IndexPageConstraints = """
        <div class="guide">
          <h2>制約・計測対象外パターン</h2>
          <ul style="margin:4px 0 0;padding-left:20px;line-height:1.9">
            <li>
              <strong>eval() / new Function() で動的生成されるコード</strong> —
              V8 がこれらのスクリプトに URL を付与しないためスキップされます。
            </li>
            <li>
              <strong>Web Worker 内のスクリプト</strong> —
              Worker は別スレッドで動作するため、このツールの CDP セッションの対象外です。
            </li>
            <li>
              <strong>ソースマップ非対応</strong> —
              TypeScript や webpack などでバンドルされた JavaScript は、変換後のコードがそのまま計測対象になります。
              元のソースファイルへのマッピングは行いません。
            </li>
            <li>
              <strong>外側関数が実行された場合の内側未実行関数</strong> —
              V8 は外側関数の実行範囲を CDP で報告するため、その範囲内に定義された内側の未実行関数が
              「実行済み（緑）」として表示されることがあります。
            </li>
            <li>
              <strong>インラインスクリプト（&lt;script&gt; ブロック）の取得条件</strong> —
              インラインスクリプトの URL はページ URL と同じになります。
              scriptFilters が空の場合はキャプチャされます。
              scriptFilters を指定する場合は、ページ URL に含まれるキーワード（例: ページのファイル名）を追加してください。
            </li>
          </ul>
        </div>
        """;
}
