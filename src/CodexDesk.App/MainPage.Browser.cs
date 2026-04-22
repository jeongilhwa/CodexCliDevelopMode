namespace CodexDesk;

using CodexDesk.Services;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
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
#if WINDOWS
    private void OnNaverTalkWebViewHandlerChanged(object? sender, EventArgs e)
    {
        ConfigureWebView2ForWindows();
    }

    private void ConfigureWebView2ForWindows()
    {
        try
        {
            var handler = NaverTalkWebView.Handler as Microsoft.Maui.Handlers.WebViewHandler;
            if (handler?.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 webView2)
            {
                webView2.CoreWebView2Initialized -= OnCoreWebView2Initialized;
                webView2.CoreWebView2Initialized += OnCoreWebView2Initialized;
                if (webView2.CoreWebView2 is not null)
                {
                    AttachWebViewNewWindowHandler(webView2.CoreWebView2);
                    AttachWebViewKeyboardDebugHandlers(webView2.CoreWebView2);
                    _ = EnsureWebViewInspectorScriptRegisteredAsync(webView2.CoreWebView2);
                    _ = ApplyWebViewInspectorModeAsync(_webViewInspectorModeEnabled);
                    _ = ApplyWebViewNetworkCaptureModeAsync(_webViewNetworkCaptureEnabled);
                }
            }
        }
        catch (Exception ex)
        {
            _naverTalkBrowser.SetStatus($"Cookie configuration failed: {ex.Message}");
        }
    }

    private void OnCoreWebView2Initialized(Microsoft.UI.Xaml.Controls.WebView2 sender, Microsoft.UI.Xaml.Controls.CoreWebView2InitializedEventArgs args)
    {
        if (sender.CoreWebView2 is null)
        {
            return;
        }

        _naverTalkBrowser.SetStatus("WebView2 is ready.");
        AttachWebViewNewWindowHandler(sender.CoreWebView2);
        AttachWebViewKeyboardDebugHandlers(sender.CoreWebView2);
        _ = EnsureWebViewInspectorScriptRegisteredAsync(sender.CoreWebView2);
        _ = ApplyWebViewInspectorModeAsync(_webViewInspectorModeEnabled);
        _ = ApplyWebViewNetworkCaptureModeAsync(_webViewNetworkCaptureEnabled);
    }

    private void AttachWebViewNewWindowHandler(CoreWebView2 coreWebView2)
    {
        if (_webViewNewWindowHandlerAttached)
        {
            return;
        }

        _webViewNewWindowHandlerAttached = true;
        coreWebView2.NewWindowRequested -= OnWebViewNewWindowRequested;
        coreWebView2.NewWindowRequested += OnWebViewNewWindowRequested;
    }

    private void OnWebViewNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        try
        {
            var target = string.IsNullOrWhiteSpace(e.Uri) ? _naverTalkBrowser.CurrentUrl : e.Uri;
            e.Handled = true;
            _ = MainThread.InvokeOnMainThreadAsync(async () =>
            {
                await _naverTalkBrowser.NavigateAsync(target);
                _naverTalkBrowser.SetStatus("New window request was opened in the current WebView.", target);
            });
        }
        catch (Exception ex)
        {
            _naverTalkBrowser.SetStatus($"Failed to handle new-window request: {ex.Message}", _naverTalkBrowser.CurrentUrl);
        }
    }

    private void AttachWebViewKeyboardDebugHandlers(CoreWebView2 coreWebView2)
    {
        if (_webViewKeyboardBridgeAttached)
        {
            return;
        }

        _webViewKeyboardBridgeAttached = true;
        coreWebView2.WebMessageReceived -= OnWebViewMessageReceived;
        coreWebView2.WebMessageReceived += OnWebViewMessageReceived;
        _ = coreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            """
            (() => {
                if (!window.chrome?.webview || window.__codexKeyDebugHook) {
                    return;
                }
                window.__codexKeyDebugHook = true;
                window.addEventListener('keydown', (e) => {
                    const key = e.key ?? '';
                    const code = e.code ?? '';
                    const msg = `codex-keydbg:${key}|${code}|${e.ctrlKey}|${e.shiftKey}|${e.altKey}|${e.metaKey}`;
                    try { window.chrome.webview.postMessage(msg); } catch {}
                }, true);
            })();
            """);
    }

    private void OnWebViewMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var message = e.TryGetWebMessageAsString();
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (message.StartsWith(WebViewKeyDebugPrefix, StringComparison.Ordinal))
        {
            var payload = message[WebViewKeyDebugPrefix.Length..];
            var parts = payload.Split('|');
            var key = parts.Length > 0 ? parts[0] : string.Empty;
            var code = parts.Length > 1 ? parts[1] : string.Empty;
            var ctrl = parts.Length > 2 ? parts[2] : "false";
            var shift = parts.Length > 3 ? parts[3] : "false";
            var alt = parts.Length > 4 ? parts[4] : "false";
            var meta = parts.Length > 5 ? parts[5] : "false";
            var keyMessage =
                $"{DateTime.Now:HH:mm:ss} webview key={key}, code={code}, ctrl={ctrl}, shift={shift}, alt={alt}, meta={meta}";

            MainThread.BeginInvokeOnMainThread(() =>
            {
                _codexPromptMacroService.PublishKeyObserved(keyMessage);
            });
            return;
        }

        if (message.StartsWith(WebViewInspectPrefix, StringComparison.Ordinal))
        {
            var payload = message[WebViewInspectPrefix.Length..];
            var separatorIndex = payload.IndexOf('|');
            var action = separatorIndex < 0 ? payload : payload[..separatorIndex];
            var details = separatorIndex < 0 ? string.Empty : payload[(separatorIndex + 1)..];
            if (action is "selected" or "applied")
            {
                _lastInspectorSelection = ParseInspectorSelection(details);
            }

            var status = action switch
            {
                "enabled" => "Focus mode enabled.",
                "disabled" => "Focus mode disabled.",
                "selected" => "Element locked. Press Inspect to view details.",
                "applied" => "Element action applied.",
                "unlocked" => string.Empty,
                "escape" => "Focus selection cleared (Esc).",
                _ => $"Inspector event: {action}"
            };

            if (string.IsNullOrWhiteSpace(status))
            {
                return;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                _naverTalkBrowser.SetStatus(status, _naverTalkBrowser.CurrentUrl);
            });
            return;
        }

    }

    private static string BuildInspectorSelectedStatus(string details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return "Selected element.";
        }

        try
        {
            var node = JsonNode.Parse(details)?.AsObject();
            if (node is null)
            {
                return $"Selected element: {TrimForLog(details, 160)}";
            }

            var selector = node["selector"]?.GetValue<string>() ?? "-";
            var idRaw = node["id"]?.GetValue<string>();
            var nameRaw = node["name"]?.GetValue<string>();
            var classNameRaw = node["className"]?.GetValue<string>();
            var tag = TrimForLog(node["tag"]?.GetValue<string>(), 40);
            var role = TrimForLog(node["role"]?.GetValue<string>(), 40);
            var type = TrimForLog(node["inputType"]?.GetValue<string>(), 40);
            var href = TrimForLog(node["href"]?.GetValue<string>(), 80);
            var ariaLabel = TrimForLog(node["ariaLabel"]?.GetValue<string>(), 60);
            var placeholder = TrimForLog(node["placeholder"]?.GetValue<string>(), 50);
            var valuePreview = TrimForLog(node["valuePreview"]?.GetValue<string>(), 70);
            var rect = node["rect"]?.AsObject();
            var left = rect?["left"]?.GetValue<double>() ?? 0;
            var top = rect?["top"]?.GetValue<double>() ?? 0;
            var width = rect?["width"]?.GetValue<double>() ?? 0;
            var height = rect?["height"]?.GetValue<double>() ?? 0;
            var text = TrimForLog(node["textPreview"]?.GetValue<string>(), 90);
            var identityParts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(idRaw))
            {
                identityParts.Add($"id={TrimForLog(idRaw, 60)}");
            }

            if (!string.IsNullOrWhiteSpace(nameRaw))
            {
                identityParts.Add($"name={TrimForLog(nameRaw, 60)}");
            }

            if (!string.IsNullOrWhiteSpace(classNameRaw))
            {
                identityParts.Add($"class={TrimForLog(classNameRaw, 60)}");
            }

            var metaParts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(tag) && tag != "-")
            {
                metaParts.Add($"tag={tag}");
            }

            if (!string.IsNullOrWhiteSpace(type) && type != "-")
            {
                metaParts.Add($"type={type}");
            }

            if (!string.IsNullOrWhiteSpace(role) && role != "-")
            {
                metaParts.Add($"role={role}");
            }

            if (!string.IsNullOrWhiteSpace(ariaLabel) && ariaLabel != "-")
            {
                metaParts.Add($"aria={ariaLabel}");
            }

            var lines = new System.Collections.Generic.List<string>
            {
                $"선택 요소: {selector}",
                $"위치/크기: {width:0}x{height:0} @ ({left:0},{top:0})",
                $"텍스트: {text}"
            };

            if (identityParts.Count > 0)
            {
                lines.Add($"식별자: {string.Join(", ", identityParts)}");
            }

            if (!string.IsNullOrWhiteSpace(href) && href != "-")
            {
                lines.Add($"링크: {href}");
            }

            if (!string.IsNullOrWhiteSpace(placeholder) && placeholder != "-")
            {
                lines.Add($"placeholder: {placeholder}");
            }

            if (!string.IsNullOrWhiteSpace(valuePreview) && valuePreview != "-")
            {
                lines.Add($"value: {valuePreview}");
            }

            if (metaParts.Count > 0)
            {
                lines.Add($"메타: {string.Join(", ", metaParts)}");
            }

            return string.Join("\n", lines);
        }
        catch
        {
            return $"Selected element: {TrimForLog(details, 160)}";
        }
    }

    private WebViewNetworkLogEntry BuildNetworkStatusLine(
        string channel,
        string method,
        string url,
        int? status,
        double? durationMs,
        string? requestHeadersPreview,
        string? requestBodyPreview,
        string? responseHeadersPreview,
        string? responseBodyPreview,
        string? statusText = null,
        string? resourceType = null,
        string? mimeType = null,
        double encodedDataLength = 0d)
    {
        var statusCode = status.HasValue ? status.Value.ToString(CultureInfo.InvariantCulture) : "-";
        var durationText = durationMs.HasValue ? $"{durationMs.Value:0.0}ms" : "-";
        var shortUrl = TrimForLog(url, 180);
        var fullUrl = string.IsNullOrWhiteSpace(url) ? "-" : url.Trim();
        var reqHeaders = string.IsNullOrWhiteSpace(requestHeadersPreview) ? "-" : requestHeadersPreview.Trim();
        var reqBody = string.IsNullOrWhiteSpace(requestBodyPreview) ? "-" : requestBodyPreview.Trim();
        var resHeaders = string.IsNullOrWhiteSpace(responseHeadersPreview) ? "-" : responseHeadersPreview.Trim();
        var resBody = string.IsNullOrWhiteSpace(responseBodyPreview) ? "-" : responseBodyPreview.Trim();
        var normalizedStatus = NormalizeStatusMessage(status, statusText);
        var statusMessage = TrimForLog(normalizedStatus, 60);
        var resource = TrimForLog(resourceType, 32);
        var mime = TrimForLog(mimeType, 48);
        var timestamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        var summary = $"[{timestamp}] [{channel}] {method} {statusCode} {durationText} {shortUrl}";
        var request = $"URL: {fullUrl}\nTYPE: {resource}\nMIME: {mime}\nHEADERS:\n{reqHeaders}\nBODY:\n{reqBody}";
        var response = $"STATUS: {statusCode} ({statusMessage})\nDURATION: {durationText}\nHEADERS:\n{resHeaders}\nBODY:\n{resBody}";
        return BuildWebViewNetworkEntry(
            _webViewNetworkEntries.Count + 1,
            summary,
            request,
            response,
            method,
            url,
            status,
            durationMs,
            encodedDataLength);
    }

    private static string NormalizeStatusMessage(int? status, string? statusText)
    {
        if (!string.IsNullOrWhiteSpace(statusText))
        {
            return statusText.Trim();
        }

        if (!status.HasValue)
        {
            return "-";
        }

        try
        {
            var code = (HttpStatusCode)status.Value;
            return Enum.IsDefined(typeof(HttpStatusCode), code) ? code.ToString() : "-";
        }
        catch
        {
            return "-";
        }
    }

    private async Task ToggleWebViewInspectorModeAsync()
    {
        try
        {
            _webViewInspectorModeEnabled = !_webViewInspectorModeEnabled;
            UpdateBrowserFocusButton();
            UpdateWebViewInspectorToggleButton();
            await ApplyWebViewInspectorModeAsync(_webViewInspectorModeEnabled);
            _naverTalkBrowser.SetStatus(
                _webViewInspectorModeEnabled
                    ? "Focus mode turned on."
                    : "Focus mode turned off.",
                _naverTalkBrowser.CurrentUrl);
        }
        catch (Exception ex)
        {
            _naverTalkBrowser.SetStatus($"Focus mode toggle failed: {ex.Message}", _naverTalkBrowser.CurrentUrl);
        }
    }

    private async Task ToggleWebViewNetworkCaptureModeAsync()
    {
        try
        {
            _webViewNetworkCaptureEnabled = !_webViewNetworkCaptureEnabled;
            UpdateWebViewNetworkCaptureToggleButton();
            await ApplyWebViewNetworkCaptureModeAsync(_webViewNetworkCaptureEnabled);
            _naverTalkBrowser.SetStatus(
                _webViewNetworkCaptureEnabled
                    ? "WebView network capture turned on."
                    : "WebView network capture turned off.",
                _naverTalkBrowser.CurrentUrl);
        }
        catch (Exception ex)
        {
            _naverTalkBrowser.SetStatus($"Network capture toggle failed: {ex.Message}", _naverTalkBrowser.CurrentUrl);
        }
    }

    private async Task EnsureWebViewInspectorScriptRegisteredAsync(CoreWebView2 coreWebView2)
    {
        if (_webViewInspectorScriptRegistered)
        {
            return;
        }

        _webViewInspectorScriptRegistered = true;
        await coreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            """
            (() => {
                if (!window.chrome?.webview || window.__codexInspectorBootstrap) {
                    return;
                }

                window.__codexInspectorBootstrap = true;
                const overlay = document.createElement('div');
                overlay.style.position = 'fixed';
                overlay.style.pointerEvents = 'none';
                overlay.style.zIndex = '2147483646';
                overlay.style.border = '2px solid #0d8a65';
                overlay.style.background = 'rgba(13, 138, 101, 0.15)';
                overlay.style.display = 'none';
                overlay.style.boxSizing = 'border-box';

                const badge = document.createElement('div');
                badge.style.position = 'fixed';
                badge.style.pointerEvents = 'none';
                badge.style.zIndex = '2147483647';
                badge.style.fontFamily = 'Consolas, monospace';
                badge.style.fontSize = '12px';
                badge.style.maxWidth = '60vw';
                badge.style.padding = '4px 8px';
                badge.style.borderRadius = '4px';
                badge.style.color = '#ffffff';
                badge.style.background = '#0d8a65';
                badge.style.boxShadow = '0 2px 8px rgba(0,0,0,.25)';
                badge.style.display = 'none';
                badge.style.whiteSpace = 'pre-wrap';
                badge.style.lineHeight = '1.35';
                badge.style.maxHeight = '45vh';
                badge.style.overflow = 'hidden';

                const state = {
                    enabled: false,
                    locked: false,
                    current: null,
                    syntheticClickInProgress: false
                };

                const post = (kind, details = '') => {
                    try {
                        window.chrome.webview.postMessage(`codex-inspect:${kind}|${details}`);
                    } catch {}
                };
                const truncate = (value, max = 800) => {
                    if (value == null) return '';
                    const text = String(value).replace(/\s+/g, ' ').trim();
                    return text.length <= max ? text : `${text.slice(0, max)}...`;
                };
                const safeSelector = (el) => {
                    if (!el) return '';
                    const tag = String(el.tagName || '').toLowerCase();
                    const id = el.id ? `#${el.id}` : '';
                    const classes = typeof el.className === 'string'
                        ? el.className.trim().split(/\s+/).filter(Boolean).slice(0, 4).map(v => `.${v}`).join('')
                        : '';
                    return `${tag}${id}${classes}`;
                };
                const buildElementSummary = (el) => {
                    if (!el) {
                        return '';
                    }

                    const rect = el.getBoundingClientRect?.() || { left: 0, top: 0, width: 0, height: 0 };
                    const textPreview = truncate(el.innerText || el.value || el.textContent || '', 120);
                    const href = typeof el.getAttribute === 'function' ? (el.getAttribute('href') || '') : '';
                    const name = typeof el.getAttribute === 'function' ? (el.getAttribute('name') || '') : '';
                    const ariaLabel = typeof el.getAttribute === 'function' ? (el.getAttribute('aria-label') || '') : '';
                    const role = typeof el.getAttribute === 'function' ? (el.getAttribute('role') || '') : '';
                    const placeholder = typeof el.getAttribute === 'function' ? (el.getAttribute('placeholder') || '') : '';
                    const inputType = typeof el.getAttribute === 'function' ? (el.getAttribute('type') || '') : '';
                    const valuePreview = truncate(el.value || '', 100);
                    const domPathParts = [];
                    let node = el;
                    for (let i = 0; i < 6 && node; i += 1) {
                        const nodeTag = String(node.tagName || '').toLowerCase();
                        if (!nodeTag) break;
                        const nodeId = node.id ? `#${node.id}` : '';
                        domPathParts.unshift(`${nodeTag}${nodeId}`);
                        node = node.parentElement;
                    }
                    const domPath = domPathParts.join(' > ');

                    return {
                        selector: safeSelector(el),
                        tag: String(el.tagName || '').toLowerCase(),
                        id: el.id || '',
                        className: typeof el.className === 'string' ? el.className : '',
                        href: truncate(href, 180),
                        name: truncate(name, 80),
                        ariaLabel: truncate(ariaLabel, 80),
                        role: truncate(role, 60),
                        placeholder: truncate(placeholder, 80),
                        inputType: truncate(inputType, 40),
                        valuePreview,
                        domPath: truncate(domPath, 220),
                        textPreview,
                        rect: {
                            left: Number(rect.left || 0),
                            top: Number(rect.top || 0),
                            width: Number(rect.width || 0),
                            height: Number(rect.height || 0)
                        }
                    };
                };
                const ensureUi = () => {
                    if (!overlay.isConnected) {
                        document.documentElement.appendChild(overlay);
                    }
                    if (!badge.isConnected) {
                        document.documentElement.appendChild(badge);
                    }
                };

                const clearUi = () => {
                    overlay.style.display = 'none';
                    badge.style.display = 'none';
                };

                const placeBadge = (x, y) => {
                    const viewportWidth = Math.max(document.documentElement.clientWidth, window.innerWidth || 0);
                    const viewportHeight = Math.max(document.documentElement.clientHeight, window.innerHeight || 0);
                    const left = Math.min(Math.max(x + 12, 8), Math.max(8, viewportWidth - badge.offsetWidth - 8));
                    const top = Math.min(Math.max(y + 12, 8), Math.max(8, viewportHeight - badge.offsetHeight - 8));
                    badge.style.left = `${left}px`;
                    badge.style.top = `${top}px`;
                };

                const renderTarget = (target, clientX, clientY) => {
                    if (!target || target === overlay || target === badge) {
                        clearUi();
                        return;
                    }

                    const rect = target.getBoundingClientRect();
                    if (!rect || rect.width <= 0 || rect.height <= 0) {
                        clearUi();
                        return;
                    }

                    ensureUi();
                    overlay.style.display = 'block';
                    overlay.style.left = `${rect.left}px`;
                    overlay.style.top = `${rect.top}px`;
                    overlay.style.width = `${rect.width}px`;
                    overlay.style.height = `${rect.height}px`;

                    const summary = buildElementSummary(target);
                    const idLine = summary.id ? `id: ${summary.id}` : '';
                    const nameLine = summary.name ? `name: ${summary.name}` : '';
                    const identityLine = idLine && nameLine
                        ? `${idLine} | ${nameLine}`
                        : (idLine || nameLine);
                    const lines = [
                        summary.selector,
                        `${summary.rect.width.toFixed(0)}x${summary.rect.height.toFixed(0)} @ (${summary.rect.left.toFixed(0)},${summary.rect.top.toFixed(0)})`,
                        `text: ${summary.textPreview || '-'}`
                    ];
                    if (identityLine) {
                        lines.splice(1, 0, identityLine);
                    }
                    badge.style.display = 'block';
                    badge.textContent = lines.join('\n');
                    placeBadge(clientX, clientY);
                };

                document.addEventListener('mousemove', (event) => {
                    if (!state.enabled || state.locked) {
                        return;
                    }

                    const target = document.elementFromPoint(event.clientX, event.clientY);
                    state.current = target;
                    renderTarget(target, event.clientX, event.clientY);
                }, true);

                document.addEventListener('click', (event) => {
                    if (!state.enabled) {
                        return;
                    }

                    if (state.syntheticClickInProgress) {
                        return;
                    }

                    const target = state.current ?? document.elementFromPoint(event.clientX, event.clientY);
                    if (!target) {
                        return;
                    }

                    if (state.locked) {
                        event.preventDefault();
                        event.stopPropagation();
                        event.stopImmediatePropagation();
                        state.locked = false;
                        state.current = null;
                        clearUi();
                        post('unlocked', '');
                        return;
                    }

                    if ((event.detail || 0) >= 2) {
                        event.preventDefault();
                        event.stopPropagation();
                        event.stopImmediatePropagation();

                        const details = JSON.stringify(buildElementSummary(target));
                        state.locked = false;
                        clearUi();
                        try {
                            state.syntheticClickInProgress = true;
                            if (typeof target.click === 'function') {
                                target.click();
                            } else {
                                target.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                            }
                        } catch {}
                        finally {
                            state.syntheticClickInProgress = false;
                        }
                        post('applied', details);
                        return;
                    }

                    event.preventDefault();
                    event.stopPropagation();
                    event.stopImmediatePropagation();

                    state.current = target;
                    state.locked = true;
                    renderTarget(target, event.clientX, event.clientY);
                    post('selected', JSON.stringify(buildElementSummary(target)));
                }, true);

                document.addEventListener('keydown', (event) => {
                    if (!state.enabled) {
                        return;
                    }

                    if (event.key === 'Escape') {
                        state.locked = false;
                        clearUi();
                        post('escape', '');
                    }
                }, true);

                window.__codexInspectorSetEnabled = (enabled) => {
                    state.enabled = !!enabled;
                    state.locked = false;
                    state.current = null;
                    if (!state.enabled) {
                        clearUi();
                        post('disabled', '');
                        return true;
                    }

                    ensureUi();
                    post('enabled', '');
                    return true;
                };

            })();
            """);
    }

    private async Task ApplyWebViewInspectorModeAsync(bool enabled)
    {
        var coreWebView2 = await TryEnsureCoreWebView2Async();
        if (coreWebView2 is null)
        {
            return;
        }

        await EnsureWebViewInspectorScriptRegisteredAsync(coreWebView2);
        var script = $"window.__codexInspectorSetEnabled?.({enabled.ToString().ToLowerInvariant()});";
        await RunBrowserScriptAsync(script);
    }

    private async Task ApplyWebViewNetworkCaptureModeAsync(bool enabled)
    {
        var coreWebView2 = await TryEnsureCoreWebView2Async();
        if (coreWebView2 is null)
        {
            return;
        }

        await EnsureWebViewNetworkDevToolsHookAsync(coreWebView2);
        if (enabled)
        {
            if (!_webViewNetworkDomainEnabled)
            {
                await coreWebView2.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                _webViewNetworkDomainEnabled = true;
            }
            return;
        }

        if (_webViewNetworkDomainEnabled)
        {
            await coreWebView2.CallDevToolsProtocolMethodAsync("Network.disable", "{}");
            _webViewNetworkDomainEnabled = false;
        }

        lock (_webViewNetworkSync)
        {
            _pendingNetworkRequests.Clear();
            _pendingRequestExtraHeaders.Clear();
            _pendingResponseExtraHeaders.Clear();
        }
    }

    private Task EnsureWebViewNetworkDevToolsHookAsync(CoreWebView2 coreWebView2)
    {
        if (ReferenceEquals(_networkReceiversAttachedCore, coreWebView2))
        {
            return Task.CompletedTask;
        }

        DetachWebViewNetworkDevToolsHook();

        _networkRequestWillBeSentReceiver = coreWebView2.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
        _networkRequestWillBeSentExtraInfoReceiver = coreWebView2.GetDevToolsProtocolEventReceiver("Network.requestWillBeSentExtraInfo");
        _networkResponseReceivedReceiver = coreWebView2.GetDevToolsProtocolEventReceiver("Network.responseReceived");
        _networkResponseReceivedExtraInfoReceiver = coreWebView2.GetDevToolsProtocolEventReceiver("Network.responseReceivedExtraInfo");
        _networkLoadingFinishedReceiver = coreWebView2.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
        _networkLoadingFailedReceiver = coreWebView2.GetDevToolsProtocolEventReceiver("Network.loadingFailed");

        _networkRequestWillBeSentReceiver.DevToolsProtocolEventReceived += OnNetworkRequestWillBeSent;
        _networkRequestWillBeSentExtraInfoReceiver.DevToolsProtocolEventReceived += OnNetworkRequestWillBeSentExtraInfo;
        _networkResponseReceivedReceiver.DevToolsProtocolEventReceived += OnNetworkResponseReceived;
        _networkResponseReceivedExtraInfoReceiver.DevToolsProtocolEventReceived += OnNetworkResponseReceivedExtraInfo;
        _networkLoadingFinishedReceiver.DevToolsProtocolEventReceived += OnNetworkLoadingFinished;
        _networkLoadingFailedReceiver.DevToolsProtocolEventReceived += OnNetworkLoadingFailed;

        _networkReceiversAttachedCore = coreWebView2;
        _webViewNetworkDomainEnabled = false;
        return Task.CompletedTask;
    }

    private void DetachWebViewNetworkDevToolsHook()
    {
        if (_networkRequestWillBeSentReceiver is not null)
        {
            _networkRequestWillBeSentReceiver.DevToolsProtocolEventReceived -= OnNetworkRequestWillBeSent;
            _networkRequestWillBeSentReceiver = null;
        }

        if (_networkRequestWillBeSentExtraInfoReceiver is not null)
        {
            _networkRequestWillBeSentExtraInfoReceiver.DevToolsProtocolEventReceived -= OnNetworkRequestWillBeSentExtraInfo;
            _networkRequestWillBeSentExtraInfoReceiver = null;
        }

        if (_networkResponseReceivedReceiver is not null)
        {
            _networkResponseReceivedReceiver.DevToolsProtocolEventReceived -= OnNetworkResponseReceived;
            _networkResponseReceivedReceiver = null;
        }

        if (_networkResponseReceivedExtraInfoReceiver is not null)
        {
            _networkResponseReceivedExtraInfoReceiver.DevToolsProtocolEventReceived -= OnNetworkResponseReceivedExtraInfo;
            _networkResponseReceivedExtraInfoReceiver = null;
        }

        if (_networkLoadingFinishedReceiver is not null)
        {
            _networkLoadingFinishedReceiver.DevToolsProtocolEventReceived -= OnNetworkLoadingFinished;
            _networkLoadingFinishedReceiver = null;
        }

        if (_networkLoadingFailedReceiver is not null)
        {
            _networkLoadingFailedReceiver.DevToolsProtocolEventReceived -= OnNetworkLoadingFailed;
            _networkLoadingFailedReceiver = null;
        }

        _networkReceiversAttachedCore = null;
        _webViewNetworkDomainEnabled = false;
        lock (_webViewNetworkSync)
        {
            _pendingNetworkRequests.Clear();
            _pendingRequestExtraHeaders.Clear();
            _pendingResponseExtraHeaders.Clear();
        }
    }

    private void OnNetworkRequestWillBeSent(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (!_webViewNetworkCaptureEnabled)
        {
            return;
        }

        try
        {
            var payload = JsonNode.Parse(e.ParameterObjectAsJson)?.AsObject();
            if (payload is null)
            {
                return;
            }

            var requestId = payload["requestId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return;
            }

            var request = payload["request"]?.AsObject();
            var method = request?["method"]?.GetValue<string>() ?? "GET";
            var url = request?["url"]?.GetValue<string>() ?? string.Empty;
            var requestHeadersPreview = ToHeaderLines(request?["headers"]);
            var requestBodyPreview = request?["postData"]?.GetValue<string>() ?? string.Empty;
            var resourceType = payload["type"]?.GetValue<string>() ?? "-";
            var startedMonotonicSeconds = payload["timestamp"]?.GetValue<double?>() ?? 0d;
            var startedAt = DateTimeOffset.Now;

            var wallTimeSeconds = payload["wallTime"]?.GetValue<double?>();
            if (wallTimeSeconds.HasValue)
            {
                try
                {
                    startedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(wallTimeSeconds.Value * 1000d));
                }
                catch
                {
                    startedAt = DateTimeOffset.Now;
                }
            }

            var captured = new CapturedNetworkRequest
            {
                RequestId = requestId,
                Method = method,
                Url = url,
                RequestHeadersPreview = requestHeadersPreview,
                RequestBodyPreview = requestBodyPreview,
                ResourceType = resourceType,
                StartedMonotonicSeconds = startedMonotonicSeconds,
                StartedAt = startedAt
            };

            lock (_webViewNetworkSync)
            {
                if (_pendingRequestExtraHeaders.Remove(requestId, out var requestExtraHeaders))
                {
                    captured.RequestHeadersPreview = MergeHeaderPreviews(captured.RequestHeadersPreview, requestExtraHeaders);
                }

                _pendingNetworkRequests[requestId] = captured;
            }
        }
        catch
        {
        }
    }

    private void OnNetworkRequestWillBeSentExtraInfo(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (!_webViewNetworkCaptureEnabled)
        {
            return;
        }

        try
        {
            var payload = JsonNode.Parse(e.ParameterObjectAsJson)?.AsObject();
            var requestId = payload?["requestId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return;
            }

            var headersPreview = ToHeaderLines(payload?["headers"]);
            if (string.IsNullOrWhiteSpace(headersPreview) || headersPreview == "-")
            {
                return;
            }

            lock (_webViewNetworkSync)
            {
                if (_pendingNetworkRequests.TryGetValue(requestId, out var captured))
                {
                    captured.RequestHeadersPreview = MergeHeaderPreviews(captured.RequestHeadersPreview, headersPreview);
                }
                else
                {
                    _pendingRequestExtraHeaders[requestId] = headersPreview;
                }
            }
        }
        catch
        {
        }
    }

    private void OnNetworkResponseReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (!_webViewNetworkCaptureEnabled)
        {
            return;
        }

        try
        {
            var payload = JsonNode.Parse(e.ParameterObjectAsJson)?.AsObject();
            var requestId = payload?["requestId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return;
            }

            var response = payload?["response"]?.AsObject();
            if (response is null)
            {
                return;
            }

            lock (_webViewNetworkSync)
            {
                if (!_pendingNetworkRequests.TryGetValue(requestId, out var captured))
                {
                    return;
                }

                var statusValue = response["status"]?.GetValue<double?>();
                captured.Status = statusValue.HasValue ? (int)Math.Round(statusValue.Value) : null;
                captured.StatusText = response["statusText"]?.GetValue<string>() ?? string.Empty;
                captured.MimeType = response["mimeType"]?.GetValue<string>() ?? string.Empty;
                captured.ResponseHeadersPreview = ToHeaderLines(response["headers"]);
                if (_pendingResponseExtraHeaders.Remove(requestId, out var responseExtraHeaders))
                {
                    captured.ResponseHeadersPreview = MergeHeaderPreviews(captured.ResponseHeadersPreview, responseExtraHeaders);
                }
            }
        }
        catch
        {
        }
    }

    private void OnNetworkResponseReceivedExtraInfo(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (!_webViewNetworkCaptureEnabled)
        {
            return;
        }

        try
        {
            var payload = JsonNode.Parse(e.ParameterObjectAsJson)?.AsObject();
            var requestId = payload?["requestId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return;
            }

            var headersPreview = ToHeaderLines(payload?["headers"]);
            if (string.IsNullOrWhiteSpace(headersPreview) || headersPreview == "-")
            {
                return;
            }

            lock (_webViewNetworkSync)
            {
                if (_pendingNetworkRequests.TryGetValue(requestId, out var captured))
                {
                    captured.ResponseHeadersPreview = MergeHeaderPreviews(captured.ResponseHeadersPreview, headersPreview);
                }
                else
                {
                    _pendingResponseExtraHeaders[requestId] = headersPreview;
                }
            }
        }
        catch
        {
        }
    }

    private void OnNetworkLoadingFinished(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        _ = HandleNetworkLoadingFinishedAsync(e.ParameterObjectAsJson);
    }

    private async Task HandleNetworkLoadingFinishedAsync(string parameterJson)
    {
        if (!_webViewNetworkCaptureEnabled)
        {
            return;
        }

        try
        {
            var payload = JsonNode.Parse(parameterJson)?.AsObject();
            var requestId = payload?["requestId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return;
            }

            CapturedNetworkRequest? captured = null;
            var finishedMonotonicSeconds = payload?["timestamp"]?.GetValue<double?>();
            var encodedDataLength = payload?["encodedDataLength"]?.GetValue<double?>() ?? 0d;
            lock (_webViewNetworkSync)
            {
                if (_pendingNetworkRequests.TryGetValue(requestId, out captured))
                {
                    _pendingNetworkRequests.Remove(requestId);
                }
            }

            if (captured is null)
            {
                return;
            }

            var durationMs = finishedMonotonicSeconds.HasValue && captured.StartedMonotonicSeconds > 0
                ? Math.Max(0d, (finishedMonotonicSeconds.Value - captured.StartedMonotonicSeconds) * 1000d)
                : (DateTimeOffset.Now - captured.StartedAt).TotalMilliseconds;

            captured.EncodedDataLength = encodedDataLength;
            var shouldReadBody = ShouldReadResponseBody(captured.ResourceType, captured.MimeType, captured.EncodedDataLength);
            var responseBodyPreview = shouldReadBody
                ? await TryGetResponseBodyPreviewAsync(captured.RequestId, captured.MimeType)
                : $"[response body skipped: type={TrimForLog(captured.ResourceType, 20)}, size={captured.EncodedDataLength:0} bytes]";
            if (string.IsNullOrWhiteSpace(responseBodyPreview))
            {
                var bytesText = captured.EncodedDataLength > 0
                    ? $"{captured.EncodedDataLength:0} bytes"
                    : "0 bytes";
                responseBodyPreview = $"[response body unavailable: {bytesText}, mime={TrimForLog(captured.MimeType, 60)}]";
            }
            var line = BuildNetworkStatusLine(
                "cdp",
                captured.Method,
                captured.Url,
                captured.Status,
                durationMs,
                captured.RequestHeadersPreview,
                captured.RequestBodyPreview,
                captured.ResponseHeadersPreview,
                responseBodyPreview,
                captured.StatusText,
                captured.ResourceType,
                captured.MimeType,
                captured.EncodedDataLength);

            MainThread.BeginInvokeOnMainThread(() => AppendWebViewNetworkEntry(line));
        }
        catch
        {
        }
    }

    private static bool ShouldReadResponseBody(string? resourceType, string? mimeType, double encodedDataLength)
    {
        if (encodedDataLength > 512 * 1024)
        {
            return false;
        }

        var type = resourceType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (type is "image" or "media" or "font" or "other")
        {
            return false;
        }

        if (type is "xhr" or "fetch" or "document")
        {
            return true;
        }

        return IsLikelyTextMimeType(mimeType);
    }

    private void OnNetworkLoadingFailed(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (!_webViewNetworkCaptureEnabled)
        {
            return;
        }

        try
        {
            var payload = JsonNode.Parse(e.ParameterObjectAsJson)?.AsObject();
            var requestId = payload?["requestId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return;
            }

            CapturedNetworkRequest? captured = null;
            lock (_webViewNetworkSync)
            {
                if (_pendingNetworkRequests.TryGetValue(requestId, out captured))
                {
                    _pendingNetworkRequests.Remove(requestId);
                }
            }

            var errorText = payload?["errorText"]?.GetValue<string>() ?? "network error";
            var canceled = payload?["canceled"]?.GetValue<bool?>() ?? false;
            var line = BuildNetworkStatusLine(
                "cdp",
                captured?.Method ?? "GET",
                captured?.Url ?? "-",
                0,
                null,
                captured?.RequestHeadersPreview,
                captured?.RequestBodyPreview,
                captured?.ResponseHeadersPreview,
                canceled ? "request canceled" : errorText,
                errorText,
                captured?.ResourceType,
                captured?.MimeType,
                captured?.EncodedDataLength ?? 0d);

            MainThread.BeginInvokeOnMainThread(() => AppendWebViewNetworkEntry(line));
        }
        catch
        {
        }
    }

    private async Task<string> TryGetResponseBodyPreviewAsync(string requestId, string? mimeType)
    {
        var coreWebView2 = _networkReceiversAttachedCore;
        if (coreWebView2 is null || string.IsNullOrWhiteSpace(requestId))
        {
            return string.Empty;
        }

        try
        {
            var requestIdJson = JsonSerializer.Serialize(requestId);
            var args = $"{{\"requestId\":{requestIdJson}}}";
            var raw = await coreWebView2.CallDevToolsProtocolMethodAsync("Network.getResponseBody", args);
            var payload = JsonNode.Parse(raw)?.AsObject();
            var body = payload?["body"]?.GetValue<string>();
            var isBase64 = payload?["base64Encoded"]?.GetValue<bool?>() ?? false;
            if (!isBase64)
            {
                return body ?? string.Empty;
            }

            if (string.IsNullOrEmpty(body))
            {
                return string.Empty;
            }

            if (!IsLikelyTextMimeType(mimeType))
            {
                return $"[base64:{body.Length}]";
            }

            try
            {
                var bytes = Convert.FromBase64String(body);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return $"[base64:{body.Length}]";
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsLikelyTextMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return false;
        }

        var normalized = mimeType.Trim().ToLowerInvariant();
        return normalized.StartsWith("text/", StringComparison.Ordinal) ||
               normalized.Contains("json", StringComparison.Ordinal) ||
               normalized.Contains("xml", StringComparison.Ordinal) ||
               normalized.Contains("javascript", StringComparison.Ordinal) ||
               normalized.Contains("x-www-form-urlencoded", StringComparison.Ordinal);
    }

    private static string ToHeaderLines(JsonNode? headersNode)
    {
        if (headersNode is not JsonObject headersObject || headersObject.Count == 0)
        {
            return "-";
        }

        var lines = new List<string>();
        foreach (var pair in headersObject.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var value = pair.Value switch
            {
                JsonValue jsonValue => jsonValue.ToString(),
                null => string.Empty,
                _ => pair.Value.ToJsonString()
            };

            lines.Add($"{pair.Key}: {value}");
        }

        return lines.Count == 0 ? "-" : string.Join("\n", lines);
    }

    private static string MergeHeaderPreviews(string? primary, string? secondary)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddLines(List<string> target, HashSet<string> dedupe, string? source)
        {
            if (string.IsNullOrWhiteSpace(source) || source.Trim() == "-")
            {
                return;
            }

            foreach (var line in source.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (dedupe.Add(line))
                {
                    target.Add(line);
                }
            }
        }

        AddLines(lines, seen, primary);
        AddLines(lines, seen, secondary);
        return lines.Count == 0 ? "-" : string.Join('\n', lines);
    }

