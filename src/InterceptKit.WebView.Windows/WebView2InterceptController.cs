namespace InterceptKit.WebView.Windows;

using InterceptKit.Core;
using InterceptKit.WebView;
public sealed class WebView2InterceptController : IWebViewInterceptController, IInterceptDecisionSink
{
    private static readonly TimeSpan MinimumPassThroughDuration = TimeSpan.FromMilliseconds(250);
    private readonly IInterceptSessionStore _sessionStore;
    private readonly IWebViewInterceptPlatformAdapter _adapter;
    private readonly WebViewInterceptRuntimeState? _runtimeState;
    private readonly Dictionary<Guid, PausedWebViewRequestContext> _pausedRequests = [];
    private object? _attachedCoreWebView2;
    private DateTimeOffset? _temporaryPassThroughUntilUtc;

    public WebView2InterceptController(
        IInterceptSessionStore sessionStore,
        IWebViewInterceptPlatformAdapter adapter,
        WebViewInterceptRuntimeState? runtimeState = null,
        WebViewInterceptOptions? options = null)
    {
        _sessionStore = sessionStore;
        _adapter = adapter;
        _runtimeState = runtimeState;
        Options = options ?? new WebViewInterceptOptions();
        _adapter.RequestPaused += OnRequestPaused;
    }

    public WebViewInterceptOptions Options { get; }

    public bool IsEnabled { get; private set; }

    public void BeginTemporaryPassThrough(TimeSpan duration)
    {
        var effectiveDuration = duration < MinimumPassThroughDuration
            ? MinimumPassThroughDuration
            : duration;
        _temporaryPassThroughUntilUtc = DateTimeOffset.UtcNow.Add(effectiveDuration);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsEnabled = false;
        await ForwardAllPendingRequestsAsync(cancellationToken);
        await _adapter.DisableAsync();
        _runtimeState?.MarkDisabled();
    }

    public Task AttachAsync(object coreWebView2, CancellationToken cancellationToken = default)
    {
        _attachedCoreWebView2 = coreWebView2;
        if (!IsEnabled)
        {
            _runtimeState?.MarkDisabled();
            return Task.CompletedTask;
        }

        return AttachCoreAsync(coreWebView2, cancellationToken);
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (enabled == IsEnabled)
        {
            if (!enabled)
            {
                _runtimeState?.MarkDisabled();
                return;
            }

            if (_attachedCoreWebView2 is not null)
            {
                await AttachCoreAsync(_attachedCoreWebView2, cancellationToken);
            }
            else
            {
                _runtimeState?.MarkWaitingForAttach();
            }

            return;
        }

        if (!enabled)
        {
            await DisableInterceptAsync(cancellationToken);
            return;
        }

        IsEnabled = true;
        if (_attachedCoreWebView2 is not null)
        {
            await AttachCoreAsync(_attachedCoreWebView2, cancellationToken);
            return;
        }

        _runtimeState?.MarkWaitingForAttach();
    }

    public async Task SubmitDecisionAsync(
        Guid sessionId,
        InterceptDecision decision,
        CancellationToken cancellationToken = default)
    {
        if (!_pausedRequests.TryGetValue(sessionId, out var pausedRequest))
        {
            throw new InvalidOperationException($"No paused WebView2 request is registered for session {sessionId}.");
        }

        _sessionStore.Resolve(sessionId, decision, null);
        _pausedRequests.Remove(sessionId);

        switch (decision.Kind)
        {
            case InterceptDecisionKind.Drop:
                await _adapter.DropAsync(pausedRequest, cancellationToken);
                break;

            case InterceptDecisionKind.ForwardOriginal:
            case InterceptDecisionKind.ForwardModified:
                await _adapter.ForwardAsync(pausedRequest, decision.EditedRequest, cancellationToken);
                break;

            default:
                throw new InvalidOperationException($"Unsupported decision kind: {decision.Kind}");
        }
    }

