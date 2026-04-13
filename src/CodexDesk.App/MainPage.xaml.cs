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
#endif

public partial class MainPage : ContentPage
{
    private readonly NaverTalkBrowserService _naverTalkBrowser;
    private readonly CompletedChatsCollector _completedChatsCollector;
    private readonly CodexPromptMacroService _codexPromptMacroService;
    private static readonly JsonSerializerOptions AppJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private DateTimeOffset _lastAutoLoginAttemptAt;
    private string? _lastAutoLoginAttemptUrl;
    private string? _lastSavedCookieJarJson;
    private volatile bool _isNaverTalkWebViewFocused;
    private double _splitterDragStartPointerX = double.NaN;
    private double _splitterDragStartWebViewWidth;
    private double _splitterDragStartChatWidth;
    private bool _isSplitterDragging;
    private const double SplitterColumnWidth = 16.0;
    private const double MinPaneWidth = 280.0;
    private const double FallbackWebViewContentWidth = 1100.0;
    private const double MinWebViewZoomFactor = 0.65;
    private static readonly Color SplitterIdleColor = Color.FromArgb("#B0BEC5");
    private static readonly Color SplitterHoverColor = Color.FromArgb("#78909C");
    private static readonly Color SplitterActiveColor = Color.FromArgb("#1D5C74");
#if WINDOWS
    private IntPtr _keyboardHookHandle;
    private IntPtr _mainWindowHandle;
    private KeyboardHookProc? _keyboardHookProc;
    private bool _webViewKeyboardBridgeAttached;
    private bool _webViewInspectorModeEnabled;
    private bool _webViewInspectorScriptRegistered;
    private Microsoft.UI.Xaml.Input.Pointer? _capturedSplitterPointer;
    private double _measuredWebViewContentWidth;
    private double _lastAppliedWebViewZoomFactor = 1.0;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_NUMPAD1 = 0x61;
    private const int VK_NUMPAD9 = 0x69;
    private const int VK_DIGIT1 = 0x31;
    private const int VK_DIGIT9 = 0x39;
    private const int VK_END = 0x23;
    private const int VK_DOWN = 0x28;
    private const int VK_NEXT = 0x22;
    private const int VK_LEFT = 0x25;
    private const int VK_CLEAR = 0x0C;
    private const int VK_RIGHT = 0x27;
    private const int VK_HOME = 0x24;
    private const int VK_UP = 0x26;
    private const int VK_PRIOR = 0x21;
    private const int VK_V = 0x56;
    private const string WebViewKeyDebugPrefix = "codex-keydbg:";
    private const string WebViewInspectPrefix = "codex-inspect:";
#endif

    public MainPage(
        NaverTalkBrowserService naverTalkBrowser,
        CompletedChatsCollector completedChatsCollector,
        CodexPromptMacroService codexPromptMacroService)
    {
        InitializeComponent();

        _naverTalkBrowser = naverTalkBrowser;
        _completedChatsCollector = completedChatsCollector;
        _codexPromptMacroService = codexPromptMacroService;
        _naverTalkBrowser.StateChanged += OnBrowserStateChanged;
        _naverTalkBrowser.CommandRequested += OnBrowserCommandRequested;

        BrowserPane.SizeChanged += OnBrowserPaneSizeChanged;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        NaverTalkWebView.HandlerChanged += OnNaverTalkWebViewHandlerChanged;

#if WINDOWS
        // Loaded 시점에 다시 초기화됩니다.
        ConfigureWebView2ForWindows();
#endif

        UpdateWebViewInspectorToggleButton();
    }

    private void UpdateWebViewInspectorToggleButton()
    {
        if (WebViewInspectorToggleButton is null)
        {
            return;
        }

#if WINDOWS
        WebViewInspectorToggleButton.Text = _webViewInspectorModeEnabled ? "개발자 모드 ON" : "개발자 모드 OFF";
        WebViewInspectorToggleButton.BackgroundColor = _webViewInspectorModeEnabled
            ? Color.FromArgb("#0D8A65")
            : Color.FromArgb("#1D5C74");
#else
        WebViewInspectorToggleButton.Text = "Windows 전용";
        WebViewInspectorToggleButton.IsEnabled = false;
#endif
    }

    private async void OnWebViewInspectorToggleClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        await ToggleWebViewInspectorModeAsync();
#else
        _naverTalkBrowser.SetStatus("Developer inspect mode is only available on Windows WebView2.");
#endif
    }
}

