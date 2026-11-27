namespace CodexDesk.Services;

using CodexDesk.Models;

/// <summary>Keeps chat tabs and app-server events alive while the chat page is unmounted.</summary>
public sealed class CodexChatSessionState : IDisposable
{
    private readonly CodexAppServerChatService _chat;
    private object? _dispatcherOwner;
    private Action<Action>? _dispatcher;

    public CodexChatSessionState(CodexAppServerChatService chat)
    {
        _chat = chat;
        _chat.EventReceived += OnChatEventReceived;
    }

    public event EventHandler? Changed;

    public List<ChatTab> Tabs { get; } = new();
    public string ActiveTabId { get; set; } = string.Empty;
    public int TabCounter { get; set; }
    public ChatTab? StartingTab { get; set; }
    public SemaphoreSlim TurnStartLock { get; } = new(1, 1);

    public void AttachDispatcher(object owner, Action<Action> dispatcher)
    {
        _dispatcherOwner = owner;
        _dispatcher = dispatcher;
    }

    public void DetachDispatcher(object owner)
    {
        if (!ReferenceEquals(_dispatcherOwner, owner)) return;
        _dispatcherOwner = null;
        _dispatcher = null;
    }

    public void FinishTurn(ChatTab tab)
    {
        tab.IsBusy = false;
        tab.BusyStartedAt = null;
        tab.PendingAssistantIndex = null;
        tab.ActiveItemId = null;
        tab.ActivityText = string.Empty;
        tab.LastSignalAt = null;
        tab.PendingApprovals.Clear();
        tab.RunCts?.Dispose();
        tab.RunCts = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnChatEventReceived(object? sender, CodexAppServerChatEvent notification)
    {
        // ThreadReady can arrive before StartTurnAsync returns and assigns ThreadId.
        var startingTab = notification.Kind == CodexAppServerChatEventKind.ThreadReady || string.IsNullOrEmpty(notification.ThreadId)
            ? StartingTab
            : null;
        var dispatch = _dispatcher;
        if (dispatch is null)
            ApplyEvent(notification, startingTab);
        else
            dispatch(() => ApplyEvent(notification, startingTab));
    }

    private void ApplyEvent(CodexAppServerChatEvent notification, ChatTab? startingTab)
    {
        var tab = startingTab ?? Tabs.FirstOrDefault(t => t.ThreadId == notification.ThreadId);
        if (tab is null || !Tabs.Contains(tab)) return;
        if (tab.IsBusy) tab.LastSignalAt = DateTimeOffset.Now;
        if (notification.Kind == CodexAppServerChatEventKind.ThreadReady)
        {
            tab.ThreadId = notification.ThreadId;
            if (tab.IsBusy) tab.ActivityText = "스레드 준비 중";
        }
        else if (notification.Kind == CodexAppServerChatEventKind.TurnStarted)
        {
            if (tab.IsBusy)
            {
                tab.TurnId = notification.TurnId;
                if (tab.ActivityText is "App-server 연결 중" or "스레드 준비 중")
                    tab.ActivityText = "요청 처리 중";
            }
        }
        else if (tab.IsBusy && (tab.TurnId is null || notification.TurnId is null || tab.TurnId == notification.TurnId))
        {
            switch (notification.Kind)
            {
                case CodexAppServerChatEventKind.AssistantDelta:
                    tab.ActivityText = "답변 작성 중";
                    tab.ActiveItemId = notification.ItemId;
                    if (tab.PendingAssistantIndex is not { } index)
                    {
                        tab.PendingAssistantIndex = tab.Messages.Count;
                        tab.Messages.Add(ChatMessage.Assistant(notification.Text ?? string.Empty));
                    }
                    else
                    {
                        var message = tab.Messages[index];
                        tab.Messages[index] = message with { Content = message.Content + notification.Text };
                    }
                    break;
                case CodexAppServerChatEventKind.ActivityStarted:
                    tab.ActivityText = notification.Text ?? "작업 진행 중";
                    tab.ActiveItemId = notification.ItemId;
                    break;
                case CodexAppServerChatEventKind.ActivityProgress:
                    var progressText = notification.Text ?? "작업 진행 중";
                    if (notification.ItemId != tab.ActiveItemId ||
                        !tab.ActivityText.StartsWith(progressText + ":", StringComparison.Ordinal))
                        tab.ActivityText = progressText;
                    if (notification.ItemId is not null) tab.ActiveItemId = notification.ItemId;
                    break;
                case CodexAppServerChatEventKind.ActivityCompleted:
                    if (tab.ActiveItemId is null || tab.ActiveItemId == notification.ItemId)
                    {
                        tab.ActivityText = notification.Text ?? "다음 단계 처리 중";
                        tab.ActiveItemId = null;
                    }
                    break;
                case CodexAppServerChatEventKind.CaptureToolResult:
                    tab.Messages.Add(ChatMessage.System(notification.Text ?? "Network capture tool executed."));
                    break;
                case CodexAppServerChatEventKind.ApprovalRequired:
                    if (notification.Approval is { } approval && !tab.PendingApprovals.Any(a => a.Id == approval.Id))
                        tab.PendingApprovals.Add(approval);
                    tab.ActivityText = "권한 승인 대기 중";
                    break;
                case CodexAppServerChatEventKind.ApprovalResolved:
                    var resolved = tab.PendingApprovals.FirstOrDefault(a => a.Id == notification.Approval?.Id);
                    if (resolved is not null) tab.PendingApprovals.Remove(resolved);
                    tab.ActivityText = "승인 결과 처리 중";
                    break;
                case CodexAppServerChatEventKind.TurnFailed:
                    tab.Messages.Add(ChatMessage.Error(notification.Text ?? "Codex could not complete this turn."));
                    FinishTurn(tab);
                    break;
                case CodexAppServerChatEventKind.TurnInterrupted:
                    tab.Messages.Add(ChatMessage.System("Turn interrupted."));
                    FinishTurn(tab);
                    break;
                case CodexAppServerChatEventKind.TurnCompleted:
                    FinishTurn(tab);
                    break;
            }
            if (tab.IsBusy && tab.PendingApprovals.Count > 0)
                tab.ActivityText = "권한 승인 대기 중";
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _chat.EventReceived -= OnChatEventReceived;
        TurnStartLock.Dispose();
        foreach (var tab in Tabs) tab.RunCts?.Dispose();
    }
}

public sealed class ChatTab
{
    public ChatTab(int tabNumber)
    {
        TabNumber = tabNumber;
        Title = $"Chat {tabNumber}";
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public int TabNumber { get; }
    public string Title { get; set; } = string.Empty;
    public string? ThreadId { get; set; }
    public string? TurnId { get; set; }
    public int? PendingAssistantIndex { get; set; }
    public IList<ChatMessage> Messages { get; } = new List<ChatMessage>();
    public string DraftMessage { get; set; } = string.Empty;
    public string PendingPromptNotice { get; set; } = string.Empty;
    public bool IsBusy { get; set; }
    public DateTimeOffset? BusyStartedAt { get; set; }
    public DateTimeOffset? LastSignalAt { get; set; }
    public string ActivityText { get; set; } = string.Empty;
    public string? ActiveItemId { get; set; }
    public CancellationTokenSource? RunCts { get; set; }
    public IList<CodexAppServerApproval> PendingApprovals { get; } = new List<CodexAppServerApproval>();
    public bool IsApprovalModalOpen => PendingApprovalRequest is not null;
    public CodexAppServerApproval? PendingApprovalRequest => PendingApprovals.FirstOrDefault();
    public bool IsResolvingApproval { get; set; }
}
