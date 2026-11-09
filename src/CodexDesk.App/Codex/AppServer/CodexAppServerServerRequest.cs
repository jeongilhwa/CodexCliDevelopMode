using System.Text.Json;

namespace CodexDesk.AppServer;

/// <summary>Represents a JSON-RPC request from Codex app-server that requires a client response.</summary>
/// <param name="RequestId">The server-assigned request identifier to echo in the response.</param>
/// <param name="Method">The requested method name.</param>
/// <param name="Parameters">The request payload supplied by app-server.</param>
public sealed record CodexAppServerServerRequest(long RequestId, string Method, JsonElement Parameters);
