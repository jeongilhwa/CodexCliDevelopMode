using CodexDesk.Models;
using CodexDesk.Services;
using FluentAssertions;

namespace CodexDesk.Tests.Macros;

[TestClass]
public sealed class CodexPromptMacroRulesTests
{
    [TestMethod]
    public void TryCreateMacro_should_trim_values_and_assign_first_shortcut()
    {
        var created = CodexPromptMacroRules.TryCreateMacro(
            [],
            "  Analyze Bug  ",
            "  Explain the stack trace.  ",
            autoSend: true,
            out var macro,
            out var errorMessage);

        created.Should().BeTrue();
        errorMessage.Should().BeNull();
        macro.Should().Be(new CodexPromptMacroItem("Analyze Bug", "Explain the stack trace.", true, "Alt+Numpad1"));
    }

    [TestMethod]
    public void TryCreateMacro_should_reject_empty_text()
    {
        var created = CodexPromptMacroRules.TryCreateMacro(
            [],
            "Empty",
            "   ",
            autoSend: false,
            out var macro,
            out var errorMessage);

        created.Should().BeFalse();
        macro.Should().BeNull();
        errorMessage.Should().Be("매크로 텍스트가 비어 있습니다.");
    }

    [TestMethod]
    public void TryCreateMacro_should_fail_when_all_shortcuts_are_used()
    {
        var existingMacros = Enumerable.Range(1, 9)
            .Select(index => new CodexPromptMacroItem($"Macro {index}", $"Prompt {index}", false, $"Alt+Numpad{index}"))
            .ToList();

        var created = CodexPromptMacroRules.TryCreateMacro(
            existingMacros,
            "Overflow",
            "Prompt 10",
            autoSend: false,
            out var macro,
            out var errorMessage);

        created.Should().BeFalse();
        macro.Should().BeNull();
        errorMessage.Should().Be("사용 가능한 단축키가 없습니다. 기존 매크로를 삭제한 뒤 다시 등록해 주세요.");
    }

    [TestMethod]
    public void ReindexShortcuts_should_compact_shortcuts_in_sequence()
    {
        var reindexed = CodexPromptMacroRules.ReindexShortcuts(
        [
            new CodexPromptMacroItem("One", "Prompt 1", false, "Alt+Numpad1"),
            new CodexPromptMacroItem("Three", "Prompt 3", false, "Alt+Numpad3"),
            new CodexPromptMacroItem("Nine", "Prompt 9", true, "Alt+Numpad9")
        ]);

        reindexed.Select(static macro => macro.Shortcut).Should().ContainInOrder("Alt+Numpad1", "Alt+Numpad2", "Alt+Numpad3");
    }
}
