// 8回目の総点検 (コードレビュー) で見つかった課題のテスト。
//   - NormalizeForParse が全角マイナス '－'(U+FF0D)/'−'(U+2212) を半角化していなかった
//   - ReadNumber が double の桁あふれ (Infinity) を黙って通していた
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class EighthPassTests
{
    private readonly MockFixture f = new();

    // ===== 全角マイナス付き数値 =====

    [Fact]
    public void 全角マイナスと全角数字の負数をintとして扱う()
    {
        // 修正前: NormalizeForParse の畳み込み範囲 (U+FF01..U+FF5E) に
        //         '－'(U+FF0D) が含まれず int.Parse("－1") が失敗していた
        f.Locator.Nth(Arg.Any<int>()).Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Locator, "Nth", "－１");

        Assert.Same(f.Inner, result);
        f.Locator.Received(1).Nth(-1);
    }

    [Fact]
    public void マイナス記号U2212の負数もintとして扱う()
    {
        f.Locator.Nth(Arg.Any<int>()).Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Locator, "Nth", "−２");

        Assert.Same(f.Inner, result);
        f.Locator.Received(1).Nth(-2);
    }

    [Fact]
    public void 半角の負数は引き続きintとして扱う()
    {
        f.Locator.Nth(Arg.Any<int>()).Returns(f.Inner);

        var result = LocatorResolver.Resolve(f.Locator, "Nth", "-3");

        Assert.Same(f.Inner, result);
        f.Locator.Received(1).Nth(-3);
    }

    // ===== double の桁あふれ =====

    [Fact]
    public void 巨大な小数リテラルはInfinityにせずFormatException()
    {
        // 修正前: double.Parse は OverflowException を投げず Infinity を返すため
        //         ReadNumber の catch をすり抜け、Infinity が primary になっていた
        var huge = new string('9', 400) + ".0";

        Assert.Throws<FormatException>(() => ParameterParser.Parse(huge));
    }

    [Fact]
    public void 通常の小数は引き続きdoubleとして扱う()
    {
        var (primary, _) = ParameterParser.Parse("2.5");

        Assert.Equal(2.5, primary);
    }
}
