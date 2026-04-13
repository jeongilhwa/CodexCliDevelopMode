namespace CodexDesk.Services;

/// <summary>
/// Prompt templates for chat thread projections.
/// </summary>
internal static class ChatThreadsPromptTemplates
{
    internal const string ChatThreadsFileName = "completed-chat-threads.jsonl";

    internal static string BuildPathPrompt(string chatThreadsPath, string? question = null)
    {
        var normalizedPath = (chatThreadsPath ?? string.Empty).Trim();
        var prompt =
            $"Answer from `{normalizedPath}` first.{Environment.NewLine}" +
            $"If possible, read the conversation from `{ChatThreadsFileName}` before responding.";
        return AppendQuestion(prompt, question);
    }

    private static string AppendQuestion(string prompt, string? question)
    {
        var normalizedQuestion = (question ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalizedQuestion)
            ? prompt
            : $"{prompt} {normalizedQuestion}";
    }
}
