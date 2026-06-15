// ============================================================
// LocatorResolver.cs
//
// Playwright .NET のLocatorを「文字列」だけで動的に生成するユーティリティ。
//
// 通常のPlaywrightコード:
//   page.GetByRole(AriaRole.Button, new() { Name = "送信" });
//
// このユーティリティを使った場合:
//   LocatorResolver.Resolve(page, "GetByRole", @"AriaRole.Button, new() { Name = ""送信"" }");
//
// メソッド名もパラメータも文字列なので、
// Excelや設定ファイルからテストデータを読み込んで
// 動的にLocatorを組み立てるような使い方ができる。
//
// ILocator を返すメソッド → Resolve()
// IFrameLocator を返すメソッド → ResolveFrame()
//
// ---- 対応している書き方 ----
//
// 1) メソッド名 + パラメータ文字列 (従来形式):
//      Resolve(page, "GetByRole", @"AriaRole.Button, new() { Name = ""送信"" }")
//
// 2) メソッドチェーン (methodName に式全体を書く):
//      Resolve(page, @"GetByRole(AriaRole.Button).Filter(new() { HasText = ""x"" }).First")
//      ResolveFrame(page, @"FrameLocator(""#f"").Nth(1)")
//
// 3) ILocator型の引数/オプション (And, Or, Has, HasNot) にはネスト式が書ける:
//      Resolve(locator, "Filter", @"new() { Has = GetByText(""OK"") }")
//      Resolve(locator, "And", "GetByRole(AriaRole.Button)")
//      Resolve(locator, "Filter", @"new() { Has = ""#sel"" }")   ← クォート文字列はセレクタ扱い
//    ネスト式はルートの IPage を起点に解決される。
//
// 4) 文字列リテラルは通常 ("...") と逐語的 (@"...") の両方に対応。
//    string型プロパティに Regex を渡すと "<名前>Regex" プロパティへ自動で振り替える
//    (例: Name = new Regex("送信.*") → NameRegex)。
//
// 5) 文字列引数のメソッド (GetByLabel, GetByText 等) は parameters が
//    空でも呼び出せる (空文字列 "" として扱う):
//      Resolve(page, "GetByLabel")                          → page.GetByLabel("")
//      Resolve(page, "GetByLabel", "new() { Exact = true }") → page.GetByLabel("", options)
//    enum や int が必須のメソッド (GetByRole, Nth 等) は空のままだとエラー。
//
// 6) Excel由来の汚れに耐性がある:
//      スマートクォート “...” / 全角クォート ＂...＂ → 通常の " として解釈
//      シングルクォート '...' / 全角イコール ＝      → 文字列リテラル / = として解釈
//      ゼロ幅スペース・BOM (U+200B / U+FEFF 等)     → 自動で除去
//      TRUE / FALSE / 全角スペース                   → 大文字小文字・空白の種類を問わない
//    オプション値はクォートなしの複数語テキストやセレクタも可
//      (new() { Name = 送信 ボタン } / new() { Has = #sel })。
//    オプション値に null リテラルも書ける (new() { Name = null })。
//    メソッド名・オプション名のtypoには「もしかして: GetByText ?」のような候補を提示する。
//
// 7) TryResolve / TryResolveFrame で、テスト実行前にExcel全行を一括検証できる:
//      if (!LocatorResolver.TryResolve(page, method, params, out var loc, out var error))
//          報告(error);  // 構文ミス・typo等の定義エラーが例外なしで得られる
//
// 8) EscapeText で、任意のテキストを安全にパラメータへ埋め込める:
//      Resolve(page, "GetByText", LocatorResolver.EscapeText(セルの値))
//    クォート (スマート “ ” / 全角 ＂ 含む)・バックスラッシュ・改行を
//    自動エスケープするため、テキストの中身を気にせず組み立てられる。
//
// 入力ミス (typo・型不一致・閉じ忘れ等) は黙ってスキップせず
// ArgumentException / FormatException で明確に報告する。
// ============================================================

using Microsoft.Playwright;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// 文字列のメソッド名とC#風パラメータ文字列から
/// リフレクション(実行時の型情報)を使って
/// ILocator または IFrameLocator を動的に生成するクラス。
///
///   Resolve()      → ILocator を返す (GetByRole, GetByText, Filter, And, Or 等)
///   ResolveFrame() → IFrameLocator を返す (FrameLocator, First, Last, Nth)
/// </summary>
public static class LocatorResolver
{
    // ============================================================
    //  公開メソッド — Resolve (ILocator を返す)
    // ============================================================

    /// <summary>
    /// IPage を起点にLocatorを取得する。
    /// 例: Resolve(page, "GetByText", @"""Hello""")
    ///     Resolve(page, @"GetByRole(AriaRole.Button).Nth(2)")  ← チェーン構文
    /// </summary>
    /// <param name="page">Playwrightのページオブジェクト</param>
    /// <param name="methodName">呼び出すメソッド名、またはチェーン式全体</param>
    /// <param name="parameters">C#風のパラメータ文字列 (チェーン式の場合は指定不可)</param>
    /// <returns>生成されたILocator</returns>
    public static ILocator Resolve(IPage page, string methodName, string? parameters = null)
    {
        return (ILocator)ResolveCore(page, typeof(ILocator), methodName, parameters);
    }

    /// <summary>
    /// ILocator を起点に、さらに絞り込んだLocatorを取得する。
    /// 例: Resolve(locator, "Filter", @"new() { HasText = ""active"" }")
    /// </summary>
    public static ILocator Resolve(ILocator locator, string methodName, string? parameters = null)
    {
        return (ILocator)ResolveCore(locator, typeof(ILocator), methodName, parameters);
    }

    /// <summary>
    /// IFrameLocator を起点にLocatorを取得する。
    /// iframe内の要素を検索する場合に使う。
    /// 例: Resolve(frameLocator, "GetByRole", @"AriaRole.Button")
    /// </summary>
    public static ILocator Resolve(IFrameLocator frameLocator, string methodName, string? parameters = null)
    {
        return (ILocator)ResolveCore(frameLocator, typeof(ILocator), methodName, parameters);
    }

    // ============================================================
    //  公開メソッド — ResolveFrame (IFrameLocator を返す)
    // ============================================================

    /// <summary>
    /// IPage を起点にFrameLocatorを取得する。
    /// 例: ResolveFrame(page, "FrameLocator", @"""#my-iframe""")
    ///     ResolveFrame(page, @"FrameLocator(""#f"").Nth(1)")  ← チェーン構文
    /// </summary>
    public static IFrameLocator ResolveFrame(IPage page, string methodName, string? parameters = null)
    {
        return (IFrameLocator)ResolveCore(page, typeof(IFrameLocator), methodName, parameters);
    }

    /// <summary>
    /// ILocator を起点にFrameLocatorを取得する。
    /// 例: ResolveFrame(locator, "FrameLocator", @"""iframe.child""")
    /// </summary>
    public static IFrameLocator ResolveFrame(ILocator locator, string methodName, string? parameters = null)
    {
        return (IFrameLocator)ResolveCore(locator, typeof(IFrameLocator), methodName, parameters);
    }

    /// <summary>
    /// IFrameLocator を起点に別のFrameLocatorを取得する。
    /// ネストされたiframeや、First/Last/Nthでの絞り込みに使う。
    ///
    /// 例:
    ///   ResolveFrame(frame, "FrameLocator", @"""#inner-iframe""")  — ネストiframe
    ///   ResolveFrame(frame, "First")                                — 最初のiframe
    ///   ResolveFrame(frame, "Nth", "2")                             — 3番目のiframe
    /// </summary>
    public static IFrameLocator ResolveFrame(IFrameLocator frameLocator, string methodName, string? parameters = null)
    {
        return (IFrameLocator)ResolveCore(frameLocator, typeof(IFrameLocator), methodName, parameters);
    }

    // ============================================================
    //  公開メソッド — TryResolve / TryResolveFrame (非例外版)
    // ============================================================
    //
    // Excelの全行をテスト実行前に一括検証する用途のためのAPI。
    // 定義エラー (構文ミス・メソッド名typo・型不一致等) は例外でなく
    // false + エラーメッセージで返す。
    // Playwright実行時の例外 (要素が見つからない等) はそのまま伝播する。

    /// <summary>Resolve の非例外版。定義エラーなら false とエラーメッセージを返す。</summary>
    public static bool TryResolve(IPage page, string methodName, string? parameters,
        [NotNullWhen(true)] out ILocator? locator, [NotNullWhen(false)] out string? error)
    {
        return TryCore(() => Resolve(page, methodName, parameters), out locator, out error);
    }

    /// <summary>Resolve の非例外版。定義エラーなら false とエラーメッセージを返す。</summary>
    public static bool TryResolve(ILocator target, string methodName, string? parameters,
        [NotNullWhen(true)] out ILocator? locator, [NotNullWhen(false)] out string? error)
    {
        return TryCore(() => Resolve(target, methodName, parameters), out locator, out error);
    }

    /// <summary>Resolve の非例外版。定義エラーなら false とエラーメッセージを返す。</summary>
    public static bool TryResolve(IFrameLocator frameLocator, string methodName, string? parameters,
        [NotNullWhen(true)] out ILocator? locator, [NotNullWhen(false)] out string? error)
    {
        return TryCore(() => Resolve(frameLocator, methodName, parameters), out locator, out error);
    }

    /// <summary>ResolveFrame の非例外版。定義エラーなら false とエラーメッセージを返す。</summary>
    public static bool TryResolveFrame(IPage page, string methodName, string? parameters,
        [NotNullWhen(true)] out IFrameLocator? frameLocator, [NotNullWhen(false)] out string? error)
    {
        return TryCore(() => ResolveFrame(page, methodName, parameters), out frameLocator, out error);
    }

    /// <summary>ResolveFrame の非例外版。定義エラーなら false とエラーメッセージを返す。</summary>
    public static bool TryResolveFrame(ILocator target, string methodName, string? parameters,
        [NotNullWhen(true)] out IFrameLocator? frameLocator, [NotNullWhen(false)] out string? error)
    {
        return TryCore(() => ResolveFrame(target, methodName, parameters), out frameLocator, out error);
    }

    /// <summary>ResolveFrame の非例外版。定義エラーなら false とエラーメッセージを返す。</summary>
    public static bool TryResolveFrame(IFrameLocator frame, string methodName, string? parameters,
        [NotNullWhen(true)] out IFrameLocator? frameLocator, [NotNullWhen(false)] out string? error)
    {
        return TryCore(() => ResolveFrame(frame, methodName, parameters), out frameLocator, out error);
    }

