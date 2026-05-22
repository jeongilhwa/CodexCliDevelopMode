namespace CodexDesk;

using InterceptKit.Core;
using InterceptKit.Presentation;
using InterceptKit.WebView;

public class WebViewInterceptPanelState
{
    private readonly InterceptWorkspaceService _workspace;
    private readonly WebViewInterceptRuntimeState _runtimeState;

    public WebViewInterceptPanelState(
        InterceptWorkspaceService workspace,
        WebViewInterceptRuntimeState runtimeState)
    {
        _workspace = workspace;
        _runtimeState = runtimeState;
        _workspace.Changed += OnChanged;
        _runtimeState.Changed += OnChanged;
    }

    public event EventHandler? Changed;

    public bool IsEnabled => _runtimeState.IsEnabled;

    public string StatusText => _runtimeState.StatusText;

    public string ToggleButtonText => _runtimeState.IsEnabled ? "Intercept ON" : "Intercept OFF";

    public string DetailText
    {
        get
        {
            var pendingCount = GetPendingItems().Count;
            var lastUrl = _runtimeState.LastPausedRequestUrl ?? "none";
            return $"{pendingCount} pending | {_runtimeState.PausedRequestCount} paused | {lastUrl}";
        }
    }

    public string CountText => $"{GetPendingItems().Count} pending";

    public Guid? SelectedSessionId => _workspace.SelectedSessionId;

    public string SelectedSummaryText
    {
        get
        {
            var session = GetSelectedSession();
            return session is null
                ? "선택된 요청 없음"
                : $"{session.OriginalRequest.Method} {session.OriginalRequest.Url}";
        }
    }

    public IReadOnlyList<InterceptQueueItemViewModel> GetPendingItems()
    {
        return _workspace.GetPendingItems();
    }

    public InterceptSession? GetSelectedSession()
    {
        return _workspace.GetSelectedSession();
    }

    public void SelectSession(Guid sessionId)
    {
        _workspace.SelectSession(sessionId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task ForwardSelectedAsync(CancellationToken cancellationToken = default)
    {
        return _workspace.ForwardSelectedAsync(cancellationToken);
    }

    public Task DropSelectedAsync(CancellationToken cancellationToken = default)
    {
        return _workspace.DropSelectedAsync(cancellationToken);
    }

    public Task ForwardAllAsync(CancellationToken cancellationToken = default)
    {
        return _workspace.ForwardAllAsync(cancellationToken);
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
