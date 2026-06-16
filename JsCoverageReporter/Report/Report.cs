#nullable disable

using System.Text;
using System.Text.Json;
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
        "else",    // else /regex/.test(x) のパターン
        "of",      // for...of ループ内: for (x of /regex/) のパターン
        "default", // export default /regex/ / switch の default ケース
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
        // 範囲を平坦化・ソートしてからコアに委譲する（functions==null は null を伝播し従来どおりスキャンしない）
        return BuildCoverageMapFromSortedRanges(source, FlattenAndSortRanges(functions));
    }

    /// <summary>
    /// 関数カバレッジデータの全範囲を1つのリストに平坦化し、「サイズ降順・同サイズは Count 昇順」で
    /// ソートして返す。BuildCoverageMap と BuildCountMap は同一の平坦化・ソートを行うため、
    /// 一度だけ計算して両者で共有することで重複する O(r log r) ソートと割り当てを省ける。
    /// functions が null の場合は null を返す（呼び出し側で従来の null セマンティクスを保つため）。
    /// </summary>
    /// <param name="functions">関数カバレッジデータのコレクション</param>
    /// <returns>ソート済みの範囲リスト（functions==null の場合は null）</returns>
    internal static List<CoverageRange> FlattenAndSortRanges(IEnumerable<FunctionCoverage> functions)
    {
        // functions==null は null を返し、コア側で「スキャンせず返す」従来動作に分岐させる
        if (functions == null) { return null; }

        // 全関数の範囲を1つのリストにまとめる
        var allRanges = new List<CoverageRange>();
        foreach (FunctionCoverage func in functions)
        {
            // func または func.Ranges が null の場合はスキップする（不正な CDP データへの防衛処理）
            if (func == null || func.Ranges == null) { continue; }
            foreach (CoverageRange range in func.Ranges)
            {
                allRanges.Add(range);
            }
        }

        // 大きい範囲が先に処理されるよう降順にソートする
        allRanges.Sort(CompareRangesForOverwrite);
        return allRanges;
    }

    /// <summary>
    /// 範囲の上書き順を決める比較関数。サイズ降順（大きい範囲が先）、同サイズは Count 昇順。
    /// 同一スパンが異なる count で複数報告された場合に「いずれかが実行済みなら実行済み」となり、
    /// List.Sort（不安定ソート）の内部実装に結果が依存しない決定的な動作になる。
    /// </summary>
    private static int CompareRangesForOverwrite(CoverageRange a, CoverageRange b)
    {
        // 範囲のサイズ（文字数）で降順比較する（大きい範囲が先に来るように b と a を逆に比較する）
        int sizeA = a.EndOffset - a.StartOffset;
        int sizeB = b.EndOffset - b.StartOffset;
        int cmp = sizeB.CompareTo(sizeA);
        if (cmp != 0) { return cmp; }
        // 同サイズは Count 昇順（実行済みの範囲が後から上書きされるようにする）
        return a.Count.CompareTo(b.Count);
    }

    /// <summary>
    /// 事前にソート済みの範囲リストからカバレッジマップを構築するコア処理。
    /// FlattenAndSortRanges の結果を BuildCoverageMap / BuildCountMap で共有するために分離している。
    /// sortedRanges が null の場合（= functions が null だった場合）は範囲適用も補正スキャンも行わず、
    /// 全文字を対象外(-1)としたマップを返す（従来の BuildCoverageMap(source, null) と同一動作）。
    /// </summary>
    /// <param name="source">スクリプトのソースコード全文</param>
    /// <param name="sortedRanges">FlattenAndSortRanges が返したソート済み範囲（null 可）</param>
    /// <returns>各文字のカバレッジ値を格納した配列</returns>
    internal static int[] BuildCoverageMapFromSortedRanges(string source, List<CoverageRange> sortedRanges)
    {
        // null ソースは空配列を返す（BuildLines と同様の防衛処理）
        if (source == null) { return []; }

        // まず全文字を「カバレッジ対象外(-1)」で初期化する
        var map = new int[source.Length];
        Array.Fill(map, -1);

        // sortedRanges==null は functions==null 相当。範囲適用も補正スキャンもせず -1 のまま返す（従来動作）
        if (sortedRanges == null) { return map; }

        // 各範囲をマップに書き込む（小さい範囲が後から上書きすることで正確な分岐情報を反映する）
        foreach (CoverageRange range in sortedRanges)
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
    /// ブロック本体 {}、またはテンプレートリテラル本体 `...` を持つ場合に
    /// =&gt; から本体末尾まで 0（未実行）にマークする。
    /// （その他の式本体は終端の判定が困難なため対象外とする）
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
        // 本体が { ブロックでもテンプレートリテラルでもない、またはカバレッジデータがある場合は
        // スキップする（テンプレート以外の式本体やカバー済みは対象外）
        if (afterArrow >= end || map[arrowStart] != -1
            || (source[afterArrow] != '{' && source[afterArrow] != '`'))
        {
            return arrowStart + 2; // => の次の位置へ進む
        }

        // 本体の終端位置（直後のインデックス）を求める
        int bodyEnd;
        if (source[afterArrow] == '{')
        {
            // ブロック本体: 対応する } の直後
            bodyEnd = FindMatchingBrace(source, afterArrow);
        }
        else
        {
            // テンプレートリテラル本体（() => `...`）: 閉じ ` の直後。
            // V8 の遅延コンパイルではこの形の未実行アロー関数もカバレッジデータに
            // 含まれないため、{ } ブロックと同様に補正対象とする。
            // 閉じ ` がない構文エラーソースの場合はソース末尾までを本体とみなす。
            bodyEnd = SkipTemplateLiteralFull(source, afterArrow);
        }
        if (bodyEnd <= afterArrow)
        {
            return arrowStart + 2; // 対応する終端が見つからない場合はスキップ
        }

        // => から本体末尾まで未実行（0）にマークする（map の長さを超えないようにクランプする）
        int arrowWriteEnd = Math.Min(bodyEnd, map.Length);
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

        // 本体の直後を返し、ScanRange が本体内を二重スキャンしないようにする
        return bodyEnd;
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

        // 次の文字が ( でなければメソッド短縮構文でない（識別子全体をスキップして identEnd を返す）
        if (afterIdent >= len || source[afterIdent] != '(')
        {
            return identEnd;
        }

        // SkipBalancedParens でパラメータ括弧をスキップする
        // SkipBalancedParens は閉じ括弧 ) の直後のインデックスを返す
        int afterParens = SkipBalancedParens(source, afterIdent);

        // パラメータの後の空白・コメントをスキップする（greet() /* c */ {} / greet() // c\n{} のケース）
        afterParens = SkipWhitespaceAndCommentsForward(source, afterParens, len);

        // 次の文字が { であり、かつカバレッジデータにない場合のみ処理する
        if (afterParens >= len || source[afterParens] != '{' || map[identStart] != -1)
        {
            return identEnd;
        }

        // 対応する } を探す（FindMatchingBrace は } の直後のインデックスを返す）
        int braceEnd = FindMatchingBrace(source, afterParens);
        if (braceEnd <= afterParens)
        {
            // 対応する } が見つからなかった場合は何もしない
            return identEnd;
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
        // 文字列リテラル終端クォート（" '）の直後も除算（例: "str" / 2、'a' / b）。
        // SkipWhitespaceAndCommentsBackward は文字列内容を遡らないため、ここに来る " ' は
        // 必ず文字列の「閉じ」クォートであり（開きクォートの直後に式末尾の / は来ない）、除算と確定できる。
        if (prev == ')' || prev == ']' || prev == '`' || prev == '"' || prev == '\'') { return false; }
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
        // 演算子・区切り文字の後は正規表現
        // （文字列終端クォート " ' の直後は上で除算と判定済みのためここには来ない）
        return true;
    }

    /// <summary>
    /// SkipTemplateLiteralFull の ${ } 深さカウントループ専用の正規表現開始判定。
    /// IsRegexStart は SkipWhitespaceAndCommentsBackward を呼ぶため、
    /// SkipTemplateLiteralFull → IsRegexStart → SkipWhitespaceAndCommentsBackward → SkipTemplateLiteralFull
    /// という相互再帰でスタックオーバーフローが起きる。
    /// このヘルパーは空白のみをスキップして前の文字またはキーワードで判定し、再帰しない。
    /// コメントをスキップしないためコメント直後の / を誤判定する場合があるが、
    /// テンプレートリテラル補間内の実用コードでは影響が極めて少ない。
    /// </summary>
    /// <param name="source">ソースコード全文</param>
    /// <param name="pos">'/' のインデックス</param>
    /// <returns>正規表現リテラルの開始なら true</returns>
    private static bool IsRegexStartInsideTemplate(string source, int pos)
    {
        // pos の直前の空白・タブ・改行をスキップする（コメントはスキップしない。スキップすると再帰が起きる）
        int i = pos - 1;
        while (i >= 0 && char.IsWhiteSpace(source[i])) { i--; }
        // ファイル先頭または式の先頭なら正規表現
        if (i < 0) { return true; }
        char p = source[i];
        // 閉じ括弧・閉じ角括弧・テンプレートリテラル閉じ の後は除算演算子（式の末尾）
        // 文字列リテラル終端クォート（" '）の直後も除算（IsRegexStart と同じ判定。例: "str" / 2）
        if (p == ')' || p == ']' || p == '`' || p == '"' || p == '\'') { return false; }
        // 後置インクリメント/デクリメント の直後の / は除算
        if (p == '+' && i > 0 && source[i - 1] == '+') { return false; }
        if (p == '-' && i > 0 && source[i - 1] == '-') { return false; }
        // 識別子文字末尾の後はキーワードかどうかで判定する
        if (IsIdentifierChar(p))
        {
            // 識別子全体を取り出す（数字で始まる場合はリテラル → 除算）
            int wordEnd = i;
            while (i >= 0 && IsIdentifierChar(source[i])) { i--; }
            string word = source.Substring(i + 1, wordEnd - i);
            // 正規表現の前に来るキーワードの後は正規表現（例: return /regex/ / typeof /regex/）
            return RegexPrecedingKeywords.Contains(word);
        }
        // 演算子・区切り文字の後は正規表現
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
                    // 正規表現リテラルをスキップする（/}/ のような正規表現内の } が深さカウントを狂わせないようにする）
                    // IsRegexStart は SkipWhitespaceAndCommentsBackward を呼ぶため
                    // SkipTemplateLiteralFull → IsRegexStart → SkipWhitespaceAndCommentsBackward → SkipTemplateLiteralFull
                    // の相互再帰によるスタックオーバーフローを防ぐため、専用の簡易判定ヘルパーを使う。
                    if (t == '/' && IsRegexStartInsideTemplate(source, i)) { i = SkipRegexLiteral(source, i); continue; }
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
                // i は * の位置。i++ で / の位置に進め、ループ末尾の i++ で / の次へ進む。
                // ここで continue しないのは意図的：continue すると / が次イテレーションで再処理されるため。
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
                // i は * の位置。i++ で / の位置に進め、ループ末尾の i++ で / の次へ進む（SkipBalancedParens と同じ意図）。
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
    /// ソースコードの各文字に対して実行回数を記録した配列を作成する。
    /// BuildCoverageMap と同じ「大きい範囲 → 小さい範囲」の上書き順で、
    /// 各文字に最も具体的な（最小の）範囲の実行回数を書き込む。
    /// 値はカバレッジ対象外も含めて 0 で初期化される（回数の表示にのみ使い、対象判定には使わない）。
    /// </summary>
    /// <param name="source">スクリプトのソースコード全文</param>
    /// <param name="functions">関数カバレッジデータのコレクション</param>
    /// <returns>各文字の実行回数を格納した配列（インデックス = 文字位置）</returns>
    internal static int[] BuildCountMap(string source, IEnumerable<FunctionCoverage> functions)
    {
        if (source == null) { return []; }
        // 範囲を平坦化・ソートしてからコアに委譲する（BuildCoverageMap と同じ平坦化・ソートを共有可能）
        return BuildCountMapFromSortedRanges(source, FlattenAndSortRanges(functions));
    }

    /// <summary>
    /// 事前にソート済みの範囲リストから実行回数マップを構築するコア処理。
    /// FlattenAndSortRanges の結果を BuildCoverageMap と共有して重複ソートを省くために分離している。
    /// sortedRanges が null（= functions が null だった場合）は全文字 0 のマップを返す（従来動作と同一）。
    /// </summary>
    /// <param name="source">スクリプトのソースコード全文</param>
    /// <param name="sortedRanges">FlattenAndSortRanges が返したソート済み範囲（null 可）</param>
    /// <returns>各文字の実行回数を格納した配列</returns>
    internal static int[] BuildCountMapFromSortedRanges(string source, List<CoverageRange> sortedRanges)
    {
        if (source == null) { return []; }
        var counts = new int[source.Length];
        // sortedRanges==null は functions==null 相当。全文字 0 のまま返す（従来動作）
        if (sortedRanges == null) { return counts; }

        foreach (CoverageRange range in sortedRanges)
        {
            // 負の実行回数（不正データ）は 0 に丸める
            int val = range.Count;
            if (val < 0) { val = 0; }
            int start = Math.Max(range.StartOffset, 0);
            int end = Math.Min(range.EndOffset, source.Length);
            for (int i = start; i < end; i++)
            {
                counts[i] = val;
            }
        }
        return counts;
    }

    /// <summary>
    /// 2つの実行回数マップを文字ごとの最大値で合成して返す。
    /// baseMap の長さを基準とし、otherMap が短い場合は 0 として扱う。
    /// （複数タブ・複数ナビゲーションの実行回数を「最も多く実行された値」で代表させる）
    /// </summary>
    /// <param name="baseMap">基準となる実行回数マップ</param>
    /// <param name="otherMap">合成する実行回数マップ</param>
    /// <returns>文字ごとの最大値で合成した実行回数マップ（baseMap と同じ長さ）</returns>
    internal static int[] MergeCountMaps(int[] baseMap, int[] otherMap)
    {
        if (baseMap == null)
        {
            throw new ArgumentNullException(nameof(baseMap));
        }
        if (otherMap == null)
        {
            throw new ArgumentNullException(nameof(otherMap));
        }
        var merged = new int[baseMap.Length];
        for (int i = 0; i < baseMap.Length; i++)
        {
            int v1 = baseMap[i];
            int v2;
            if (i < otherMap.Length) { v2 = otherMap[i]; } else { v2 = 0; }
            if (v1 >= v2) { merged[i] = v1; } else { merged[i] = v2; }
        }
        return merged;
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
                // ミニファイ済みファイルのような超長行での O(N²) を防ぐためスキャン範囲を
                // 最大 4096 文字に制限する。実用上の行コメント (//) はこの範囲内に収まる
                // （ミニファイ済みコードに // 行コメントは存在しないため実質影響なし）
                if (pos - lp > 4096) { lp = pos - 4096; }
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
                    // ブロックコメント終端 */ をスキップする。
                    // 前行から続く /* */ が現在行の先頭付近で閉じる場合、
                    // この * をスキップしないと直後の / を IsRegexStart が正規表現開始と誤判定し、
                    // 後続の // 行コメントを検出できなくなる。
                    if (kc == '*' && k + 1 < source.Length && source[k + 1] == '/')
                    {
                        k += 2; // */ の2文字をスキップする
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
                    // IsRegexStart は SkipWhitespaceAndCommentsBackward を呼ぶため相互再帰になる。
                    // IsRegexStartInsideTemplate はコメントをスキップしない非再帰版なので O(N) で完了する。
                    if (kc == '/' && IsRegexStartInsideTemplate(source, k))
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
/// <param name="MaxCount">行内の実行済み文字の最大実行回数（0 = 回数情報なし。ツールチップ表示に使う）</param>
internal record LineData(string Html, LineCoverageStatus Status, int MaxCount = 0);

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
        // null / 空文字は空文字を返す（#nullable disable 環境の防衛処理）
        if (string.IsNullOrEmpty(text)) { return ""; }

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
                // その他の文字（' を含む）はそのまま追加する。
                // 本ツールが生成する HTML の属性値はすべてダブルクォート (class="..."、href="..." など) で
                // 囲んでおり、シングルクォート属性は使わない。よって ' をエスケープする必要はなく、
                // テキストノードでの ' エスケープも不要（BuildLines も ' を素通しする）。両者の挙動を一致させる。
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// ファイルを書き出す。I/O エラー（書き込み権限なし・ディスク不足・パス不正など）が起きても
    /// 例外を伝播させず警告ログに変換する。1ファイルの書き込み失敗で他のページ生成を巻き添えにせず、
    /// 生成できたぶんのレポートを残すためのフォールバック。
    /// </summary>
    private static void WriteFileSafe(string path, string content, Encoding encoding)
    {
        try
        {
            File.WriteAllText(path, content, encoding);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[Warning] Failed to write '{path}': {ex.Message}");
        }
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

    internal static List<LineData> BuildLines(string source, int[] map, int[] countMap = null)
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
            // 行内の実行済み文字の最大実行回数（ツールチップ表示用。countMap 未指定なら 0 のまま）
            int lineMaxCount = 0;

            // 現在開いている <span> のカバレッジ状態（-2 = まだ <span> を開いていない）
            int currentState = -2;

            // 行内の各文字を順番に処理する
            for (int i = 0; i < rawLine.Length; i++)
            {
                // \r（CRLF 改行の CR 部分）はカバレッジカウントに含めない。
                // ただし offset は rawLine.Length + 1 でまとめて加算されるため \r 分も正しく進む。
                // \0（ヌル文字）は理論上 JS ソースに含まれる可能性があるが、
                // HTML 出力時にブラウザが無視するため表示上の問題がなく、カバレッジカウントに含めない。
                char chSkip = rawLine[i];
                if (chSkip == '\r' || chSkip == '\0')
                {
                    // カバレッジカウントと HTML 出力をスキップするが offset はループ末尾で加算済み
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
                    // 実行済み文字の最大実行回数を更新する（実行された部分の回数だけを対象にする）
                    if (countMap != null && idx < countMap.Length && countMap[idx] > lineMaxCount)
                    {
                        lineMaxCount = countMap[idx];
                    }
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

            // 行のHTMLと状態を結果リストに追加する（最大実行回数はツールチップ表示に使う）
            result.Add(new LineData(sb.ToString(), status, lineMaxCount));

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
    /// <param name="sourceMaps">スクリプト URL → 解析済みソースマップの辞書（null なら ソースマップ処理なし）</param>
    /// <param name="writeLcov">true なら lcov.info（LCOV 形式）も出力する</param>
    /// <param name="writeJson">true なら coverage.json（機械可読サマリー）も出力する</param>
    /// <param name="targetUrl">計測対象ページの URL（インデックスのメタ情報に表示する。null なら省略）</param>
    // 従来シグネチャ（後方互換）。引数を ReportOptions に詰めて単一エントリへ委譲する。
    internal void Generate(
        IReadOnlyList<ScriptCoverage> coverages,
        string outputDir,
        IReadOnlyDictionary<string, SourceMap> sourceMaps = null,
        bool writeLcov = false,
        bool writeJson = false,
        string targetUrl = null)
    {
        Generate(coverages, sourceMaps,
            new ReportOptions(outputDir, writeLcov, writeJson, targetUrl));
    }

    /// <summary>
    /// 1グループ分の「I/O・連番非依存」な計算結果。並列フェーズで生成し、
    /// 集約フェーズ（順次）でファイル名割当・HTML 組み立て・書き込みに使う。
    /// Skipped が true のグループはレポート対象外（連番 i を消費しない）。
    /// </summary>
    private sealed class GroupComputation
    {
        public bool Skipped;
        public string ScriptUrl;
        public string CanonicalSource;
        public int[][] MemberCovMaps;
        public int[][] MemberCountMaps;
        public int[] MergedMap;
        public int[] MergedCountMap;
        public List<LineData> MergedLines;
        public IReadOnlyList<(string Name, int Line)> MergedUncalled;
        public SourceMap SrcMap;
        public List<ScriptCoverage> Members;  // 元グループ（集約フェーズで urlGroups 等を再構築する）
    }

    // 1グループ分の重い計算（文字スキャン）を行う。I/O も連番 i も触らない純粋計算。
    private GroupComputation ComputeGroup(List<ScriptCoverage> group,
        IReadOnlyDictionary<string, SourceMap> sourceMaps)
    {
        var comp = new GroupComputation { Members = group };
        if (group.Count == 0) { comp.Skipped = true; return comp; }

        string scriptUrl = group[0].Url;

        // カノニカル（基準）スクリプトは最初のエントリとする
        var canonical = group[0];

        // BOM（U+FEFF）をソースから除去する
        // V8 は BOM を JavaScript の文字としてカウントしないため、CDP のオフセット値は
        // BOM 除去後の位置を指している。BOM を除去せずに渡すとオフセットが1ずれて
        // 末尾の文字がカバレッジ対象外（neutral）になるバグが発生する。
        // null の場合は空文字に正規化する（null 合体演算子を使わず if/else で明示的に分岐する）
        string canonicalSourceRaw;
        if (canonical.Source == null)
        {
            canonicalSourceRaw = "";
        }
        else
        {
            canonicalSourceRaw = canonical.Source;
        }
        string canonicalSource = canonicalSourceRaw.TrimStart('﻿');

        // メンバーごとのカバレッジマップ・実行回数マップを1回だけ計算してキャッシュする。
        // BuildCoverageMap / BuildCountMap は (source, functions) の決定的な純関数であり、
        // グループ内全メンバーのソースは canonicalSource と完全一致する（グループキーにソースを含む）。
        // 後段の URL 別ページ生成で同じメンバーを再計算していた重複を、この配列の再利用で排除する。
        // 各マップは MergeMaps/MergeCountMaps（新配列を返し入力を変更しない）と BuildLines（読み取りのみ）
        // からのみ参照されるため共有しても安全で、結果は従来とビット単位で一致する。
        var memberCovMaps   = new int[group.Count][];
        var memberCountMaps = new int[group.Count][];
        for (int g = 0; g < group.Count; g++)
        {
            // 範囲の平坦化・ソートはカバレッジマップと実行回数マップで共通のため1回だけ行い、
            // 両コアで共有する（重複する O(r log r) ソートと割り当てを省く）。
            // ソート済みリストは両コアで読み取りのみ参照するため共有しても安全。
            var sortedRanges   = CoverageParser.FlattenAndSortRanges(group[g].Functions);
            memberCovMaps[g]   = CoverageParser.BuildCoverageMapFromSortedRanges(canonicalSource, sortedRanges);
            memberCountMaps[g] = CoverageParser.BuildCountMapFromSortedRanges(canonicalSource, sortedRanges);
        }

        // 全タブ分のカバレッジマップを OR 合成する
        // 実行回数マップ（行番号ガターのツールチップ表示用）も並行して構築し、文字ごとの最大値で合成する
        var mergedMap      = memberCovMaps[0];
        var mergedCountMap = memberCountMaps[0];
        for (int g = 1; g < group.Count; g++)
        {
            mergedMap      = CoverageParser.MergeMaps(mergedMap, memberCovMaps[g]);
            mergedCountMap = CoverageParser.MergeCountMaps(mergedCountMap, memberCountMaps[g]);
        }

        // OR 合成したマップから行データを生成する（BOM 除去済みのソースを使う）
        var mergedLines = BuildLines(canonicalSource, mergedMap, mergedCountMap);

        // このスクリプトのソースマップを取得する（あれば）
        SourceMap srcMap = null;
        if (sourceMaps != null) { sourceMaps.TryGetValue(scriptUrl, out srcMap); }

        // 1行しかないスクリプトはレポート対象外としてスキップする
        // （インライン eval や最小化された1行スクリプトなど、有意な情報が得られないため）
        // ただしソースマップがある場合は元ファイル別の表示ができるためスキップしない
        // （ミニファイされた本番バンドルは1行になることが多く、本機能の主用途のため）
        // i はインクリメントしない → スキップしてもファイル番号に欠番が生じない
        if (mergedLines.Count <= 1 && srcMap == null)
        {
            // スキップ理由をユーザーが把握できるよう警告を出す
            Console.Error.WriteLine($"[Warning] Skipping 1-line script (no coverage info): {scriptUrl}");
            comp.Skipped = true;
            return comp;
        }

        comp.ScriptUrl       = scriptUrl;
        comp.CanonicalSource = canonicalSource;
        comp.MemberCovMaps   = memberCovMaps;
        comp.MemberCountMaps = memberCountMaps;
        comp.MergedMap       = mergedMap;
        comp.MergedCountMap  = mergedCountMap;
        comp.MergedLines     = mergedLines;
        // グループ全体で一度も実行されなかった関数の一覧（詳細ページの先頭に表示する）
        comp.MergedUncalled  = CollectUncalledFunctions(group, canonicalSource);
        comp.SrcMap          = srcMap;
        return comp;
    }

    // 単一の公開エントリ。全挙動は options で制御する。
    internal void Generate(
        IReadOnlyList<ScriptCoverage> coverages,
        IReadOnlyDictionary<string, SourceMap> sourceMaps,
        ReportOptions options)
    {
        string outputDir = options.OutputDir;
        bool   writeLcov = options.WriteLcov;
        bool   writeJson = options.WriteJson;
        string targetUrl = options.TargetUrl;

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
            // Source は通常 Coverage.cs 側で空を除外済みだが、
            // internal メソッドの直接呼び出し（テスト等）に備えて null を空文字に正規化して防御する。
            // 三項演算子・null 合体演算子を使わず if/else で明示的に分岐する。
            string scriptSource;
            if (script.Source == null)
            {
                scriptSource = "";
            }
            else
            {
                scriptSource = script.Source;
            }
            // グループキーは (URL, BOM 除去済みソース) のタプル。先頭 BOM を除いて BOM 有無の差を吸収する。
            var key = (script.Url, scriptSource.TrimStart('\uFEFF'));
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
        // pages: ページ URL とその詳細ページのファイル名のリスト / screenCount: このスクリプトを表示した画面数
        var summaryRows = new List<(
            IReadOnlyList<(string pageUrl, string tabFilename)> pages,
            string url, int screenCount, int covered, int partial, int total, string mergedFilename)>();

        // ソースマップで解決した元ファイル行のリスト（合成ページのファイル名 → 元ファイル別サマリー）
        var srcRowsByScript = new Dictionary<string, List<(string path, int covered, int partial, int total, string srcFilename)>>();

        // LCOV / JSON エクスポート用のスクリプトデータ（--lcov / --json 指定時にファイル出力する）
        var exportScripts = new List<ExportScriptData>();

        int i = 0;
        foreach (var group in scriptGroups)
        {
            // 1グループ分の「I/O・連番非依存」な重い計算（文字スキャン）を行う。
            // この段階では並列化せず順次呼び出す（出力はバイト一致のまま）。
            var comp = ComputeGroup(group, sourceMaps);

            // スキップ対象（空グループ or 1行スクリプト）は連番 i を消費せず次へ。
            // 1行スクリプトの警告出力は ComputeGroup 内で行う（従来挙動と同一）。
            if (comp.Skipped)
            {
                continue;
            }

            // 計算結果からループ本体が使うローカル変数を復元する（以降の本体は無改変で再利用する）。
            string scriptUrl      = comp.ScriptUrl;
            string canonicalSource = comp.CanonicalSource;
            var memberCovMaps     = comp.MemberCovMaps;
            var memberCountMaps   = comp.MemberCountMaps;
            var mergedMap         = comp.MergedMap;
            var mergedCountMap    = comp.MergedCountMap;
            var mergedLines       = comp.MergedLines;
            var mergedUncalled    = comp.MergedUncalled;
            SourceMap srcMap      = comp.SrcMap;

            // 合成ページのファイル名（全タブの OR 合成カバレッジを表示する）
            var mergedFilename = $"script-{i}.html";

            // ページ URL ごとにエントリをまとめる（同じ URL を複数の画面で開いた場合は1つに集約する）
            // 挿入順を保つため List と逆引き辞書で管理する。
            // Indices は group 内のメンバー添字を保持し、URL 別ページ生成で memberCovMaps を再利用する。
            var urlGroups = new List<(string Url, List<ScriptCoverage> Scripts, List<int> Indices)>();
            var urlGroupIndex = new Dictionary<string, int>();
            // このスクリプトを表示した画面（タブ）の数（重複なし。インデックスの「表示画面数」列に使う）
            var distinctScreens = new HashSet<int>();
            for (int gi = 0; gi < group.Count; gi++)
            {
                var s = group[gi];
                distinctScreens.Add(s.Page.Index);
                string pageUrl = s.Page.Url;
                if (pageUrl == null) { pageUrl = ""; }
                int urlIdx;
                if (!urlGroupIndex.TryGetValue(pageUrl, out urlIdx))
                {
                    urlIdx = urlGroups.Count;
                    urlGroupIndex[pageUrl] = urlIdx;
                    urlGroups.Add((pageUrl, new List<ScriptCoverage>(), new List<int>()));
                }
                urlGroups[urlIdx].Scripts.Add(s);
                urlGroups[urlIdx].Indices.Add(gi);
            }
            int screenCount = distinctScreens.Count;

            // 合成ページの見出しに表示するページ URL のリスト（空 URL は除外・重複なし）
            var pageUrls = new List<string>();
            foreach (var (groupUrl, _, _) in urlGroups)
            {
                if (!string.IsNullOrEmpty(groupUrl)) { pageUrls.Add(groupUrl); }
            }

            // エクスポート用の画面情報（タブ番号は1始まり。URL が空のタブも含めて重複なしで記録する）
            var exportPages = new List<(int Tab, string PageUrl)>();
            var seenExportPages = new HashSet<(int, string)>();
            foreach (var s in group)
            {
                var pageKey = (s.Page.Index + 1, s.Page.Url);
                if (seenExportPages.Add(pageKey))
                {
                    exportPages.Add(pageKey);
                }
            }

            // 合成カバレッジの詳細ページを生成する
            WriteFileSafe(
                Path.Combine(scriptsDir, mergedFilename),
                BuildScriptPage(pageUrls, scriptUrl, mergedLines, mergedUncalled),
                Encoding.UTF8);

            // ページ URL 別の詳細ページリストを構築する（インデックスの展開 UI 用）
            var pages = new List<(string pageUrl, string tabFilename)>();
            if (urlGroups.Count > 1)
            {
                // 複数のページ URL から読み込まれた場合: URL ごとに OR 合成した詳細ページを生成する
                // （同じ URL を複数の画面で開いた場合は1ページに集約される）
                for (int g = 0; g < urlGroups.Count; g++)
                {
                    var (groupPageUrl, urlScripts, urlIndices) = urlGroups[g];
                    // URL 別ページのファイル名（URL グループのインデックス g を使用して衝突を防ぐ）
                    var tabFilename = $"script-{i}-tab{g}.html";

                    // この URL で読み込まれた全エントリ（複数画面・複数ナビゲーション分）を OR 合成する。
                    // メンバーマップは合成ループで計算済みのため memberCovMaps/memberCountMaps を再利用し、
                    // BuildCoverageMap/BuildCountMap の再計算を省く（結果は従来と完全に一致する）。
                    var urlMap      = memberCovMaps[urlIndices[0]];
                    var urlCountMap = memberCountMaps[urlIndices[0]];
                    for (int k = 1; k < urlIndices.Count; k++)
                    {
                        urlMap      = CoverageParser.MergeMaps(urlMap, memberCovMaps[urlIndices[k]]);
                        urlCountMap = CoverageParser.MergeCountMaps(urlCountMap, memberCountMaps[urlIndices[k]]);
                    }
                    var urlLines = BuildLines(canonicalSource, urlMap, urlCountMap);

                    // この URL のエントリだけで未実行だった関数の一覧
                    var urlUncalled = CollectUncalledFunctions(urlScripts, canonicalSource);
                    // URL 別ページから全ページ合成ページへ戻れるようにナビゲーションリンクを付ける
                    var urlNavLinks = new List<(string Text, string Href)> { ("全ページ合成のカバレッジを見る", mergedFilename) };
                    var urlPageUrls = new List<string>();
                    if (!string.IsNullOrEmpty(groupPageUrl)) { urlPageUrls.Add(groupPageUrl); }
                    WriteFileSafe(
                        Path.Combine(scriptsDir, tabFilename),
                        BuildScriptPage(urlPageUrls, scriptUrl, urlLines, urlUncalled, urlNavLinks),
                        Encoding.UTF8);

                    pages.Add((groupPageUrl, tabFilename));
                }
            }
            else
            {
                // ページ URL が1種類（単一画面、または全画面が同じ URL）の場合:
                // URL 別ページは合成ページと同内容になるため別ファイルは生成しない
                string singleUrl = "";
                if (urlGroups.Count > 0) { singleUrl = urlGroups[0].Url; }
                pages.Add((singleUrl, mergedFilename));
            }

            // エクスポート用の元ファイル別データ（ソースマップ解決時に下のブロックで蓄積する）
            var exportSourceFiles = new List<ExportSourceFileData>();

            // ソースマップがある場合: 元ファイル別の行カバレッジを集計し、詳細ページを生成する
            if (srcMap != null)
            {
                // OR 合成済みマップを元ファイルの行単位に射影する
                var projected = SourceMapProjector.Project(canonicalSource, mergedMap, srcMap);

                // sources 配列の順序はバンドラー依存のため、パスの辞書順で安定して表示する
                var srcIndices = new List<int>(projected.Keys);
                srcIndices.Sort((a, b) => string.CompareOrdinal(srcMap.Sources[a], srcMap.Sources[b]));

                var srcFileRows = new List<(string path, int covered, int partial, int total, string srcFilename)>();
                foreach (int srcIndex in srcIndices)
                {
                    var lineFlags = projected[srcIndex];

                    // 行ステータスを集計する（covered/partial/uncovered すべてが「対象行」になる）
                    int srcCovered = 0;
                    int srcPartial = 0;
                    int srcTotal   = 0;
                    foreach (int flags in lineFlags.Values)
                    {
                        if (flags == (SourceMapProjector.CoveredFlag | SourceMapProjector.UncoveredFlag))
                        {
                            srcPartial++;
                        }
                        else if (flags == SourceMapProjector.CoveredFlag)
                        {
                            srcCovered++;
                        }
                        srcTotal++;
                    }

                    string srcPath = srcMap.Sources[srcIndex];

                    // sourcesContent があれば色付きの元ソース詳細ページを生成する
                    // （ない場合は集計のみインデックスに表示し、リンクなしにする）
                    string srcContent  = srcMap.SourcesContent[srcIndex];
                    string srcFilename = null;
                    if (!string.IsNullOrEmpty(srcContent))
                    {
                        srcFilename = $"script-{i}-src-{srcIndex}.html";
                        var srcLines = BuildOriginalSourceLines(srcContent, lineFlags);
                        // 見出しにはバンドル（生成コード）の URL を表示する
                        var srcPageUrls = new List<string> { scriptUrl };
                        // 元ファイルページからバンドル（生成コード）のページへ移動できるようにする
                        var srcNavLinks = new List<(string Text, string Href)> { ("バンドル（生成コード）のカバレッジを見る", mergedFilename) };
                        WriteFileSafe(
                            Path.Combine(scriptsDir, srcFilename),
                            BuildScriptPage(srcPageUrls, srcPath, srcLines, null, srcNavLinks),
                            Encoding.UTF8);
                    }

                    srcFileRows.Add((srcPath, srcCovered, srcPartial, srcTotal, srcFilename));

                    // エクスポート用に行フラグの詳細を保持する（LCOV の DA レコード生成に使う）
                    exportSourceFiles.Add(new ExportSourceFileData(srcPath, lineFlags));
                }

                if (srcFileRows.Count > 0)
                {
                    srcRowsByScript[mergedFilename] = srcFileRows;
                }
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

            summaryRows.Add((pages, scriptUrl, screenCount, covered, partial, total, mergedFilename));

            // エクスポート用データを蓄積する（HTML に出力したスクリプトと同じ範囲をエクスポートする）
            var lineStatuses = new List<LineCoverageStatus>(mergedLines.Count);
            foreach (var line in mergedLines)
            {
                lineStatuses.Add(line.Status);
            }
            exportScripts.Add(new ExportScriptData(scriptUrl, exportPages, lineStatuses, exportSourceFiles));

            i++;
        }

        // インデックスページを生成してファイルに書き出す（メタ情報として対象 URL と生成日時を渡す）
        WriteFileSafe(
            Path.Combine(outputDir, "index.html"),
            BuildIndexPage(summaryRows, srcRowsByScript, targetUrl, DateTimeOffset.Now),
            Encoding.UTF8);

        // LCOV 形式（lcov.info）を出力する（--lcov 指定時）
        // BOM があると lcov 系ツールが先頭の SF レコードを読めないため BOM なし UTF-8 で書き出す
        if (writeLcov)
        {
            WriteFileSafe(
                Path.Combine(outputDir, "lcov.info"),
                CoverageExporter.BuildLcov(exportScripts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        // JSON 形式（coverage.json）を出力する（--json 指定時）
        if (writeJson)
        {
            WriteFileSafe(
                Path.Combine(outputDir, "coverage.json"),
                CoverageExporter.BuildJson(exportScripts, DateTimeOffset.Now),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    /// <summary>
    /// 元ファイル（ソースマップで解決した TypeScript 等）のソースを行単位カバレッジで色付けした行データを生成する。
    /// ソースマップの精度は行単位のため、BuildLines と違い行全体を1つの span で包む。
    /// 部分実行（実行済みと未実行が混在）の行はテキストを中立色にし、行番号の色（黄）で示す。
    /// </summary>
    /// <param name="content">元ファイルのソース全文（sourcesContent の値）</param>
    /// <param name="lineFlags">行番号（0始まり）→ SourceMapProjector のフラグ の辞書</param>
    /// <returns>行ごとの LineData オブジェクトのリスト</returns>
    internal static List<LineData> BuildOriginalSourceLines(string content, IReadOnlyDictionary<int, int> lineFlags)
    {
        var result = new List<LineData>();
        if (string.IsNullOrEmpty(content)) { return result; }

        // BuildLines と同じ規則で行分割する（\n、\r\n、\r に対応）
        var rawLines = SplitOnNewlines(content);
        int lineCount = rawLines.Length;
        // ソースが改行で終わる場合の末尾空要素を除く（BuildLines と同じ扱い）
        if (lineCount > 0 && (content.EndsWith('\n') || content.EndsWith('\r')))
        {
            lineCount--;
        }

        for (int li = 0; li < lineCount; li++)
        {
            // CRLF の \r が行末に残っている場合は表示から除く
            string text = rawLines[li].TrimEnd('\r');

            int flags = 0;
            if (lineFlags != null) { lineFlags.TryGetValue(li, out flags); }

            // フラグから行ステータスとテキストの CSS クラスを決める
            LineCoverageStatus status;
            string cls;
            if (flags == (SourceMapProjector.CoveredFlag | SourceMapProjector.UncoveredFlag))
            {
                // 部分実行: テキストは中立色（どの部分が実行されたかは行単位では分からないため）
                status = LineCoverageStatus.Partial;
                cls    = "neutral";
            }
            else if (flags == SourceMapProjector.CoveredFlag)
            {
                status = LineCoverageStatus.Covered;
                cls    = "covered";
            }
            else if (flags == SourceMapProjector.UncoveredFlag)
            {
                status = LineCoverageStatus.Uncovered;
                cls    = "uncovered";
            }
            else
            {
                // カバレッジ情報なし（コメント・空行・マッピング外の行）
                status = LineCoverageStatus.Neutral;
                cls    = "neutral";
            }

            string html = $"<span class=\"{cls}\">{HtmlEncode(text)}</span>";
            result.Add(new LineData(html, status));
        }
        return result;
    }

    /// <summary>
    /// スクリプト詳細ページ（行ごとに色付けされたソースコード表示）のHTMLを生成する。
    /// </summary>
    /// <param name="pageUrls">このスクリプトが読み込まれたページ URL のリスト（重複なし）</param>
    /// <param name="scriptUrl">スクリプトのURL（ページタイトルと見出しに使用）</param>
    /// <param name="lines">BuildLines が返した行データのリスト</param>
    /// <param name="uncalledFunctions">一度も実行されなかった関数の一覧（名前と1始まり行番号）。null または空なら一覧を表示しない</param>
    /// <param name="navLinks">関連ページへのナビゲーションリンク（例: URL 別ページから合成ページへ）。null または空なら表示しない</param>
    /// <returns>スクリプト詳細ページの完全なHTML文字列</returns>
    internal static string BuildScriptPage(
        IReadOnlyList<string> pageUrls,
        string scriptUrl,
        List<LineData> lines,
        IReadOnlyList<(string Name, int Line)> uncalledFunctions = null,
        IReadOnlyList<(string Text, string Href)> navLinks = null)
    {
        // HTMLを構築するための文字列ビルダー
        var sb = new StringBuilder();

        // ブラウザのタブ・履歴で区別できるよう、ページタイトルにスクリプト名を入れる
        // GetFileName は内部で文字列操作・Uri.UnescapeDataString を行うため、同一 scriptUrl に対しては
        // 1回だけ計算してローカルに保持し、見出し・URL 比較で再利用する（重複呼び出しを避ける）
        string titleName = GetFileName(scriptUrl);
        string pageTitle;
        if (string.IsNullOrEmpty(titleName))
        {
            pageTitle = "JS カバレッジ";
        }
        else
        {
            pageTitle = $"{HtmlEncode(titleName)} — JS カバレッジ";
        }

        // HTMLヘッダーとスタイルシートを出力する（__TITLE__ をスクリプト名入りタイトルに置換する）
        sb.AppendLine(HtmlTemplates.ScriptPageHeader.Replace("__TITLE__", pageTitle));

        // ページ URL の表示文字列を決める（URL のみ。複数の場合はカンマ区切り）
        string pageDisplay;
        if (pageUrls == null || pageUrls.Count == 0)
        {
            // ページ情報が取得できなかった場合のフォールバック表示
            pageDisplay = "(不明)";
        }
        else
        {
            var parts = new List<string>();
            foreach (var u in pageUrls)
            {
                if (string.IsNullOrEmpty(u))
                {
                    // URL が取得できなかったページ（about:blank やクローズ直前など）
                    parts.Add("(URL なし)");
                }
                else
                {
                    parts.Add(HtmlEncode(u));
                }
            }
            pageDisplay = string.Join(", ", parts);
        }
        // 画面情報とスクリプトファイル名をページ見出しとして出力する
        sb.AppendLine($"<h1>{pageDisplay} / {HtmlEncode(titleName)}</h1>");

        // スクリプトの完全な URL を表示する（同名ファイルが複数あってもどのスクリプトか識別できるようにする）
        // ファイル名と完全 URL が同じ場合（GetFileName がそのまま返すケース）は重複表示を避ける
        if (!string.IsNullOrEmpty(scriptUrl) && scriptUrl != titleName)
        {
            sb.AppendLine($"<div class=\"script-url\">{HtmlEncode(scriptUrl)}</div>");
        }

        // 関連ページへのナビゲーションリンクを出力する（タブ別ページ → 合成ページ など）
        if (navLinks != null && navLinks.Count > 0)
        {
            var navParts = new List<string>();
            foreach (var (text, href) in navLinks)
            {
                navParts.Add($"<a href=\"{HtmlEncode(href)}\">{HtmlEncode(text)}</a>");
            }
            sb.AppendLine($"<div class=\"subnav\">{string.Join(" ｜ ", navParts)}</div>");
        }

        // 各色の意味を説明する凡例バーを出力する
        sb.AppendLine(HtmlTemplates.ScriptPageLegend);

        // 一度も実行されなかった関数の一覧を出力する（1件以上ある場合のみ）
        // 行番号リンクをクリックすると該当行（id="L行番号"）にジャンプできる
        if (uncalledFunctions != null && uncalledFunctions.Count > 0)
        {
            sb.AppendLine($"<details class=\"uncalled\"><summary>未実行関数 ({uncalledFunctions.Count})</summary><ul>");
            foreach (var (funcName, funcLine) in uncalledFunctions)
            {
                sb.AppendLine($"<li><a href=\"#L{funcLine}\">{funcLine}行目</a> {HtmlEncode(funcName)}</li>");
            }
            sb.AppendLine("</ul></details>");
        }

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

            // 行番号ガターのツールチップ（実行回数が分かる行のみ title 属性を付ける）
            string gutterTitle = "";
            if (line.MaxCount > 0)
            {
                // 実行回数が int の範囲（約21億）を超えた場合、Coverage 側で int.MaxValue にフォールバックしている。
                // 生の 2147483647 をそのまま出すと実際の回数と誤解されるため「21億回以上」と表示する。
                // 三項演算子を使わず if/else で実行回数の表示文字列を決める
                string countText;
                if (line.MaxCount == int.MaxValue)
                {
                    // int の範囲（約21億）を超えた回数は Coverage 側で int.MaxValue に丸めているため、固定文言で示す
                    countText = "21億回以上";
                }
                else
                {
                    countText = line.MaxCount.ToString();
                }
                gutterTitle = $" title=\"実行回数: {countText}\"";
            }

            // 行番号（i+1, 1始まり）と行のHTMLを div タグで囲んで出力する
            // id="L行番号" は未実行関数一覧からのジャンプ先アンカーとして使う
            sb.AppendLine($"<div class=\"{cls}\" id=\"L{i + 1}\"><span class=\"gutter\"{gutterTitle}>{i + 1}</span><span class=\"code\">{line.Html}</span></div>");
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
    /// <param name="srcRowsByScript">合成ページのファイル名 → ソースマップで解決した元ファイル別サマリーのリスト（null 可）</param>
    /// <param name="targetUrl">計測対象ページの URL（シナリオの url。null なら表示しない）</param>
    /// <param name="generatedAt">レポート生成日時（null なら表示しない）</param>
    /// <returns>インデックスページの完全なHTML文字列</returns>
    internal static string BuildIndexPage(
        List<(IReadOnlyList<(string pageUrl, string tabFilename)> pages,
              string url, int screenCount, int covered, int partial, int total, string mergedFilename)> rows,
        IReadOnlyDictionary<string, List<(string path, int covered, int partial, int total, string srcFilename)>> srcRowsByScript = null,
        string targetUrl = null,
        DateTimeOffset? generatedAt = null)
    {
        // 全スクリプトの実行済み行数の合計
        int totalCovered = 0;
        // 全スクリプトの部分実行行数の合計
        int totalPartial = 0;
        // 全スクリプトのカバレッジ対象行数の合計
        int totalLines = 0;

        // 各スクリプトの行数を合計する（タプル要素名が変わるため分割代入を使う）
        foreach (var (_, _, _, covered, partial, total, _) in rows)
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

        // 実行メタ情報（生成日時・対象 URL・ツール名）を出力する
        // 後日レポートを見返したときに「いつ・何に対する計測か」が分かるようにする
        var metaParts = new List<string>();
        if (generatedAt != null)
        {
            metaParts.Add($"生成日時: {generatedAt.Value:yyyy-MM-dd HH:mm:ss}");
        }
        if (!string.IsNullOrEmpty(targetUrl))
        {
            metaParts.Add($"対象 URL: {HtmlEncode(targetUrl)}");
        }
        if (metaParts.Count > 0)
        {
            metaParts.Add("JsCoverageReporter");
            sb.AppendLine($"<p class=\"meta\">{string.Join(" ｜ ", metaParts)}</p>");
        }

        // レポートの見方・凡例セクションを出力する（HtmlTemplates 定数を使用して重複を避ける）
        sb.AppendLine(HtmlTemplates.IndexPageGuide);

        // 全体カバレッジ率のサマリー行を出力する（小数点以下1桁で表示）
        sb.AppendLine($"<p>全体カバレッジ: <strong>{overallPct:F1}%</strong>（実行済み {totalCovered} 行、部分実行 {totalPartial} 行 / 対象 {totalLines} 行）</p>");

        // スクリプト一覧テーブルのヘッダー行を出力する
        sb.AppendLine("""
            <table class="data">
            <tr><th>ページ URL</th><th class="num">表示画面数</th><th>スクリプト</th><th class="num">実行済み</th><th class="num">部分実行</th><th class="num">対象行数</th><th class="num">カバレッジ率<br><small style="font-weight:normal;font-size:11px">※部分実行は0.5行換算</small></th></tr>
            """);

        // 重複ファイル名の連番管理: 同名ファイル名が複数行に現れる場合に "(2)" "(3)" を付ける
        // （1件目はサフィックスなし、2件目以降に出現順の連番を付ける）
        var filenameSeen = new Dictionary<string, int>();

        // スクリプトごとのデータ行を出力する
        foreach (var (pages, url, screenCount, covered, partial, total, mergedFilename) in rows)
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
            // URL が1件: URL をそのまま表示する
            // URL が2件以上: <details>/<summary> で展開表示し、URL 別ページへのリンクを並べる
            // 表示順は「最後に登場した URL を先頭」にするため pages を逆順にしたコピーを使う。
            // （data-page ソートキーは従来どおり最初の URL を使うため、元の pages はそのまま残す）
            var displayPages = new List<(string pageUrl, string tabFilename)>(pages);
            displayPages.Reverse();
            string pageUrlCell;
            if (displayPages.Count <= 1)
            {
                // 単一 URL: URL を直接表示する（XSS 対策のため HTML エスケープする）
                if (displayPages.Count == 0 || string.IsNullOrEmpty(displayPages[0].pageUrl))
                {
                    pageUrlCell = "(不明)";
                }
                else
                {
                    pageUrlCell = HtmlEncode(displayPages[0].pageUrl);
                }
            }
            else
            {
                // 複数 URL: <details>/<summary> で展開できるようにする
                var sbDetails = new StringBuilder();
                sbDetails.Append($"<details><summary>複数ページ ({displayPages.Count})</summary><ul>");
                foreach (var (pageUrl, tabFilename) in displayPages)
                {
                    // リンクテキストは URL（取得できなかった場合は "(URL なし)"）
                    string displayText;
                    if (string.IsNullOrEmpty(pageUrl))
                    {
                        displayText = "(URL なし)";
                    }
                    else
                    {
                        displayText = HtmlEncode(pageUrl);
                    }
                    sbDetails.Append($"<li><a href=\"scripts/{tabFilename}\">{displayText}</a></li>");
                }
                sbDetails.Append("</ul></details>");
                pageUrlCell = sbDetails.ToString();
            }

            // 重複ファイル名の場合は "(N)" サフィックスを付けてユーザーが区別できるようにする
            // 1件目はサフィックスなし、2件目以降は "(2)" "(3)" と連番を付ける
            string baseName = GetFileName(url);
            if (!filenameSeen.ContainsKey(baseName)) { filenameSeen[baseName] = 0; }
            filenameSeen[baseName]++;
            string displayName;
            if (filenameSeen[baseName] > 1)
            {
                displayName = $"{baseName} ({filenameSeen[baseName]})";
            }
            else
            {
                displayName = baseName;
            }

            // ソート用のキー（data 属性）を準備する
            // data-rate は JavaScript の parseFloat で読むため、カルチャに依存しない小数点表記にする
            string dataPage;
            if (pages.Count > 0)
            {
                dataPage = HtmlEncode(pages[0].pageUrl);
            }
            else
            {
                dataPage = "";
            }
            string dataRate = pct.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

            // カバレッジ率に応じたセルの色分けクラス（80% 以上 = 緑系、50% 以上 = 黄系、それ未満 = 赤系）
            string rateClass = RateClass(pct, total);

            // ページ URL・表示画面数・スクリプトファイル名（合成ページへのリンク付き）・各行数・カバレッジ率を出力する
            sb.AppendLine($"<tr class=\"script\" data-page=\"{dataPage}\" data-screens=\"{screenCount}\" data-name=\"{HtmlEncode(displayName)}\" " +
                          $"data-covered=\"{covered}\" data-partial=\"{partial}\" data-total=\"{total}\" data-rate=\"{dataRate}\">" +
                          $"<td>{pageUrlCell}</td>" +
                          $"<td class=\"num\">{screenCount}</td>" +
                          $"<td><a href=\"scripts/{mergedFilename}\">{HtmlEncode(displayName)}</a></td>" +
                          $"<td class=\"num\">{covered}</td><td class=\"num\">{partial}</td>" +
                          $"<td class=\"num\">{total}</td><td class=\"num {rateClass}\">{pct:F1}%</td></tr>");

            // ソースマップで解決された元ファイルの一覧をバンドル行の直下に折りたたみ表示する
            // （実バンドルは数百ファイルを含むことがあるため常時展開しない。
            //   行数・カバレッジ率は元ファイルの行単位集計。全体集計には含めない — バンドル行と二重計上になるため）
            if (srcRowsByScript != null && srcRowsByScript.TryGetValue(mergedFilename, out var srcRows))
            {
                sb.AppendLine($"<tr class=\"srcfiles\"><td colspan=\"7\">" +
                              $"<details><summary>元ファイル ({srcRows.Count})</summary>" +
                              $"<table class=\"srcfiles-table\">");
                foreach (var (srcPath, srcCovered, srcPartial, srcTotal, srcFilename) in srcRows)
                {
                    // 元ファイルのカバレッジ率（ゼロ除算を避ける。Partial は 0.5 行換算で本体と同じ計算式）
                    double srcPct;
                    if (srcTotal > 0)
                    {
                        srcPct = 100.0 * (srcCovered + srcPartial * 0.5) / srcTotal;
                    }
                    else
                    {
                        srcPct = 0;
                    }

                    // sourcesContent があれば詳細ページへのリンク、なければテキストのみ表示する
                    string nameCell;
                    if (srcFilename != null)
                    {
                        nameCell = $"<a href=\"scripts/{srcFilename}\">{HtmlEncode(srcPath)}</a>";
                    }
                    else
                    {
                        nameCell = $"{HtmlEncode(srcPath)} <small>（ソース内容なし）</small>";
                    }

                    sb.AppendLine($"<tr><td>{nameCell}</td>" +
                                  $"<td class=\"num\">{srcCovered}</td><td class=\"num\">{srcPartial}</td>" +
                                  $"<td class=\"num\">{srcTotal}</td><td class=\"num {RateClass(srcPct, srcTotal)}\">{srcPct:F1}%</td></tr>");
                }
                sb.AppendLine("</table></details></td></tr>");
            }
        }

        // テーブルの閉じタグを出力する
        sb.AppendLine("</table>");

        // 列見出しクリックでの並べ替え用 JavaScript を出力する
        sb.AppendLine(HtmlTemplates.IndexSortScript);

        // 制約・計測対象外パターンのセクションを出力する（レポート末尾に配置）
        sb.AppendLine(HtmlTemplates.IndexPageConstraints);
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// カバレッジ率に応じたセルの色分け CSS クラスを返す。
    /// 80% 以上 = rate-high（緑系）、50% 以上 = rate-mid（黄系）、それ未満 = rate-low（赤系）。
    /// 対象行が 0 の場合は色分けしない（空文字を返す）。
    /// </summary>
    private static string RateClass(double pct, int total)
    {
        if (total <= 0) { return ""; }
        if (pct >= 80.0) { return "rate-high"; }
        if (pct >= 50.0) { return "rate-mid"; }
        return "rate-low";
    }

    /// <summary>
    /// グループ（同一スクリプトの全タブ・全ナビゲーション分）から一度も実行されなかった関数の一覧を作る。
    /// 関数は先頭範囲のオフセット（開始・終了）で同一性を判定し、
    /// グループ内のいずれかのエントリで実行されていれば「実行済み」として除外する。
    /// V8 が常に報告するスクリプト全体を覆うトップレベルエントリ（無名・全範囲）は関数ではないため除外する。
    /// </summary>
    /// <param name="group">同一スクリプトの ScriptCoverage のリスト（1件でもよい）</param>
    /// <param name="source">スクリプトのソースコード全文（BOM 除去済み。行番号計算に使う）</param>
    /// <returns>（関数名, 1始まり行番号）のリスト。行番号順（同行は名前順）でソート済み</returns>
    internal static List<(string Name, int Line)> CollectUncalledFunctions(IReadOnlyList<ScriptCoverage> group, string source)
    {
        var result = new List<(string Name, int Line)>();
        if (group == null || string.IsNullOrEmpty(source)) { return result; }

        // 関数の範囲（開始・終了オフセット） → (名前, 実行されたか) を集約する
        var functions = new Dictionary<(int Start, int End), (string Name, bool Executed)>();
        foreach (var script in group)
        {
            if (script == null || script.Functions == null) { continue; }
            foreach (var func in script.Functions)
            {
                if (func == null || func.Ranges == null || func.Ranges.Count == 0) { continue; }

                // 先頭の範囲が関数全体のスパン（V8 精密カバレッジの仕様: ranges[0] = 関数全体）
                var root = func.Ranges[0];

                // スクリプト全体を覆うトップレベルエントリは関数定義ではないため除外する
                if (root.StartOffset <= 0 && root.EndOffset >= source.Length) { continue; }

                // いずれかの範囲が1回でも実行されていれば実行済みとみなす
                // （関数が呼ばれていなければ内側の範囲も実行されないため、この判定で漏れはない）
                bool executed = false;
                foreach (var range in func.Ranges)
                {
                    if (range.Count > 0) { executed = true; break; }
                }

                var key = (root.StartOffset, root.EndOffset);
                if (functions.TryGetValue(key, out var existing))
                {
                    // 同じ範囲が複数エントリにある場合: 名前は最初に見つかった非空のものを使い、実行状態は OR 合成する
                    string mergedName = existing.Name;
                    if (string.IsNullOrEmpty(mergedName)) { mergedName = func.FunctionName; }
                    functions[key] = (mergedName, existing.Executed || executed);
                }
                else
                {
                    functions[key] = (func.FunctionName, executed);
                }
            }
        }

        // 行番号計算用に各行の開始オフセットを前計算する（'\n'・'\r\n'・'\r' 区切り・0始まり）
        // SplitOnNewlines と同じ規則で \r のみの改行（旧 Mac 形式）にも対応する
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
            else if (source[i] == '\r' && (i + 1 >= source.Length || source[i + 1] != '\n'))
            {
                // CR のみ（CRLF の \r は後続の \n 側で処理されるため、ここでは \n が後続しない場合のみ）
                lineStarts.Add(i + 1);
            }
        }

        // 未実行の関数だけを行番号付きで結果に追加する
        foreach (var kv in functions)
        {
            if (kv.Value.Executed) { continue; }

            int start = kv.Key.Start;
            if (start < 0) { start = 0; }
            // start を含む行を二分探索で求める（見つからない場合は挿入位置の1つ前の行）
            int lineIndex = lineStarts.BinarySearch(start);
            if (lineIndex < 0) { lineIndex = ~lineIndex - 1; }

            string name = kv.Value.Name;
            if (string.IsNullOrEmpty(name)) { name = "(無名関数)"; }
            result.Add((name, lineIndex + 1));
        }

        // 行番号順（同行は名前順）に並べて表示を安定させる
        result.Sort((a, b) =>
        {
            int cmp = a.Line.CompareTo(b.Line);
            if (cmp != 0) { return cmp; }
            return string.CompareOrdinal(a.Name, b.Name);
        });
        return result;
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
    /// <summary>
    /// スクリプト詳細ページのヘッダーテンプレート。
    /// __TITLE__ は BuildScriptPage がスクリプト名入りのタイトルに置換する。
    /// </summary>
    public const string ScriptPageHeader = """
        <!DOCTYPE html><html lang="ja"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <link rel="icon" href="data:,">
        <title>__TITLE__</title>
        <style>
        body{font-family:monospace;font-size:13px;margin:0;background:#fff}
        h1{padding:8px 12px;background:#2d2d2d;color:#fff;margin:0;font-size:13px;word-break:break-all}
        .script-url{padding:4px 12px;background:#3d3d3d;color:#bbb;font-size:11px;word-break:break-all}
        .subnav{padding:4px 12px;background:#f0f0f0;border-bottom:1px solid #ddd;
                font-size:12px;font-family:sans-serif}
        .subnav a{color:#1a7a4a;text-decoration:none}
        .subnav a:hover{text-decoration:underline}
        .legend{display:flex;flex-wrap:wrap;gap:8px 24px;padding:8px 12px;
                background:#f7f7f7;border-bottom:1px solid #ddd;
                font-size:12px;font-family:sans-serif;align-items:center;
                position:sticky;top:0;z-index:2}
        .legend-item{display:inline-flex;align-items:center;gap:5px;color:#444}
        .swatch{display:inline-block;width:16px;height:12px;
                border:1px solid rgba(0,0,0,.18);border-radius:2px;flex-shrink:0}
        .back-link{color:#1a7a4a;text-decoration:none;white-space:nowrap}
        .back-link:hover{text-decoration:underline}
        .source{white-space:pre;overflow-x:auto}
        .line{display:flex;line-height:1.6;width:max-content;min-width:100%;
              scroll-margin-top:6em}
        .line:target{outline:2px solid #f5a623;outline-offset:-2px}
        .gutter{min-width:48px;padding:0 8px;text-align:right;user-select:none;
                background:#f5f5f5;color:#aaa;border-right:2px solid #e0e0e0;
                position:sticky;left:0;z-index:1}
        .gutter::after{display:inline-block;width:14px;content:""}
        .line-covered   .gutter::after{content:"✓"}
        .line-uncovered .gutter::after{content:"✗"}
        .line-partial   .gutter::after{content:"◐"}
        .code{padding:0 8px;flex:1}
        .line-covered   .gutter{background:#c6efc6;color:#3a7d3a;border-color:#8fc98f}
        .line-uncovered .gutter{background:#f0c6c6;color:#7d3a3a;border-color:#c98f8f}
        .line-partial   .gutter{background:#f0e8a0;color:#6b6000;border-color:#c9b800}
        span.covered  {background:#d4f8d4}
        span.uncovered{background:#f8d4d4}
        span.neutral  {}
        .uncalled{padding:8px 12px;background:#fff6f6;border-bottom:1px solid #ddd;
                  font-size:12px;font-family:sans-serif;color:#444}
        .uncalled summary{cursor:pointer;color:#a33;font-weight:600}
        .uncalled ul{margin:6px 0 2px;padding-left:20px;max-height:200px;overflow-y:auto}
        .uncalled li{margin:2px 0}
        .uncalled a{color:#1a7a4a;text-decoration:none}
        .uncalled a:hover{text-decoration:underline}
        </style></head><body>
        """;

    public const string ScriptPageLegend = """
        <div class="legend">
          <a class="back-link" href="../index.html">← 一覧に戻る</a>
          <span class="legend-item"><span class="swatch" style="background:#c6efc6"></span>✓ 実行済み — 行内すべてのブロックが実行された</span>
          <span class="legend-item"><span class="swatch" style="background:#f0e8a0"></span>◐ 部分実行 — 実行済みと未実行が混在（if/else の片側など）</span>
          <span class="legend-item"><span class="swatch" style="background:#f0c6c6"></span>✗ 未実行 — 一度も実行されなかった</span>
          <span class="legend-item"><span class="swatch" style="background:#e8e8e8"></span>対象外 — コメント・空行・変数宣言のみの行など</span>
        </div>
        """;

    public const string IndexPageHeader = """
        <!DOCTYPE html><html lang="ja"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <link rel="icon" href="data:,">
        <title>JS カバレッジレポート</title>
        <style>
        body{font-family:sans-serif;padding:24px;color:#333}
        h1{font-size:20px;margin-bottom:16px}
        .meta{color:#777;font-size:12px;margin:-8px 0 16px}
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
        table.data th.sortable{cursor:pointer;user-select:none}
        table.data th.sortable:hover{background:#ececec}
        td.rate-high{background:#e7f6e7}
        td.rate-mid{background:#fdf6dd}
        td.rate-low{background:#fbe9e9}
        tr.srcfiles > td{background:#fbfbfb;padding:4px 12px}
        table.srcfiles-table{border-collapse:collapse;width:100%;font-size:12px;color:#555;margin:4px 0}
        table.srcfiles-table td{border:none;padding:3px 12px 3px 0}
        table.srcfiles-table td.num{text-align:right;font-variant-numeric:tabular-nums;width:80px}
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
          ※ 対象行数にはコメント・空行・実装を持たない行など（対象外）は含みません。<br>
          ※ ソースマップで解決した元ファイル（「元ファイル (N)」内の行）の数値は参考情報であり、
          バンドル行との二重計上を避けるため全体カバレッジには含めません。<br>
          ※ 一覧の列見出しをクリックすると、その列で並べ替えできます。</p>
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

    /// <summary>
    /// インデックスページのテーブルソート用 JavaScript。
    /// 列見出しのクリックで昇順/降順を切り替える。バンドル行（tr.script）と
    /// その直後の元ファイル行（tr.srcfiles）をひとまとまりとして並べ替える。
    /// </summary>
    public const string IndexSortScript = """
        <script type="text/javascript">
        (function () {
          var table = document.querySelector('table.data');
          if (!table) { return; }
          var headerCells = table.rows[0].cells;
          var keys = ['page', 'screens', 'name', 'covered', 'partial', 'total', 'rate'];
          // 文字列として比較する列（ページ URL とスクリプト名）。それ以外は数値として比較する
          var stringColumns = { 0: true, 2: true };
          var ascending = {};
          function collectGroups() {
            var groups = [];
            table.querySelectorAll('tr.script').forEach(function (row) {
              var unit = [row];
              var next = row.nextElementSibling;
              if (next) {
                if (next.classList.contains('srcfiles')) { unit.push(next); }
              }
              groups.push(unit);
            });
            return groups;
          }
          var i;
          var columnCount = Math.min(headerCells.length, keys.length);
          for (i = 0; i < columnCount; i++) {
            (function (col) {
              headerCells[col].classList.add('sortable');
              headerCells[col].title = 'クリックで並べ替え';
              headerCells[col].addEventListener('click', function () {
                var key = keys[col];
                var numeric = true;
                if (stringColumns[col]) { numeric = false; }
                var dir = ascending[col] = !ascending[col];
                var groups = collectGroups();
                groups.sort(function (a, b) {
                  var va = a[0].getAttribute('data-' + key) || '';
                  var vb = b[0].getAttribute('data-' + key) || '';
                  var cmp;
                  if (numeric) { cmp = parseFloat(va) - parseFloat(vb); }
                  else {
                    // 三項演算子を使わず文字列比較の大小を if/else で求める
                    if (va < vb) { cmp = -1; }
                    else if (va > vb) { cmp = 1; }
                    else { cmp = 0; }
                  }
                  // 昇順 (dir=true) はそのまま、降順は符号を反転して返す（三項演算子を使わない）
                  if (dir) { return cmp; }
                  else { return -cmp; }
                });
                var body = table.tBodies[0];
                groups.forEach(function (unit) {
                  unit.forEach(function (row) { body.appendChild(row); });
                });
              });
            })(i);
          }
        })();
        </script>
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
              <strong>ソースマップ対応は行単位</strong> —
              //# sourceMappingURL が取得できるスクリプトは、元ファイル（TypeScript 等）別の行カバレッジを
              バンドル行の下に表示します。元ソースの色分けは行単位です（文字単位の色分けはバンドル後コードのページのみ）。
              sourcesContent を含まないマップは集計のみ表示し、ソース表示はできません。
              マップが取得できないスクリプトは従来どおり変換後のコードが計測対象になります。
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

// ============================================================================
// 以下は旧 SourceMap.cs から統合した型（Source Map v3 の解析と行単位カバレッジ射影）。
// ルール「関連機能は全てこのソース内に収める」に従い Report.cs へ取り込んだ。
// ============================================================================

/// <summary>
/// Source Map v3 の1セグメント（生成コードの位置 → 元ファイルの位置の対応）を表すレコード。
/// 元ファイルの列情報は行単位集計には不要なため保持しない。
/// </summary>
/// <param name="GenColumn">生成コードの列（0始まり・UTF-16 コードユニット単位）</param>
/// <param name="SourceIndex">sources 配列のインデックス</param>
/// <param name="SourceLine">元ファイルの行（0始まり）</param>
internal readonly record struct SourceMapSegment(
    int GenColumn,   // 生成コードの列（0始まり）
    int SourceIndex, // sources 配列のインデックス
    int SourceLine   // 元ファイルの行（0始まり）
);

/// <summary>
/// Source Map v3 を解析した結果を保持するクラス。
/// 生成コードの行ごとに「列 → 元ファイル位置」のセグメントリストを持つ。
/// </summary>
internal sealed class SourceMap
{
    /// <summary>元ファイルのパス一覧（sourceRoot 適用済み）</summary>
    public IReadOnlyList<string> Sources { get; }

    /// <summary>元ファイルのソース本文一覧（マップに埋め込まれていない場合は null。Sources と同じ長さ）</summary>
    public IReadOnlyList<string> SourcesContent { get; }

    /// <summary>生成コードの行ごとのセグメントリスト（インデックス = 生成コードの行番号、0始まり）</summary>
    public IReadOnlyList<IReadOnlyList<SourceMapSegment>> GeneratedLines { get; }

    private SourceMap(
        IReadOnlyList<string> sources,
        IReadOnlyList<string> sourcesContent,
        IReadOnlyList<IReadOnlyList<SourceMapSegment>> generatedLines)
    {
        Sources        = sources;
        SourcesContent = sourcesContent;
        GeneratedLines = generatedLines;
    }

    /// <summary>
    /// Source Map v3 の JSON を解析する。
    /// 解析できない場合（不正な JSON・sections 形式・必須フィールド欠落）は null を返す。
    /// </summary>
    /// <param name="json">ソースマップの JSON 文字列</param>
    /// <returns>解析結果。失敗時は null</returns>
    public static SourceMap Parse(string json)
    {
        if (string.IsNullOrEmpty(json)) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return null; }

            // sections（インデックスマップ形式）は非対応のため null を返す
            if (root.TryGetProperty("sections", out _)) { return null; }

            // version フィールドがあれば 3 であることを確認する（欠落は許容する）
            if (root.TryGetProperty("version", out var verProp)
                && verProp.ValueKind == JsonValueKind.Number
                && verProp.TryGetInt32(out int version)
                && version != 3)
            {
                return null;
            }

            // sources（必須）を取り出す
            if (!root.TryGetProperty("sources", out var sourcesProp) || sourcesProp.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            // sourceRoot があれば各 sources エントリの前に連結する（仕様どおり）
            string sourceRoot = "";
            if (root.TryGetProperty("sourceRoot", out var rootProp) && rootProp.ValueKind == JsonValueKind.String)
            {
                string rootTmp = rootProp.GetString();
                if (rootTmp != null) { sourceRoot = rootTmp; }
            }

            var sources = new List<string>();
            foreach (var s in sourcesProp.EnumerateArray())
            {
                string path = "";
                if (s.ValueKind == JsonValueKind.String)
                {
                    string pathTmp = s.GetString();
                    if (pathTmp != null) { path = pathTmp; }
                }
                // sourceRoot を連結する（区切りスラッシュは重複・欠落しないよう補完する）
                if (sourceRoot.Length > 0)
                {
                    if (!sourceRoot.EndsWith('/') && !path.StartsWith('/'))
                    {
                        path = sourceRoot + "/" + path;
                    }
                    else
                    {
                        path = sourceRoot + path;
                    }
                }
                sources.Add(path);
            }

            // sourcesContent（任意）を取り出す。sources と同じ長さになるよう null で埋める
            var contents = new List<string>();
            if (root.TryGetProperty("sourcesContent", out var contentProp) && contentProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in contentProp.EnumerateArray())
                {
                    if (c.ValueKind == JsonValueKind.String) { contents.Add(c.GetString()); }
                    else { contents.Add(null); }
                }
            }
            while (contents.Count < sources.Count) { contents.Add(null); }

            // mappings（必須）をデコードする
            if (!root.TryGetProperty("mappings", out var mappingsProp) || mappingsProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            string mappings = mappingsProp.GetString();
            if (mappings == null) { mappings = ""; }

            var lines = DecodeMappings(mappings, sources.Count);
            return new SourceMap(sources, contents, lines);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// mappings 文字列（Base64 VLQ）をデコードして生成行ごとのセグメントリストを作る。
    /// セグメントは 1・4・5 フィールドのいずれか。元ファイル情報を持つ 4・5 フィールドのみ保持する。
    /// 壊れた VLQ に遭遇した場合はその時点までのデコード結果を返す（部分的なマップとして利用する）。
    /// </summary>
    /// <param name="mappings">mappings フィールドの文字列</param>
    /// <param name="sourceCount">sources 配列の要素数（範囲外参照のセグメントを捨てるために使う）</param>
    private static List<IReadOnlyList<SourceMapSegment>> DecodeMappings(string mappings, int sourceCount)
    {
        var lines = new List<IReadOnlyList<SourceMapSegment>>();
        var current = new List<SourceMapSegment>();

        // VLQ のデルタ累積値（仕様: genCol は行ごとにリセット、src 系は行をまたいで累積）
        int genCol  = 0;
        int srcIdx  = 0;
        int srcLine = 0;
        int srcCol  = 0;
        int nameIdx = 0;

        int pos = 0;
        while (true)
        {
            // 行末（';'）または文字列終端で現在の行を確定する
            if (pos >= mappings.Length || mappings[pos] == ';')
            {
                lines.Add(current);
                current = new List<SourceMapSegment>();
                genCol = 0; // 生成列は行ごとにリセットする
                if (pos >= mappings.Length) { break; }
                pos++;
                continue;
            }
            // セグメント区切り
            if (mappings[pos] == ',') { pos++; continue; }

            // フィールド1: 生成コードの列デルタ
            if (!TryDecodeVlq(mappings, ref pos, out int dGenCol)) { lines.Add(current); return lines; }
            genCol += dGenCol;

            // フィールド2〜4（元ファイル情報）があるかどうかを次の文字で判定する
            bool hasSourceInfo = pos < mappings.Length && mappings[pos] != ',' && mappings[pos] != ';';
            if (hasSourceInfo)
            {
                if (!TryDecodeVlq(mappings, ref pos, out int dSrcIdx))  { lines.Add(current); return lines; }
                if (!TryDecodeVlq(mappings, ref pos, out int dSrcLine)) { lines.Add(current); return lines; }
                if (!TryDecodeVlq(mappings, ref pos, out int dSrcCol))  { lines.Add(current); return lines; }
                srcIdx  += dSrcIdx;
                srcLine += dSrcLine;
                srcCol  += dSrcCol;

                // フィールド5（名前インデックス）があれば消費する（行単位集計では使わない）
                if (pos < mappings.Length && mappings[pos] != ',' && mappings[pos] != ';')
                {
                    if (!TryDecodeVlq(mappings, ref pos, out int dNameIdx)) { lines.Add(current); return lines; }
                    nameIdx += dNameIdx;
                }
                _ = nameIdx; // 累積は仕様上必要だが値自体は未使用

                // 範囲外の参照（壊れたマップ）は捨てる
                if (srcIdx >= 0 && srcIdx < sourceCount && srcLine >= 0 && genCol >= 0)
                {
                    current.Add(new SourceMapSegment(genCol, srcIdx, srcLine));
                }
            }
        }
        return lines;
    }

    /// <summary>
    /// Base64 VLQ の1値をデコードする。pos はデコードした分だけ進む。
    /// </summary>
    /// <returns>デコードに成功したら true</returns>
    private static bool TryDecodeVlq(string s, ref int pos, out int value)
    {
        int result = 0;
        int shift  = 0;
        while (true)
        {
            if (pos >= s.Length) { value = 0; return false; }
            int digit = Base64Value(s[pos]);
            if (digit < 0) { value = 0; return false; }
            pos++;
            result |= (digit & 31) << shift;
            // 継続ビット（32）が立っていなければ終端
            if ((digit & 32) == 0) { break; }
            shift += 5;
            // 32bit を超える値は壊れたマップとみなす（シフトオーバーフロー防止）
            if (shift > 30) { value = 0; return false; }
        }
        // 最下位ビットが符号（1 = 負）
        bool negative = (result & 1) == 1;
        result >>= 1;
        if (negative) { value = -result; } else { value = result; }
        return true;
    }

    /// <summary>
    /// Base64 文字（A-Za-z0-9+/）を 0〜63 の値に変換する。不正な文字は -1。
    /// </summary>
    private static int Base64Value(char c)
    {
        if (c >= 'A' && c <= 'Z') { return c - 'A'; }
        if (c >= 'a' && c <= 'z') { return c - 'a' + 26; }
        if (c >= '0' && c <= '9') { return c - '0' + 52; }
        if (c == '+') { return 62; }
        if (c == '/') { return 63; }
        return -1;
    }
}

/// <summary>
/// スクリプトソース末尾の sourceMappingURL コメントを抽出する静的クラス。
/// </summary>
internal static class SourceMapUrlExtractor
{
    /// <summary>
    /// ソースコードから sourceMappingURL コメントの値を抽出する。
    /// 対応形式: //# sourceMappingURL=URL および //@ sourceMappingURL=URL（レガシー）。
    /// 複数ある場合は最後の出現を使う（バンドラーが末尾に付け直すため）。
    /// </summary>
    /// <param name="source">スクリプトのソースコード全文</param>
    /// <returns>マップの URL（相対・絶対・data: のいずれか）。見つからない場合は null</returns>
    public static string Extract(string source)
    {
        if (string.IsNullOrEmpty(source)) { return null; }

        const string marker = "sourceMappingURL=";
        int idx = source.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) { return null; }

        // marker の前方の空白（スペース・タブ）をスキップして '#' または '@' を探す
        // （仕様の形式は "//# sourceMappingURL=..." で # の後に空白が入る）
        int hashPos = idx - 1;
        while (hashPos >= 0 && (source[hashPos] == ' ' || source[hashPos] == '\t')) { hashPos--; }
        if (hashPos < 0) { return null; }
        char h = source[hashPos];
        if (h != '#' && h != '@') { return null; }

        // さらにその直前に "//" が連続していること（//# / //@ 形式のコメントのみ受け付ける）
        if (hashPos < 2 || source[hashPos - 1] != '/' || source[hashPos - 2] != '/')
        {
            return null;
        }

        // 値部分: marker の直後から空白・改行の手前まで
        int valStart = idx + marker.Length;
        int valEnd   = valStart;
        while (valEnd < source.Length && !char.IsWhiteSpace(source[valEnd])) { valEnd++; }
        if (valEnd == valStart) { return null; }
        return source.Substring(valStart, valEnd - valStart);
    }
}

/// <summary>
/// 生成コードの文字単位カバレッジマップを、ソースマップを使って元ファイルの行単位カバレッジに射影する静的クラス。
/// </summary>
internal static class SourceMapProjector
{
    /// <summary>この元ファイル行に対応する生成コードに実行済み（1）の文字があったことを示すフラグ</summary>
    public const int CoveredFlag = 1;

    /// <summary>この元ファイル行に対応する生成コードに未実行（0）の文字があったことを示すフラグ</summary>
    public const int UncoveredFlag = 2;

    /// <summary>
    /// 生成コードのカバレッジマップを元ファイルの行単位に集計する。
    /// 各セグメントの生成コード範囲（このセグメントの列から同じ行の次のセグメントの列まで）の
    /// カバレッジ値を調べ、対応する元ファイルの行にフラグを立てる。
    /// </summary>
    /// <param name="generatedSource">生成コード（バンドル後 JS）の全文。BOM 除去済みであること</param>
    /// <param name="map">BuildCoverageMap が返した文字単位カバレッジマップ（generatedSource と同じ長さ）</param>
    /// <param name="sourceMap">解析済みソースマップ</param>
    /// <returns>sourceIndex → (元ファイルの行番号 → フラグ) の辞書。カバレッジ情報のない行は含まれない</returns>
    public static Dictionary<int, Dictionary<int, int>> Project(string generatedSource, int[] map, SourceMap sourceMap)
    {
        var result = new Dictionary<int, Dictionary<int, int>>();
        if (string.IsNullOrEmpty(generatedSource) || map == null || sourceMap == null) { return result; }

        // 生成コードの各行の開始オフセットを求める（ソースマップの行・列は 0 始まり、行は '\n' 区切り）
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < generatedSource.Length; i++)
        {
            if (generatedSource[i] == '\n') { lineStarts.Add(i + 1); }
        }

        int lineCount = Math.Min(lineStarts.Count, sourceMap.GeneratedLines.Count);
        for (int line = 0; line < lineCount; line++)
        {
            var segments = sourceMap.GeneratedLines[line];
            if (segments.Count == 0) { continue; }

            int lineStart = lineStarts[line];
            // 行末オフセット（'\n' 自体は含めない）
            int lineEnd;
            if (line + 1 < lineStarts.Count) { lineEnd = lineStarts[line + 1] - 1; }
            else { lineEnd = generatedSource.Length; }

            for (int j = 0; j < segments.Count; j++)
            {
                var seg = segments[j];
                // このセグメントが対応する生成コードの範囲: [自分の列, 次のセグメントの列)（最後は行末まで）
                int spanStart = lineStart + seg.GenColumn;
                int spanEnd;
                if (j + 1 < segments.Count) { spanEnd = lineStart + segments[j + 1].GenColumn; }
                else { spanEnd = lineEnd; }

                // マップ範囲・行範囲にクランプする（壊れたマップへの防衛処理）
                if (spanStart < 0) { spanStart = 0; }
                if (spanEnd > lineEnd) { spanEnd = lineEnd; }
                if (spanEnd > map.Length) { spanEnd = map.Length; }
                if (spanStart >= spanEnd) { continue; }

                // 範囲内のカバレッジ値を調べる（1 = 実行済み、0 = 未実行、-1 = 対象外）
                bool hasCovered   = false;
                bool hasUncovered = false;
                for (int k = spanStart; k < spanEnd; k++)
                {
                    if (map[k] == 1) { hasCovered = true; }
                    else if (map[k] == 0) { hasUncovered = true; }
                    if (hasCovered && hasUncovered) { break; }
                }
                if (!hasCovered && !hasUncovered) { continue; } // 全文字が対象外 → 集計しない

                // 元ファイルの行にフラグを集約する
                Dictionary<int, int> lineFlags;
                if (!result.TryGetValue(seg.SourceIndex, out lineFlags))
                {
                    lineFlags = new Dictionary<int, int>();
                    result[seg.SourceIndex] = lineFlags;
                }
                lineFlags.TryGetValue(seg.SourceLine, out int flags);
                if (hasCovered)   { flags |= CoveredFlag; }
                if (hasUncovered) { flags |= UncoveredFlag; }
                lineFlags[seg.SourceLine] = flags;
            }
        }
        return result;
    }
}

// ============================================================================
// 以下は旧 CoverageExporter.cs から統合した型（LCOV / JSON 形式のエクスポート）。
// ルール「関連機能は全てこのソース内に収める」に従い Report.cs へ取り込んだ。
// ============================================================================

/// <summary>
/// エクスポート用に1スクリプト（URL ごとの合成済みグループ）分のデータをまとめるレコード。
/// HtmlReportGenerator.Generate がレポート生成中に蓄積する。
/// </summary>
/// <param name="Url">スクリプトの URL</param>
/// <param name="Pages">読み込まれた画面のリスト（タブ番号は1始まり）</param>
/// <param name="LineStatuses">合成（OR マージ済み）カバレッジの行ステータス（インデックス = 0始まり行番号）</param>
/// <param name="SourceFiles">ソースマップで解決した元ファイルのリスト（マップなしの場合は空）</param>
internal sealed record ExportScriptData(
    string                                       Url,          // スクリプトの URL
    IReadOnlyList<(int Tab, string PageUrl)>     Pages,        // 画面（タブ番号・URL）のリスト
    IReadOnlyList<LineCoverageStatus>            LineStatuses, // 合成カバレッジの行ステータス
    IReadOnlyList<ExportSourceFileData>          SourceFiles   // 元ファイル別データ（ソースマップ解決時のみ）
);

/// <summary>
/// エクスポート用に元ファイル1つ分の行カバレッジをまとめるレコード。
/// </summary>
/// <param name="Path">元ファイルのパス（ソースマップの sources の値）</param>
/// <param name="LineFlags">行番号（0始まり）→ SourceMapProjector のフラグ の辞書</param>
internal sealed record ExportSourceFileData(
    string                          Path,      // 元ファイルのパス
    IReadOnlyDictionary<int, int>   LineFlags  // 行番号 → カバレッジフラグ
);

/// <summary>
/// カバレッジデータを機械可読形式（LCOV・JSON）に変換する静的クラス。
/// LCOV は VSCode Coverage Gutters・Codecov・SonarQube・ReportGenerator などの既存ツール連携用、
/// JSON はしきい値判定・ベースライン比較・独自集計などのスクリプト処理用。
/// </summary>
internal static class CoverageExporter
{
    /// <summary>
    /// LCOV 形式（lcov.info）の文字列を生成する。
    /// ソースマップが解決できたスクリプトは元ファイル（TypeScript 等）単位で出力し、
    /// 解決できなかったスクリプトはスクリプト URL を SF パスとして出力する。
    /// 行の変換規則: 実行済み・部分実行 → DA:行,1 / 未実行 → DA:行,0 / 対象外 → レコードなし。
    /// </summary>
    /// <param name="scripts">エクスポート用スクリプトデータのリスト</param>
    /// <returns>LCOV 形式の文字列</returns>
    internal static string BuildLcov(IReadOnlyList<ExportScriptData> scripts)
    {
        var sb = new StringBuilder();
        if (scripts == null) { return ""; }

        foreach (var script in scripts)
        {
            if (script.SourceFiles.Count > 0)
            {
                // ソースマップ解決済み: 元ファイル単位で出力する
                // （バンドル行は出力しない — 元ファイルと二重計上になり、ミニファイ行は連携先で意味を持たないため）
                foreach (var sourceFile in script.SourceFiles)
                {
                    EmitLcovRecord(sb, sourceFile.Path, BuildLinesFromFlags(sourceFile.LineFlags));
                }
            }
            else
            {
                // マップなし: スクリプト URL をパスとして合成カバレッジを出力する
                EmitLcovRecord(sb, script.Url, BuildLinesFromStatuses(script.LineStatuses));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// LCOV の1レコード（SF / DA / LF / LH / end_of_record）を書き出す。
    /// </summary>
    /// <param name="sb">出力先の StringBuilder</param>
    /// <param name="path">SF レコードのパス</param>
    /// <param name="lines">（1始まり行番号, 実行回数）のリスト</param>
    private static void EmitLcovRecord(StringBuilder sb, string path, List<(int Line, int Hits)> lines)
    {
        // パスに改行が含まれると LCOV が壊れるため除去する（防衛処理）
        string safePath = path;
        if (safePath == null) { safePath = ""; }
        safePath = safePath.Replace("\r", "").Replace("\n", "");

        sb.Append("SF:").Append(safePath).Append('\n');

        // 行番号順に DA レコードを出力する
        lines.Sort((a, b) => a.Line.CompareTo(b.Line));
        int hitLines = 0;
        foreach (var (line, hits) in lines)
        {
            sb.Append("DA:").Append(line).Append(',').Append(hits).Append('\n');
            if (hits > 0) { hitLines++; }
        }

        // LF = 計測対象の行数、LH = 実行された行数
        sb.Append("LF:").Append(lines.Count).Append('\n');
        sb.Append("LH:").Append(hitLines).Append('\n');
        sb.Append("end_of_record\n");
    }

    /// <summary>
    /// 行ステータスのリストから LCOV の DA 行リストを作る。
    /// </summary>
    private static List<(int Line, int Hits)> BuildLinesFromStatuses(IReadOnlyList<LineCoverageStatus> statuses)
    {
        var lines = new List<(int Line, int Hits)>();
        if (statuses == null) { return lines; }
        for (int i = 0; i < statuses.Count; i++)
        {
            // 実行済み・部分実行 → 1、未実行 → 0、対象外 → レコードなし
            if (statuses[i] == LineCoverageStatus.Covered || statuses[i] == LineCoverageStatus.Partial)
            {
                lines.Add((i + 1, 1));
            }
            else if (statuses[i] == LineCoverageStatus.Uncovered)
            {
                lines.Add((i + 1, 0));
            }
        }
        return lines;
    }

    /// <summary>
    /// SourceMapProjector のフラグ辞書から LCOV の DA 行リストを作る。
    /// </summary>
    private static List<(int Line, int Hits)> BuildLinesFromFlags(IReadOnlyDictionary<int, int> lineFlags)
    {
        var lines = new List<(int Line, int Hits)>();
        if (lineFlags == null) { return lines; }
        foreach (var kv in lineFlags)
        {
            // 実行済みフラグが立っていれば（部分実行含む）1、未実行のみなら 0
            if ((kv.Value & SourceMapProjector.CoveredFlag) != 0)
            {
                lines.Add((kv.Key + 1, 1));
            }
            else if ((kv.Value & SourceMapProjector.UncoveredFlag) != 0)
            {
                lines.Add((kv.Key + 1, 0));
            }
        }
        return lines;
    }

    // -----------------------------------------------------------------------
    // JSON エクスポート
    // -----------------------------------------------------------------------

    // JSON シリアライズ設定（camelCase・インデント付き）
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = true,
    };

    // JSON 出力用の DTO（シリアライズ専用のため private record にする）
    private sealed record JsonReport(string GeneratedAt, JsonTotals Overall, List<JsonScript> Scripts);
    private sealed record JsonTotals(int Covered, int Partial, int Total, double Rate);
    private sealed record JsonScript(string Url, List<JsonPage> Pages, JsonTotals Lines, List<JsonSourceFile> SourceFiles);
    private sealed record JsonPage(int Tab, string Url);
    private sealed record JsonSourceFile(string Path, int Covered, int Partial, int Total, double Rate);

    /// <summary>
    /// JSON 形式（coverage.json）の文字列を生成する。
    /// 全体集計・スクリプト別の行数とカバレッジ率・画面情報・元ファイル別集計を含む。
    /// カバレッジ率は HTML レポートと同じ計算式（部分実行 = 0.5 行換算）で小数1桁に丸める。
    /// </summary>
    /// <param name="scripts">エクスポート用スクリプトデータのリスト</param>
    /// <param name="generatedAt">レポート生成日時</param>
    /// <returns>JSON 形式の文字列</returns>
    internal static string BuildJson(IReadOnlyList<ExportScriptData> scripts, DateTimeOffset generatedAt)
    {
        var jsonScripts  = new List<JsonScript>();
        int totalCovered = 0;
        int totalPartial = 0;
        int totalLines   = 0;

        if (scripts != null)
        {
            foreach (var script in scripts)
            {
                // 合成カバレッジの行数を集計する（HTML レポートのサマリーと同じ規則）
                int covered = 0;
                int partial = 0;
                int total   = 0;
                foreach (var status in script.LineStatuses)
                {
                    if (status == LineCoverageStatus.Covered)        { covered++; total++; }
                    else if (status == LineCoverageStatus.Partial)   { partial++; total++; }
                    else if (status == LineCoverageStatus.Uncovered) { total++; }
                }
                totalCovered += covered;
                totalPartial += partial;
                totalLines   += total;

                // 画面情報を DTO に変換する
                var pages = new List<JsonPage>();
                foreach (var (tab, pageUrl) in script.Pages)
                {
                    pages.Add(new JsonPage(tab, pageUrl));
                }

                // 元ファイル別の集計を DTO に変換する
                var sourceFiles = new List<JsonSourceFile>();
                foreach (var sourceFile in script.SourceFiles)
                {
                    int srcCovered = 0;
                    int srcPartial = 0;
                    int srcTotal   = 0;
                    foreach (int flags in sourceFile.LineFlags.Values)
                    {
                        if (flags == (SourceMapProjector.CoveredFlag | SourceMapProjector.UncoveredFlag))
                        {
                            srcPartial++;
                        }
                        else if (flags == SourceMapProjector.CoveredFlag)
                        {
                            srcCovered++;
                        }
                        srcTotal++;
                    }
                    sourceFiles.Add(new JsonSourceFile(sourceFile.Path, srcCovered, srcPartial, srcTotal, CalcRate(srcCovered, srcPartial, srcTotal)));
                }

                jsonScripts.Add(new JsonScript(
                    script.Url,
                    pages,
                    new JsonTotals(covered, partial, total, CalcRate(covered, partial, total)),
                    sourceFiles));
            }
        }

        var report = new JsonReport(
            generatedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            new JsonTotals(totalCovered, totalPartial, totalLines, CalcRate(totalCovered, totalPartial, totalLines)),
            jsonScripts);

        return JsonSerializer.Serialize(report, JsonOptions);
    }

    /// <summary>
    /// カバレッジ率を計算する（部分実行 = 0.5 行換算・小数1桁丸め・対象行0のときは 0）。
    /// HTML レポートと同じ計算式を使う。
    /// </summary>
    private static double CalcRate(int covered, int partial, int total)
    {
        if (total <= 0) { return 0; }
        return Math.Round(100.0 * (covered + partial * 0.5) / total, 1);
    }
}
