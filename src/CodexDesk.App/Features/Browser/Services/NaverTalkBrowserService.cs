using CodexDesk.Models;
using System.Globalization;
using System.Text.Json;

namespace CodexDesk.Services;

/// <summary>
/// 네이버 톡 브라우저 상태와 사용자 설정을 보관하고,
/// 화면 계층이 따라야 할 브라우저 명령을 발행하는 애플리케이션 서비스입니다.
/// </summary>
public sealed class NaverTalkBrowserService
{
    private const string CompletedChatsBaselineFileName = "completed-chats.json";
    private const string TargetUrlKey = "naver-talk.target-url";
    private const string LoginIdKey = "naver-talk.login-id";
    private const string AutoLoginEnabledKey = "naver-talk.auto-login-enabled";
    private const string WebViewUserAgentKey = "naver-talk.webview-user-agent";
    private const string TypingMacroTextKey = "naver-talk.typing-macro-text";
    private const string TypingMacroSendEnterEnabledKey = "naver-talk.typing-macro-send-enter-enabled";
    private const string TypingMacrosJsonKey = "naver-talk.typing-macros-json";
    private const string PasswordKey = "naver-talk-password";
    private const string CookieJarKey = "naver-talk-cookie-jar";

    private readonly SemaphoreSlim _initializeLock = new(1, 1);

    private bool _initialized;
    private NaverTalkSettings _settings = NaverTalkSettings.Default;
    private string _status = "WebView is idle.";
    private string _currentUrl = "about:blank";
    private bool _hasStoredPassword;
    private bool _hasStoredCookieJar;
    private bool _isCollectingCompletedChats;
    private string _completedChatCollectionStatus = "No collection run yet.";
    private int _completedChatCollectionProcessedCount;
    private int _completedChatCollectionTotalCount;
    private DateTimeOffset? _completedChatBaselineExportedAt;
    private CompletedChatCollectionResult? _lastCompletedChatCollectionResult;

    public event Action? StateChanged;

    public event Action<BrowserCommand>? CommandRequested;

    public NaverTalkSettings Settings => _settings;

    public string Status => _status;

    public string CurrentUrl => _currentUrl;

    public bool HasStoredPassword => _hasStoredPassword;

    public bool HasStoredCookieJar => _hasStoredCookieJar;

    public bool IsCollectingCompletedChats => _isCollectingCompletedChats;

    public string CompletedChatCollectionStatus => _completedChatCollectionStatus;

    public int CompletedChatCollectionProcessedCount => _completedChatCollectionProcessedCount;

    public int CompletedChatCollectionTotalCount => _completedChatCollectionTotalCount;

    public DateTimeOffset? CompletedChatBaselineExportedAt => _completedChatBaselineExportedAt;

    public CompletedChatCollectionResult? LastCompletedChatCollectionResult => _lastCompletedChatCollectionResult;

    /// <summary>
    /// 저장된 설정과 보안 정보를 한 번만 읽어 서비스 상태를 준비합니다.
    /// 여러 화면에서 반복 호출해도 최초 한 번만 실제 초기화를 수행합니다.
    /// </summary>
    public async Task EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _initializeLock.WaitAsync();
        try
        {
            if (_initialized)
            {
                return;
            }

            var savedTargetUrl = Preferences.Default.Get(TargetUrlKey, NaverTalkSettings.DefaultTargetUrl);
            var savedLoginId = Preferences.Default.Get(LoginIdKey, string.Empty);
            var savedAutoLoginEnabled = Preferences.Default.Get(AutoLoginEnabledKey, true);
            var savedWebViewUserAgent = Preferences.Default.Get(WebViewUserAgentKey, string.Empty);
            var savedTypingMacroText = Preferences.Default.Get(TypingMacroTextKey, string.Empty);
            var savedTypingMacroSendEnterEnabled = Preferences.Default.Get(TypingMacroSendEnterEnabledKey, false);
            var savedTypingMacrosJson = Preferences.Default.Get(TypingMacrosJsonKey, string.Empty);

            if (string.IsNullOrWhiteSpace(savedTypingMacrosJson))
            {
                savedTypingMacrosJson = BuildFallbackTypingMacrosJson(savedTypingMacroText, savedTypingMacroSendEnterEnabled);
            }

            _settings = new NaverTalkSettings(
                NormalizeUrl(savedTargetUrl),
                savedLoginId.Trim(),
                savedAutoLoginEnabled,
                savedWebViewUserAgent.Trim(),
                savedTypingMacroText,
                savedTypingMacroSendEnterEnabled,
                savedTypingMacrosJson);

            _currentUrl = _settings.TargetUrl;
            _hasStoredPassword = await HasPasswordAsync();
            _hasStoredCookieJar = await HasCookieJarAsync();
            RefreshCompletedChatBaselineExportedAt();
            _status = "WebView is ready.";
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }

