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

        // 各文字のカバレッジ値が格納された配列を返す
        return map;
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
