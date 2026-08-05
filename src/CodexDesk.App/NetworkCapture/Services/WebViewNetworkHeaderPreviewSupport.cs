namespace CodexDesk.Services;

using System.Text.Json.Nodes;

internal static class WebViewNetworkHeaderPreviewSupport
{
    public static string ToHeaderLines(JsonNode? headersNode)
    {
        if (headersNode is not JsonObject headersObject || headersObject.Count == 0)
        {
            return "-";
        }

        var lines = new List<string>();
        foreach (var pair in headersObject.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (ShouldHideHeader(pair.Key))
            {
                continue;
            }

            var value = pair.Value switch
            {
                JsonValue jsonValue => jsonValue.ToString(),
                null => string.Empty,
                _ => pair.Value.ToJsonString()
            };

            lines.Add($"{pair.Key}: {value}");
        }

        return lines.Count == 0 ? "-" : string.Join("\n", lines);
    }

    public static string MergeHeaderPreviews(string? primary, string? secondary)
    {
        var lines = new List<string>();
        var seenHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddLines(lines, seenHeaderNames, primary);
        AddLines(lines, seenHeaderNames, secondary);

        return lines.Count == 0 ? "-" : string.Join("\n", lines);
    }

    private static void AddLines(List<string> target, HashSet<string> seenHeaderNames, string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Trim() == "-")
        {
            return;
        }

        foreach (var line in source.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var headerName = ExtractHeaderName(line);
            if (ShouldHideHeader(headerName))
            {
                continue;
            }

            if (seenHeaderNames.Add(headerName))
            {
                target.Add(line);
            }
        }
    }

    private static string ExtractHeaderName(string headerLine)
    {
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return string.Empty;
        }

        if (headerLine[0] == ':')
        {
            var pseudoEndIndex = headerLine.IndexOf(':', 1);
            return pseudoEndIndex > 0
                ? headerLine[..pseudoEndIndex]
                : headerLine.Trim();
        }

        var separatorIndex = headerLine.IndexOf(':');
        return separatorIndex > 0
            ? headerLine[..separatorIndex].Trim()
            : headerLine.Trim();
    }

    private static bool ShouldHideHeader(string? headerName)
    {
        return string.IsNullOrWhiteSpace(headerName) ||
               headerName.TrimStart().StartsWith(':');
    }
}
