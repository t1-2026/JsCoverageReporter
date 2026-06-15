// 非機能要件のテスト: 並行実行・性能・巨大入力・API契約。
// データ駆動テストでの実運用 (大量行・並列実行) を想定した検証。
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class NonFunctionalTests
{
    // ===== API契約: TryResolve の null 注釈 =====

    [Fact]
    public void Try系メソッドのout引数にNotNullWhen属性が付いている()
    {
        // 成功時に locator!、失敗時に error! と書かなくて済むように、
        // コンパイラのnull解析へ契約を伝える
        var tryMethods = typeof(LocatorResolver).GetMethods()
            .Where(m => m.Name is "TryResolve" or "TryResolveFrame")
            .ToList();

        Assert.Equal(6, tryMethods.Count);

        foreach (var m in tryMethods)
        {
            var ps = m.GetParameters();
            var result = ps[^2]; // out locator / out frameLocator
            var error = ps[^1];  // out error

            var resultAttr = result.GetCustomAttribute<NotNullWhenAttribute>();
            var errorAttr = error.GetCustomAttribute<NotNullWhenAttribute>();

            Assert.True(resultAttr?.ReturnValue == true,
                $"{m} の {result.Name} に [NotNullWhen(true)] がありません");
            Assert.True(errorAttr?.ReturnValue == false,
                $"{m} の {error.Name} に [NotNullWhen(false)] がありません");
        }
    }

    // ===== 並行実行 =====

    [Fact]
    public void 並行実行でも例外なく解決できる()
    {
        // リフレクションキャッシュの初期化競合、ThreadStaticの深度カウンタ、
        // typo候補生成 (エラー経路) を複数スレッドから同時に叩く
        var errors = new ConcurrentBag<Exception>();

        Parallel.For(0, 8, thread =>
        {
            try
            {
                var f = new MockFixture();
                f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(f.Locator);
                f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Inner);
                f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(f.Locator);
                f.Locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(f.Inner);
                f.Locator.Nth(Arg.Any<int>()).Returns(f.Inner);

                for (var i = 0; i < 100; i++)
                {
                    Assert.NotNull(LocatorResolver.Resolve(f.Page, "GetByRole",
                        @"AriaRole.Button, new() { Name = ""送信"" }"));
                    Assert.NotNull(LocatorResolver.Resolve(f.Page, @"Locator(""#l"").Nth(2)"));
                    Assert.NotNull(LocatorResolver.Resolve(f.Locator, "Filter",
                        @"new() { Has = GetByText(""OK"") }"));

                    var ok = LocatorResolver.TryResolve(f.Page, "GetByTet", @"""x""",
                        out _, out var error);
                    Assert.False(ok);
                    Assert.NotNull(error);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        Assert.Empty(errors);
    }

    // ===== 性能 =====

    [Fact]
    public void 大量呼び出しが現実的な時間で完了する()
    {
        // データ駆動テストで数千行を解決するシナリオ。
        // キャッシュ劣化や偶発的な O(n^2) 化の回帰ガード (上限はかなり緩め)
        var f = new MockFixture();
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(f.Locator);

        // ウォームアップ (キャッシュ・JIT)
        for (var i = 0; i < 10; i++)
        {
            LocatorResolver.Resolve(f.Page, "GetByRole",
                @"AriaRole.Button, new() { Name = ""送信"", Exact = true }");
        }

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 5000; i++)
        {
            LocatorResolver.Resolve(f.Page, "GetByRole",
                @"AriaRole.Button, new() { Name = ""送信"", Exact = true }");
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"5000回の解決に {sw.ElapsedMilliseconds}ms かかりました (上限5000ms)");
    }

    // ===== 巨大入力 =====

    [Fact]
    public void 巨大な未クォートセレクタも線形時間で読める()
    {
        var selector = "#" + new string('a', 500_000);

        var sw = Stopwatch.StartNew();
        var (primary, _) = ParameterParser.Parse(selector);
        sw.Stop();

        Assert.Equal(selector, primary);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"500KBのパースに {sw.ElapsedMilliseconds}ms かかりました");
    }

    [Fact]
    public void 巨大なクォート文字列も読める()
    {
        var text = new string('x', 200_000);

        var (primary, _) = ParameterParser.Parse("\"" + text + "\"");

        Assert.Equal(text, primary);
    }

    [Fact]
    public void 長大なチェーンもスタックを溢れさせない()
    {
        // チェーン評価はループ実装なので、何千段あってもスタックは消費しない
        var f = new MockFixture();
        f.Locator.First.Returns(f.Locator);

        var expression = string.Join(".", Enumerable.Repeat("First", 2000));

        var result = LocatorResolver.Resolve(f.Locator, expression);

        Assert.Same(f.Locator, result);
    }
}
