using System.Globalization;
using System.Text.Json;
using CodexDesk.AppServer;
using CodexDesk.Models;

namespace CodexDesk.Services;

/// <summary>Orchestrates persistent app-server threads, streaming turns, and approvals for the UI.</summary>
public sealed class CodexAppServerChatService : IDisposable
{
    private readonly ICodexAppServerClient _client;
    private readonly SemaphoreSlim _turnStartLock = new(1, 1);
    private readonly HashSet<string> _loadedThreads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodexAppServerApproval> _approvals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolvingApprovals = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates the orchestration layer over the shared app-server client.</summary>
    /// <param name="client">The transport owned and disposed by the application container.</param>
    public CodexAppServerChatService(ICodexAppServerClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.EventReceived += OnEventReceived;
        _client.ServerRequestReceived += OnServerRequestReceived;
    }

    /// <summary>Occurs on the receiving thread when conversation or approval state changes.</summary>
    /// <remarks>UI consumers must dispatch updates to their UI synchronization context.</remarks>
    public event EventHandler<CodexAppServerChatEvent>? EventReceived;

    /// <summary>Creates or resumes a thread and starts a turn using the persistent process.</summary>
    /// <param name="request">The prompt, thread, model, and initial process configuration.</param>
    /// <param name="cancellationToken">A token used to cancel the request; use interruption to stop an accepted turn.</param>
    /// <returns>The resolved thread identifier. Assistant output arrives through <see cref="EventReceived"/>.</returns>
    public async Task<string> StartTurnAsync(CodexChatRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        var threadId = request.ThreadId;
        await _turnStartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_client.IsRunning)
            {
                var options = new CodexAppServerOptions { CommandPath = request.CommandPath, WorkspacePath = request.WorkspacePath };
                options.AdditionalArguments.Add("-c");
                options.AdditionalArguments.Add($"sandbox_mode=\"{(request.AllowWorkspaceWrite ? "workspace-write" : "read-only")}\"");
                options.AdditionalArguments.Add("-c");
                options.AdditionalArguments.Add("approval_policy=\"on-request\"");
                _loadedThreads.Clear();
                await _client.StartAsync(options, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(threadId))
            {
                var result = await _client.StartThreadAsync(request.Model, cancellationToken).ConfigureAwait(false);
                threadId = ReadString(ReadObject(result, "thread"), "id")
                    ?? throw new InvalidOperationException("Codex app-server did not return a thread identifier.");
                _loadedThreads.Add(threadId);
            }
            else if (!_loadedThreads.Contains(threadId))
            {
                await _client.ResumeThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
                _loadedThreads.Add(threadId);
            }

            Publish(new(CodexAppServerChatEventKind.ThreadReady, threadId));
            var turn = await _client.StartTurnAsync(threadId, request.Prompt, cancellationToken).ConfigureAwait(false);
            Publish(new(CodexAppServerChatEventKind.TurnStarted, threadId, ReadString(ReadObject(turn, "turn"), "id")));
            return threadId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation stops waiting for a request; the server may still be running the turn.
            throw;
        }
        catch (Exception exception)
        {
            Publish(new(CodexAppServerChatEventKind.TurnFailed, threadId ?? string.Empty, Text: exception.Message));
            throw;
        }
        finally
        {
            _turnStartLock.Release();
        }
    }

    /// <summary>Asks the server to interrupt the active turn; terminal state arrives as an event.</summary>
    /// <param name="threadId">The thread whose turn should stop.</param>
    /// <param name="cancellationToken">A token used to cancel the interruption request.</param>
    public Task InterruptAsync(string threadId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return _client.InterruptTurnAsync(threadId, cancellationToken);
    }

    /// <summary>Resolves a pending approval through its original JSON-RPC request identifier.</summary>
    /// <param name="approvalId">The identifier supplied in the approval event.</param>
    /// <param name="decision">Accept, reject, or decline. Reject is sent as decline.</param>
    /// <param name="cancellationToken">A token used to cancel writing the response.</param>
    public async Task ResolveApprovalAsync(string approvalId, string decision, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        var serverDecision = decision switch
        {
            "accept" => "accept",
            "reject" or "decline" => "decline",
            _ => throw new ArgumentException("The decision must be accept, reject, or decline.", nameof(decision))
        };
        CodexAppServerApproval approval;
        lock (_approvals)
        {
            if (!_approvals.TryGetValue(approvalId, out approval!) || !_resolvingApprovals.Add(approvalId))
            {
                throw new InvalidOperationException("The approval is no longer pending or is already being resolved.");
            }
        }

        try
        {
            await _client.RespondAsync(approval.RequestId, new { decision = serverDecision }, cancellationToken).ConfigureAwait(false);
            lock (_approvals)
            {
                _approvals.Remove(approvalId);
            }

            Publish(new(CodexAppServerChatEventKind.ApprovalResolved, approval.ThreadId, approval.TurnId, Approval: approval));
        }
        finally
        {
            lock (_approvals)
            {
                _resolvingApprovals.Remove(approvalId);
            }
        }
    }

    /// <summary>Unsubscribes event handlers without disposing the shared client.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.EventReceived -= OnEventReceived;
        _client.ServerRequestReceived -= OnServerRequestReceived;
        // In-flight operations may still release the semaphore during application shutdown.
    }

    private void OnEventReceived(object? sender, CodexAppServerEvent notification)
    {
        var parameters = notification.Parameters;
        var threadId = ReadString(parameters, "threadId");
        if (threadId is null) return;

        if (notification.Method is "item/agentMessage/delta" or "agentMessage/delta")
        {
            if (ReadString(parameters, "delta") is { } delta)
            {
                Publish(new(CodexAppServerChatEventKind.AssistantDelta, threadId, ReadString(parameters, "turnId"), delta));
            }

            return;
        }

        var turn = ReadObject(parameters, "turn");
        var turnId = ReadString(turn, "id") ?? ReadString(parameters, "turnId");
        if (notification.Method == "turn/started")
        {
            Publish(new(CodexAppServerChatEventKind.TurnStarted, threadId, turnId));
            return;
        }

        if (notification.Method != "turn/completed") return;
        var kind = ReadString(turn, "status") switch
        {
            "completed" => CodexAppServerChatEventKind.TurnCompleted,
            "failed" => CodexAppServerChatEventKind.TurnFailed,
            "interrupted" => CodexAppServerChatEventKind.TurnInterrupted,
            _ => (CodexAppServerChatEventKind?)null
        };
        if (kind is null) return;

        lock (_approvals)
        {
            foreach (var approval in _approvals.Values.Where(a => a.ThreadId == threadId && a.TurnId == turnId).ToArray())
            {
                _approvals.Remove(approval.Id);
            }
        }

        Publish(new(kind.Value, threadId, turnId, ReadString(ReadObject(turn, "error"), "message")));
    }

    private void OnServerRequestReceived(object? sender, CodexAppServerServerRequest request)
    {
        var kind = request.Method switch
        {
            "commandExecution/requestApproval" or "item/commandExecution/requestApproval" => CodexAppServerApprovalKind.CommandExecution,
            "fileChange/requestApproval" or "item/fileChange/requestApproval" => CodexAppServerApprovalKind.FileChange,
            _ => (CodexAppServerApprovalKind?)null
        };
        if (kind is null) return;
        var parameters = request.Parameters;
        var approval = new CodexAppServerApproval(
            request.RequestId.ToString(CultureInfo.InvariantCulture), request.RequestId, kind.Value,
            ReadString(parameters, "threadId") ?? string.Empty, ReadString(parameters, "turnId"),
            ReadString(parameters, "itemId"), ReadString(parameters, "reason"),
            ReadString(parameters, "command"), ReadString(parameters, "cwd"), ReadString(parameters, "grantRoot"));
        lock (_approvals)
        {
            _approvals[approval.Id] = approval;
        }

        Publish(new(CodexAppServerChatEventKind.ApprovalRequired, approval.ThreadId, approval.TurnId, Approval: approval));
    }

    private void Publish(CodexAppServerChatEvent notification) => EventReceived?.Invoke(this, notification);

    private static JsonElement ReadObject(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? ReadString(JsonElement element, string name) =>
        ReadObject(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
