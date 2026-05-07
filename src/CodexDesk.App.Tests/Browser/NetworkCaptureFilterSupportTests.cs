namespace CodexDesk.App.Tests.Browser;

using CodexDesk.Services;
using FluentAssertions;

[TestClass]
public sealed class NetworkCaptureFilterSupportTests
{
    [TestMethod]
    [DataRow("https", "xhr", "application/json", "/api/orders", 200, "Api")]
    [DataRow("https", "fetch", "application/json", "/api/orders", 200, "Api")]
    [DataRow("https", "document", "text/html", "/dashboard", 200, "Document")]
    [DataRow("https", "script", "application/javascript", "/assets/app.js", 200, "JavaScript")]
    [DataRow("https", "other", "text/javascript", "/assets/app.js?v=1", 200, "JavaScript")]
    [DataRow("https", "stylesheet", "text/css", "/assets/site.css", 200, "Css")]
    [DataRow("https", "image", "image/png", "/assets/logo.png", 200, "Image")]
    [DataRow("https", "media", "video/mp4", "/assets/intro.mp4", 200, "Media")]
    [DataRow("https", "font", "font/woff2", "/assets/app.woff2", 200, "Font")]
    [DataRow("https", "other", "application/octet-stream", "/download/file.bin", 200, "Other")]
    public void Classify_ReturnsExpectedKind(
        string scheme,
        string resourceType,
        string mimeType,
        string path,
        int status,
        string expected)
    {
        var actual = NetworkCaptureFilterSupport.Classify(scheme, resourceType, mimeType, path, status.ToString());

        actual.Should().Be(Enum.Parse<NetworkCaptureFilterKind>(expected));
    }

    [TestMethod]
    public void Classify_PrefersErrorForFailedRequest()
    {
        var actual = NetworkCaptureFilterSupport.Classify(
            "https",
            "xhr",
            "application/json",
            "/api/orders",
            "500");

        actual.Should().Be(NetworkCaptureFilterKind.Error);
    }

    [TestMethod]
    [DataRow("All", "Image", true)]
    [DataRow("Image", "Image", true)]
    [DataRow("Api", "Image", false)]
    public void Matches_AppliesAllOrExactKind(string selected, string actual, bool expected)
    {
        NetworkCaptureFilterSupport.Matches(
                Enum.Parse<NetworkCaptureFilterKind>(selected),
                Enum.Parse<NetworkCaptureFilterKind>(actual))
            .Should().Be(expected);
    }

    [TestMethod]
    [DataRow("", "GET", "200", "www.youtube.com", "/watch?v=1", "https://www.youtube.com/watch?v=1", "xhr", "application/json", "HEADERS:\nAccept: */*", "HEADERS:\nContent-Type: text/html", true)]
    [DataRow("youtube", "GET", "200", "www.youtube.com", "/watch?v=1", "https://www.youtube.com/watch?v=1", "xhr", "application/json", "HEADERS:\nAccept: */*", "HEADERS:\nContent-Type: text/html", true)]
    [DataRow("POST", "POST", "200", "api.example.com", "/v1/log_event", "https://api.example.com/v1/log_event", "fetch", "application/json", "HEADERS:\nAccept: */*", "HEADERS:\nContent-Type: application/json", true)]
    [DataRow("404", "GET", "404", "cdn.example.com", "/missing.png", "https://cdn.example.com/missing.png", "image", "image/png", "HEADERS:\nAccept: image/png", "HEADERS:\nContent-Type: image/png", true)]
    [DataRow("json", "POST", "200", "api.example.com", "/v1/log_event", "https://api.example.com/v1/log_event", "fetch", "application/json", "HEADERS:\nAccept: */*", "HEADERS:\nContent-Type: application/json", true)]
    [DataRow("https://seller-acs.aliexpress.com/h5/mtop.aliexpress.seller.iopnotify.defence.pull/1.0/?jsv=2.7.2", "GET", "200", "seller-acs.aliexpress.com", "/h5/mtop.aliexpress.seller.iopnotify.defence.pull/1.0/?jsv=2.7.2", "https://seller-acs.aliexpress.com/h5/mtop.aliexpress.seller.iopnotify.defence.pull/1.0/?jsv=2.7.2", "fetch", "application/json", "HEADERS:\nAccept: */*", "HEADERS:\nContent-Type: application/json", true)]
    [DataRow("sec", "GET", "200", "www.youtube.com", "/watch?v=1", "https://www.youtube.com/watch?v=1", "xhr", "application/json", "HEADERS:\nSec-Fetch-Site: same-origin", "HEADERS:\nServer: test", true)]
    [DataRow("missing", "GET", "200", "www.youtube.com", "/watch?v=1", "https://www.youtube.com/watch?v=1", "xhr", "application/json", "HEADERS:\nAccept: */*", "HEADERS:\nContent-Type: text/html", false)]
    public void MatchesSearch_MatchesVisibleNetworkFields(
        string query,
        string method,
        string status,
        string host,
        string path,
        string fullUrl,
        string resourceType,
        string mimeType,
        string requestDetails,
        string responseDetails,
        bool expected)
    {
        NetworkCaptureFilterSupport.MatchesSearch(
                query,
                method,
                status,
                host,
                path,
                fullUrl,
                resourceType,
                mimeType,
                requestDetails,
                responseDetails)
            .Should().Be(expected);
    }

    [TestMethod]
    public void MaxEntries_IsOneThousand()
    {
        NetworkCaptureFilterSupport.MaxEntries.Should().Be(1000);
    }

    [TestMethod]
    [DataRow("Api", "Data")]
    [DataRow("Document", "Page")]
    [DataRow("JavaScript", "JS")]
    public void FormatLabel_ReturnsUserFacingLabel(string kind, string expected)
    {
        NetworkCaptureFilterSupport.FormatLabel(Enum.Parse<NetworkCaptureFilterKind>(kind))
            .Should().Be(expected);
    }
}
