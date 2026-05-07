namespace CodexDesk.App.Tests.Browser;

using CodexDesk.Services;
using FluentAssertions;

[TestClass]
public sealed class NetworkCaptureSelectionSupportTests
{
    [TestMethod]
    public void ResolveSelectionAfterRefresh_PreservesRememberedSelectionWhenStillVisible()
    {
        var first = "first";
        var second = "second";

        var result = NetworkCaptureSelectionSupport.ResolveSelectionAfterRefresh(
            currentControlSelection: null,
            rememberedSelection: second,
            visibleEntries: [first, second],
            preserveSelection: true);

        result.SelectedEntry.Should().Be(second);
        result.KeepCurrentDetails.Should().BeTrue();
    }

    [TestMethod]
    public void ResolveSelectionAfterRefresh_LeavesDetailsUntouchedWhenControlLosesSelectionDuringAppend()
    {
        var selected = "selected";

        var result = NetworkCaptureSelectionSupport.ResolveSelectionAfterRefresh(
            currentControlSelection: null,
            rememberedSelection: selected,
            visibleEntries: [selected, "newest"],
            preserveSelection: true);

        result.SelectedEntry.Should().Be(selected);
        result.KeepCurrentDetails.Should().BeTrue();
    }

    [TestMethod]
    public void ResolveSelectionAfterRefresh_FallsBackToFirstVisibleEntryWhenSelectionShouldNotBePreserved()
    {
        var result = NetworkCaptureSelectionSupport.ResolveSelectionAfterRefresh(
            currentControlSelection: "selected",
            rememberedSelection: "selected",
            visibleEntries: ["first", "second"],
            preserveSelection: false);

        result.SelectedEntry.Should().Be("first");
        result.KeepCurrentDetails.Should().BeFalse();
    }
}
