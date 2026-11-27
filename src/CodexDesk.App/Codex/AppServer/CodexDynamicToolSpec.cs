using System.Text.Json;

namespace CodexDesk.AppServer;

/// <summary>A function that Codex can call through the app-server's client-handled tool protocol.</summary>
public sealed record CodexDynamicToolSpec(string Name, string Description, JsonElement InputSchema);
