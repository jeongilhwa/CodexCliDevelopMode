namespace InterceptKit.Tests.Windows;

using FluentAssertions;

[TestClass]
public sealed class WebView2RequestParsingTests
{
    [TestMethod]
    public void ParsePausedRequest_CreatesInterceptedRequestAndFetchMetadata()
    {
        const string payload = """
        {
          "requestId": "fetch-1",
          "resourceType": "XHR",
          "request": {
            "url": "https://example.com/api/orders",
            "method": "POST",
            "headers": {
              "content-type": "application/json",
              "x-test": "yes"
            },
            "postData": "{\"id\":1}"
          }
        }
        """;

        var parsed = InterceptKit.WebView.Windows.WebView2PausedRequestParser.Parse(payload);

        parsed.FetchRequestId.Should().Be("fetch-1");
        parsed.Request.Method.Should().Be("POST");
        parsed.Request.Url.Should().Be("https://example.com/api/orders");
        parsed.Request.Headers["content-type"].Should().Be("application/json");
        parsed.Request.Body.Should().Be("{\"id\":1}");
    }
}
