using JsCoverageReporter.Coverage;
using JsCoverageReporter.Report;

namespace JsCoverageReporter.Tests;

/// <summary>
/// サンプルの HTML レポートを生成するテスト。
/// dotnet test --filter "GenerateSampleReport" で実行すると
/// %USERPROFILE%\sample-coverage-report\index.html にレポートが出力される。
/// このファイルは、レポート生成機能の動作確認と視覚的な出力確認のために使用する。
/// </summary>
public class SampleReportTests
{
    // -----------------------------------------------------------------------
    // ヘルパーメソッド（テストデータの準備に使う共通処理）
    // -----------------------------------------------------------------------

    /// <summary>
    /// ソースコードの各行が何文字目から始まるかを配列で返す（0始まり）。
    /// 例: "AB\nCD" → [0, 3]（1行目は0文字目から、2行目は3文字目から）
    /// </summary>
    /// <param name="source">調べるソースコード全文</param>
    /// <returns>各行の先頭文字インデックスを格納した配列</returns>
    private static int[] LineStarts(string source)
    {
        // 1行目は必ず0文字目から始まる
        var starts = new List<int> { 0 };

        // ソースコードを1文字ずつ調べて改行を探す
        for (int i = 0; i < source.Length; i++)
        {
            // \n（改行）の次の文字が次の行の先頭になる
            if (source[i] == '\n' && i + 1 < source.Length)
            {
                // 次の文字のインデックスを「次の行の先頭」として記録する
                starts.Add(i + 1);
            }
        }

        return starts.ToArray();
    }

    /// <summary>
    /// 指定した行範囲（0始まり・両端含む）の文字オフセット (start, end) を返す。
    /// end は次の行の先頭インデックス（または最終行の場合はソースの長さ）を指す。
    /// </summary>
    /// <param name="ls">LineStarts が返した各行の先頭文字インデックスの配列</param>
    /// <param name="source">ソースコード全文（終了位置の計算に使う）</param>
    /// <param name="fromLine">開始行番号（0始まり）</param>
    /// <param name="toLine">終了行番号（0始まり・この行を含む）</param>
    private static (int start, int end) LineRange(int[] ls, string source, int fromLine, int toLine)
    {
        // 開始行の先頭文字インデックスを取得する
        int start = ls[fromLine];

        // 終了位置を決める
        int end;
        if (toLine + 1 < ls.Length)
        {
            // 次の行が存在する場合は、次の行の先頭インデックスを終了位置とする
            // （終了行の改行文字までを範囲に含めることになる）
            end = ls[toLine + 1];
        }
        else
        {
            // 最終行の場合は、ソースコード全体の文字数を終了位置とする
            end = source.Length;
        }

        return (start, end);
    }

    // -----------------------------------------------------------------------
    // サンプルスクリプト 1 — app.js（多くの行が実行済み、else ブランチだけ未実行）
    // -----------------------------------------------------------------------

