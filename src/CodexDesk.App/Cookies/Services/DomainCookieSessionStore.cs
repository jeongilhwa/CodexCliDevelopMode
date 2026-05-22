using System.Text.Json;

namespace CodexDesk.Services;

internal sealed record SavedDomainCookieSnapshot(
    string TargetOrigin,
    List<PersistedBrowserCookie> Cookies);

internal static class DomainCookieSessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static string? UpsertSnapshot(
        string? existingJson,
        string targetOrigin,
        IReadOnlyList<PersistedBrowserCookie> cookies)
    {
        var normalizedOrigin = NormalizeOrigin(targetOrigin);
        if (string.IsNullOrWhiteSpace(normalizedOrigin))
        {
            return existingJson;
        }

        var snapshots = ParseSnapshots(existingJson).ToList();
        snapshots.RemoveAll(snapshot => string.Equals(snapshot.TargetOrigin, normalizedOrigin, StringComparison.OrdinalIgnoreCase));

        if (cookies.Count > 0)
        {
            snapshots.Add(new SavedDomainCookieSnapshot(normalizedOrigin, [.. cookies]));
        }

        return snapshots.Count == 0
            ? null
            : JsonSerializer.Serialize(snapshots, SerializerOptions);
    }

    public static IReadOnlyList<SavedDomainCookieSnapshot> ParseSnapshots(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var snapshots = JsonSerializer.Deserialize<List<SavedDomainCookieSnapshot>>(json, SerializerOptions);
            if (snapshots is null)
            {
                return [];
            }

            return snapshots
                .Select(snapshot => new SavedDomainCookieSnapshot(
                    NormalizeOrigin(snapshot.TargetOrigin) ?? string.Empty,
                    snapshot.Cookies
                        .Where(static cookie => !string.IsNullOrWhiteSpace(cookie.Name) && !string.IsNullOrWhiteSpace(cookie.Domain))
                        .GroupBy(static cookie => $"{cookie.Name}|{cookie.Domain}|{cookie.Path}", StringComparer.Ordinal)
                        .Select(static group => group.Last())
                        .ToList()))
                .Where(static snapshot => !string.IsNullOrWhiteSpace(snapshot.TargetOrigin) && snapshot.Cookies.Count > 0)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string? NormalizeOrigin(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        return uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }
}
