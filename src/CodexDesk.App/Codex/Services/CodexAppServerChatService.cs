using System.Globalization;
using System.Text.Json;
using CodexDesk.AppServer;
using CodexDesk.Models;

namespace CodexDesk.Services;

/// <summary>Orchestrates persistent app-server threads, streaming turns, and approvals for the UI.</summary>
public sealed class CodexAppServerChatService : IDisposable
{
    private const string ConfigurationOption = "-c";
    private const string WorkspaceWriteSandboxMode = "sandbox_mode=\"workspace-write\"";
    private const string ReadOnlySandboxMode = "sandbox_mode=\"read-only\"";
    private const string ApprovalPolicy = "approval_policy=\"on-request\"";
    private const string AgentMessageDeltaMethod = "agentMessage/delta";
    private const string ItemAgentMessageDeltaMethod = "item/agentMessage/delta";
    private const string ItemStartedMethod = "item/started";
    private const string ItemCompletedMethod = "item/completed";
    private const string TurnStartedMethod = "turn/started";
    private const string TurnCompletedMethod = "turn/completed";
    private const string DynamicToolCallMethod = "item/tool/call";

    private readonly ICodexAppServerClient _client;
    private readonly IBrowserAutomationService? _browserAutomation;
    private readonly SemaphoreSlim _turnStartLock = new(1, 1);
    private readonly HashSet<string> _loadedThreads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _browserTurnCancellations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodexAppServerApproval> _approvals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolvingApprovals = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates the orchestration layer over the shared app-server client.</summary>
    /// <param name="client">The transport owned and disposed by the application container.</param>
    public CodexAppServerChatService(ICodexAppServerClient client, IBrowserAutomationService? browserAutomation = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _browserAutomation = browserAutomation;
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
            await EnsureServerStartedAsync(request, cancellationToken).ConfigureAwait(false);
            threadId = await ResolveThreadIdAsync(request, threadId, cancellationToken).ConfigureAwait(false);

            Publish(new(CodexAppServerChatEventKind.ThreadReady, threadId));
            RegisterBrowserTurn(threadId);
            await StartTurnAsync(threadId, request.Prompt, cancellationToken).ConfigureAwait(false);
            return threadId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (threadId is not null) CancelBrowserTurn(threadId);
            // Cancellation stops waiting for a request; the server may still be running the turn.
            throw;
        }
        catch (Exception exception)
        {
            if (threadId is not null) CancelBrowserTurn(threadId);
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
        CancelBrowserTurn(threadId);
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
        var serverDecision = NormalizeApprovalDecision(decision);
        var approval = BeginApprovalResolution(approvalId);

        try
        {
            await _client.RespondAsync(approval.RequestId, new { decision = serverDecision }, cancellationToken).ConfigureAwait(false);
            CompleteApprovalResolution(approvalId);
            Publish(new(CodexAppServerChatEventKind.ApprovalResolved, approval.ThreadId, approval.TurnId, Approval: approval));
        }
        finally
        {
            EndApprovalResolution(approvalId);
        }
    }

