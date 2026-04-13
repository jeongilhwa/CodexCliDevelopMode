namespace CodexDesk.Services;

public sealed record NaverTalkPersistedCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    double? ExpiresAtUnixSeconds,
    bool IsSecure,
    bool IsHttpOnly,
    string? SameSite);
