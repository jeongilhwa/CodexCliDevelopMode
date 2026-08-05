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
    private void SetupSplitterGesture()
    {
        ResizeHandle.GestureRecognizers.Clear();
        RightResizeHandle.GestureRecognizers.Clear();

        var pointerGesture = new PointerGestureRecognizer();
        pointerGesture.PointerEntered += OnSplitterPointerEntered;
        pointerGesture.PointerExited += OnSplitterPointerExited;
        pointerGesture.PointerPressed += OnSplitterPointerPressed;
        pointerGesture.PointerMoved += OnSplitterPointerMoved;
        pointerGesture.PointerReleased += OnSplitterPointerReleased;
        ResizeHandle.GestureRecognizers.Add(pointerGesture);

        var rightPointerGesture = new PointerGestureRecognizer();
        rightPointerGesture.PointerEntered += OnRightSplitterPointerEntered;
        rightPointerGesture.PointerExited += OnRightSplitterPointerExited;
        rightPointerGesture.PointerPressed += OnRightSplitterPointerPressed;
        rightPointerGesture.PointerMoved += OnRightSplitterPointerMoved;
        rightPointerGesture.PointerReleased += OnRightSplitterPointerReleased;
        RightResizeHandle.GestureRecognizers.Add(rightPointerGesture);

        UpdateSplitterVisual(isDragging: false, isHovering: false);
        UpdateRightSplitterVisual(isDragging: false, isHovering: false);
    }

    private void OnSplitterPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_isSplitterDragging)
        {
            return;
        }

        UpdateSplitterVisual(isDragging: false, isHovering: true);
    }

    private void OnSplitterPointerExited(object? sender, PointerEventArgs e)
    {
        if (_isSplitterDragging)
        {
            return;
        }

        UpdateSplitterVisual(isDragging: false, isHovering: false);
    }

    private void OnSplitterPointerPressed(object? sender, PointerEventArgs e)
    {
        var pointerX = GetSplitterPointerX(e);
        var paneWidths = TryGetCurrentPaneWidths();
        if (pointerX is null || paneWidths is null)
        {
            return;
        }

        _isSplitterDragging = true;
        _splitterDragStartPointerX = pointerX.Value;
        _splitterDragStartWebViewWidth = paneWidths.Value.WebViewWidth;
        _splitterDragStartChatWidth = paneWidths.Value.ChatWidth;
        UpdateSplitterVisual(isDragging: true, isHovering: true);
        TryCaptureSplitterPointer(e, isRightSplitter: false);                                           
    }

    private void OnSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isSplitterDragging)
        {
            return;
        }

        var pointerX = GetSplitterPointerX(e);
        if (pointerX is null)
        {
            return;
        }

        var delta = pointerX.Value - _splitterDragStartPointerX;
        if (Math.Abs(delta) <= double.Epsilon)
        {
            return;
        }

        ResizePanelsByDelta(delta);
    }

    private void OnSplitterPointerReleased(object? sender, PointerEventArgs e)
    {
        if (!_isSplitterDragging)
        {
            return;
        }

        _isSplitterDragging = false;
        _splitterDragStartPointerX = double.NaN;
        _splitterDragStartWebViewWidth = 0;
        _splitterDragStartChatWidth = 0;
        ReleaseCapturedSplitterPointer(isRightSplitter: false);
        UpdateSplitterVisual(isDragging: false, isHovering: false);
    }

    private void OnRightSplitterPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_isRightSplitterDragging)
        {
            return;
        }

        UpdateRightSplitterVisual(isDragging: false, isHovering: true);
    }

    private void OnRightSplitterPointerExited(object? sender, PointerEventArgs e)
    {
        if (_isRightSplitterDragging)
        {
            return;
        }

        UpdateRightSplitterVisual(isDragging: false, isHovering: false);
    }

    private void OnRightSplitterPointerPressed(object? sender, PointerEventArgs e)
    {
        var pointerX = GetRightSplitterPointerX(e);
        var paneWidths = TryGetCurrentRightPaneWidths();
        if (pointerX is null || paneWidths is null)
        {
            return;
        }

        _isRightSplitterDragging = true;
        _rightSplitterDragStartPointerX = pointerX.Value;
        _rightSplitterDragStartNetworkWidth = paneWidths.Value.NetworkWidth;
        _rightSplitterDragStartCodexWidth = paneWidths.Value.CodexWidth;
        UpdateRightSplitterVisual(isDragging: true, isHovering: true);
        TryCaptureSplitterPointer(e, isRightSplitter: true);
    }

    private void OnRightSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isRightSplitterDragging)
        {
            return;
        }

        var pointerX = GetRightSplitterPointerX(e);
        if (pointerX is null)
        {
            return;
        }

        var delta = pointerX.Value - _rightSplitterDragStartPointerX;
        if (Math.Abs(delta) <= double.Epsilon)
        {
            return;
        }

        ResizeRightPanelsByDelta(delta);
    }

    private void OnRightSplitterPointerReleased(object? sender, PointerEventArgs e)
    {
        if (!_isRightSplitterDragging)
        {
            return;
        }

        _isRightSplitterDragging = false;
        _rightSplitterDragStartPointerX = double.NaN;
        _rightSplitterDragStartNetworkWidth = 0;
        _rightSplitterDragStartCodexWidth = 0;
        ReleaseCapturedSplitterPointer(isRightSplitter: true);
        UpdateRightSplitterVisual(isDragging: false, isHovering: false);
    }

    private double? GetSplitterPointerX(PointerEventArgs e)
    {
        var position = e.GetPosition(MainLayoutGrid);
        if (position is null)
        {
            return null;
        }

        return position.Value.X;
    }

    private double? GetRightSplitterPointerX(PointerEventArgs e)
    {
        var position = e.GetPosition(ChatSplitGrid);
        if (position is null)
        {
            return null;
        }

        return position.Value.X;
    }

    private void UpdateSplitterVisual(bool isDragging, bool isHovering)
    {
        ResizeHandleGrip.BackgroundColor = isDragging
            ? SplitterActiveColor
            : isHovering
                ? SplitterHoverColor
                : SplitterIdleColor;
    }

    private void UpdateRightSplitterVisual(bool isDragging, bool isHovering)
    {
        RightResizeHandleGrip.BackgroundColor = isDragging
            ? SplitterActiveColor
            : isHovering
                ? SplitterHoverColor
                : Color.FromArgb("#D0DEE5");
    }

    private void TryCaptureSplitterPointer(PointerEventArgs e, bool isRightSplitter)
    {
#if WINDOWS
        var platformArgs = e.PlatformArgs;
        if (platformArgs?.Sender is not UI.FrameworkElement sender ||
            platformArgs.PointerRoutedEventArgs is null)
        {
            return;
        }

        var pointer = platformArgs.PointerRoutedEventArgs.Pointer;
        if (pointer is null)
        {
            return;
        }

        if (sender.CapturePointer(pointer))
        {
            if (isRightSplitter)
            {
                _capturedRightSplitterPointer = pointer;
            }
            else
            {
                _capturedSplitterPointer = pointer;
            }
        }
#endif
    }

    private void ReleaseCapturedSplitterPointer(bool isRightSplitter)
    {
#if WINDOWS
        if (isRightSplitter)
        {
            if (_capturedRightSplitterPointer is null)
            {
                return;
            }

            if (RightResizeHandle.Handler?.PlatformView is UI.FrameworkElement rightSender)
            {
                rightSender.ReleasePointerCapture(_capturedRightSplitterPointer);
            }

            _capturedRightSplitterPointer = null;
            return;
        }

        if (_capturedSplitterPointer is null)
        {
            return;
        }

        if (ResizeHandle.Handler?.PlatformView is UI.FrameworkElement leftSender)
        {
            leftSender.ReleasePointerCapture(_capturedSplitterPointer);
        }

        _capturedSplitterPointer = null;
#endif
    }

    private void ResizePanelsByDelta(double delta)
    {
        var usableWidth = _splitterDragStartWebViewWidth + _splitterDragStartChatWidth;
        if (usableWidth <= 0)
        {
            return;
        }

        var effectiveMinPaneWidth = Math.Min(MinPaneWidth, usableWidth / 2.0);
        if (effectiveMinPaneWidth <= 0)
        {
            return;
        }

        var newWebViewWidth = Math.Clamp(
            _splitterDragStartWebViewWidth + delta,
            effectiveMinPaneWidth,
            usableWidth - effectiveMinPaneWidth);
        var newChatWidth = usableWidth - newWebViewWidth;

        ApplyPaneWidths(newWebViewWidth, newChatWidth);
    }

    private (double WebViewWidth, double ChatWidth)? TryGetCurrentPaneWidths()
    {
        if (BrowserPane.Width > 0 && ChatPane.Width > 0)
        {
            return (BrowserPane.Width, ChatPane.Width);
        }

        var usableWidth = MainLayoutGrid.Width - SplitterColumnWidth;
        if (usableWidth <= 0)
        {
            return null;
        }

        var webViewWidth = ResolveColumnWidth(MainLayoutGrid.ColumnDefinitions[0].Width, MainLayoutGrid.ColumnDefinitions[2].Width, usableWidth);
        if (webViewWidth <= 0 || webViewWidth >= usableWidth)
        {
            return null;
        }

        return (webViewWidth, usableWidth - webViewWidth);
    }

    private void ResizeRightPanelsByDelta(double delta)
    {
        var usableWidth = _rightSplitterDragStartNetworkWidth + _rightSplitterDragStartCodexWidth;
        if (usableWidth <= 0)
        {
            return;
        }

        var effectiveMinPaneWidth = Math.Min(MinRightPaneWidth, usableWidth / 2.0);
        if (effectiveMinPaneWidth <= 0)
        {
            return;
        }

        var newNetworkWidth = Math.Clamp(
            _rightSplitterDragStartNetworkWidth + delta,
            effectiveMinPaneWidth,
            usableWidth - effectiveMinPaneWidth);
        var newCodexWidth = usableWidth - newNetworkWidth;

        ApplyRightPaneWidths(newNetworkWidth, newCodexWidth);
    }

    private (double NetworkWidth, double CodexWidth)? TryGetCurrentRightPaneWidths()
    {
        if (NetworkPane.Width > 0 && CodexPane.Width > 0)
        {
            return (NetworkPane.Width, CodexPane.Width);
        }

        var usableWidth = ChatSplitGrid.Width - RightSplitterColumnWidth;
        if (usableWidth <= 0)
        {
            return null;
        }

        var networkWidth = ResolveColumnWidth(ChatSplitGrid.ColumnDefinitions[0].Width, ChatSplitGrid.ColumnDefinitions[2].Width, usableWidth);
        if (networkWidth <= 0 || networkWidth >= usableWidth)
        {
            return null;
        }

        return (networkWidth, usableWidth - networkWidth);
    }

    private static double ResolveColumnWidth(GridLength targetWidth, GridLength otherWidth, double usableWidth)
    {
        if (targetWidth.IsAbsolute)
        {
            return targetWidth.Value;
        }

        if (otherWidth.IsAbsolute)
        {
            return usableWidth - otherWidth.Value;
        }

        if (!targetWidth.IsStar || !otherWidth.IsStar)
        {
            return 0;
        }

        var totalStars = targetWidth.Value + otherWidth.Value;
        if (totalStars <= 0)
        {
            return 0;
        }

        return usableWidth * (targetWidth.Value / totalStars);
    }

    private void ApplyPaneWidths(double webViewWidth, double chatWidth)
    {
        MainLayoutGrid.ColumnDefinitions[0].Width = new GridLength(webViewWidth, GridUnitType.Absolute);
        MainLayoutGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);

        MainLayoutGrid.InvalidateMeasure();
        BrowserPane.InvalidateMeasure();
        ChatPane.InvalidateMeasure();
        BrowserWebView.InvalidateMeasure();

