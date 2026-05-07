namespace CodexDesk;

using CodexDesk.Services;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
#if WINDOWS
using Microsoft.Web.WebView2.Core;
using UI = Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using WinColor = Windows.UI.Color;
#endif

public partial class MainPage : ContentPage
{
#if WINDOWS
    private void ConfigureWindowChrome()
    {
        if (_windowChromeConfigured)
        {
            return;
        }

        if (!TryGetNativeWindow(out var nativeWindow))
        {
            return;
        }

        if (WindowDragRegion?.Handler?.PlatformView is not UI.FrameworkElement dragRegion)
        {
            WindowDragRegion?.HandlerChanged += OnWindowDragRegionHandlerChanged;
            return;
        }

        _windowChromeConfigured = true;
        WindowDragRegion.HandlerChanged -= OnWindowDragRegionHandlerChanged;

        nativeWindow.ExtendsContentIntoTitleBar = true;
        nativeWindow.SetTitleBar(dragRegion);
        nativeWindow.Title = string.Empty;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
        TryApplyRoundedWindowCorners(hwnd);

        if (!TryGetAppWindow(nativeWindow, out var appWindow))
        {
            return;
        }

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            ApplyTitleBarButtonColors(appWindow.TitleBar);
        }
    }

    private void OnWindowMinimizeClicked(object? sender, EventArgs e)
    {
        if (!TryGetCurrentAppWindow(out var appWindow) ||
            appWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        presenter.Minimize();
    }

    private void OnWindowMaximizeRestoreClicked(object? sender, EventArgs e)
    {
        if (!TryGetCurrentAppWindow(out var appWindow) ||
            appWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        if (presenter.State == OverlappedPresenterState.Maximized)
        {
            presenter.Restore();
            return;
        }

        presenter.Maximize();
    }

    private void OnWindowCloseClicked(object? sender, EventArgs e)
    {
        Application.Current?.Quit();
    }

    private void OnWindowDragRegionHandlerChanged(object? sender, EventArgs e)
    {
        ConfigureWindowChrome();
    }

    private static bool TryGetNativeWindow(out UI.Window nativeWindow)
    {
        if (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is UI.Window window)
        {
            nativeWindow = window;
            return true;
        }

        nativeWindow = null!;
        return false;
    }

    private static bool TryGetCurrentAppWindow(out AppWindow appWindow)
    {
        if (TryGetNativeWindow(out var nativeWindow) &&
            TryGetAppWindow(nativeWindow, out appWindow))
        {
            return true;
        }

        appWindow = null!;
        return false;
    }

    private static bool TryGetAppWindow(UI.Window nativeWindow, out AppWindow appWindow)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
        if (hwnd == IntPtr.Zero)
        {
            appWindow = null!;
            return false;
        }

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        appWindow = AppWindow.GetFromWindowId(windowId);
        return appWindow is not null;
    }

    private static void ApplyTitleBarButtonColors(AppWindowTitleBar titleBar)
    {
        var transparent = WinColor.FromArgb(0, 0, 0, 0);
        var text = WinColor.FromArgb(255, 49, 66, 74);
        var hover = WinColor.FromArgb(28, 20, 35, 43);
        var pressed = WinColor.FromArgb(44, 20, 35, 43);

        titleBar.BackgroundColor = transparent;
        titleBar.ForegroundColor = text;
        titleBar.InactiveBackgroundColor = transparent;
        titleBar.InactiveForegroundColor = WinColor.FromArgb(255, 92, 112, 120);
        titleBar.ButtonBackgroundColor = transparent;
        titleBar.ButtonForegroundColor = text;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = text;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = text;
        titleBar.ButtonInactiveBackgroundColor = transparent;
        titleBar.ButtonInactiveForegroundColor = WinColor.FromArgb(255, 120, 136, 142);
    }

    private static void TryApplyRoundedWindowCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var cornerPreference = DwmWindowCornerPreferenceRound;
        _ = DwmSetWindowAttribute(
            hwnd,
            DwmWindowAttributeWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));
    }

    private const int DwmWindowAttributeWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
