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

        var pointerGesture = new PointerGestureRecognizer();
        pointerGesture.PointerEntered += OnSplitterPointerEntered;
        pointerGesture.PointerExited += OnSplitterPointerExited;
        pointerGesture.PointerPressed += OnSplitterPointerPressed;
        pointerGesture.PointerMoved += OnSplitterPointerMoved;
        pointerGesture.PointerReleased += OnSplitterPointerReleased;
        ResizeHandle.GestureRecognizers.Add(pointerGesture);

        UpdateSplitterVisual(isDragging: false, isHovering: false);
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
        TryCaptureSplitterPointer(e);
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
        ReleaseCapturedSplitterPointer();
        UpdateSplitterVisual(isDragging: false, isHovering: false);
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

    private void UpdateSplitterVisual(bool isDragging, bool isHovering)
    {
        ResizeHandleGrip.BackgroundColor = isDragging
            ? SplitterActiveColor
            : isHovering
                ? SplitterHoverColor
                : SplitterIdleColor;
    }

    private void TryCaptureSplitterPointer(PointerEventArgs e)
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
            _capturedSplitterPointer = pointer;
        }
#endif
    }

    private void ReleaseCapturedSplitterPointer()
    {
#if WINDOWS
        if (_capturedSplitterPointer is null)
        {
            return;
        }

        if (ResizeHandle.Handler?.PlatformView is UI.FrameworkElement sender)
        {
            sender.ReleasePointerCapture(_capturedSplitterPointer);
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
        NaverTalkWebView.InvalidateMeasure();

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

        if (NaverTalkWebView.Handler?.PlatformView is UI.FrameworkElement webViewElement)
        {
            webViewElement.InvalidateMeasure();
            webViewElement.UpdateLayout();
        }

        ApplyAdaptiveWebViewZoom();
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
        if (NaverTalkWebView.Handler?.PlatformView is not UI.Controls.WebView2 platformWebView)
        {
            return;
        }

        var availableWidth = NaverTalkWebView.Width > 0
            ? NaverTalkWebView.Width
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

