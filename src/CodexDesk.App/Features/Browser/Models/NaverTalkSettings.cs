namespace CodexDesk.Models;

public sealed record NaverTalkSettings(
    string TargetUrl,
    string LoginId,
    bool AutoLoginEnabled,
    string TypingMacroText,
    bool TypingMacroSendEnterEnabled,
    string TypingMacrosJson)
{
    public const string DefaultTargetUrl = "https://www.google.com";

    public static NaverTalkSettings Default => new(DefaultTargetUrl, string.Empty, false, string.Empty, false, "[]");
}
