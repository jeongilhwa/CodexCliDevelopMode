namespace CodexDesk.Services;

public class BrowserStateService
{
    private const string DomainCookieSnapshotsKey = "browser.domain-cookie-snapshots";

    private bool _initialized;
    private string _status = "WebView is idle.";
    private string _currentUrl = "about:blank";

    public event Action? StateChanged;

    public event Action<BrowserCommand>? CommandRequested;

    public string Status => _status;

    public string CurrentUrl => _currentUrl;

    public Task EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return Task.CompletedTask;
        }

        _status = "WebView is ready.";
        _initialized = true;
        NotifyStateChanged();
        return Task.CompletedTask;
    }

    internal async Task SaveDomainCookieSnapshotAsync(string targetOrigin, IReadOnlyList<PersistedBrowserCookie> cookies)
    {
        await EnsureInitializedAsync();

        try
        {
            var existingJson = await TryGetRawSecureValueAsync(DomainCookieSnapshotsKey);
            var updatedJson = DomainCookieSessionStore.UpsertSnapshot(existingJson, targetOrigin, cookies);
            if (string.IsNullOrWhiteSpace(updatedJson))
            {
                SecureStorage.Default.Remove(DomainCookieSnapshotsKey);
                SetStatus("Removed saved domain cookie snapshots.");
                return;
            }

            await SecureStorage.Default.SetAsync(DomainCookieSnapshotsKey, updatedJson);
            SetStatus("Saved domain cookie snapshot.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to save domain cookie snapshot: {ex.Message}");
        }
    }

    internal async Task<IReadOnlyList<SavedDomainCookieSnapshot>> TryGetSavedDomainCookieSnapshotsAsync()
    {
        await EnsureInitializedAsync();
        var raw = await TryGetRawSecureValueAsync(DomainCookieSnapshotsKey);
        return DomainCookieSessionStore.ParseSnapshots(raw);
    }

    public async Task NavigateAsync(string targetUrl)
    {
        await EnsureInitializedAsync();

        var resolvedUrl = NormalizeUrl(targetUrl);
        _currentUrl = resolvedUrl;
        _status = "Opening page.";
        NotifyStateChanged();
        CommandRequested?.Invoke(new BrowserCommand(BrowserCommandType.Navigate, resolvedUrl));
    }

    public async Task ReloadBrowserAsync()
    {
        await EnsureInitializedAsync();
        _status = "Reloading page.";
        NotifyStateChanged();
        CommandRequested?.Invoke(new BrowserCommand(BrowserCommandType.Reload));
    }

    public void SetStatus(string status, string? currentUrl = null)
    {
        _status = string.IsNullOrWhiteSpace(status) ? "WebView is idle." : status.Trim();
        if (!string.IsNullOrWhiteSpace(currentUrl))
        {
            _currentUrl = NormalizeUrl(currentUrl);
        }

        NotifyStateChanged();
    }

    private static string NormalizeUrl(string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return "about:blank";
        }

        var trimmed = targetUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.AbsoluteUri;
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return $"https:{trimmed}";
        }

        return trimmed;
    }

    private static async Task<string?> TryGetRawSecureValueAsync(string key)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(key);
        }
        catch
        {
            return null;
        }
    }

    private void NotifyStateChanged()
    {
        StateChanged?.Invoke();
    }
}

public sealed record BrowserCommand(
    BrowserCommandType Type,
    string? Url = null);

public enum BrowserCommandType
{
    Navigate,
    Reload
}
