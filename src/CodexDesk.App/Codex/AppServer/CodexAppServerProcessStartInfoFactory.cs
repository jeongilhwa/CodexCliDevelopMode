using System.Diagnostics;
using System.Text;

namespace CodexDesk.AppServer;

/// <summary>Builds the isolated stdio process configuration for Codex app-server.</summary>
public sealed class CodexAppServerProcessStartInfoFactory : ICodexAppServerProcessStartInfoFactory
{
    private const string AppServerCommand = "app-server";
    private const string ListenOption = "--listen";
    private const string StandardInputEndpoint = "stdio://";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <inheritdoc />
    public ProcessStartInfo Create(CodexAppServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.CommandPath))
        {
            throw new ArgumentException("A Codex command path is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.WorkspacePath))
        {
            throw new ArgumentException("A workspace path is required.", nameof(options));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.CommandPath.Trim(),
            WorkingDirectory = options.WorkspacePath.Trim(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };

        AddArguments(startInfo, options.AdditionalArguments);
        AddEnvironmentVariables(startInfo, options.EnvironmentVariables);

        return startInfo;
    }

    private static void AddArguments(ProcessStartInfo startInfo, IEnumerable<string> additionalArguments)
    {
        startInfo.ArgumentList.Add(AppServerCommand);
        startInfo.ArgumentList.Add(ListenOption);
        startInfo.ArgumentList.Add(StandardInputEndpoint);

        foreach (var argument in additionalArguments.Where(static argument => !string.IsNullOrWhiteSpace(argument)))
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static void AddEnvironmentVariables(ProcessStartInfo startInfo, IEnumerable<KeyValuePair<string, string>> environmentVariables)
    {
        foreach (var (key, value) in environmentVariables)
        {
            startInfo.Environment[key] = value;
        }
    }
}
