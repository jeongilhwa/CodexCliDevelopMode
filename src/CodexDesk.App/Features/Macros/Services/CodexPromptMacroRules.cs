using CodexDesk.Models;

namespace CodexDesk.Services;

internal static class CodexPromptMacroRules
{
    private static readonly string[] AutoMacroShortcuts =
    [
        "Alt+Numpad1",
        "Alt+Numpad2",
        "Alt+Numpad3",
        "Alt+Numpad4",
        "Alt+Numpad5",
        "Alt+Numpad6",
        "Alt+Numpad7",
        "Alt+Numpad8",
        "Alt+Numpad9"
    ];

    internal static bool TryCreateMacro(
        IReadOnlyList<CodexPromptMacroItem> existingMacros,
        string? name,
        string? text,
        bool autoSend,
        out CodexPromptMacroItem? macro,
        out string? errorMessage)
    {
        macro = null;
        errorMessage = null;

        var normalizedText = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            errorMessage = "매크로 텍스트가 비어 있습니다.";
            return false;
        }

        var shortcut = GetNextAutoShortcut(existingMacros);
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            errorMessage = "사용 가능한 단축키가 없습니다. 기존 매크로를 삭제한 뒤 다시 등록해 주세요.";
            return false;
        }

        var normalizedName = string.IsNullOrWhiteSpace(name)
            ? $"Prompt Macro {existingMacros.Count + 1}"
            : name.Trim();

        macro = new CodexPromptMacroItem(normalizedName, normalizedText, autoSend, shortcut);
        return true;
    }

    internal static string GetNextAutoShortcut(IEnumerable<CodexPromptMacroItem> macros)
    {
        var inUse = new HashSet<string>(
            macros
                .Select(static macro => macro.Shortcut)
                .Where(static shortcut => !string.IsNullOrWhiteSpace(shortcut)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var shortcut in AutoMacroShortcuts)
        {
            if (!inUse.Contains(shortcut))
            {
                return shortcut;
            }
        }

        return string.Empty;
    }

    internal static List<CodexPromptMacroItem> ReindexShortcuts(IEnumerable<CodexPromptMacroItem> macros)
    {
        var list = macros.ToList();
        for (var index = 0; index < list.Count; index++)
        {
            var shortcut = index < AutoMacroShortcuts.Length ? AutoMacroShortcuts[index] : string.Empty;
            list[index] = list[index] with { Shortcut = shortcut };
        }

        return list;
    }
}