    /// <summary>
    /// app.js のサンプルカバレッジデータを生成する。
    /// handleClick の else ブランチ（id が null のケース）だけが未実行となる。
    /// </summary>
    /// <returns>app.js を模した ScriptCoverage オブジェクト</returns>
    private static ScriptCoverage BuildAppJs()
    {
        var source = string.Join("\n",
            "// アプリケーションのメインモジュール",                        //  0
            "const App = {",                                               //  1
            "",                                                            //  2
            "  // ページの初期化",                                          //  3
            "  init: function () {",                                       //  4
            "    this.bindEvents();",                                       //  5
            "    this.loadData();",                                         //  6
            "    console.log('App initialized');",                         //  7
            "  },",                                                        //  8
            "",                                                            //  9
            "  // イベントのバインド",                                      // 10
            "  bindEvents: function () {",                                 // 11
            "    document.querySelectorAll('.btn').forEach(function (btn) {", // 12
            "      btn.addEventListener('click', App.handleClick);",       // 13
            "    });",                                                     // 14
            "  },",                                                        // 15
            "",                                                            // 16
            "  // クリックイベントのハンドラ",                               // 17
            "  handleClick: function (event) {",                           // 18
            "    const id = event.target.dataset.id;",                    // 19
            "    if (id) {",                                               // 20
            "      App.processItem(id);",                                  // 21
            "    } else {",                                                // 22
            "      console.warn('No id found');",                          // 23
            "    }",                                                       // 24
            "  },",                                                        // 25
            "",                                                            // 26
            "  // データ読み込み",                                          // 27
            "  loadData: function () {",                                   // 28
            "    return fetch('/api/data')",                               // 29
            "      .then(function (res) { return res.json(); })",          // 30
            "      .then(function (data) { App.render(data); });",         // 31
            "  },",                                                        // 32
            "",                                                            // 33
            "  // 描画",                                                   // 34
            "  render: function (items) {",                               // 35
            "    const container = document.getElementById('list');",     // 36
            "    container.innerHTML = '';",                               // 37
            "    items.forEach(function (item) {",                        // 38
            "      const li = document.createElement('li');",             // 39
            "      li.textContent = item.name;",                          // 40
            "      container.appendChild(li);",                           // 41
            "    });",                                                     // 42
            "  },",                                                        // 43
            "",                                                            // 44
            "};",                                                          // 45
            "",                                                            // 46
            "App.init();"                                                  // 47
        );

        var ls = LineStarts(source);

        var (appS,   appE  ) = LineRange(ls, source,  1, 47);
        var (initS,  initE ) = LineRange(ls, source,  4,  8);
        var (bindS,  bindE ) = LineRange(ls, source, 11, 15);
        var (cbS,    cbE   ) = LineRange(ls, source, 13, 13);
        var (clickS, clickE) = LineRange(ls, source, 18, 25);
        var (ifS,    ifE   ) = LineRange(ls, source, 21, 21);  // if ブランチ（covered）
        var (elseS,  elseE ) = LineRange(ls, source, 23, 23);  // else ブランチ（uncovered）
        var (loadS,  loadE ) = LineRange(ls, source, 28, 32);
        var (thenS,  thenE ) = LineRange(ls, source, 31, 31);
        var (renS,   renE  ) = LineRange(ls, source, 35, 43);
        var (fmapS,  fmapE ) = LineRange(ls, source, 39, 41);

        var functions = new List<FunctionCoverage>
        {
            new("(anonymous)",   [new(appS,   appE,   5)]),
            new("init",          [new(initS,  initE,  1)]),
            new("bindEvents",    [new(bindS,  bindE,  1), new(cbS, cbE, 1)]),
            new("handleClick",   [new(clickS, clickE, 8), new(ifS, ifE, 8), new(elseS, elseE, 0)]),
            new("loadData",      [new(loadS,  loadE,  1), new(thenS, thenE, 1)]),
            new("render",        [new(renS,   renE,   3), new(fmapS, fmapE, 3)]),
        };

        return new ScriptCoverage(new PageInfo(0, "https://example.com"), "https://example.com/js/app.js", source, functions);
    }

    // -----------------------------------------------------------------------
    // サンプルスクリプト 2 — validator.js（エラー分岐の多くが未実行）
    // -----------------------------------------------------------------------