#endif

    private async void OnLoaded(object? sender, EventArgs e)
    {
        Loaded -= OnLoaded;

        SetupSplitterGesture();
        ConfigureWindowChrome();

#if WINDOWS
        try
        {
            ConfigureWebView2ForWindows();
            await TryEnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            _naverTalkBrowser.SetStatus($"WebView2 initialization failed: {ex.Message}");
        }

        SetupWindowKeyboardHook();
#endif

        await _naverTalkBrowser.EnsureInitializedAsync();
        UpdateBrowserChrome();
        await _naverTalkBrowser.NavigateAsync("https://www.google.com");
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _naverTalkBrowser.StateChanged -= OnBrowserStateChanged;
        _naverTalkBrowser.CommandRequested -= OnBrowserCommandRequested;
        BrowserPane.SizeChanged -= OnBrowserPaneSizeChanged;
#if WINDOWS
        DetachWebViewNetworkDevToolsHook();
        TeardownWindowKeyboardHook();
#endif
        Unloaded -= OnUnloaded;
    }

    private void OnBrowserStateChanged()
    {
        _ = MainThread.InvokeOnMainThreadAsync(UpdateBrowserChrome);
    }

    private void OnNaverTalkWebViewFocused(object? sender, FocusEventArgs e)
    {
        _isNaverTalkWebViewFocused = true;
    }

    private void OnNaverTalkWebViewUnfocused(object? sender, FocusEventArgs e)
    {
        _isNaverTalkWebViewFocused = false;
    }

    private void OnBrowserCommandRequested(BrowserCommand command)
    {
        _ = MainThread.InvokeOnMainThreadAsync(async () =>
        {
            switch (command.Type)
            {
                case BrowserCommandType.Navigate:
                    var targetUrl = string.IsNullOrWhiteSpace(command.Url)
                        ? _naverTalkBrowser.Settings.TargetUrl
                        : command.Url;

                    NaverTalkWebView.Source = new UrlWebViewSource { Url = targetUrl };
                    break;

                case BrowserCommandType.Reload:
                    NaverTalkWebView.Reload();
                    break;

                case BrowserCommandType.TypeText:
                    await TypeTextIntoNaverTalkAsync(command.Text, command.Submit);
                    break;

                case BrowserCommandType.CollectCompletedChats:
                    _naverTalkBrowser.SetStatus("WebView completed chat collection is disabled. The app now uses Playwright for this action.");
                    break;
            }

            UpdateBrowserChrome();

        });
    }

    private async void OnBrowserAddressEntryCompleted(object? sender, EventArgs e)
    {
        await NavigateFromAddressBarAsync();
    }

    private async void OnBrowserAddressGoClicked(object? sender, EventArgs e)
    {
        await NavigateFromAddressBarAsync();
    }

    private void OnBrowserBackClicked(object? sender, EventArgs e)
    {
        if (NaverTalkWebView.CanGoBack)
        {
            NaverTalkWebView.GoBack();
        }

        UpdateBrowserChrome();
    }

    private void OnBrowserForwardClicked(object? sender, EventArgs e)
    {
        if (NaverTalkWebView.CanGoForward)
        {
            NaverTalkWebView.GoForward();
        }

        UpdateBrowserChrome();
    }

    private async Task NavigateFromAddressBarAsync()
    {
        var input = BrowserAddressEntry?.Text;
        if (string.IsNullOrWhiteSpace(input))
        {
            _naverTalkBrowser.SetStatus("주소를 입력해 주세요.", _naverTalkBrowser.CurrentUrl);
            return;
        }

        var normalizedUrl = NormalizeAddressInput(input);
        if (string.IsNullOrWhiteSpace(normalizedUrl))
        {
            _naverTalkBrowser.SetStatus("유효한 주소를 입력해 주세요.", _naverTalkBrowser.CurrentUrl);
            return;
        }

        if (BrowserAddressEntry is not null)
        {
            BrowserAddressEntry.Text = normalizedUrl;
        }

        await _naverTalkBrowser.NavigateAsync(normalizedUrl);
    }

    private static string? NormalizeAddressInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.AbsoluteUri;
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            trimmed = $"https:{trimmed}";
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var protocolRelativeUri)
                ? protocolRelativeUri.AbsoluteUri
                : null;
        }

        var candidate = $"https://{trimmed}";
        return Uri.TryCreate(candidate, UriKind.Absolute, out var httpsUri)
            ? httpsUri.AbsoluteUri
            : null;
    }

    private void OnBrowserCookieClicked(object? sender, EventArgs e)
    {
        if (CookieImportModal is null)
        {
            return;
        }

        CookieImportModal.IsVisible = true;

        if (CookieDomainEntry is not null && string.IsNullOrWhiteSpace(CookieDomainEntry.Text))
        {
            CookieDomainEntry.Text = TryResolveCurrentBrowserOrigin();
        }

        if (CookieImportStatusLabel is not null)
        {
            CookieImportStatusLabel.Text = "쿠키 문자열을 붙여넣고 적용할 도메인을 확인한 뒤 Apply Cookies를 누르세요.";
            CookieImportStatusLabel.TextColor = Color.FromArgb("#637780");
        }
    }

    private void OnCloseCookieImportModalClicked(object? sender, EventArgs e)
    {
        if (CookieImportModal is not null)
        {
            CookieImportModal.IsVisible = false;
        }
    }

    private void OnBrowserRequestClicked(object? sender, EventArgs e)
    {
        if (BrowserRequestModal is null)
        {
            return;
        }

        BrowserRequestModal.IsVisible = true;

        if (BrowserRequestMethodEntry is not null && string.IsNullOrWhiteSpace(BrowserRequestMethodEntry.Text))
        {
            BrowserRequestMethodEntry.Text = "POST";
        }

        if (BrowserRequestUrlEntry is not null && string.IsNullOrWhiteSpace(BrowserRequestUrlEntry.Text))
        {
            BrowserRequestUrlEntry.Text = "/Home/ExtendLoginTime";
        }

        SetBrowserRequestStatus("Body가 비어 있으면 body 없이 요청합니다.", isError: false);
    }

    private void OnCloseBrowserRequestModalClicked(object? sender, EventArgs e)
    {
        if (BrowserRequestModal is not null)
        {
            BrowserRequestModal.IsVisible = false;
        }
    }

    private async void OnBrowserSnapshotClicked(object? sender, EventArgs e)
    {
        await ShowSnapshotButtonPressedAsync();

        try
        {
            SetSnapshotButtonSavingState(isSaving: true);
            _naverTalkBrowser.SetStatus("현재 WebView 페이지 스냅샷을 저장하는 중입니다...", _naverTalkBrowser.CurrentUrl);

            var raw = await RunBrowserScriptAsync(WebViewPageSnapshotScriptBuilder.Build());
            var snapshot = ParseScriptJson<WebViewPageSnapshotPayload>(raw);
            if (snapshot is null)
            {
                _naverTalkBrowser.SetStatus("WebView 페이지 스냅샷 결과를 해석하지 못했습니다.", _naverTalkBrowser.CurrentUrl);
                return;
            }

            var result = await WebViewPageSnapshotWriter.SaveAsync(snapshot, AppContext.BaseDirectory);
            _pendingWebViewSnapshot = result;
            _naverTalkBrowser.SetStatus($"WebView 페이지 스냅샷 저장 완료: {result.DirectoryPath}", _naverTalkBrowser.CurrentUrl);

            SetSnapshotButtonSavingState(isSaving: false);
            HideBrowserWebViewForSnapshotModal();
            ShowBrowserSnapshotConfirmModal(result);
        }
        catch (Exception ex)
        {
            _naverTalkBrowser.SetStatus($"WebView 페이지 스냅샷 저장 실패: {ex.Message}", _naverTalkBrowser.CurrentUrl);
        }
        finally
        {
            SetSnapshotButtonSavingState(isSaving: false);
        }
    }

    private void HideBrowserWebViewForSnapshotModal()
    {
        if (NaverTalkWebView is null || !NaverTalkWebView.IsVisible)
        {
            return;
        }

        NaverTalkWebView.IsVisible = false;
        _snapshotModalHidWebView = true;
    }

    private void RestoreBrowserWebViewAfterSnapshotModal()
    {
        if (!_snapshotModalHidWebView)
        {
            return;
        }

        NaverTalkWebView.IsVisible = true;
        _snapshotModalHidWebView = false;
    }

    private async Task<bool> ShowBrowserSnapshotBeforeSaveModalAsync()
    {
        _pendingWebViewSnapshot = null;
        _pendingSnapshotAnalyzeChoice = new TaskCompletionSource<bool>();
        _pendingSnapshotUserQuestion = null;

        if (BrowserSnapshotModalTitleLabel is not null)
        {
            BrowserSnapshotModalTitleLabel.Text = "Snapshot";
        }

        if (BrowserSnapshotModalDescriptionLabel is not null)
        {
            BrowserSnapshotModalDescriptionLabel.Text = "snapshot 저장 후 Codex로 분석할까요?";
        }

        if (BrowserSnapshotFolderLabel is not null)
        {
            BrowserSnapshotFolderLabel.Text = "Folder: 저장 후 생성됩니다.";
        }

        if (BrowserSnapshotPathLabel is not null)
        {
            BrowserSnapshotPathLabel.Text = "Path: 저장 후 생성됩니다.";
        }

        if (BrowserSnapshotPromptEditor is not null)
        {
            BrowserSnapshotPromptEditor.Text = "현재 WebView 페이지 스냅샷을 분석해줘.";
        }

        if (BrowserSnapshotConfirmModal is not null)
        {
            BrowserSnapshotConfirmModal.IsVisible = true;
        }

        return await _pendingSnapshotAnalyzeChoice.Task;
    }

    private async Task ShowSnapshotButtonPressedAsync()
    {
        if (BrowserSnapshotButton is null)
        {
            return;
        }

        try
        {
            await BrowserSnapshotButton.ScaleToAsync(0.94, 70, Easing.CubicOut);
            await BrowserSnapshotButton.ScaleToAsync(1.0, 110, Easing.CubicOut);
        }
        catch
        {
            BrowserSnapshotButton.Scale = 1.0;
        }
    }

    private void SetSnapshotButtonSavingState(bool isSaving)
    {
        if (BrowserSnapshotButton is null)
        {
            return;
        }

        BrowserSnapshotButton.IsEnabled = !isSaving;
        BrowserSnapshotButton.Text = isSaving ? "Saving..." : "Snapshot";
        BrowserSnapshotButton.BackgroundColor = isSaving
            ? Color.FromArgb("#1F6780")
            : Color.FromArgb("#1220252C");
        BrowserSnapshotButton.TextColor = isSaving
            ? Colors.White
            : Color.FromArgb("#50656F");
    }

    private async Task<string?> ShowSnapshotAnalyzePromptDialogAsync(WebViewPageSnapshotSaveResult result)
    {
#if WINDOWS
        if (MainLayoutGrid.Handler?.PlatformView is UI.FrameworkElement rootElement &&
            rootElement.XamlRoot is not null)
        {
            try
            {
                return await Dispatcher.DispatchAsync(async () =>
                {
                    var promptBox = new UI.Controls.TextBox
                    {
                        Text = result.Prompt,
                        AcceptsReturn = true,
                        TextWrapping = UI.TextWrapping.Wrap,
                        MinHeight = 220,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
                    };

                    var content = new UI.Controls.StackPanel
                    {
                        Spacing = 10
                    };
                    content.Children.Add(new UI.Controls.TextBlock
                    {
                        Text = "방금 저장한 snapshot을 Codex로 분석 시작할까요?",
                        TextWrapping = UI.TextWrapping.Wrap
                    });
                    content.Children.Add(new UI.Controls.TextBlock
                    {
                        Text = $"Folder: {Path.GetFileName(result.DirectoryPath)}{Environment.NewLine}Path: {result.DirectoryPath}",
                        TextWrapping = UI.TextWrapping.Wrap,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
                    });
                    content.Children.Add(promptBox);

                    var dialog = new UI.Controls.ContentDialog
                    {
                        XamlRoot = rootElement.XamlRoot,
                        Title = "Snapshot Saved",
                        Content = content,
                        PrimaryButtonText = "Yes",
                        CloseButtonText = "No",
                        DefaultButton = UI.Controls.ContentDialogButton.Primary
                    };

                    var dialogResult = await dialog.ShowAsync();
                    return dialogResult == UI.Controls.ContentDialogResult.Primary
                        ? promptBox.Text
                        : null;
                });
            }
            catch (Exception ex)
            {
                _naverTalkBrowser.SetStatus($"WinUI dialog failed. Trying native MessageBox: {ex.Message}", _naverTalkBrowser.CurrentUrl);
            }
        }

        if (TryShowNativeSnapshotMessageBox(result, out var shouldAnalyze))
        {
            return shouldAnalyze ? result.Prompt : null;
        }
#endif

        var shouldAnalyzeFallback = await DisplayAlertAsync(
            "Snapshot Saved",
            $"방금 저장한 snapshot을 Codex로 분석 시작할까요?{Environment.NewLine}{Environment.NewLine}Folder: {Path.GetFileName(result.DirectoryPath)}{Environment.NewLine}Path: {result.DirectoryPath}",
            "Yes",
            "No");

        return shouldAnalyzeFallback ? result.Prompt : null;
    }

    private static bool TryShowNativeSnapshotMessageBox(WebViewPageSnapshotSaveResult result, out bool shouldAnalyze)
    {
        shouldAnalyze = false;
#if WINDOWS
        try
        {
            var hwnd = IntPtr.Zero;
            if (TryGetNativeWindow(out var nativeWindow))
            {
                hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
            }

            var message =
                $"방금 저장한 snapshot을 Codex로 분석 시작할까요?{Environment.NewLine}{Environment.NewLine}" +
                $"Folder: {Path.GetFileName(result.DirectoryPath)}{Environment.NewLine}" +
                $"Path: {result.DirectoryPath}";

            var dialogResult = MessageBoxW(
                hwnd,
                message,
                "Snapshot Saved",
                MessageBoxYesNo | MessageBoxIconQuestion | MessageBoxApplicationModal | MessageBoxSetForeground);

            shouldAnalyze = dialogResult == MessageBoxResultYes;
            return dialogResult is MessageBoxResultYes or MessageBoxResultNo;
        }
        catch
        {
            return false;
        }
#else
        return false;
#endif
    }

