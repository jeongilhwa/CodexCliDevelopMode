using System.Text;

namespace CodexDesk.Services;

/// <summary>Formats bounded capture metadata for the in-app Codex conversation.</summary>
public static class NetworkCaptureSummaryFormatter
{
    /// <summary>Returns one page of request metadata without headers, bodies, or URL query values.</summary>
    /// <param name="entries">The retained capture entries.</param>
    /// <param name="isCapturing">Whether capture is currently enabled.</param>
    /// <param name="afterSequence">The last sequence read by the caller.</param>
    /// <param name="limit">The maximum number of entries to return.</param>
    /// <returns>A bounded text page with a continuation cursor.</returns>
    public static string FormatPage(IReadOnlyList<NetworkCaptureRow> entries, bool isCapturing, int afterSequence, int limit)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);

        var page = entries.Where(entry => entry.Sequence > afterSequence)
            .OrderBy(entry => entry.Sequence)
            .Take(limit + 1)
            .ToArray();
        var hasMore = page.Length > limit;
        var shown = page.Take(limit).ToArray();
        var result = new StringBuilder()
            .AppendLine($"Capture: {(isCapturing ? "recording" : "stopped")}")
            .AppendLine($"Total: {entries.Count} retained requests")
            .AppendLine($"Failures: {entries.Count(entry => entry.IsFailure)}")
            .AppendLine("Request metadata (URL query values, headers, and bodies omitted):");

        foreach (var entry in shown)
        {
            result.Append('#').Append(entry.Sequence).Append(' ')
                .Append(SafeField(entry.Method, 16)).Append(' ')
                .Append(SafeField(entry.Status, 16)).Append(' ')
                .Append(SafeField(entry.Type, 32)).Append(' ')
                .Append(SafeField(entry.Duration, 24)).Append(' ')
                .Append(SafeField(entry.Size, 24)).Append(' ')
                .AppendLine(SafeUrl(entry.Url));
        }

        if (hasMore)
        {
            result.AppendLine($"Next afterSequence: {shown[^1].Sequence}");
        }
        else
        {
            result.AppendLine("Next afterSequence: none");
        }

        return result.ToString();
    }

    private static string SafeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return "[invalid URL]";
        }

        var path = uri.AbsolutePath.Length <= 300 ? uri.AbsolutePath : uri.AbsolutePath[..300] + "…";
        return $"{uri.Scheme}://{uri.Authority}{path}{(uri.Query.Length > 0 ? "?[query omitted]" : string.Empty)}";
    }

    private static string SafeField(string? value, int limit)
    {
        var safe = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return safe.Length <= limit ? safe : safe[..limit] + "…";
    }
}
