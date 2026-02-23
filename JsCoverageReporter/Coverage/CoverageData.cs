namespace JsCoverageReporter.Coverage;

/// <summary>
/// スクリプトの収集元ページ（タブ）の情報を表すレコード。
/// Index はタブが開いた順番（0始まり）、Url は StopAsync 直前に取得した page.Url。
/// </summary>
internal record PageInfo(
    int    Index,  // タブ番号（0始まり。最初のページが 0、次に開いたタブが 1 ...）
    string Url     // StopAsync 直前に取得した page.Url
);

/// <summary>
/// スクリプト1つ分のカバレッジデータ全体をまとめるレコード型。
/// Playwright CDP から取得したデータをそのまま保持する。
/// </summary>
/// <param name="Page">スクリプトの収集元ページ（タブ）情報</param>
/// <param name="Url">スクリプトのURL（ファイルパスや https:// で始まるアドレス）</param>
/// <param name="Source">スクリプトのソースコード全文</param>
/// <param name="Functions">関数ごとのカバレッジ情報のリスト</param>
internal record ScriptCoverage(
    PageInfo                        Page,      // 収集元ページ（タブ）情報
    string                          Url,       // スクリプトのURL（ファイルパスや https:// で始まるアドレス）
    string                          Source,    // スクリプトのソースコード全文
    IReadOnlyList<FunctionCoverage> Functions  // 関数ごとのカバレッジ情報のリスト
);

/// <summary>
/// 関数1つ分のカバレッジデータを保持するレコード型。
/// 関数名と、その関数内に含まれるカバレッジ範囲のリストを持つ。
/// </summary>
/// <param name="FunctionName">関数名（無名関数は空文字になることがある）</param>
/// <param name="Ranges">この関数内のカバレッジ範囲のリスト</param>
internal record FunctionCoverage(
    string FunctionName,                   // 関数名（無名関数は空文字になることがある）
    IReadOnlyList<CoverageRange> Ranges    // この関数内のカバレッジ範囲のリスト
);

/// <summary>
/// ソースコード内のある範囲が何回実行されたかを表すレコード型。
/// StartOffset と EndOffset はソースコード先頭からの文字数（バイト数ではなく文字数）。
/// </summary>
/// <param name="StartOffset">範囲の開始位置（ソースコード先頭からの文字数）</param>
/// <param name="EndOffset">範囲の終了位置（ソースコード先頭からの文字数）</param>
/// <param name="Count">実行された回数（0 = 未実行）</param>
internal record CoverageRange(
    int StartOffset,  // 範囲の開始位置（ソースコード先頭からの文字数）
    int EndOffset,    // 範囲の終了位置（ソースコード先頭からの文字数）
    int Count         // 実行された回数（0 = 未実行）
);
