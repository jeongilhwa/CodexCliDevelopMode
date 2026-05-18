namespace InterceptKit.Tests.WebView;

using FluentAssertions;
using InterceptKit.Core;

[TestClass]
public sealed class WebViewInterceptRuntimeStateTests
{
    [TestMethod]
    public void RuntimeState_TracksAttachPauseAndErrorSignals()
    {
        var state = new InterceptKit.WebView.WebViewInterceptRuntimeState();

        state.MarkWaitingForAttach();
        state.MarkAttached();
        state.MarkPaused(new InterceptedRequest(
            "request-1",
            "GET",
            "https://example.com/live",
            new Dictionary<string, string>(),
            null,
            DateTimeOffset.UtcNow));
        state.MarkError("Fetch.enable failed");

        state.IsEnabled.Should().BeTrue();
        state.IsAttached.Should().BeTrue();
        state.PausedRequestCount.Should().Be(1);
        state.LastPausedRequestUrl.Should().Be("https://example.com/live");
        state.LastError.Should().Be("Fetch.enable failed");
        state.StatusText.Should().Be("Fetch.enable failed");
    }

    [TestMethod]
    public void RuntimeState_DefaultsToDisabled()
    {
        var state = new InterceptKit.WebView.WebViewInterceptRuntimeState();

        state.IsEnabled.Should().BeFalse();
        state.IsAttached.Should().BeFalse();
        state.StatusText.Should().Be("Intercept OFF.");
    }
}
