using CodexDesk.Models;

namespace CodexDesk.Services;

internal static class CodexCliCommandBuilder
{
    internal static string BuildExecCommandLine(CodexChatRequest request, string commandPath)
    {
        var args = new List<string> { "exec" };
        var isResume = !string.IsNullOrWhiteSpace(request.ThreadId);

        if (isResume)
        {
            args.Add("resume");
        }

        args.Add("--json");
        args.Add("--skip-git-repo-check");

        if (!isResume)
        {
            args.Add("--color");
            args.Add("never");
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            args.Add("--model");
            args.Add(request.Model.Trim());
        }

        if (request.AllowWorkspaceWrite)
        {
            AddWorkspaceWriteApprovalArgs(args);
        }
        else
        {
            args.Add("-s");
            args.Add("read-only");
        }

        if (isResume)
        {
            args.Add(request.ThreadId!.Trim());
        }

        args.Add("-");

        var escapedArgs = string.Join(" ", args.Select(QuoteForCmd));
        return $"chcp 65001 > nul && {QuoteForCmd(commandPath)} {escapedArgs}";
    }

    internal static string BuildVisibleCodexArgs(
        string commandPath,
        string? model,
        bool allowWorkspaceWrite,
        string? threadId = null,
        string? prompt = null)
    {
        var arguments = new List<string>();

        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("-m");
            arguments.Add(model.Trim());
        }

        if (allowWorkspaceWrite)
        {
            AddWorkspaceWriteApprovalArgs(arguments);
        }

        if (!string.IsNullOrWhiteSpace(threadId))
        {
            arguments.Add("resume");
            arguments.Add(threadId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            arguments.Add(prompt.Trim());
        }

        var formattedArgs = string.Join(" ", arguments.Select(QuoteForCmd));
        var suffix = string.IsNullOrWhiteSpace(formattedArgs) ? string.Empty : $" {formattedArgs}";
        return $"{QuoteForCmd(commandPath.Trim())}{suffix}";
    }

    internal static string BuildVisibleCmdCommand(string commandToRun, string workspacePath, string title)
    {
        var command = $"\"title {title} && chcp 65001 > nul && cd /d {QuoteForCmd(workspacePath)}";
        if (!string.IsNullOrWhiteSpace(commandToRun))
        {
            command += $" && {commandToRun}";
        }

        command += "\"";
        return command;
    }

    internal static string QuoteForCmd(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var needsQuotes = value.Contains(' ') || value.Contains('&') || value.Contains('|') || value.Contains('<') ||
                          value.Contains('>') || value.Contains('^') || value.Contains('(') || value.Contains(')');

        if (!needsQuotes && !value.Contains('"'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private static void AddWorkspaceWriteApprovalArgs(List<string> args)
    {
        args.Add("--approve-for-me");
    }
}


