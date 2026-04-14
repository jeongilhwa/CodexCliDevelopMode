namespace CodexDesk;

using CodexDesk.Services;
using System.Globalization;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    private double _rightSplitterDragStartPointerX = double.NaN;
    private double _rightSplitterDragStartNetworkWidth;
    private double _rightSplitterDragStartCodexWidth;
    private bool _isRightSplitterDragging;
    private const double SplitterColumnWidth = 16.0;
    private const double RightSplitterColumnWidth = 10.0;
    private const double MinPaneWidth = 280.0;
    private const double MinRightPaneWidth = 240.0;
    private const double FallbackWebViewContentWidth = 1100.0;
    private const double MinWebViewZoomFactor = 0.65;
    private static readonly Color SplitterIdleColor = Color.FromArgb("#B0BEC5");
    private static readonly Color SplitterHoverColor = Color.FromArgb("#78909C");
    private static readonly Color SplitterActiveColor = Color.FromArgb("#1D5C74");
    private readonly ObservableCollection<WebViewNetworkLogEntry> _webViewNetworkEntries = [];
    private const int MaxWebViewNetworkEntries = 120;
    private sealed record WebViewNetworkLogEntry(string Summary, string RequestDetails, string ResponseDetails);
#if WINDOWS
    private IntPtr _keyboardHookHandle;
    private IntPtr _mainWindowHandle;
    private KeyboardHookProc? _keyboardHookProc;
    private bool _webViewKeyboardBridgeAttached;
    private bool _webViewInspectorModeEnabled;
    private bool _webViewNetworkCaptureEnabled;
    private bool _webViewNetworkDomainEnabled;
    private bool _webViewNewWindowHandlerAttached;
    private bool _webViewInspectorScriptRegistered;
    private CoreWebView2? _networkReceiversAttachedCore;
    private CoreWebView2DevToolsProtocolEventReceiver? _networkRequestWillBeSentReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _networkRequestWillBeSentExtraInfoReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _networkResponseReceivedReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _networkResponseReceivedExtraInfoReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _networkLoadingFinishedReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _networkLoadingFailedReceiver;
    private readonly object _webViewNetworkSync = new();
    private readonly Dictionary<string, CapturedNetworkRequest> _pendingNetworkRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingRequestExtraHeaders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingResponseExtraHeaders = new(StringComparer.Ordinal);
    private Microsoft.UI.Xaml.Input.Pointer? _capturedSplitterPointer;
    private Microsoft.UI.Xaml.Input.Pointer? _capturedRightSplitterPointer;
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

    private sealed class CapturedNetworkRequest
    {
        public string RequestId { get; init; } = string.Empty;
        public string Method { get; set; } = "GET";
        public string Url { get; set; } = string.Empty;
        public string RequestHeadersPreview { get; set; } = string.Empty;
        public string RequestBodyPreview { get; set; } = string.Empty;
        public string ResourceType { get; set; } = string.Empty;
        public string MimeType { get; set; } = string.Empty;
        public int? Status { get; set; }
        public string StatusText { get; set; } = string.Empty;
        public string ResponseHeadersPreview { get; set; } = string.Empty;
        public double StartedMonotonicSeconds { get; set; }
        public double EncodedDataLength { get; set; }
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    }
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
        UpdateWebViewNetworkCaptureToggleButton();
        if (WebViewNetworkListView is not null)
        {
            WebViewNetworkListView.ItemsSource = _webViewNetworkEntries;
        }
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

    private void UpdateWebViewNetworkCaptureToggleButton()
    {
        if (WebViewNetworkCaptureToggleButton is null)
        {
            return;
        }

#if WINDOWS
        WebViewNetworkCaptureToggleButton.Text = _webViewNetworkCaptureEnabled ? "네트워크 캡처 ON" : "네트워크 캡처 OFF";
        WebViewNetworkCaptureToggleButton.BackgroundColor = _webViewNetworkCaptureEnabled
            ? Color.FromArgb("#A95C00")
            : Color.FromArgb("#4E5D66");
#else
        WebViewNetworkCaptureToggleButton.Text = "Windows 전용";
        WebViewNetworkCaptureToggleButton.IsEnabled = false;
#endif
    }

    private async void OnWebViewNetworkCaptureToggleClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        await ToggleWebViewNetworkCaptureModeAsync();
