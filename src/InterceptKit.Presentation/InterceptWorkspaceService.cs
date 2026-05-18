namespace InterceptKit.Presentation;

using InterceptKit.Core;

public sealed class InterceptWorkspaceService
{
    private readonly IInterceptSessionStore _sessionStore;
    private readonly IInterceptDecisionSink? _decisionSink;
    private Guid? _selectedSessionId;

    public InterceptWorkspaceService(
        IInterceptSessionStore sessionStore,
        IInterceptDecisionSink? decisionSink = null)
    {
        _sessionStore = sessionStore;
        _decisionSink = decisionSink;
        _sessionStore.Changed += OnSessionStoreChanged;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<InterceptQueueItemViewModel> GetPendingItems()
    {
        var items = _sessionStore.GetPendingSessions()
            .Select(session => new InterceptQueueItemViewModel(
                session.Id,
                session.OriginalRequest.Method,
                session.OriginalRequest.Url,
                session.Status.ToString()))
            .ToArray();

        if (items.Length == 0)
        {
            _selectedSessionId = null;
        }
        else if (_selectedSessionId is null || items.All(item => item.SessionId != _selectedSessionId.Value))
        {
            _selectedSessionId = items[0].SessionId;
        }

        return items;
    }

    public Guid? SelectedSessionId => _selectedSessionId;

    public InterceptSession? GetSelectedSession()
    {
        if (_selectedSessionId is not Guid selectedSessionId)
        {
            return null;
        }

        return _sessionStore.GetPendingSessions().FirstOrDefault(session => session.Id == selectedSessionId);
    }

    public void SelectSession(Guid sessionId)
    {
        _selectedSessionId = sessionId;
    }

    public Task ForwardSelectedAsync(CancellationToken cancellationToken = default)
    {
        return SubmitSelectedAsync(InterceptDecision.ForwardOriginal(), cancellationToken);
    }

    public Task DropSelectedAsync(CancellationToken cancellationToken = default)
    {
        return SubmitSelectedAsync(InterceptDecision.Drop(), cancellationToken);
    }

    public async Task ForwardAllAsync(CancellationToken cancellationToken = default)
    {
        if (_decisionSink is null)
        {
            throw new InvalidOperationException("No intercept decision sink is registered.");
        }

        var processedSessionIds = new HashSet<Guid>();

        // Keep draining newly-created pending sessions, but do not resubmit the same
        // session repeatedly if a decision sink resolves it asynchronously.
        for (var pass = 0; pass < 64; pass++)
        {
            var pendingSessions = _sessionStore.GetPendingSessions().ToArray();
            if (pendingSessions.Length == 0)
            {
                return;
            }

            var unprocessedSessions = pendingSessions
                .Where(session => processedSessionIds.Add(session.Id))
                .ToArray();

            if (unprocessedSessions.Length == 0)
            {
                return;
            }

            foreach (var pendingSession in unprocessedSessions)
            {
                await _decisionSink.SubmitDecisionAsync(
                    pendingSession.Id,
                    InterceptDecision.ForwardOriginal(),
                    cancellationToken);

                // Give the UI thread a chance to render the shortened queue
                // before the next request is forwarded.
                await Task.Yield();
            }
        }
    }

    private Task SubmitSelectedAsync(InterceptDecision decision, CancellationToken cancellationToken)
    {
        if (_selectedSessionId is not Guid sessionId)
        {
            throw new InvalidOperationException("No intercept session is selected.");
        }

        if (_decisionSink is null)
        {
            throw new InvalidOperationException("No intercept decision sink is registered.");
        }

        return _decisionSink.SubmitDecisionAsync(sessionId, decision, cancellationToken);
    }

    private void OnSessionStoreChanged(object? sender, EventArgs e)
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
