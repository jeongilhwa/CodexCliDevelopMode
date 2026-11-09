namespace CodexDesk.Models;

/// <summary>Identifies the operation requiring approval.</summary>
public enum CodexAppServerApprovalKind
{
    /// <summary>A command is waiting for permission to execute.</summary>
    CommandExecution,
    /// <summary>A file change is waiting for permission.</summary>
    FileChange
}

/// <summary>Contains modal details and the original server request correlation.</summary>
/// <param name="Id">The opaque identifier used to resolve this approval.</param>
/// <param name="RequestId">The original numeric JSON-RPC request identifier.</param>
/// <param name="Kind">The operation requiring approval.</param>
/// <param name="ThreadId">The requesting thread.</param>
/// <param name="TurnId">The requesting turn.</param>
/// <param name="ItemId">The requesting command or file-change item.</param>
/// <param name="Reason">The server's explanation, when supplied.</param>
/// <param name="Command">The command awaiting approval, when supplied.</param>
/// <param name="WorkingDirectory">The command's working directory, when supplied.</param>
/// <param name="GrantRoot">The file access root requested by a file-change approval.</param>
public sealed record CodexAppServerApproval(
    string Id,
    long RequestId,
    CodexAppServerApprovalKind Kind,
    string ThreadId,
    string? TurnId,
    string? ItemId,
    string? Reason,
    string? Command,
    string? WorkingDirectory,
    string? GrantRoot);
