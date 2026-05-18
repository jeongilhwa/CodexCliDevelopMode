namespace InterceptKit.WebView.Windows;

using InterceptKit.Core;
using System.Text.Json.Nodes;

public sealed record WebView2PausedRequest(
    string FetchRequestId,
    InterceptedRequest Request,
    string? ResourceType);

public static class WebView2PausedRequestParser
{
    public static WebView2PausedRequest Parse(string payload)
    {
        var root = JsonNode.Parse(payload)?.AsObject()
            ?? throw new InvalidOperationException("Fetch.requestPaused payload was empty.");

        var fetchRequestId = root["requestId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Fetch.requestPaused payload did not contain requestId.");

        var request = root["request"]?.AsObject()
            ?? throw new InvalidOperationException("Fetch.requestPaused payload did not contain request details.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request["headers"] is JsonObject headerObject)
        {
            foreach (var header in headerObject)
            {
                if (header.Value is not null)
                {
                    headers[header.Key] = header.Value.ToString();
                }
            }
        }

        return new WebView2PausedRequest(
            fetchRequestId,
            new InterceptedRequest(
                fetchRequestId,
                request["method"]?.GetValue<string>() ?? "GET",
                request["url"]?.GetValue<string>() ?? string.Empty,
                headers,
                request["postData"]?.GetValue<string>(),
                DateTimeOffset.UtcNow),
            root["resourceType"]?.GetValue<string>());
    }
}
