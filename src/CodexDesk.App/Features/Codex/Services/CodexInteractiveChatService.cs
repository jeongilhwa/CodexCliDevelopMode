using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexDesk.Models;

namespace CodexDesk.Services;

public sealed class CodexInteractiveChatService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string GetDefaultCommandPath()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npmShim = Path.Combine(appDataPath, "npm", "codex.cmd");
        return File.Exists(npmShim) ? npmShim : "codex";
    }

    public string GetDefaultWorkspacePath()
    {
        foreach (var candidate in CodexWorkspaceLocator.GetCandidates())
        {
            var workspace = CodexWorkspaceLocator.FindWorkspaceRoot(candidate);
            if (workspace is not null)
            {
                return workspace;
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    public string? LaunchInteractiveCli(string workspacePath, string commandPath, string? model, bool allowWorkspaceWrite)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return "The workspace path does not exist.";
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                WorkingDirectory = workspacePath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
                Arguments = $"/k {CodexCliCommandBuilder.BuildVisibleCmdCommand(BuildVisibleCodexArgs(commandPath, model, allowWorkspaceWrite), workspacePath, "Codex CLI")}"
            };

            Process.Start(startInfo);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public string? LaunchHelpWindow(string workspacePath, string commandPath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return "The workspace path does not exist.";
        }

        try
        {
            var resolvedCommand = string.IsNullOrWhiteSpace(commandPath) ? GetDefaultCommandPath() : commandPath.Trim();
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                WorkingDirectory = workspacePath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
                Arguments = $"/k {CodexCliCommandBuilder.BuildVisibleCmdCommand($"{CodexCliCommandBuilder.QuoteForCmd(resolvedCommand)} --help", workspacePath, "Codex Help")}"
            };

            Process.Start(startInfo);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public string? LaunchResumeWindow(
        string workspacePath,
        string commandPath,
        string? model,
        bool allowWorkspaceWrite,
        string? threadId,
        string? transcriptPreview = null)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return "The workspace path does not exist.";
        }

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return LaunchInteractiveCli(workspacePath, commandPath, model, allowWorkspaceWrite);
        }

        try
        {
            var resolvedCommand = string.IsNullOrWhiteSpace(commandPath) ? GetDefaultCommandPath() : commandPath.Trim();
            var args = new List<string> { "resume", threadId.Trim() };

            var commandToRun = $"{CodexCliCommandBuilder.QuoteForCmd(resolvedCommand)} {string.Join(" ", args.Select(CodexCliCommandBuilder.QuoteForCmd))}";
            var previewCommand = BuildTranscriptPreviewCommand(workspacePath, transcriptPreview);
            if (!string.IsNullOrWhiteSpace(previewCommand))
            {
                commandToRun = $"{previewCommand} && {commandToRun}";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                WorkingDirectory = workspacePath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
                Arguments = $"/k {CodexCliCommandBuilder.BuildVisibleCmdCommand(commandToRun, workspacePath, "Codex Resume")}"
            };

            Process.Start(startInfo);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string? BuildTranscriptPreviewCommand(string workspacePath, string? transcriptPreview)
    {
        if (string.IsNullOrWhiteSpace(transcriptPreview))
        {
            return null;
        }

        try
        {
            var previewDir = Path.Combine(workspacePath, ".codex-build");
            Directory.CreateDirectory(previewDir);

            var previewFile = Path.Combine(previewDir, "current-thread-preview.txt");
            var normalized = transcriptPreview.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            File.WriteAllText(previewFile, normalized, Utf8NoBom);

            return $"echo ===== Current Conversation Preview ===== && type {CodexCliCommandBuilder.QuoteForCmd(previewFile)} && echo. && echo ========================================";
        }
        catch
        {
            return null;
        }
    }

    public string? LaunchWorkspaceShell(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return "The workspace path does not exist.";
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                WorkingDirectory = workspacePath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
                Arguments = $"/k {CodexCliCommandBuilder.BuildVisibleCmdCommand(string.Empty, workspacePath, "Workspace Shell")}"
            };

            Process.Start(startInfo);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task<CodexChatResult> SendAsync(CodexChatRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return new CodexChatResult(false, "The prompt is empty.", request.ThreadId, string.Empty, -1);
        }

        if (string.IsNullOrWhiteSpace(request.WorkspacePath) || !Directory.Exists(request.WorkspacePath))
        {
            return new CodexChatResult(false, "The workspace path does not exist.", request.ThreadId, string.Empty, -1);
        }

        var resolvedCommandPath = ResolveCommandPath(request.CommandPath);
        if (string.IsNullOrWhiteSpace(resolvedCommandPath))
        {
            return new CodexChatResult(
                false,
                "The codex command path is invalid. Check the CommandPath field.",
                request.ThreadId,
                string.Empty,
                -1);
        }

        string? threadId = request.ThreadId;
        string? lastError = null;
        string? lastAssistantMessage = null;
        CodexApprovalRequest? pendingApproval = null;

        var actualPrompt = request.Prompt;
        if (request.IncludeCompletedChatsContext)
        {
            var contextPrompt = TryBuildContextPrompt(request.Prompt);
            if (!string.IsNullOrWhiteSpace(contextPrompt))
            {
                actualPrompt = contextPrompt;
            }
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                WorkingDirectory = request.WorkspacePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = Utf8NoBom,
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom
            };

            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(CodexCliCommandBuilder.BuildExecCommandLine(request, resolvedCommandPath));

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdinTask = Task.Run(async () =>
            {
                try
                {
                    await process.StandardInput.WriteAsync(actualPrompt);
                    await process.StandardInput.WriteLineAsync();
                    process.StandardInput.Close();
                }
                catch (Exception ex)
                {
                    lastError ??= ex.Message;
                }
            }, cancellationToken);

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
            });

            var stdoutTask = PumpAsync(process.StandardOutput, line =>
            {
                ParseJsonEvent(line, ref threadId, ref lastError, ref lastAssistantMessage, ref pendingApproval);
            }, cancellationToken);

            var stderrTask = PumpAsync(process.StandardError, line =>
            {
                lastError = line;
            }, cancellationToken);

            await Task.WhenAll(stdinTask, stdoutTask, stderrTask, process.WaitForExitAsync(cancellationToken));

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(lastAssistantMessage))
            {
                return new CodexChatResult(true, lastAssistantMessage, threadId, string.Empty, process.ExitCode, pendingApproval);
            }

            var failureMessage =
                !string.IsNullOrWhiteSpace(lastError)
                    ? lastError
                    : !string.IsNullOrWhiteSpace(lastAssistantMessage)
                        ? lastAssistantMessage
                        : pendingApproval?.Prompt ?? $"Codex exited with code {process.ExitCode}.";

            return new CodexChatResult(false, failureMessage, threadId, string.Empty, process.ExitCode, pendingApproval);
        }
        catch (OperationCanceledException)
        {
            return new CodexChatResult(false, "The request was canceled.", threadId, string.Empty, -1, pendingApproval);
        }
        catch (Exception ex)
        {
            return new CodexChatResult(false, ex.Message, threadId, string.Empty, -1, pendingApproval);
        }
    }

    private string BuildVisibleCodexArgs(string commandPath, string? model, bool allowWorkspaceWrite)
    {
        var resolvedCommand = string.IsNullOrWhiteSpace(commandPath) ? GetDefaultCommandPath() : commandPath.Trim();
        return CodexCliCommandBuilder.BuildVisibleCodexArgs(resolvedCommand, model, allowWorkspaceWrite);
    }

    private string? ResolveCommandPath(string? commandPath)
    {
        var candidate = string.IsNullOrWhiteSpace(commandPath) ? GetDefaultCommandPath() : commandPath.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (File.Exists(candidate))
        {
            return candidate;
        }

        if (!candidate.Contains(Path.DirectorySeparatorChar) && !candidate.Contains(Path.AltDirectorySeparatorChar))
        {
            return candidate;
        }

        var fallback = GetDefaultCommandPath();
        return File.Exists(fallback) ? fallback : null;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            onLine(line);
        }
    }

    private static void ParseJsonEvent(string line, ref string? threadId, ref string? lastError, ref string? lastAssistantMessage, ref CodexApprovalRequest? pendingApproval)
    {
        if (!line.StartsWith('{'))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var eventType = TryGetEventType(root);
            if (string.IsNullOrWhiteSpace(eventType))
            {
                return;
            }

            if (eventType == "thread.started" && root.TryGetProperty("thread_id", out var threadProperty))
            {
                threadId = threadProperty.GetString();
            }

            if (eventType == "error" && root.TryGetProperty("message", out var messageProperty))
            {
                lastError = messageProperty.GetString();
            }

            if (eventType == "turn.failed" &&
                root.TryGetProperty("error", out var errorProperty) &&
                errorProperty.TryGetProperty("message", out var errorMessageProperty))
            {
                lastError = errorMessageProperty.GetString();
            }

            if (eventType == "message.completed" &&
                root.TryGetProperty("role", out var roleProperty) &&
                roleProperty.GetString() == "assistant" &&
                root.TryGetProperty("content", out var contentProperty))
            {
                lastAssistantMessage = contentProperty.GetString();
            }

            if (eventType == "item.completed" &&
                root.TryGetProperty("item", out var itemProperty) &&
                itemProperty.TryGetProperty("type", out var itemTypeProperty) &&
                itemTypeProperty.GetString() == "agent_message" &&
                itemProperty.TryGetProperty("text", out var textProperty))
            {
                lastAssistantMessage = textProperty.GetString();
            }

            if (TryExtractApprovalRequest(root, eventType, out var approvalRequest))
            {
                pendingApproval = approvalRequest;
            }
        }
        catch (JsonException)
        {
        }
    }

    private static bool TryExtractApprovalRequest(JsonElement root, string? eventType, out CodexApprovalRequest? approvalRequest)
    {
        approvalRequest = null;
        var normalized = eventType?.ToLowerInvariant() ?? string.Empty;
        if (!normalized.Contains("user_input", StringComparison.Ordinal) &&
            !normalized.Contains("exec_approval", StringComparison.Ordinal) &&
            !normalized.Contains("patch_approval", StringComparison.Ordinal) &&
            !normalized.Contains("request_user_input", StringComparison.Ordinal) &&
            !normalized.Contains("approval", StringComparison.Ordinal))
        {
            return false;
        }

        var sources = GetApprovalSources(root).ToArray();
        var prompt = sources
            .Select(BuildApprovalPrompt)
            .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text)) ??
            "Approval is required to continue.";

        var options = sources
            .Select(TryReadOptions)
            .FirstOrDefault(static items => items is { Count: > 0 }) ?? new List<CodexApprovalOption>
        {
            new("Approve", "y"),
            new("Reject", "n")
        };

        approvalRequest = new CodexApprovalRequest(prompt, options);
        return true;
    }

    private static List<CodexApprovalOption>? TryReadOptions(JsonElement element)
    {
        foreach (var key in new[] { "options", "choices", "items" })
        {
            if (!element.TryGetProperty(key, out var optionsProperty) || optionsProperty.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var options = new List<CodexApprovalOption>();
            foreach (var option in optionsProperty.EnumerateArray())
            {
                if (option.ValueKind == JsonValueKind.String)
                {
                    var value = option.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        options.Add(new CodexApprovalOption(value, value));
                    }

                    continue;
                }

                if (option.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var label = TryReadFirstString(option, "label", "title", "name", "text", "value") ?? "Option";
                var optionValue = TryReadFirstString(option, "value", "id", "label", "title", "text") ?? label;
                options.Add(new CodexApprovalOption(label, optionValue));
            }

            if (options.Count > 0)
            {
                return options;
            }
        }

        return null;
    }

    private static string? TryGetEventType(JsonElement root)
    {
        if (TryReadFirstString(root, "type") is { } topLevelType)
        {
            return topLevelType;
        }

        if (root.TryGetProperty("msg", out var messageProperty) &&
            TryReadFirstString(messageProperty, "type") is { } messageType)
        {
            return messageType;
        }

        if (root.TryGetProperty("item", out var itemProperty) &&
            TryReadFirstString(itemProperty, "type") is { } itemType)
        {
            return itemType;
        }

        return null;
    }

    private static IEnumerable<JsonElement> GetApprovalSources(JsonElement root)
    {
        yield return root;

        if (root.TryGetProperty("item", out var itemProperty))
        {
            yield return itemProperty;
        }

        if (root.TryGetProperty("msg", out var messageProperty))
        {
            yield return messageProperty;
        }

        if (root.TryGetProperty("params", out var paramsProperty))
        {
            yield return paramsProperty;
        }
    }

    private static string? BuildApprovalPrompt(JsonElement element)
    {
        var directPrompt = TryReadFirstString(
            element,
            "prompt",
            "question",
            "message",
            "text",
            "reason",
            "description",
            "title");

        if (!string.IsNullOrWhiteSpace(directPrompt))
        {
            return directPrompt;
        }

        var command = TryReadFirstString(element, "command", "cmd");
        var workingDirectory = TryReadFirstString(element, "cwd", "workdir", "working_directory");
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(workingDirectory)
            ? $"Approval required to run:{Environment.NewLine}{command}"
            : $"Approval required to run in {workingDirectory}:{Environment.NewLine}{command}";
    }

    private static string? TryReadFirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? TryBuildContextPrompt(string userPrompt)
    {
        return ChatThreadsContextPromptBuilder.TryBuildPrompt(userPrompt);
    }
}



