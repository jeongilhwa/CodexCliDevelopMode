namespace InterceptKit.Tests.Windows;

using FluentAssertions;

[TestClass]
public sealed class WebView2InterceptFeatureTests
{
    [TestMethod]
    public void FeatureMetadata_DeclaresWindowsOnlyCapabilities()
    {
        var feature = InterceptKit.WebView.Windows.WebView2InterceptFeature.Default;

        feature.Name.Should().Be("WebView2");
        feature.Capabilities.HasFlag(InterceptKit.Core.InterceptCapabilities.EditHeaders).Should().BeTrue();
        feature.Capabilities.HasFlag(InterceptKit.Core.InterceptCapabilities.EditBody).Should().BeTrue();
        feature.Capabilities.HasFlag(InterceptKit.Core.InterceptCapabilities.CaptureResponse).Should().BeTrue();
    }
}
