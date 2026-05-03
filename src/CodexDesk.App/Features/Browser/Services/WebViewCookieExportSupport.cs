namespace CodexDesk.Services;

internal sealed record WebViewCookieExportItem(
    string Name,
    string Value,
    string Domain,
    string Path);

internal static class WebViewCookieExportSupport
{
    public static bool MatchesHost(string? targetHost, string? cookieDomain)
    {
        if (string.IsNullOrWhiteSpace(targetHost) || string.IsNullOrWhiteSpace(cookieDomain))
        {
            return false;
        }

        var host = targetHost.Trim().TrimEnd('.').ToLowerInvariant();
        var domain = cookieDomain.Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.StartsWith(".", StringComparison.Ordinal))
        {
            var bareDomain = domain[1..];
            return string.Equals(host, bareDomain, StringComparison.Ordinal) ||
                   host.EndsWith($".{bareDomain}", StringComparison.Ordinal);
        }

        return string.Equals(host, domain, StringComparison.Ordinal);
    }

    public static string BuildCookieHeader(IEnumerable<WebViewCookieExportItem> cookies)
    {
        return string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }
}
