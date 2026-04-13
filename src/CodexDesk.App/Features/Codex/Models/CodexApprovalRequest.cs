namespace CodexDesk.Models;

public sealed record CodexApprovalRequest(
    string Prompt,
    IReadOnlyList<CodexApprovalOption> Options);