    /// <summary>
    /// 任意のテキストを、parameters に安全に埋め込める文字列リテラルへ変換する。
    /// クォート文字 (スマートクォート “ ” / 全角 ＂ / シングル ' 含む)、
    /// バックスラッシュ、改行・タブをエスケープする。
    ///
    /// Excelの値からパラメータ文字列を組み立てるコード向け:
    ///   LocatorResolver.Resolve(page, "GetByText",
    ///       LocatorResolver.EscapeText(セルの値) + ", new() { Exact = true }");
    /// </summary>
    /// <param name="text">埋め込みたい生テキスト</param>
    /// <returns>ダブルクォートで囲まれたエスケープ済みリテラル</returns>
    public static string EscapeText(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var sb = new StringBuilder(text.Length + 8);
        sb.Append('"');

        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                default:
                    if (ParameterParser.IsQuoteChar(c))
                    {
                        sb.Append('\\');
                    }
                    sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Try系の共通処理。定義エラー
    /// (ArgumentException / FormatException / InvalidOperationException) だけを
    /// 捕捉し、それ以外 (Playwright実行時例外等) はそのまま伝播させる。
    /// InvalidOperationException は「メソッドが null を返した」等の内部不整合で、
    /// これも一括検証時には例外でなくエラー文字列として返したいため含める。
    /// </summary>
    private static bool TryCore<T>(Func<T> resolve,
        [NotNullWhen(true)] out T? result, [NotNullWhen(false)] out string? error)
        where T : class
    {
        try
        {
            result = resolve();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            result = null;
            error = ex.Message;
            return false;
        }
    }

    // ============================================================
    //  内部処理 — エントリポイント
    // ============================================================

    /// <summary>
    /// Resolve / ResolveFrame 共通の内部処理。
    /// methodName が "GetByRole(...)" のようなチェーン式なら分解して順に評価し、
    /// 単独のメソッド名なら1段だけ解決する。
    /// </summary>
    private static object ResolveCore(object target, Type returnType, string methodName, string? parameters)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        // メソッド名の前後空白と不可視文字を除去 (Excel等から読み込む際の混入対策)
        if (methodName == null)
        {
            methodName = "";
        }
        methodName = ParameterParser.StripInvisible(methodName).Trim();

        if (methodName.Length == 0)
        {
            throw new ArgumentException("メソッド名が空です。");
        }

        // '(' か '.' を含む場合はチェーン式とみなす
        // (通常のメソッド名にこれらの文字が含まれることはない)
        if (methodName.Contains('(') || methodName.Contains('.'))
        {
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                throw new ArgumentException(
                    $"チェーン構文 '{methodName}' と parameters ('{parameters}') は同時に指定できません。"
                    + " 引数はチェーン式の中に書いてください。");
            }

            var chained = EvaluateChain(target, target, methodName);

            // 最終結果が要求された型 (ILocator / IFrameLocator) かチェック
            if (!returnType.IsInstanceOfType(chained))
            {
                throw new ArgumentException(
                    $"チェーン '{methodName}' の結果は {KindName(chained)} ですが、"
                    + $" {returnType.Name} が必要です。");
            }

            return chained;
        }

