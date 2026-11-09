using System.Text.Json;

namespace CodexDesk.AppServer;

/// <summary>Controls a persistent local Codex app-server session over stdio JSON-RPC.</summary>
public interface ICodexAppServerClient : IAsyncDisposable
{
    /// <summary>Occurs when app-server sends a JSON-RPC notification.</summary>
    event EventHandler<CodexAppServerEvent>? EventReceived;

    /// <summary>Occurs when app-server sends a JSON-RPC request that requires a response.</summary>
    event EventHandler<CodexAppServerServerRequest>? ServerRequestReceived;

    /// <summary>Sends the result of a server-initiated JSON-RPC request.</summary>
    /// <param name="requestId">The server-assigned identifier from the incoming request.</param>
    /// <param name="result">The result payload to send to app-server.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    Task RespondAsync(long requestId, object result, CancellationToken cancellationToken = default);

    /// <summary>Gets a value indicating whether a local app-server process is active.</summary>
    bool IsRunning { get; }

    /// <summary>Starts the child process and completes the initialization handshake.</summary>
    /// <param name="options">The local process and client identity configuration.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    Task StartAsync(CodexAppServerOptions options, CancellationToken cancellationToken = default);

    /// <summary>Starts a new persistent Codex thread.</summary>
    /// <param name="model">The requested model, or <see langword="null"/> for the Codex default.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The raw JSON-RPC result, including the server-assigned thread identifier.</returns>
    Task<JsonElement> StartThreadAsync(string? model = null, CancellationToken cancellationToken = default);

    /// <summary>Resumes an existing Codex thread after starting or restarting app-server.</summary>
    /// <param name="threadId">The saved Codex thread identifier.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The raw JSON-RPC result from app-server.</returns>
    Task<JsonElement> ResumeThreadAsync(string threadId, CancellationToken cancellationToken = default);

    /// <summary>Starts a user turn in an existing thread.</summary>
    /// <param name="threadId">The target Codex thread identifier.</param>
    /// <param name="message">The user message to send.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The raw JSON-RPC result from app-server.</returns>
    Task<JsonElement> StartTurnAsync(string threadId, string message, CancellationToken cancellationToken = default);

    /// <summary>Requests interruption of the active turn in a thread.</summary>
    /// <param name="threadId">The target Codex thread identifier.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    Task InterruptTurnAsync(string threadId, CancellationToken cancellationToken = default);
}
