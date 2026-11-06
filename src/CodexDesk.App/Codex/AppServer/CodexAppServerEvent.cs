using System.Text.Json;

namespace CodexDesk.AppServer;

/// <summary>Represents one JSON-RPC notification emitted by Codex app-server.</summary>
/// <param name="Method">The notification method name.</param>
/// <param name="Parameters">The event payload supplied by app-server.</param>
public sealed record CodexAppServerEvent(string Method, JsonElement Parameters);
