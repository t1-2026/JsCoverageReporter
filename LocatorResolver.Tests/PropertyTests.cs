// プロパティベーステスト (往復律)。
// 「任意のテキスト T に対して Parse(EscapeText(T)) == T」という性質を
// ランダム生成で検証する。ファズ (クラッシュしない) より強い、
// 正しさそのものの保証。
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class PropertyTests
{
    // ===== エスケープの個別ケース =====

    [Fact]
    public void スマートクォートをエスケープして文字列内に書ける()
    {
        // 修正前: \“ がバックスラッシュごと残り、“ で文字列が途切れていた
        var (primary, _) = ParameterParser.Parse("\"a\\“b\\”c\"");
        Assert.Equal("a“b”c", primary);
    }

    [Fact]
    public void 全角クォートもエスケープできる()
    {
        var (primary, _) = ParameterParser.Parse("\"a\\＂b\"");
        Assert.Equal("a＂b", primary);
    }

    [Fact]
    public void シングルクォート文字列内のスマート単引用符エスケープ()
    {
        var (primary, _) = ParameterParser.Parse("'a\\’b'");
        Assert.Equal("a’b", primary);
    }

    [Fact]
    public void 通常のエスケープは従来どおり()
    {
        var (primary, _) = ParameterParser.Parse(@"""a\""b\\c\nd""");
        Assert.Equal("a\"b\\c\nd", primary);
    }

    [Fact]
    public void 正規表現エスケープは引き続き保持される()
    {
        var (primary, _) = ParameterParser.Parse(@"""a\d+b""");
        Assert.Equal(@"a\d+b", primary);
    }

    // ===== EscapeText ヘルパー =====

    [Fact]
    public void EscapeTextは任意のテキストを安全な文字列リテラルにする()
    {
        var escaped = LocatorResolver.EscapeText(@"He said ""hi"" and “bye”");
        var (primary, _) = ParameterParser.Parse(escaped);

        Assert.Equal(@"He said ""hi"" and “bye”", primary);
    }

    [Fact]
    public void EscapeTextをパラメータに埋め込んで使える()
    {
        var f = new MockFixture();
        f.Page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions>()).Returns(f.Locator);

        var text = "値は “100, new” です";
        var result = LocatorResolver.Resolve(f.Page, "GetByText",
            $"{LocatorResolver.EscapeText(text)}, new() {{ Exact = true }}");

        Assert.Same(f.Locator, result);
        f.Page.Received(1).GetByText(text,
            Arg.Is<PageGetByTextOptions>(o => o.Exact == true));
    }

    [Fact]
    public void EscapeTextにnullを渡すとArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => LocatorResolver.EscapeText(null!));
    }

    // ===== 往復律 (ランダム10,000件) =====

    [Fact]
    public void 任意のテキストはEscapeTextで往復できる()
    {
        // クォート全種・バックスラッシュ・改行・区切り文字・日本語・絵文字を含む
        const string pool = "ab1 \"“”＂'‘’\\\n\r\t,{}()=.@#新Regex🎉あ　ー";
        var rng = new Random(20260613);

        for (var i = 0; i < 10000; i++)
        {
            var length = rng.Next(0, 40);
            var chars = new char[length];
            for (var j = 0; j < length; j++)
            {
                chars[j] = pool[rng.Next(pool.Length)];
            }
            var original = new string(chars);

            var (primary, options) = ParameterParser.Parse(LocatorResolver.EscapeText(original));

            Assert.Equal(original, primary);
            Assert.Empty(options);
        }
    }
}
