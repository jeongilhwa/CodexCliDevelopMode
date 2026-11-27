using System.Collections.Concurrent;
using System.Text.Json;
using CodexDesk.Services;
using FluentAssertions;

namespace CodexDesk.App.Tests.Browser;

[TestClass]
public sealed class BrowserAutomationServiceTests
{
    [TestMethod]
    [DataRow("javascript:alert(1)")]
    [DataRow("file:///C:/private.txt")]
    public async Task Navigate_rejects_non_web_urls_before_reaching_the_browser(string url)
    {
        var service = new BrowserAutomationService();
        var called = false;
        service.Attach((_, _) => { called = true; return Task.FromResult(new BrowserToolResult(true, "opened")); });

        var result = await service.ExecuteAsync("browser_navigate", JsonSerializer.SerializeToElement(new { url }));

        result.Success.Should().BeFalse();
        called.Should().BeFalse();
    }

    [TestMethod]
    public async Task Search_encodes_the_query_in_the_requested_search_engine_url()
    {
        var service = new BrowserAutomationService();
        BrowserAutomationAction? received = null;
        service.Attach((action, _) => { received = action; return Task.FromResult(new BrowserToolResult(true, "opened")); });

        var result = await service.ExecuteAsync("browser_search", JsonSerializer.SerializeToElement(new { engine = "naver", query = "사탕 & 초콜릿" }));

        result.Success.Should().BeTrue();
        received.Should().NotBeNull();
        received!.Url.Should().Be("https://search.naver.com/search.naver?query=%EC%82%AC%ED%83%95%20%26%20%EC%B4%88%EC%BD%9C%EB%A6%BF");
    }

    [TestMethod]
    public async Task Search_rejects_unknown_engines_before_reaching_the_browser()
    {
        var service = new BrowserAutomationService();
        var called = false;
        service.Attach((_, _) => { called = true; return Task.FromResult(new BrowserToolResult(true, "opened")); });

        var result = await service.ExecuteAsync("browser_search", JsonSerializer.SerializeToElement(new { engine = "unknown", query = "사탕" }));

        result.Success.Should().BeFalse();
        called.Should().BeFalse();
    }

    [TestMethod]
    public async Task Click_and_type_reject_oversized_arguments()
    {
        var service = new BrowserAutomationService();
        var called = false;
        service.Attach((_, _) => { called = true; return Task.FromResult(new BrowserToolResult(true, "done")); });

        var click = await service.ExecuteAsync("browser_click", JsonSerializer.SerializeToElement(new { selector = new string('a', 513) }));
        var type = await service.ExecuteAsync("browser_type", JsonSerializer.SerializeToElement(new { selector = "input", text = new string('a', 10001) }));

        click.Success.Should().BeFalse();
        type.Success.Should().BeFalse();
        called.Should().BeFalse();
    }

    [TestMethod]
    public async Task Screenshot_reaches_browser_host_without_arguments()
    {
        var service = new BrowserAutomationService();
        BrowserAutomationAction? received = null;
        service.Attach((action, _) =>
        {
            received = action;
            return Task.FromResult(new BrowserToolResult(true, "captured"));
        });

        var result = await service.ExecuteAsync("browser_screenshot", JsonSerializer.SerializeToElement(new { }));

        result.Success.Should().BeTrue();
        received.Should().NotBeNull();
        received!.Tool.Should().Be("browser_screenshot");
    }

    [TestMethod]
    public async Task Coordinate_click_forwards_a_valid_screenshot_and_position()
    {
        var service = new BrowserAutomationService();
        BrowserAutomationAction? received = null;
        service.Attach((action, _) =>
        {
            received = action;
            return Task.FromResult(new BrowserToolResult(true, "clicked"));
        });
        const string screenshotId = "3f660e91-61e5-4d2a-8c05-8e2e231114b4";

        var result = await service.ExecuteAsync("browser_click_at",
            JsonSerializer.SerializeToElement(new { screenshotId, x = 320, y = 180 }));

        result.Success.Should().BeTrue();
        received.Should().NotBeNull();
        received!.Tool.Should().Be("browser_click_at");
        received.ScreenshotId.Should().Be(screenshotId);
        received.X.Should().Be(320);
        received.Y.Should().Be(180);
    }

