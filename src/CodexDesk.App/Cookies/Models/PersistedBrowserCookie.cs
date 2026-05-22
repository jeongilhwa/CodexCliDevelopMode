namespace CodexDesk.Services;

public record PersistedBrowserCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    double? ExpiresAtUnixSeconds,
    bool IsSecure,
    bool IsHttpOnly,
    string? SameSite);
