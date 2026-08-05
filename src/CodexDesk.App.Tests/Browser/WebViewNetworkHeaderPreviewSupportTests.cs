namespace CodexDesk.App.Tests.Browser;

using CodexDesk.Services;
using FluentAssertions;
using System.Text.Json.Nodes;

[TestClass]
public sealed class WebViewNetworkHeaderPreviewSupportTests
{
    [TestMethod]
    public void ToHeaderLines_HidesHttp2PseudoHeadersThatDuplicateUrlData()
    {
        var headers = new JsonObject
        {
            [":authority"] = "seller-acs.aliexpress.com",
            [":method"] = "GET",
            [":path"] = "/h5/api?data=%7B%22pageName%22%3A%22%2Fm_apps%2Fhomepage%2Fpop%22%7D",
            [":scheme"] = "https",
            ["Referer"] = "https://gsp.aliexpress.com/",
            ["User-Agent"] = "Mozilla/5.0"
        };

        var preview = WebViewNetworkHeaderPreviewSupport.ToHeaderLines(headers);

        preview.Should().Contain("Referer: https://gsp.aliexpress.com/");
        preview.Should().Contain("User-Agent: Mozilla/5.0");
        preview.Should().NotContain(":path:");
        preview.Should().NotContain("data=%7B");
    }

    [TestMethod]
    public void MergeHeaderPreviews_DeduplicatesHeadersByNameAndSkipsPseudoHeaders()
    {
        var merged = WebViewNetworkHeaderPreviewSupport.MergeHeaderPreviews(
            """
            Referer: https://gsp.aliexpress.com/
            User-Agent: Mozilla/5.0
            """,
            """
            :path: /h5/api?data=%7B%7D
            referer: https://gsp.aliexpress.com/
            accept-language: ko
            """);

        merged.Should().Be(
            """
            Referer: https://gsp.aliexpress.com/
            User-Agent: Mozilla/5.0
            accept-language: ko
            """);
    }
}