        return ResolveSegment(target, target, methodName, parameters, returnType);
    }

    /// <summary>
    /// チェーン式 "A(...).B(...).C" を分解し、target を起点に順番に評価する。
    /// 中間結果は ILocator / IFrameLocator のどちらでもよい
    /// (FrameLocator をまたぐチェーンに対応するため)。
    /// </summary>
    /// <param name="root">ネスト式 (Has 等) の解決に使うルートオブジェクト</param>
    /// <param name="target">チェーンの起点</param>
    /// <param name="expression">チェーン式全体</param>
    private static object EvaluateChain(object root, object target, string expression)
    {
        var current = target;

        foreach (var (name, args) in ParameterParser.ParseChain(expression))
        {
            // returnType = null は「ILocator か IFrameLocator のどちらでも可」
            current = ResolveSegment(root, current, name, args, returnType: null);
        }

        return current;
    }

    // ============================================================
    //  内部処理 — 1段分の解決 (プロパティ or メソッド呼び出し)
    // ============================================================

    /// <summary>
    /// 1つのメソッド呼び出し (またはプロパティ参照) を解決する。
    /// 1. プロパティかどうかチェック (First, Last, Owner 等)
    /// 2. パラメータ文字列をパース
    /// 3. オーバーロードを選択
    /// 4. 引数を組み立てて実行
    /// </summary>
    /// <param name="root">ネスト式の解決起点 (公開メソッドに渡された元のオブジェクト)</param>
    /// <param name="target">このセグメントを呼び出す対象</param>
    /// <param name="methodName">メソッド名 (チェーン式は不可)</param>
    /// <param name="parameters">C#風パラメータ文字列</param>
    /// <param name="returnType">期待する戻り値型。null は ILocator/IFrameLocator どちらでも可</param>
    private static object ResolveSegment(
        object root,
        object target,
        string methodName,
        string? parameters,
        Type? returnType)
    {
        var interfaceType = GetInterfaceType(target);

        // 戻り値型が受け入れ可能かの判定関数
        bool Acceptable(Type rt)
        {
            if (returnType == null)
            {
                // returnType 未指定なら ILocator / IFrameLocator のどちらでも受け入れる
                return AcceptsAnyLocator(rt);
            }

            return returnType.IsAssignableFrom(rt);
        }

        // -----------------------------------------------
        // ステップ1: プロパティかどうかチェック (First, Last, Owner, ContentFrame)
        // -----------------------------------------------

        var prop = GetAllProperties(interfaceType)
            .FirstOrDefault(p =>
                p.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && Acceptable(p.PropertyType));

        if (prop != null)
        {
            // プロパティは引数を取れないので、指定されていたらエラーにする
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                throw new ArgumentException(
                    $"'{prop.Name}' はプロパティのため引数 '{parameters}' は指定できません。");
            }

            // メソッド呼び出し側 (下のステップ6) と同様に null を明示的に検出する。
            // ここで握りつぶすと、後続の KindName / GetInterfaceType が
            // null を受け取って NRE になり、Try系の検証を素通りしてしまう。
            var propValue = prop.GetValue(target);
            if (propValue == null)
            {
                throw new InvalidOperationException($"'{prop.Name}' が null を返しました。");
            }

            return propValue;
        }

        // -----------------------------------------------
        // ステップ2: パラメータ文字列をパースする
        // -----------------------------------------------

        // 例: @"""Hello"", new() { Exact = true }"
        //   → primary = "Hello" (文字列)
        //   → options = { "Exact": true } (辞書)
        var (primary, options) = ParameterParser.Parse(parameters);

        // -----------------------------------------------
        // ステップ3: メソッド候補を検索する
        // -----------------------------------------------

        var candidates = GetAllMethods(interfaceType)
            .Where(m =>
                m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && Acceptable(m.ReturnType))
            .ToList();

        if (candidates.Count == 0)
        {
            // typoの可能性が高いので、近い名前があれば候補として提示する
            var hint = SuggestNames(interfaceType, methodName, Acceptable);

            string notFound;
            if (returnType == null)
            {
                notFound = $"'{methodName}' は {interfaceType.Name} に Locator を返すメンバーとして存在しません。";
            }
            else
            {
                notFound = $"'{methodName}' は {interfaceType.Name} に"
                    + $" {returnType.Name} を返すメソッドとして存在しません。";
            }

            throw new ArgumentException(notFound + hint);
        }

        // -----------------------------------------------
        // ステップ4: オーバーロードを選択する
        // -----------------------------------------------

        var method = SelectOverload(candidates, primary, methodName, interfaceType, parameters);

        // -----------------------------------------------
        // ステップ5: 引数配列を組み立てる
        // -----------------------------------------------

        var args = BuildArguments(method, primary, options, root, methodName, parameters);

        // -----------------------------------------------
        // ステップ6: メソッドを実行して結果を返す
        // -----------------------------------------------

        object? result;
        try
        {
            result = method.Invoke(target, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // リフレクション呼び出しの例外ラップを剥がし、
            // Playwright側の例外を元のスタックトレース付きでそのまま伝える
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // 到達しない (Throwが必ず例外を投げる)
        }

        if (result == null)
        {
            throw new InvalidOperationException($"'{methodName}' が null を返しました。");
        }

        return result;
    }

    /// <summary>
    /// オーバーロード候補の中から、primary の型に最も適合するものを選ぶ。
    /// 各候補の「options以外の第1引数」と primary の相性をスコア化し、最小を採用する。
    /// </summary>
    private static MethodInfo SelectOverload(
        List<MethodInfo> candidates,
        object? primary,
        string methodName,
        Type interfaceType,
        string? parameters)
    {
        MethodInfo? best = null;
        var bestScore = int.MaxValue;
        var bestParamCount = int.MaxValue;
        var bestSig = "";

        // リフレクションの列挙順はランタイム/バージョン間で安定保証がないため、
        // スコア同点時は (引数の少なさ → シグネチャの序数順) で決定的に選ぶ。
        static string SigKey(ParameterInfo[] ps) =>
            string.Join(",", ps.Select(p => p.ParameterType.FullName));

        foreach (var m in candidates)
        {
            // GetParameters() は呼ぶたびに配列をアロケートし、ランタイムでも
            // キャッシュされないため、候補ごとに一度だけ取得して使い回す
            // (データ駆動で数千回呼ばれてもアロケーションを最小に保つ)。
            var mparams = m.GetParameters();

            // Options型以外のパラメータ (= primaryで埋めるべき引数)
            var required = mparams
                .Where(p => !IsOptionsType(p.ParameterType))
                .ToArray();

            int score;

            if (primary == null)
            {
                if (required.Length == 0)
                {
                    // 必須引数のないオーバーロードが最優先
                    score = 0;
                }
                else if (required.Length == 1 && required.All(p =>
                {
                    // Nullable<T> なら中身の型 T を取り出してから string かどうかを見る
                    var paramType = Nullable.GetUnderlyingType(p.ParameterType);
                    if (paramType == null)
                    {
                        paramType = p.ParameterType;
                    }
                    return paramType == typeof(string);
                }))
                {
                    // 必須引数が string 1個だけなら空文字列で埋めて呼び出せる
                    // (GetByLabel等でExcelのセルが空のケースに対応)。
                    // BuildArguments の空文字埋めは1個までなので、必須stringが
                    // 2個以上あるオーバーロードはここで候補から外し、
                    // 「選択は通るが実行直前で失敗」する不整合を防ぐ。
                    score = 50 + required.Length;
                }
                else
                {
                    // enumやint等は空から補えないので候補外
                    continue;
                }
            }
            else
            {
                // primaryがあるのに受け取る引数がないオーバーロードは除外
                if (required.Length == 0)
                {
                    continue;
                }

                score = ScorePrimary(required[0].ParameterType, primary);
                if (score < 0)
                {
                    continue; // 変換不可能な組み合わせ
                }

                // 必須引数が2つ以上あるオーバーロードは埋められないため強く避ける
                score = score * 10 + (required.Length - 1) * 1000;
            }

            var paramCount = mparams.Length;

            var better = best == null || score < bestScore;
            string? sig = null;
            if (!better && score == bestScore)
            {
                // 同点 → 引数の少ない方、それも同じならシグネチャ序数順で決める
                if (paramCount < bestParamCount)
                {
                    better = true;
                }
                else if (paramCount == bestParamCount)
                {
                    sig = SigKey(mparams);
                    if (string.CompareOrdinal(sig, bestSig) < 0)
                    {
                        better = true;
                    }
                }
            }

            if (better)
            {
                bestScore = score;
                bestParamCount = paramCount;
                bestSig = sig ?? SigKey(mparams);
                best = m;
            }
        }

        if (best != null)
        {
            return best;
        }

        // 候補が1つも残らなかった → 原因別にメッセージを変える
        if (primary == null)
        {
            throw new ArgumentException(
                $"'{methodName}' には必須引数があります。parameters を指定してください。");
        }

        if (primary is Regex)
        {
            throw new ArgumentException(
                $"'{methodName}' に Regex を受け取るオーバーロードは {interfaceType.Name} にありません。");
        }

        throw new ArgumentException(
            $"'{methodName}' に '{parameters}' を適用できるオーバーロードが見つかりません。");
    }

    /// <summary>
    /// primary の値と引数型の相性をスコア化する (小さいほど良い、負は不可)。
    /// 例: 文字列はstring引数を最優先、次にenum (AriaRole等)、数値、ILocator (ネスト式) の順。
    /// </summary>
    private static int ScorePrimary(Type parameterType, object primary)
    {
        // Nullable<T> なら中身の型 T で判定する
        var pt = Nullable.GetUnderlyingType(parameterType);
        if (pt == null)
        {
            pt = parameterType;
        }

        // Regex は Regex 引数にのみ適合する
        if (primary is Regex)
        {
            if (pt == typeof(Regex))
            {
                return 0;
            }
            return -1;
        }

        // int: int を最優先、次に浮動小数点、最後に string
        if (primary is int)
        {
            if (pt == typeof(int))
            {
                return 0;
            }
            if (pt == typeof(long) || pt == typeof(double)
                || pt == typeof(float) || pt == typeof(decimal))
            {
                return 1;
            }
            if (pt == typeof(string))
            {
                return 2;
            }
            return -1;
        }

        // double: 浮動小数点を最優先、次に string
        if (primary is double)
        {
            if (pt == typeof(double) || pt == typeof(float))
            {
                return 0;
            }
            if (pt == typeof(string))
            {
                return 2;
            }
            return -1;
        }

        // bool: bool を最優先、次に string
        if (primary is bool)
        {
            if (pt == typeof(bool))
            {
                return 0;
            }
            if (pt == typeof(string))
            {
                return 2;
            }
            return -1;
        }

        // 文字列の場合、通常は string 引数を最優先するが、
        // "GetByText(...)" のようなチェーン形 (ネスト式) に見える文字列は
        // ILocator 引数のオーバーロードを優先する
        // (Locator(ILocator, options) にネスト式を渡せるようにするため。
        //  チェーン形は有効なPlaywrightセレクタにならないので誤爆しない)
        if (primary is string s)
        {
            var looksLikeChain = ChainHeadPattern.IsMatch(s);

            if (pt == typeof(string))
            {
                // チェーン形に見える文字列は string より ILocator を優先したいので
                // string への適合度をあえて下げる
                if (looksLikeChain)
                {
                    return 2;
                }
                return 0;
            }
            if (pt.IsEnum)
            {
                return 1;
            }
            if (pt == typeof(int) || pt == typeof(long))
            {
                return 2;
            }
            if (pt == typeof(double) || pt == typeof(float) || pt == typeof(decimal))
            {
                return 3;
            }
            if (typeof(ILocator).IsAssignableFrom(pt))
            {
                // And/Or/Locator (ネスト式として解決)
                if (looksLikeChain)
                {
                    return 1;
                }
                return 4;
            }
            if (pt == typeof(bool))
            {
                return 5;
            }
            if (pt == typeof(Regex))
            {
                // string→Regex変換は最終手段
                return 8;
            }
            return -1;
        }

        return -1;
    }

    /// <summary>
    /// メソッドの引数配列を組み立てる。
    /// Options型の引数には BuildOptions の結果を、
    /// それ以外の最初の引数には primary を型変換して割り当てる。
    /// 必須引数が埋まらない場合はエラーにする。
    /// </summary>
    private static object?[] BuildArguments(
        MethodInfo method,
        object? primary,
        Dictionary<string, object> options,
        object root,
        string methodName,
        string? parameters)
    {
        var methodParams = method.GetParameters();
        var args = new object?[methodParams.Length];

        var primaryConsumed = primary == null;
        var optionsConsumed = options.Count == 0;
        var emptyFilled = false;

        for (var i = 0; i < methodParams.Length; i++)
        {
            var mp = methodParams[i];

            if (IsOptionsType(mp.ParameterType))
            {
                args[i] = BuildOptions(mp.ParameterType, options, root);
                optionsConsumed = true;
            }
            else if (!primaryConsumed)
            {
                try
                {
                    args[i] = CoerceTo(mp.ParameterType, primary!, root);
                }
                catch (ArgumentException)
                {
                    throw; // 既にコンテキスト付きのメッセージなのでそのまま
                }
                catch (Exception ex)
                {
                    throw new ArgumentException(
                        $"'{methodName}' の引数 '{Display(primary!)}' を"
                        + $" {mp.ParameterType.Name} に変換できません: {ex.Message}", ex);
                }
                primaryConsumed = true;
            }
            else if (primary == null && mp.ParameterType == typeof(string) && !emptyFilled)
            {
                // パラメータ未指定の文字列引数は空文字列で埋める
                // (例: Resolve(page, "GetByLabel") → page.GetByLabel(""))
                // ただし埋めるのは1個目だけ。2個以上の必須string引数を持つメソッドを
                // 黙って全部 "" で埋めると意図しない呼び出しになるため、2個目以降は
                // 下の else に落として明示エラーにする。
                args[i] = "";
                emptyFilled = true;
            }
            else
            {
                // primary は使用済みなのに、まだ必須引数が残っている
                throw new ArgumentException(
                    $"'{methodName}' の引数 '{mp.Name}' ({mp.ParameterType.Name}) に渡す値がありません。"
                    + $" parameters: '{parameters}'");
            }
        }

        if (!primaryConsumed)
        {
            throw new ArgumentException(
                $"'{methodName}' は引数 '{Display(primary!)}' を受け取れません。");
        }

        if (!optionsConsumed)
        {
            throw new ArgumentException(
                $"'{methodName}' はオプション (new() {{ ... }}) を受け取りません。 parameters: '{parameters}'");
        }

        return args;
    }

    // ============================================================
    //  型変換ユーティリティ
    // ============================================================

    /// <summary>
    /// 指定された型が「Options型」かどうかを判定する。
    /// PlaywrightのOptions型はクラス名が "Options" で終わる
    /// (例: PageGetByRoleOptions, LocatorFilterOptions)。
    /// </summary>
    private static bool IsOptionsType(Type t)
    {
        return t.IsClass
            && t != typeof(string)
            && t != typeof(Regex)
            && t.Name.EndsWith("Options", StringComparison.Ordinal)
            && (t.Namespace?.StartsWith("Microsoft.Playwright", StringComparison.Ordinal) ?? false);
    }

    /// <summary>
    /// パース済みの値を、メソッドが期待する型に変換する。
    ///
    /// 例:
    ///   CoerceTo(typeof(AriaRole),  "Button")            → AriaRole.Button
    ///   CoerceTo(typeof(int),       "2")                 → 2
    ///   CoerceTo(typeof(ILocator),  "GetByText(\"OK\")") → ネスト式を解決してILocator
    ///   CoerceTo(typeof(ILocator),  "#sel")              → page.Locator("#sel")
    ///
    /// 数値変換はカルチャ非依存 (InvariantCulture)。
    /// </summary>
    /// <param name="targetType">変換先の型</param>
    /// <param name="value">パース済みの値 (string, int, double, bool, Regex, LocatorExpr)</param>
    /// <param name="root">ネスト式の解決起点</param>
    private static object CoerceTo(Type targetType, object value, object root)
    {
        // Nullable<T> の場合、中身の型 T を取り出す (例: bool? → bool)
        var t = Nullable.GetUnderlyingType(targetType);
        if (t == null)
        {
            t = targetType;
        }

        // 既に正しい型なら変換不要
        if (t.IsInstanceOfType(value))
        {
            return value;
        }

        // ILocator型 (And/Or の引数、Has/HasNot オプション):
        // ネスト式またはセレクタ文字列として解決する
        if (typeof(ILocator).IsAssignableFrom(t))
        {
            switch (value)
            {
                case LocatorExpr expr:
                    return ResolveNestedLocator(root, expr.Text);
                case string selector:
                    return ResolveNestedLocator(root, selector);
                default:
                    throw new ArgumentException(
                        $"値 '{Display(value)}' を ILocator に変換できません。"
                        + @" ネスト式 (例: GetByText(""OK"")) かセレクタ文字列を指定してください。");
            }
        }

        // ネスト式マーカーが ILocator 以外の型に来た場合:
        // string なら "Submit (2)" のような括弧付きテキストの誤検出なので
        // 元のテキストをそのまま使う。それ以外の型はエラー
        if (value is LocatorExpr le)
        {
            if (t == typeof(string))
            {
                return le.Text;
            }

            throw new ArgumentException(
                $"ネストしたLocator式 '{le.Text}' は {t.Name} 型には使えません。");
        }

        // 値が文字列の場合、目的の型に応じて変換する
        if (value is string s)
        {
            // Enum型の場合: "AriaRole.Button" → AriaRole.Button
            if (t.IsEnum)
            {
                // "型名.値" 形式なら、ドット以降を使う
                var enumStr = s;
                if (s.Contains('.'))
                {
                    enumStr = s.Split('.').Last();
                }

                try
                {
                    return Enum.Parse(t, enumStr, ignoreCase: true);
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException(
                        $"'{s}' は {t.Name} の値として解釈できません。", ex);
                }
            }

            // 数値・bool は全角→半角に正規化してからパースする。
            // (Excelで全角入力された "２" や "ＴＲＵＥ" にも耐えるため。
            //  数値トークンの読み取りは ASCII 限定なので、全角はここへ流れてくる)
            if (t == typeof(int))
            {
                return int.Parse(NormalizeForParse(s), NumberStyles.Integer, CultureInfo.InvariantCulture);
            }
            if (t == typeof(long))
            {
                return long.Parse(NormalizeForParse(s), NumberStyles.Integer, CultureInfo.InvariantCulture);
            }
            if (t == typeof(double))
            {
                return double.Parse(NormalizeForParse(s), CultureInfo.InvariantCulture);
            }
            if (t == typeof(float))
            {
                return float.Parse(NormalizeForParse(s), CultureInfo.InvariantCulture);
            }
            if (t == typeof(decimal))
            {
                return decimal.Parse(NormalizeForParse(s), CultureInfo.InvariantCulture);
            }
            if (t == typeof(bool))
            {
                return bool.Parse(NormalizeForParse(s));
            }
            if (t == typeof(Regex))
            {
                return new Regex(s, RegexOptions.None, ParameterParser.RegexMatchTimeout);
            }

            // どれにも該当しなければ文字列のまま返す
            return s;
        }

        // 数値→文字列など、残りはカルチャ非依存の汎用変換に任せる
        return Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// パース済みの辞書から、PlaywrightのOptionsオブジェクトを
    /// リフレクションで組み立てる。
    ///
    /// 例:
    ///   optionsType = typeof(PageGetByRoleOptions)
    ///   parsed      = { "Name": "送信", "Exact": true }
    ///   → new PageGetByRoleOptions { Name = "送信", Exact = true }
    ///
    /// 不明なプロパティ名や変換できない値は黙ってスキップせず例外にする。
    /// string型プロパティにRegexが来たら "<名前>Regex" プロパティへ振り替える。
    /// </summary>
    /// <returns>構築されたOptionsオブジェクト (設定項目がなければnull)</returns>
    private static object? BuildOptions(
        Type optionsType,
        Dictionary<string, object> parsed,
        object root)
    {
        // 設定するプロパティがなければnullを返す (Optionsを渡さない)
        if (parsed.Count == 0)
        {
            return null;
        }

        var props = GetOptionsProperties(optionsType);
        var instance = Activator.CreateInstance(optionsType)!;

        foreach (var (key, value) in parsed)
        {
            // プロパティ名の存在チェック (typoはここで検出される)
            if (!props.TryGetValue(key, out var p))
            {
                // 編集距離2以内の近い名前があれば「もしかして」、なければ全列挙
                var lower = key.ToLowerInvariant();
                var hints = props.Keys
                    .Select(n => (Name: n, Dist: EditDistance(n.ToLowerInvariant(), lower)))
                    .Where(x => x.Dist <= 2)
                    .OrderBy(x => x.Dist)
                    .ThenBy(x => x.Name, StringComparer.Ordinal)
                    .Select(x => x.Name)
                    .Take(3)
                    .ToList();

                string suggestion;
                if (hints.Count > 0)
                {
                    suggestion = $" もしかして: {string.Join(", ", hints)} ?";
                }
                else
                {
                    suggestion = $" 指定可能: {string.Join(", ", props.Keys.OrderBy(n => n, StringComparer.Ordinal))}";
                }

                throw new ArgumentException(
                    $"オプション '{key}' は {optionsType.Name} に存在しません。" + suggestion);
            }

            // null リテラルはプロパティを明示的に null にする (未設定と同じ)
            if (value is NullLiteral)
            {
                p.SetValue(instance, null);
                continue;
            }

            var pt = Nullable.GetUnderlyingType(p.PropertyType);
            if (pt == null)
            {
                pt = p.PropertyType;
            }

            // Regex値がRegex以外の型のプロパティに来た場合、
            // "<名前>Regex" という対のプロパティがあればそちらに振り替える
            // (例: Name = new Regex(...) → NameRegex、HasText = new Regex(...) → HasTextRegex)
            if (value is Regex && pt != typeof(Regex)
                && props.TryGetValue(p.Name + "Regex", out var regexProp))
            {
                // 振り替え先プロパティの (Nullable を剥がした) 型が Regex のときだけ採用する
                var regexPropType = Nullable.GetUnderlyingType(regexProp.PropertyType);
                if (regexPropType == null)
                {
                    regexPropType = regexProp.PropertyType;
                }

                if (regexPropType == typeof(Regex))
                {
                    p = regexProp;
                }
            }

            try
            {
                p.SetValue(instance, CoerceTo(p.PropertyType, value, root));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(
                    $"オプション '{p.Name}' に '{Display(value)}' を設定できません: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new ArgumentException(
                    $"オプション '{p.Name}' ({p.PropertyType.Name}型) に"
                    + $" '{Display(value)}' を設定できません: {ex.Message}", ex);
            }
        }

        return instance;
    }

    // ============================================================
    //  ネストしたLocator式の解決
    // ============================================================

    /// <summary>
    /// ネスト式の先頭が "メソッド名(" の形かどうかの判定用。
    ///
    /// 注意: このパターンに一致する文字列はネスト式 (チェーン評価) とみなされ、
    /// セレクタ文字列としては扱われない。先頭が「英字/アンダースコア + ( 」の形
    /// (例: "foo(...)") のセレクタはネスト式に誤分類される。
    /// ただし実際の CSS / XPath / text= 等のセレクタは先頭に ':' '#' '.' '[' '='
    /// 等が入るためこのパターンには一致せず (例: ":nth-child(2)", "text=x(y)")、
    /// "識別子(" で始まる有効なセレクタは事実上存在しないため実害は出ない。
    /// もし "ident(...)" 形のセレクタを Has/And/Or に渡す必要が生じた場合は
    /// 別経路 (Locator(...) のネスト式) を使うこと。
    /// </summary>
    private static readonly Regex ChainHeadPattern =
        new(@"^[A-Za-z_][A-Za-z0-9_]*\s*\(", RegexOptions.Compiled);

    /// <summary>ネスト式の最大深度。超えたら StackOverflow になる前に明示エラーにする。</summary>
    private const int MaxNestingDepth = 32;

    /// <summary>現在のネスト式評価の深さ (スレッドごと)。</summary>
    [ThreadStatic]
    private static int t_nestingDepth;

    /// <summary>
    /// ILocator型の値 (Has, HasNot, And/Orの引数) を解決する。
    ///   "GetByText(""OK"")" のようなネスト式 → ルートのIPageを起点にチェーン評価
    ///   "#sel" のようなセレクタ文字列       → page.Locator(セレクタ)
    /// </summary>
    private static ILocator ResolveNestedLocator(object root, string text)
    {
        var page = GetRootPage(root);
        var expr = text.Trim();

        // 空のセレクタは実行時まで持ち越さず、定義エラーとして即検出する
        if (expr.Length == 0)
        {
            throw new ArgumentException(
                "ILocator に変換する値が空です。セレクタまたはネスト式を指定してください。");
        }

        // 先頭に書かれた冗長なルート参照 (page. / frame. / 任意の別名) を、
        // 最初の Locator ファクトリ (GetByText/Locator 等) の手前まで読み飛ばす。
        // ネスト式は元々ルートの IPage を起点に解決されるため、これらの接頭辞は
        // 本来不要 (冗長)。うっかり書いても動かせるようにするための救済。
        expr = StripRootReference(expr);

        // 異常に深いネストは StackOverflowException (catch不能・プロセス即死) に
        // なる前に、明示的な定義エラーとして止める
        if (t_nestingDepth >= MaxNestingDepth)
        {
            throw new ArgumentException(
                $"ネスト式が深すぎます (上限 {MaxNestingDepth} 段)。定義を見直してください。");
        }

        if (ChainHeadPattern.IsMatch(expr))
        {
            t_nestingDepth++;
            try
            {
                var result = EvaluateChain(page, page, expr);

                var nested = result as ILocator;
                if (nested == null)
                {
                    throw new ArgumentException(
                        $"ネスト式 '{expr}' の結果は {KindName(result)} ですが、ILocator が必要です。");
                }

                return nested;
            }
            finally
            {
                t_nestingDepth--;
            }
        }

        // ネスト式でなければセレクタ文字列とみなす
        return page.Locator(expr);
    }

    /// <summary>
    /// ネスト式の先頭に書かれた冗長なルート参照 (page. / frame. / 任意の別名) を、
    /// 「最初に現れる IPage 起点の Locator ファクトリ (GetByText / Locator 等)」の
    /// 手前まで読み飛ばす。
    ///
    ///   "page.GetByText(\"OK\")"                      → "GetByText(\"OK\")"
    ///   "frame.Locator(\"#x\").ContentFrame.GetBy..." → "Locator(\"#x\").ContentFrame..."
    ///   "myPage.GetByText(\"OK\")"                    → "GetByText(\"OK\")"  (別名でも可)
    ///
    /// 分割は括弧・クォートを解釈する ParseChain で行うため、引数や文字列内の
    /// ドット ("a.b" / AriaRole.Button) で誤分割しない。
    ///
    /// 読み飛ばす側に First / Filter / ContentFrame 等の「レシーバが必要な
    /// Locator 操作」が含まれる場合は、意味のある起点を黙って捨てることになるため
    /// 例外にする (例: "ContentFrame.GetByText(...)" は起点にできない)。
    /// チェーンとして解釈できない文字列 (CSS/XPath セレクタ等) はそのまま返す。
    /// </summary>
    private static string StripRootReference(string expr)
    {
        // 先頭プレフィックスが存在しうるのは「最初の '(' より前にトップレベルの
        // '.' がある」場合だけ (例: page.GetByText(...) は '.'(4) < '('(14))。
        //   - '.' が無い               → プレフィックス無し
        //   - '(' が無い               → チェーンでない (CSS セレクタ "div.locator" 等)
        //   - '.' が '(' より後          → ドットは引数内 (GetByRole(AriaRole.Button) 等)
        // いずれも剥がす余地が無いので、無駄な ParseChain を避けて即返す。
        // これにより一般的なネスト式 (GetByRole(AriaRole.Button) 等) の二重パースも防ぐ。
        var dotPos = expr.IndexOf('.');
        var parenPos = expr.IndexOf('(');
        if (dotPos < 0 || parenPos < 0 || dotPos > parenPos)
        {
            return expr;
        }

        List<(string Name, string? Args)> segments;
        try
        {
            segments = ParameterParser.ParseChain(expr);
        }
        catch (FormatException)
        {
            // チェーンとして解釈できない = セレクタ文字列。手を加えない。
            return expr;
        }

        // 先頭から、IPage 起点で Locator を生成できる最初のセグメントを探す。
        // ファクトリは必ず引数を取る呼び出し (Args != null) なので、それを条件に
        // 加えることで、別名の "locator." (Args == null) を Locator ファクトリと
        // 取り違えず、後段で正しくルート別名として読み飛ばせるようにする。
        var cut = -1;
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].Args != null
                && PageLevelLocatorFactories.Contains(segments[i].Name))
            {
                cut = i;
                break;
            }
        }

        // ファクトリが無い (純粋なセレクタ等) / 先頭がファクトリ (剥がす物が無い)
        // 場合は素通り
        if (cut <= 0)
        {
            return expr;
        }

        // 読み飛ばす先頭セグメントに「実在する API メンバー (MainFrame / Frames /
        // First / Filter / ContentFrame 等)」が混じっていたら、意味のあるナビゲーション
        // を黙って捨てて別要素を指してしまうので明示エラーにする。
        // page. / frame. / locator. 等のルート別名は捨ててもスコープが変わらないため除外。
        for (var i = 0; i < cut; i++)
        {
            var name = segments[i].Name;
            if (AllKnownMembers.Contains(name) && !SafeRootAliases.Contains(name))
            {
                throw new ArgumentException(
                    $"ネスト式の起点に '{name}' は使えません。"
                    + " page./frame. 等のルート参照か、GetByText(...) / Locator(...) 等の"
                    + " ファクトリで始めてください。");
            }
        }

        // 残り (ファクトリ以降) を組み立て直して返す
        var sb = new StringBuilder();
        for (var i = cut; i < segments.Count; i++)
        {
            if (i > cut)
            {
                sb.Append('.');
            }

            sb.Append(segments[i].Name);
            if (segments[i].Args != null)
            {
                sb.Append('(').Append(segments[i].Args).Append(')');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// ネスト式の解決起点となる IPage を取得する。
    /// (PlaywrightのHas/And/Or等に渡すLocatorは通常pageから構築するため)
    /// </summary>
    private static IPage GetRootPage(object target)
    {
        switch (target)
        {
            case IPage p:
                return p;
            case ILocator l:
                return l.Page;
            case IFrameLocator f:
                return GetFrameOwner(f).Page;
            default:
                throw new ArgumentException(
                    $"ネスト式の解決起点を {target.GetType().Name} から取得できません。");
        }
    }

    /// <summary>
    /// IFrameLocator.Owner プロパティ (存在すれば) をキャッシュする。
    /// リフレクション解決を毎回やり直さないため一度だけ取得する。
    /// 値が null の場合は「このPlaywrightバージョンに Owner が無い」を意味する。
    /// </summary>
    private static readonly PropertyInfo? FrameOwnerProperty =
        typeof(IFrameLocator).GetProperty("Owner");

    /// <summary>
    /// IFrameLocator.Owner を取得する。
    /// Owner は比較的新しいAPIのため、古いPlaywrightバージョンの
    /// プロジェクトでもこのファイルがコンパイルできるよう
    /// 静的参照ではなくリフレクションで取得する。
    /// </summary>
    private static ILocator GetFrameOwner(IFrameLocator frame)
    {
        // Owner プロパティ自体が存在しないPlaywrightバージョンでは null になる
        var ownerProp = FrameOwnerProperty;

        ILocator? owner = null;
        if (ownerProp != null)
        {
            owner = ownerProp.GetValue(frame) as ILocator;
        }

        if (owner == null)
        {
            throw new ArgumentException(
                "IFrameLocator から起点ページを取得できません"
                + " (このPlaywrightバージョンには Owner プロパティがありません)。");
        }

        return owner;
    }

    // ============================================================
    //  リフレクション情報のキャッシュ
    // ============================================================

    // 同じ型のメソッド/プロパティ探索を毎回やり直さないためのキャッシュ。
    // データ駆動テストで数千回呼ばれてもコストが一定になる。
    private static readonly ConcurrentDictionary<Type, MethodInfo[]> MethodCache = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();
    private static readonly ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> OptionsPropsCache = new();

    /// <summary>
    /// インターフェースの公開メソッドを、継承元インターフェースの分も含めて取得する。
    /// </summary>
    private static MethodInfo[] GetAllMethods(Type interfaceType)
    {
        return MethodCache.GetOrAdd(interfaceType, t =>
            t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Concat(t.GetInterfaces()
                    .SelectMany(i => i.GetMethods(BindingFlags.Public | BindingFlags.Instance)))
                .ToArray());
    }

    /// <summary>
    /// インターフェースの公開プロパティを、継承元インターフェースの分も含めて取得する。
    /// </summary>
    private static PropertyInfo[] GetAllProperties(Type interfaceType)
    {
        return PropertyCache.GetOrAdd(interfaceType, t =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Concat(t.GetInterfaces()
                    .SelectMany(i => i.GetProperties(BindingFlags.Public | BindingFlags.Instance)))
                .ToArray());
    }

    /// <summary>
    /// Options型の書き込み可能プロパティを名前引き辞書 (大文字小文字無視) で取得する。
    /// </summary>
    private static Dictionary<string, PropertyInfo> GetOptionsProperties(Type optionsType)
    {
        return OptionsPropsCache.GetOrAdd(optionsType, t =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite)
                .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase));
    }

    // ============================================================
    //  ネスト式のルート参照解決に使うメンバー名集合
    // ============================================================
    //
    // 注意: これらは GetAllMethods/GetAllProperties (上のキャッシュ) を使うため、
    // MethodCache / PropertyCache の宣言より後で初期化される必要がある
    // (静的フィールドの初期化はテキスト順)。

    /// <summary>
    /// IPage 上で Locator (ILocator / IFrameLocator) を生成できる
    /// 「起点メソッド/プロパティ」名の集合 (大文字小文字無視)。
    /// 例: GetByRole, GetByText, Locator, FrameLocator。
    /// ネスト式の先頭プレフィックスを「最初にこの集合へ一致するセグメント」まで
    /// 読み飛ばす判定に使う。
    /// </summary>
    private static readonly HashSet<string> PageLevelLocatorFactories =
        BuildMemberNames(locatorReturningOnly: true, typeof(IPage));

    /// <summary>
    /// IPage / ILocator / IFrameLocator の全メンバー名 (大文字小文字無視)。
    /// 読み飛ばす側に MainFrame / Frames / First / Filter / ContentFrame 等の
    /// 「実在する API メンバー (＝意味のあるナビゲーション)」が紛れていないかの
    /// 検出に使う。これらを黙って捨てると別要素を指してしまうため例外にする。
    /// </summary>
    private static readonly HashSet<string> AllKnownMembers =
        BuildMemberNames(locatorReturningOnly: false,
            typeof(IPage), typeof(ILocator), typeof(IFrameLocator));

    /// <summary>
    /// 「ルートそのもの」を指す安全な別名。これらは実在メンバー名 (Page / Locator /
    /// FrameLocator) と衝突するが、いずれも解決起点＝ページに帰着するため、
    /// 読み飛ばしてもスコープが変わらない。AllKnownMembers の検出から除外する。
    /// </summary>
    private static readonly HashSet<string> SafeRootAliases =
        new(new[] { "page", "frame", "frameLocator", "locator" }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 値が ILocator / IFrameLocator として受け入れ可能か (どちらかに代入可能か)。
    /// ResolveSegment の戻り値型判定 (returnType 未指定時) と、メンバー名集合の
    /// 構築で共有する。
    /// </summary>
    private static bool AcceptsAnyLocator(Type rt) =>
        typeof(ILocator).IsAssignableFrom(rt) || typeof(IFrameLocator).IsAssignableFrom(rt);

    /// <summary>
    /// 指定インターフェース群のメソッド・プロパティ名集合を作る (大文字小文字無視)。
    /// locatorReturningOnly が true なら ILocator / IFrameLocator を返すものだけ。
    /// </summary>
    private static HashSet<string> BuildMemberNames(bool locatorReturningOnly, params Type[] interfaceTypes)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var it in interfaceTypes)
        {
            foreach (var m in GetAllMethods(it))
            {
                // プロパティのアクセサ (get_XXX) は除外し、プロパティ側で名前を拾う
                if (!m.IsSpecialName && (!locatorReturningOnly || AcceptsAnyLocator(m.ReturnType)))
                {
                    set.Add(m.Name);
                }
            }

            foreach (var p in GetAllProperties(it))
            {
                if (!locatorReturningOnly || AcceptsAnyLocator(p.PropertyType))
                {
                    set.Add(p.Name);
                }
            }
        }

        return set;
    }

    // ============================================================
    //  雑多なヘルパー
    // ============================================================

    /// <summary>
    /// 実体オブジェクトから、リフレクション探索に使うインターフェース型を決める。
    /// </summary>
    private static Type GetInterfaceType(object? target)
    {
        switch (target)
        {
            case IPage:
                return typeof(IPage);
            case ILocator:
                return typeof(ILocator);
            case IFrameLocator:
                return typeof(IFrameLocator);
            case null:
                throw new InvalidOperationException(
                    "対象が null です (直前のメンバーが null を返した可能性があります)。");
            default:
                throw new ArgumentException(
                    $"対象 {target.GetType().Name} は IPage / ILocator / IFrameLocator のいずれでもありません。");
        }
    }

    /// <summary>エラーメッセージ用に、オブジェクトの種類を分かりやすい名前で返す。</summary>
    private static string KindName(object? o)
    {
        switch (o)
        {
            case IPage:
                return nameof(IPage);
            case ILocator:
                return nameof(ILocator);
            case IFrameLocator:
                return nameof(IFrameLocator);
            case null:
                return "null";
            default:
                return o.GetType().Name;
        }
    }

    /// <summary>
    /// メソッド名のtypoに対して「もしかして」候補を提示する。
    /// 編集距離2以内のメンバー名を最大3件返す (なければ空文字列)。
    /// </summary>
    private static string SuggestNames(Type interfaceType, string methodName, Func<Type, bool> acceptable)
    {
        var names = GetAllMethods(interfaceType)
            .Where(m => !m.IsSpecialName && acceptable(m.ReturnType))
            .Select(m => m.Name)
            .Concat(GetAllProperties(interfaceType)
                .Where(pr => acceptable(pr.PropertyType))
                .Select(pr => pr.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var lower = methodName.ToLowerInvariant();

        var hints = names
            .Select(n => (Name: n, Dist: EditDistance(n.ToLowerInvariant(), lower)))
            .Where(x => x.Dist <= 2)
            .OrderBy(x => x.Dist)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name)
            .Take(3)
            .ToList();

        if (hints.Count == 0)
        {
            return "";
        }

        return $" もしかして: {string.Join(", ", hints)} ?";
    }

    /// <summary>
    /// レーベンシュタイン編集距離 (挿入・削除・置換の最小回数) を計算する。
    /// メソッド名は短いので単純なDP実装で十分。
    /// </summary>
    private static int EditDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = 0;
                if (a[i - 1] != b[j - 1])
                {
                    cost = 1;
                }

                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    /// <summary>
    /// 数値・bool パースの前処理として、全角の英数字・記号 (U+FF01..U+FF5E) を
    /// 対応するASCII文字へ、全角スペース (U+3000) を半角スペースへ畳み込む。
    /// Excelで全角入力された "２" や "ＴＲＵＥ" を受け付けられるようにするため。
    /// 変換対象がなければ元の文字列をそのまま返す (高速パス)。
    /// </summary>
    private static string NormalizeForParse(string s)
    {
        StringBuilder? sb = null;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var r = c;

            if (c >= '！' && c <= '～')
            {
                r = (char)(c - 0xFEE0);
            }
            else if (c == '　')
            {
                r = ' ';
            }
            else if (c is '－' or '−')
            {
                // 全角ハイフンマイナス (U+FF0D) と マイナス記号 (U+2212) は
                // 全角英数記号の畳み込み範囲 (U+FF01..U+FF5E) の外にあるため個別に半角化する。
                // (Excel/IMEで全角入力された負数 "－１" を扱えるようにするため)
                r = '-';
            }

            if (r != c && sb == null)
            {
                sb = new StringBuilder(s.Length);
                sb.Append(s, 0, i);
            }

            sb?.Append(r);
        }

        return sb == null ? s : sb.ToString();
    }

    /// <summary>エラーメッセージ用に、パース済みの値を表示用文字列にする。</summary>
    private static string Display(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case LocatorExpr expr:
                return expr.Text;
            case NullLiteral:
                return "null";
            case Regex r:
                return $"new Regex(\"{r}\")";
            default:
                // ToString が null を返す型でも空文字列で受ける
                var text = value.ToString();
                if (text == null)
                {
                    return "";
                }
                return text;
        }
    }
}

