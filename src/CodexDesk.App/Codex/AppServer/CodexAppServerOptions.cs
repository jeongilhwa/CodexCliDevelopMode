namespace CodexDesk.AppServer;

/// <summary>Configures a local Codex app-server process.</summary>
public sealed class CodexAppServerOptions
{
    /// <summary>Gets or sets the Codex executable or command shim.</summary>
    public string CommandPath { get; set; } = "codex";

    /// <summary>Gets or sets the working directory supplied to the child process.</summary>
    public string WorkspacePath { get; set; } = Environment.CurrentDirectory;

    /// <summary>Gets or sets the stable app identifier reported during JSON-RPC initialization.</summary>
    public string ClientName { get; set; } = "codexdesk";

    /// <summary>Gets or sets the human-readable app title reported during JSON-RPC initialization.</summary>
    public string ClientTitle { get; set; } = "CodexDesk";

    /// <summary>Gets or sets the app version reported during JSON-RPC initialization.</summary>
    public string ClientVersion { get; set; } = "1.0";

    /// <summary>Gets a collection of additional, explicitly supplied app-server arguments.</summary>
    public IList<string> AdditionalArguments { get; } = new List<string>();

    /// <summary>Gets environment variables to add to the app-server child process.</summary>
    public IDictionary<string, string> EnvironmentVariables { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
