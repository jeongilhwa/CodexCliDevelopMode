namespace CodexDesk.Models;

/// <summary>Identifies a change in the persistent app-server conversation.</summary>
public enum CodexAppServerChatEventKind
{
    /// <summary>A thread has been created or resumed.</summary>
    ThreadReady,
    /// <summary>The server accepted a user turn.</summary>
    TurnStarted,
    /// <summary>Incremental assistant text is available.</summary>
    AssistantDelta,
    /// <summary>The turn completed successfully.</summary>
    TurnCompleted,
    /// <summary>The turn or an operation failed.</summary>
    TurnFailed,
    /// <summary>The server confirmed interruption.</summary>
    TurnInterrupted,
    /// <summary>The user must resolve a server approval request.</summary>
    ApprovalRequired,
    /// <summary>An approval response was sent successfully.</summary>
    ApprovalResolved
}

/// <summary>Transports UI state without exposing the JSON-RPC transport.</summary>
/// <param name="Kind">The state transition.</param>
/// <param name="ThreadId">The affected thread, or an empty string when startup failed before thread creation.</param>
/// <param name="TurnId">The affected turn, when known.</param>
/// <param name="Text">Incremental assistant text or an error message.</param>
/// <param name="Approval">The approval to display or dismiss.</param>
public sealed record CodexAppServerChatEvent(
    CodexAppServerChatEventKind Kind,
    string ThreadId,
    string? TurnId = null,
    string? Text = null,
    CodexAppServerApproval? Approval = null);