/// <summary>
/// パース結果のうち「ネストしたLocator式」を表すマーカー。
/// 例: Has = GetByText("OK") の右辺は LocatorExpr("GetByText(\"OK\")") になり、
/// ILocator型へ変換するタイミングでチェーン評価される。
/// </summary>
internal sealed record LocatorExpr(string Text);

/// <summary>
/// オブジェクト初期化子内の null リテラルを表すマーカー。
/// (Dictionary の値に null を入れられないため、シングルトンで代用する)
/// </summary>
internal sealed class NullLiteral
{
    public static readonly NullLiteral Instance = new();
    private NullLiteral() { }
}

// ============================================================
// ParameterParser
//
// C#風のメソッド引数文字列をパースして、
// 「メインの値」と「Optionsのプロパティ辞書」に分解する。
//
// 対応フォーマット例:
//   "Hello"                                    → primary="Hello", options={}
//   @"xpath=//div[@id=""x""]"                  → primary=逐語的文字列, options={}
//   "Hello", new() { Exact = true }            → primary="Hello", options={Exact:true}
//   new Regex("Hello")                         → primary=Regex,   options={}
//   AriaRole.Button, new() { Name = "送信" }   → primary="AriaRole.Button", options={Name:"送信"}
//   new() { Has = GetByText("OK") }            → primary=null, options={Has:LocatorExpr}
//   2 / -1 / 2.5                               → primary=int または double
//
// 不正な入力 (閉じていない文字列、'='のないプロパティ等) は
// FormatException で位置情報付きのエラーにする。
// ============================================================
internal static class ParameterParser
{
    /// <summary>
    /// Excel等の外部データ由来の正規表現に適用するマッチタイムアウト。
    /// 壊滅的バックトラッキング (ReDoS) を持つパターンが渡されても
    /// マッチ実行が無限にハングしないよう上限を設ける。
    /// </summary>
    internal static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// C#風パラメータ文字列をパースする。
    /// </summary>
    /// <param name="input">パラメータ文字列全体</param>
    /// <returns>
    ///   Primary: メソッドの第1引数にあたる値 (string, Regex, int, double, またはEnum名の文字列)
    ///   Options: オブジェクト初期化子の中身を辞書にしたもの
    /// </returns>
    public static (object? Primary, Dictionary<string, object> Options) Parse(string? input)
    {
        // 不可視文字 (ゼロ幅スペース・BOM等) を除去してから判定する
        // (Excelやコピペ由来の混入対策)
        if (input != null)
        {
            input = StripInvisible(input);
        }

        // 空やnullなら「引数なし」として返す
        if (string.IsNullOrWhiteSpace(input))
        {
            return (null, new(StringComparer.OrdinalIgnoreCase));
        }

        var t = input.Trim();
        var p = 0;

        SkipWs(t, ref p);

        object? primary = null;
        var options = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // -----------------------------------------------
        // 先頭トークンの種類を判定する
        // -----------------------------------------------

        if (p < t.Length && IsDq(t[p]))
        {
            // ダブルクォート (スマートクォート等含む) で始まる → 文字列リテラル
            primary = ReadString(t, ref p);
        }
        else if (IsVerbatimStart(t, p))
        {
            // @" で始まる → 逐語的文字列リテラル
            primary = ReadVerbatimString(t, ref p);
        }
        else if (p < t.Length && IsSq(t[p]))
        {
            // シングルクォートで始まる → 文字列リテラル (Excel由来データ向けの拡張)
            primary = ReadSingleQuoted(t, ref p);
        }
        else if (IsKeyword(t, p, "new"))
        {
            // "new" キーワード → Regex生成 or オブジェクト初期化子
            if (IsRegexNew(t, p))
            {
                // "new Regex(...)" → Regexオブジェクトを生成
                primary = ReadRegex(t, ref p);
            }
            else
            {
                // "new() { ... }" → メイン引数なし、Optionsのみ (Filter等で使う)
                options = ReadObjectInitializer(t, ref p);
                EnsureEnd(t, p);
                return (null, options);
            }
        }
        else if (IsNumberStart(t, p) && IsNumberToken(t, p, allowCloseBrace: false))
        {
            // 数字またはマイナスで始まり、直後が区切り → 数値 (int or double)
            // ("2件" や "2.5.3" のような数字始まりのテキストは下の未クォート分岐へ)
            primary = ReadNumber(t, ref p);
        }
        else if (p < t.Length)
        {
            // 未クォートの値: 識別子 (AriaRole.Button)、セレクタ文字列
            // (xpath=..., #id, 2件 等)、ネスト式 (GetByText("OK")) の可能性がある。
            // ", new" パターンを探してプライマリ引数の範囲を決定する。
            var boundary = FindOptionsBoundary(t, p);
            var raw = t[p..boundary].TrimEnd();
            p = boundary;

            // 未クォートの "null"、および空 (先頭カンマで ", new() {...}" と
            // 書いた場合) は「引数なし」として扱う。
            // クォートされた "null" や "" は文字列のまま
            if (raw.Length == 0 || raw.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                primary = null;
            }
            else
            {
                primary = raw;
            }
        }

        // -----------------------------------------------
        // メイン引数とOptionsの間のカンマを処理する
        // -----------------------------------------------

        SkipWs(t, ref p);

        if (p < t.Length && t[p] == ',')
        {
            p++;
        }

        SkipWs(t, ref p);

        // -----------------------------------------------
        // Optionsのオブジェクト初期化子があればパースする
        // -----------------------------------------------

        if (p < t.Length && IsKeyword(t, p, "new"))
        {
            // 2番目の引数に書けるのはオプション初期化子のみ。
            // new Regex はオーバーロードが存在しないので明確にエラーにする
            if (IsRegexNew(t, p))
            {
                throw new FormatException(
                    "2番目の引数に new Regex(...) は指定できません。"
                    + " オプションは new() { ... } の形式で指定してください。");
            }

            options = ReadObjectInitializer(t, ref p);
        }

        EnsureEnd(t, p);
        return (primary, options);
    }

