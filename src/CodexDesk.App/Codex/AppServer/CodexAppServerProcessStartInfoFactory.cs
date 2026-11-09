using System.Diagnostics;
using System.Text;

namespace CodexDesk.AppServer;

/// <summary>Builds the isolated stdio process configuration for Codex app-server.</summary>
public sealed class CodexAppServerProcessStartInfoFactory : ICodexAppServerProcessStartInfoFactory
{
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

        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");

        foreach (var argument in options.AdditionalArguments.Where(static argument => !string.IsNullOrWhiteSpace(argument)))
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in options.EnvironmentVariables)
        {
            startInfo.Environment[key] = value;
        }

        return startInfo;
    }
}