#else
        _naverTalkBrowser.SetStatus("Network capture is only available on Windows WebView2.");
#endif
    }

    private void AppendWebViewNetworkEntry(
        string summary,
        string requestDetails,
        string responseDetails)
    {
        if (WebViewNetworkListView is null ||
            WebViewNetworkRequestHeadersLabel is null ||
            WebViewNetworkRequestBodyLabel is null ||
            WebViewNetworkResponseHeadersLabel is null ||
            WebViewNetworkResponseBodyLabel is null)
        {
            return;
        }

        var normalizedSummary = string.IsNullOrWhiteSpace(summary) ? "[network]" : summary.Trim();
        var normalizedRequest = string.IsNullOrWhiteSpace(requestDetails) ? "-" : requestDetails.Trim();
        var normalizedResponse = string.IsNullOrWhiteSpace(responseDetails) ? "-" : responseDetails.Trim();

        _webViewNetworkEntries.Insert(0, new WebViewNetworkLogEntry(normalizedSummary, normalizedRequest, normalizedResponse));
        while (_webViewNetworkEntries.Count > MaxWebViewNetworkEntries)
        {
            _webViewNetworkEntries.RemoveAt(_webViewNetworkEntries.Count - 1);
        }

        if (WebViewNetworkListView.SelectedItem is null)
        {
            WebViewNetworkListView.SelectedItem = _webViewNetworkEntries[0];
            ApplyNetworkDetails(_webViewNetworkEntries[0]);
        }
    }

    private void OnWebViewNetworkSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (WebViewNetworkRequestHeadersLabel is null ||
            WebViewNetworkRequestBodyLabel is null ||
            WebViewNetworkResponseHeadersLabel is null ||
            WebViewNetworkResponseBodyLabel is null)
        {
            return;
        }

        if (e.CurrentSelection.FirstOrDefault() is WebViewNetworkLogEntry selected)
        {
            ApplyNetworkDetails(selected);
            return;
        }

        WebViewNetworkRequestHeadersLabel.Text = string.Empty;
        WebViewNetworkRequestBodyLabel.Text = string.Empty;
        WebViewNetworkResponseHeadersLabel.Text = string.Empty;
        WebViewNetworkResponseBodyLabel.Text = string.Empty;
    }

    private void ApplyNetworkDetails(WebViewNetworkLogEntry entry)
    {
        var requestHeaders = ExtractSection(entry.RequestDetails, "HEADERS:", "BODY:");
        var requestBody = ExtractSection(entry.RequestDetails, "BODY:", null);
        var responseHeaders = ExtractSection(entry.ResponseDetails, "HEADERS:", "BODY:");
        var responseBody = ExtractSection(entry.ResponseDetails, "BODY:", null);

        WebViewNetworkRequestHeadersLabel!.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"SUMMARY: {entry.Summary}{Environment.NewLine}{Environment.NewLine}{requestHeaders}");
        WebViewNetworkRequestBodyLabel!.Text = requestBody;
        WebViewNetworkResponseHeadersLabel!.Text = responseHeaders;
        WebViewNetworkResponseBodyLabel!.Text = responseBody;
    }

    private static string ExtractSection(string source, string startMarker, string? endMarker)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "-";
        }

        var startIndex = source.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
        {
            return startMarker.Equals("BODY:", StringComparison.OrdinalIgnoreCase)
                ? source.Trim()
                : "-";
        }

        startIndex += startMarker.Length;
        var chunk = source[startIndex..];

        if (!string.IsNullOrEmpty(endMarker))
        {
            var endIndex = chunk.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
            if (endIndex >= 0)
            {
                chunk = chunk[..endIndex];
            }
        }

        var trimmed = chunk.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "-" : trimmed;
    }

    private static string TrimForLog(string? text, int maxLength = 300)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "-";
        }

        var flattened = text
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        return flattened.Length <= maxLength
            ? flattened
            : $"{flattened[..maxLength]}...";
    }
}

