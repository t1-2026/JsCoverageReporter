// ファズテスト。
// ランダム生成した入力に対して「定義エラーは必ず FormatException /
// ArgumentException として報告され、それ以外の例外 (IndexOutOfRange等の
// パーサ内部クラッシュ) は絶対に漏れない」ことを機械的に保証する。
// シードは固定 (失敗時に再現可能)。
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class FuzzTests
{
    // パーサが反応しやすい文字を多めに混ぜた文字プール
    private const string Pool =
        "ab1Z9 \t\n()[]{}.,=\"'\\@#-_:|/!?<>*&%$^~`+;" +
        "あ漢ア🎉“”‘’＂＝　、，（）" +
        "newRegxtrufalsenil";

    private static string RandomString(Random rng, int maxLength)
    {
        var length = rng.Next(0, maxLength);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = Pool[rng.Next(Pool.Length)];
        }
        return new string(chars);
    }

    [Fact]
    public void Parseはどんな入力でも想定外の例外を投げない()
    {
        var rng = new Random(20260612);

        for (var i = 0; i < 30000; i++)
        {
            var input = RandomString(rng, 80);
            try
            {
                ParameterParser.Parse(input);
            }
            catch (FormatException) { }   // 定義エラーとして想定内
            catch (ArgumentException) { } // Regexパターン不正等も想定内
            // それ以外 (IndexOutOfRange, NullReference, StackOverflow...) は
            // パーサのバグなのでテスト失敗として伝播させる
        }
    }

    [Fact]
    public void ParseChainはどんな入力でも想定外の例外を投げない()
    {
        var rng = new Random(8128);

        for (var i = 0; i < 30000; i++)
        {
            var input = RandomString(rng, 80);
            try
            {
                ParameterParser.ParseChain(input);
            }
            catch (FormatException) { }
            catch (ArgumentException) { }
        }
    }

    [Fact]
    public void TryResolveはどんな入力でも例外を漏らさない()
    {
        // TryResolve は定義エラーを false で返す契約なので、
        // ランダム入力で例外が漏れたら契約違反 = バグ
        var f = new MockFixture();
        f.Page.GetByRole(Arg.Any<AriaRole>(), Arg.Any<PageGetByRoleOptions>()).Returns(f.Locator);
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Locator);
        f.Page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions>()).Returns(f.Locator);
        f.Page.GetByLabel(Arg.Any<string>(), Arg.Any<PageGetByLabelOptions>()).Returns(f.Locator);
        f.Page.FrameLocator(Arg.Any<string>()).Returns(f.Frame);
        f.Locator.First.Returns(f.Inner);
        f.Locator.Nth(Arg.Any<int>()).Returns(f.Inner);

        string[] methodPool =
            ["GetByText", "GetByRole", "Filter", "First", "Nth", "Locator", "ZZZ", "new", ""];

        var rng = new Random(42);

        for (var i = 0; i < 5000; i++)
        {
            // メソッド名: 既知の名前か、ランダム文字列 (チェーン形含む)
            var method = rng.Next(2) == 0
                ? methodPool[rng.Next(methodPool.Length)]
                : RandomString(rng, 30);

            var parameters = rng.Next(4) == 0 ? null : RandomString(rng, 60);

            // 例外が漏れたらこの行で落ちる (TryCoreの契約違反)
            LocatorResolver.TryResolve(f.Page, method, parameters, out _, out _);
        }
    }
}