#if WINDOWS
    private const uint MessageBoxYesNo = 0x00000004;
    private const uint MessageBoxIconQuestion = 0x00000020;
    private const uint MessageBoxApplicationModal = 0x00000000;
    private const uint MessageBoxSetForeground = 0x00010000;
    private const int MessageBoxResultYes = 6;
    private const int MessageBoxResultNo = 7;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(
        IntPtr hWnd,
        string lpText,
        string lpCaption,
        uint uType);
#endif

    private void ShowBrowserSnapshotConfirmModal(WebViewPageSnapshotSaveResult result)
    {
        if (BrowserSnapshotModalTitleLabel is not null)
        {
            BrowserSnapshotModalTitleLabel.Text = "Snapshot Saved";
        }

        if (BrowserSnapshotModalDescriptionLabel is not null)
        {
            BrowserSnapshotModalDescriptionLabel.Text = "방금 저장한 snapshot을 Codex로 분석 시작할까요?";
        }

        if (BrowserSnapshotFolderLabel is not null)
        {
            BrowserSnapshotFolderLabel.Text = $"Folder: {Path.GetFileName(result.DirectoryPath)}";
        }

        if (BrowserSnapshotPathLabel is not null)
        {
            BrowserSnapshotPathLabel.Text = $"Path: {result.DirectoryPath}";
        }

        if (BrowserSnapshotPromptEditor is not null)
        {
            BrowserSnapshotPromptEditor.Text = result.Prompt;
        }

        if (BrowserSnapshotConfirmModal is not null)
        {
            BrowserSnapshotConfirmModal.IsVisible = true;
        }
    }

    private void OnCancelBrowserSnapshotAnalysisClicked(object? sender, EventArgs e)
    {
        if (BrowserSnapshotConfirmModal is not null)
        {
            BrowserSnapshotConfirmModal.IsVisible = false;
        }

        RestoreBrowserWebViewAfterSnapshotModal();

        var pendingChoice = _pendingSnapshotAnalyzeChoice;
        if (pendingChoice is not null)
        {
            _pendingSnapshotUserQuestion = BrowserSnapshotPromptEditor?.Text;
            _pendingSnapshotAnalyzeChoice = null;
            pendingChoice.TrySetResult(false);
        }
    }

    private void OnConfirmBrowserSnapshotAnalysisClicked(object? sender, EventArgs e)
    {
        var snapshot = _pendingWebViewSnapshot;
        var pendingChoice = _pendingSnapshotAnalyzeChoice;
        if (pendingChoice is not null)
        {
            if (BrowserSnapshotConfirmModal is not null)
            {
                BrowserSnapshotConfirmModal.IsVisible = false;
            }

            _pendingSnapshotUserQuestion = BrowserSnapshotPromptEditor?.Text;
            _pendingSnapshotAnalyzeChoice = null;
            pendingChoice.TrySetResult(true);
            return;
        }

        if (snapshot is null)
        {
            _naverTalkBrowser.SetStatus("분석할 snapshot 정보가 없습니다.", _naverTalkBrowser.CurrentUrl);
            return;
        }

        if (BrowserSnapshotConfirmModal is not null)
        {
            BrowserSnapshotConfirmModal.IsVisible = false;
        }

        RestoreBrowserWebViewAfterSnapshotModal();

        ApplyWorkspaceTab(WorkspaceTab.Codex);
        var prompt = BrowserSnapshotPromptEditor?.Text;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = snapshot.Prompt;
        }

        PublishSnapshotPromptToCodex(snapshot, prompt);
    }

    private static string BuildSnapshotPromptWithUserQuestion(WebViewPageSnapshotSaveResult snapshot, string? userQuestion)
    {
        if (string.IsNullOrWhiteSpace(userQuestion))
        {
            return snapshot.Prompt;
        }

        return snapshot.Prompt +
            Environment.NewLine +
            Environment.NewLine +
            "추가 질문:" +
            Environment.NewLine +
            userQuestion.Trim();
    }

    private void PublishSnapshotPromptToCodex(WebViewPageSnapshotSaveResult snapshot, string? promptOverride = null)
    {
        ApplyWorkspaceTab(WorkspaceTab.Codex);
        _codexPromptMacroService.PublishPromptRequested(string.IsNullOrWhiteSpace(promptOverride) ? snapshot.Prompt : promptOverride);
        _naverTalkBrowser.SetStatus($"Codex 분석 프롬프트 생성 완료: {Path.GetFileName(snapshot.DirectoryPath)}");
    }

    private async void OnSendBrowserRequestClicked(object? sender, EventArgs e)
    {
        var url = BrowserRequestUrlEntry?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            SetBrowserRequestStatus("요청 URL을 입력해 주세요.", isError: true);
            return;
        }

        var method = BrowserRequestMethodEntry?.Text;
        if (string.IsNullOrWhiteSpace(method))
        {
            method = "POST";
        }

        try
        {
            SetBrowserRequestStatus("요청을 보내는 중입니다...", isError: false);
            if (BrowserRequestResultLabel is not null)
            {
                BrowserRequestResultLabel.Text = "-";
            }

            var script = WebViewFetchRequestScriptBuilder.Build(url, method, BrowserRequestBodyEditor?.Text);
            var raw = await RunBrowserScriptAsync(script);
            var result = ParseScriptJson<BrowserRequestResult>(raw);
            if (result is null)
            {
                SetBrowserRequestStatus("요청 결과를 해석하지 못했습니다.", isError: true);
                if (BrowserRequestResultLabel is not null)
                {
                    BrowserRequestResultLabel.Text = TrimForLog(raw, 4000);
                }

                return;
            }

            var statusText = result.Status > 0
                ? $"{result.Status} {result.StatusText}".Trim()
                : result.StatusText;
            var message = result.Ok
                ? $"요청 성공: {statusText}"
                : $"요청 실패: {statusText}";
            SetBrowserRequestStatus(message, isError: !result.Ok);

            if (BrowserRequestResultLabel is not null)
            {
                BrowserRequestResultLabel.Text =
                    $"URL: {result.Url}{Environment.NewLine}" +
                    $"Status: {statusText}{Environment.NewLine}{Environment.NewLine}" +
                    TrimForLog(result.Body, 4000);
            }
        }
        catch (Exception ex)
        {
            SetBrowserRequestStatus($"요청 실행 실패: {ex.Message}", isError: true);
        }
    }

    private async void OnApplyCookieClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        try
        {
            if (!TryResolveCookieTargetUri(out var targetOrigin, out var targetUri))
            {
                SetCookieImportStatus("쿠키를 넣을 도메인을 먼저 입력하세요.", isError: true);
                return;
            }

            var cookiePairs = ParseCookieHeader(CookieHeaderEditor?.Text).ToList();
            if (cookiePairs.Count == 0)
            {
                SetCookieImportStatus("붙여넣은 쿠키 문자열에서 유효한 name=value 항목을 찾지 못했습니다.", isError: true);
                return;
            }

            var coreWebView2 = await TryEnsureCoreWebView2Async();
            if (coreWebView2 is null)
            {
                SetCookieImportStatus("WebView2가 아직 준비되지 않았습니다. 페이지를 한 번 연 뒤 다시 시도하세요.", isError: true);
                return;
            }

            var imported = 0;
            var failed = 0;
            var cookieManager = coreWebView2.CookieManager;
            foreach (var (name, value) in cookiePairs)
            {
                try
                {
                    var cookie = cookieManager.CreateCookie(name, value, targetUri.Host, "/");
                    cookie.IsSecure = string.Equals(targetUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
                    cookieManager.AddOrUpdateCookie(cookie);
                    imported++;
                }
                catch
                {
                    failed++;
                }
            }

            SetCookieImportStatus(
                failed == 0
                    ? $"{targetUri.Host}에 쿠키 {imported}개를 적용했습니다. 필요하면 페이지를 새로고침하세요."
                    : $"{targetUri.Host}에 쿠키 {imported}개 적용, {failed}개 실패했습니다.",
                isError: failed > 0);

            if (BrowserAddressEntry is not null)
            {
                BrowserAddressEntry.Text = targetOrigin;
            }

            NaverTalkWebView.Reload();
        }
        catch (Exception ex)
        {
            SetCookieImportStatus($"쿠키 적용 실패: {ex.Message}", isError: true);
        }
#else
        SetCookieImportStatus("쿠키 주입은 Windows WebView2에서만 지원됩니다.", isError: true);
#endif
    }

    private async void OnGetCurrentCookiesClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        try
        {
            if (!TryResolveCookieTargetUri(out var targetOrigin, out var targetUri))
            {
                SetCookieImportStatus("쿠키를 가져올 도메인을 먼저 입력하거나 페이지에 접속하세요.", isError: true);
                return;
            }

            var coreWebView2 = await TryEnsureCoreWebView2Async();
            if (coreWebView2 is null)
            {
                SetCookieImportStatus("WebView2가 아직 준비되지 않았습니다. 페이지를 한 번 연 뒤 다시 시도하세요.", isError: true);
                return;
            }

            var cookies = await coreWebView2.CookieManager.GetCookiesAsync(targetOrigin);
            var cookieHeader = string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
            if (CookieHeaderEditor is not null)
            {
                CookieHeaderEditor.Text = cookieHeader;
            }

            SetCookieImportStatus(
                cookies.Count == 0
                    ? $"{targetUri.Host}에서 가져올 쿠키가 없습니다."
                    : $"{targetUri.Host} 쿠키 {cookies.Count}개를 문자열로 가져왔습니다.",
                isError: false);
        }
        catch (Exception ex)
        {
            SetCookieImportStatus($"쿠키 가져오기 실패: {ex.Message}", isError: true);
        }
