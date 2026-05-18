namespace InterceptKit.Tests.WebView;

using FluentAssertions;

[TestClass]
public sealed class WebViewContractTests
{
    [TestMethod]
    public void WebViewInterceptOptions_DefaultToRequestInterceptionEnabled()
    {
        var options = new InterceptKit.WebView.WebViewInterceptOptions();

        options.InterceptRequests.Should().BeTrue();
        options.CaptureResponses.Should().BeTrue();
        options.MaxCapturedBodyLength.Should().BeGreaterThan(0);
        options.IncludedResourceTypes.Should().Contain(["Document", "Fetch", "XHR"]);
    }

    [TestMethod]
    public void PausedContext_CarriesSessionIdAndCapabilityFlags()
    {
        var sessionId = Guid.NewGuid();
        var context = new InterceptKit.WebView.PausedWebViewRequestContext(
            sessionId,
            InterceptKit.Core.InterceptCapabilities.EditHeaders | InterceptKit.Core.InterceptCapabilities.CaptureResponse);

        context.SessionId.Should().Be(sessionId);
        context.Capabilities.HasFlag(InterceptKit.Core.InterceptCapabilities.EditHeaders).Should().BeTrue();
        context.Capabilities.HasFlag(InterceptKit.Core.InterceptCapabilities.CaptureResponse).Should().BeTrue();
    }
}
