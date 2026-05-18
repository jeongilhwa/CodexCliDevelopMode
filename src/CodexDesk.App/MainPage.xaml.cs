namespace CodexDesk;

using CodexDesk.Services;
using InterceptKit.WebView.Windows;
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
    private readonly WebView2InterceptController _webViewInterceptController;
    private readonly CodexDeskWebViewInterceptPanelState _webViewInterceptPanelState;
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
    private readonly List<WebViewNetworkLogEntry> _webViewNetworkEntries = [];
    private readonly ObservableCollection<WebViewNetworkLogEntry> _filteredWebViewNetworkEntries = [];
    private WebViewNetworkLogEntry? _selectedWebViewNetworkEntry;
    private bool _isRefreshingNetworkSelection;
    private bool _isNetworkListPinnedToBottom = true;
    private const int MaxWebViewNetworkEntries = NetworkCaptureFilterSupport.MaxEntries;
    private const int MaxNetworkAnalysisPromptLength = 52000;
    private int _webViewNetworkSequence;
    private NetworkCaptureFilterKind _networkCaptureFilter = NetworkCaptureFilterKind.All;
    private string _networkSearchText = string.Empty;
    private NetworkAnalysisScope _networkAnalysisScope = NetworkAnalysisScope.Selected;
    private NetworkAnalysisDetailMode _networkAnalysisDetailMode = NetworkAnalysisDetailMode.Summary;
    private WorkspaceTab _activeWorkspaceTab = WorkspaceTab.Codex;
    private InspectorSelectionSnapshot? _lastInspectorSelection;
    private WebViewPageSnapshotSaveResult? _pendingWebViewSnapshot;
    private WebViewNetworkCaptureSaveResult? _pendingNetworkCaptureSnapshot;
    private TaskCompletionSource<bool>? _pendingSnapshotAnalyzeChoice;
    private string? _pendingSnapshotUserQuestion;
    private bool _snapshotModalHidWebView;

    private enum WorkspaceTab
    {
        Codex,
        WebBrowser,
        NetworkCapture
    }

    private enum NetworkScrollTarget
    {
        None,
        LogList,
        RequestHeaders,
        RequestBody,
        ResponseHeaders,
        ResponseBody
    }

    private enum NetworkAnalysisScope
    {
        Selected,
        All
    }

    private enum NetworkAnalysisDetailMode
    {
        Summary,
        ErrorsDetailed,
        FullDetailed
    }

    private sealed record WebViewNetworkLogEntry(
        int Sequence,
        string Summary,
        string RequestDetails,
        string ResponseDetails,
        string StatusDisplay,
        Color StatusColor,
        string MethodDisplay,
        string HostDisplay,
        string PathDisplay,
        string FullUrlDisplay,
        string SchemeDisplay,
        string ResourceTypeDisplay,
        string MimeTypeDisplay,
        NetworkCaptureFilterKind FilterKind,
        string SizeDisplay,
        string TimeDisplay)
    {
        public string IndexDisplay => Sequence.ToString("D3", CultureInfo.InvariantCulture);
    }

    private sealed record InspectorSelectionSnapshot(
        string Selector,
        string Tag,
        string ElementId,
        string Name,
        string ClassName,
        string Role,
        string InputType,
        string AriaLabel,
        string Placeholder,
        string ValuePreview,
        string TextPreview,
        string Href,
        string DomPath,
        double Left,
        double Top,
        double Width,
        double Height,
        string RawJson);

    private sealed record BrowserRequestResult(
        bool Ok,
        int Status,
        string StatusText,
        string Url,
        string Body);
