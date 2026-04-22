namespace CodexDesk.App.Tests.Browser;

using CodexDesk.Services;
using CodexDesk.Tests.TestSupport;
using FluentAssertions;

[TestClass]
public sealed class WebViewNetworkCaptureSnapshotSupportTests
{
    [TestMethod]
    public async Task SaveAsync_writes_request_response_files_manifest_and_prompt()
    {
        using var tempDirectory = new TemporaryDirectory();
        var entries = new[]
        {
            new WebViewNetworkCaptureEntrySnapshot(
                1,
                "[15:47:00] [cdp] POST 200 42.0ms https://www.esmplus.com/Home/ExtendLoginTime",
                "POST",
                "200",
                "www.esmplus.com",
                "/Home/ExtendLoginTime",
                "512 B",
                "42.0ms",
                "URL: https://www.esmplus.com/Home/ExtendLoginTime\nHEADERS:\naccept: application/json\nBODY:\n{}",
                "STATUS: 200 (OK)\nHEADERS:\ncontent-type: application/json\nBODY:\n{\"ok\":true}",
                "accept: application/json",
                "{}",
                "content-type: application/json",
                "{\"ok\":true}")
        };

        var result = await WebViewNetworkCaptureSnapshotWriter.SaveAsync(entries, tempDirectory.Path, "selected", "전체 요청 상세 포함");

        result.DirectoryPath.Should().StartWith(Path.Combine(tempDirectory.Path, ".tmp-build", "network-captures"));
        File.Exists(Path.Combine(result.DirectoryPath, "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(result.DirectoryPath, "summary.md")).Should().BeTrue();
        File.Exists(Path.Combine(result.DirectoryPath, "analysis-prompt.md")).Should().BeTrue();

        var requestFolders = Directory.GetDirectories(Path.Combine(result.DirectoryPath, "requests"));
        requestFolders.Should().ContainSingle();
        File.ReadAllText(Path.Combine(requestFolders[0], "request-body.txt")).Should().Be("{}");
        File.ReadAllText(Path.Combine(requestFolders[0], "response-body.txt")).Should().Be("{\"ok\":true}");
        result.Prompt.Should().Contain($"Network capture path: {result.DirectoryPath}");
    }
}