    /// <summary>
    /// validator.js のサンプルカバレッジデータを生成する。
    /// validateEmail・validatePassword・validateForm の各エラー分岐が未実行となる。
    /// </summary>
    /// <returns>validator.js を模した ScriptCoverage オブジェクト</returns>
    private static ScriptCoverage BuildValidatorJs()
    {
        var source = string.Join("\n",
            "// フォーム入力のバリデーション",                              //  0
            "",                                                            //  1
            "function validateEmail(email) {",                             //  2
            "  if (!email || email.length === 0) {",                       //  3
            "    return { ok: false, error: 'メールアドレスを入力してください' };",  //  4
            "  }",                                                         //  5
            "  if (!email.includes('@')) {",                               //  6
            "    return { ok: false, error: '@ を含む正しいアドレスを入力してください' };",  //  7
            "  }",                                                         //  8
            "  return { ok: true };",                                      //  9
            "}",                                                           // 10
            "",                                                            // 11
            "function validatePassword(password) {",                       // 12
            "  if (!password || password.length < 8) {",                   // 13
            "    return { ok: false, error: '8文字以上のパスワードを入力してください' };",  // 14
            "  }",                                                         // 15
            "  if (!/[A-Z]/.test(password)) {",                            // 16
            "    return { ok: false, error: '大文字を1文字以上含めてください' };",  // 17
            "  }",                                                         // 18
            "  if (!/[0-9]/.test(password)) {",                            // 19
            "    return { ok: false, error: '数字を1文字以上含めてください' };",   // 20
            "  }",                                                         // 21
            "  return { ok: true };",                                      // 22
            "}",                                                           // 23
            "",                                                            // 24
            "function validateForm(data) {",                               // 25
            "  const emailResult = validateEmail(data.email);",            // 26
            "  if (!emailResult.ok) {",                                    // 27
            "    return emailResult;",                                     // 28
            "  }",                                                         // 29
            "  const passwordResult = validatePassword(data.password);",   // 30
            "  if (!passwordResult.ok) {",                                 // 31
            "    return passwordResult;",                                  // 32
            "  }",                                                         // 33
            "  return { ok: true };",                                      // 34
            "}"                                                            // 35
        );

        var ls = LineStarts(source);

        var (veS, veE)    = LineRange(ls, source,  2, 10);
        var (e4S, e4E)    = LineRange(ls, source,  4,  4);
        var (e7S, e7E)    = LineRange(ls, source,  7,  7);
        var (vpS, vpE)    = LineRange(ls, source, 12, 23);
        var (p14S, p14E)  = LineRange(ls, source, 14, 14);
        var (p17S, p17E)  = LineRange(ls, source, 17, 17);
        var (p20S, p20E)  = LineRange(ls, source, 20, 20);
        var (vfS, vfE)    = LineRange(ls, source, 25, 35);
        var (f28S, f28E)  = LineRange(ls, source, 28, 28);
        var (f32S, f32E)  = LineRange(ls, source, 32, 32);

        var functions = new List<FunctionCoverage>
        {
            new("validateEmail",    [new(veS,  veE,  5), new(e4S,  e4E,  0), new(e7S,  e7E,  0)]),
            new("validatePassword", [new(vpS,  vpE,  3), new(p14S, p14E, 0), new(p17S, p17E, 0), new(p20S, p20E, 0)]),
            new("validateForm",     [new(vfS,  vfE,  2), new(f28S, f28E, 0), new(f32S, f32E, 0)]),
        };

        return new ScriptCoverage(new PageInfo(0, "https://example.com"), "https://example.com/js/validator.js", source, functions);
    }

    // -----------------------------------------------------------------------
    // サンプルスクリプト 3 — analytics.js（全関数が未実行）
    // -----------------------------------------------------------------------

