namespace CodexDesk.Models;

public sealed record ChatMessage(string Role, string Content, DateTimeOffset Timestamp, bool IsError = false)
{
    public string CssClass =>
        Role switch
        {
            "assistant" when IsError => "message assistant error",
            "assistant" => "message assistant",
            "system" => "message system",
            _ => "message user"
        };

    public string RoleLabel =>
        Role switch
        {
            "assistant" => IsError ? "Codex Error" : "Codex",
            "system" => "Session",
            _ => "You"
        };

    public static ChatMessage User(string content) => new("user", content, DateTimeOffset.Now);

    public static ChatMessage Assistant(string content) => new("assistant", content, DateTimeOffset.Now);

    public static ChatMessage Error(string content) => new("assistant", content, DateTimeOffset.Now, true);

    public static ChatMessage System(string content) => new("system", content, DateTimeOffset.Now);
}
