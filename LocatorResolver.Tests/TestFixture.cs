// テスト共通のモック構築ヘルパー。
// 実ブラウザは使わず、NSubstituteでIPage/ILocator/IFrameLocatorを差し替える。
using Microsoft.Playwright;
using NSubstitute;

namespace LocatorResolverTests;

public class MockFixture
{
    public IPage Page { get; } = Substitute.For<IPage>();
    public ILocator Locator { get; } = Substitute.For<ILocator>();
    public ILocator Inner { get; } = Substitute.For<ILocator>();
    public IFrameLocator Frame { get; } = Substitute.For<IFrameLocator>();

    public MockFixture()
    {
        // ネスト式の評価でルートページを辿れるように接続しておく
        Locator.Page.Returns(Page);
        Frame.Owner.Returns(Locator);
    }
}