    /// <summary>
    /// analytics.js のサンプルカバレッジデータを生成する。
    /// trackPageView・trackEvent・sendToServer・catch コールバックすべてが未実行となる。
    /// </summary>
    /// <returns>analytics.js を模した ScriptCoverage オブジェクト</returns>
    private static ScriptCoverage BuildAnalyticsJs()
    {
        var source = string.Join("\n",
            "// アナリティクスモジュール（未実装）",                        //  0
            "",                                                            //  1
            "function trackPageView(page) {",                              //  2
            "  const payload = {",                                         //  3
            "    page: page,",                                             //  4
            "    timestamp: Date.now(),",                                  //  5
            "    userAgent: navigator.userAgent,",                         //  6
            "  };",                                                        //  7
            "  sendToServer('/api/analytics/pageview', payload);",         //  8
            "}",                                                           //  9
            "",                                                            // 10
            "function trackEvent(category, action, label) {",              // 11
            "  const payload = {",                                         // 12
            "    category: category,",                                     // 13
            "    action: action,",                                         // 14
            "    label: label,",                                           // 15
            "    timestamp: Date.now(),",                                  // 16
            "  };",                                                        // 17
            "  sendToServer('/api/analytics/event', payload);",            // 18
            "}",                                                           // 19
            "",                                                            // 20
            "function sendToServer(endpoint, data) {",                     // 21
            "  fetch(endpoint, {",                                         // 22
            "    method: 'POST',",                                         // 23
            "    headers: { 'Content-Type': 'application/json' },",        // 24
            "    body: JSON.stringify(data),",                             // 25
            "  }).catch(function (err) {",                                 // 26
            "    console.error('Analytics error:', err);",                 // 27
            "  });",                                                       // 28
            "}"                                                            // 29
        );

        var ls = LineStarts(source);

        var (tvS, tvE) = LineRange(ls, source,  2,  9);
        var (teS, teE) = LineRange(ls, source, 11, 19);
        var (ssS, ssE) = LineRange(ls, source, 21, 29);
        var (cbS, cbE) = LineRange(ls, source, 27, 27);

        var functions = new List<FunctionCoverage>
        {
            new("trackPageView",  [new(tvS, tvE, 0)]),
            new("trackEvent",     [new(teS, teE, 0)]),
            new("sendToServer",   [new(ssS, ssE, 0)]),
            new("(anonymous)",    [new(cbS, cbE, 0)]),
        };

        return new ScriptCoverage(new PageInfo(0, "https://example.com"), "https://example.com/js/analytics.js", source, functions);
    }

    // -----------------------------------------------------------------------
    // サンプルスクリプト 4 — index.html のインラインスクリプト（.html と .js の混在デモ用）
    // -----------------------------------------------------------------------

    /// <summary>
    /// index.html に埋め込まれたインラインスクリプトのサンプルカバレッジデータを生成する。
    /// CDP はインラインスクリプトをページ URL（.html）として報告するため、
    /// URL が .html 拡張子のデータとなる。
    /// </summary>
    /// <returns>index.html インラインスクリプトを模した ScriptCoverage オブジェクト</returns>
    private static ScriptCoverage BuildIndexHtml()
    {
        // HTML ページに埋め込まれたインライン <script> ブロック
        // CDP はインラインスクリプトをページ URL（.html）で報告する
        var source = string.Join("\n",
            "// ログインページのインラインスクリプト",                            //  0
            "document.addEventListener('DOMContentLoaded', function () {",       //  1
            "  var form = document.getElementById('loginForm');",                 //  2
            "  if (!form) {",                                                     //  3
            "    return;",                                                        //  4
            "  }",                                                                //  5
            "  form.addEventListener('submit', function (e) {",                  //  6
            "    e.preventDefault();",                                            //  7
            "    var username = document.getElementById('username').value;",      //  8
            "    var password = document.getElementById('password').value;",      //  9
            "    if (!username || !password) {",                                  // 10
            "      alert('ユーザー名とパスワードを入力してください');",             // 11
            "      return;",                                                       // 12
            "    }",                                                               // 13
            "    fetch('/api/login', {",                                           // 14
            "      method: 'POST',",                                               // 15
            "      headers: { 'Content-Type': 'application/json' },",             // 16
            "      body: JSON.stringify({ username: username, password: password }),", // 17
            "    }).then(function (res) {",                                        // 18
            "      if (res.ok) {",                                                 // 19
            "        window.location.href = '/dashboard';",                       // 20
            "      } else {",                                                      // 21
            "        document.getElementById('error').textContent = 'ログインに失敗しました';", // 22
            "      }",                                                             // 23
            "    }).catch(function (err) {",                                       // 24
            "      console.error('ネットワークエラー:', err);",                   // 25
            "    });",                                                             // 26
            "  });",                                                               // 27
            "});"                                                                  // 28
        );

        var ls = LineStarts(source);

        // DOMContentLoaded コールバック全体（実行済み）
        var (domS, domE) = LineRange(ls, source, 1, 28);
        // フォームなし分岐の return（未実行 — フォームは存在したので通らなかった）
        var (noFormS, noFormE) = LineRange(ls, source, 4, 4);
        // submit コールバック全体（実行済み）
        var (submitS, submitE) = LineRange(ls, source, 6, 27);
        // 未入力チェックのブロック（未実行 — 常に入力された）
        var (emptyS, emptyE) = LineRange(ls, source, 11, 12);
        // fetch の then コールバック（実行済み）
        var (thenS, thenE) = LineRange(ls, source, 18, 23);
        // res.ok == true ブランチ（実行済み）
        var (okS, okE) = LineRange(ls, source, 20, 20);
        // res.ok == false ブランチ（未実行 — ログイン成功したので通らなかった）
        var (ngS, ngE) = LineRange(ls, source, 22, 22);
        // catch コールバック（未実行 — ネットワークエラーなし）
        var (catchS, catchE) = LineRange(ls, source, 24, 26);

        var functions = new List<FunctionCoverage>
        {
            new("(anonymous: DOMContentLoaded)", [new(domS,    domE,    1), new(noFormS,  noFormE,  0)]),
            new("(anonymous: submit)",           [new(submitS, submitE, 1), new(emptyS,   emptyE,   0)]),
            new("(anonymous: then)",             [new(thenS,   thenE,   1), new(okS,      okE,      1), new(ngS, ngE, 0)]),
            new("(anonymous: catch)",            [new(catchS,  catchE,  0)]),
        };

        return new ScriptCoverage(new PageInfo(0, "https://example.com"), "https://example.com/index.html", source, functions);
    }

