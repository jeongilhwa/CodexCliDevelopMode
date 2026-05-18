namespace InterceptKit.Tests;

using FluentAssertions;

[TestClass]
public sealed class ProjectLayoutTests
{
    [TestMethod]
    public void PlaceholderProjectsExposeDocumentedExpansionPoints()
    {
        typeof(InterceptKit.Http.HttpInterceptionNotes).FullName.Should().NotBeNull();
        typeof(InterceptKit.WebView.Android.PlatformNotes).FullName.Should().NotBeNull();
        typeof(InterceptKit.WebView.iOS.PlatformNotes).FullName.Should().NotBeNull();
        typeof(InterceptKit.WebView.MacCatalyst.PlatformNotes).FullName.Should().NotBeNull();
    }
}
