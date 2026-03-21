namespace JsCoverageReporter.Report;

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
          ※ 対象行数にはコメント・空行・宣言のみの行（対象外）は含みません。</p>
          <table class="legend-table">
            <tr>
              <td><span class="swatch" style="background:#c6efc6"></span><strong>実行済み</strong></td>
              <td>行内のすべてのブロックが実行された</td>
            </tr>
            <tr>
              <td><span class="swatch" style="background:#f0e8a0"></span><strong>部分実行</strong></td>
              <td>if / else など、実行された部分と未実行の部分が混在（分岐の片側だけ通った場合など）</td>
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
