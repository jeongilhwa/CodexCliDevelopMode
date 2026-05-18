namespace InterceptKit.Core;

public interface IInterceptSessionStore
{
    event EventHandler? Changed;

    InterceptSession CreatePending(InterceptedRequest request);

    IReadOnlyList<InterceptSession> GetPendingSessions();

    InterceptSession Resolve(Guid sessionId, InterceptDecision decision, InterceptedResponse? response);
}

public interface IInterceptDecisionSink
{
    Task SubmitDecisionAsync(
        Guid sessionId,
        InterceptDecision decision,
        CancellationToken cancellationToken = default);
}

public sealed class InterceptSessionStore : IInterceptSessionStore
{
    private readonly List<Guid> _pendingOrder = [];
    private readonly Dictionary<Guid, InterceptSession> _sessions = [];

    public event EventHandler? Changed;

    public InterceptSession CreatePending(InterceptedRequest request)
    {
        var session = new InterceptSession(
            Guid.NewGuid(),
            request,
            null,
            InterceptDecision.ForwardOriginal(),
            InterceptSessionStatus.Pending,
            null,
            DateTimeOffset.UtcNow,
            null);

        _pendingOrder.Add(session.Id);
        _sessions[session.Id] = session;
        Changed?.Invoke(this, EventArgs.Empty);
        return session;
    }

    public IReadOnlyList<InterceptSession> GetPendingSessions()
    {
        return _pendingOrder
            .Select(id => _sessions[id])
            .ToArray();
    }

    public InterceptSession Resolve(Guid sessionId, InterceptDecision decision, InterceptedResponse? response)
    {
        if (!_sessions.TryGetValue(sessionId, out var existing))
        {
            throw new InvalidOperationException($"Unknown intercept session: {sessionId}");
        }

        var status = decision.Kind switch
        {
            InterceptDecisionKind.Drop => InterceptSessionStatus.Dropped,
            InterceptDecisionKind.ForwardModified or InterceptDecisionKind.ForwardOriginal => InterceptSessionStatus.Forwarded,
            _ => throw new InvalidOperationException($"Unsupported intercept decision: {decision.Kind}")
        };

        var resolved = existing with
        {
            EditedRequest = decision.EditedRequest,
            Decision = decision,
            Response = response,
            Status = status,
            ResolvedAt = DateTimeOffset.UtcNow
        };

        _sessions[sessionId] = resolved;
        _pendingOrder.Remove(sessionId);
        Changed?.Invoke(this, EventArgs.Empty);
        return resolved;
    }
}
