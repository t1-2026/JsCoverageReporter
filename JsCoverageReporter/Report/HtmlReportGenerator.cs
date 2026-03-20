using System.Text;
using JsCoverageReporter.Coverage;

namespace JsCoverageReporter.Report;

/// <summary>
/// 行のカバレッジ状態を表す列挙型。
/// HTML出力時のCSSクラス選択やカウント集計に使用する。
/// </summary>
internal enum LineCoverageStatus
{
    /// <summary>カバレッジ対象外（空行・コメントなど実行されないコード）</summary>
    Neutral,
    /// <summary>完全に実行された行（行内のすべての文字がカバー済み）</summary>
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
    /// メソッド短縮構文の検出対象から除外するコントロールフローキーワードの集合。
    /// これらのキーワードは identifier(...) { } の形でも関数定義ではないため除外する。
    /// 例: if (cond) { } や for (;;) { } を誤検出しないようにする。
    /// </summary>
    private static readonly HashSet<string> ControlFlowKeywords = new HashSet<string>
    {
        "if", "for", "while", "switch", "catch", "do",
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
        // まず全文字を「カバレッジ対象外(-1)」で初期化する
        var map = new int[source.Length];
        Array.Fill(map, -1);

        // 全関数の範囲を1つのリストにまとめる
        var allRanges = new List<CoverageRange>();
        // 各関数のカバレッジ範囲を走査してリストに追加する
        foreach (FunctionCoverage func in functions)
        {
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
    /// ソースコードを走査して function キーワードで始まる関数宣言を検出し、
    /// カバレッジ対象外（-1）のままになっている関数本体全体を未実行（0）としてマークする。
    /// 文字列・コメント・テンプレートリテラルの中に現れる function は正しく無視する。
    /// </summary>
    /// <param name="source">スクリプトのソースコード全文</param>
    /// <param name="map">BuildCoverageMap で作成したカバレッジマップ（内容を書き換える）</param>
    internal static void MarkUncalledFunctionBodiesAsUncovered(string source, int[] map)
    {
        int len = source.Length;
        int i = 0;

        // ソースコード全体を1文字ずつ走査する
        while (i < len)
        {
            char c = source[i];

            // 単一引用符の文字列をスキップする（中の function は無視する）
            if (c == '\'')
            {
                i++;
                while (i < len && source[i] != '\'')
                {
                    // バックスラッシュによるエスケープシーケンスの次の文字を読み飛ばす
                    if (source[i] == '\\') { i++; }
                    i++;
                }
                i++; // 閉じ引用符をスキップする
                continue;
            }

            // 二重引用符の文字列をスキップする（中の function は無視する）
            if (c == '"')
            {
                i++;
                while (i < len && source[i] != '"')
                {
                    // バックスラッシュによるエスケープシーケンスの次の文字を読み飛ばす
                    if (source[i] == '\\') { i++; }
                    i++;
                }
                i++; // 閉じ引用符をスキップする
                continue;
            }

            // テンプレートリテラルをスキップする（${ } の中はネスト深さで処理する）
            if (c == '`')
            {
                i++;
                int depth = 0;
                while (i < len)
                {
                    char t = source[i];
                    // バックスラッシュによるエスケープをスキップする
                    if (t == '\\') { i += 2; continue; }
                    // テンプレートリテラルの終端（${ の外側のみ）
                    if (t == '`' && depth == 0) { i++; break; }
                    // ${ でネストが1段深くなる
                    if (t == '$' && i + 1 < len && source[i + 1] == '{') { depth++; i += 2; continue; }
                    // } でネストが1段浅くなる
                    if (t == '}' && depth > 0) { depth--; }
                    i++;
                }
                continue;
            }

            // 行コメント // をスキップする（改行まで読み飛ばす）
            if (c == '/' && i + 1 < len && source[i + 1] == '/')
            {
                while (i < len && source[i] != '\n') { i++; }
                continue;
            }

            // ブロックコメント /* */ をスキップする
            if (c == '/' && i + 1 < len && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < len && !(source[i] == '*' && source[i + 1] == '/')) { i++; }
                i += 2; // */ の2文字をスキップする
                continue;
            }

            // 正規表現リテラルをスキップして function キーワードを誤検出しないようにする
            if (c == '/' && IsRegexStart(source, i))
            {
                // 正規表現リテラル全体をスキップして直後の位置へ進む
                i = SkipRegexLiteral(source, i);
                continue;
            }

            // アロー関数 => の検出（ブロック本体 {} を持つ場合のみ）
            if (c == '=' && i + 1 < len && source[i + 1] == '>')
            {
                // => の開始位置を記録する
                int arrowStart = i;

                // => の後の空白をスキップする
                int afterArrow = i + 2;
                while (afterArrow < len && char.IsWhiteSpace(source[afterArrow]))
                {
                    afterArrow++;
                }

                // 次の文字が { でなければブロック本体ではないのでスキップする（例: x => x + 1）
                if (afterArrow < len && source[afterArrow] == '{')
                {
                    // このアロー関数がカバレッジデータにない（未実行）場合のみ処理する
                    if (map[arrowStart] == -1)
                    {
                        // 対応する } を探す
                        int braceEnd = FindMatchingBrace(source, afterArrow);
                        if (braceEnd > afterArrow)
                        {
                            // => から } までを未実行（0）にマークする
                            for (int m = arrowStart; m <= braceEnd; m++)
                            {
                                if (map[m] == -1)
                                {
                                    map[m] = 0;
                                }
                            }
                        }
                    }
                }

                // => の 2 文字目（>）をスキップして次の文字へ進む
                i++;
            }

            // function キーワードの検出を試みる（'f' 以外の文字は確実にスキップする）
            if (c == 'f' && i + 8 <= len && source.Substring(i, 8) == "function")
            {
                // function の前が識別子文字でないことを確認する（別の単語の一部を誤検出しない）
                bool prevOk = i == 0 || !IsIdentifierChar(source[i - 1]);
                // function の直後が識別子文字でないことを確認する（例: functionCall を除外する）
                bool nextOk = i + 8 >= len || !IsIdentifierChar(source[i + 8]);

                if (prevOk && nextOk && map[i] == -1)
                {
                    // このオフセットは V8 がカバレッジデータに含めなかった関数と判断する
                    int funcStart = i;

                    // async function の場合、async キーワードも未実行（赤）としてマークする
                    // function キーワードの直前を逆方向に走査して async を探す
                    int scanBack = funcStart - 1;
                    // function キーワードの前の空白をスキップする
                    while (scanBack >= 0 && char.IsWhiteSpace(source[scanBack]))
                    {
                        scanBack--;
                    }
                    // 5文字以上あり、その位置の5文字が "async" であるか確認する
                    if (scanBack >= 4)
                    {
                        string candidate = source.Substring(scanBack - 4, 5);
                        if (candidate == "async")
                        {
                            // async の前がidentifier文字でないことを確認する（notasync などを除外）
                            bool isStandaloneAsync;
                            if (scanBack - 5 < 0)
                            {
                                // ファイル先頭なので前に文字がない → standalone async
                                isStandaloneAsync = true;
                            }
                            else
                            {
                                // 直前の文字が識別子文字でなければ standalone async
                                isStandaloneAsync = !IsIdentifierChar(source[scanBack - 5]);
                            }
                            if (isStandaloneAsync)
                            {
                                // async キーワードの開始位置（scanBack - 4）から5文字を 0 にマークする
                                int asyncStart = scanBack - 4;
                                for (int a = asyncStart; a <= scanBack; a++)
                                {
                                    if (map[a] == -1)
                                    {
                                        map[a] = 0;
                                    }
                                }
                            }
                        }
                    }

                    int j = i + 8; // "function" の次の文字へ進む

                    // ジェネレータ関数の * をスキップする
                    if (j < len && source[j] == '*') { j++; }
                    // 空白をスキップする
                    while (j < len && char.IsWhiteSpace(source[j])) { j++; }
                    // 関数名（識別子）をスキップする（無名関数の場合はスキップなし）
                    while (j < len && IsIdentifierChar(source[j])) { j++; }
                    // 空白をスキップする
                    while (j < len && char.IsWhiteSpace(source[j])) { j++; }

                    // パラメータリスト ( ... ) をスキップする
                    if (j < len && source[j] == '(')
                    {
                        j = SkipBalancedParens(source, j);
                    }

                    // 空白をスキップする
                    while (j < len && char.IsWhiteSpace(source[j])) { j++; }

                    // 関数本体 { ... } の終端位置を探す
                    if (j < len && source[j] == '{')
                    {
                        int funcEnd = FindMatchingBrace(source, j);
                        if (funcEnd > 0)
                        {
                            // 関数本体内でカバレッジ対象外(-1)の部分を未実行(0)としてマークする
                            for (int k = funcStart; k < funcEnd; k++)
                            {
                                if (map[k] == -1)
                                {
                                    map[k] = 0;
                                }
                            }
                            // 関数本体をスキップして次の走査位置へ進む
                            i = funcEnd;
                            continue;
                        }
                    }
                }
            }

            // メソッド短縮構文（例: greet() { }）の検出
            // identifier の先頭にいる場合（前の文字がidentifier文字でない）に処理する
            bool prevIsIdentChar;
            if (i == 0)
            {
                // ファイル先頭なので直前に文字がない → identifier 先頭とみなす
                prevIsIdentChar = false;
            }
            else
            {
                // 直前の文字が識別子文字かどうかを確認する
                prevIsIdentChar = IsIdentifierChar(source[i - 1]);
            }

            if (IsIdentifierChar(c) && !prevIsIdentChar)
            {
                // メソッド短縮構文の検出と未実行マークをヘルパーメソッドに委譲する
                TryMarkMethodShorthand(source, map, i, len);
            }

            i++;
        }
    }

    /// <summary>
    /// メソッド短縮構文（例: greet() { }）を検出し、
    /// カバレッジデータにない（未実行）場合は本体全体を未実行（0）としてマークする。
    /// function キーワードおよびコントロールフローキーワードは除外する。
    /// </summary>
    /// <param name="source">スクリプトのソースコード全文</param>
    /// <param name="map">BuildCoverageMap で作成したカバレッジマップ（内容を書き換える）</param>
    /// <param name="identStart">identifier の先頭インデックス</param>
    /// <param name="len">ソースコードの長さ（source.Length）</param>
    private static void TryMarkMethodShorthand(string source, int[] map, int identStart, int len)
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
            // 除外対象なので何もしない
            return;
        }