    /// <summary>
    /// チェーン式 "A(args).B(args).C" を (名前, 引数文字列) のリストに分解する。
    /// 引数なしのセグメント (プロパティ) は Args = null になる。
    /// </summary>
    public static List<(string Name, string? Args)> ParseChain(string expression)
    {
        var t = StripInvisible(expression).Trim();
        var p = 0;
        var segments = new List<(string, string?)>();

        while (true)
        {
            SkipWs(t, ref p);

            // セグメント名 (英数字とアンダースコアのみ、ドットは区切りなので含めない)
            var start = p;
            while (p < t.Length && (char.IsLetterOrDigit(t[p]) || t[p] == '_'))
            {
                p++;
            }

            if (p == start)
            {
                throw new FormatException(
                    $"チェーン式のメソッド名が読み取れません (位置 {p}): '{t}'");
            }

            var name = t[start..p];

            SkipWs(t, ref p);

            // "(" があれば対応する ")" までを引数文字列として切り出す
            string? args = null;
            if (p < t.Length && t[p] == '(')
            {
                var argStart = p + 1;
                SkipBalancedParens(t, ref p); // p は ')' の次へ進む
                args = t[argStart..(p - 1)];
            }

            segments.Add((name, args));

            SkipWs(t, ref p);

            // "." が続けば次のセグメントへ
            if (p < t.Length && t[p] == '.')
            {
                p++;
                continue;
            }

            break;
        }

        if (p < t.Length)
        {
            throw new FormatException(
                $"チェーン式に解釈できない入力が残っています (位置 {p}): '{t[p..]}'");
        }

        return segments;
    }