#if WINDOWS
        if (MainLayoutGrid.Handler?.PlatformView is UI.FrameworkElement gridElement)
        {
            gridElement.InvalidateMeasure();
            gridElement.UpdateLayout();
        }

        if (BrowserPane.Handler?.PlatformView is UI.FrameworkElement browserPaneElement)
        {
            browserPaneElement.InvalidateMeasure();
            browserPaneElement.UpdateLayout();
        }

        if (ChatPane.Handler?.PlatformView is UI.FrameworkElement chatPaneElement)
        {
            chatPaneElement.InvalidateMeasure();
            chatPaneElement.UpdateLayout();
        }

        if (BrowserWebView.Handler?.PlatformView is UI.FrameworkElement webViewElement)
        {
            webViewElement.InvalidateMeasure();
            webViewElement.UpdateLayout();
        }

        ApplyAdaptiveWebViewZoom();
#endif
    }

    private void ApplyRightPaneWidths(double networkWidth, double codexWidth)
    {
        ChatSplitGrid.ColumnDefinitions[0].Width = new GridLength(networkWidth, GridUnitType.Absolute);
        ChatSplitGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);

        ChatSplitGrid.InvalidateMeasure();
        NetworkPane.InvalidateMeasure();
        CodexPane.InvalidateMeasure();