        // identifier の後の空白をスキップする
        int afterIdent = identEnd;
        while (afterIdent < len && char.IsWhiteSpace(source[afterIdent]))
        {
            afterIdent++;
        }

        // 次の文字が ( でなければメソッド短縮構文でない
        if (afterIdent >= len || source[afterIdent] != '(')
        {
            return;
        }

        // SkipBalancedParens でパラメータ括弧をスキップする
        // SkipBalancedParens は閉じ括弧 ) の直後のインデックスを返す
        int afterParens = SkipBalancedParens(source, afterIdent);

        // パラメータの後の空白をスキップする
        while (afterParens < len && char.IsWhiteSpace(source[afterParens]))
        {
            afterParens++;
        }

        // 次の文字が { であり、かつカバレッジデータにない場合のみ処理する
        if (afterParens >= len || source[afterParens] != '{' || map[identStart] != -1)
        {
            return;
        }

        // 対応する } を探す（FindMatchingBrace は } の直後のインデックスを返す）
        int braceEnd = FindMatchingBrace(source, afterParens);
        if (braceEnd <= afterParens)
        {
            // 対応する } が見つからなかった場合は何もしない
            return;
        }

        // FindMatchingBrace は } の次の位置を返すため、} 自体は braceEnd - 1
        // identifier の先頭から } までを未実行（0）にマークする
        for (int m = identStart; m <= braceEnd - 1; m++)
        {
            if (map[m] == -1)
            {
                map[m] = 0;
            }
        }
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
        // pos の直前の非空白文字を探す
        int i = pos - 1;
        while (i >= 0 && char.IsWhiteSpace(source[i])) { i--; }
        // ファイル先頭または文の先頭なら正規表現
        if (i < 0) { return true; }
        char prev = source[i];
        // 識別子末尾・数字・閉じ括弧の後は除算演算子
        if (IsIdentifierChar(prev) || prev == ')' || prev == ']') { return false; }
        // 演算子・区切り文字の後は正規表現
        return true;
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
                // 単一引用符の文字列をスキップする
                i++;
                while (i < len && source[i] != '\'') { if (source[i] == '\\') { i++; } i++; }
            }
            else if (c == '"')
            {
                // 二重引用符の文字列をスキップする
                i++;
                while (i < len && source[i] != '"') { if (source[i] == '\\') { i++; } i++; }
            }
            else if (c == '`')
            {
                // テンプレートリテラルをスキップする（簡易版）
                i++;
                while (i < len && source[i] != '`') { if (source[i] == '\\') { i++; } i++; }
            }
            else if (c == '/' && IsRegexStart(source, i))
            {
                // 正規表現リテラルをスキップして括弧のカウントがずれないようにする
                i = SkipRegexLiteral(source, i) - 1;
            }
            i++;
        }

        // depth が 0 になった時点で i は ')' の次の位置を指している
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
                // 単一引用符の文字列をスキップする
                i++;
                while (i < len && source[i] != '\'') { if (source[i] == '\\') { i++; } i++; }
            }
            else if (c == '"')
            {
                // 二重引用符の文字列をスキップする
                i++;
                while (i < len && source[i] != '"') { if (source[i] == '\\') { i++; } i++; }
            }
            else if (c == '`')
            {
                // テンプレートリテラルをスキップする（簡易版）
                i++;
                while (i < len && source[i] != '`') { if (source[i] == '\\') { i++; } i++; }
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
    /// HTMLの特殊文字をエスケープする。
    /// ブラウザがHTMLタグとして解釈しないように変換する。
    /// </summary>
    /// <param name="text">エスケープ対象の文字列</param>
    /// <returns>HTMLエスケープ済みの文字列</returns>
    internal static string HtmlEncode(string text)
    {
        // エスケープ処理の対象文字列（元の text を上書きしながら変換する）
        string result = text;
        // & は必ず最初に変換する（他の変換結果の & を二重変換しないため）
        result = result.Replace("&", "&amp;");
        // < をエスケープする（HTMLタグの開始文字として解釈されないようにする）
        result = result.Replace("<", "&lt;");
        // > をエスケープする（HTMLタグの終了文字として解釈されないようにする）
        result = result.Replace(">", "&gt;");
        // " をエスケープする（HTML属性値の中で使用できるようにする）
        result = result.Replace("\"", "&quot;");
        return result;
    }

    /// <summary>
    /// ソースコードを行ごとに分割し、各行のHTMLと状態を返す。
    /// 各文字にカバレッジ値に応じた span タグを付けてHTMLを構築する。
    /// </summary>
    /// <param name="source">スクリプトのソースコード全文</param>
    /// <param name="map">BuildCoverageMap が返したカバレッジ値の配列</param>
    /// <returns>行ごとの LineData オブジェクトのリスト</returns>
    internal static List<LineData> BuildLines(string source, int[] map)
    {
        // 行データを格納する結果リスト
        var result = new List<LineData>();

        // ソースコードを改行文字 \n で行に分割する
        var rawLines = source.Split('\n');

        // ソースが \n で終わる場合、末尾に空の要素が生まれるので除く
        int lineCount = rawLines.Length;
        // 最後の要素が空行（\r のみを除去すると長さ0になる）かどうか確認する
        if (lineCount > 0 && rawLines[lineCount - 1].TrimEnd('\r').Length == 0)
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

            // 行内でカバー済みの文字数（行の状態判定に使う）
            int coveredCount = 0;
            // 行内で未カバーの文字数（行の状態判定に使う）
            int uncoveredCount = 0;

            // 現在開いている <span> のカバレッジ状態（-2 = まだ <span> を開いていない）
            int currentState = -2;

            // 行内の各文字を順番に処理する
            for (int i = 0; i < rawLine.Length; i++)
            {
                // \r（CRLF 改行の CR 部分）と \0（ヌル文字）はカバレッジカウントに含めない
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

                // 文字をHTMLエスケープして追加する
                // \r（Windowsの改行コードの一部）は表示不要なので空文字に変換する
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
                else if (ch == '\r')
                {
                    // \r は出力しない（Windowsの CRLF 対応）
                }
                else if (ch == '\0')
                {
                    // NUL 文字は出力しない（一部ブラウザで表示が壊れるため）
                }
                else
                {
                    // その他の文字はそのまま文字列に追加する
                    sb.Append(ch.ToString());
                }

                // カバー済み・未カバーの文字数を集計する（行の状態判定に使う）
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
            offset += rawLine.Length + 1;
        }

        return result;
    }

    /// <summary>
    /// カバレッジデータからHTMLレポートを生成してファイルに書き出す。
    /// index.html（サマリーページ）と scripts/script-N.html（詳細ページ）を生成する。
    /// </summary>
    /// <param name="coverages">収集したスクリプトカバレッジデータのリスト</param>
    /// <param name="outputDir">レポートを出力するディレクトリのパス</param>
    internal void Generate(IReadOnlyList<ScriptCoverage> coverages, string outputDir)
    {
        // 出力ディレクトリを作成する（既に存在しても問題ない）
        Directory.CreateDirectory(outputDir);
        // スクリプト詳細ページを格納するサブディレクトリのパス
        var scriptsDir = Path.Combine(outputDir, "scripts");
        // スクリプト詳細ページ用のサブディレクトリを作成する
        Directory.CreateDirectory(scriptsDir);

        // インデックスページに表示するサマリー行のリスト（URL・行数・ファイル名を持つタプル）
        var summaryRows = new List<(string pageUrl, string url, int covered, int partial, int total, string filename)>();

        // 各スクリプトのカバレッジデータを処理して詳細ページを生成する
        for (int i = 0; i < coverages.Count; i++)
        {
            // 現在処理中のスクリプトカバレッジデータ
            var script = coverages[i];
            // このスクリプトの詳細ページのファイル名（例: script-0.html）
            // ファイル名にタブ番号を含める（例: script-0-tab0.html、script-1-tab0.html）
            var filename = $"script-{i}-tab{script.Page.Index}.html";

            // カバレッジマップ（各文字のカバレッジ値の配列）を生成する
            var map = BuildCoverageMap(script.Source, script.Functions);
            // カバレッジマップを基に行ごとのHTML・状態データを生成する
            var lines = BuildLines(script.Source, map);

            // 行の状態ごとに数を集計する変数を初期化する
            // カバー済み行数
            int covered = 0;
            // 部分カバー行数
            int partial = 0;
            // カバレッジ対象行の合計数（Neutral は含まない）
            int total = 0;

            // 各行の状態を確認してカウントする
            foreach (LineData line in lines)
            {
                if (line.Status == LineCoverageStatus.Covered)
                {
                    // 完全にカバーされた行をカウントする
                    covered++;
                    // 対象行の合計にも加算する
                    total++;
                }
                else if (line.Status == LineCoverageStatus.Partial)
                {
                    // 部分的にカバーされた行をカウントする
                    partial++;
                    // 対象行の合計にも加算する
                    total++;
                }
                else if (line.Status == LineCoverageStatus.Uncovered)
                {
                    // 未カバーの行は total のみ加算する（covered/partial には含めない）
                    total++;
                }
                // Neutral は total に含めない
            }

            // スクリプト詳細ページのHTMLを生成してファイルに書き出す
            File.WriteAllText(
                Path.Combine(scriptsDir, filename),
                BuildScriptPage(script.Page, script.Url, lines),
                Encoding.UTF8);

            // このスクリプトのサマリー情報（URL・行数・ファイル名）をリストに追加する
            summaryRows.Add((script.Page.Url, script.Url, covered, partial, total, filename));
        }

        // インデックスページ（全スクリプトのサマリー表）のHTMLを生成してファイルに書き出す
        File.WriteAllText(
            Path.Combine(outputDir, "index.html"),
            BuildIndexPage(summaryRows),
            Encoding.UTF8);
    }

    /// <summary>
    /// スクリプト詳細ページ（行ごとに色付けされたソースコード表示）のHTMLを生成する。
    /// </summary>
    /// <param name="url">スクリプトのURL（ページタイトルと見出しに使用）</param>
    /// <param name="lines">BuildLines が返した行データのリスト</param>
    /// <returns>スクリプト詳細ページの完全なHTML文字列</returns>
    internal static string BuildScriptPage(PageInfo page, string scriptUrl, List<LineData> lines)
    {
        // HTMLを構築するための文字列ビルダー
        var sb = new StringBuilder();

        // HTMLヘッダーとスタイルシートを出力する
        sb.AppendLine("""
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
            """);

        // ページ URL を取得する（空の場合は "(tab {Index})" と表示する）
        string pageUrlDisplay;
        if (string.IsNullOrEmpty(page.Url))
        {
            // ページ URL が取得できなかった場合はタブ番号をフォールバックとして表示する
            pageUrlDisplay = $"(tab {page.Index})";
        }
        else
        {
            // XSS 対策のため HTML エスケープする
            pageUrlDisplay = HtmlEncode(page.Url);
        }
        // ページ URL とスクリプト URL をページ見出しとして出力する
        sb.AppendLine($"<h1>{pageUrlDisplay} / {HtmlEncode(scriptUrl)}</h1>");

        // 各色の意味を説明する凡例バーを出力する
        sb.AppendLine("""
            <div class="legend">
              <a class="back-link" href="../index.html">← 一覧に戻る</a>
              <span class="legend-item"><span class="swatch" style="background:#c6efc6"></span>カバー済み — 行内すべてのブロックが実行された</span>
              <span class="legend-item"><span class="swatch" style="background:#f0e8a0"></span>部分カバー — 実行済みと未実行が混在（if/else の片側など）</span>
              <span class="legend-item"><span class="swatch" style="background:#f0c6c6"></span>未実行 — 一度も実行されなかった</span>
              <span class="legend-item"><span class="swatch" style="background:#e8e8e8"></span>対象外 — コメント・空行・変数宣言のみの行など</span>
            </div>
            """);

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
                // 完全カバー行は緑色の行番号を表示する
                cls = "line line-covered";
            }
            else if (line.Status == LineCoverageStatus.Uncovered)
            {
                // 未カバー行は赤色の行番号を表示する
                cls = "line line-uncovered";
            }
            else if (line.Status == LineCoverageStatus.Partial)
            {
                // 部分カバー行は黄色の行番号を表示する
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
    /// </summary>
    /// <param name="rows">各スクリプトのサマリー情報（URL・行数・ファイル名）のリスト</param>
    /// <returns>インデックスページの完全なHTML文字列</returns>
    internal static string BuildIndexPage(
        List<(string pageUrl, string url, int covered, int partial, int total, string filename)> rows)
    {
        // 全スクリプトのカバー済み行数の合計
        int totalCovered = 0;
        // 全スクリプトの部分カバー行数の合計
        int totalPartial = 0;
        // 全スクリプトのカバレッジ対象行数の合計
        int totalLines = 0;

        // 各スクリプトの行数を合計する
        foreach (var row in rows)
        {
            // カバー済み行数を合計に加算する
            totalCovered += row.covered;
            // 部分カバー行数を合計に加算する
            totalPartial += row.partial;
            // 対象行数を合計に加算する
            totalLines += row.total;
        }

        // 全体のカバレッジ率を計算する（ゼロ除算を避けるため行数が0のときは0%にする）
        double overallPct;
        if (totalLines > 0)
        {
            // (カバー済み + 部分カバー) / 全対象行数 × 100 でパーセントを計算する
            overallPct = 100.0 * (totalCovered + totalPartial) / totalLines;
        }
        else
        {
            // 対象行がない場合は 0% とする
            overallPct = 0;
        }

        // HTMLを構築するための文字列ビルダー
        var sb = new StringBuilder();

        // HTMLヘッダーとスタイルシートを出力する
        sb.AppendLine("""
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
            </style></head><body>
            <h1>JS カバレッジレポート</h1>
            """);

        // レポートの見方・凡例セクションを出力する
        sb.AppendLine("""
            <div class="guide">
              <h2>レポートの見方</h2>
              <p>このレポートは、JavaScript ファイルの各行が実際に実行されたかどうかを記録したカバレッジレポートです。<br>
              スクリプト名をクリックすると、行ごとの実行状況を色分け表示で確認できます。</p>
              <p><strong>カバレッジ率の計算式</strong><br>
              <span class="formula">（カバー済み行数 ＋ 部分カバー行数）÷ 対象行数 × 100</span><br>
              ※ 対象行数にはコメント・空行・宣言のみの行（対象外）は含みません。</p>
              <table class="legend-table">
                <tr>
                  <td><span class="swatch" style="background:#c6efc6"></span><strong>カバー済み</strong></td>
                  <td>行内のすべてのブロックが実行された</td>
                </tr>
                <tr>
                  <td><span class="swatch" style="background:#f0e8a0"></span><strong>部分カバー</strong></td>
                  <td>if / else など、実行された部分と未実行の部分が混在する（分岐の片側だけ通った場合など）</td>
                </tr>
                <tr>
                  <td><span class="swatch" style="background:#f0c6c6"></span><strong>未実行</strong></td>
                  <td>行内のコードが一度も実行されなかった</td>
                </tr>
                <tr>
                  <td><span class="swatch" style="background:#e8e8e8;border-color:#ccc"></span><strong>対象外</strong></td>
                  <td>コメント・空行・変数宣言のみの行など（カバレッジ計測の対象外）</td>
                </tr>
              </table>
            </div>
            """);

        // 全体カバレッジ率のサマリー行を出力する（小数点以下1桁で表示）
        sb.AppendLine($"<p>全体カバレッジ: <strong>{overallPct:F1}%</strong>（カバー済み {totalCovered} 行、部分カバー {totalPartial} 行 / 対象 {totalLines} 行）</p>");

        // スクリプト一覧テーブルのヘッダー行を出力する
        sb.AppendLine("""
            <table class="data">
            <tr><th>ページ URL</th><th>スクリプト</th><th class="num">カバー済み</th><th class="num">部分カバー</th><th class="num">対象行数</th><th class="num">カバレッジ率</th></tr>
            """);

        // スクリプトごとのデータ行を出力する
        foreach (var (pageUrl, url, covered, partial, total, filename) in rows)
        {
            // このスクリプトのカバレッジ率を計算する（ゼロ除算を避ける）
            double pct;
            if (total > 0)
            {
                // (カバー済み + 部分カバー) / 全対象行数 × 100 でパーセントを計算する
                pct = 100.0 * (covered + partial) / total;
            }
            else
            {
                // 対象行がない場合は 0% とする
                pct = 0;
            }

            // ページ URL のセル表示文字列を決める（空の場合は "(不明)" とする）
            string pageUrlDisplay;
            if (string.IsNullOrEmpty(pageUrl))
            {
                // ページ URL が取得できなかった場合のフォールバック表示
                pageUrlDisplay = "(不明)";
            }
            else
            {
                // XSS 対策のため HTML エスケープする
                pageUrlDisplay = HtmlEncode(pageUrl);
            }

            // ページ URL・スクリプト URL（リンク付き）・各行数・カバレッジ率を出力する
            sb.AppendLine($"<tr><td>{pageUrlDisplay}</td><td><a href=\"scripts/{filename}\">{HtmlEncode(url)}</a></td>" +
                          $"<td class=\"num\">{covered}</td><td class=\"num\">{partial}</td><td class=\"num\">{total}</td>" +
                          $"<td class=\"num\">{pct:F1}%</td></tr>");
        }

        // テーブルとページの閉じタグを出力する
        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }
}
