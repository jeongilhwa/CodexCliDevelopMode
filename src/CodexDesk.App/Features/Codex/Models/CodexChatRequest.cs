namespace CodexDesk.Models;

public sealed record CodexChatRequest(
    string Prompt,
    string WorkspacePath,
    string CommandPath,
    string? Model,
    string? ThreadId,
    bool AllowWorkspaceWrite);