    [TestMethod]
    [DataRow(-1, 100)]
    [DataRow(100, -1)]
    [DataRow(10001, 100)]
    [DataRow(100, 10001)]
    public async Task Coordinate_click_rejects_out_of_range_positions_before_reaching_browser(int x, int y)
    {
        var service = new BrowserAutomationService();
        var called = false;
        service.Attach((_, _) => { called = true; return Task.FromResult(new BrowserToolResult(true, "clicked")); });

        var result = await service.ExecuteAsync("browser_click_at", JsonSerializer.SerializeToElement(new
        {
            screenshotId = "3f660e91-61e5-4d2a-8c05-8e2e231114b4", x, y
        }));

        result.Success.Should().BeFalse();
        called.Should().BeFalse();
    }

    [TestMethod]
    public async Task Coordinate_click_rejects_missing_or_invalid_screenshot_id_before_reaching_browser()
    {
        var service = new BrowserAutomationService();
        var called = false;
        service.Attach((_, _) => { called = true; return Task.FromResult(new BrowserToolResult(true, "clicked")); });

        var missing = await service.ExecuteAsync("browser_click_at", JsonSerializer.SerializeToElement(new { x = 1, y = 2 }));
        var invalid = await service.ExecuteAsync("browser_click_at",
            JsonSerializer.SerializeToElement(new { screenshotId = "not-a-guid", x = 1, y = 2 }));

        missing.Success.Should().BeFalse();
        invalid.Success.Should().BeFalse();
        called.Should().BeFalse();
    }

    [TestMethod]
    public async Task Focused_typing_forwards_text_to_browser_host()
    {
        var service = new BrowserAutomationService();
        BrowserAutomationAction? received = null;
        service.Attach((action, _) =>
        {
            received = action;
            return Task.FromResult(new BrowserToolResult(true, "typed"));
        });

        var result = await service.ExecuteAsync("browser_type_focused",
            JsonSerializer.SerializeToElement(new { text = "example-user" }));

        result.Success.Should().BeTrue();
        received.Should().NotBeNull();
        received!.Tool.Should().Be("browser_type_focused");
        received.Text.Should().Be("example-user");
    }

    [TestMethod]
    public async Task Focused_typing_rejects_missing_or_oversized_text_before_reaching_browser()
    {
        var service = new BrowserAutomationService();
        var called = false;
        service.Attach((_, _) => { called = true; return Task.FromResult(new BrowserToolResult(true, "typed")); });

        var missing = await service.ExecuteAsync("browser_type_focused", JsonSerializer.SerializeToElement(new { }));
        var oversized = await service.ExecuteAsync("browser_type_focused",
            JsonSerializer.SerializeToElement(new { text = new string('x', 10001) }));

        missing.Success.Should().BeFalse();
        oversized.Success.Should().BeFalse();
        called.Should().BeFalse();
    }

    [TestMethod]
    public async Task Browser_actions_wait_for_the_previous_action_to_finish()
    {
        var service = new BrowserAutomationService();
        var calls = new ConcurrentQueue<string>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Attach(async (action, _) =>
        {
            calls.Enqueue(action.Tool);
            if (action.Tool == "browser_navigate")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }

            return new BrowserToolResult(true, "done");
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = service.ExecuteAsync("browser_navigate", JsonSerializer.SerializeToElement(new { url = "https://naver.com" }));

        try
        {
            await firstStarted.Task.WaitAsync(timeout.Token);
            var second = service.ExecuteAsync("browser_read_page", JsonSerializer.SerializeToElement(new { }));

            calls.ToArray().Should().ContainSingle().Which.Should().Be("browser_navigate");
            second.IsCompleted.Should().BeFalse();

            releaseFirst.TrySetResult();
            (await first.WaitAsync(timeout.Token)).Success.Should().BeTrue();
            (await second.WaitAsync(timeout.Token)).Success.Should().BeTrue();
            calls.ToArray().Should().Equal("browser_navigate", "browser_read_page");
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
    }
}