        NotifyStateChanged();
    }

    /// <summary>
    /// 네이버 톡 접속 설정과 자동 로그인 관련 정보를 저장합니다.
    /// 비밀번호는 빈 문자열이면 삭제하고, <c>null</c>이면 기존 값을 유지합니다.
    /// </summary>
    public async Task SaveBrowserSettingsAsync(NaverTalkSettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        await EnsureInitializedAsync();

        var normalizedTypingMacrosJson = string.IsNullOrWhiteSpace(update.TypingMacrosJson)
            ? BuildFallbackTypingMacrosJson(update.TypingMacroText, update.TypingMacroSendEnterEnabled)
            : update.TypingMacrosJson;

        _settings = new NaverTalkSettings(
            NormalizeUrl(update.TargetUrl),
            update.LoginId.Trim(),
            update.AutoLoginEnabled,
            update.WebViewUserAgent.Trim(),
            update.TypingMacroText,
            update.TypingMacroSendEnterEnabled,
            normalizedTypingMacrosJson);

        Preferences.Default.Set(TargetUrlKey, _settings.TargetUrl);
        Preferences.Default.Set(LoginIdKey, _settings.LoginId);
        Preferences.Default.Set(AutoLoginEnabledKey, _settings.AutoLoginEnabled);
        Preferences.Default.Set(WebViewUserAgentKey, _settings.WebViewUserAgent);
        Preferences.Default.Set(TypingMacroTextKey, _settings.TypingMacroText);
        Preferences.Default.Set(TypingMacroSendEnterEnabledKey, _settings.TypingMacroSendEnterEnabled);
        Preferences.Default.Set(TypingMacrosJsonKey, _settings.TypingMacrosJson);

        if (update.NewPasswordOrNull is not null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(update.NewPasswordOrNull))
                {
                    SecureStorage.Default.Remove(PasswordKey);
                }
                else
                {
                    await SecureStorage.Default.SetAsync(PasswordKey, update.NewPasswordOrNull);
                }
            }
            catch (Exception ex)
            {
                _status = $"Failed to save password: {ex.Message}";
                _hasStoredPassword = await HasPasswordAsync();
                NotifyStateChanged();
                return;
            }
        }

        _hasStoredPassword = await HasPasswordAsync();
        _currentUrl = _settings.TargetUrl;
        _status = "Saved browser settings.";
        NotifyStateChanged();
    }

    /// <summary>
    /// 저장된 네이버 비밀번호를 삭제하고 화면 상태를 갱신합니다.
    /// </summary>
    public async Task ClearSavedPasswordAsync()
    {
        await EnsureInitializedAsync();

        try
        {
            SecureStorage.Default.Remove(PasswordKey);
            _hasStoredPassword = false;
            _status = "Removed the saved Naver password.";
        }
        catch (Exception ex)
        {
            _status = $"Failed to remove password: {ex.Message}";
        }

        NotifyStateChanged();
    }

    /// <summary>
    /// 현재 로그인 쿠키 세션을 안전 저장소에 저장하거나 삭제합니다.
    /// </summary>
    public async Task SaveCookieSessionAsync(string cookieJarJson)
    {
        await EnsureInitializedAsync();

        try
        {
            if (string.IsNullOrWhiteSpace(cookieJarJson))
            {
                SecureStorage.Default.Remove(CookieJarKey);
                _hasStoredCookieJar = false;
                _status = "Removed saved cookie session.";
            }
            else
            {
                await SecureStorage.Default.SetAsync(CookieJarKey, cookieJarJson);
                _hasStoredCookieJar = true;
                _status = "Saved cookie session.";
            }
        }
        catch (Exception ex)
        {
            _status = $"Failed to save cookie session: {ex.Message}";
            _hasStoredCookieJar = await HasCookieJarAsync();
        }

        NotifyStateChanged();
    }

    /// <summary>
    /// 저장된 쿠키 세션 문자열을 읽어 옵니다.
    /// 읽기에 실패하면 예외 대신 <c>null</c>을 반환합니다.
    /// </summary>
    public async Task<string?> TryGetSavedCookieSessionAsync()
    {
        await EnsureInitializedAsync();

        try
        {
            return await SecureStorage.Default.GetAsync(CookieJarKey);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 브라우저를 지정한 URL로 이동시키도록 화면 계층에 탐색 명령을 보냅니다.
    /// </summary>
    public async Task NavigateAsync(string? targetUrl = null, bool forceLogin = false)
    {
        await EnsureInitializedAsync();

        var resolvedUrl = NormalizeUrl(targetUrl ?? _settings.TargetUrl);
        _currentUrl = resolvedUrl;
        _status = "Opening page.";
        NotifyStateChanged();
        CommandRequested?.Invoke(new BrowserCommand(BrowserCommandType.Navigate, resolvedUrl, forceLogin));
    }

    /// <summary>
    /// 현재 브라우저 페이지를 다시 로드하도록 요청합니다.
    /// </summary>
    public async Task ReloadBrowserAsync()
    {
        await EnsureInitializedAsync();
        _status = "Reloading the WebView.";
        NotifyStateChanged();
        CommandRequested?.Invoke(new BrowserCommand(BrowserCommandType.Reload));
    }

    /// <summary>
    /// 저장된 매크로나 지정된 텍스트를 현재 입력창에 넣도록 요청합니다.
    /// </summary>
    public async Task RunTypingMacroAsync(string? text, bool sendEnter)
    {
        await EnsureInitializedAsync();

        if (string.IsNullOrWhiteSpace(text))
        {
            _status = "Typing macro text is empty.";
            NotifyStateChanged();
            return;
        }

        _status = sendEnter ? "Typing macro and sending." : "Typing macro.";
        NotifyStateChanged();
        CommandRequested?.Invoke(new BrowserCommand(BrowserCommandType.TypeText, Text: text, Submit: sendEnter));
    }

    /// <summary>
    /// 완료 대화 수집 실행을 시작하고 진행 상태를 초기화합니다.
    /// 이미 수집 중이면 중복 실행하지 않습니다.
    /// </summary>
    /// <summary>
    /// 완료 대화 수집 실행 전에 상태값만 초기화합니다.
    /// 실제 수집 실행은 별도 Playwright 수집 서비스가 담당하며, 이미 진행 중이면 중복 초기화하지 않습니다.
    /// </summary>
    public async Task StartCompletedChatsCollectionAsync()
    {
        await EnsureInitializedAsync();

        if (_isCollectingCompletedChats)
        {
            return;
        }

        RefreshCompletedChatBaselineExportedAt();
        _isCollectingCompletedChats = true;
        _completedChatCollectionProcessedCount = 0;
        _completedChatCollectionTotalCount = 0;
        _completedChatCollectionStatus = "Starting completed chat collection.";
        _lastCompletedChatCollectionResult = null;
        _status = "Collecting completed Naver Talk chats.";
        NotifyStateChanged();
    }

    /// <summary>
    /// 현재 URL에서 자동 로그인 스크립트를 시도해도 되는지 판단합니다.
    /// </summary>
    public bool CanAutoLoginAtUrl(string? url)
    {
        if (!_settings.AutoLoginEnabled ||
            !_hasStoredPassword ||
            string.IsNullOrWhiteSpace(_settings.LoginId) ||
            string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Contains("nid.naver.com", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsoluteUri.Contains("nidlogin", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsoluteUri.Contains("login", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 현재 로그인 페이지 상태에 맞는 자동 로그인 스크립트 묶음을 만듭니다.
    /// 필요한 정보가 부족하면 <c>null</c>을 반환합니다.
    /// </summary>
    public async Task<NaverTalkAutoLoginScriptSet?> BuildAutoLoginScriptsIfAvailableAsync(string? currentUrl, bool force = false)
    {
        await EnsureInitializedAsync();

        if (!force && !CanAutoLoginAtUrl(currentUrl))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_settings.LoginId))
        {
            return null;
        }

        var password = await TryGetPasswordAsync();
        if (string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        return new NaverTalkAutoLoginScriptSet(
            BuildAutoLoginStep1Script(_settings.LoginId),
            BuildAutoLoginStep2Script(password),
            BuildAutoLoginStep3Script(),
            BuildAutoLoginStep4Script());
    }

    /// <summary>
    /// 네이버 톡 로그아웃 팝업을 닫기 위한 브라우저 스크립트를 반환합니다.
    /// </summary>
    public string GetLogoutPopupCloseScript()
    {
        return BuildLogoutPopupHandlerScript();
    }

    /// <summary>
    /// 브라우저 상태 문구와 현재 URL을 갱신하고 화면에 알립니다.
    /// </summary>
    public void SetStatus(string status, string? currentUrl = null)
    {
        _status = status;

        if (!string.IsNullOrWhiteSpace(currentUrl))
        {
            _currentUrl = currentUrl;
        }

        NotifyStateChanged();
    }

    /// <summary>
    /// 완료 대화 수집이 정상 종료되었음을 기록하고 결과를 화면에 노출합니다.
    /// </summary>
    public void CompleteCompletedChatsCollection(string status, string? currentUrl = null, CompletedChatCollectionResult? result = null)
    {
        _isCollectingCompletedChats = false;
        _completedChatCollectionStatus = status;
        if (result?.UpdatedBaseline == true)
        {
            RefreshCompletedChatBaselineExportedAt();
        }

        _lastCompletedChatCollectionResult = result;
        SetStatus(status, currentUrl);
    }

    /// <summary>
    /// 완료 대화 수집 실패 상태와 마지막 결과를 기록합니다.
    /// </summary>
    public void FailCompletedChatsCollection(string status, string? currentUrl = null, CompletedChatCollectionResult? result = null)
    {
        _isCollectingCompletedChats = false;
        _completedChatCollectionStatus = status;
        _lastCompletedChatCollectionResult = result;
        SetStatus(status, currentUrl);
    }

    /// <summary>
    /// 완료 대화 수집 진행률과 상태 메시지를 함께 갱신합니다.
    /// </summary>
    public void SetCompletedChatsCollectionProgress(string status, int processedCount, int totalCount, string? currentUrl = null)
    {
        _completedChatCollectionProcessedCount = Math.Max(0, processedCount);
        _completedChatCollectionTotalCount = Math.Max(0, totalCount);
        _completedChatCollectionStatus = status;
        SetStatus(status, currentUrl);
    }

    /// <summary>
    /// 수집 결과가 기준 파일에 반영되었음을 표시하고 exportedAt 정보를 다시 계산합니다.
    /// </summary>
    public void MarkCompletedChatsSavedToBaseline(string status, string? currentUrl = null)
    {
        _completedChatCollectionStatus = status;
        RefreshCompletedChatBaselineExportedAt();

        if (_lastCompletedChatCollectionResult is not null)
        {
            _lastCompletedChatCollectionResult = _lastCompletedChatCollectionResult with { UpdatedBaseline = true };
        }

        SetStatus(status, currentUrl);
    }

    public static string BuildLogoutPopupHandlerScript()
    {
        return """
(() => {
    // 로그아웃 팝업/알림 감지 및 자동 닫기
    const closeSelectors = [
        'button.close',
        'button[class*="close"]',
        'button[aria-label*="닫기"]',
        'a[class*="close"]',
        '.btn_close',
        '[role="dialog"] button'
    ];

    let closed = false;
    for (const selector of closeSelectors) {
        const closeBtn = document.querySelector(selector);
        if (closeBtn && (closeBtn.offsetParent !== null || closeBtn.checkVisibility?.())) {
            closeBtn.click();
            closed = true;
            break;
        }
    }

    // 로그아웃 텍스트를 포함한 요소 찾기
    const allElements = document.querySelectorAll('*');
    for (const el of allElements) {
        const text = el.textContent || '';
        if ((text.includes('로그아웃') || text.includes('로그인') || text.includes('세션')) &&
            (text.includes('되었습니다') || text.includes('만료'))) {
            // 부모 dialog/modal 찾아서 닫기
            let parent = el.parentElement;
            while (parent) {
                if (parent.role === 'dialog' || parent.classList.contains('modal') || parent.classList.contains('popup')) {
                    const closeBtn = parent.querySelector('button[class*="close"], button[aria-label*="닫기"]');
                    if (closeBtn) {
                        closeBtn.click();
                        closed = true;
                        break;
                    }
                }
                parent = parent.parentElement;
            }
            if (closed) break;
        }
    }

    return JSON.stringify({ closed, foundLogoutMessage: closed });
})();
""";
    }

    private static string BuildAutoLoginScript(string loginId, string password)
    {
        var loginIdEscaped = loginId.Replace("'", "''");
        var passwordEscaped = password.Replace("'", "''");

        return $$"""
(() => {
    try {
        // ID 입력
        const idInput = document.getElementById('id');
        if (!idInput) {
            return JSON.stringify({ success: false, error: 'id-field-not-found' });
        }
        idInput.value = '{{loginIdEscaped}}';

        // 비밀번호 입력
        const pwInput = document.getElementById('pw');
        if (!pwInput) {
            return JSON.stringify({ success: false, error: 'pw-field-not-found' });
        }
        pwInput.value = '{{passwordEscaped}}';

        // 로그인 상태 유지 체크박스 처리
        const keepCheckbox = document.getElementById('keep');
        if (keepCheckbox && keepCheckbox.value === 'off') {
            keepCheckbox.click();
        }

        // 로그인 버튼 찾기 및 클릭
        // Disable smart login when the page indicates it is enabled.
        const smartLevelInput = document.getElementById('smart_LEVEL');
        const switchButton = document.getElementById('switch');
        if (smartLevelInput && smartLevelInput.value === '1' && switchButton) {
            switchButton.click();
        }

        let loginClicked = false;

        // 1. id="log.login" 시도
        let loginButton = document.getElementById('log.login');
        if (loginButton) {
            loginButton.click();
            loginClicked = true;
            return JSON.stringify({ success: true, method: 'clicked-log.login' });
        }

        // 2. button[type="submit"] 시도
        loginButton = document.querySelector('button[type="submit"]');
        if (loginButton) {
            loginButton.click();
            loginClicked = true;
            return JSON.stringify({ success: true, method: 'clicked-submit-button' });
        }

        // 3. .btn_login 시도
        loginButton = document.querySelector('.btn_login');
        if (loginButton) {
            loginButton.click();
            loginClicked = true;
            return JSON.stringify({ success: true, method: 'clicked-btn_login' });
        }

        // 4. form submit 시도
        const form = pwInput.closest('form');
        if (form) {
            form.submit();
            return JSON.stringify({ success: true, method: 'submitted-form' });
        }

        return JSON.stringify({ success: false, error: 'login-button-not-found' });
    } catch (ex) {
        return JSON.stringify({ success: false, error: ex.message });
    }
})();
""";
    }

    public static string BuildAutoLoginStep1Script(string loginId)
    {
        var loginIdEscaped = loginId.Replace("'", "''");

        return $$"""
document.getElementById('id').value='{{loginIdEscaped}}';
""";
    }

    public static string BuildAutoLoginStep2Script(string password)
    {
        var passwordEscaped = password.Replace("'", "''");

        return $$"""
document.getElementById('pw').value='{{passwordEscaped}}';
""";
    }

    public static string BuildAutoLoginStep3Script()
    {
        return """
document.getElementById('pw').focus();
document.getElementById('pw').click();
""";
    }

    public static string BuildAutoLoginStep4Script()
    {
        return """
const keepCheckbox = document.getElementById('keep');
if (keepCheckbox && keepCheckbox.value == 'off') {
    keepCheckbox.click();
}
const smartLevelInput = document.getElementById('smart_LEVEL');
const switchButton = document.getElementById('switch');
if (smartLevelInput && smartLevelInput.value == '1' && switchButton) {
    switchButton.click();
}
const loginButton = document.getElementById('log.login');
if (loginButton) {
    loginButton.click();
}
""";
    }

    private async Task<bool> HasPasswordAsync()
    {
        var password = await TryGetPasswordAsync();
        return !string.IsNullOrWhiteSpace(password);
    }

    private async Task<bool> HasCookieJarAsync()
    {
        var cookieJar = await TryGetRawCookieJarAsync();
        return !string.IsNullOrWhiteSpace(cookieJar);
    }

    private static string NormalizeUrl(string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return NaverTalkSettings.DefaultTargetUrl;
        }

        if (Uri.TryCreate(targetUrl.Trim(), UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.ToString();
        }

        return NaverTalkSettings.DefaultTargetUrl; 
    }

    public async Task<string?> TryGetPasswordAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(PasswordKey);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryGetRawCookieJarAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(CookieJarKey);
        }
        catch
        {
            return null;
        }
    }

    private void RefreshCompletedChatBaselineExportedAt()
    {
        _completedChatBaselineExportedAt = TryReadCompletedChatBaselineExportedAt();
    }

    private static DateTimeOffset? TryReadCompletedChatBaselineExportedAt()
    {
        var path = CompletedChatsSupport.ResolveProjectDataPath(CompletedChatsBaselineFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return TryReadRootDateTimeOffsetProperty(path, "exportedAt");
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryReadRootDateTimeOffsetProperty(string path, string propertyName)
    {
        var buffer = new byte[4096];
        var state = new JsonReaderState();
        var bytesInBuffer = 0;

        using var stream = File.OpenRead(path);
        while (true)
        {
            if (bytesInBuffer == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            var bytesRead = stream.Read(buffer, bytesInBuffer, buffer.Length - bytesInBuffer);
            var isFinalBlock = bytesRead == 0;
            bytesInBuffer += bytesRead;

            var reader = new Utf8JsonReader(buffer.AsSpan(0, bytesInBuffer), isFinalBlock, state);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                if (!string.Equals(reader.GetString(), propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                {
                    return null;
                }

                var rawValue = reader.GetString();
                return DateTimeOffset.TryParse(rawValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
                    ? value
                    : null;
            }

            if (isFinalBlock)
            {
                return null;
            }

            var consumed = (int)reader.BytesConsumed;
            var remaining = bytesInBuffer - consumed;
            if (remaining > 0)
            {
                Buffer.BlockCopy(buffer, consumed, buffer, 0, remaining);
            }

            bytesInBuffer = remaining;
            state = reader.CurrentState;
        }
    }

    /// <summary>
    /// 브라우저 상태 변경 이벤트를 UI 스레드에서 안전하게 전달합니다.
    /// 테스트 환경처럼 MAUI MainThread가 준비되지 않은 경우에는 현재 스레드에서 바로 이벤트를 호출합니다.
    /// </summary>
    private void NotifyStateChanged()
    {
        try
        {
            if (MainThread.IsMainThread)
            {
                StateChanged?.Invoke();
                return;
            }

            MainThread.BeginInvokeOnMainThread(() => StateChanged?.Invoke());
        }
        catch
        {
            StateChanged?.Invoke();
        }
    }

    private static string BuildFallbackTypingMacrosJson(string? typingMacroText, bool sendEnter)
    {
        if (string.IsNullOrWhiteSpace(typingMacroText))
        {
            return "[]";
        }

        var fallback = new[]
        {
            new
            {
                Name = "기본 매크로",
                Text = typingMacroText,
                SendEnter = sendEnter
            }
        };

        return JsonSerializer.Serialize(fallback);
    }
}

public sealed record BrowserCommand(
    BrowserCommandType Type,
    string? Url = null,
    bool ForceLogin = false,
    string? Text = null,
    bool Submit = false);

public sealed record CompletedChatCollectionResult(
    DateTimeOffset GeneratedAt,
    bool Succeeded,
    string Summary,
    string OutputPath,
    string BaselinePath,
    bool UpdatedBaseline,
    string FromDate,
    int DiscoveredRouteCount,
    int NewRouteCount,
    int SavedCount,
    int SkippedCount,
    IReadOnlyList<string> SampleItems,
    string CollectedDataJson);

public enum BrowserCommandType
{
    Navigate,
    Reload,
    TypeText,
    CollectCompletedChats
}