#endif

    private async Task PersistCookieLoginSessionAsync()
    {
#if WINDOWS
        var coreWebView2 = await TryEnsureCoreWebView2Async();
        if (coreWebView2 is null)
        {
            return;
        }

        var cookieManager = coreWebView2.CookieManager;
        var loginCookies = await cookieManager.GetCookiesAsync("https://nid.naver.com");
        var talkCookies = await cookieManager.GetCookiesAsync("https://partner.talk.naver.com");

        var persistedCookies = loginCookies
            .Concat(talkCookies)
            .GroupBy(static cookie => $"{cookie.Name}|{cookie.Domain}|{cookie.Path}", StringComparer.Ordinal)
            .Select(static group => group.Last())
            .Select(static cookie => new NaverTalkPersistedCookie(
                cookie.Name,
                cookie.Value,
                cookie.Domain,
                cookie.Path,
                cookie.Expires <= 0 ? null : cookie.Expires,
                cookie.IsSecure,
                cookie.IsHttpOnly,
                cookie.SameSite.ToString()))
            .ToList();

        if (persistedCookies.Count == 0)
        {
            return;
        }

        var serialized = JsonSerializer.Serialize(persistedCookies);
        if (string.Equals(_lastSavedCookieJarJson, serialized, StringComparison.Ordinal))
        {
            return;
        }

        _lastSavedCookieJarJson = serialized;
        await _naverTalkBrowser.SaveCookieSessionAsync(serialized);
#endif
    }

    private async Task TryRestoreCookieLoginSessionAsync()
    {
#if WINDOWS
        var serialized = await _naverTalkBrowser.TryGetSavedCookieSessionAsync();
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return;
        }

        List<NaverTalkPersistedCookie>? persistedCookies;
        try
        {
            persistedCookies = JsonSerializer.Deserialize<List<NaverTalkPersistedCookie>>(serialized);
        }
        catch
        {
            return;
        }

        if (persistedCookies is null || persistedCookies.Count == 0)
        {
            return;
        }

        var coreWebView2 = await TryEnsureCoreWebView2Async();
        if (coreWebView2 is null)
        {
            return;
        }

        var cookieManager = coreWebView2.CookieManager;

        foreach (var persistedCookie in persistedCookies)
        {
            var cookie = cookieManager.CreateCookie(
                persistedCookie.Name,
                persistedCookie.Value,
                persistedCookie.Domain,
                persistedCookie.Path);

            cookie.IsSecure = persistedCookie.IsSecure;
            cookie.IsHttpOnly = persistedCookie.IsHttpOnly;

            if (persistedCookie.ExpiresAtUnixSeconds is { } expiresAtUnixSeconds)
            {
                cookie.Expires = expiresAtUnixSeconds;
            }

            if (TryMapSameSite(persistedCookie.SameSite, out var sameSite))
            {
                cookie.SameSite = sameSite;
            }

            cookieManager.AddOrUpdateCookie(cookie);
        }

        _lastSavedCookieJarJson = serialized;
        _naverTalkBrowser.SetStatus("Saved cookie session restored.");
#endif
    }

    private async Task TryRestoreSavedDomainCookieSnapshotsAsync()
    {
#if WINDOWS
        if (_savedDomainCookieSnapshotsRestored)
        {
            return;
        }

        var snapshots = await _naverTalkBrowser.TryGetSavedDomainCookieSnapshotsAsync();
        _savedDomainCookieSnapshotsRestored = true;
        if (snapshots.Count == 0)
        {
            return;
        }

        var coreWebView2 = await TryEnsureCoreWebView2Async();
        if (coreWebView2 is null)
        {
            _savedDomainCookieSnapshotsRestored = false;
            return;
        }

        var restored = 0;
        var failed = 0;
        var cookieManager = coreWebView2.CookieManager;
        foreach (var snapshot in snapshots)
        {
            foreach (var persistedCookie in snapshot.Cookies)
            {
                try
                {
                    var cookie = cookieManager.CreateCookie(
                        persistedCookie.Name,
                        persistedCookie.Value,
                        persistedCookie.Domain,
                        persistedCookie.Path);

                    cookie.IsSecure = persistedCookie.IsSecure;
                    cookie.IsHttpOnly = persistedCookie.IsHttpOnly;

                    if (persistedCookie.ExpiresAtUnixSeconds is { } expiresAtUnixSeconds)
                    {
                        cookie.Expires = expiresAtUnixSeconds;
                    }

                    if (TryMapSameSite(persistedCookie.SameSite, out var sameSite))
                    {
                        cookie.SameSite = sameSite;
                    }

                    cookieManager.AddOrUpdateCookie(cookie);
                    restored++;
                }
                catch
                {
                    failed++;
                }
            }
        }

        _naverTalkBrowser.SetStatus(
            failed == 0
                ? $"저장된 도메인 쿠키 {restored}개를 자동 로드했습니다."
                : $"저장된 도메인 쿠키 {restored}개 로드, {failed}개 실패했습니다.");
#endif
    }

