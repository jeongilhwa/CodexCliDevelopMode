namespace InterceptKit.App;

using InterceptKit.WebView;
using InterceptKit.WebView.Windows;
#if WINDOWS
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;
#endif

public partial class MainPage : ContentPage
{
    private readonly WebView2InterceptController _interceptController;
    private readonly WebViewInterceptRuntimeState _runtimeState;
    private bool _initialNavigationRequested;

    public MainPage(
        WebView2InterceptController interceptController,
        WebViewInterceptRuntimeState runtimeState)
    {
        InitializeComponent();
        _interceptController = interceptController;
        _runtimeState = runtimeState;
        _runtimeState.Changed += OnRuntimeStateChanged;
        ApplyRuntimeState();
    }

    private async void OnBrowserWebViewHandlerChanged(object? sender, EventArgs e)
    {
#if WINDOWS
        await ConfigureBrowserForWindowsAsync();
#endif
    }

#if WINDOWS
    private async Task ConfigureBrowserForWindowsAsync()
    {
        try
        {
            if (BrowserWebView.Handler is not WebViewHandler handler ||
                handler.PlatformView is not WebView2 webView2)
            {
                return;
            }

            webView2.CoreWebView2Initialized -= OnCoreWebView2Initialized;
            webView2.CoreWebView2Initialized += OnCoreWebView2Initialized;

            if (webView2.CoreWebView2 is not null)
            {
                await _interceptController.AttachAsync(webView2.CoreWebView2);
                _runtimeState.MarkAttached();
                EnsureInitialNavigation();
                return;
            }

            await webView2.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            _runtimeState.MarkError($"WebView2 attach failed: {ex.Message}");
        }
    }

    private void OnCoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        if (sender.CoreWebView2 is null || args.Exception is not null)
        {
            if (args.Exception is not null)
            {
                _runtimeState.MarkError($"CoreWebView2 init failed: {args.Exception.Message}");
            }

            return;
        }

        _ = AttachAndNavigateAsync(sender.CoreWebView2);
    }
#endif

    private void OnNavigateClicked(object? sender, EventArgs e)
    {
        NavigateToRequestedUrl();
    }

    private void OnRefreshClicked(object? sender, EventArgs e)
    {
        BrowserWebView.Reload();
    }

    private void NavigateToRequestedUrl()
    {
        var raw = BrowserAddressEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var target = raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? raw
            : $"https://{raw}";

        _initialNavigationRequested = true;
        BrowserWebView.Source = target;
    }

#if WINDOWS
    private async Task AttachAndNavigateAsync(Microsoft.Web.WebView2.Core.CoreWebView2 coreWebView2)
    {
        try
        {
            await _interceptController.AttachAsync(coreWebView2);
            _runtimeState.MarkAttached();
            EnsureInitialNavigation();
        }
        catch (Exception ex)
        {
            _runtimeState.MarkError($"Fetch attach failed: {ex.Message}");
        }
    }
#endif

    private void EnsureInitialNavigation()
    {
        if (_initialNavigationRequested)
        {
            return;
        }

        NavigateToRequestedUrl();
    }

    private void OnRuntimeStateChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(ApplyRuntimeState);
    }

    private void ApplyRuntimeState()
    {
        RuntimeStatusLabel.Text = $"Status: {_runtimeState.StatusText}";
        RuntimeDetailLabel.Text = $"Paused: {_runtimeState.PausedRequestCount} | Last URL: {_runtimeState.LastPausedRequestUrl ?? "none"}";
    }
}