    private void OnRequestPaused(object? sender, string payload)
    {
        var pausedRequest = WebView2PausedRequestParser.Parse(payload);

        if (!IsEnabled)
        {
            ForwardPausedRequest(pausedRequest, "Intercept OFF pass-through failed");
            return;
        }

        if (IsTemporaryPassThroughActive())
        {
            ForwardPausedRequest(pausedRequest, "Temporary WebView2 pass-through failed");
            return;
        }

        if (!ShouldIntercept(pausedRequest))
        {
            ForwardPausedRequest(pausedRequest, "Filtered WebView2 request pass-through failed");
            return;
        }

        _runtimeState?.MarkPaused(pausedRequest.Request);
        var session = _sessionStore.CreatePending(pausedRequest.Request);
        _pausedRequests[session.Id] = new PausedWebViewRequestContext(
            session.Id,
            WebView2InterceptFeature.Default.Capabilities,
            pausedRequest.FetchRequestId);
    }

    private async Task AttachCoreAsync(object coreWebView2, CancellationToken cancellationToken)
    {
        try
        {
            await _adapter.AttachAsync(coreWebView2, cancellationToken);
            _runtimeState?.MarkAttached();
        }
        catch (Exception ex)
        {
            _runtimeState?.MarkError($"WebView2 attach failed: {ex.Message}");
            throw;
        }
    }

    private async Task DisableInterceptAsync(CancellationToken cancellationToken)
    {
        IsEnabled = false;
        await ForwardAllPendingRequestsAsync(cancellationToken);
        _runtimeState?.MarkDisabled();
    }

    private async Task ForwardAllPendingRequestsAsync(CancellationToken cancellationToken)
    {
        foreach (var pending in _pausedRequests.ToArray())
        {
            await _adapter.ForwardAsync(pending.Value, null, cancellationToken);
            _sessionStore.Resolve(pending.Key, InterceptDecision.ForwardOriginal(), null);
            _pausedRequests.Remove(pending.Key);
        }
    }

    private bool ShouldIntercept(WebView2PausedRequest pausedRequest)
    {
        // Authentication redirects must finish without waiting for a user decision.
        // Providers commonly rotate cookies while navigating through these endpoints;
        // pausing even one document or login mutation can leave the WebView at the
        // logged-out page after the credentials were accepted.
        if (IsAuthenticationFlow(pausedRequest.Request))
        {
            return false;
        }

        if (!Options.InterceptRequests)
        {
            return false;
        }

        if (Options.IncludedResourceTypes.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(pausedRequest.ResourceType))
        {
            return true;
        }

        return Options.IncludedResourceTypes.Contains(pausedRequest.ResourceType);
    }

    private static bool IsAuthenticationFlow(InterceptedRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            (!uri.Host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase) &&
              !uri.Host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (uri.AbsolutePath.StartsWith("/accounts/onetap", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.StartsWith("/accounts/login", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.StartsWith("/challenge", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.StartsWith("/checkpoint", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!uri.AbsolutePath.Equals("/api/graphql", StringComparison.OrdinalIgnoreCase) ||
            !request.Headers.TryGetValue("X-FB-Friendly-Name", out var friendlyName))
        {
            return false;
        }

        return friendlyName.Equals("useCDSWebLoginMutation", StringComparison.OrdinalIgnoreCase) ||
               friendlyName.Equals("CAAFetaAYMHPasswordEntryQuery", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsTemporaryPassThroughActive()
    {
        return _temporaryPassThroughUntilUtc is DateTimeOffset until &&
               until > DateTimeOffset.UtcNow;
    }

    private void ForwardPausedRequest(WebView2PausedRequest pausedRequest, string errorPrefix)
    {
        try
        {
            var context = new PausedWebViewRequestContext(
                Guid.Empty,
                WebView2InterceptFeature.Default.Capabilities,
                pausedRequest.FetchRequestId);
            _adapter.ForwardAsync(context, null).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _runtimeState?.MarkError($"{errorPrefix}: {ex.Message}");
        }
    }
}
