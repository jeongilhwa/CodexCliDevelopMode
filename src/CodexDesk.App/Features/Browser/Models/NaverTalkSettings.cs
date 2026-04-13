namespace CodexDesk.Models;

public sealed record NaverTalkSettings(
    string TargetUrl,
    string LoginId,
    bool AutoLoginEnabled,
    string TypingMacroText,
    bool TypingMacroSendEnterEnabled,
    string TypingMacrosJson)
{
    public const string DefaultTargetUrl = "https://partner.talk.naver.com/chat/ct/wc5ymk?state=wait&device=pc";

    public static NaverTalkSettings Default => new(DefaultTargetUrl, string.Empty, true, string.Empty, false, "[]");
}
