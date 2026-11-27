using System.Text.Json;
using CodexDesk.Services;
using FluentAssertions;

namespace CodexDesk.App.Tests.Browser;

[TestClass]
public sealed class NetworkCaptureAutomationTests
{
    [TestMethod]
    public async Task Capture_start_and_stop_are_forwarded_to_the_browser_host()
    {
        var service = new BrowserAutomationService();
        var actions = new List<string>();
        service.Attach((action, _) =>
        {
            actions.Add(action.Tool);
            return Task.FromResult(new BrowserToolResult(true, "done"));
        });

        var start = await service.ExecuteAsync("network_capture_start", JsonSerializer.SerializeToElement(new { }));
        var stop = await service.ExecuteAsync("network_capture_stop", JsonSerializer.SerializeToElement(new { }));

        start.Success.Should().BeTrue();
        stop.Success.Should().BeTrue();
        actions.Should().Equal("network_capture_start", "network_capture_stop");
    }

    [TestMethod]
    public async Task Capture_list_uses_bounded_pagination_arguments()
    {
        var service = new BrowserAutomationService();
        BrowserAutomationAction? received = null;
        service.Attach((action, _) =>
        {
            received = action;
            return Task.FromResult(new BrowserToolResult(true, "listed"));
        });

        var defaults = await service.ExecuteAsync("network_capture_list", JsonSerializer.SerializeToElement(new { }));
        defaults.Success.Should().BeTrue();
        received!.AfterSequence.Should().Be(0);
        received.Limit.Should().Be(50);

        var explicitPage = await service.ExecuteAsync("network_capture_list", JsonSerializer.SerializeToElement(new { afterSequence = 24, limit = 100 }));
        explicitPage.Success.Should().BeTrue();
        received!.AfterSequence.Should().Be(24);
        received.Limit.Should().Be(100);
    }

    [TestMethod]
    [DataRow(-1, 50)]
    [DataRow(0, 0)]
    [DataRow(0, 101)]
    public async Task Capture_list_rejects_invalid_pagination_before_reaching_the_host(int afterSequence, int limit)
    {
        var service = new BrowserAutomationService();
        var called = false;
        service.Attach((_, _) =>
        {
            called = true;
            return Task.FromResult(new BrowserToolResult(true, "listed"));
        });

        var result = await service.ExecuteAsync("network_capture_list", JsonSerializer.SerializeToElement(new { afterSequence, limit }));

        result.Success.Should().BeFalse();
        called.Should().BeFalse();
    }

    [TestMethod]
    public void Capture_summary_paginates_requests_and_never_exposes_headers_bodies_or_url_secrets()
    {
        var entries = new[]
        {
            Row(1, "https://shop.example/start?token=secret-token&key=secret-key&code=secret-code&session=secret-session&access_token=secret-access"),
            Row(2, "https://shop.example/blog?category=news"),
            Row(3, "https://shop.example/api/blog")
        };

        var page = NetworkCaptureSummaryFormatter.FormatPage(entries, true, afterSequence: 0, limit: 2);

        page.Should().Contain("Total: 3");
        page.Should().Contain("Next afterSequence: 2");
        page.Should().Contain("#1").And.Contain("#2").And.NotContain("#3");
        page.Should().Contain("GET").And.Contain("200").And.Contain("Document")
            .And.Contain("shop.example").And.Contain("/blog");
        page.Should().NotContain("secret-token").And.NotContain("secret-key")
            .And.NotContain("secret-code").And.NotContain("secret-session")
            .And.NotContain("secret-access").And.NotContain("private-header")
            .And.NotContain("private-request-body").And.NotContain("private-response-body");
    }

    private static NetworkCaptureRow Row(int sequence, string url) => new(
        "shop.example", new Uri(url).AbsolutePath, "200", "GET", "Document", "1 KB", "20 ms", url,
        sequence, "Authorization: private-header", "private-request-body", "Set-Cookie: private-header", "private-response-body");
}