    /// <summary>Unsubscribes event handlers without disposing the shared client.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.EventReceived -= OnEventReceived;
        _client.ServerRequestReceived -= OnServerRequestReceived;
        CancelAllBrowserTurns();
        // In-flight operations may still release the semaphore during application shutdown.
    }

    private void OnEventReceived(object? sender, CodexAppServerEvent notification)
    {
        var parameters = notification.Parameters;
        var threadId = ReadString(parameters, "threadId");
        if (threadId is null) return;

        if (notification.Method is ItemAgentMessageDeltaMethod or AgentMessageDeltaMethod)
        {
            PublishAssistantDelta(parameters, threadId);
            return;
        }

        if (notification.Method is ItemStartedMethod or ItemCompletedMethod)
        {
            PublishItemActivity(notification.Method, parameters, threadId);
            return;
        }

        var progress = notification.Method switch
        {
            "item/reasoning/summaryTextDelta" or "item/reasoning/summaryPartAdded" => "생각 정리 중",
            "item/commandExecution/outputDelta" => "명령 실행 중",
            "item/plan/delta" or "turn/plan/updated" => "계획 작성 중",
            "item/mcpToolCall/progress" => "도구 실행 중",
            "turn/diff/updated" => "변경 내용 정리 중",
            _ => null
        };
        if (progress is not null)
        {
            Publish(new(CodexAppServerChatEventKind.ActivityProgress, threadId,
                ReadString(parameters, "turnId"), progress, ItemId: ReadString(parameters, "itemId")));
            return;
        }

        if (notification.Method == "error" && ReadBoolean(parameters, "willRetry"))
        {
            Publish(new(CodexAppServerChatEventKind.ActivityProgress, threadId,
                ReadString(parameters, "turnId"), "연결 재시도 중"));
            return;
        }

        var turn = ReadObject(parameters, "turn");
        var turnId = ReadString(turn, "id") ?? ReadString(parameters, "turnId");
        if (notification.Method == TurnStartedMethod)
        {
            Publish(new(CodexAppServerChatEventKind.TurnStarted, threadId, turnId));
            return;
        }

        if (notification.Method == TurnCompletedMethod)
        {
            PublishTurnCompletion(turn, threadId, turnId);
        }
    }

    private void OnServerRequestReceived(object? sender, CodexAppServerServerRequest request)
    {
        if (request.Method == DynamicToolCallMethod)
        {
            _ = RespondToBrowserToolAsync(request);
            return;
        }

        if (!TryCreateApproval(request, out var approval)) return;

        lock (_approvals)
        {
            _approvals[approval.Id] = approval;
        }

        Publish(new(CodexAppServerChatEventKind.ApprovalRequired, approval.ThreadId, approval.TurnId, Approval: approval));
    }

    private async Task RespondToBrowserToolAsync(CodexAppServerServerRequest request)
    {
        var tool = ReadString(request.Parameters, "tool");
        var threadId = ReadString(request.Parameters, "threadId");
        var turnCancellation = GetBrowserTurnCancellation(threadId);
        BrowserToolResult result;
        if (_browserAutomation is null || !CodexBrowserTools.Contains(tool))
        {
            result = new BrowserToolResult(false, $"Unsupported browser tool: {tool ?? "(missing)"}");
        }
        else if (turnCancellation is null || turnCancellation.Value.IsCancellationRequested)
        {
            result = new BrowserToolResult(false, "The browser turn is no longer active.");
        }
        else
        {
            try
            {
                result = await _browserAutomation.ExecuteAsync(tool!, ReadObject(request.Parameters, "arguments"), turnCancellation.Value).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                result = new BrowserToolResult(false, $"Browser tool failed: {exception.Message}");
            }
        }

        if (tool is "network_capture_start" or "network_capture_stop" or "network_capture_list")
        {
            var status = result.Success ? "succeeded" : "failed";
            var count = result.CaptureCount is { } captured ? $"; {captured} requests retained" : string.Empty;
            Publish(new(CodexAppServerChatEventKind.CaptureToolResult, threadId ?? string.Empty,
                ReadString(request.Parameters, "turnId"), $"{tool} {status}{count}."));
        }

        try
        {
            await _client.RespondAsync(request.RequestId, new
            {
                success = result.Success,
                contentItems = new[] { new { type = "inputText", text = result.Text } }
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Publish(new(CodexAppServerChatEventKind.TurnFailed,
                ReadString(request.Parameters, "threadId") ?? string.Empty,
                ReadString(request.Parameters, "turnId"),
                $"Could not return browser result to Codex: {exception.Message}"));
        }
    }

    private void RegisterBrowserTurn(string threadId)
    {
        CancellationTokenSource? previous;
        lock (_browserTurnCancellations)
        {
            _browserTurnCancellations.Remove(threadId, out previous);
            _browserTurnCancellations.Add(threadId, new CancellationTokenSource());
        }

        previous?.Cancel();
        previous?.Dispose();
    }

    private CancellationToken? GetBrowserTurnCancellation(string? threadId)
    {
        if (threadId is null) return null;
        lock (_browserTurnCancellations)
        {
            return _browserTurnCancellations.TryGetValue(threadId, out var source) ? source.Token : null;
        }
    }

    private void CancelBrowserTurn(string threadId)
    {
        CancellationTokenSource? source;
        lock (_browserTurnCancellations)
        {
            _browserTurnCancellations.Remove(threadId, out source);
        }

        source?.Cancel();
        source?.Dispose();
    }

    private void CancelAllBrowserTurns()
    {
        CancellationTokenSource[] sources;
        lock (_browserTurnCancellations)
        {
            sources = _browserTurnCancellations.Values.ToArray();
            _browserTurnCancellations.Clear();
        }

        foreach (var source in sources)
        {
            source.Cancel();
            source.Dispose();
        }
    }

    private async Task EnsureServerStartedAsync(CodexChatRequest request, CancellationToken cancellationToken)
    {
        if (_client.IsRunning) return;

        CancelAllBrowserTurns();
        _loadedThreads.Clear();
        await _client.StartAsync(CreateServerOptions(request), cancellationToken).ConfigureAwait(false);
    }

    private static CodexAppServerOptions CreateServerOptions(CodexChatRequest request)
    {
        var options = new CodexAppServerOptions { CommandPath = request.CommandPath, WorkspacePath = request.WorkspacePath };
        options.AdditionalArguments.Add(ConfigurationOption);
        options.AdditionalArguments.Add(request.AllowWorkspaceWrite ? WorkspaceWriteSandboxMode : ReadOnlySandboxMode);
        options.AdditionalArguments.Add(ConfigurationOption);
        options.AdditionalArguments.Add(ApprovalPolicy);
        return options;
    }

    private async Task<string> ResolveThreadIdAsync(CodexChatRequest request, string? threadId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            var result = await _client.StartThreadAsync(request.Model, cancellationToken,
                _browserAutomation is null ? null : CodexBrowserTools.Specifications).ConfigureAwait(false);
            threadId = ReadString(ReadObject(result, "thread"), "id")
                ?? throw new InvalidOperationException("Codex app-server did not return a thread identifier.");
            _loadedThreads.Add(threadId);
            return threadId;
        }

        if (!_loadedThreads.Contains(threadId))
        {
            await _client.ResumeThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
            _loadedThreads.Add(threadId);
        }

        return threadId;
    }

    private async Task StartTurnAsync(string threadId, string prompt, CancellationToken cancellationToken)
    {
        var turn = await _client.StartTurnAsync(threadId, prompt, cancellationToken).ConfigureAwait(false);
        Publish(new(CodexAppServerChatEventKind.TurnStarted, threadId, ReadString(ReadObject(turn, "turn"), "id")));
    }

    private static string NormalizeApprovalDecision(string decision) => decision switch
    {
        "accept" => "accept",
        "reject" or "decline" => "decline",
        _ => throw new ArgumentException("The decision must be accept, reject, or decline.", nameof(decision))
    };

    private CodexAppServerApproval BeginApprovalResolution(string approvalId)
    {
        lock (_approvals)
        {
            if (!_approvals.TryGetValue(approvalId, out var approval) || !_resolvingApprovals.Add(approvalId))
            {
                throw new InvalidOperationException("The approval is no longer pending or is already being resolved.");
            }

            return approval;
        }
    }

    private void CompleteApprovalResolution(string approvalId)
    {
        lock (_approvals)
        {
            _approvals.Remove(approvalId);
        }
    }

    private void EndApprovalResolution(string approvalId)
    {
        lock (_approvals)
        {
            _resolvingApprovals.Remove(approvalId);
        }
    }

    private void PublishAssistantDelta(JsonElement parameters, string threadId)
    {
        if (ReadString(parameters, "delta") is { } delta)
        {
            Publish(new(CodexAppServerChatEventKind.AssistantDelta, threadId, ReadString(parameters, "turnId"), delta,
                ItemId: ReadString(parameters, "itemId")));
        }
    }

    private void PublishItemActivity(string method, JsonElement parameters, string threadId)
    {
        var item = ReadObject(parameters, "item");
        var type = ReadString(item, "type");
        var activity = type switch
        {
            "agentMessage" => "답변 작성 중",
            "reasoning" => "생각 정리 중",
            "commandExecution" => FormatActivity("명령 실행 중", ReadString(item, "command")),
            "fileChange" => "파일 수정 중",
            "mcpToolCall" => FormatActivity("도구 실행 중", JoinToolName(item)),
            "dynamicToolCall" => FormatActivity("도구 실행 중", ReadString(item, "tool")),
            "webSearch" => "웹 검색 중",
            "imageView" => "이미지 확인 중",
            "plan" => "계획 작성 중",
            "collabToolCall" => "하위 작업 진행 중",
            "contextCompaction" => "대화 기록 정리 중",
            _ => null
        };
        if (activity is null) return;

        var started = method == ItemStartedMethod;
        Publish(new(started ? CodexAppServerChatEventKind.ActivityStarted : CodexAppServerChatEventKind.ActivityCompleted,
            threadId, ReadString(parameters, "turnId"), started ? activity : "단계 완료 · 계속 처리 중",
            ItemId: ReadString(item, "id")));
    }

    private static string? JoinToolName(JsonElement item)
    {
        var server = ReadString(item, "server");
        var tool = ReadString(item, "tool");
        return string.IsNullOrWhiteSpace(server) ? tool : string.IsNullOrWhiteSpace(tool) ? server : $"{server}/{tool}";
    }

    private static string FormatActivity(string label, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return label;
        var compact = string.Join(' ', detail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return $"{label}: {(compact.Length <= 72 ? compact : compact[..69] + "…")}";
    }

    private void PublishTurnCompletion(JsonElement turn, string threadId, string? turnId)
    {
        var kind = ReadString(turn, "status") switch
        {
            "completed" => CodexAppServerChatEventKind.TurnCompleted,
            "failed" => CodexAppServerChatEventKind.TurnFailed,
            "interrupted" => CodexAppServerChatEventKind.TurnInterrupted,
            _ => (CodexAppServerChatEventKind?)null
        };
        if (kind is null) return;

        CancelBrowserTurn(threadId);
        RemoveTurnApprovals(threadId, turnId);
        Publish(new(kind.Value, threadId, turnId, ReadString(ReadObject(turn, "error"), "message")));
    }

    private void RemoveTurnApprovals(string threadId, string? turnId)
    {
        lock (_approvals)
        {
            foreach (var approval in _approvals.Values.Where(approval => approval.ThreadId == threadId && approval.TurnId == turnId).ToArray())
            {
                _approvals.Remove(approval.Id);
            }
        }
    }

    private static bool TryCreateApproval(CodexAppServerServerRequest request, out CodexAppServerApproval approval)
    {
        var kind = request.Method switch
        {
            "commandExecution/requestApproval" or "item/commandExecution/requestApproval" => CodexAppServerApprovalKind.CommandExecution,
            "fileChange/requestApproval" or "item/fileChange/requestApproval" => CodexAppServerApprovalKind.FileChange,
            _ => (CodexAppServerApprovalKind?)null
        };

        if (kind is null)
        {
            approval = default!;
            return false;
        }

        var parameters = request.Parameters;
        approval = new CodexAppServerApproval(
            request.RequestId.ToString(CultureInfo.InvariantCulture), request.RequestId, kind.Value,
            ReadString(parameters, "threadId") ?? string.Empty, ReadString(parameters, "turnId"),
            ReadString(parameters, "itemId"), ReadString(parameters, "reason"),
            ReadString(parameters, "command"), ReadString(parameters, "cwd"), ReadString(parameters, "grantRoot"));
        return true;
    }

    private void Publish(CodexAppServerChatEvent notification) => EventReceived?.Invoke(this, notification);

    private static JsonElement ReadObject(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? ReadString(JsonElement element, string name) =>
        ReadObject(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool ReadBoolean(JsonElement element, string name) =>
        ReadObject(element, name) is { ValueKind: JsonValueKind.True };
}
