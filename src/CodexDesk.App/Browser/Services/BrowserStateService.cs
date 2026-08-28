namespace CodexDesk.Services;

/// <summary>
/// Coordinates persisted browser settings and commands between the Blazor UI and the native WebView host.
/// </summary>
public class BrowserStateService
{
    private const string DomainCookieSnapshotsKey = "browser.domain-cookie-snapshots";
    private const string UserAgentPresetPreferenceKey = "browser.user-agent-preset";
    private const string CustomUserAgentPreferenceKey = "browser.custom-user-agent";

    private bool _initialized;
    private string _status = "WebView is idle.";
    private string _currentUrl = "about:blank";
    private BrowserUserAgentPreference _userAgentPreference = BrowserUserAgentPreference.Default;

    /// <summary>Occurs when browser state exposed to the UI changes.</summary>
    public event Action? StateChanged;

    /// <summary>Occurs when the native browser host should execute a browser command.</summary>
    public event Action<BrowserCommand>? CommandRequested;

    /// <summary>Gets the latest browser status message.</summary>
    public string Status => _status;

    /// <summary>Gets the current or requested browser URL.</summary>
    public string CurrentUrl => _currentUrl;

    /// <summary>Gets the normalized, persisted User-Agent preference.</summary>
    public BrowserUserAgentPreference UserAgentPreference => _userAgentPreference;

    /// <summary>Loads persisted browser settings once for the current application session.</summary>
    /// <returns>A task that completes after the settings are available.</returns>
    public Task EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return Task.CompletedTask;
        }

        _userAgentPreference = LoadUserAgentPreference();
        _status = "WebView is ready.";
        _initialized = true;
        NotifyStateChanged();
        return Task.CompletedTask;
    }

    /// <summary>Saves a User-Agent preset and asks the native WebView host to apply it immediately.</summary>
    /// <param name="presetKey">The preset key to save.</param>
    /// <param name="customUserAgent">The custom User-Agent used by the custom preset.</param>
    /// <returns>A task that completes after the preference is saved and the apply command is raised.</returns>
    public async Task SaveUserAgentPreferenceAsync(string? presetKey, string? customUserAgent)
    {
        await EnsureInitializedAsync();

        var normalizedPreference = BrowserUserAgentPreference.Normalize(presetKey, customUserAgent);
        try
        {
            Preferences.Default.Set(UserAgentPresetPreferenceKey, normalizedPreference.PresetKey);
            Preferences.Default.Set(CustomUserAgentPreferenceKey, normalizedPreference.CustomUserAgent);
            _userAgentPreference = normalizedPreference;
            _status = "User-Agent setting saved. Applying it to the current page.";
            NotifyStateChanged();
            CommandRequested?.Invoke(new BrowserCommand(BrowserCommandType.ApplyUserAgent));
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to save User-Agent setting: {ex.Message}");
        }
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

    /// <summary>Navigates the native browser to the supplied URL.</summary>
    /// <param name="targetUrl">The URL to normalize and open.</param>
    /// <returns>A task that completes after the navigation command is raised.</returns>
    public async Task NavigateAsync(string targetUrl)
    {
        await EnsureInitializedAsync();

        var resolvedUrl = NormalizeUrl(targetUrl);
        _currentUrl = resolvedUrl;
        _status = "Opening page.";
        NotifyStateChanged();
        CommandRequested?.Invoke(new BrowserCommand(BrowserCommandType.Navigate, resolvedUrl));
    }

    /// <summary>Requests a reload of the current native browser page.</summary>
    /// <returns>A task that completes after the reload command is raised.</returns>
    public async Task ReloadBrowserAsync()
    {
        await EnsureInitializedAsync();
        _status = "Reloading page.";
        NotifyStateChanged();
        CommandRequested?.Invoke(new BrowserCommand(BrowserCommandType.Reload));
    }

    /// <summary>Updates the browser status and optionally the current URL.</summary>
    /// <param name="status">The status message to expose.</param>
    /// <param name="currentUrl">An optional current URL.</param>
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

    private static BrowserUserAgentPreference LoadUserAgentPreference()
    {
        try
        {
            return BrowserUserAgentPreference.Normalize(
                Preferences.Default.Get(UserAgentPresetPreferenceKey, BrowserUserAgentPreference.DefaultPresetKey),
                Preferences.Default.Get(CustomUserAgentPreferenceKey, string.Empty));
        }
        catch
        {
            return BrowserUserAgentPreference.Default;
        }
    }

    private void NotifyStateChanged()
    {
        StateChanged?.Invoke();
    }
}

/// <summary>Represents a normalized browser User-Agent selection.</summary>
/// <param name="PresetKey">The normalized preset key.</param>
/// <param name="CustomUserAgent">The trimmed custom User-Agent, if supplied.</param>
public sealed record BrowserUserAgentPreference(string PresetKey, string CustomUserAgent)
{
    /// <summary>The key for the WebView2 default User-Agent.</summary>
    public const string DefaultPresetKey = "default";

    /// <summary>The key for a Chrome-compatible User-Agent derived from the installed WebView2 runtime.</summary>
    public const string ChromePresetKey = "chrome";

    /// <summary>The key for a manually supplied User-Agent.</summary>
    public const string CustomPresetKey = "custom";

    /// <summary>Gets the default User-Agent preference.</summary>
    public static BrowserUserAgentPreference Default { get; } = new(DefaultPresetKey, string.Empty);

    /// <summary>Normalizes persisted or user-provided User-Agent settings.</summary>
    /// <param name="presetKey">The preset key to normalize.</param>
    /// <param name="customUserAgent">The custom User-Agent to trim.</param>
    /// <returns>A valid preference, falling back to the WebView2 default preset for unknown keys.</returns>
    public static BrowserUserAgentPreference Normalize(string? presetKey, string? customUserAgent)
    {
        var normalizedKey = presetKey?.Trim().ToLowerInvariant();
        if (normalizedKey is not (DefaultPresetKey or ChromePresetKey or CustomPresetKey))
        {
            normalizedKey = DefaultPresetKey;
        }

        return new BrowserUserAgentPreference(normalizedKey, customUserAgent?.Trim() ?? string.Empty);
    }
}

/// <summary>Represents a command sent from browser state to the native browser host.</summary>
/// <param name="Type">The command type.</param>
/// <param name="Url">The optional navigation URL.</param>
public sealed record BrowserCommand(
    BrowserCommandType Type,
    string? Url = null);

/// <summary>Identifies commands supported by the native browser host.</summary>
public enum BrowserCommandType
{
    /// <summary>Navigate to a URL.</summary>
    Navigate,
    /// <summary>Reload the current page.</summary>
    Reload,
    /// <summary>Apply the saved User-Agent and reload the current page.</summary>
    ApplyUserAgent
}