#if WINDOWS
    private IntPtr _keyboardHookHandle;
    private IntPtr _mainWindowHandle;
    private KeyboardHookProc? _keyboardHookProc;
    private bool _windowChromeConfigured;
    private bool _webViewKeyboardBridgeAttached;
    private bool _webViewInspectorModeEnabled;
    private bool _webViewNetworkCaptureEnabled;
    private bool _webViewNetworkDomainEnabled;
    private bool _webViewNewWindowHandlerAttached;
    private bool _webViewInspectorScriptRegistered;
    private bool _savedDomainCookieSnapshotsRestored;
    private string? _lastAppliedWebViewUserAgent;
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
        CodexPromptMacroService codexPromptMacroService,
        WebView2InterceptController webViewInterceptController,
        CodexDeskWebViewInterceptPanelState webViewInterceptPanelState)
    {
        InitializeComponent();

        _naverTalkBrowser = naverTalkBrowser;
        _completedChatsCollector = completedChatsCollector;
        _codexPromptMacroService = codexPromptMacroService;
        _webViewInterceptController = webViewInterceptController;
        _webViewInterceptPanelState = webViewInterceptPanelState;
        _naverTalkBrowser.StateChanged += OnBrowserStateChanged;
        _naverTalkBrowser.CommandRequested += OnBrowserCommandRequested;
        _webViewInterceptPanelState.Changed += OnWebViewInterceptPanelChanged;

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
        if (BrowserRequestMethodEntry is not null)
        {
            BrowserRequestMethodEntry.Text = "POST";
        }

        if (BrowserRequestUrlEntry is not null)
        {
            BrowserRequestUrlEntry.Text = "/Home/ExtendLoginTime";
        }

        if (WebViewNetworkListView is not null)
        {
            WebViewNetworkListView.ItemsSource = _filteredWebViewNetworkEntries;
        }

        if (InterceptQueueCollection is not null)
        {
            InterceptQueueCollection.ItemsSource = _webViewInterceptPanelState.GetPendingItems();
        }

        InitializeWorkspaceShell();
        UpdateWebViewInterceptPanel();
        UpdateNetworkCount();
        UpdateNetworkFilterButtons();
        UpdateNetworkAnalysisButtons();
        UpdateNetworkPanelScrollbars(NetworkScrollTarget.None);
    }

    private void InitializeWorkspaceShell()
    {
        ApplyWorkspaceTab(WorkspaceTab.Codex);
    }

    private void OnWorkspaceTabClicked(object? sender, EventArgs e)
    {
        if (sender is not Element target || string.IsNullOrWhiteSpace(target.ClassId))
        {
            return;
        }

        if (!Enum.TryParse<WorkspaceTab>(target.ClassId, ignoreCase: true, out var tab))
        {
            return;
        }

        ApplyWorkspaceTab(tab);
    }

    private void ApplyWorkspaceTab(WorkspaceTab tab)
    {
        _activeWorkspaceTab = tab;

        if (CodexWorkspacePanel is not null)
        {
            CodexWorkspacePanel.IsVisible = tab == WorkspaceTab.Codex;
        }

        if (WebBrowserWorkspacePanel is not null)
        {
            WebBrowserWorkspacePanel.IsVisible = tab == WorkspaceTab.WebBrowser;
        }

        if (NetworkWorkspacePanel is not null)
        {
            NetworkWorkspacePanel.IsVisible = tab == WorkspaceTab.NetworkCapture;
        }

        ApplyTabStyle(
            CodexTabBorder,
            CodexTabIcon,
            CodexTabTitleLabel,
            CodexTabCountLabel,
            tab == WorkspaceTab.Codex,
            activeCount: "default",
            inactiveCount: "tab");
        ApplyTabStyle(
            WebBrowserTabBorder,
            WebBrowserTabIcon,
            WebBrowserTabTitleLabel,
            WebBrowserTabCountLabel,
            tab == WorkspaceTab.WebBrowser,
            activeCount: _webViewInterceptPanelState.CountText,
            inactiveCount: _webViewInterceptPanelState.CountText);
        ApplyTabStyle(
            NetworkCaptureTabBorder,
            NetworkCaptureTabIcon,
            NetworkCaptureTabTitleLabel,
            NetworkCaptureTabCountLabel,
            tab == WorkspaceTab.NetworkCapture,
            activeCount: "active",
            inactiveCount: "tab");
    }

    private static void ApplyTabStyle(
        Border? border,
        Border? iconBorder,
        Label? titleLabel,
        Label? countLabel,
        bool isActive,
        string activeCount,
        string inactiveCount)
    {
        if (border is not null)
        {
            border.BackgroundColor = isActive
                ? Color.FromArgb("#F7FFFCF8")
                : Color.FromArgb("#1120252C");
            border.Stroke = isActive
                ? Color.FromArgb("#12000000")
                : Color.FromArgb("#10000000");
        }

        if (iconBorder is not null)
        {
            iconBorder.BackgroundColor = Color.FromArgb("#1220252C");
        }

        if (titleLabel is not null)
        {
            titleLabel.TextColor = isActive
                ? Color.FromArgb("#14232B")
                : Color.FromArgb("#637780");
        }

        if (countLabel is not null)
        {
            countLabel.Text = isActive ? activeCount : inactiveCount;
            countLabel.TextColor = Color.FromArgb("#61757E");
        }
    }

    private void UpdateWebViewInspectorToggleButton()
    {
        if (WebViewInspectorToggleButton is null)
        {
            return;
        }

        WebViewInspectorToggleButton.Text = "Inspect";
        WebViewInspectorToggleButton.BackgroundColor = Color.FromArgb("#1220252C");
        WebViewInspectorToggleButton.TextColor = Color.FromArgb("#50656F");
#if !WINDOWS
        WebViewInspectorToggleButton.IsEnabled = false;
#endif
    }

    private void UpdateBrowserFocusButton()
    {
        if (BrowserFocusButton is null)
        {
            return;
        }

#if WINDOWS
        BrowserFocusButton.Text = _webViewInspectorModeEnabled ? "Focus ON" : "Focus OFF";
        BrowserFocusButton.BackgroundColor = _webViewInspectorModeEnabled
            ? Color.FromArgb("#1F6780")
            : Color.FromArgb("#1220252C");
        BrowserFocusButton.TextColor = _webViewInspectorModeEnabled
            ? Colors.White
            : Color.FromArgb("#50656F");
#else
        BrowserFocusButton.Text = "Windows 전용";
        BrowserFocusButton.IsEnabled = false;
#endif
    }

    private async void OnBrowserFocusClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        await ToggleWebViewInspectorModeAsync();
#else
        _naverTalkBrowser.SetStatus("Focus mode is only available on Windows WebView2.");
