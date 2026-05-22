namespace CodexDesk.Models;

public sealed record CodexPromptMacroItem(string Name, string Text, bool AutoSend, string Shortcut = "");
