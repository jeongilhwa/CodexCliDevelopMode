namespace CodexDesk;

using CodexDesk.Services;
using InterceptKit.Core;
using InterceptKit.Presentation;
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
    private const int WebViewNetworkMaxPostDataPreviewBytes = 256 * 1024;

    private void OnBrowserWebViewHandlerChanged(object? sender, EventArgs e)
    {
        ConfigureWebView2ForWindows();
    }

    private void ConfigureWebView2ForWindows()
    {
        try
        {
            var handler = BrowserWebView.Handler as Microsoft.Maui.Handlers.WebViewHandler;
            if (handler?.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 webView2)
            {
                webView2.CoreWebView2Initialized -= OnCoreWebView2Initialized;
                webView2.CoreWebView2Initialized += OnCoreWebView2Initialized;
                if (webView2.CoreWebView2 is not null)
                {
                    _ = EnsureWebViewInterceptAttachedAsync(webView2.CoreWebView2);
                    AttachWebViewNewWindowHandler(webView2.CoreWebView2);
                    AttachWebViewKeyboardDebugHandlers(webView2.CoreWebView2);
                    _ = EnsureWebViewInspectorScriptRegisteredAsync(webView2.CoreWebView2);
                    _ = ApplyWebViewInspectorModeAsync(_webViewInspectorModeEnabled);
                    _ = ApplyWebViewNetworkCaptureModeAsync(_webViewNetworkCaptureEnabled);
                    _ = TryRestoreSavedDomainCookieSnapshotsAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _browserState.SetStatus($"Cookie configuration failed: {ex.Message}");
        }
    }

    private void OnCoreWebView2Initialized(Microsoft.UI.Xaml.Controls.WebView2 sender, Microsoft.UI.Xaml.Controls.CoreWebView2InitializedEventArgs args)
    {
        if (sender.CoreWebView2 is null)
        {
            return;
        }

        _ = EnsureWebViewInterceptAttachedAsync(sender.CoreWebView2);
        _browserState.SetStatus("WebView2 is ready.");
        AttachWebViewNewWindowHandler(sender.CoreWebView2);
        AttachWebViewKeyboardDebugHandlers(sender.CoreWebView2);
        _ = EnsureWebViewInspectorScriptRegisteredAsync(sender.CoreWebView2);
        _ = ApplyWebViewInspectorModeAsync(_webViewInspectorModeEnabled);
        _ = ApplyWebViewNetworkCaptureModeAsync(_webViewNetworkCaptureEnabled);
        _ = TryRestoreSavedDomainCookieSnapshotsAsync();
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
            var target = string.IsNullOrWhiteSpace(e.Uri) ? _browserState.CurrentUrl : e.Uri;
            e.Handled = true;
            _ = MainThread.InvokeOnMainThreadAsync(async () =>
            {
                await _browserState.NavigateAsync(target);
                _browserState.SetStatus("New window request was opened in the current WebView.", target);
            });
        }
        catch (Exception ex)
        {
            _browserState.SetStatus($"Failed to handle new-window request: {ex.Message}", _browserState.CurrentUrl);
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
                _browserState.SetStatus(status, _browserState.CurrentUrl);
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
            ++_webViewNetworkSequence,
            summary,
            request,
            response,
            method,
            url,
            status,
            durationMs,
            encodedDataLength,
            resourceType,
            mimeType);
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
            _browserState.SetStatus(
                _webViewInspectorModeEnabled
                    ? "Focus mode turned on."
                    : "Focus mode turned off.",
                _browserState.CurrentUrl);
        }
        catch (Exception ex)
        {
            _browserState.SetStatus($"Focus mode toggle failed: {ex.Message}", _browserState.CurrentUrl);
        }
    }

    private async Task ToggleWebViewNetworkCaptureModeAsync()
    {
        try
        {
            _webViewNetworkCaptureEnabled = !_webViewNetworkCaptureEnabled;
            UpdateWebViewNetworkCaptureToggleButton();
            await ApplyWebViewNetworkCaptureModeAsync(_webViewNetworkCaptureEnabled);
            _browserState.SetStatus(
                _webViewNetworkCaptureEnabled
                    ? "WebView network capture turned on."
                    : "WebView network capture turned off.",
                _browserState.CurrentUrl);
        }
        catch (Exception ex)
        {
            _browserState.SetStatus($"Network capture toggle failed: {ex.Message}", _browserState.CurrentUrl);
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
                await coreWebView2.CallDevToolsProtocolMethodAsync("Network.enable", BuildNetworkEnableArgs());
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
            if (string.IsNullOrWhiteSpace(captured.RequestBodyPreview))
            {
                captured.RequestBodyPreview = await TryGetRequestPostDataPreviewAsync(captured.RequestId);
            }

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

    private async Task<string> TryGetRequestPostDataPreviewAsync(string requestId)
    {
        var coreWebView2 = _networkReceiversAttachedCore;
        if (coreWebView2 is null || string.IsNullOrWhiteSpace(requestId))
        {
            return string.Empty;
        }

        try
        {
            var args = JsonSerializer.Serialize(new { requestId });
            var raw = await coreWebView2.CallDevToolsProtocolMethodAsync("Network.getRequestPostData", args);
            var payload = JsonNode.Parse(raw)?.AsObject();
            return payload?["postData"]?.GetValue<string>() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildNetworkEnableArgs()
    {
        return JsonSerializer.Serialize(new
        {
            maxPostDataSize = WebViewNetworkMaxPostDataPreviewBytes
        });
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
        return WebViewNetworkHeaderPreviewSupport.ToHeaderLines(headersNode);
    }

    private static string MergeHeaderPreviews(string? primary, string? secondary)
    {
        return WebViewNetworkHeaderPreviewSupport.MergeHeaderPreviews(primary, secondary);
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
            _browserState.SetStatus($"WebView2 initialization failed: {ex.Message}");
        }

        SetupWindowKeyboardHook();
#endif

        await _browserState.EnsureInitializedAsync();
        UpdateBrowserChrome();
        await _browserState.NavigateAsync("https://www.google.com");
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _browserState.StateChanged -= OnBrowserStateChanged;
        _browserState.CommandRequested -= OnBrowserCommandRequested;
        _webViewInterceptPanelState.Changed -= OnWebViewInterceptPanelChanged;
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

    private void OnBrowserWebViewFocused(object? sender, FocusEventArgs e)
    {
        _isBrowserWebViewFocused = true;
    }

    private void OnBrowserWebViewUnfocused(object? sender, FocusEventArgs e)
    {
        _isBrowserWebViewFocused = false;
    }

    private void OnBrowserCommandRequested(BrowserCommand command)
    {
        _ = MainThread.InvokeOnMainThreadAsync(() =>
        {
            switch (command.Type)
            {
                case BrowserCommandType.Navigate:
                    if (!string.IsNullOrWhiteSpace(command.Url))
                    {
                        BrowserWebView.Source = new UrlWebViewSource { Url = command.Url };
                    }
                    break;

                case BrowserCommandType.Reload:
                    BrowserWebView.Reload();
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

    private void OnBrowserAddressEntryFocused(object? sender, FocusEventArgs e)
    {
        _isBrowserAddressEntryFocused = true;
    }

    private void OnBrowserAddressEntryUnfocused(object? sender, FocusEventArgs e)
    {
        _isBrowserAddressEntryFocused = false;
        UpdateBrowserChrome();
    }

    private void OnWebViewInterceptPanelChanged(object? sender, EventArgs e)
    {
        _ = MainThread.InvokeOnMainThreadAsync(UpdateWebViewInterceptPanel);
    }

    private void UpdateWebViewInterceptPanel()
    {
        var pendingItems = _webViewInterceptPanelState.GetPendingItems();
        if (InterceptStatusLabel is not null)
        {
            InterceptStatusLabel.Text = _webViewInterceptPanelState.StatusText;
        }

        if (InterceptDetailLabel is not null)
        {
            InterceptDetailLabel.Text = _webViewInterceptPanelState.DetailText;
        }

        if (InterceptCountLabel is not null)
        {
            InterceptCountLabel.Text = _webViewInterceptPanelState.CountText;
        }

        if (InterceptRailCountLabel is not null)
        {
            InterceptRailCountLabel.Text = pendingItems.Count.ToString(CultureInfo.InvariantCulture);
        }

        if (InterceptRailCountBadge is not null)
        {
            InterceptRailCountBadge.IsVisible = pendingItems.Count > 0;
        }

        if (InterceptToggleButton is not null)
        {
            InterceptToggleButton.Text = _webViewInterceptPanelState.ToggleButtonText;
            InterceptToggleButton.BackgroundColor = _webViewInterceptPanelState.IsEnabled
                ? Color.FromArgb("#1F6780")
                : Color.FromArgb("#EEF3F6");
            InterceptToggleButton.TextColor = _webViewInterceptPanelState.IsEnabled
                ? Colors.White
                : Color.FromArgb("#31474F");
        }

        if (WebBrowserTabCountLabel is not null)
        {
            WebBrowserTabCountLabel.Text = _webViewInterceptPanelState.CountText;
        }

        if (InterceptQueueCollection is not null)
        {
            InterceptQueueCollection.ItemsSource = pendingItems;
            InterceptQueueCollection.SelectedItem = pendingItems.FirstOrDefault(item => item.SessionId == _webViewInterceptPanelState.SelectedSessionId);
        }

        ApplyInterceptQueuePanelState();
        ApplySelectedInterceptSession(_webViewInterceptPanelState.GetSelectedSession());
    }

    private void OnInterceptSideToggleClicked(object? sender, EventArgs e)
    {
        _isInterceptQueuePanelOpen = !_isInterceptQueuePanelOpen;
        ApplyInterceptQueuePanelState();
    }

    private void ApplyInterceptQueuePanelState()
    {
        if (InterceptQueuePanelColumn is not null)
        {
            InterceptQueuePanelColumn.Width = _isInterceptQueuePanelOpen
                ? new GridLength(InterceptQueuePanelWidth)
                : new GridLength(0);
        }

        if (InterceptQueuePanel is not null)
        {
            InterceptQueuePanel.IsVisible = _isInterceptQueuePanelOpen;
        }

        if (InterceptSideToggleButton is not null)
        {
            InterceptSideToggleButton.Text = _isInterceptQueuePanelOpen ? "<" : ">";
            InterceptSideToggleButton.BackgroundColor = _isInterceptQueuePanelOpen
                ? Color.FromArgb("#E2EFF2")
                : Color.FromArgb("#00FFFFFF");
            InterceptSideToggleButton.TextColor = _isInterceptQueuePanelOpen
                ? Color.FromArgb("#1F6780")
                : Color.FromArgb("#50656F");
        }
    }

    private async void OnInterceptToggleClicked(object? sender, EventArgs e)
    {
        try
        {
            await _webViewInterceptController.SetEnabledAsync(!_webViewInterceptPanelState.IsEnabled);
            UpdateWebViewInterceptPanel();
        }
        catch (Exception ex)
        {
            _browserState.SetStatus($"Intercept toggle failed: {ex.Message}", _browserState.CurrentUrl);
        }
    }

    private void OnInterceptQueueSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is InterceptQueueItemViewModel selected)
        {
            _webViewInterceptPanelState.SelectSession(selected.SessionId);
            ApplySelectedInterceptSession(_webViewInterceptPanelState.GetSelectedSession());
            return;
        }

        ApplySelectedInterceptSession(null);
    }

    private async void OnInterceptForwardClicked(object? sender, EventArgs e)
    {
        try
        {
            await _webViewInterceptPanelState.ForwardSelectedAsync();
            UpdateWebViewInterceptPanel();
        }
        catch (Exception ex)
        {
            _browserState.SetStatus($"Intercept forward failed: {ex.Message}", _browserState.CurrentUrl);
        }
    }

    private async void OnInterceptForwardAllClicked(object? sender, EventArgs e)
    {
        try
        {
            _webViewInterceptController.BeginTemporaryPassThrough(TimeSpan.FromSeconds(8));
            await _webViewInterceptPanelState.ForwardAllAsync();
            UpdateWebViewInterceptPanel();
        }
        catch (Exception ex)
        {
            _browserState.SetStatus($"Intercept forward-all failed: {ex.Message}", _browserState.CurrentUrl);
        }
    }

    private async void OnInterceptDropClicked(object? sender, EventArgs e)
    {
        try
        {
            await _webViewInterceptPanelState.DropSelectedAsync();
            UpdateWebViewInterceptPanel();
        }
        catch (Exception ex)
        {
            _browserState.SetStatus($"Intercept drop failed: {ex.Message}", _browserState.CurrentUrl);
        }
    }

    private async void OnCopyInterceptSummaryClicked(object? sender, EventArgs e)
    {
        var summaryText = _webViewInterceptPanelState.SelectedSummaryText;
        if (string.IsNullOrWhiteSpace(summaryText) || summaryText == "선택된 요청 없음")
        {
            _browserState.SetStatus("복사할 요청 요약이 없습니다.", _browserState.CurrentUrl);
            return;
        }

        try
        {
            await Clipboard.Default.SetTextAsync(summaryText);
            _browserState.SetStatus("요청 Method / URL을 클립보드에 복사했습니다.", _browserState.CurrentUrl);
        }
        catch (Exception ex)
        {
            _browserState.SetStatus($"요청 요약 복사 실패: {ex.Message}", _browserState.CurrentUrl);
        }
    }

    private void ApplySelectedInterceptSession(InterceptSession? session)
    {
        if (InterceptSelectedSummaryLabel is not null)
        {
            InterceptSelectedSummaryLabel.Text = _webViewInterceptPanelState.SelectedSummaryText;
        }

        if (InterceptSelectedHeadersEditor is not null)
        {
            InterceptSelectedHeadersEditor.Text = session is null
                ? string.Empty
                : FormatInterceptHeaders(session.OriginalRequest.Headers);
        }

        if (InterceptSelectedBodyEditor is not null)
        {
            InterceptSelectedBodyEditor.Text = session?.OriginalRequest.Body ?? string.Empty;
        }

        if (InterceptForwardButton is not null)
        {
            InterceptForwardButton.IsEnabled = session is not null;
        }

        if (InterceptDropButton is not null)
        {
            InterceptDropButton.IsEnabled = session is not null;
        }

        if (InterceptForwardAllButton is not null)
        {
            InterceptForwardAllButton.IsEnabled = _webViewInterceptPanelState.GetPendingItems().Count > 0;
        }

        if (InterceptCopySummaryButton is not null)
        {
            InterceptCopySummaryButton.IsEnabled = session is not null;
        }
    }

#if WINDOWS
    private async Task EnsureWebViewInterceptAttachedAsync(CoreWebView2 coreWebView2)
    {
        try
        {
            await _webViewInterceptController.AttachAsync(coreWebView2);
            await MainThread.InvokeOnMainThreadAsync(UpdateWebViewInterceptPanel);
        }
        catch (Exception ex)
        {
            _browserState.SetStatus($"WebView intercept attach failed: {ex.Message}", _browserState.CurrentUrl);
        }
    }
#endif

    private static string FormatInterceptHeaders(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, headers.Select(header => $"{header.Key}: {header.Value}"));
    }

    private void OnBrowserBackClicked(object? sender, EventArgs e)
    {
        if (BrowserWebView.CanGoBack)
        {
            BrowserWebView.GoBack();
        }

        UpdateBrowserChrome();
    }

    private void OnBrowserForwardClicked(object? sender, EventArgs e)
    {
        if (BrowserWebView.CanGoForward)
        {
            BrowserWebView.GoForward();
        }

        UpdateBrowserChrome();
    }

    private async Task NavigateFromAddressBarAsync()
    {
        var input = BrowserAddressEntry?.Text;
        if (string.IsNullOrWhiteSpace(input))
        {
            _browserState.SetStatus("주소를 입력해 주세요.", _browserState.CurrentUrl);
            return;
        }

        var normalizedUrl = NormalizeAddressInput(input);
        if (string.IsNullOrWhiteSpace(normalizedUrl))
        {
            _browserState.SetStatus("유효한 주소를 입력해 주세요.", _browserState.CurrentUrl);
            return;
        }

        if (BrowserAddressEntry is not null)
        {
            BrowserAddressEntry.Text = normalizedUrl;
        }

        await _browserState.NavigateAsync(normalizedUrl);
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
            _browserState.SetStatus("현재 WebView 페이지 스냅샷을 저장하는 중입니다...", _browserState.CurrentUrl);

            var raw = await RunBrowserScriptAsync(WebViewPageSnapshotScriptBuilder.Build());
            var snapshot = ParseScriptJson<WebViewPageSnapshotPayload>(raw);
            if (snapshot is null)
            {
                _browserState.SetStatus("WebView 페이지 스냅샷 결과를 해석하지 못했습니다.", _browserState.CurrentUrl);
                return;
            }

            var result = await WebViewPageSnapshotWriter.SaveAsync(snapshot, AppContext.BaseDirectory);
            _pendingWebViewSnapshot = result;
            _browserState.SetStatus($"WebView 페이지 스냅샷 저장 완료: {result.DirectoryPath}", _browserState.CurrentUrl);

            SetSnapshotButtonSavingState(isSaving: false);
            HideBrowserWebViewForSnapshotModal();
            ShowBrowserSnapshotConfirmModal(result);
        }
        catch (Exception ex)
        {
            _browserState.SetStatus($"WebView 페이지 스냅샷 저장 실패: {ex.Message}", _browserState.CurrentUrl);
        }
        finally
        {
            SetSnapshotButtonSavingState(isSaving: false);
        }
    }

    private void HideBrowserWebViewForSnapshotModal()
    {
        if (BrowserWebView is null || !BrowserWebView.IsVisible)
        {
            return;
        }

        BrowserWebView.IsVisible = false;
        _snapshotModalHidWebView = true;
    }

    private void RestoreBrowserWebViewAfterSnapshotModal()
    {
        if (!_snapshotModalHidWebView)
        {
            return;
        }

        BrowserWebView.IsVisible = true;
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
                _browserState.SetStatus($"WinUI dialog failed. Trying native MessageBox: {ex.Message}", _browserState.CurrentUrl);
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
            _browserState.SetStatus("분석할 snapshot 정보가 없습니다.", _browserState.CurrentUrl);
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
        _browserState.SetStatus($"Codex 분석 프롬프트 생성 완료: {Path.GetFileName(snapshot.DirectoryPath)}");
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

            var cookiesToImport = WebViewCookieExportSupport.ParseCookieImportText(CookieHeaderEditor?.Text, targetOrigin);
            if (cookiesToImport.Count == 0)
            {
                if (CookieDetailsEditor is not null)
                {
                    CookieDetailsEditor.Text = WebViewCookieExportSupport.BuildCookieImportDiagnostics(CookieHeaderEditor?.Text, targetOrigin);
                }

                SetCookieImportStatus("붙여넣은 텍스트에서 유효한 쿠키 항목을 찾지 못했습니다. 아래 진단 로그를 확인하세요.", isError: true);
                return;
            }

            var coreWebView2 = await TryEnsureCoreWebView2Async();
            if (coreWebView2 is null)
            {
                SetCookieImportStatus("WebView2가 아직 준비되지 않았습니다. 페이지를 한 번 연 뒤 다시 시도하세요.", isError: true);
                return;
            }

            var cookieManager = coreWebView2.CookieManager;
            var existingCookies = await cookieManager.GetCookiesAsync(string.Empty);
            var importPlan = WebViewCookieExportSupport.BuildImportPlan(
                cookiesToImport,
                existingCookies
                    .Select(cookie => new WebViewCookieImportItem(
                        cookie.Name,
                        cookie.Value,
                        cookie.Domain,
                        cookie.Path,
                        cookie.IsSecure,
                        cookie.IsHttpOnly,
                        cookie.SameSite.ToString()))
                    .ToList());

            var deleted = 0;
            var deleteFailed = 0;
            foreach (var cookieToDelete in importPlan.CookiesToDelete)
            {
                try
                {
                    cookieManager.DeleteCookiesWithDomainAndPath(
                        cookieToDelete.Name,
                        cookieToDelete.Domain,
                        cookieToDelete.Path);
                    deleted++;
                }
                catch
                {
                    deleteFailed++;
                }
            }

            foreach (var cookieToReplace in importPlan.CookiesToReplace)
            {
                try
                {
                    cookieManager.DeleteCookiesWithDomainAndPath(
                        cookieToReplace.Name,
                        cookieToReplace.Domain,
                        cookieToReplace.Path);
                }
                catch
                {
                    deleteFailed++;
                }
            }

            var applied = 0;
            var applyFailed = 0;
            foreach (var cookieToImport in importPlan.CookiesToApply)
            {
                try
                {
                    var cookie = cookieManager.CreateCookie(
                        cookieToImport.Name,
                        cookieToImport.Value,
                        cookieToImport.Domain,
                        cookieToImport.Path);
                    cookie.IsSecure = cookieToImport.IsSecure;
                    cookie.IsHttpOnly = cookieToImport.IsHttpOnly;
                    if (TryMapSameSite(cookieToImport.SameSite, out var sameSite))
                    {
                        cookie.SameSite = sameSite;
                    }
                    cookieManager.AddOrUpdateCookie(cookie);
                    applied++;
                }
                catch
                {
                    applyFailed++;
                }
            }

            var failureCount = deleteFailed + applyFailed;
            var statusBuilder = new StringBuilder()
                .Append($"{targetUri.Host} 쿠키 동기화 완료: ")
                .Append($"추가 {importPlan.AddedCount}건, 수정 {importPlan.ModifiedCount}건");

            if (importPlan.UnchangedCount > 0)
            {
                statusBuilder.Append($", 유지 {importPlan.UnchangedCount}건");
            }

            if (deleted > 0 || importPlan.RemovedCount > 0)
            {
                statusBuilder.Append($", 삭제 {deleted}건");
            }

            if (failureCount > 0)
            {
                statusBuilder.Append($", 실패 {failureCount}건");
            }

            statusBuilder.Append(". 필요하면 페이지를 새로고침하세요.");
            SetCookieImportStatus(statusBuilder.ToString(), isError: failureCount > 0);

            if (BrowserAddressEntry is not null)
            {
                BrowserAddressEntry.Text = WebViewCookieExportSupport.ExtractTargetOrigin(CookieHeaderEditor?.Text) ?? targetOrigin;
            }

            BrowserWebView.Reload();
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

            var allCookies = await coreWebView2.CookieManager.GetCookiesAsync(string.Empty);
            var cookies = allCookies
                .Where(cookie => WebViewCookieExportSupport.MatchesHost(targetUri.Host, cookie.Domain))
                .Select(cookie => new WebViewCookieExportItem(cookie.Name, cookie.Value, cookie.Domain, cookie.Path))
                .OrderBy(cookie => cookie.Domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(cookie => cookie.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(cookie => cookie.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var cookieHeader = WebViewCookieExportSupport.BuildCookieHeader(cookies);
            var cookieDetails = WebViewCookieExportSupport.BuildCookieDetails(targetOrigin, cookies);
            if (CookieHeaderEditor is not null)
            {
                CookieHeaderEditor.Text = cookieHeader;
            }

            if (CookieDetailsEditor is not null)
            {
                CookieDetailsEditor.Text = cookieDetails;
            }

            SetCookieImportStatus(
                cookies.Count == 0
                    ? $"{targetUri.Host}에서 가져올 쿠키가 없습니다."
                    : $"{targetUri.Host} 저장소 쿠키 {cookies.Count}개를 문자열로 가져왔습니다.",
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

    private async void OnSaveCookiesClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        try
        {
            if (!TryResolveCookieTargetUri(out var targetOrigin, out var targetUri))
            {
                SetCookieImportStatus("저장할 쿠키의 도메인을 먼저 입력하거나 페이지에 접속하세요.", isError: true);
                return;
            }

            var coreWebView2 = await TryEnsureCoreWebView2Async();
            if (coreWebView2 is null)
            {
                SetCookieImportStatus("WebView2가 아직 준비되지 않았습니다. 페이지를 한 번 연 뒤 다시 시도하세요.", isError: true);
                return;
            }

            var allCookies = await coreWebView2.CookieManager.GetCookiesAsync(string.Empty);
            var cookiesToSave = allCookies
                .Where(cookie => WebViewCookieExportSupport.MatchesHost(targetUri.Host, cookie.Domain))
                .GroupBy(cookie => $"{cookie.Name}|{cookie.Domain}|{cookie.Path}", StringComparer.Ordinal)
                .Select(group => group.Last())
                .Select(cookie => new PersistedBrowserCookie(
                    cookie.Name,
                    cookie.Value,
                    cookie.Domain,
                    cookie.Path,
                    cookie.Expires <= 0 ? null : cookie.Expires,
                    cookie.IsSecure,
                    cookie.IsHttpOnly,
                    cookie.SameSite.ToString()))
                .OrderBy(cookie => cookie.Domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(cookie => cookie.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(cookie => cookie.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (cookiesToSave.Count == 0)
            {
                SetCookieImportStatus($"{targetUri.Host}에서 저장할 쿠키가 없습니다.", isError: true);
                return;
            }

            await _browserState.SaveDomainCookieSnapshotAsync(targetOrigin, cookiesToSave);
            SetCookieImportStatus(
                $"{targetUri.Host} 쿠키 {cookiesToSave.Count}개를 저장했습니다. 다음 실행 때 자동으로 로드됩니다.",
                isError: false);
        }
        catch (Exception ex)
        {
            SetCookieImportStatus($"쿠키 저장 실패: {ex.Message}", isError: true);
        }
#else
        SetCookieImportStatus("쿠키 저장은 Windows WebView2에서만 지원됩니다.", isError: true);
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

    private void OnClearCookiesClicked(object? sender, EventArgs e)
    {
        if (CookieDomainEntry is not null)
        {
            CookieDomainEntry.Text = string.Empty;
        }

        if (CookieHeaderEditor is not null)
        {
            CookieHeaderEditor.Text = string.Empty;
        }

        if (CookieDetailsEditor is not null)
        {
            CookieDetailsEditor.Text = string.Empty;
        }

        SetCookieImportStatus("쿠키 입력과 상세 표시를 비웠습니다.", isError: false);
    }

    private bool TryResolveCookieTargetUri(out string targetOrigin, out Uri targetUri)
    {
        targetOrigin =
            NormalizeCookieTargetOrigin(CookieDomainEntry?.Text) ??
            WebViewCookieExportSupport.ExtractTargetOrigin(CookieHeaderEditor?.Text) ??
            WebViewCookieExportSupport.ExtractTargetOrigin(CookieDetailsEditor?.Text) ??
            TryResolveCurrentBrowserOrigin() ??
            string.Empty;
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

        _browserState.SetStatus(message, _browserState.CurrentUrl);
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

        _browserState.SetStatus(message, _browserState.CurrentUrl);
    }

    private string? TryResolveCurrentBrowserOrigin()
    {
        var candidates = new[]
        {
            _browserState.CurrentUrl,
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

    private void UpdateBrowserChrome()
    {
        if (BrowserStatusLabel is not null)
        {
            BrowserStatusLabel.Text = _browserState.Status;
        }

        if (BrowserUrlLabel is not null)
        {
            BrowserUrlLabel.Text = _browserState.CurrentUrl;
        }

        if (BrowserAddressEntry is not null &&
            !_isBrowserAddressEntryFocused &&
            !string.Equals(BrowserAddressEntry.Text, _browserState.CurrentUrl, StringComparison.Ordinal))
        {
            BrowserAddressEntry.Text = _browserState.CurrentUrl;
        }

        if (BrowserBackButton is not null)
        {
            BrowserBackButton.IsEnabled = BrowserWebView?.CanGoBack ?? false;
            BrowserBackButton.Opacity = BrowserBackButton.IsEnabled ? 1.0 : 0.45;
        }

        if (BrowserForwardButton is not null)
        {
            BrowserForwardButton.IsEnabled = BrowserWebView?.CanGoForward ?? false;
            BrowserForwardButton.Opacity = BrowserForwardButton.IsEnabled ? 1.0 : 0.45;
        }
    }

    private async Task<string> RunBrowserScriptAsync(string script)
    {
#if WINDOWS
        var coreWebView2 = await TryEnsureCoreWebView2Async();
        if (coreWebView2 is null)
        {
            throw new InvalidOperationException("WebView2 is not ready.");
        }

        return await coreWebView2.ExecuteScriptAsync(script);
#else
        throw new PlatformNotSupportedException("Browser script execution requires Windows WebView2.");
#endif
    }

    private static T ParseScriptJson<T>(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("Browser script returned an empty payload.");
        }

        var parsed = JsonNode.Parse(raw) ?? throw new InvalidOperationException("Browser script returned invalid JSON.");
        var effectiveJson = parsed switch
        {
            JsonValue value when value.TryGetValue<string>(out var innerJson) => innerJson,
            _ => parsed.ToJsonString()
        };

        var result = JsonSerializer.Deserialize<T>(effectiveJson, AppJsonOptions);
        return result ?? throw new InvalidOperationException($"Failed to deserialize browser script payload to {typeof(T).Name}.");
    }

    private void OnBrowserWebViewNavigating(object? sender, WebNavigatingEventArgs e)
    {
        var nextUrl = string.IsNullOrWhiteSpace(e.Url) ? _browserState.CurrentUrl : e.Url;
        _browserState.SetStatus("Opening page.", nextUrl);
        UpdateBrowserChrome();
    }

    private async void OnBrowserWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        var currentUrl = string.IsNullOrWhiteSpace(e.Url) ? _browserState.CurrentUrl : e.Url;

        if (e.Result != WebNavigationResult.Success)
        {
            _browserState.SetStatus($"Page load failed: {e.Result}", currentUrl);
            UpdateBrowserChrome();
            return;
        }

        _browserState.SetStatus("The WebView page is open.", currentUrl);
        UpdateBrowserChrome();

        await RefreshWebViewLayoutMetricsAsync();
        await ApplyWebViewInspectorModeAsync(_webViewInspectorModeEnabled);
    }

    private void OnBrowserPaneSizeChanged(object? sender, EventArgs e)
    {
#if WINDOWS
        ApplyAdaptiveWebViewZoom();
#endif
    }

}