#else
        SetCookieImportStatus("쿠키 가져오기는 Windows WebView2에서만 지원됩니다.", isError: true);
#endif
    }

    private async void OnCopyCookiesClicked(object? sender, EventArgs e)
    {
        var cookieHeader = CookieHeaderEditor?.Text;
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            SetCookieImportStatus("복사할 쿠키 문자열이 없습니다.", isError: true);
            return;
        }

        try
        {
            await Clipboard.Default.SetTextAsync(cookieHeader.Trim());
            SetCookieImportStatus("쿠키 문자열을 클립보드에 복사했습니다.", isError: false);
        }
        catch (Exception ex)
        {
            SetCookieImportStatus($"클립보드 복사 실패: {ex.Message}", isError: true);
        }
    }

    private bool TryResolveCookieTargetUri(out string targetOrigin, out Uri targetUri)
    {
        targetOrigin = NormalizeCookieTargetOrigin(CookieDomainEntry?.Text) ?? TryResolveCurrentBrowserOrigin() ?? string.Empty;
        if (Uri.TryCreate(targetOrigin, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            targetUri = uri;
            if (CookieDomainEntry is not null)
            {
                CookieDomainEntry.Text = targetOrigin;
            }

            return true;
        }

        targetUri = null!;
        return false;
    }

    private void SetCookieImportStatus(string message, bool isError)
    {
        if (CookieImportStatusLabel is not null)
        {
            CookieImportStatusLabel.Text = message;
            CookieImportStatusLabel.TextColor = isError
                ? Color.FromArgb("#B35A4A")
                : Color.FromArgb("#2D8A63");
        }

        _naverTalkBrowser.SetStatus(message, _naverTalkBrowser.CurrentUrl);
    }

    private void SetBrowserRequestStatus(string message, bool isError)
    {
        if (BrowserRequestStatusLabel is not null)
        {
            BrowserRequestStatusLabel.Text = message;
            BrowserRequestStatusLabel.TextColor = isError
                ? Color.FromArgb("#B35A4A")
                : Color.FromArgb("#2D8A63");
        }

        _naverTalkBrowser.SetStatus(message, _naverTalkBrowser.CurrentUrl);
    }

    private string? TryResolveCurrentBrowserOrigin()
    {
        var candidates = new[]
        {
            _naverTalkBrowser.CurrentUrl,
            BrowserAddressEntry?.Text,
            BrowserUrlLabel?.Text
        };

        foreach (var candidate in candidates)
        {
            var normalized = NormalizeCookieTargetOrigin(candidate);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private static string? NormalizeCookieTargetOrigin(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var normalizedUrl = NormalizeAddressInput(input);
        if (string.IsNullOrWhiteSpace(normalizedUrl) ||
            !Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        return uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    private static IEnumerable<(string Name, string Value)> ParseCookieHeader(string? cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            yield break;
        }

        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var name = segment[..separatorIndex].Trim();
            var value = segment[(separatorIndex + 1)..].Trim();
            if (!IsValidCookieName(name))
            {
                continue;
            }

            cookies[name] = value.Trim('"');
        }

        foreach (var cookie in cookies)
        {
            yield return (cookie.Key, cookie.Value);
        }
    }

    private static bool IsValidCookieName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        const string separators = "()<>@,;:\\\"/[]?={} \t";
        return name.All(ch => ch > 0x20 && ch < 0x7f && !separators.Contains(ch, StringComparison.Ordinal));
    }

    private async Task TypeTextIntoNaverTalkAsync(string? text, bool submit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _naverTalkBrowser.SetStatus("Typing macro text is empty.");
            return;
        }

        try
        {
#if WINDOWS
            if (NaverTalkWebView.Handler?.PlatformView is UI.Controls.WebView2 platformWebView)
            {
                //platformWebView.Focus(UI.FocusState.Programmatic);
            }
            var insertedByJs = await InsertTextIntoNaverTalkByJavaScriptAsync(text, submit);
            if (insertedByJs)
            {
                _naverTalkBrowser.SetStatus(submit ? "Typing macro inserted by WebView JS and sent." : "Typing macro inserted by WebView JS.");
                return;
            }

            ReleasePressedAltKeys();
            await Task.Delay(40);

            var pasted = await TryPasteTextIntoFocusedTargetAsync(text, submit);
            if (pasted)
            {
                _naverTalkBrowser.SetStatus(submit ? "Typing macro pasted by clipboard." : "Typing macro pasted by clipboard.");
                return;
            }

            var typed = SendUnicodeText(text);
            var submitted = !submit || SendEnterKey();

            if (typed && submitted)
            {
                _naverTalkBrowser.SetStatus(submit ? "Typing macro sent by SendInput." : "Typing macro typed by SendInput.");
                return;
            }

            var devToolsTyped = await TryTypeTextIntoNaverTalkByDevToolsAsync(text, submit);
            _naverTalkBrowser.SetStatus(devToolsTyped
                ? (submit ? "Typing macro sent by WebView2 DevTools fallback." : "Typing macro typed by WebView2 DevTools fallback.")
                : "Typing macro failed: SendInput/WebView2 DevTools both failed.");
#else
            _naverTalkBrowser.SetStatus("Typing macro failed: SendInput is only available on Windows.");
#endif
        }
        catch (Exception ex)
        {
            _naverTalkBrowser.SetStatus($"Typing macro failed: {ex.Message}");
        }
    }

    private static async Task<bool> TryPasteTextIntoFocusedTargetAsync(string text, bool submit)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string? previousClipboard = null;
        var clipboardCaptured = false;
        try
        {
            previousClipboard = await Clipboard.Default.GetTextAsync();
            clipboardCaptured = true;
        }
        catch
        {
            // 일부 환경에서는 기존 클립보드 읽기를 막을 수 있습니다.
        }

        try
        {
            await Clipboard.Default.SetTextAsync(text);
            await Task.Delay(30);

            var pasted = SendCtrlVKey();
            var submitted = !submit || SendEnterKey();
            return pasted && submitted;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (clipboardCaptured)
            {
                try
                {
                    await Clipboard.Default.SetTextAsync(previousClipboard ?? string.Empty);
                }
                catch
                {
                    // 복원 실패 시 무시
                }
            }
        }
    }

    private async Task<bool> FocusNaverTalkTypingTargetAsync()
    {
#if WINDOWS
        if (NaverTalkWebView.Handler?.PlatformView is UI.Controls.WebView2 platformWebView)
        {
            platformWebView.Focus(UI.FocusState.Programmatic);
        }
#endif

        var focusScript = """
(() => {
    const isTypingTarget = (el) => !!el &&
        (el.tagName === 'TEXTAREA' ||
         (el.tagName === 'INPUT' && el.type !== 'password') ||
         el.isContentEditable);

    if (!window.__codexTypingFocusTrackerAttached) {
        window.__codexTypingFocusTrackerAttached = true;
        document.addEventListener('focusin', (e) => {
            const focused = e.target;
            if (isTypingTarget(focused)) {
                window.__codexLastTypingTarget = focused;
            }
        }, true);
    }

    let target = document.activeElement;
    if (!isTypingTarget(target)) {
        const remembered = window.__codexLastTypingTarget;
        if (isTypingTarget(remembered) && document.contains(remembered)) {
            target = remembered;
        }
    }

    if (!isTypingTarget(target)) {
        const candidates = [
            'div[role="textbox"][contenteditable="true"]',
            '[contenteditable="true"][data-testid*="chat"]',
            '[contenteditable="true"][aria-label*="메시지"]',
            '[contenteditable="true"][aria-label*="message"]',
            'textarea',
            'input[type="text"]',
            'input[type="search"]',
            '[contenteditable="true"]'
        ];

        for (const selector of candidates) {
            const found = document.querySelector(selector);
            if (isTypingTarget(found)) {
                target = found;
                break;
            }
        }
    }

    if (!isTypingTarget(target)) {
        return JSON.stringify({ success: false });
    }

    target.focus();
    window.__codexLastTypingTarget = target;
    return JSON.stringify({ success: true, tag: target.tagName });
})();
""";

        var result = await RunBrowserScriptAsync(focusScript);
        return !string.IsNullOrWhiteSpace(result) && result.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> InsertTextIntoNaverTalkByJavaScriptAsync(string text, bool submit)
    {
        var textJson = JsonSerializer.Serialize(text);
        var script = $$"""
(() => {
    const text = {{textJson}};
    if (!text) return JSON.stringify({ success: false });

    const isTypingTarget = (el) => !!el &&
        (el.tagName === 'TEXTAREA' ||
         (el.tagName === 'INPUT' && el.type !== 'password') ||
         el.isContentEditable);

    let target = document.activeElement;
    if (!isTypingTarget(target)) target = window.__codexLastTypingTarget;
    if (!isTypingTarget(target)) {
        target = document.querySelector('div[role="textbox"][contenteditable="true"], textarea, input[type="text"], input[type="search"], [contenteditable="true"]');
    }
    if (!isTypingTarget(target)) return JSON.stringify({ success: false });

    target.focus();
    window.__codexLastTypingTarget = target;

    if (target.isContentEditable) {
        const sel = window.getSelection?.();
        const range = sel && sel.rangeCount > 0 ? sel.getRangeAt(0) : null;
        if (range && target.contains(range.startContainer)) {
            range.deleteContents();
            const node = document.createTextNode(text);
            range.insertNode(node);
            range.setStartAfter(node);
            range.setEndAfter(node);
            sel.removeAllRanges();
            sel.addRange(range);
        } else {
            target.textContent = (target.textContent ?? '') + text;
        }
    } else {
        const start = typeof target.selectionStart === 'number' ? target.selectionStart : (target.value ?? '').length;
        const end = typeof target.selectionEnd === 'number' ? target.selectionEnd : start;
        const current = target.value ?? '';
        target.value = current.slice(0, start) + text + current.slice(end);
        const next = start + text.length;
        if (typeof target.setSelectionRange === 'function') target.setSelectionRange(next, next);
    }

    target.dispatchEvent(new Event('input', { bubbles: true }));
    target.dispatchEvent(new Event('change', { bubbles: true }));

    const submitNow = {{submit.ToString().ToLowerInvariant()}};
    if (submitNow) {
        const down = new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true });
        const up = new KeyboardEvent('keyup', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true });
        target.dispatchEvent(down);
        target.dispatchEvent(up);
        target.form?.requestSubmit?.();
    }

    return JSON.stringify({ success: true });
})();
""";

        var result = await RunBrowserScriptAsync(script);
        return !string.IsNullOrWhiteSpace(result) && result.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase);
    }

    #if WINDOWS
    private async Task<bool> TryTypeTextIntoNaverTalkByDevToolsAsync(string text, bool submit)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var coreWebView2 = await TryEnsureCoreWebView2Async();
        if (coreWebView2 is null)
        {
            return false;
        }

        try
        {
            await coreWebView2.CallDevToolsProtocolMethodAsync(
                "Input.insertText",
                JsonSerializer.Serialize(new { text }));

            if (submit)
            {
                var enterKeyDown = JsonSerializer.Serialize(new
                {
                    type = "keyDown",
                    key = "Enter",
                    code = "Enter",
                    windowsVirtualKeyCode = 13
                });
                var enterKeyUp = JsonSerializer.Serialize(new
                {
                    type = "keyUp",
                    key = "Enter",
                    code = "Enter",
                    windowsVirtualKeyCode = 13
                });

                await coreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", enterKeyDown);
                await coreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", enterKeyUp);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
    #endif

#if WINDOWS
    private static bool SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(CreateUnicodeKeyInput(ch, keyUp: false));
            inputs.Add(CreateUnicodeKeyInput(ch, keyUp: true));
        }

        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        return sent == inputs.Count;
    }

    private static bool SendEnterKey()
    {
        var down = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = VK_RETURN,
                    wScan = 0,
                    dwFlags = 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        var up = down;
        up.U.ki.dwFlags = KEYEVENTF_KEYUP;

        var inputs = new[] { down, up };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return sent == inputs.Length;
    }

    private static bool SendCtrlVKey()
    {
        var ctrlDown = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = VK_CONTROL,
                    wScan = 0,
                    dwFlags = 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        var vDown = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = VK_V,
                    wScan = 0,
                    dwFlags = 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        var vUp = vDown;
        vUp.U.ki.dwFlags = KEYEVENTF_KEYUP;

        var ctrlUp = ctrlDown;
        ctrlUp.U.ki.dwFlags = KEYEVENTF_KEYUP;

        var inputs = new[] { ctrlDown, vDown, vUp, ctrlUp };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return sent == inputs.Length;
    }

    private static void ReleasePressedAltKeys()
    {
        if (IsVirtualKeyDown(VK_MENU) || IsVirtualKeyDown(VK_LMENU) || IsVirtualKeyDown(VK_RMENU))
        {
            SendVirtualKeyUp((ushort)VK_MENU);
            SendVirtualKeyUp((ushort)VK_LMENU);
            SendVirtualKeyUp((ushort)VK_RMENU);
        }
    }

    private static void SendVirtualKeyUp(ushort virtualKey)
    {
        var keyUp = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        _ = SendInput(1, new[] { keyUp }, Marshal.SizeOf<INPUT>());
    }

    private static INPUT CreateUnicodeKeyInput(char ch, bool keyUp)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_RETURN = 0x0D;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
