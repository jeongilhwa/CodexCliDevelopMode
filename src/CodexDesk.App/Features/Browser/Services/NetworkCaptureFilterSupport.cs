namespace CodexDesk.Services;

using System.Globalization;

internal enum NetworkCaptureFilterKind
{
    All,
    Api,
    Document,
    JavaScript,
    Css,
    Image,
    Media,
    Font,
    Error,
    Other
}

internal static class NetworkCaptureFilterSupport
{
    public const int MaxEntries = 1000;

    public static bool Matches(NetworkCaptureFilterKind selected, NetworkCaptureFilterKind actual)
    {
        return selected == NetworkCaptureFilterKind.All || selected == actual;
    }

    public static bool MatchesSearch(
        string? query,
        string? method,
        string? status,
        string? host,
        string? path,
        string? resourceType,
        string? mimeType)
    {
        var normalizedQuery = Normalize(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return true;
        }

        return Contains(method, normalizedQuery) ||
               Contains(status, normalizedQuery) ||
               Contains(host, normalizedQuery) ||
               Contains(path, normalizedQuery) ||
               Contains(resourceType, normalizedQuery) ||
               Contains(mimeType, normalizedQuery);
    }

    public static string FormatLabel(NetworkCaptureFilterKind kind)
    {
        return kind switch
        {
            NetworkCaptureFilterKind.All => "All",
            NetworkCaptureFilterKind.Api => "Data",
            NetworkCaptureFilterKind.Document => "Page",
            NetworkCaptureFilterKind.JavaScript => "JS",
            NetworkCaptureFilterKind.Css => "CSS",
            NetworkCaptureFilterKind.Image => "Image",
            NetworkCaptureFilterKind.Media => "Media",
            NetworkCaptureFilterKind.Font => "Font",
            NetworkCaptureFilterKind.Error => "Error",
            _ => "Other"
        };
    }

    public static NetworkCaptureFilterKind Classify(
        string? scheme,
        string? resourceType,
        string? mimeType,
        string? path,
        string? statusDisplay)
    {
        if (IsErrorStatus(statusDisplay))
        {
            return NetworkCaptureFilterKind.Error;
        }

        var type = Normalize(resourceType);
        var mime = Normalize(mimeType);
        var normalizedPath = Normalize(path);

        if (type is "xhr" or "fetch")
        {
            return NetworkCaptureFilterKind.Api;
        }

        if (type is "document")
        {
            return NetworkCaptureFilterKind.Document;
        }

        if (type is "script" ||
            mime.Contains("javascript", StringComparison.Ordinal) ||
            HasAnyExtension(normalizedPath, ".js", ".mjs"))
        {
            return NetworkCaptureFilterKind.JavaScript;
        }

        if (type is "stylesheet" ||
            mime.Contains("css", StringComparison.Ordinal) ||
            HasAnyExtension(normalizedPath, ".css"))
        {
            return NetworkCaptureFilterKind.Css;
        }

        if (type is "image" ||
            mime.StartsWith("image/", StringComparison.Ordinal) ||
            HasAnyExtension(normalizedPath, ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico", ".bmp", ".avif"))
        {
            return NetworkCaptureFilterKind.Image;
        }

        if (type is "media" ||
            mime.StartsWith("audio/", StringComparison.Ordinal) ||
            mime.StartsWith("video/", StringComparison.Ordinal))
        {
            return NetworkCaptureFilterKind.Media;
        }

        if (type is "font" ||
            mime.Contains("font", StringComparison.Ordinal) ||
            HasAnyExtension(normalizedPath, ".woff", ".woff2", ".ttf", ".otf", ".eot"))
        {
            return NetworkCaptureFilterKind.Font;
        }

        return NetworkCaptureFilterKind.Other;
    }

    private static bool IsErrorStatus(string? statusDisplay)
    {
        if (!int.TryParse(statusDisplay, NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
        {
            return string.IsNullOrWhiteSpace(statusDisplay) || statusDisplay.Trim() == "-";
        }

        return status == 0 || status >= 400;
    }

    private static bool HasAnyExtension(string path, params string[] extensions)
    {
        var queryIndex = path.IndexOfAny(['?', '#']);
        var pathOnly = queryIndex >= 0 ? path[..queryIndex] : path;
        return extensions.Any(extension => pathOnly.EndsWith(extension, StringComparison.Ordinal));
    }

    private static string Normalize(string? value)
    {
        return value?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static bool Contains(string? value, string query)
    {
        return Normalize(value).Contains(query, StringComparison.Ordinal);
    }
}