#if WINDOWS
    private async Task<CoreWebView2?> TryEnsureCoreWebView2Async()
    {
        if (NaverTalkWebView.Handler is not Microsoft.Maui.Handlers.WebViewHandler handler ||
            handler.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 webView2)
        {
            return null;
        }

        if (webView2.CoreWebView2 is null)
        {
            await webView2.EnsureCoreWebView2Async();
        }

        return webView2.CoreWebView2;
    }

    private static bool TryMapSameSite(string? sameSite, out CoreWebView2CookieSameSiteKind kind)
    {
        switch (sameSite)
        {
            case nameof(CoreWebView2CookieSameSiteKind.Lax):
                kind = CoreWebView2CookieSameSiteKind.Lax;
                return true;
            case nameof(CoreWebView2CookieSameSiteKind.Strict):
                kind = CoreWebView2CookieSameSiteKind.Strict;
                return true;
            case nameof(CoreWebView2CookieSameSiteKind.None):
                kind = CoreWebView2CookieSameSiteKind.None;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private void SetupWindowKeyboardHook()
    {
        if (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView
            is not Microsoft.UI.Xaml.Window nativeWindow)
        {
            System.Diagnostics.Debug.WriteLine("[KeyboardHook] Failed to get native window");
            return;
        }

        RegisterKeyboardHook(nativeWindow);
    }

    private void TeardownWindowKeyboardHook()
    {
        UnregisterKeyboardHook();
    }

    private void RegisterKeyboardHook(Microsoft.UI.Xaml.Window nativeWindow)
    {
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            return;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
        if (hwnd == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[KeyboardHook] Window handle is null");
            return;
        }

        _mainWindowHandle = hwnd;

        _keyboardHookProc = LowLevelKeyboardProc;

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var moduleName = process.MainModule?.ModuleName;
        var hModule = string.IsNullOrEmpty(moduleName) ? IntPtr.Zero : GetModuleHandle(moduleName);

        _keyboardHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardHookProc, hModule, 0);

        if (_keyboardHookHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine($"[KeyboardHook] Failed to install. Win32 error={error}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[KeyboardHook] Installed successfully. hModule={hModule}");
        }
    }

    private void UnregisterKeyboardHook()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookHandle);
        }

        _keyboardHookProc = null;
        _keyboardHookHandle = IntPtr.Zero;
        _mainWindowHandle = IntPtr.Zero;
    }

    private IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        var message = wParam.ToInt32();
        if (message is not WM_KEYDOWN and not WM_SYSKEYDOWN)
        {
            return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        if (lParam == IntPtr.Zero)
        {
            return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        if (!IsAppWindowForeground())
        {
            return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        var keyData = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var vkCode = (int)keyData.vkCode;
        var ctrlPressed = IsVirtualKeyDown(VK_CONTROL);
        var shiftPressed = IsVirtualKeyDown(VK_SHIFT);
        var altPressed = IsVirtualKeyDown(VK_MENU);
        var lwinPressed = IsVirtualKeyDown(VK_LWIN);
        var rwinPressed = IsVirtualKeyDown(VK_RWIN);
        var webViewFocused = _isNaverTalkWebViewFocused;
        var keyDebugMessage =
            $"{DateTime.Now:HH:mm:ss} vk=0x{vkCode:X2}({vkCode}), ctrl={ctrlPressed}, shift={shiftPressed}, alt={altPressed}, lwin={lwinPressed}, rwin={rwinPressed}, webviewFocused={webViewFocused}";

        MainThread.BeginInvokeOnMainThread(() =>
        {
            _codexPromptMacroService.PublishKeyObserved(keyDebugMessage);
        });

        if (_isNaverTalkWebViewFocused && altPressed && !ctrlPressed && !lwinPressed && !rwinPressed && IsMacroNumberVirtualKey(vkCode))
        {
            // WebView 포커스일 때만 Alt+숫자 매크로 단축키 전파를 차단
            return (IntPtr)1;
        }

        return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private static bool IsMacroNumberVirtualKey(int vkCode)
    {
        return (vkCode >= VK_NUMPAD1 && vkCode <= VK_NUMPAD9) ||
               (vkCode >= VK_DIGIT1 && vkCode <= VK_DIGIT9);
    }

    private static bool IsVirtualKeyDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private bool IsAppWindowForeground()
    {
        if (_mainWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        var foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero && foreground == _mainWindowHandle;
    }

    private delegate IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, KeyboardHookProc lpfn, IntPtr hmod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

#endif
}