    // ============================================================
    //  個別トークンの読み取りメソッド
    // ============================================================

    /// <summary>
    /// ダブルクォートで囲まれた文字列リテラルを読み取る。
    /// エスケープ文字 (\n, \t, \", \\) にも対応。
    /// Excel/Wordのオートコレクト対策として、スマートクォート (“ ”) と
    /// 全角クォート (＂) も区切りとして受け付ける。
    /// 閉じクォートがなければ FormatException。
    /// </summary>
    private static string ReadString(string t, ref int p)
    {
        var startPos = p;

        // 開きクォートをスキップ
        p++;

        var sb = new StringBuilder();

        while (p < t.Length && !IsDq(t[p]))
        {
            if (t[p] == '\\' && p + 1 < t.Length)
            {
                p++;
                switch (t[p])
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    default:
                        if (IsQuoteChar(t[p]))
                        {
                            // エスケープされたクォート文字 (スマート/全角含む) は
                            // その文字だけを残す。これにより “ 等を含むテキストも
                            // \“ と書けば文字列内に表現できる
                            sb.Append(t[p]);
                        }
                        else
                        {
                            // 未知のエスケープはバックスラッシュごと残す。
                            // 正規表現パターン (\d, \s 等) を通常の "..." に
                            // 書いても壊れないようにするため
                            sb.Append('\\').Append(t[p]);
                        }
                        break;
                }
            }
            else
            {
                sb.Append(t[p]);
            }

            p++;
        }

        // 閉じクォートがないまま終端に達したらエラー (黙って成功扱いにしない)
        if (p >= t.Length)
        {
            throw new FormatException(
                $"文字列リテラル (\"...\") が閉じていません (開始位置 {startPos})。");
        }