#endif
    }

    private void OnWebViewInspectorToggleClicked(object? sender, EventArgs e)
    {
        OpenInspectorDetailsModal();
    }

    private void UpdateWebViewNetworkCaptureToggleButton()
    {
        if (WebViewNetworkCaptureToggleButton is null &&
            NetworkCaptureToolbarToggleButton is null)
        {
            return;
        }

#if WINDOWS
        var buttonText = _webViewNetworkCaptureEnabled ? "Capture ON" : "Capture OFF";
        var background = _webViewNetworkCaptureEnabled
            ? Color.FromArgb("#A95C00")
            : Color.FromArgb("#EEF3F6");
        var textColor = _webViewNetworkCaptureEnabled
            ? Colors.White
            : Color.FromArgb("#31474F");
        if (WebViewNetworkCaptureToggleButton is not null)
        {
            WebViewNetworkCaptureToggleButton.Text = buttonText;
            WebViewNetworkCaptureToggleButton.BackgroundColor = background;
            WebViewNetworkCaptureToggleButton.TextColor = textColor;
        }

        if (NetworkCaptureToolbarToggleButton is not null)
        {
            NetworkCaptureToolbarToggleButton.Text = buttonText;
            NetworkCaptureToolbarToggleButton.BackgroundColor = background;
            NetworkCaptureToolbarToggleButton.TextColor = textColor;
        }
#else
        if (WebViewNetworkCaptureToggleButton is not null)
        {
            WebViewNetworkCaptureToggleButton.Text = "Windows 전용";
            WebViewNetworkCaptureToggleButton.IsEnabled = false;
        }

        if (NetworkCaptureToolbarToggleButton is not null)
        {
            NetworkCaptureToolbarToggleButton.Text = "Windows 전용";
            NetworkCaptureToolbarToggleButton.IsEnabled = false;
        }
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

    private void AppendWebViewNetworkEntry(WebViewNetworkLogEntry entry)
    {
        if (WebViewNetworkListView is null ||
            WebViewNetworkRequestHeadersLabel is null ||
            WebViewNetworkRequestBodyLabel is null ||
            WebViewNetworkResponseHeadersLabel is null ||
            WebViewNetworkResponseBodyLabel is null)
        {
            return;
        }

        var shouldAutoScroll = _isNetworkListPinnedToBottom;

        NetworkCaptureListSupport.AppendEntry(_webViewNetworkEntries, entry, MaxWebViewNetworkEntries);

        RefreshFilteredNetworkEntries(preserveSelection: true);

        if (_selectedWebViewNetworkEntry is null &&
            WebViewNetworkListView.SelectedItem is null &&
            _filteredWebViewNetworkEntries.Count > 0)
        {
            var lastEntry = NetworkCaptureListSupport.ResolveAutoSelectedEntryAfterAppend(_filteredWebViewNetworkEntries);
            if (lastEntry is not null)
            {
                _selectedWebViewNetworkEntry = lastEntry;
                WebViewNetworkListView.SelectedItem = lastEntry;
                ApplyNetworkDetails(lastEntry);
            }
        }

        if (shouldAutoScroll)
        {
            ScrollWebViewNetworkListToBottom();
        }

        UpdateNetworkCount();
        UpdateNetworkPanelScrollbars(NetworkScrollTarget.LogList);
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
            _selectedWebViewNetworkEntry = selected;
            UpdateNetworkPanelScrollbars(NetworkScrollTarget.LogList);
            ApplyNetworkDetails(selected);
            return;
        }

        if (_isRefreshingNetworkSelection)
        {
            return;
        }

        _selectedWebViewNetworkEntry = null;
        WebViewNetworkRequestHeadersLabel.Text = string.Empty;
        WebViewNetworkRequestBodyLabel.Text = string.Empty;
        WebViewNetworkResponseHeadersLabel.Text = string.Empty;
        WebViewNetworkResponseBodyLabel.Text = string.Empty;
        if (SelectedNetworkSummaryLabel is not null)
        {
            SelectedNetworkSummaryLabel.Text = "선택된 요청 없음";
        }

        if (SelectedNetworkMethodLabel is not null)
        {
            SelectedNetworkMethodLabel.Text = "-";
        }

        if (SelectedNetworkStatusLabel is not null)
        {
            SelectedNetworkStatusLabel.Text = "-";
            SelectedNetworkStatusLabel.TextColor = Color.FromArgb("#60717A");
        }

        if (SelectedNetworkSizeLabel is not null)
        {
            SelectedNetworkSizeLabel.Text = "-";
        }

        if (SelectedNetworkTimeLabel is not null)
        {
            SelectedNetworkTimeLabel.Text = "-";
        }

        UpdateNetworkPanelScrollbars(NetworkScrollTarget.None);
    }

    private void OnNetworkClearClicked(object? sender, EventArgs e)
    {
        _webViewNetworkEntries.Clear();
        _filteredWebViewNetworkEntries.Clear();
        _selectedWebViewNetworkEntry = null;
        _webViewNetworkSequence = 0;
        if (WebViewNetworkListView is not null)
        {
            WebViewNetworkListView.SelectedItem = null;
        }

        if (WebViewNetworkRequestHeadersLabel is not null)
        {
            WebViewNetworkRequestHeadersLabel.Text = "Request headers";
        }

        if (WebViewNetworkRequestBodyLabel is not null)
        {
            WebViewNetworkRequestBodyLabel.Text = "Request body";
        }

        if (WebViewNetworkResponseHeadersLabel is not null)
        {
            WebViewNetworkResponseHeadersLabel.Text = "Response headers";
        }

        if (WebViewNetworkResponseBodyLabel is not null)
        {
            WebViewNetworkResponseBodyLabel.Text = "Response body";
        }

        if (SelectedNetworkSummaryLabel is not null)
        {
            SelectedNetworkSummaryLabel.Text = "선택된 요청 없음";
        }

        if (SelectedNetworkMethodLabel is not null)
        {
            SelectedNetworkMethodLabel.Text = "-";
        }

        if (SelectedNetworkStatusLabel is not null)
        {
            SelectedNetworkStatusLabel.Text = "-";
            SelectedNetworkStatusLabel.TextColor = Color.FromArgb("#60717A");
        }

        if (SelectedNetworkSizeLabel is not null)
        {
            SelectedNetworkSizeLabel.Text = "-";
        }

        if (SelectedNetworkTimeLabel is not null)
        {
            SelectedNetworkTimeLabel.Text = "-";
        }

        UpdateNetworkCount();
        UpdateNetworkPanelScrollbars(NetworkScrollTarget.None);
    }

    private void OnNetworkFilterClicked(object? sender, EventArgs e)
    {
        if (sender is not Element element ||
            string.IsNullOrWhiteSpace(element.ClassId) ||
            !Enum.TryParse<NetworkCaptureFilterKind>(element.ClassId, ignoreCase: true, out var filter))
        {
            return;
        }

        if (filter == NetworkCaptureFilterKind.All)
        {
            _networkCaptureFilter = NetworkCaptureFilterKind.All;
        }
        else
        {
            _networkCaptureFilter = filter;
        }

        RefreshFilteredNetworkEntries(preserveSelection: false);
        UpdateNetworkFilterButtons();
        UpdateNetworkCount();
    }

    private void OnNetworkSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _networkSearchText = e.NewTextValue?.Trim() ?? string.Empty;
        RefreshFilteredNetworkEntries(preserveSelection: true);
        UpdateNetworkCount();
    }

    private void OnNetworkAnalysisScopeClicked(object? sender, EventArgs e)
    {
        _networkAnalysisScope = _networkAnalysisScope == NetworkAnalysisScope.Selected
            ? NetworkAnalysisScope.All
            : NetworkAnalysisScope.Selected;
        UpdateNetworkAnalysisButtons();
    }

    private void OnNetworkAnalysisDetailClicked(object? sender, EventArgs e)
    {
        _networkAnalysisDetailMode = _networkAnalysisDetailMode switch
        {
            NetworkAnalysisDetailMode.Summary => NetworkAnalysisDetailMode.ErrorsDetailed,
            NetworkAnalysisDetailMode.ErrorsDetailed => NetworkAnalysisDetailMode.FullDetailed,
            _ => NetworkAnalysisDetailMode.Summary
        };
        UpdateNetworkAnalysisButtons();
    }

    private async void OnNetworkAnalyzeWithCodexClicked(object? sender, EventArgs e)
    {
        var entries = ResolveNetworkAnalysisEntries();
        if (entries.Count == 0)
        {
            _naverTalkBrowser.SetStatus(_networkAnalysisScope == NetworkAnalysisScope.Selected
                ? "분석할 네트워크 요청을 먼저 선택하세요."
                : "분석할 네트워크 캡쳐 목록이 없습니다.");
            return;
        }

        try
        {
            SetNetworkAnalyzeButtonSavingState(isSaving: true);
            var snapshots = entries.Select(ToNetworkCaptureEntrySnapshot).ToList();
            var result = await WebViewNetworkCaptureSnapshotWriter.SaveAsync(
                snapshots,
                AppContext.BaseDirectory,
                _networkAnalysisScope == NetworkAnalysisScope.Selected ? "selected" : "all",
                FormatNetworkAnalysisDetailMode(_networkAnalysisDetailMode));

            _pendingNetworkCaptureSnapshot = result;
            ShowNetworkCaptureSnapshotConfirmModal(result);
            _naverTalkBrowser.SetStatus($"Network capture 저장 완료: {result.DirectoryPath}");
        }
        catch (Exception ex)
        {
            _naverTalkBrowser.SetStatus($"Network capture 저장 실패: {ex.Message}");
        }
        finally
        {
            SetNetworkAnalyzeButtonSavingState(isSaving: false);
        }
    }

    private void SetNetworkAnalyzeButtonSavingState(bool isSaving)
    {
        if (NetworkAnalyzeWithCodexButton is null)
        {
            return;
        }

        NetworkAnalyzeWithCodexButton.IsEnabled = !isSaving;
        NetworkAnalyzeWithCodexButton.Text = isSaving ? "Saving..." : "Codex 분석";
        NetworkAnalyzeWithCodexButton.BackgroundColor = isSaving
            ? Color.FromArgb("#1F6780")
            : Color.FromArgb("#1F6780");
        NetworkAnalyzeWithCodexButton.TextColor = Colors.White;
    }

    private static WebViewNetworkCaptureEntrySnapshot ToNetworkCaptureEntrySnapshot(WebViewNetworkLogEntry entry)
    {
        return new WebViewNetworkCaptureEntrySnapshot(
            int.TryParse(entry.IndexDisplay, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) ? sequence : 0,
            entry.Summary,
            entry.MethodDisplay,
            entry.StatusDisplay,
            entry.HostDisplay,
            entry.PathDisplay,
            entry.SizeDisplay,
            entry.TimeDisplay,
            entry.RequestDetails,
            entry.ResponseDetails,
            ExtractSection(entry.RequestDetails, "HEADERS:", "BODY:"),
            ExtractSection(entry.RequestDetails, "BODY:", null),
            ExtractSection(entry.ResponseDetails, "HEADERS:", "BODY:"),
            ExtractSection(entry.ResponseDetails, "BODY:", null));
    }

    private void ShowNetworkCaptureSnapshotConfirmModal(WebViewNetworkCaptureSaveResult result)
    {
        if (NetworkCaptureSnapshotFolderLabel is not null)
        {
            NetworkCaptureSnapshotFolderLabel.Text = $"Folder: {Path.GetFileName(result.DirectoryPath)}";
        }

        if (NetworkCaptureSnapshotPathLabel is not null)
        {
            NetworkCaptureSnapshotPathLabel.Text = $"Path: {result.DirectoryPath}";
        }

        if (NetworkCaptureSnapshotPromptEditor is not null)
        {
            NetworkCaptureSnapshotPromptEditor.Text = result.Prompt;
        }

        if (NetworkCaptureSnapshotConfirmModal is not null)
        {
            NetworkCaptureSnapshotConfirmModal.IsVisible = true;
        }
    }

    private void OnCancelNetworkCaptureSnapshotAnalysisClicked(object? sender, EventArgs e)
    {
        if (NetworkCaptureSnapshotConfirmModal is not null)
        {
            NetworkCaptureSnapshotConfirmModal.IsVisible = false;
        }
    }

    private void OnConfirmNetworkCaptureSnapshotAnalysisClicked(object? sender, EventArgs e)
    {
        var snapshot = _pendingNetworkCaptureSnapshot;
        if (snapshot is null)
        {
            _naverTalkBrowser.SetStatus("분석할 network capture snapshot 정보가 없습니다.");
            return;
        }

        if (NetworkCaptureSnapshotConfirmModal is not null)
        {
            NetworkCaptureSnapshotConfirmModal.IsVisible = false;
        }

        var prompt = NetworkCaptureSnapshotPromptEditor?.Text;
        ApplyWorkspaceTab(WorkspaceTab.Codex);
        _codexPromptMacroService.PublishPromptRequested(string.IsNullOrWhiteSpace(prompt) ? snapshot.Prompt : prompt);
        _naverTalkBrowser.SetStatus($"Codex 분석 프롬프트 생성 완료: {Path.GetFileName(snapshot.DirectoryPath)}");
    }

    private void UpdateNetworkAnalysisButtons()
    {
        if (NetworkAnalysisScopeButton is not null)
        {
            NetworkAnalysisScopeButton.Text = _networkAnalysisScope == NetworkAnalysisScope.Selected
                ? "범위: 선택"
                : "범위: 전체";
        }

        if (NetworkAnalysisDetailButton is not null)
        {
            NetworkAnalysisDetailButton.Text = _networkAnalysisDetailMode switch
            {
                NetworkAnalysisDetailMode.Summary => "상세: 요약",
                NetworkAnalysisDetailMode.ErrorsDetailed => "상세: 실패만",
                _ => "상세: 전체"
            };
        }
    }

    private void UpdateNetworkFilterButtons()
    {
        foreach (var button in ResolveNetworkFilterButtons())
        {
            if (!Enum.TryParse<NetworkCaptureFilterKind>(button.ClassId, ignoreCase: true, out var filter))
            {
                continue;
            }

            var selected = filter == _networkCaptureFilter;
            button.Text = NetworkCaptureFilterSupport.FormatLabel(filter);
            button.BackgroundColor = selected ? Color.FromArgb("#1F6780") : Color.FromArgb("#EEF3F6");
            button.TextColor = selected ? Colors.White : Color.FromArgb("#31474F");
        }
    }

    private IEnumerable<Button> ResolveNetworkFilterButtons()
    {
        var buttons = new[]
        {
            NetworkFilterAllButton,
            NetworkFilterApiButton,
            NetworkFilterDocumentButton,
            NetworkFilterJavaScriptButton,
            NetworkFilterCssButton,
            NetworkFilterImageButton,
            NetworkFilterMediaButton,
            NetworkFilterFontButton,
            NetworkFilterErrorButton,
            NetworkFilterOtherButton
        };

        return buttons.OfType<Button>();
    }

    private void RefreshFilteredNetworkEntries(bool preserveSelection)
    {
        var currentSelection = WebViewNetworkListView?.SelectedItem as WebViewNetworkLogEntry;

        _isRefreshingNetworkSelection = true;
        _filteredWebViewNetworkEntries.Clear();
        foreach (var entry in _webViewNetworkEntries.Where(entry =>
                     NetworkCaptureFilterSupport.Matches(_networkCaptureFilter, entry.FilterKind) &&
                     NetworkCaptureFilterSupport.MatchesSearch(
                         _networkSearchText,
                         entry.MethodDisplay,
                         entry.StatusDisplay,
                         entry.HostDisplay,
                         entry.PathDisplay,
                         entry.FullUrlDisplay,
                         entry.ResourceTypeDisplay,
                         entry.MimeTypeDisplay,
                         entry.RequestDetails,
                         entry.ResponseDetails)))
        {
            _filteredWebViewNetworkEntries.Add(entry);
        }

        if (WebViewNetworkListView is null)
        {
            _isRefreshingNetworkSelection = false;
            return;
        }

        var resolution = NetworkCaptureSelectionSupport.ResolveSelectionAfterRefresh(
            currentSelection,
            _selectedWebViewNetworkEntry,
            _filteredWebViewNetworkEntries,
            preserveSelection);

        WebViewNetworkListView.SelectedItem = resolution.SelectedEntry;

        if (resolution.SelectedEntry is not null)
        {
            _selectedWebViewNetworkEntry = resolution.SelectedEntry;
            ApplyNetworkDetails(resolution.SelectedEntry);
            _isRefreshingNetworkSelection = false;
            return;
        }

        _isRefreshingNetworkSelection = false;

        if (!resolution.KeepCurrentDetails)
        {
            _selectedWebViewNetworkEntry = null;
        }
    }

    private List<WebViewNetworkLogEntry> ResolveNetworkAnalysisEntries()
    {
        if (_networkAnalysisScope == NetworkAnalysisScope.Selected)
        {
            return WebViewNetworkListView?.SelectedItem is WebViewNetworkLogEntry selected
                ? [selected]
                : [];
        }

        return _webViewNetworkEntries.ToList();
    }

    private static string BuildNetworkAnalysisPrompt(
        IReadOnlyList<WebViewNetworkLogEntry> entries,
        NetworkAnalysisScope scope,
        NetworkAnalysisDetailMode detailMode)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("아래 WebView 네트워크 캡쳐를 분석해줘.");
        builder.AppendLine("- 실패/지연/중복 호출/인증 또는 CORS 의심 지점을 먼저 찾아줘.");
        builder.AppendLine("- 원인 후보, 확인 방법, 수정 방향을 한국어로 정리해줘.");
        builder.AppendLine("- 민감정보가 있어 보이면 노출 위험도 같이 표시해줘.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 분석 범위: {(scope == NetworkAnalysisScope.Selected ? "선택 요청" : "전체 캡쳐 리스트")}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 상세 포함: {FormatNetworkAnalysisDetailMode(detailMode)}");
        builder.AppendLine();
        builder.AppendLine("## Capture Summary");

        foreach (var entry in entries)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"- #{entry.IndexDisplay} {entry.MethodDisplay} {entry.StatusDisplay} {entry.TimeDisplay} {entry.SizeDisplay} {entry.HostDisplay}{entry.PathDisplay}");
        }

        var detailedEntries = ResolveDetailedNetworkAnalysisEntries(entries, detailMode);
        if (detailedEntries.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Request Details");
            foreach (var entry in detailedEntries)
            {
                AppendNetworkAnalysisEntry(builder, entry);
                if (builder.Length >= MaxNetworkAnalysisPromptLength)
                {
                    builder.AppendLine();
                    builder.AppendLine("[잘림] 프롬프트 길이 제한 때문에 이후 요청 상세는 생략됨.");
                    break;
                }
            }
        }

        return builder.Length <= MaxNetworkAnalysisPromptLength
            ? builder.ToString().Trim()
            : builder.ToString(0, MaxNetworkAnalysisPromptLength).Trim() + Environment.NewLine + "[잘림]";
    }

    private static List<WebViewNetworkLogEntry> ResolveDetailedNetworkAnalysisEntries(
        IReadOnlyList<WebViewNetworkLogEntry> entries,
        NetworkAnalysisDetailMode detailMode)
    {
        return detailMode switch
        {
            NetworkAnalysisDetailMode.Summary => [],
            NetworkAnalysisDetailMode.ErrorsDetailed => entries.Where(IsNetworkErrorOrSuspicious).ToList(),
            _ => entries.ToList()
        };
    }

    private static bool IsNetworkErrorOrSuspicious(WebViewNetworkLogEntry entry)
    {
        if (!int.TryParse(entry.StatusDisplay, NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
        {
            return true;
        }

        return status >= 400;
    }

    private static string FormatNetworkAnalysisDetailMode(NetworkAnalysisDetailMode detailMode)
    {
        return detailMode switch
        {
            NetworkAnalysisDetailMode.Summary => "목록 요약만",
            NetworkAnalysisDetailMode.ErrorsDetailed => "실패/의심 요청 상세 포함",
            _ => "전체 요청 상세 포함"
        };
    }

    private static void AppendNetworkAnalysisEntry(System.Text.StringBuilder builder, WebViewNetworkLogEntry entry)
    {
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"### #{entry.IndexDisplay} {entry.MethodDisplay} {entry.StatusDisplay} {entry.HostDisplay}{entry.PathDisplay}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Summary: {entry.Summary}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Time: {entry.TimeDisplay}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Size: {entry.SizeDisplay}");
        builder.AppendLine();
        builder.AppendLine("Request:");
        builder.AppendLine(LimitNetworkPromptText(entry.RequestDetails, 2500));
        builder.AppendLine();
        builder.AppendLine("Response:");
        builder.AppendLine(LimitNetworkPromptText(entry.ResponseDetails, 2500));
    }

    private static string LimitNetworkPromptText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "-";
        }

        var normalized = text.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + Environment.NewLine + "[이 항목 내용 일부 생략]";
    }

    private void OnWebViewNetworkListFocused(object? sender, FocusEventArgs e)
    {
        UpdateNetworkPanelScrollbars(NetworkScrollTarget.LogList);
    }

    private void OnWebViewNetworkListScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        _isNetworkListPinnedToBottom = NetworkCaptureListSupport.IsPinnedToBottom(
            e.LastVisibleItemIndex,
            _filteredWebViewNetworkEntries.Count);
    }

    private void OnWebViewNetworkListUnfocused(object? sender, FocusEventArgs e)
    {
        UpdateNetworkPanelScrollbars(NetworkScrollTarget.None);
    }

    private async void OnWebViewNetworkItemSecondaryTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject bindable ||
            bindable.BindingContext is not WebViewNetworkLogEntry entry)
        {
            return;
        }

        if (WebViewNetworkListView is not null)
        {
            _selectedWebViewNetworkEntry = entry;
            WebViewNetworkListView.SelectedItem = entry;
        }

        ApplyNetworkDetails(entry);

        var action = await DisplayActionSheetAsync(
            "Network Copy",
            "Cancel",
            null,
            "Copy URL",
            "Copy Session");

        if (string.Equals(action, "Copy URL", StringComparison.Ordinal))
        {
            await CopyNetworkUrlAsync(entry);
            return;
        }

        if (string.Equals(action, "Copy Session", StringComparison.Ordinal))
        {
            await CopyNetworkSessionAsync(entry);
        }
    }

    private async Task CopyNetworkUrlAsync(WebViewNetworkLogEntry entry)
    {
        var url = ExtractSection(entry.RequestDetails, "URL:", "TYPE:");
        if (string.IsNullOrWhiteSpace(url) || url == "-")
        {
            _naverTalkBrowser.SetStatus("선택한 세션에 복사할 URL이 없습니다.");
            return;
        }

        try
        {
            await Clipboard.Default.SetTextAsync(url);
            _naverTalkBrowser.SetStatus("세션 URL을 클립보드에 복사했습니다.");
        }
        catch (Exception ex)
        {
            _naverTalkBrowser.SetStatus($"세션 URL 복사 실패: {ex.Message}");
        }
    }

    private async Task CopyNetworkSessionAsync(WebViewNetworkLogEntry entry)
    {
        var url = ExtractSection(entry.RequestDetails, "URL:", "TYPE:");
        if (string.IsNullOrWhiteSpace(url) || url == "-")
        {
            _naverTalkBrowser.SetStatus("선택한 세션에 복사할 요청 URL이 없습니다.");
            return;
        }

        var raw = WebViewNetworkSessionExportSupport.BuildRawSessionText(
            entry.MethodDisplay,
            url,
            entry.StatusDisplay,
            "-",
            entry.RequestDetails,
            entry.ResponseDetails);

        try
        {
            await Clipboard.Default.SetTextAsync(raw);
            _naverTalkBrowser.SetStatus("세션 전체를 클립보드에 복사했습니다.");
        }
        catch (Exception ex)
        {
            _naverTalkBrowser.SetStatus($"세션 복사 실패: {ex.Message}");
        }
    }

    private void OnRequestHeadersPanelTapped(object? sender, TappedEventArgs e)
    {
        UpdateNetworkPanelScrollbars(NetworkScrollTarget.RequestHeaders);
    }

    private void OnRequestBodyPanelTapped(object? sender, TappedEventArgs e)
    {
        UpdateNetworkPanelScrollbars(NetworkScrollTarget.RequestBody);
    }

    private void OnResponseHeadersPanelTapped(object? sender, TappedEventArgs e)
    {
        UpdateNetworkPanelScrollbars(NetworkScrollTarget.ResponseHeaders);
    }

    private void OnResponseBodyPanelTapped(object? sender, TappedEventArgs e)
    {
        UpdateNetworkPanelScrollbars(NetworkScrollTarget.ResponseBody);
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
        if (SelectedNetworkSummaryLabel is not null)
        {
            SelectedNetworkSummaryLabel.Text = entry.Summary;
        }

        if (SelectedNetworkMethodLabel is not null)
        {
            SelectedNetworkMethodLabel.Text = entry.MethodDisplay;
        }

        if (SelectedNetworkStatusLabel is not null)
        {
            SelectedNetworkStatusLabel.Text = entry.StatusDisplay;
            SelectedNetworkStatusLabel.TextColor = entry.StatusColor;
        }

        if (SelectedNetworkSizeLabel is not null)
        {
            SelectedNetworkSizeLabel.Text = entry.SizeDisplay;
        }

        if (SelectedNetworkTimeLabel is not null)
        {
            SelectedNetworkTimeLabel.Text = entry.TimeDisplay;
        }
    }

    private void UpdateNetworkCount()
    {
        if (NetworkCountLabel is not null)
        {
            NetworkCountLabel.Text = _networkCaptureFilter == NetworkCaptureFilterKind.All &&
                                     string.IsNullOrWhiteSpace(_networkSearchText)
                ? $"{_webViewNetworkEntries.Count} items"
                : $"{_filteredWebViewNetworkEntries.Count} / {_webViewNetworkEntries.Count} items";
        }
    }

    private void ScrollWebViewNetworkListToBottom()
    {
        if (WebViewNetworkListView is null || _filteredWebViewNetworkEntries.Count == 0)
        {
            return;
        }

        _isNetworkListPinnedToBottom = true;
        var lastEntry = _filteredWebViewNetworkEntries[^1];
        Dispatcher.Dispatch(() => WebViewNetworkListView.ScrollTo(lastEntry, position: ScrollToPosition.End, animate: false));
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

    private static WebViewNetworkLogEntry BuildWebViewNetworkEntry(
        int sequence,
        string summary,
        string requestDetails,
        string responseDetails,
        string method,
        string url,
        int? status,
        double? durationMs,
        double encodedDataLength,
        string? resourceType,
        string? mimeType)
    {
        var normalizedSummary = string.IsNullOrWhiteSpace(summary) ? "[network]" : summary.Trim();
        var normalizedRequest = string.IsNullOrWhiteSpace(requestDetails) ? "-" : requestDetails.Trim();
        var normalizedResponse = string.IsNullOrWhiteSpace(responseDetails) ? "-" : responseDetails.Trim();
        var statusDisplay = status.HasValue
            ? status.Value.ToString(CultureInfo.InvariantCulture)
            : "-";
        var statusColor = ResolveStatusColor(status);
        var methodDisplay = string.IsNullOrWhiteSpace(method) ? "-" : method.Trim().ToUpperInvariant();
        var timeDisplay = durationMs.HasValue ? $"{durationMs.Value:0.0}ms" : "-";
        var sizeDisplay = FormatNetworkSize(encodedDataLength);
        var hostDisplay = "-";
        var pathDisplay = normalizedSummary;
        var fullUrlDisplay = "-";
        var schemeDisplay = "-";

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            hostDisplay = uri.Host;
            pathDisplay = string.IsNullOrWhiteSpace(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            fullUrlDisplay = uri.AbsoluteUri;
            schemeDisplay = string.IsNullOrWhiteSpace(uri.Scheme) ? "-" : uri.Scheme;
        }
        else if (!string.IsNullOrWhiteSpace(url))
        {
            pathDisplay = url.Trim();
            fullUrlDisplay = url.Trim();
        }

        var resourceTypeDisplay = string.IsNullOrWhiteSpace(resourceType) ? "-" : resourceType.Trim();
        var mimeTypeDisplay = string.IsNullOrWhiteSpace(mimeType) ? "-" : mimeType.Trim();
        var filterKind = NetworkCaptureFilterSupport.Classify(
            schemeDisplay,
            resourceTypeDisplay,
            mimeTypeDisplay,
            pathDisplay,
            statusDisplay);

        return new WebViewNetworkLogEntry(
            sequence,
            normalizedSummary,
            normalizedRequest,
            normalizedResponse,
            statusDisplay,
            statusColor,
            methodDisplay,
            hostDisplay,
            pathDisplay,
            fullUrlDisplay,
            schemeDisplay,
            resourceTypeDisplay,
            mimeTypeDisplay,
            filterKind,
            sizeDisplay,
            timeDisplay);
    }

    private static Color ResolveStatusColor(int? status)
    {
        if (!status.HasValue)
        {
            return Color.FromArgb("#60717A");
        }

        return status.Value switch
        {
            >= 200 and < 300 => Color.FromArgb("#2D8A63"),
            >= 300 and < 400 => Color.FromArgb("#1C6780"),
            >= 400 and < 500 => Color.FromArgb("#C07A2F"),
            >= 500 => Color.FromArgb("#B35A4A"),
            _ => Color.FromArgb("#60717A")
        };
    }

    private static string FormatNetworkSize(double encodedDataLength)
    {
        if (encodedDataLength <= 0)
        {
            return "-";
        }

        if (encodedDataLength >= 1024 * 1024)
        {
            return $"{encodedDataLength / (1024 * 1024):0.0} MB";
        }

        if (encodedDataLength >= 1024)
        {
            return $"{encodedDataLength / 1024:0.0} KB";
        }

        return $"{encodedDataLength:0} B";
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

    private void UpdateNetworkPanelScrollbars(NetworkScrollTarget target)
    {
        if (WebViewNetworkListView is not null)
        {
            WebViewNetworkListView.VerticalScrollBarVisibility =
                target == NetworkScrollTarget.LogList ? ScrollBarVisibility.Always : ScrollBarVisibility.Default;
        }

        if (WebViewNetworkRequestHeadersScrollView is not null)
        {
            WebViewNetworkRequestHeadersScrollView.VerticalScrollBarVisibility =
                target == NetworkScrollTarget.RequestHeaders ? ScrollBarVisibility.Always : ScrollBarVisibility.Default;
        }

        if (WebViewNetworkRequestBodyScrollView is not null)
        {
            WebViewNetworkRequestBodyScrollView.VerticalScrollBarVisibility =
                target == NetworkScrollTarget.RequestBody ? ScrollBarVisibility.Always : ScrollBarVisibility.Default;
        }

        if (WebViewNetworkResponseHeadersScrollView is not null)
        {
            WebViewNetworkResponseHeadersScrollView.VerticalScrollBarVisibility =
                target == NetworkScrollTarget.ResponseHeaders ? ScrollBarVisibility.Always : ScrollBarVisibility.Default;
        }

        if (WebViewNetworkResponseBodyScrollView is not null)
        {
            WebViewNetworkResponseBodyScrollView.VerticalScrollBarVisibility =
                target == NetworkScrollTarget.ResponseBody ? ScrollBarVisibility.Always : ScrollBarVisibility.Default;
        }
    }

    private static InspectorSelectionSnapshot? ParseInspectorSelection(string details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(details)?.AsObject();
            if (node is null)
            {
                return null;
            }

            var rect = node["rect"]?.AsObject();
            return new InspectorSelectionSnapshot(
                node["selector"]?.GetValue<string>() ?? "-",
                node["tag"]?.GetValue<string>() ?? "-",
                node["id"]?.GetValue<string>() ?? string.Empty,
                node["name"]?.GetValue<string>() ?? string.Empty,
                node["className"]?.GetValue<string>() ?? string.Empty,
                node["role"]?.GetValue<string>() ?? string.Empty,
                node["inputType"]?.GetValue<string>() ?? string.Empty,
                node["ariaLabel"]?.GetValue<string>() ?? string.Empty,
                node["placeholder"]?.GetValue<string>() ?? string.Empty,
                node["valuePreview"]?.GetValue<string>() ?? string.Empty,
                node["textPreview"]?.GetValue<string>() ?? string.Empty,
                node["href"]?.GetValue<string>() ?? string.Empty,
                node["domPath"]?.GetValue<string>() ?? string.Empty,
                rect?["left"]?.GetValue<double>() ?? 0,
                rect?["top"]?.GetValue<double>() ?? 0,
                rect?["width"]?.GetValue<double>() ?? 0,
                rect?["height"]?.GetValue<double>() ?? 0,
                details);
        }
        catch
        {
            return null;
        }
    }

    private void OpenInspectorDetailsModal()
    {
        if (InspectDetailsModal is null)
        {
            return;
        }

        var snapshot = _lastInspectorSelection;
        InspectModalSelectorLabel.Text = snapshot?.Selector ?? "선택된 요소 없음";
        InspectModalTagLabel.Text = snapshot is null
            ? "-"
            : string.IsNullOrWhiteSpace(snapshot.InputType)
                ? snapshot.Tag
                : $"{snapshot.Tag} / {snapshot.InputType}";
        InspectModalRectLabel.Text = snapshot is null
            ? "-"
            : $"{snapshot.Width:0}x{snapshot.Height:0} @ ({snapshot.Left:0}, {snapshot.Top:0})";
        InspectModalIdentityLabel.Text = snapshot is null
            ? "-"
            : string.Join(" | ", new[]
            {
                string.IsNullOrWhiteSpace(snapshot.ElementId) ? null : $"id={snapshot.ElementId}",
                string.IsNullOrWhiteSpace(snapshot.Name) ? null : $"name={snapshot.Name}",
                string.IsNullOrWhiteSpace(snapshot.ClassName) ? null : $"class={snapshot.ClassName}"
            }.Where(static v => !string.IsNullOrWhiteSpace(v)));
        InspectModalMetaLabel.Text = snapshot is null
            ? "-"
            : string.Join(" | ", new[]
            {
                string.IsNullOrWhiteSpace(snapshot.Role) ? null : $"role={snapshot.Role}",
                string.IsNullOrWhiteSpace(snapshot.Href) ? null : snapshot.Href
            }.Where(static v => !string.IsNullOrWhiteSpace(v)));
        InspectModalPreviewLabel.Text = snapshot is null
            ? "-"
            : string.Join(Environment.NewLine, new[]
            {
                string.IsNullOrWhiteSpace(snapshot.TextPreview) ? null : $"text: {snapshot.TextPreview}",
                string.IsNullOrWhiteSpace(snapshot.AriaLabel) ? null : $"aria: {snapshot.AriaLabel}",
                string.IsNullOrWhiteSpace(snapshot.Placeholder) ? null : $"placeholder: {snapshot.Placeholder}",
                string.IsNullOrWhiteSpace(snapshot.ValuePreview) ? null : $"value: {snapshot.ValuePreview}"
            }.Where(static v => !string.IsNullOrWhiteSpace(v)));
        InspectModalDomPathLabel.Text = snapshot?.DomPath ?? "-";
        InspectDetailsModal.IsVisible = true;
    }

    private void OnCloseInspectorModalClicked(object? sender, EventArgs e)
    {
        if (InspectDetailsModal is not null)
        {
            InspectDetailsModal.IsVisible = false;
        }
    }
}

