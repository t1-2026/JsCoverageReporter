// ============================================================================
//  PlaywrightCodegenAnalyzer — 外部からの呼び出し方・戻り値の処理ガイド
// ----------------------------------------------------------------------------
//  概要:
//    Playwright.NET の codegen（playwright codegen）が出力した .cs を行ベースで
//    解析し、「どの Page 変数の・どのフレームの・どの要素に・何をしているか」を
//    List<PlaywrightAction> として返す静的ユーティリティ。
//    Library / NUnit / MSTest / xUnit いずれのターゲット出力にも対応。
//
//  ── 呼び出し方（エントリポイントは 3 つ。いずれも List<PlaywrightAction> を返す）──
//
//    using PlaywrightCodegen;
//
//    // (a) ファイルパスから
//    List<PlaywrightAction> actions = PlaywrightCodegenAnalyzer.AnalyzeFile(@"C:\tests\MyTest.cs");
//
//    // (b) ソース文字列から（\r\n / \n どちらでも可）
//    List<PlaywrightAction> actions = PlaywrightCodegenAnalyzer.AnalyzeSource(sourceText);
//
//    // (c) 行の配列/リストから
//    List<PlaywrightAction> actions = PlaywrightCodegenAnalyzer.Analyze(File.ReadAllLines(path));
//
//    ※ 解析できなかった行（boilerplate や対象外）は黙って除外される。例外は投げない。
//      結果は元コードの行順（ダイアログ応答だけはトリガ操作の直前に配置される）。
//
//  ── 戻り値 PlaywrightAction の主なフィールド ──
//    PageVariable     操作対象の Page 変数名（"page" / "Page" / popup の "page1" 等）。必須
//    Frames           フレームチェーン（List<Step>）。トップフレームなら null
//    Locators         要素ロケータチェーン（List<Step>）。Page 直接操作なら null
//    Target           要素を特定するロケータのメソッド名（"GetByRole" 等）。無ければ null
//    Device           "Mouse"/"Keyboard"/"Touchscreen"。要素操作なら null
//    Action           アクションのメソッド名（"ClickAsync" 等）。必須
//    Arguments        アクション引数（生の式文字列の配列）。無ければ null
//    IsAssertion      Expect(...) アサーションなら true（Action は "ToBeVisibleAsync" 等）
//    Negated          否定アサーション(.Not.)なら true
//    ActionName       日本語の操作名（"クリック" / "検証:表示確認" / "ドラッグFrom" 等）。必須
//    Comment          行の説明（日本語）。必須
//    OpensNewPage     この操作で新しい画面が開くなら true（NewPageVariable に開く先）
//    NewPageVariable  開く先の Page 変数名（"page1" 等）。無ければ null
//    FilePath         アップロード元 / ダウンロード保存先パス。無ければ null
//    LineNumber       元の行番号（1 始まり） / RawLine  元のソース 1 行
//    Step             { Method, Args } … Frames/Locators の各要素（ToFlat の元データ）
//
//  ── 戻り値の処理例 1: フィールドを直接参照 ──
//    foreach (var a in actions)
//    {
//        Console.WriteLine($"L{a.LineNumber} [{a.ActionName}] {a.Comment}");
//        if (a.Frames != null)   Console.WriteLine($"  フレーム: {a.FrameDescription}");
//        if (a.Target != null)   Console.WriteLine($"  対象: {a.Target}");
//        if (a.Arguments != null)Console.WriteLine($"  引数: {string.Join(", ", a.Arguments)}");
//        if (a.OpensNewPage)     Console.WriteLine($"  → 新画面 {a.NewPageVariable} を開く");
//        if (a.FilePath != null) Console.WriteLine($"  パス: {a.FilePath}");
//    }
//
//  ── 戻り値の処理例 2: ToFlat() で Locator1 / Locator1Param1 … に展開（CSV・表向き）──
//    foreach (var a in actions)
//        foreach (var (key, value) in a.ToFlat())
//            Console.WriteLine($"{key}={value ?? "null"}");   // 例: Locator1=GetByRole / Action=ClickAsync
//
//  ── 戻り値の処理例 3: 種別で振り分け／集計 ──
//    var clicks     = actions.Where(a => a.Action == "ClickAsync").ToList();
//    var assertions = actions.Where(a => a.IsAssertion).ToList();
//    var dialogs    = actions.Where(a => a.Target == "Dialog").ToList();      // ダイアログ応答
//    var newWindows = actions.Where(a => a.OpensNewPage).ToList();            // 画面遷移ポイント
//    var byPage     = actions.GroupBy(a => a.PageVariable);                   // Page 変数ごと
//
//  ── 戻り値の処理例 4: JSON 化（PlaywrightAction は record なのでそのままシリアライズ可）──
//    string json = System.Text.Json.JsonSerializer.Serialize(
//        actions, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
//
//  注意: 「1 アクション = 1 行」という codegen の標準出力を前提とする
//        （唯一の複数行出力 ToMatchAriaSnapshotAsync の YAML も吸収済み）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PlaywrightCodegen
{
    /// <summary>
    /// メソッドチェーンの 1 ステップ。
    /// 例: GetByRole(AriaRole.Button, new(){ Name = "Submit" })
    ///   → Method="GetByRole", Args=["AriaRole.Button", "new(){ Name = \"Submit\" }"]
    /// </summary>
    public sealed record Step(string Method, IReadOnlyList<string> Args)
    {
        // デバッグ表示用: 引数が無ければメソッド名のみ、あれば "Method(arg, ...)"。
        public override string ToString()
        {
            // 引数なしのプロパティ的ステップ（First/Last など）はメソッド名だけ返す。
            if (Args.Count == 0)
            {
                return Method;
            }

            // 引数ありは "Method(a, b)" 形式に整形する。
            return $"{Method}({string.Join(", ", Args)})";
        }
    }

    /// <summary>
    /// codegen の 1 行（= 1 アクション）を解析した結果。
    /// </summary>
    public sealed record PlaywrightAction
    {
        /// <summary>操作対象の Page 変数名（"page", "page1" など。popup も含む）。</summary>
        public required string PageVariable { get; init; }

        /// <summary>FrameLocator / Frame チェーン。トップフレームなら null。</summary>
        public IReadOnlyList<Step>? Frames { get; init; }

        /// <summary>GetByRole / Locator / Nth / Filter などの要素ロケータチェーン。Page 直接操作なら null。</summary>
        public IReadOnlyList<Step>? Locators { get; init; }

        /// <summary>アクションのメソッド名（"ClickAsync", "FillAsync", "GotoAsync" など）。</summary>
        public required string Action { get; init; }

        /// <summary>アクションの引数（トップレベルでカンマ分割した生の式文字列）。引数なしなら null。</summary>
        public IReadOnlyList<string>? Arguments { get; init; }

        /// <summary>要素を特定するロケータのメソッド名（"GetByRole", "Locator" など）。Page 直接操作なら null。</summary>
        public string? Target { get; init; }

        /// <summary>page.Mouse / Keyboard / Touchscreen のデバイス操作なら、そのデバイス名。要素操作なら null。</summary>
        public string? Device { get; init; }

        /// <summary>Expect(...) によるアサーションなら true（その場合 Action は "ToBeVisibleAsync" 等）。</summary>
        public bool IsAssertion { get; init; }

        /// <summary>アサーションが .Not. 付き（否定）なら true。操作行では常に false。</summary>
        public bool Negated { get; init; }

        /// <summary>操作名（日本語）。例: "クリック", "入力", "ページ遷移", "検証:表示確認", "ドラッグFrom"。</summary>
        public required string ActionName { get; init; }

        /// <summary>どの要素に何をしている行かの説明（日本語）。</summary>
        public required string Comment { get; init; }

        /// <summary>この操作で新しい画面（ポップアップ/別タブ）が開くなら true（RunAndWaitForPopupAsync のトリガ）。</summary>
        public bool OpensNewPage { get; init; }

        /// <summary>開く新しい画面が代入される Page 変数名（例: "page1"）。判別不能・該当なしなら null。</summary>
        public string? NewPageVariable { get; init; }

        /// <summary>アップロード元 / ダウンロード保存先のパス（手編集で書かれている場合のみ。無ければ null）。</summary>
        public string? FilePath { get; init; }

        /// <summary>解析対象の行番号（1 始まり）。</summary>
        public int LineNumber { get; init; }

        /// <summary>元のソース 1 行（trim 済み）。</summary>
        public string RawLine { get; init; } = "";

        /// <summary>フレームチェーンの文字列表現。トップフレームなら null。</summary>
        public string? FrameDescription
        {
            get
            {
                // フレームが無ければトップフレーム扱いで null。
                if (Frames == null)
                {
                    return null;
                }

                // 各フレームステップを " » " で連結する。
                return string.Join(" » ", Frames.Select(f => f.ToString()));
            }
        }

        /// <summary>
        /// ロケータチェーンとその引数を番号付きのフラットなキー/値に展開する。
        /// 例:
        ///   Locator1       = GetByRole
        ///   Locator1Param1 = AriaRole.Button
        ///   Locator1Param2 = new(){ Name = "Pay" }
        ///   Locator2       = Nth
        ///   Locator2Param1 = 1
        /// Frame / Action も同様に Frame1/Frame1Param1、Action/ActionParam1 として展開する。
        /// 未指定のものは出力されない（= null 扱い）。順序は解析順を維持する。
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, string?>> ToFlat()
        {
            // 出力列を順に積んでいくリスト。
            var cols = new List<KeyValuePair<string, string?>>();

            // 先頭は必ず Page 変数。
            cols.Add(new KeyValuePair<string, string?>("Page", PageVariable));

            // デバイス操作なら Device 列を出す。
            if (Device != null)
            {
                cols.Add(new KeyValuePair<string, string?>("Device", Device));
            }

            // フレームチェーンを Frame1, Frame1Param1, ... に展開。
            AppendSteps(cols, "Frame", Frames);

            // ロケータチェーンを Locator1, Locator1Param1, ... に展開。
            AppendSteps(cols, "Locator", Locators);

            // アサーションなら Is / Not を示す Assert 列を出す。
            if (IsAssertion)
            {
                // 否定なら "Not"、肯定なら "Is"。
                string mark;
                if (Negated)
                {
                    mark = "Not";
                }
                else
                {
                    mark = "Is";
                }
                cols.Add(new KeyValuePair<string, string?>("Assert", mark));
            }

            // アクションのメソッド名。
            cols.Add(new KeyValuePair<string, string?>("Action", Action));

            // アクションの引数を ActionParam1, ActionParam2, ... に展開。
            if (Arguments != null)
            {
                for (int p = 0; p < Arguments.Count; p++)
                {
                    cols.Add(new KeyValuePair<string, string?>($"ActionParam{p + 1}", Arguments[p]));
                }
            }

            // 新画面を開く操作なら OpensNewPage 列を出す。
            if (OpensNewPage)
            {
                // 変数名が分かればそれを、無ければ "true" を値にする。
                string val;
                if (NewPageVariable != null)
                {
                    val = NewPageVariable;
                }
                else
                {
                    val = "true";
                }
                cols.Add(new KeyValuePair<string, string?>("OpensNewPage", val));
            }

            // アップロード元 / ダウンロード保存先パスがあれば FilePath 列を出す。
            if (FilePath != null)
            {
                cols.Add(new KeyValuePair<string, string?>("FilePath", FilePath));
            }

            return cols;
        }

        // ステップ列を "PrefixN" / "PrefixNParamM" の形で cols に追加する。
        private static void AppendSteps(List<KeyValuePair<string, string?>> cols, string prefix, IReadOnlyList<Step>? steps)
        {
            // ステップが無ければ何も追加しない。
            if (steps == null)
            {
                return;
            }

            // 各ステップを番号付きで展開する。
            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i];

                // "Locator1" = メソッド名。
                cols.Add(new KeyValuePair<string, string?>($"{prefix}{i + 1}", s.Method));

                // "Locator1Param1" = 各引数。
                for (int p = 0; p < s.Args.Count; p++)
                {
                    cols.Add(new KeyValuePair<string, string?>($"{prefix}{i + 1}Param{p + 1}", s.Args[p]));
                }
            }
        }

        // デバッグ用の 1 行表現。
        public override string ToString()
        {
            // 引数があれば " args=[...]" を付ける。
            string args = "";
            if (Arguments != null)
            {
                args = $" args=[{string.Join(", ", Arguments)}]";
            }

            // アサーションの種別プレフィックス。
            string kind = "";
            if (IsAssertion)
            {
                if (Negated)
                {
                    kind = "assert-not ";
                }
                else
                {
                    kind = "assert ";
                }
            }

            // 操作対象（デバイス → ロケータ → ページ の優先順）。
            string what;
            if (Device != null)
            {
                what = Device;
            }
            else if (Target != null)
            {
                what = Target;
            }
            else
            {
                what = "(page)";
            }

            // フレーム表記（無ければ "(top)"）。
            string frame;
            if (FrameDescription != null)
            {
                frame = FrameDescription;
            }
            else
            {
                frame = "(top)";
            }

            return $"L{LineNumber}: {PageVariable} / {frame} / {what} → {kind}{Action}{args}";
        }
    }

    /// <summary>
    /// Playwright.NET の codegen が出力した .cs を行ベースで解析するアナライザ。
    /// 「1 アクション = 1 行」という codegen の標準出力を前提とする（複数行折り返しは非対応）。
    /// </summary>
    public static class PlaywrightCodegenAnalyzer
    {
        // codegen が Page 変数を生成する代入の右辺メソッド。
        private static readonly string[] PageFactoryMethods =
        {
            "NewPageAsync",
            "RunAndWaitForPopupAsync",
            "WaitForPopupAsync",
        };

        // フレーム境界となるステップ。これ以外のロケータは要素チェーン側へ振り分ける。
        private static readonly HashSet<string> FrameMethods = new(StringComparer.Ordinal)
        {
            "FrameLocator",
            "Frame",
            "ChildFrames",
        };

        // 要素そのものを特定せず、位置や条件で絞り込む/合成する修飾メソッド。Target・主語選定では飛ばす。
        private static readonly HashSet<string> ModifierMethods = new(StringComparer.Ordinal)
        {
            "Nth",
            "First",
            "Last",
            "Filter",
            "And",
            "Or",
        };

        // page 直下の入力デバイス。要素ロケータではなくデバイスとして扱う。
        private static readonly HashSet<string> DeviceNames = new(StringComparer.Ordinal)
        {
            "Mouse",
            "Keyboard",
            "Touchscreen",
        };

        // 操作メソッド名 → 日本語の操作名。
        private static readonly Dictionary<string, string> ActionNameJa = new(StringComparer.Ordinal)
        {
            ["ClickAsync"] = "クリック",
            ["DblClickAsync"] = "ダブルクリック",
            ["FillAsync"] = "入力",
            ["TypeAsync"] = "入力",
            ["PressAsync"] = "キー入力",
            ["PressSequentiallyAsync"] = "キー入力",
            ["CheckAsync"] = "チェックを入れる",
            ["UncheckAsync"] = "チェックを外す",
            ["SetCheckedAsync"] = "チェック設定",
            ["SelectOptionAsync"] = "選択",
            ["SelectTextAsync"] = "テキスト選択",
            ["HoverAsync"] = "ホバー",
            ["FocusAsync"] = "フォーカス",
            ["BlurAsync"] = "フォーカスを外す",
            ["TapAsync"] = "タップ",
            ["ClearAsync"] = "クリア",
            ["SetInputFilesAsync"] = "アップロード",
            ["DragToAsync"] = "ドラッグ",
            ["ScreenshotAsync"] = "スクリーンショット",
            ["ScrollIntoViewIfNeededAsync"] = "スクロール表示",
            // page レベル操作
            ["GotoAsync"] = "ページ遷移",
            ["GoBackAsync"] = "戻る",
            ["GoForwardAsync"] = "進む",
            ["ReloadAsync"] = "リロード",
            ["CloseAsync"] = "閉じる",
            ["WaitForTimeoutAsync"] = "待機",
            ["WaitForLoadStateAsync"] = "読み込み待ち",
            ["WaitForURLAsync"] = "URL遷移待ち",
            ["SetViewportSizeAsync"] = "ビューポート設定",
            // mouse / keyboard 操作
            ["MoveAsync"] = "移動",
            ["DownAsync"] = "押下",
            ["UpAsync"] = "離す",
            ["WheelAsync"] = "ホイール",
            ["InsertTextAsync"] = "テキスト挿入",
        };

        // アサーションメソッド名 → 日本語（"検証:"/"検証(否定):" は付与時に前置する）。
        private static readonly Dictionary<string, string> AssertNameJa = new(StringComparer.Ordinal)
        {
            ["ToBeVisibleAsync"] = "表示確認",
            ["ToBeHiddenAsync"] = "非表示確認",
            ["ToBeAttachedAsync"] = "存在確認",
            ["ToBeEnabledAsync"] = "有効確認",
            ["ToBeDisabledAsync"] = "無効確認",
            ["ToBeEditableAsync"] = "編集可能確認",
            ["ToBeCheckedAsync"] = "チェック状態確認",
            ["ToBeFocusedAsync"] = "フォーカス確認",
            ["ToBeEmptyAsync"] = "空確認",
            ["ToBeInViewportAsync"] = "ビューポート内確認",
            ["ToHaveTextAsync"] = "テキスト確認",
            ["ToContainTextAsync"] = "テキスト包含確認",
            ["ToHaveValueAsync"] = "値確認",
            ["ToHaveValuesAsync"] = "選択値確認",
            ["ToHaveAttributeAsync"] = "属性確認",
            ["ToHaveClassAsync"] = "class確認",
            ["ToContainClassAsync"] = "class包含確認",
            ["ToHaveScreenshotAsync"] = "スクリーンショット一致確認",
            ["ToHaveCountAsync"] = "件数確認",
            ["ToHaveCSSAsync"] = "CSS確認",
            ["ToHaveIdAsync"] = "id確認",
            ["ToHaveURLAsync"] = "URL確認",
            ["ToHaveTitleAsync"] = "タイトル確認",
            // 新しめのアサーション
            ["ToHaveAccessibleNameAsync"] = "アクセシブル名確認",
            ["ToHaveAccessibleDescriptionAsync"] = "アクセシブル説明確認",
            ["ToHaveAccessibleErrorMessageAsync"] = "アクセシブルエラー確認",
            ["ToHaveRoleAsync"] = "ロール確認",
            ["ToHaveJSPropertyAsync"] = "JSプロパティ確認",
            ["ToMatchAriaSnapshotAsync"] = "ARIAスナップショット確認",
        };

        // デバイス名 → 日本語。
        private static readonly Dictionary<string, string> DeviceJa = new(StringComparer.Ordinal)
        {
            ["Mouse"] = "マウス",
            ["Keyboard"] = "キーボード",
            ["Touchscreen"] = "タッチスクリーン",
        };

        // 反対メソッドが存在するアサーション。Not 付きなら否定を解除して反対メソッドへ正規化する。
        // （Visible⇔Hidden / Enabled⇔Disabled のみ。他は反対メソッドが無いので否定のまま）
        private static readonly Dictionary<string, string> AssertInverse = new(StringComparer.Ordinal)
        {
            ["ToBeVisibleAsync"] = "ToBeHiddenAsync",
            ["ToBeHiddenAsync"] = "ToBeVisibleAsync",
            ["ToBeEnabledAsync"] = "ToBeDisabledAsync",
            ["ToBeDisabledAsync"] = "ToBeEnabledAsync",
        };

        // GetByXxx → 要素種別の日本語名（コメント文を自然にするため）。
        private static readonly Dictionary<string, string> ElementNoun = new(StringComparer.Ordinal)
        {
            ["GetByLabel"] = "ラベル",
            ["GetByText"] = "テキスト",
            ["GetByPlaceholder"] = "プレースホルダ",
            ["GetByTestId"] = "テストID",
            ["GetByTitle"] = "タイトル",
            ["GetByAltText"] = "代替テキスト",
        };

        // AriaRole 名 → 日本語のロール名（未知のロールは英語のまま）。
        private static readonly Dictionary<string, string> RoleJa = new(StringComparer.Ordinal)
        {
            ["Button"] = "ボタン",
            ["Link"] = "リンク",
            ["Checkbox"] = "チェックボックス",
            ["Radio"] = "ラジオボタン",
            ["Textbox"] = "テキストボックス",
            ["Searchbox"] = "検索ボックス",
            ["Combobox"] = "コンボボックス",
            ["Listbox"] = "リストボックス",
            ["Option"] = "選択肢",
            ["Heading"] = "見出し",
            ["Listitem"] = "リスト項目",
            ["List"] = "リスト",
            ["Row"] = "行",
            ["Cell"] = "セル",
            ["Columnheader"] = "列見出し",
            ["Rowheader"] = "行見出し",
            ["Table"] = "テーブル",
            ["Grid"] = "グリッド",
            ["Tab"] = "タブ",
            ["Tabpanel"] = "タブパネル",
            ["Menu"] = "メニュー",
            ["Menuitem"] = "メニュー項目",
            ["Dialog"] = "ダイアログ",
            ["Alert"] = "アラート",
            ["Img"] = "画像",
            ["Switch"] = "スイッチ",
            ["Slider"] = "スライダー",
            ["Spinbutton"] = "数値入力",
            ["Progressbar"] = "進捗バー",
            ["Tooltip"] = "ツールチップ",
            ["Navigation"] = "ナビゲーション",
            ["Banner"] = "バナー",
            ["Form"] = "フォーム",
            ["Region"] = "領域",
            ["Group"] = "グループ",
            ["Article"] = "記事",
            ["Paragraph"] = "段落",
            ["Separator"] = "区切り",
            ["Status"] = "ステータス",
            ["Document"] = "ドキュメント",
            ["Main"] = "メイン",
        };

        // コメント文で助詞「に」を取る操作メソッド（入力・チェック・フォーカス系）。それ以外は「を」。
        private static readonly HashSet<string> ParticleNiMethods = new(StringComparer.Ordinal)
        {
            "FillAsync",
            "TypeAsync",
            "PressSequentiallyAsync",
            "InsertTextAsync",
            "PressAsync",
            "CheckAsync",
            "UncheckAsync",
            "SetCheckedAsync",
            "HoverAsync",
            "FocusAsync",
        };

        // アサーションメソッド → (肯定文, 否定文)。{arg} は引数へ置換し、主語の直後に連結する。
        private static readonly Dictionary<string, (string pos, string neg)> AssertTemplates = new(StringComparer.Ordinal)
        {
            ["ToBeVisibleAsync"] = ("が表示されていることを確認", "が表示されていないことを確認"),
            ["ToBeHiddenAsync"] = ("が表示されていないことを確認", "が表示されていることを確認"),
            ["ToBeAttachedAsync"] = ("が存在することを確認", "が存在しないことを確認"),
            ["ToBeEnabledAsync"] = ("が有効であることを確認", "が無効であることを確認"),
            ["ToBeDisabledAsync"] = ("が無効であることを確認", "が有効であることを確認"),
            ["ToBeEditableAsync"] = ("が編集可能であることを確認", "が編集不可であることを確認"),
            ["ToBeCheckedAsync"] = ("がチェックされていることを確認", "がチェックされていないことを確認"),
            ["ToBeFocusedAsync"] = ("にフォーカスがあることを確認", "にフォーカスがないことを確認"),
            ["ToBeEmptyAsync"] = ("が空であることを確認", "が空でないことを確認"),
            ["ToBeInViewportAsync"] = ("がビューポート内にあることを確認", "がビューポート内にないことを確認"),
            ["ToHaveTextAsync"] = ("のテキストが {arg} であることを確認", "のテキストが {arg} でないことを確認"),
            ["ToContainTextAsync"] = ("が {arg} を含むことを確認", "が {arg} を含まないことを確認"),
            ["ToHaveValueAsync"] = ("の値が {arg} であることを確認", "の値が {arg} でないことを確認"),
            ["ToHaveValuesAsync"] = ("の選択値が {arg} であることを確認", "の選択値が {arg} でないことを確認"),
            ["ToHaveAttributeAsync"] = ("の {arg0} 属性が {arg1} であることを確認", "の {arg0} 属性が {arg1} でないことを確認"),
            ["ToHaveClassAsync"] = ("の class が {arg} であることを確認", "の class が {arg} でないことを確認"),
            ["ToContainClassAsync"] = ("の class に {arg} が含まれることを確認", "の class に {arg} が含まれないことを確認"),
            ["ToHaveScreenshotAsync"] = ("がスクリーンショットに一致することを確認", "がスクリーンショットに一致しないことを確認"),
            ["ToHaveCountAsync"] = ("の件数が {arg} であることを確認", "の件数が {arg} でないことを確認"),
            ["ToHaveCSSAsync"] = ("の {arg0} が {arg1} であることを確認", "の {arg0} が {arg1} でないことを確認"),
            ["ToHaveIdAsync"] = ("の id が {arg} であることを確認", "の id が {arg} でないことを確認"),
            ["ToHaveURLAsync"] = ("の URL が {arg} であることを確認", "の URL が {arg} でないことを確認"),
            ["ToHaveTitleAsync"] = ("のタイトルが {arg} であることを確認", "のタイトルが {arg} でないことを確認"),
            // 新しめのアサーション
            ["ToHaveAccessibleNameAsync"] = ("のアクセシブル名が {arg} であることを確認", "のアクセシブル名が {arg} でないことを確認"),
            ["ToHaveAccessibleDescriptionAsync"] = ("のアクセシブル説明が {arg} であることを確認", "のアクセシブル説明が {arg} でないことを確認"),
            ["ToHaveAccessibleErrorMessageAsync"] = ("のエラーメッセージが {arg} であることを確認", "のエラーメッセージが {arg} でないことを確認"),
            ["ToHaveRoleAsync"] = ("のロールが {arg} であることを確認", "のロールが {arg} でないことを確認"),
            ["ToHaveJSPropertyAsync"] = ("の {arg0} プロパティが {arg1} であることを確認", "の {arg0} プロパティが {arg1} でないことを確認"),
            ["ToMatchAriaSnapshotAsync"] = ("が ARIA スナップショットに一致することを確認", "が ARIA スナップショットに一致しないことを確認"),
        };

        // 解析対象なし（空）を表す共有リスト。
        private static readonly List<PlaywrightAction> None = new();

        // ファイルパスから読み込んで解析する。
        public static List<PlaywrightAction> AnalyzeFile(string path)
        {
            // ファイルを行配列で読み込み、行配列版へ委譲する。
            return Analyze(File.ReadAllLines(path));
        }

        // ソース文字列から解析する。
        public static List<PlaywrightAction> AnalyzeSource(string source)
        {
            // 改行コードを LF に正規化してから行分割する。
            var lines = source.Replace("\r\n", "\n").Split('\n');
            return Analyze(lines);
        }

        // 行配列を解析して PlaywrightAction のリストを返す（本体）。
        public static List<PlaywrightAction> Analyze(IReadOnlyList<string> lines)
        {
            // まずファイル全体から Page 変数名を収集する。
            var pageVars = CollectPageVariables(lines);

            // codegen の既定 Page 名を保険で入れておく。
            //  - "page": Library ターゲット（var page = await context.NewPageAsync()）
            //  - "Page": NUnit/MSTest/xUnit ターゲット（PageTest 継承のフィクスチャプロパティ。var 宣言が無い）
            pageVars.Add("page");
            pageVars.Add("Page");

            // 各行を解析して通常アクションを集める。
            var results = new List<PlaywrightAction>();
            for (int i = 0; i < lines.Count; i++)
            {
                // 1 行から 0〜複数のアクション（DragTo は 2 件）を得る。
                results.AddRange(TryParseLine(lines[i], i + 1, pageVars));
            }

            // RunAndWaitFor* ブロックのトリガに意味づけ（popup/upload/download）を行う。
            MarkRunAndWaitTriggers(lines, results);

            // ダイアログ応答（dialog.AcceptAsync/DismissAsync）を専用行として収集する。
            // anchor は紐づくトリガ操作の行で、ダイアログ行はトリガの直前に配置する。
            var dialogs = CollectDialogActions(lines, pageVars, results);

            // 並べ替え用に (アクション, アンカー行, 並び順) のタプルを作る。
            var items = new List<(PlaywrightAction a, int anchor, int order)>();

            // 通常行は order=1（同一行ではダイアログの後）。
            foreach (var a in results)
            {
                items.Add((a, a.LineNumber, 1));
            }

            // ダイアログ行は order=0（同一アンカーではトリガより先）。
            foreach (var (a, anchor) in dialogs)
            {
                items.Add((a, anchor, 0));
            }

            // アンカー行 → 並び順 の順で安定ソートし、アクションだけ取り出す。
            return items.OrderBy(x => x.anchor).ThenBy(x => x.order).Select(x => x.a).ToList();
        }

        /// <summary>
        /// `var page1 = await context.NewPageAsync();` の形から Page 変数名を収集する。
        /// </summary>
        private static HashSet<string> CollectPageVariables(IReadOnlyList<string> lines)
        {
            // 収集した変数名の集合。
            var vars = new HashSet<string>(StringComparer.Ordinal);

            foreach (var raw in lines)
            {
                var line = raw.Trim();

                // Page を生成するメソッドを含まない行は対象外。
                if (!PageFactoryMethods.Any(m => line.Contains(m + "(")))
                {
                    continue;
                }

                // 左辺の変数名を取り出す。
                var name = ExtractLhsVar(line);
                if (name != null)
                {
                    vars.Add(name);
                }
            }

            return vars;
        }

        /// <summary>
        /// dialog.AcceptAsync / DismissAsync（confirm/alert/prompt の応答）を専用の行として収集する。
        /// codegen はダイアログをイベントハンドラとして出力するため、通常チェーン解析では拾えない。
        /// </summary>
        private static List<(PlaywrightAction action, int anchorLine)> CollectDialogActions(
            IReadOnlyList<string> lines, HashSet<string> pageVars, IReadOnlyList<PlaywrightAction> triggerCandidates)
        {
            // 収集したダイアログ行（アクション本体とアンカー行のペア）。
            var acts = new List<(PlaywrightAction, int)>();

            // ダイアログが属する Page 変数を推定する。
            var dialogPage = GuessDialogPage(lines, pageVars);

            for (int i = 0; i < lines.Count; i++)
            {
                var rawTrim = lines[i].Trim();

                // ダイアログ応答メソッド（AcceptAsync=OK / DismissAsync=CANCEL）を 2 形式から検出する。
                Step? step = null;
                int subLine = -1;  // 紐づく購読行（.Dialog +=）。トリガ特定の基準。

                // 形式A: 名前付きハンドラ本体 "dialog.AcceptAsync()/DismissAsync()"。
                var stripped = StripAwaitAndSemicolon(rawTrim);
                if (stripped.StartsWith("dialog.", StringComparison.Ordinal))
                {
                    step = ParseDialogResponse(stripped);
                    if (step != null)
                    {
                        subLine = FindDialogSubscriptionLine(lines, i);
                    }
                }
                // 形式B: インラインラムダ "<X>.Dialog += (_, dialog) => dialog.AcceptAsync()/DismissAsync()"。
                else if (rawTrim.Contains(".Dialog +=")
                         && (rawTrim.Contains(".AcceptAsync(") || rawTrim.Contains(".DismissAsync(")))
                {
                    step = ExtractInlineDialogResponse(rawTrim);
                    if (step != null)
                    {
                        // この購読行そのものがトリガ特定の基準。
                        subLine = i;
                    }
                }

                // どちらの形式でもなければ対象外。
                if (step == null)
                {
                    continue;
                }

                // 購読行より後ろの最初の操作行＝トリガ（=アンカー）。無ければこの行自身。
                int anchor = i + 1;
                if (subLine >= 0)
                {
                    var trigger = triggerCandidates
                        .Where(a => a.LineNumber - 1 > subLine)
                        .OrderBy(a => a.LineNumber)
                        .FirstOrDefault();
                    if (trigger != null)
                    {
                        anchor = trigger.LineNumber;
                    }
                }

                // プロンプト入力値があれば引数として渡す。
                IReadOnlyList<string>? dialogArgs = null;
                if (step.Args.Count > 0)
                {
                    dialogArgs = step.Args;
                }

                // ダイアログ行のアクションを組み立てて積む。
                var action = BuildDialogAction(dialogPage, step.Method, dialogArgs, i + 1, rawTrim);
                acts.Add((action, anchor));
            }

            return acts;
        }

        // "dialog.AcceptAsync(...)" 等を Step に変換する。root が dialog で末尾が Accept/Dismiss のときだけ返す。
        private static Step? ParseDialogResponse(string line)
        {
            List<Step> steps;
            string? root;
            try
            {
                steps = SplitChain(line, out root);
            }
            catch
            {
                return null;
            }

            if (root != "dialog" || steps.Count == 0)
            {
                return null;
            }

            var m = steps[^1];
            if (m.Method != "AcceptAsync" && m.Method != "DismissAsync")
            {
                return null;
            }
            return m;
        }

        // インラインラムダ行から AcceptAsync/DismissAsync 呼び出しを取り出す（受け手の変数名は問わない）。
        private static Step? ExtractInlineDialogResponse(string line)
        {
            // ".AcceptAsync(" / ".DismissAsync(" の位置を探す。
            int idx = line.IndexOf(".AcceptAsync(", StringComparison.Ordinal);
            if (idx < 0)
            {
                idx = line.IndexOf(".DismissAsync(", StringComparison.Ordinal);
            }
            if (idx < 0)
            {
                return null;
            }

            // '.' の次（メソッド名）から ParseSegment（最初の '(' で method、対応する ')' まで引数）。
            return ParseSegment(line[(idx + 1)..]);
        }

        // ハンドラ名経由で登録行（.Dialog +=）の行 index を返す。無ければ -1。
        private static int FindDialogSubscriptionLine(IReadOnlyList<string> lines, int dialogCallLine)
        {
            string? handler = FindEnclosingDialogHandler(lines, dialogCallLine);
            if (handler == null)
            {
                return -1;
            }

            for (int j = 0; j < lines.Count; j++)
            {
                if (lines[j].Contains(handler) && lines[j].Contains(".Dialog +="))
                {
                    return j;
                }
            }
            return -1;
        }

        // ダイアログが属する Page 変数を、最初の "<X>.Dialog +=/-=" の左側から推定する。
        private static string GuessDialogPage(IReadOnlyList<string> lines, HashSet<string> pageVars)
        {
            // 既定値: page があれば page、無ければ集合の先頭、それも無ければ "page"。
            string dialogPage;
            if (pageVars.Contains("page"))
            {
                dialogPage = "page";
            }
            else
            {
                var first = pageVars.FirstOrDefault();
                if (first != null)
                {
                    dialogPage = first;
                }
                else
                {
                    dialogPage = "page";
                }
            }

            // ".Dialog +=" / ".Dialog -=" の行を探して左辺の変数名を採用する。
            foreach (var raw in lines)
            {
                var t = raw.Trim();
                int d = t.IndexOf(".Dialog", StringComparison.Ordinal);

                // ".Dialog" が先頭以外に現れ、かつ += / -= の購読/解除行であること。
                if (d <= 0 || !(t.Contains(".Dialog +=") || t.Contains(".Dialog -=")))
                {
                    continue;
                }

                // ".Dialog" の直前トークンが変数名。
                var name = t[..d].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                if (!string.IsNullOrEmpty(name) && IsIdentifier(name))
                {
                    dialogPage = name;
                    break;
                }
            }

            return dialogPage;
        }

        /// <summary>line i から上方向に "void XxxEventHandler(object sender, IDialog dialog)" を探し、ハンドラ名を返す。</summary>
        private static string? FindEnclosingDialogHandler(IReadOnlyList<string> lines, int i)
        {
            // 自身の行から上に向かって走査する。
            for (int j = i; j >= 0; j--)
            {
                var t = lines[j].Trim();

                // "void " で始まり IDialog を引数に取るメソッド定義だけが対象。
                if (!t.StartsWith("void ", StringComparison.Ordinal) || !t.Contains("IDialog"))
                {
                    continue;
                }

                // メソッド名は "void " と '(' の間。
                int p = t.IndexOf('(');
                if (p < 0)
                {
                    continue;
                }

                var name = t["void ".Length..p].Trim();
                if (IsIdentifier(name))
                {
                    return name;
                }
            }

            return null;
        }

        // ダイアログ応答 1 件分のアクションを組み立てる。
        private static PlaywrightAction BuildDialogAction(string pageVar, string method,
            IReadOnlyList<string>? args, int lineNo, string raw)
        {
            // Accept 系かどうか。
            bool accept = method == "AcceptAsync";

            // 操作名（日本語）を決める。
            string actionName;
            if (accept)
            {
                // 引数ありはプロンプト入力、無しは単なる承認。
                if (args != null)
                {
                    actionName = "ダイアログ入力";
                }
                else
                {
                    actionName = "ダイアログ承認";
                }
            }
            else
            {
                actionName = "ダイアログ却下";
            }

            // 説明コメントを決める。
            string comment;
            if (accept)
            {
                if (args != null)
                {
                    comment = $"プロンプトに {string.Join(", ", args)} を入力して承認";
                }
                else
                {
                    comment = "確認ダイアログを承認";
                }
            }
            else
            {
                comment = "確認ダイアログを却下";
            }

            return new PlaywrightAction
            {
                PageVariable = pageVar,
                Action = method,
                Arguments = args,
                Target = "Dialog",
                ActionName = actionName,
                Comment = comment,
                LineNumber = lineNo,
                RawLine = raw,
            };
        }

        /// <summary>
        /// RunAndWaitFor* ブロックの中身（トリガ操作）に意味づけを行う。
        /// Popup→新画面フラグ、FileChooser→操作名「アップロード」、Download→操作名「ダウンロード」。
        /// いずれもブロック内の最後の操作行をトリガとみなす。
        /// </summary>
        private static void MarkRunAndWaitTriggers(IReadOnlyList<string> lines, List<PlaywrightAction> results)
        {
            // popup: 新画面フラグと開く先 Page 変数を付与する。
            ApplyTrigger(lines, results, "RunAndWaitForPopupAsync", (trig, lhsVar) =>
            {
                // 開く先 Page 変数（空なら null）。
                string? newPage = null;
                if (!string.IsNullOrEmpty(lhsVar))
                {
                    newPage = lhsVar;
                }

                // コメント末尾の注記。
                string note;
                if (string.IsNullOrEmpty(lhsVar))
                {
                    note = "　※新しい画面を開く";
                }
                else
                {
                    note = $"　※新しい画面 {lhsVar} を開く";
                }

                return trig with
                {
                    OpensNewPage = true,
                    NewPageVariable = newPage,
                    Comment = trig.Comment + note,
                };
            });

            // FileChooser: 操作名をアップロードにし、後続の SetFilesAsync からパスを拾う。
            ApplyTrigger(lines, results, "RunAndWaitForFileChooserAsync", (trig, lhsVar) =>
            {
                // "<fileChooser>.SetFilesAsync(...)" からアップロード元パスを拾う（手編集時のみ）。
                var path = FindFollowupPath(lines, lhsVar, "SetFilesAsync");

                // 注記（パスがあれば併記）。
                string note = "　※ファイルアップロード";
                if (path != null)
                {
                    note += $"（{path}）";
                }

                return trig with
                {
                    ActionName = "アップロード",
                    FilePath = path,
                    Comment = trig.Comment + note,
                };
            });

            // Download: 操作名をダウンロードにし、後続の SaveAsAsync からパスを拾う。
            ApplyTrigger(lines, results, "RunAndWaitForDownloadAsync", (trig, lhsVar) =>
            {
                // "<download>.SaveAsAsync(...)" からダウンロード保存先パスを拾う（手編集時のみ）。
                var path = FindFollowupPath(lines, lhsVar, "SaveAsAsync");

                // 注記（パスがあれば併記）。
                string note = "　※ダウンロード";
                if (path != null)
                {
                    note += $"（保存先 {path}）";
                }

                return trig with
                {
                    ActionName = "ダウンロード",
                    FilePath = path,
                    Comment = trig.Comment + note,
                };
            });
        }

        /// <summary>"&lt;lhsVar&gt;.&lt;method&gt;(...)" の行を探して引数（パス）を返す。無ければ null。</summary>
        private static string? FindFollowupPath(IReadOnlyList<string> lines, string lhsVar, string method)
        {
            // 変数名が無ければ探しようがない。
            if (string.IsNullOrEmpty(lhsVar))
            {
                return null;
            }

            foreach (var raw in lines)
            {
                // await / 末尾セミコロンを剥がす。
                var line = StripAwaitAndSemicolon(raw.Trim());

                // "lhsVar." で始まらない行は対象外。
                if (!line.StartsWith(lhsVar + ".", StringComparison.Ordinal))
                {
                    continue;
                }

                // チェーンを分解（失敗したら無視）。
                List<Step> steps;
                string? root;
                try
                {
                    steps = SplitChain(line, out root);
                }
                catch
                {
                    continue;
                }

                // root が一致しない、またはステップが無ければ対象外。
                if (root != lhsVar || steps.Count == 0)
                {
                    continue;
                }

                // 末尾メソッドが目的のもので引数があれば、それをパスとして返す。
                var m = steps[^1];
                if (m.Method != method || m.Args.Count == 0)
                {
                    continue;
                }

                // 単一引数はアンエスケープ、複数なら各要素をアンエスケープして連結する。
                if (m.Args.Count == 1)
                {
                    return UnescapePathValue(m.Args[0]);
                }
                return string.Join(", ", m.Args.Select(UnescapePathValue));
            }

            return null;
        }

        /// <summary>marker の RunAndWaitFor* ブロックを探し、ブロック内末尾の操作（トリガ）へ transform を適用する。</summary>
        private static void ApplyTrigger(IReadOnlyList<string> lines, List<PlaywrightAction> results,
            string marker, Func<PlaywrightAction, string, PlaywrightAction> transform)
        {
            // marker ブロックごとに処理する。
            foreach (var (start, end, lhsVar) in CollectRunAndWaitBlocks(lines, marker))
            {
                // ブロック（中括弧の内側）に属するアクションを集める。
                var inner = results.Where(r => r.LineNumber - 1 > start && r.LineNumber - 1 < end).ToList();
                if (inner.Count == 0)
                {
                    continue;
                }

                // 末尾の操作（最大行番号）が実際のトリガ。
                var trigger = inner.OrderBy(r => r.LineNumber).Last();

                // トリガを変換して置き換える。
                int idx = results.IndexOf(trigger);
                results[idx] = transform(trigger, lhsVar);
            }
        }

        /// <summary>
        /// 指定した RunAndWaitFor* メソッドの中括弧ブロック範囲（0 始まり行 index）と代入先変数を収集する。
        /// 呼び出しの丸括弧内に現れる '{' のみをラムダブロックの開始とみなし、後続ブロックの誤検出を防ぐ。
        /// </summary>
        private static List<(int start, int end, string lhsVar)> CollectRunAndWaitBlocks(IReadOnlyList<string> lines, string marker)
        {
            // 収集したブロックのリスト。
            var blocks = new List<(int, int, string)>();

            for (int i = 0; i < lines.Count; i++)
            {
                // marker を含む行だけが対象。
                int mPos = lines[i].IndexOf(marker, StringComparison.Ordinal);
                if (mPos < 0)
                {
                    continue;
                }

                // 代入先変数（無ければ空文字）。
                var lhsVar = ExtractLhsVar(lines[i]);
                if (lhsVar == null)
                {
                    lhsVar = "";
                }

                // 呼び出しの '(' 位置（marker の直後以降）。
                int callParen = lines[i].IndexOf('(', mPos + marker.Length);
                if (callParen < 0)
                {
                    continue;
                }

                // 走査用の状態。
                int round = 0;        // 呼び出しの丸括弧の深さ
                int open = -1;        // ブロック開始行（最初の '{' の行）
                int end = -1;         // ブロック終了行（呼び出しが閉じる行）
                bool started = false; // ラムダブロックに入ったか
                bool inStr = false;   // 文字列内か
                bool inChar = false;  // 文字リテラル内か
                bool verbatim = false;// @"" 逐語文字列か
                bool done = false;    // 走査終了か

                for (int j = i; j < lines.Count && !done; j++)
                {
                    var s = lines[j];

                    // 最初の行は呼び出しの '(' から、それ以降は行頭から。
                    int kStart;
                    if (j == i)
                    {
                        kStart = callParen;
                    }
                    else
                    {
                        kStart = 0;
                    }

                    for (int k = kStart; k < s.Length; k++)
                    {
                        char c = s[k];

                        // 文字列の中: 終端だけ判定する。
                        if (inStr)
                        {
                            if (c == '\\' && !verbatim)
                            {
                                // エスケープ: 次の 1 文字を読み飛ばす。
                                k++;
                            }
                            else if (c == '"')
                            {
                                inStr = false;
                            }
                            continue;
                        }

                        // 文字リテラルの中: 終端だけ判定する（'"' のような括弧/引用符を誤検出しないため）。
                        if (inChar)
                        {
                            if (c == '\\')
                            {
                                // エスケープ: 次の 1 文字を読み飛ばす。
                                k++;
                            }
                            else if (c == '\'')
                            {
                                inChar = false;
                            }
                            continue;
                        }

                        // 文字列開始。
                        if (c == '"')
                        {
                            inStr = true;
                            verbatim = k > 0 && s[k - 1] == '@';
                            continue;
                        }

                        // 文字リテラル開始。
                        if (c == '\'')
                        {
                            inChar = true;
                            continue;
                        }

                        // 丸括弧の対応を追う（文字列/文字の外）。
                        // ブロックの開始は「呼び出し括弧内の最初の '{'」、終了は「呼び出しが閉じる ')'」。
                        // 波括弧ではなく丸括弧で終端を取ることで、'}' が無い不正ブロックを採用せず誤検出を防ぐ。
                        if (c == '(')
                        {
                            round++;
                        }
                        else if (c == ')')
                        {
                            round--;
                            if (round == 0)
                            {
                                // 呼び出しが閉じた。ラムダブロックに入っていればここが終端。
                                // started=false（単一行ラムダや '}' 無しの不正ブロック）なら採用しない。
                                if (started)
                                {
                                    end = j;
                                }
                                done = true;
                                break;
                            }
                        }
                        else if (c == '{' && round >= 1 && !started)
                        {
                            // 呼び出し括弧内の最初の '{' がラムダブロックの開始。
                            started = true;
                            open = j;
                        }
                    }
                }

                // 開始と終了が揃ったブロックだけ採用する。
                if (started && end >= 0)
                {
                    blocks.Add((open, end, lhsVar));
                }
            }

            return blocks;
        }

        /// <summary>"var page1 = await ..." の左辺の変数名を返す。取れなければ null。</summary>
        private static string? ExtractLhsVar(string line)
        {
            line = line.Trim();

            // '=' が無ければ代入ではない。
            int eq = line.IndexOf('=');
            if (eq < 0)
            {
                return null;
            }

            // '=' より左の最後のトークンが変数名。
            var name = line[..eq].Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

            // 識別子として妥当なら返す。
            if (!string.IsNullOrEmpty(name) && IsIdentifier(name))
            {
                return name;
            }
            return null;
        }

        // 1 行を解析して 0〜複数のアクションを返す。
        private static List<PlaywrightAction> TryParseLine(string raw, int lineNo, HashSet<string> pageVars)
        {
            var line = raw.Trim();

            // 空行・コメント行は対象外。
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                return None;
            }

            // ダイアログの購読/解除行（X.Dialog += / -=）はアクションではない。
            // インラインラムダ形式は末尾が AcceptAsync/DismissAsync でアクション誤検出されるため、
            // ここで除外する（ダイアログ応答は CollectDialogActions が専用に拾う）。
            if (line.Contains(".Dialog +=") || line.Contains(".Dialog -="))
            {
                return None;
            }

            // await / 末尾セミコロンを剥がす。
            line = StripAwaitAndSemicolon(line);

            try
            {
                // アサーション: Expect( <page.ロケータ> ).Not.ToXxxAsync(...)
                if (line.StartsWith("Expect(", StringComparison.Ordinal))
                {
                    var a = TryParseAssertion(line, lineNo, raw.Trim(), pageVars);
                    if (a == null)
                    {
                        return None;
                    }
                    return new List<PlaywrightAction> { a };
                }

                // 通常アクション: page.ロケータ.XxxAsync(...)
                var steps = SplitChain(line, out var root);

                // 先頭が既知の Page 変数で始まる行だけを対象にする。
                if (root == null || !pageVars.Contains(root))
                {
                    return None;
                }

                return BuildAction(root, steps, lineNo, raw.Trim(), pageVars);
            }
            catch
            {
                // 解析途中の例外は「対象外の行」とみなす。
                return None;
            }
        }

        /// <summary>
        /// "Expect(page.GetByText("x")).Not.ToBeVisibleAsync()" を解析する。
        /// Expect(...) の内側を通常のロケータチェーンとして解析し、
        /// 末尾の ToXxxAsync をアサーションメソッド、.Not. の有無を否定として扱う。
        /// </summary>
        private static PlaywrightAction? TryParseAssertion(string line, int lineNo, string raw, HashSet<string> pageVars)
        {
            // "Expect" 直後の '(' に対応する ')' を探す。
            int open = "Expect".Length;
            int close = MatchParen(line, open);
            if (close < 0)
            {
                return null;
            }

            // Expect(...) の内側と、その後ろの ".Not.ToBeVisibleAsync()" 部分。
            var inner = line[(open + 1)..close].Trim();
            var remainder = line[(close + 1)..].Trim();

            // 内側のロケータチェーンを解析（アクションは含まれない）。
            var steps = SplitChain(inner, out var root);

            // 内側の root が既知 Page 変数でなければアサーションとして扱わない。
            if (root == null || !pageVars.Contains(root))
            {
                return null;
            }

            // 残りの先頭 '.' を落とす。
            if (remainder.StartsWith(".", StringComparison.Ordinal))
            {
                remainder = remainder[1..];
            }

            // 残りを '.' 区切りで見て、Not とアサーションメソッドを取り出す。
            bool negated = false;
            Step? assertStep = null;
            foreach (var seg in SplitTopLevel(remainder, '.'))
            {
                var t = seg.Trim();
                if (t.Length == 0)
                {
                    continue;
                }

                // ".Not." は否定フラグ。
                if (t == "Not")
                {
                    negated = true;
                    continue;
                }

                // それ以外はアサーションメソッド（最後のものを採用）。
                assertStep = ParseSegment(t);
            }

            // アサーションメソッドが取れない / Async で終わらないなら対象外。
            if (assertStep == null || !assertStep.Method.EndsWith("Async", StringComparison.Ordinal))
            {
                return null;
            }

            // Not 付き かつ 反対メソッドがあるものは、肯定形の反対メソッドへ正規化する。
            // 例: Not.ToBeVisibleAsync → ToBeHiddenAsync（Negated=false）。
            var method = assertStep.Method;
            if (negated && AssertInverse.TryGetValue(method, out var inverse))
            {
                method = inverse;
                negated = false;
            }

            // アサーションの引数（あれば）。
            // ToMatchAriaSnapshotAsync は複数行の verbatim 文字列（YAML）で、行ベース解析では
            // 1 行目しか取れず途中の "@\"" が混入するため、引数は保持しない（コメントも {arg} 不使用）。
            IReadOnlyList<string>? assertArgs = null;
            if (assertStep.Args.Count > 0 && method != "ToMatchAriaSnapshotAsync")
            {
                assertArgs = assertStep.Args;
            }

            return MakeAction(root, steps, method, assertArgs, lineNo, raw, isAssertion: true, negated: negated);
        }

        /// <summary>openIdx の '(' に対応する ')' の位置を返す。文字列/char を尊重。見つからなければ -1。</summary>
        private static int MatchParen(string s, int openIdx)
        {
            int depth = 0;          // 丸括弧深さ
            bool inStr = false;     // 文字列内か
            bool inChar = false;    // 文字リテラル内か
            bool verbatim = false;  // @"" 逐語文字列か

            for (int i = openIdx; i < s.Length; i++)
            {
                char c = s[i];

                // 文字列の中。
                if (inStr)
                {
                    if (c == '\\' && !verbatim)
                    {
                        // エスケープ: 次の 1 文字を読み飛ばす。
                        i++;
                        continue;
                    }
                    if (c == '"')
                    {
                        inStr = false;
                    }
                    continue;
                }

                // 文字リテラルの中。
                if (inChar)
                {
                    if (c == '\\')
                    {
                        i++;
                        continue;
                    }
                    if (c == '\'')
                    {
                        inChar = false;
                    }
                    continue;
                }

                // 文字列開始。
                if (c == '"')
                {
                    inStr = true;
                    verbatim = i > 0 && s[i - 1] == '@';
                    continue;
                }

                // 文字リテラル開始。
                if (c == '\'')
                {
                    inChar = true;
                    continue;
                }

                // 括弧の対応を追う。
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        // root + ステップ列から 1〜2 件のアクションを組み立てる（DragTo は元/先の 2 件）。
        private static List<PlaywrightAction> BuildAction(string root, List<Step> steps, int lineNo, string raw, HashSet<string> pageVars)
        {
            // ステップが無ければ対象外。
            if (steps.Count == 0)
            {
                return None;
            }

            // 末尾が Async で終わるメソッドをアクションとみなす。
            var last = steps[^1];
            if (!last.Method.EndsWith("Async", StringComparison.Ordinal))
            {
                return None;
            }

            // アクションを除いたロケータ/フレーム/デバイス列。
            var chain = steps.Take(steps.Count - 1);

            // DragToAsync(ILocator) は「ドラッグ元」「ドラッグ先」の 2 行に分割する。
            if (last.Method == "DragToAsync" && last.Args.Count >= 1)
            {
                // 第 1 引数をロケータ式として解析してみる。
                var destSteps = SplitChain(last.Args[0], out var destRoot);

                // 第 1 引数がロケータ式のときだけ分割する。
                if (destSteps.Count > 0)
                {
                    // 残りの引数（オプション）は元側に残す。
                    var options = last.Args.Skip(1).ToList();
                    IReadOnlyList<string>? srcArgs = null;
                    if (options.Count > 0)
                    {
                        srcArgs = options;
                    }

                    // ドラッグ元の行。
                    var srcAction = MakeAction(root, chain, last.Method, srcArgs, lineNo, raw,
                                               actionNameOverride: "ドラッグFrom");

                    // ドラッグ先の Page 変数（解析できなければ元と同じ Page）。
                    string destPage;
                    if (destRoot != null && pageVars.Contains(destRoot))
                    {
                        destPage = destRoot;
                    }
                    else
                    {
                        destPage = root;
                    }

                    // ドラッグ先の行。
                    var destAction = MakeAction(destPage, destSteps, last.Method, null, lineNo, raw,
                                                actionNameOverride: "ドラッグTo");

                    return new List<PlaywrightAction> { srcAction, destAction };
                }
            }

            // 通常は 1 件。引数があれば渡す。
            IReadOnlyList<string>? actionArgs = null;
            if (last.Args.Count > 0)
            {
                actionArgs = last.Args;
            }

            var action = MakeAction(root, chain, last.Method, actionArgs, lineNo, raw);
            return new List<PlaywrightAction> { action };
        }

        /// <summary>
        /// ロケータ/フレーム/デバイス列 + アクション名 から PlaywrightAction を 1 件組み立てる。
        /// </summary>
        private static PlaywrightAction MakeAction(string pageVar, IEnumerable<Step> chain, string actionMethod,
            IReadOnlyList<string>? actionArgs, int lineNo, string raw, bool isAssertion = false, bool negated = false,
            string? actionNameOverride = null)
        {
            // チェーンを Frame / Device / Locator に振り分ける。
            var frames = new List<Step>();
            var locators = new List<Step>();
            string? device = null;

            // ContentFrame（新 iframe API）の前後判定のため、インデックス走査する。
            var chainList = chain.ToList();
            for (int i = 0; i < chainList.Count; i++)
            {
                var s = chainList[i];

                // ContentFrame 自体は iframe 境界のマーカー。直前の要素を Frame 側へ移すので読み飛ばす。
                if (s.Method == "ContentFrame")
                {
                    continue;
                }

                // 次が ContentFrame なら、この要素は iframe を指すセレクタ → フレームへ。
                // 例: page.Locator("#f").ContentFrame.GetByText(...) の Locator("#f")。
                if (i + 1 < chainList.Count && chainList[i + 1].Method == "ContentFrame")
                {
                    frames.Add(s);
                    continue;
                }

                // FrameLocator/Frame 系はフレームへ。
                if (FrameMethods.Contains(s.Method))
                {
                    frames.Add(s);
                }
                // 引数なしの Mouse/Keyboard/Touchscreen はデバイスへ。
                else if (DeviceNames.Contains(s.Method) && s.Args.Count == 0)
                {
                    device = s.Method;
                }
                // それ以外は要素ロケータへ。
                else
                {
                    locators.Add(s);
                }
            }

            // 操作名（override 優先、無ければ日本語化）。
            string actionName;
            if (actionNameOverride != null)
            {
                actionName = actionNameOverride;
            }
            else
            {
                actionName = JapaneseActionName(actionMethod, isAssertion, negated);
            }

            // 説明コメントを組み立てる。
            var comment = BuildComment(frames, locators, device, actionMethod, actionName, actionArgs, isAssertion, negated);

            // 直接 SetInputFilesAsync("path") の場合はアップロード元パスを FilePath にも載せる
            // （FileChooser フローと表現を揃えるため）。
            string? filePath = null;
            if (actionMethod == "SetInputFilesAsync" && actionArgs != null && actionArgs.Count > 0)
            {
                // 単一引数はアンエスケープ、複数（または配列式）は各要素をアンエスケープして連結する。
                if (actionArgs.Count == 1)
                {
                    filePath = UnescapePathValue(actionArgs[0]);
                }
                else
                {
                    filePath = string.Join(", ", actionArgs.Select(UnescapePathValue));
                }
            }

            // 空のチェーンは null として保持する。
            IReadOnlyList<Step>? framesField = null;
            if (frames.Count > 0)
            {
                framesField = frames;
            }
            IReadOnlyList<Step>? locatorsField = null;
            if (locators.Count > 0)
            {
                locatorsField = locators;
            }

            return new PlaywrightAction
            {
                PageVariable = pageVar,
                Frames = framesField,
                Locators = locatorsField,
                Action = actionMethod,
                Arguments = actionArgs,
                Target = SelectTarget(locators),
                Device = device,
                IsAssertion = isAssertion,
                Negated = negated,
                ActionName = actionName,
                Comment = comment,
                FilePath = filePath,
                LineNumber = lineNo,
                RawLine = raw,
            };
        }

        // メソッド名 → 日本語の操作名（アサーションは "検証:"/"検証(否定):" を前置）。
        private static string JapaneseActionName(string method, bool isAssertion, bool negated)
        {
            // アサーションの場合。
            if (isAssertion)
            {
                // 既知の確認名、無ければメソッド名そのまま。
                string ja;
                if (AssertNameJa.TryGetValue(method, out var v))
                {
                    ja = v;
                }
                else
                {
                    ja = method;
                }

                // 否定かどうかでプレフィックスを変える。
                string prefix;
                if (negated)
                {
                    prefix = "検証(否定):";
                }
                else
                {
                    prefix = "検証:";
                }

                return prefix + ja;
            }

            // 通常操作: 既知の操作名、無ければメソッド名そのまま。
            if (ActionNameJa.TryGetValue(method, out var name))
            {
                return name;
            }
            return method;
        }

        /// <summary>「どの要素に何をしているか」の日本語コメントを組み立てる。</summary>
        private static string BuildComment(List<Step> frames, List<Step> locators, string? device,
            string actionMethod, string actionName, IReadOnlyList<string>? args, bool isAssertion, bool negated)
        {
            // フレーム部分の前置き（フレーム配下なら "フレーム[...]内の "）。
            string framePart = "";
            if (frames.Count > 0)
            {
                // 各フレームのセレクタ（引数があればそれ、無ければメソッド名）を集める。
                var sels = new List<string>();
                foreach (var f in frames)
                {
                    if (f.Args.Count > 0)
                    {
                        sels.Add(Unquote(f.Args[0]));
                    }
                    else
                    {
                        sels.Add(f.Method);
                    }
                }
                framePart = $"フレーム[{string.Join("/", sels)}]内の ";
            }

            // 主語（要素 or デバイス or ページ）。
            string subject = DescribeSubject(locators, device);

            // アサーションは状態フレーズで「〜であることを確認」とする。
            if (isAssertion)
            {
                return framePart + BuildAssertPredicate(subject, actionMethod, args, negated);
            }

            // 引数部分（あれば "（...）"）。
            string argPart = "";
            if (args != null)
            {
                argPart = $"（{string.Join(", ", args)}）";
            }

            // ページ直接操作（要素もデバイスも無い）は主語「ページ」を省いて操作名だけにする。
            // 例: "ページ を ページ遷移（…）" → "ページ遷移（…）"。
            if (locators.Count == 0 && device == null)
            {
                return $"{framePart}{actionName}{argPart}";
            }

            // 助詞を選ぶ。デバイス操作は「を」、入力/チェック系は「に」、それ以外は「を」。
            string particle;
            if (device != null)
            {
                particle = "を";
            }
            else if (ParticleNiMethods.Contains(actionMethod))
            {
                particle = "に";
            }
            else
            {
                particle = "を";
            }

            return $"{framePart}{subject} {particle} {actionName}{argPart}";
        }

        // アサーションの述語（「〜であることを確認」など）を主語の直後に付く形で組み立てる。
        private static string BuildAssertPredicate(string subject, string method, IReadOnlyList<string>? args, bool negated)
        {
            // 末尾のオプションオブジェクト（new(){ Timeout=... } 等）は文に含めない（値のみ使う）。
            var valueArgs = StripTrailingOptions(args);
            string argText = string.Join(", ", valueArgs);

            // 既知のアサーションは肯定/否定の文型から組み立てる。
            if (AssertTemplates.TryGetValue(method, out var tpl))
            {
                string body;
                if (negated)
                {
                    body = tpl.neg;
                }
                else
                {
                    body = tpl.pos;
                }

                // {arg} は値引数の連結、{arg0}/{arg1}/... は各値引数で置換する。
                body = body.Replace("{arg}", argText);
                for (int i = 0; i < valueArgs.Count; i++)
                {
                    body = body.Replace("{arg" + i + "}", valueArgs[i]);
                }
                return subject + body;
            }

            // 未知のアサーションは汎用形でフォールバックする。
            string ja;
            if (AssertNameJa.TryGetValue(method, out var v))
            {
                ja = v;
            }
            else
            {
                ja = method;
            }

            string argPart = "";
            if (valueArgs.Count > 0)
            {
                argPart = $"（{argText}）";
            }

            string tail;
            if (negated)
            {
                tail = "でないことを確認";
            }
            else
            {
                tail = "であることを確認";
            }

            return $"{subject} が {ja}{argPart}{tail}";
        }

        // 末尾のオプションオブジェクト引数（new(){...} / new Foo(){...}）を取り除いた値引数のみを返す。
        private static IReadOnlyList<string> StripTrailingOptions(IReadOnlyList<string>? args)
        {
            if (args == null)
            {
                return Array.Empty<string>();
            }

            int count = args.Count;
            while (count > 0 && IsOptionsArg(args[count - 1]))
            {
                count--;
            }

            if (count == args.Count)
            {
                return args;
            }
            return args.Take(count).ToList();
        }

        // "new() {...}" / "new Foo() {...}" 形式のオプションオブジェクトか判定する（配列 new[]{...} は値なので除外）。
        private static bool IsOptionsArg(string arg)
        {
            var t = arg.TrimStart();
            if (!t.StartsWith("new", StringComparison.Ordinal))
            {
                return false;
            }

            int brace = t.IndexOf('{');
            if (brace < 0)
            {
                return false;
            }

            // '{' より前に '(' があり '[' が無ければオプション（() を持つオブジェクト初期化子）。
            var head = t[..brace];
            return head.Contains("(") && !head.Contains("[");
        }

        // コメントの主語（デバイス / 要素 / ページ）を返す。
        private static string DescribeSubject(List<Step> locators, string? device)
        {
            // デバイス操作ならデバイス名（日本語）。
            if (device != null)
            {
                if (DeviceJa.TryGetValue(device, out var d))
                {
                    return d;
                }
                return device;
            }

            // ロケータが無ければページ。
            if (locators.Count == 0)
            {
                return "ページ";
            }

            // 末尾側から、修飾でない最初のロケータを主語にする。
            Step chosen = locators[^1];
            for (int i = locators.Count - 1; i >= 0; i--)
            {
                if (!ModifierMethods.Contains(locators[i].Method))
                {
                    chosen = locators[i];
                    break;
                }
            }

            return DescribeLocator(chosen);
        }

        // ロケータ 1 つを人間向けの日本語で表現する。
        private static string DescribeLocator(Step s)
        {
            // 第 1 引数（引用符を外したもの）。
            string first = "";
            if (s.Args.Count > 0)
            {
                first = Unquote(s.Args[0]);
            }

            // GetByRole は「ロール（日本語）」＋ Name オプションで表す。
            if (s.Method == "GetByRole")
            {
                // "AriaRole.Button" → "Button" → "ボタン"（未知ロールは英語のまま）。
                string roleRaw = first.Replace("AriaRole.", "");
                string roleJa;
                if (RoleJa.TryGetValue(roleRaw, out var rj))
                {
                    roleJa = rj;
                }
                else
                {
                    roleJa = roleRaw;
                }

                // 識別子は Name 優先、無ければ Description（アクセシブル説明, 1.60+）を使う。
                string name = OptionValue(s, "Name");
                if (name.Length == 0)
                {
                    name = OptionValue(s, "Description");
                }
                if (name.Length > 0)
                {
                    return $"「{name}」{roleJa}";
                }
                return roleJa;
            }

            // GetByLabel/Text/Placeholder 等は "「値」要素種別" で表す。
            if (ElementNoun.TryGetValue(s.Method, out var noun))
            {
                if (first.Length > 0)
                {
                    return $"「{first}」{noun}";
                }
                return noun;
            }

            // Locator（CSS/セレクタ）は "要素「セレクタ」"。
            if (s.Method == "Locator")
            {
                if (first.Length > 0)
                {
                    return $"要素「{first}」";
                }
                return "要素";
            }

            // その他の未知メソッドは "メソッド名「第1引数」"。
            if (first.Length > 0)
            {
                return $"{s.Method}「{first}」";
            }
            return s.Method;
        }

        /// <summary>
        /// ファイルパス用に文字列リテラルを実際の値へ復元する。
        /// 通常文字列は C# のエスケープ（\\, \" など）を解除、verbatim 文字列は "" を " に戻す。
        /// 文字列リテラルでなければ（配列式や変数など）そのまま返す。
        /// </summary>
        private static string UnescapePathValue(string raw)
        {
            var v = raw.Trim();

            // verbatim 文字列 @"..." は中身の "" だけ " に戻す（バックスラッシュは素のまま）。
            if (v.Length >= 3 && v.StartsWith("@\"", StringComparison.Ordinal) && v.EndsWith("\"", StringComparison.Ordinal))
            {
                var inner = v[2..^1];
                return inner.Replace("\"\"", "\"");
            }

            // 通常の文字列 "..." はエスケープを解除する。
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
            {
                return UnescapeCSharp(v[1..^1]);
            }

            // 文字列リテラルでなければそのまま。
            return v;
        }

        // C# 通常文字列のエスケープシーケンスを実文字へ変換する（\uXXXX 等の未知は素のまま）。
        private static string UnescapeCSharp(string s)
        {
            var sb = new StringBuilder();

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];

                // バックスラッシュでない、または末尾ならそのまま積む。
                if (c != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(c);
                    continue;
                }

                // 次の 1 文字を見てエスケープを解決する。
                char n = s[i + 1];
                i++;

                if (n == '\\')
                {
                    sb.Append('\\');
                }
                else if (n == '"')
                {
                    sb.Append('"');
                }
                else if (n == '\'')
                {
                    sb.Append('\'');
                }
                else if (n == 'n')
                {
                    sb.Append('\n');
                }
                else if (n == 't')
                {
                    sb.Append('\t');
                }
                else if (n == 'r')
                {
                    sb.Append('\r');
                }
                else if (n == '0')
                {
                    sb.Append('\0');
                }
                else if (n == 'b')
                {
                    sb.Append('\b');
                }
                else if (n == 'f')
                {
                    sb.Append('\f');
                }
                else if (n == 'v')
                {
                    sb.Append('\v');
                }
                else if (n == 'a')
                {
                    sb.Append('\a');
                }
                else
                {
                    // 未知のエスケープ（\uXXXX など）はそのまま残す。
                    sb.Append('\\');
                    sb.Append(n);
                }
            }

            return sb.ToString();
        }

        /// <summary>先頭・末尾の二重引用符を外す（@付き含む）。文字列リテラルでなければそのまま。</summary>
        private static string Unquote(string v)
        {
            v = v.Trim();

            // 逐語文字列の @ を落とす。
            if (v.StartsWith("@\"", StringComparison.Ordinal))
            {
                v = v[1..];
            }

            // 前後が " で囲まれていれば中身を返す。
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
            {
                return v[1..^1];
            }

            return v;
        }

        /// <summary>new(){ Name = "x", ... } 形式のオプションから指定プロパティの値を取り出す。</summary>
        private static string OptionValue(Step s, string prop)
        {
            foreach (var a in s.Args)
            {
                // '{' '}' で囲まれたオプション本体を探す。
                int brace = a.IndexOf('{');
                int end = a.LastIndexOf('}');
                if (brace < 0 || end <= brace)
                {
                    continue;
                }

                // "Name = \"x\"" のような代入をトップレベルのカンマで分割する。
                foreach (var assign in SplitTopLevel(a[(brace + 1)..end], ','))
                {
                    var kv = assign.Split('=', 2);

                    // プロパティ名が一致すれば値を返す。
                    if (kv.Length == 2 && kv[0].Trim() == prop)
                    {
                        return Unquote(kv[1]);
                    }
                }
            }

            return "";
        }

        /// <summary>
        /// "page.GetByRole(...).ClickAsync()" を root="page" と Step 列に分割する。
        /// 文字列リテラルと括弧の深さを追跡し、'.' / ',' の誤分割を防ぐ。
        /// </summary>
        private static List<Step> SplitChain(string expr, out string? root)
        {
            // root は呼び出し側に out で返す。
            root = null;
            var steps = new List<Step>();

            // トップレベルの '.' でセグメントに分ける。
            var segments = SplitTopLevel(expr, '.');
            if (segments.Count == 0)
            {
                return steps;
            }

            // 先頭セグメントが root。識別子でなければ解析対象外。
            root = segments[0].Trim();
            if (!IsIdentifier(root))
            {
                root = null;
                return steps;
            }

            // 2 番目以降をステップに変換する。
            for (int i = 1; i < segments.Count; i++)
            {
                var seg = segments[i].Trim();
                if (seg.Length == 0)
                {
                    continue;
                }
                steps.Add(ParseSegment(seg));
            }

            return steps;
        }

        /// <summary>
        /// "GetByRole(AriaRole.Button, new(){ Name = \"x\" })" を Step に変換する。
        /// 括弧の無い "First" 等はパラメータ無しのプロパティアクセスとして扱う。
        /// </summary>
        private static Step ParseSegment(string seg)
        {
            // '(' が無ければプロパティアクセス（引数なし）。
            int paren = seg.IndexOf('(');
            if (paren < 0)
            {
                return new Step(seg, Array.Empty<string>());
            }

            // メソッド名は '(' の前。
            var method = seg[..paren].Trim();

            // 括弧の中身を取り出す。
            var inner = ExtractParenContent(seg, paren);

            // 中身が空なら引数なし、あればトップレベルのカンマで分割。
            List<string> args;
            if (inner.Length == 0)
            {
                args = new List<string>();
            }
            else
            {
                args = SplitTopLevel(inner, ',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            }

            return new Step(method, args);
        }

        /// <summary>
        /// 要素を特定するロケータのメソッド名を選ぶ。Nth/First/Last/Filter などの修飾は飛ばし、
        /// 最も具体的な要素ロケータのメソッド名を採用する。ロケータが無ければ null。
        /// </summary>
        private static string? SelectTarget(List<Step> locators)
        {
            // ロケータが無ければ対象なし。
            if (locators.Count == 0)
            {
                return null;
            }

            // 末尾側から、修飾でない最初のロケータのメソッド名を返す。
            for (int i = locators.Count - 1; i >= 0; i--)
            {
                if (!ModifierMethods.Contains(locators[i].Method))
                {
                    return locators[i].Method;
                }
            }

            // すべて修飾だった場合は末尾を返す。
            return locators[^1].Method;
        }

        // openIdx の '(' から対応する ')' までの「中身」を返す（外側の括弧自体は含めない）。
        private static string ExtractParenContent(string seg, int openIdx)
        {
            int depth = 0;          // 丸括弧深さ
            bool inStr = false;     // 文字列内か
            bool inChar = false;    // 文字リテラル内か
            bool verbatim = false;  // @"" 逐語文字列か
            var sb = new StringBuilder();

            for (int i = openIdx; i < seg.Length; i++)
            {
                char c = seg[i];

                // 文字列の中。
                if (inStr)
                {
                    if (c == '\\' && !verbatim)
                    {
                        // エスケープ: 2 文字まとめて取り込む。
                        sb.Append(c);
                        if (i + 1 < seg.Length)
                        {
                            sb.Append(seg[++i]);
                        }
                        continue;
                    }
                    if (c == '"')
                    {
                        inStr = false;
                    }
                    sb.Append(c);
                    continue;
                }

                // 文字リテラルの中。
                if (inChar)
                {
                    if (c == '\\')
                    {
                        sb.Append(c);
                        if (i + 1 < seg.Length)
                        {
                            sb.Append(seg[++i]);
                        }
                        continue;
                    }
                    if (c == '\'')
                    {
                        inChar = false;
                    }
                    sb.Append(c);
                    continue;
                }

                // 文字列開始。
                if (c == '"')
                {
                    inStr = true;
                    verbatim = i > 0 && seg[i - 1] == '@';
                    sb.Append(c);
                    continue;
                }

                // 文字リテラル開始。
                if (c == '\'')
                {
                    inChar = true;
                    sb.Append(c);
                    continue;
                }

                // 開き括弧: 深さを上げる。最初の '(' 自体は中身に含めない。
                if (c == '(')
                {
                    depth++;
                    if (depth == 1)
                    {
                        continue;
                    }
                }

                // 閉じ括弧: 深さを下げる。対応する ')' で終了。
                if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                // 括弧の内側なら 1 文字積む。
                if (depth >= 1)
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 文字列・char・括弧（() [] {}）の深さを尊重してトップレベルの区切り文字で分割する。
        /// </summary>
        private static List<string> SplitTopLevel(string s, char delim)
        {
            var parts = new List<string>();  // 分割結果
            var sb = new StringBuilder();     // 現在のトークン
            int round = 0;                    // () 深さ
            int square = 0;                   // [] 深さ
            int curly = 0;                    // {} 深さ
            bool inStr = false;               // 文字列 "" の中か
            bool inChar = false;              // 文字 '' の中か
            bool verbatim = false;            // @"" 逐語文字列か

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];

                // 文字列の中。
                if (inStr)
                {
                    sb.Append(c);
                    if (c == '\\' && !verbatim)
                    {
                        // エスケープ: 次の 1 文字も取り込む。
                        if (i + 1 < s.Length)
                        {
                            sb.Append(s[++i]);
                        }
                    }
                    else if (c == '"')
                    {
                        inStr = false;
                    }
                    continue;
                }

                // 文字リテラルの中。
                if (inChar)
                {
                    sb.Append(c);
                    if (c == '\\')
                    {
                        if (i + 1 < s.Length)
                        {
                            sb.Append(s[++i]);
                        }
                    }
                    else if (c == '\'')
                    {
                        inChar = false;
                    }
                    continue;
                }

                // 文字列開始。
                if (c == '"')
                {
                    inStr = true;
                    verbatim = i > 0 && s[i - 1] == '@';
                    sb.Append(c);
                    continue;
                }

                // 文字リテラル開始。
                if (c == '\'')
                {
                    inChar = true;
                    sb.Append(c);
                    continue;
                }

                // 括弧の深さを更新する。
                if (c == '(')
                {
                    round++;
                }
                else if (c == ')')
                {
                    round--;
                }
                else if (c == '[')
                {
                    square++;
                }
                else if (c == ']')
                {
                    square--;
                }
                else if (c == '{')
                {
                    curly++;
                }
                else if (c == '}')
                {
                    curly--;
                }

                // トップレベル（どの括弧の中でもない）の区切り文字で分割する。
                if (c == delim && round == 0 && square == 0 && curly == 0)
                {
                    parts.Add(sb.ToString());
                    sb.Clear();
                    continue;
                }

                // 区切りでなければそのまま積む。
                sb.Append(c);
            }

            // 末尾に残ったトークンを追加する。
            if (sb.Length > 0)
            {
                parts.Add(sb.ToString());
            }

            return parts;
        }

        // C# 識別子として妥当か（先頭は英字/_、以降は英数字/_）。
        private static bool IsIdentifier(string s)
        {
            // 空は不可。
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }

            // 先頭は英字またはアンダースコア。
            if (!(char.IsLetter(s[0]) || s[0] == '_'))
            {
                return false;
            }

            // 2 文字目以降は英数字またはアンダースコア。
            for (int i = 1; i < s.Length; i++)
            {
                if (!(char.IsLetterOrDigit(s[i]) || s[i] == '_'))
                {
                    return false;
                }
            }

            return true;
        }

        // 先頭の "await " と末尾の ";" を剥がした行を返す。
        private static string StripAwaitAndSemicolon(string line)
        {
            // 先頭の await を落とす。
            if (line.StartsWith("await ", StringComparison.Ordinal))
            {
                line = line["await ".Length..].Trim();
            }

            // 末尾の余白とセミコロンを落とす。
            line = line.TrimEnd();
            if (line.EndsWith(";", StringComparison.Ordinal))
            {
                line = line[..^1].TrimEnd();
            }

            return line;
        }
    }
}
