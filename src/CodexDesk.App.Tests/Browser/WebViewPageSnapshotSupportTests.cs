namespace CodexDesk.App.Tests.Browser;

using CodexDesk.Services;
using CodexDesk.Tests.TestSupport;
using FluentAssertions;

[TestClass]
public sealed class WebViewPageSnapshotSupportTests
{
    [TestMethod]
    public void BuildScript_collects_current_dom_styles_and_scripts()
    {
        var script = WebViewPageSnapshotScriptBuilder.Build();

        script.Should().Contain("(() =>");
        script.Should().NotContain("(async () =>");
        script.Should().Contain("document.documentElement.outerHTML");
        script.Should().Contain("document.querySelectorAll('link[rel=\"stylesheet\"]')");
        script.Should().Contain("document.querySelectorAll('script')");
        script.Should().Contain("XMLHttpRequest");
        script.Should().NotContain("fetch(url");
    }

    [TestMethod]
    public async Task SaveAsync_writes_page_assets_manifest_and_prompt()
    {
        using var tempDirectory = new TemporaryDirectory();
        var snapshot = new WebViewPageSnapshotPayload(
            "https://www.esmplus.com/Home/v2",
            "ESM Plus",
            "<html><head></head><body>ok</body></html>",
            [
                new WebViewPageAssetSnapshot("inline", "", "body{color:red}", "")
            ],
            [
                new WebViewPageAssetSnapshot("external", "https://www.esmplus.com/app.js", "console.log('ok');", "")
            ]);

        var result = await WebViewPageSnapshotWriter.SaveAsync(snapshot, tempDirectory.Path);

        result.DirectoryPath.Should().StartWith(Path.Combine(tempDirectory.Path, ".tmp-build", "web-snapshots"));
        File.Exists(Path.Combine(result.DirectoryPath, "page.html")).Should().BeTrue();
        File.Exists(Path.Combine(result.DirectoryPath, "styles", "001-inline.css")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(result.DirectoryPath, "scripts"), "001-external-*.js").Should().ContainSingle();
        File.Exists(Path.Combine(result.DirectoryPath, "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(result.DirectoryPath, "analysis-prompt.md")).Should().BeTrue();
        result.Prompt.Should().Contain("page.html");
        result.Prompt.Should().Contain("manifest.json");
        result.Prompt.Should().Contain("https://www.esmplus.com/Home/v2");
        result.Prompt.Should().Contain($"Snapshot folder: {Path.GetFileName(result.DirectoryPath)}");
        result.Prompt.Should().Contain($"Snapshot path: {result.DirectoryPath}");
    }

    [TestMethod]
    public async Task SaveAsync_handles_missing_asset_collections()
    {
        using var tempDirectory = new TemporaryDirectory();
        var snapshot = new WebViewPageSnapshotPayload(
            "https://www.esmplus.com/Home/v2",
            "ESM Plus",
            "<html></html>",
            null!,
            null!);

        var result = await WebViewPageSnapshotWriter.SaveAsync(snapshot, tempDirectory.Path);

        File.Exists(Path.Combine(result.DirectoryPath, "page.html")).Should().BeTrue();
        File.Exists(Path.Combine(result.DirectoryPath, "manifest.json")).Should().BeTrue();
        result.Prompt.Should().Contain("styles/*.css (0 files)");
        result.Prompt.Should().Contain("scripts/*.js (0 files)");
    }

    [TestMethod]
    public async Task SaveAsync_truncates_long_asset_file_names()
    {
        using var tempDirectory = new TemporaryDirectory();
        var longName = new string('a', 320);
        var snapshot = new WebViewPageSnapshotPayload(
            "https://www.esmplus.com/Home/v2",
            "ESM Plus",
            "<html></html>",
            [],
            [
                new WebViewPageAssetSnapshot("external", $"https://www.esmplus.com/{longName}.js", "console.log('ok');", "")
            ]);

        var result = await WebViewPageSnapshotWriter.SaveAsync(snapshot, tempDirectory.Path);

        var scriptFiles = Directory.GetFiles(Path.Combine(result.DirectoryPath, "scripts"), "*.js");
        scriptFiles.Should().ContainSingle();
        Path.GetFileName(scriptFiles[0]).Should().StartWith("001-external-");
        Path.GetFileName(scriptFiles[0]).Length.Should().BeLessThan(32);
    }
}
