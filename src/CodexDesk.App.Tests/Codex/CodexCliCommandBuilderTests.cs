using CodexDesk.Models;
using CodexDesk.Services;
using FluentAssertions;

namespace CodexDesk.Tests.Codex;

[TestClass]
public sealed class CodexCliCommandBuilderTests
{
    [TestMethod]
    public void BuildExecCommandLine_should_include_resume_and_read_only_flags()
    {
        var request = new CodexChatRequest(
            Prompt: "Explain the error",
            WorkspacePath: @"C:\repo",
            CommandPath: @"C:\Program Files\Codex\codex.cmd",
            Model: "gpt-5",
            ThreadId: "thread-123",
            AllowWorkspaceWrite: false);

        var commandLine = CodexCliCommandBuilder.BuildExecCommandLine(request, request.CommandPath);

        commandLine.Should().StartWith("chcp 65001 > nul && ");
        commandLine.Should().Contain("\"C:\\Program Files\\Codex\\codex.cmd\"");
        commandLine.Should().Contain(" exec ");
        commandLine.Should().Contain(" resume ");
        commandLine.Should().Contain(" --json ");
        commandLine.Should().Contain(" --skip-git-repo-check ");
        commandLine.Should().Contain(" --model ");
        commandLine.Should().Contain(" gpt-5 ");
        commandLine.Should().Contain(" -s ");
        commandLine.Should().Contain(" read-only ");
        commandLine.Should().Contain(" thread-123 ");
        commandLine.Should().NotContain(" --color ");
        commandLine.Should().NotContain(" --full-auto ");
    }

    [TestMethod]
    public void BuildExecCommandLine_should_include_color_and_full_auto_for_new_thread()
    {
        var request = new CodexChatRequest(
            Prompt: "Summarize the repo",
            WorkspacePath: @"C:\repo",
            CommandPath: "codex",
            Model: null,
            ThreadId: null,
            AllowWorkspaceWrite: true);

        var commandLine = CodexCliCommandBuilder.BuildExecCommandLine(request, request.CommandPath);

        commandLine.Should().Contain(" exec ");
        commandLine.Should().Contain(" --color ");
        commandLine.Should().Contain(" never ");
        commandLine.Should().Contain(" --full-auto ");
        commandLine.Should().NotContain(" resume ");
        commandLine.Should().NotContain(" read-only ");
    }

    [TestMethod]
    public void BuildVisibleCodexArgs_should_include_setup_prompt_when_provided()
    {
        var commandLine = CodexCliCommandBuilder.BuildVisibleCodexArgs(
            commandPath: "codex",
            model: "gpt-5",
            allowWorkspaceWrite: true,
            threadId: null,
            prompt: "check completed-chats first");

        commandLine.Should().Contain("codex");
        commandLine.Should().Contain("-m");
        commandLine.Should().Contain("gpt-5");
        commandLine.Should().Contain("--full-auto");
        commandLine.Should().Contain("\"check completed-chats first\"");
    }

    [TestMethod]
    public void QuoteForCmd_should_wrap_values_with_spaces_or_special_characters()
    {
        CodexCliCommandBuilder.QuoteForCmd(@"C:\Program Files\Codex\codex.cmd")
            .Should().Be("\"C:\\Program Files\\Codex\\codex.cmd\"");

        CodexCliCommandBuilder.QuoteForCmd(@"C:\tools\codex.cmd")
            .Should().Be(@"C:\tools\codex.cmd");
    }
}
