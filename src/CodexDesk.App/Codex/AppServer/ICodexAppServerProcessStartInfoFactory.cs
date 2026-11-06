using System.Diagnostics;

namespace CodexDesk.AppServer;

/// <summary>Creates the process configuration required to run Codex app-server.</summary>
public interface ICodexAppServerProcessStartInfoFactory
{
    /// <summary>Creates a stdio-based app-server process configuration.</summary>
    /// <param name="options">The child-process configuration.</param>
    /// <returns>A process start configuration that has not yet been started.</returns>
    ProcessStartInfo Create(CodexAppServerOptions options);
}
