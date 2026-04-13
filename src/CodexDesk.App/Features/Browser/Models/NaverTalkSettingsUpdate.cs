namespace CodexDesk.Models;

/// <summary>
/// 네이버 톡 브라우저 설정 저장 요청에 필요한 값을 한 번에 전달하는 DTO입니다.
/// 화면 계층이 긴 인자 목록 대신 의도가 드러나는 객체로 설정을 넘기도록 돕습니다.
/// </summary>
public sealed record NaverTalkSettingsUpdate(
    string TargetUrl,
    string LoginId,
    string? NewPasswordOrNull,
    bool AutoLoginEnabled,
    string TypingMacroText,
    bool TypingMacroSendEnterEnabled,
    string TypingMacrosJson);