#endif

    private void OnNaverTalkNavigating(object? sender, WebNavigatingEventArgs e)
    {
        var nextUrl = string.IsNullOrWhiteSpace(e.Url) ? _naverTalkBrowser.CurrentUrl : e.Url;
        _naverTalkBrowser.SetStatus("Opening page.", nextUrl);
    }

    private async void OnNaverTalkNavigated(object? sender, WebNavigatedEventArgs e)
    {
        var currentUrl = string.IsNullOrWhiteSpace(e.Url) ? _naverTalkBrowser.CurrentUrl : e.Url;

        if (e.Result != WebNavigationResult.Success)
        {
            _naverTalkBrowser.SetStatus($"Page load failed: {e.Result}", currentUrl);
            return;
        }

        _naverTalkBrowser.SetStatus("The WebView page is open.", currentUrl);

        await RefreshWebViewLayoutMetricsAsync();
        await ApplyWebViewInspectorModeAsync(_webViewInspectorModeEnabled);
    }

    private void OnBrowserPaneSizeChanged(object? sender, EventArgs e)
    {
#if WINDOWS
        ApplyAdaptiveWebViewZoom();
#endif
    }

    private async Task HandleLogoutPopupAsync(string currentUrl)
    {
        try
        {
            var script = _naverTalkBrowser.GetLogoutPopupCloseScript();
            var result = await RunBrowserScriptAsync(script);

            if (!string.IsNullOrWhiteSpace(result) && result.Contains("\"closed\":true"))
            {
                _naverTalkBrowser.SetStatus("Closed logout popup. Attempting to re-login.", currentUrl);

                // 팝업 닫은 후 잠시 대기 후 로그인 시도
                await Task.Delay(500);
                await _naverTalkBrowser.NavigateAsync(forceLogin: true);
            }
        }
        catch (Exception ex)
        {
            _naverTalkBrowser.SetStatus($"Logout popup handler failed: {ex.Message}", currentUrl);
        }
    }

    private async Task AttemptAutoLoginAsync(string? currentUrl, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(currentUrl))
        {
            return;
        }

        if (!force && !_naverTalkBrowser.CanAutoLoginAtUrl(currentUrl))
        {
            return;
        }

        if (!force && WasRecentAutoLoginAttempt(currentUrl))
        {
            return;
        }

        var scripts = await _naverTalkBrowser.BuildAutoLoginScriptsIfAvailableAsync(currentUrl, force);
        if (scripts is null)
        {
            if (IsLoginPage(currentUrl))
            {
                _naverTalkBrowser.SetStatus("Auto-login needs a saved id and password.", currentUrl);
            }

            return;
        }

        RememberAutoLoginAttempt(currentUrl);

        try
        {
            await Task.Delay(1000);

            // Step 1: ID 입력
            _naverTalkBrowser.SetStatus("Step 1: Entering login ID...", currentUrl);
            var step1Result = await RunBrowserScriptAsync(scripts.Step1Script);

            await Task.Delay(500);

            // Step 2: 비밀번호 입력
            _naverTalkBrowser.SetStatus("Step 2: Entering password...", currentUrl);
            var step2Result = await RunBrowserScriptAsync(scripts.Step2Script);

            await Task.Delay(500);

            // Step 3: 비밀번호 필드 클릭하여 포커스 이동
            _naverTalkBrowser.SetStatus("Step 3: Focusing password field...", currentUrl);
            var step3Result = await RunBrowserScriptAsync(scripts.Step3Script);

            await Task.Delay(500);

            // Step 4: 로그인 버튼 클릭
            _naverTalkBrowser.SetStatus("Step 4: Clicking login button...", currentUrl);
            var step4Result = await RunBrowserScriptAsync(scripts.Step4Script);

            _naverTalkBrowser.SetStatus("Auto-login completed. Finish any extra verification manually.", currentUrl);
        }
        catch (Exception ex)
        {
            _naverTalkBrowser.SetStatus($"Auto-login failed: {ex.Message}", currentUrl);
        }
    }

}


