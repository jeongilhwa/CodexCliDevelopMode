using System.Text;

namespace CodexDesk.Services;

internal static class WebViewNetworkSessionExportSupport
{
    public static string BuildRawSessionText(
        string method,
        string url,
        string statusCode,
        string statusText,
        string requestDetails,
        string responseDetails)
    {
        var normalizedMethod = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
        var normalizedUrl = string.IsNullOrWhiteSpace(url) ? "-" : url.Trim();
        var normalizedStatusCode = string.IsNullOrWhiteSpace(statusCode) ? "-" : statusCode.Trim();
        var normalizedStatusText = ResolveStatusText(statusText, responseDetails);
        var requestHeaders = ExtractSection(requestDetails, "HEADERS:", "BODY:");
        var requestBody = ExtractSection(requestDetails, "BODY:", null);
        var responseHeaders = ExtractSection(responseDetails, "HEADERS:", "BODY:");
        var responseBody = ExtractSection(responseDetails, "BODY:", null);

        var builder = new StringBuilder()
            .Append(normalizedMethod)
            .Append(' ')
            .Append(normalizedUrl)
            .AppendLine(" HTTP/1.1");

        AppendOptionalBlock(builder, requestHeaders);
        builder.AppendLine();
        AppendOptionalBlock(builder, requestBody);

        builder
            .Append("HTTP/1.1 ")
            .Append(normalizedStatusCode);

        if (!string.IsNullOrWhiteSpace(normalizedStatusText) && normalizedStatusText != "-")
        {
            builder.Append(' ').Append(normalizedStatusText);
        }

        builder.AppendLine();
        AppendOptionalBlock(builder, responseHeaders);
        builder.AppendLine();
        AppendOptionalBlock(builder, responseBody);
        return builder.ToString().TrimEnd();
    }

    private static string ResolveStatusText(string statusText, string responseDetails)
    {
        if (!string.IsNullOrWhiteSpace(statusText) && statusText.Trim() != "-")
        {
            return statusText.Trim();
        }

        var statusLine = ExtractSection(responseDetails, "STATUS:", "DURATION:");
        if (string.IsNullOrWhiteSpace(statusLine) || statusLine == "-")
        {
            return "-";
        }

        var openParenIndex = statusLine.IndexOf('(');
        var closeParenIndex = statusLine.LastIndexOf(')');
        if (openParenIndex >= 0 && closeParenIndex > openParenIndex)
        {
            return statusLine[(openParenIndex + 1)..closeParenIndex].Trim();
        }

        return "-";
    }

    private static void AppendOptionalBlock(StringBuilder builder, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-")
        {
            return;
        }

        builder.AppendLine(value.Trim());
    }

    private static string ExtractSection(string text, string startMarker, string? endMarker)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "-";
        }

        var normalizedText = text.ReplaceLineEndings("\n");
        var startIndex = normalizedText.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return "-";
        }

        startIndex += startMarker.Length;
        var endIndex = string.IsNullOrEmpty(endMarker)
            ? normalizedText.Length
            : normalizedText.IndexOf(endMarker, startIndex, StringComparison.Ordinal);

        var chunk = endIndex < 0
            ? normalizedText[startIndex..]
            : normalizedText[startIndex..endIndex];

        var trimmed = chunk.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "-" : trimmed.ReplaceLineEndings(Environment.NewLine);
    }
}
