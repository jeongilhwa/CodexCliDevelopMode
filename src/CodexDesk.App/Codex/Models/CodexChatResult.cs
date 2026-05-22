namespace CodexDesk.Models;

public sealed record CodexChatResult(
    bool Success,
    string Message,
    string? ThreadId,
    string ExecutionLog,
    int ExitCode,
    CodexApprovalRequest? PendingApproval = null);