    // -----------------------------------------------------------------------
    // テスト本体
    // -----------------------------------------------------------------------

    /// <summary>
    /// 4つのサンプルスクリプトのカバレッジデータからHTMLレポートを生成し、
    /// 必要なファイルがすべて出力されることを確認するテスト。
    ///
    /// 手動確認手順:
    ///   dotnet test --filter "GenerateSampleReport"
    ///   → %USERPROFILE%\sample-coverage-report\index.html をブラウザで開く
    /// </summary>
    [Fact]
    [Trait("Category", "Sample")]
    public void GenerateSampleReport()
    {
        // 出力先ディレクトリを組み立てる（例: C:\Users\YourName\sample-coverage-report）
        var outputDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "sample-coverage-report");

        // 4つのサンプルスクリプトのカバレッジデータを用意する
        var coverages = new List<ScriptCoverage>
        {
            BuildIndexHtml(),    // インラインスクリプト（複数のコールバック）
            BuildAppJs(),        // app.js（else ブランチだけ未実行）
            BuildValidatorJs(),  // validator.js（エラー分岐が多く未実行）
            BuildAnalyticsJs(),  // analytics.js（全関数が未実行）
        };

        // HTMLレポートを生成する
        new HtmlReportGenerator().Generate(coverages, outputDir);

        // index.html（一覧ページ）が生成されていることを確認する
        Assert.True(File.Exists(Path.Combine(outputDir, "index.html")));

        // 各スクリプトの詳細ページが生成されていることを確認する（全スクリプトが tab0 なので -tab0 がつく）
        Assert.True(File.Exists(Path.Combine(outputDir, "scripts", "script-0-tab0.html")));
        Assert.True(File.Exists(Path.Combine(outputDir, "scripts", "script-1-tab0.html")));
        Assert.True(File.Exists(Path.Combine(outputDir, "scripts", "script-2-tab0.html")));
        Assert.True(File.Exists(Path.Combine(outputDir, "scripts", "script-3-tab0.html")));
    }
}