#if WINDOWS
        if (ChatSplitGrid.Handler?.PlatformView is UI.FrameworkElement splitGridElement)
        {
            splitGridElement.InvalidateMeasure();
            splitGridElement.UpdateLayout();
        }

        if (NetworkPane.Handler?.PlatformView is UI.FrameworkElement networkPaneElement)
        {
            networkPaneElement.InvalidateMeasure();
            networkPaneElement.UpdateLayout();
        }

        if (CodexPane.Handler?.PlatformView is UI.FrameworkElement codexPaneElement)
        {
            codexPaneElement.InvalidateMeasure();
            codexPaneElement.UpdateLayout();
        }
#endif
    }

    private async Task RefreshWebViewLayoutMetricsAsync()
    {
#if WINDOWS
        await Task.Delay(150);

        var measuredWidth = await TryMeasureWebViewContentWidthAsync();
        if (measuredWidth is > 0)
        {
            _measuredWebViewContentWidth = measuredWidth.Value;
        }

        ApplyAdaptiveWebViewZoom();
#endif
    }

#if WINDOWS
    private async Task<double?> TryMeasureWebViewContentWidthAsync()
    {
        const string script =
            """
            (() => {
                const root = document.documentElement;
                const body = document.body;
                if (!root) {
                    return "";
                }

                const previousZoom = root.style.zoom;
                root.style.zoom = "1";

                const widths = [
                    root.clientWidth,
                    root.scrollWidth,
                    root.offsetWidth,
                    body ? body.clientWidth : 0,
                    body ? body.scrollWidth : 0,
                    body ? body.offsetWidth : 0
                ].filter(value => Number.isFinite(value) && value > 0);

                root.style.zoom = previousZoom;
                return widths.length ? Math.max(...widths).toString() : "";
            })();
            """;

        try
        {
            var result = await RunBrowserScriptAsync(script);
            return TryParseJavaScriptNumber(result, out var width) ? width : null;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyAdaptiveWebViewZoom()
    {
        if (BrowserWebView.Handler?.PlatformView is not UI.Controls.WebView2 platformWebView)
        {
            return;
        }

        var availableWidth = BrowserWebView.Width > 0
            ? BrowserWebView.Width
            : BrowserPane.Width;

        if (availableWidth <= 0)
        {
            return;
        }

        var referenceWidth = _measuredWebViewContentWidth > 0
            ? _measuredWebViewContentWidth
            : Math.Max(availableWidth, FallbackWebViewContentWidth);

        var zoomFactor = Math.Clamp(availableWidth / referenceWidth, MinWebViewZoomFactor, 1.0);
        if (Math.Abs(zoomFactor - _lastAppliedWebViewZoomFactor) < 0.01)
        {
            return;
        }

        var zoomProperty = platformWebView.GetType().GetProperty("ZoomFactor");
        if (zoomProperty?.CanWrite != true)
        {
            return;
        }

        zoomProperty.SetValue(platformWebView, zoomFactor);
        _lastAppliedWebViewZoomFactor = zoomFactor;
    }

    private static bool TryParseJavaScriptNumber(string? value, out double number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().Trim('"');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
#endif
}

