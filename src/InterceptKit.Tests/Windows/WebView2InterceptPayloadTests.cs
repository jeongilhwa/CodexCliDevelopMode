namespace InterceptKit.Tests.Windows;

using FluentAssertions;
using System.Text;
using System.Text.Json.Nodes;

[TestClass]
public sealed class WebView2InterceptPayloadTests
{
    [TestMethod]
    public void BuildContinueRequestPayload_EncodesHeadersAndBody()
    {
        var payload = InterceptKit.WebView.Windows.WebView2InterceptPayloadBuilder.BuildContinueRequestPayload(
            "fetch-request-1",
            new InterceptKit.Core.EditableInterceptedRequest(
                "POST",
                "https://example.com/edited",
                new Dictionary<string, string>
                {
                    ["content-type"] = "application/json",
                    ["x-test"] = "yes"
                },
                "{\"ok\":true}"),
            interceptResponse: true);

        var json = JsonNode.Parse(payload)!.AsObject();
        json["requestId"]!.GetValue<string>().Should().Be("fetch-request-1");
        json["method"]!.GetValue<string>().Should().Be("POST");
        json["url"]!.GetValue<string>().Should().Be("https://example.com/edited");
        json["interceptResponse"]!.GetValue<bool>().Should().BeTrue();
        Encoding.UTF8.GetString(Convert.FromBase64String(json["postData"]!.GetValue<string>()))
            .Should()
            .Be("{\"ok\":true}");

        var headers = json["headers"]!.AsArray();
        headers.Should().HaveCount(2);
    }

    [TestMethod]
    public void BuildFailRequestPayload_UsesAbortedReason()
    {
        var payload = InterceptKit.WebView.Windows.WebView2InterceptPayloadBuilder.BuildFailRequestPayload("fetch-request-2");
        var json = JsonNode.Parse(payload)!.AsObject();

        json["requestId"]!.GetValue<string>().Should().Be("fetch-request-2");
        json["errorReason"]!.GetValue<string>().Should().Be("Aborted");
    }
}
