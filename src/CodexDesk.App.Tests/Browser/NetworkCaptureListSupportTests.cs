namespace CodexDesk.App.Tests.Browser;

using CodexDesk.Services;
using FluentAssertions;

[TestClass]
public sealed class NetworkCaptureListSupportTests
{
    [TestMethod]
    public void AppendEntry_AppendsNewestItemToEndAndTrimsOldestItem()
    {
        List<string> entries = ["001", "002", "003"];

        NetworkCaptureListSupport.AppendEntry(entries, "004", maxEntries: 3);

        entries.Should().Equal("002", "003", "004");
    }

    [TestMethod]
    public void IsPinnedToBottom_ReturnsTrue_WhenLastVisibleItemIsAtEnd()
    {
        var result = NetworkCaptureListSupport.IsPinnedToBottom(
            lastVisibleItemIndex: 9,
            itemCount: 10);

        result.Should().BeTrue();
    }

    [TestMethod]
    public void IsPinnedToBottom_ReturnsFalse_WhenViewportIsInMiddle()
    {
        var result = NetworkCaptureListSupport.IsPinnedToBottom(
            lastVisibleItemIndex: 4,
            itemCount: 10);

        result.Should().BeFalse();
    }
}