        p++; // 閉じクォートをスキップ
        return sb.ToString();
    }

    /// <summary>
    /// シングルクォートで囲まれた文字列リテラルを読み取る。
    /// C#の構文ではないが、Excel由来のデータで頻出するため拡張として受け付ける。
    /// スマートクォート (‘ ’) も区切りとして扱う。
    /// </summary>
    private static string ReadSingleQuoted(string t, ref int p)
    {
        var startPos = p;

        // 開きクォートをスキップ
        p++;

        var sb = new StringBuilder();

        while (p < t.Length && !IsSq(t[p]))
        {
            // エスケープ処理は ReadString (ダブルクォート) と対称にする。
            // \n \t \r は制御文字に、クォート文字はその文字だけを残し、
            // 未知のエスケープ (\d 等) はバックスラッシュごと温存する。
            if (t[p] == '\\' && p + 1 < t.Length)
            {
                p++;
                switch (t[p])
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '\\': sb.Append('\\'); break;
                    default:
                        if (IsQuoteChar(t[p]))
                        {
                            sb.Append(t[p]);
                        }
                        else
                        {
                            sb.Append('\\').Append(t[p]);
                        }
                        break;
                }
            }
            else
            {
                sb.Append(t[p]);
            }

            p++;
        }

        if (p >= t.Length)
        {
            throw new FormatException(
                $"文字列リテラル ('...') が閉じていません (開始位置 {startPos})。");
        }

        p++; // 閉じクォートをスキップ
        return sb.ToString();
    }

    /// <summary>
    /// 逐語的文字列リテラル @"..." を読み取る。
    /// C#の仕様どおり、内部の "" は " 1文字として扱い、
    /// バックスラッシュはエスケープしない。
    /// </summary>
    private static string ReadVerbatimString(string t, ref int p)
    {
        var startPos = p;

        // @" の2文字をスキップ
        p += 2;

        var sb = new StringBuilder();

        while (p < t.Length)
        {
            if (IsDq(t[p]))
            {
                // "" は " 1文字のエスケープ (スマートクォートも同様に扱う)
                if (p + 1 < t.Length && IsDq(t[p + 1]))
                {
                    sb.Append('"');
                    p += 2;
                    continue;
                }

                // 単独のクォートは終端
                p++;
                return sb.ToString();
            }

            sb.Append(t[p]);
            p++;
        }

        throw new FormatException(
            $"逐語的文字列リテラル (@\"...\") が閉じていません (開始位置 {startPos})。");
    }

    /// <summary>
    /// new Regex("pattern") 形式を読み取り、Regexオブジェクトを生成する。
    /// 完全修飾 (new System.Text.RegularExpressions.Regex(...))、
    /// 逐語的文字列パターン (new Regex(@"\d+"))、
    /// 第2引数の RegexOptions ("|" 結合も可) に対応。
    /// 読み取り開始時、p は "new" の "n" を指していること。
    /// </summary>
    private static Regex ReadRegex(string t, ref int p)
    {
        p += 3;              // "new" をスキップ
        SkipWs(t, ref p);
        ReadIdent(t, ref p); // "Regex" (完全修飾名も可) をスキップ
        SkipWs(t, ref p);

        if (p >= t.Length || t[p] != '(')
        {
            throw new FormatException($"new Regex の '(' がありません (位置 {p})。");
        }

        p++;
        SkipWs(t, ref p);

        // パターン文字列: 通常 ("...")、逐語的 (@"...")、シングルクォート ('...') に対応
        string pattern;
        if (IsVerbatimStart(t, p))
        {
            pattern = ReadVerbatimString(t, ref p);
        }
        else if (p < t.Length && IsSq(t[p]))
        {
            pattern = ReadSingleQuoted(t, ref p);
        }
        else if (p < t.Length && IsDq(t[p]))
        {
            pattern = ReadString(t, ref p);
        }
        else
        {
            throw new FormatException(
                $"new Regex のパターン文字列がありません (位置 {p})。");
        }

        SkipWs(t, ref p);

        // オプションの RegexOptions パラメータを処理する
        // 例: new Regex("pattern", RegexOptions.IgnoreCase | RegexOptions.Multiline)
        var regexOptions = RegexOptions.None;
        if (p < t.Length && t[p] == ',')
        {
            p++;
            SkipWs(t, ref p);
            regexOptions = ReadRegexOptions(t, ref p);
            SkipWs(t, ref p);
        }

        if (p >= t.Length || t[p] != ')')
        {
            throw new FormatException($"new Regex の ')' が閉じていません (位置 {p})。");
        }

        p++;
        return new Regex(pattern, regexOptions, RegexMatchTimeout);
    }

    /// <summary>
    /// RegexOptions の値を読み取る。"|" で結合された複数のオプションにも対応。
    /// </summary>
    private static RegexOptions ReadRegexOptions(string t, ref int p)
    {
        var result = RegexOptions.None;

        while (p < t.Length)
        {
            SkipWs(t, ref p);

            var ident = ReadIdent(t, ref p);
            if (ident.Length == 0)
            {
                throw new FormatException($"RegexOptions の値が読み取れません (位置 {p})。");
            }

            // "RegexOptions.IgnoreCase" → "IgnoreCase" 部分を取り出す
            var optName = ident;
            if (ident.Contains('.'))
            {
                optName = ident.Split('.').Last();
            }

            // typo を黙って RegexOptions.None にしない
            if (!Enum.TryParse<RegexOptions>(optName, ignoreCase: true, out var opt))
            {
                throw new FormatException(
                    $"'{optName}' は RegexOptions の値として解釈できません。");
            }

            result |= opt;

            SkipWs(t, ref p);

            if (p < t.Length && t[p] == '|')
            {
                p++;
                continue;
            }

            break;
        }

        return result;
    }

    /// <summary>
    /// 数値リテラルを読み取る。負数と小数に対応。
    /// "2" → int 2、"-1" → int -1、"2.5" → double 2.5
    /// パースはカルチャ非依存 (小数点は '.' 固定)。
    /// </summary>
    private static object ReadNumber(string t, ref int p)
    {
        var start = p;

        // 終端の走査は ScanNumberEnd に一本化する (分類用の走査と文法を共有し、
        // 片方だけ仕様変更されて「数値判定したのにパースできない」不整合が
        // 生じるのを防ぐ)。
        p = ScanNumberEnd(t, p);

        var s = t[start..p];

        // 小数部 ("." の後に数字) を含むかどうかで int / double を分ける。
        // ScanNumberEnd は数字が続く '.' のみを取り込むので、'.' の有無で判別できる。
        var isDouble = s.Contains('.');

        // 注意: 三項演算子で書くと int が double に暗黙変換されてしまうため if で分ける
        try
        {
            if (isDouble)
            {
                var d = double.Parse(s, CultureInfo.InvariantCulture);

                // double.Parse は OverflowException を投げず、桁あふれ時は
                // ±Infinity を返す。黙って Infinity を渡さず明示エラーにする。
                if (double.IsInfinity(d))
                {
                    throw new FormatException($"数値 '{s}' が大きすぎて扱えません。");
                }

                return d;
            }
            return int.Parse(s, CultureInfo.InvariantCulture);
        }
        catch (OverflowException ex)
        {
            throw new FormatException($"数値 '{s}' が大きすぎて扱えません。", ex);
        }
    }

    /// <summary>
    /// 識別子 (英数字、アンダースコア、ドットで構成) を読み取る。
    /// Enum値 (AriaRole.Button) やプロパティ名 (Name, Exact) に使う。
    /// </summary>
    private static string ReadIdent(string t, ref int p)
    {
        var start = p;

        while (p < t.Length && (char.IsLetterOrDigit(t[p]) || t[p] is '_' or '.'))
        {
            p++;
        }

        return t[start..p];
    }

    // ============================================================
    //  オブジェクト初期化子の読み取り
    // ============================================================

    /// <summary>
    /// C#のオブジェクト初期化子をパースして辞書にする。
    ///
    /// 例: new() { Name = "送信", Exact = true }
    ///   → { "Name": "送信", "Exact": true }
    ///
    /// new() でも new XxxOptions() でも対応可能。
    /// 文法エラー ('='がない、値が読めない等) は FormatException にする。
    /// </summary>
    private static Dictionary<string, object> ReadObjectInitializer(string t, ref int p)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // "new" / "new()" / "new XxxOptions()" の部分を検証しながら読み進める。
        // かつては「'{' まで何でも読み飛ばす」実装だったため、
        // "x", new Regex("y") のような不正入力が黙って無視されていた。
        p += 3; // "new" をスキップ
        SkipWs(t, ref p);
        ReadIdent(t, ref p); // 型名 (省略可: "new()" の場合は空)
        SkipWs(t, ref p);

        // コンストラクタの "()" (引数があったらエラー)
        var sawParens = false;
        if (p < t.Length && t[p] == '(')
        {
            p++;
            SkipWs(t, ref p);

            if (p >= t.Length || t[p] != ')')
            {
                throw new FormatException(
                    $"new 式のコンストラクタに引数は書けません (位置 {p})。"
                    + " オプションは new() { ... } の形式で指定してください。");
            }

            p++;
            sawParens = true;
        }

        SkipWs(t, ref p);

        // "{" がない場合: "new()" だけなら空オプションとして許容、
        // "()" も "{}" もない裸の new 式 (例: "new b") はエラー
        if (p >= t.Length)
        {
            if (sawParens)
            {
                return result;
            }

            throw new FormatException(
                "new 式の後に '()' または '{ ... }' が必要です。");
        }

        if (t[p] != '{')
        {
            throw new FormatException(
                $"オブジェクト初期化子の '{{' が必要です (位置 {p}): '{t[p]}'");
        }

        p++; // "{" をスキップ

        while (true)
        {
            SkipWs(t, ref p);

            if (p >= t.Length)
            {
                throw new FormatException("オブジェクト初期化子の '}' が閉じていません。");
            }

            if (t[p] == '}')
            {
                break;
            }

            // プロパティ名を読み取る (例: "Name", "Exact")
            var name = ReadIdent(t, ref p);
            if (name.Length == 0)
            {
                throw new FormatException(
                    $"オブジェクト初期化子のプロパティ名が読み取れません (位置 {p}): '{t[p]}'");
            }

            SkipWs(t, ref p);

            // "=" は必須。なければ書き間違いとして即エラーにする
            // (日本語IME由来の全角 '＝' も受け付ける)
            if (p >= t.Length || (t[p] != '=' && t[p] != '＝'))
            {
                throw new FormatException(
                    $"プロパティ '{name}' の後に '=' がありません (位置 {p})。");
            }

            p++; // "=" をスキップ
            SkipWs(t, ref p);

            // 値を読み取る (文字列、bool、数値、Regex、ネスト式 等)
            result[name] = ReadValue(t, ref p);

            SkipWs(t, ref p);

            // 値の後は "," (次のプロパティ) か "}" (終端) のどちらか
            if (p < t.Length && t[p] == ',')
            {
                p++;
                continue;
            }

            if (p < t.Length && t[p] == '}')
            {
                break;
            }

            throw new FormatException(
                $"プロパティ '{name}' の値の後に ',' か '}}' が必要です (位置 {p})。");
        }

        p++; // "}" をスキップ
        return result;
    }

    /// <summary>
    /// オブジェクト初期化子の中の「値」を1つ読み取る。
    /// 値の型は先頭の文字で判別する。
    ///
    /// "..." / @"..."   → 文字列
    /// new Regex(...)   → Regex
    /// true / false     → bool (語境界チェックあり)
    /// 数字             → int or double
    /// 識別子(          → ネストLocator式 (LocatorExpr)
    /// その他           → 識別子 (文字列として返す)
    /// </summary>
    private static object ReadValue(string t, ref int p)
    {
        if (p >= t.Length)
        {
            throw new FormatException("値がありません (入力の終端に達しました)。");
        }

        if (IsDq(t[p]))
        {
            return ReadString(t, ref p);
        }

        if (IsVerbatimStart(t, p))
        {
            return ReadVerbatimString(t, ref p);
        }

        if (IsSq(t[p]))
        {
            return ReadSingleQuoted(t, ref p);
        }

        if (IsKeyword(t, p, "new"))
        {
            if (IsRegexNew(t, p))
            {
                return ReadRegex(t, ref p);
            }

            throw new FormatException(
                $"未対応の new 式です (位置 {p})。値に使えるのは new Regex(...) のみです。");
        }

        // null リテラル → プロパティを明示的に null にするマーカー
        if (IsKeyword(t, p, "null"))
        {
            p += 4;
            return NullLiteral.Instance;
        }

        // true/false は語境界をチェックする ("trueblue" のような識別子と区別)
        if (IsKeyword(t, p, "true"))
        {
            p += 4;
            return true;
        }

        if (IsKeyword(t, p, "false"))
        {
            p += 5;
            return false;
        }

        // 数値: 直後が区切り (',' '}' 終端) の場合のみ。
        // "2件" のような数字始まりのテキストは下の識別子分岐で文字列になる
        if (IsNumberStart(t, p) && IsNumberToken(t, p, allowCloseBrace: true))
        {
            return ReadNumber(t, ref p);
        }

        // 識別子: enum値 (AriaRole.Button)、ネストLocator式 (GetByText("OK"))、
        // または未クォートのテキスト (送信 ボタン、#sel、-x- 等)
        var identStart = p;
        var ident = ReadIdent(t, ref p);

        // ASCII識別子の直後に "(" が続けばネストLocator式として丸ごと切り出す。
        // ("送信 (注)" のような日本語テキスト+括弧はネスト式ではないので除外)
        if (ident.Length > 0)
        {
            var look = p;
            SkipWs(t, ref look);

            if (look < t.Length && t[look] == '(' && IsAsciiIdent(ident))
            {
                p = identStart;
                return new LocatorExpr(CaptureChain(t, ref p));
            }
        }

        // それ以外は区切り (',' '}') までを未クォート文字列として読む
        // (例: Name = 送信 ボタン、Has = #sel)
        var end = p;
        while (end < t.Length && t[end] != ',' && t[end] != '}')
        {
            end++;
        }

        var raw = t[identStart..end].TrimEnd();
        p = end;

        // 区切りまで進んでも空なら書き間違い (例: "Name = ,")
        if (raw.Length == 0)
        {
            throw new FormatException($"値が読み取れません (位置 {identStart})。");
        }

        return raw;
    }

    /// <summary>
    /// ネストLocator式のテキストを丸ごと切り出す。
    /// "GetByText("OK")" や "Locator("#x").First" のような
    /// メソッド呼び出し+チェーンの連なりを、括弧の対応を保ちながら読み進める。
    /// </summary>
    private static string CaptureChain(string t, ref int p)
    {
        var start = p;

        ReadIdent(t, ref p); // 先頭のメソッド名

        while (true)
        {
            var look = p;
            SkipWs(t, ref look);

            if (look < t.Length && t[look] == '(')
            {
                // 引数部分: 対応する ")" まで読み飛ばす
                p = look;
                SkipBalancedParens(t, ref p);
                continue;
            }

            if (look < t.Length && t[look] == '.')
            {
                // チェーンの続き: "." の後の識別子を読む
                var afterDot = look + 1;
                SkipWs(t, ref afterDot);

                var q = afterDot;
                ReadIdent(t, ref q);

                if (q == afterDot)
                {
                    break; // ドットの後に識別子がない → ここでチェーン終了
                }

                p = q;
                continue;
            }

            break;
        }

        return t[start..p];
    }

    /// <summary>
    /// "(" から対応する ")" までを読み飛ばす (pは ")" の次に進む)。
    /// 内部の文字列リテラル ("..." / @"..." / '...') の中の括弧は数えない。
    /// 読み取り開始時、t[p] は "(" を指していること。
    /// </summary>
    private static void SkipBalancedParens(string t, ref int p)
    {
        var startPos = p;
        var depth = 0;

        while (p < t.Length)
        {
            if (IsDq(t[p]))
            {
                ReadString(t, ref p); // 文字列を丸ごとスキップ
                continue;
            }

            if (IsVerbatimStart(t, p))
            {
                ReadVerbatimString(t, ref p);
                continue;
            }

            // シングルクォート文字列も丸ごとスキップする。
            // ReadSingleQuoted/IsSq でサポート済みの記法なので、ここで
            // 文字列扱いしないと内部の '(' ')' を括弧として誤カウントし、
            // ネスト式やチェーン式の捕捉範囲がずれる
            // (例: Filter(new() { HasText = 'a)b' }))。
            if (IsSq(t[p]))
            {
                ReadSingleQuoted(t, ref p);
                continue;
            }

            if (t[p] == '(')
            {
                depth++;
            }
            else if (t[p] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    p++;
                    return;
                }
            }

            p++;
        }

        throw new FormatException($"'(' が閉じていません (開始位置 {startPos})。");
    }

    // ============================================================
    //  ユーティリティメソッド
    // ============================================================

    /// <summary>
    /// プライマリ引数とオプション (new() { ... }) の境界位置を探す。
    /// ", new" パターン (カンマ + 空白 + "new" キーワード) を探し、
    /// 見つかればカンマの位置を、見つからなければ文字列末尾を返す。
    /// クォート内、および括弧 ()[]{} の内側のカンマは無視する
    /// (ネスト式 "GetByRole(AriaRole.Button, new() {...})" を分断しないため)。
    /// </summary>
    private static int FindOptionsBoundary(string t, int start)
    {
        var depth = 0;

        for (var i = start; i < t.Length; i++)
        {
            // 逐語的文字列 @"..." はスキップ ("" エスケープに注意、スマートクォート含む)。
            // 閉じクォートがない場合は文字列とみなさず通常文字として続行する
            if (IsVerbatimStart(t, i))
            {
                var j = i + 2;
                while (j < t.Length)
                {
                    if (IsDq(t[j]))
                    {
                        if (j + 1 < t.Length && IsDq(t[j + 1])) { j += 2; continue; }
                        break;
                    }
                    j++;
                }

                if (j < t.Length)
                {
                    i = j; // 閉じクォートの位置へ (forのi++で次の文字へ進む)
                    continue;
                }
            }

            // ダブルクォート文字列 (スマートクォート含む) はスキップ
            // (内部のカンマを誤検出しないため)。
            // 閉じクォートがない場合 (インチ記号 5" 等) は通常文字として扱う
            if (IsDq(t[i]))
            {
                var j = i + 1;
                while (j < t.Length && !IsDq(t[j]))
                {
                    if (t[j] == '\\' && j + 1 < t.Length)
                    {
                        j++;
                    }
                    j++;
                }

                if (j < t.Length)
                {
                    i = j;
                    continue;
                }
            }

            // シングルクォート文字列はスキップ (XPath/CSSの 'say "hi"' のような記法に対応)。
            // 括弧の外 (深さ0) の ' は英文の所有格・短縮形 (It's 等) の可能性が
            // 高いため文字列とみなさない。閉じクォートがない場合も同様
            if (t[i] == '\'' && depth > 0)
            {
                var j = i + 1;
                while (j < t.Length && t[j] != '\'')
                {
                    if (t[j] == '\\' && j + 1 < t.Length)
                    {
                        j++;
                    }
                    j++;
                }

                if (j < t.Length)
                {
                    i = j;
                    continue;
                }
            }

            // 括弧の深さを追跡 (深さ0のカンマだけが境界候補)
            if (t[i] is '(' or '[' or '{')
            {
                depth++;
                continue;
            }

            if (t[i] is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (t[i] == ',' && depth == 0)
            {
                var afterComma = i + 1;
                while (afterComma < t.Length && char.IsWhiteSpace(t[afterComma]))
                {
                    afterComma++;
                }

                // カンマの後が "new" キーワードかチェック
                // ("newcomer" 等の識別子と区別するため語境界も見る)
                if (IsKeyword(t, afterComma, "new"))
                {
                    return i;
                }
            }
        }

        return t.Length;
    }

    /// <summary>
    /// 指定位置から先が、指定キーワードで始まり、かつ直後が語境界かをチェックする。
    /// "new" と "newcomer"、"true" と "trueblue" を区別するために使う。
    /// </summary>
    private static bool IsKeyword(string t, int p, string word)
    {
        if (!AtI(t, p, word))
        {
            return false;
        }

        var next = p + word.Length;

        // 末尾ならキーワード確定。続く文字が識別子構成文字ならただの識別子
        return next >= t.Length
            || !(char.IsLetterOrDigit(t[next]) || t[next] is '_' or '-');
    }

    /// <summary>数値リテラルの開始位置かどうか (数字、または マイナス+数字)。</summary>
    private static bool IsNumberStart(string t, int p)
    {
        return p < t.Length
            && (IsAsciiDigit(t[p])
                || (t[p] == '-' && p + 1 < t.Length && IsAsciiDigit(t[p + 1])));
    }

    /// <summary>
    /// ASCII数字 ('0'..'9') かどうか。
    /// char.IsDigit は全角 '２' や他スクリプトの数字も true を返すが、
    /// int.Parse / double.Parse はそれらを受け付けず FormatException になるため、
    /// 数値トークンの判定は ASCII に限定し、全角は未クォート文字列として
    /// CoerceTo の正規化パスへ回す。
    /// </summary>
    private static bool IsAsciiDigit(char c)
    {
        return c is >= '0' and <= '9';
    }

    /// <summary>
    /// 逐語的文字列リテラル @" の開始位置かどうか。
    /// スマートクォート (@“ 等) も受け付ける。
    /// </summary>
    private static bool IsVerbatimStart(string t, int p)
    {
        return p + 1 < t.Length && t[p] == '@' && IsDq(t[p + 1]);
    }

    /// <summary>
    /// 数値トークンの終端位置を走査する (パースはしない)。
    /// 符号・整数部・小数部 (.数字) の並びを読み飛ばした位置を返す。
    /// </summary>
    private static int ScanNumberEnd(string t, int p)
    {
        if (t[p] == '-')
        {
            p++;
        }

        while (p < t.Length && IsAsciiDigit(t[p]))
        {
            p++;
        }

        if (p + 1 < t.Length && t[p] == '.' && IsAsciiDigit(t[p + 1]))
        {
            p++;
            while (p < t.Length && IsAsciiDigit(t[p]))
            {
                p++;
            }
        }

        return p;
    }

    /// <summary>
    /// 数字で始まるトークンが「純粋な数値」かどうか判定する。
    /// 数値の直後 (空白除く) が終端・カンマ (・初期化子内なら '}') なら数値、
    /// それ以外 ("2件" の '件' 等) が続くなら未クォート文字列として扱わせる。
    /// </summary>
    private static bool IsNumberToken(string t, int p, bool allowCloseBrace)
    {
        var end = ScanNumberEnd(t, p);
        SkipWs(t, ref end);

        return end >= t.Length
            || t[end] == ','
            || (allowCloseBrace && t[end] == '}');
    }

    /// <summary>
    /// ダブルクォートとして扱う文字かどうか。
    /// Excel/Wordのオートコレクトで混入するスマートクォート (“ ”) と
    /// 全角ダブルクォート (＂) も受け付ける。
    /// </summary>
    private static bool IsDq(char c)
    {
        return c is '"' or '“' or '”' or '＂'; // " “ ” ＂
    }

    /// <summary>
    /// シングルクォートとして扱う文字かどうか (スマートクォート ‘ ’ 含む)。
    /// </summary>
    private static bool IsSq(char c)
    {
        return c is '\'' or '‘' or '’'; // ' ‘ ’
    }

    /// <summary>
    /// クォートとして扱う文字すべて (ダブル/シングル、スマート/全角含む)。
    /// エスケープ判定と LocatorResolver.EscapeText で使う。
    /// </summary>
    internal static bool IsQuoteChar(char c)
    {
        return IsDq(c) || IsSq(c);
    }

    /// <summary>
    /// 識別子がASCII文字だけで構成されているかどうか。
    /// ネストLocator式のメソッド名 (GetByText 等) は必ずASCIIなので、
    /// 日本語テキスト+括弧との判別に使う。
    /// </summary>
    private static bool IsAsciiIdent(string ident)
    {
        foreach (var c in ident)
        {
            if (c >= 128)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// "new" の位置から先読みして、new Regex(...) 形式かどうか判定する (pは進めない)。
    /// 完全修飾 (System.Text.RegularExpressions.Regex) にも対応し、
    /// "Regexx" のような前方一致の誤検出はしない。
    /// </summary>
    private static bool IsRegexNew(string t, int p)
    {
        var q = p + 3; // "new" の直後
        SkipWs(t, ref q);

        var ident = ReadIdent(t, ref q);
        if (ident.Length == 0)
        {
            return false;
        }

        var last = ident;
        if (ident.Contains('.'))
        {
            last = ident.Split('.').Last();
        }
        return last.Equals("Regex", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Excelやコピペ由来の不可視文字 (BOM、ゼロ幅スペース、LRM/RLM) を除去する。
    /// これらは見た目で気付けないままメソッド名やパラメータの解釈を壊すため。
    /// </summary>
    internal static string StripInvisible(string s)
    {
        // 高速パス: 不可視文字を含まない入力はそのまま返す
        if (s.IndexOfAny(InvisibleChars) < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (Array.IndexOf(InvisibleChars, c) < 0)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    // 注意: C#12のコレクション式 [...] は使わない
    // (C#10/.NET 6既定のプロジェクトへ単一ファイル移行できるようにするため)
    private static readonly char[] InvisibleChars = new char[]
    {
        '\uFEFF', // BOM (バイトオーダーマーク)
        '\u200B', // ゼロ幅スペース
        '\u200E', // 左から右マーク (LRM)
        '\u200F', // 右から左マーク (RLM)
        '\u2060', // ワードジョイナー
        '\u00AD', // ソフトハイフン
    };

    /// <summary>
    /// 指定位置から先が、指定文字列で始まるかチェックする (大文字小文字無視)。
    /// 「I」は IgnoreCase の略。
    /// </summary>
    private static bool AtI(string t, int p, string prefix)
    {
        return p <= t.Length && t.AsSpan(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 空白文字 (スペース、タブ、改行、全角スペース等) をスキップする。
    /// </summary>
    private static void SkipWs(string t, ref int p)
    {
        while (p < t.Length && char.IsWhiteSpace(t[p]))
        {
            p++;
        }
    }

    /// <summary>
    /// パース完了後に未消費の入力が残っていないか確認する。
    /// 残っていれば書き間違いの可能性が高いのでエラーにする。
    /// </summary>
    private static void EnsureEnd(string t, int p)
    {
        SkipWs(t, ref p);

        if (p < t.Length)
        {
            throw new FormatException(
                $"解釈できない入力が残っています (位置 {p}): '{t[p..]}'");
        }
    }
}
