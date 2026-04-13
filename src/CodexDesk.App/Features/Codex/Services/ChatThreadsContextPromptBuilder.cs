namespace CodexDesk.Services;

/// <summary>
/// Builds the chat context prompt from the thread projection file.
/// </summary>
internal static class ChatThreadsContextPromptBuilder
{
    internal static string? TryBuildPrompt(string userPrompt)
    {
        try
        {
            var chatThreadsPath = CompletedChatsSupport.ResolveProjectDataPath(
                ChatThreadsPromptTemplates.ChatThreadsFileName);
            return BuildPrompt(
                userPrompt,
                File.Exists(chatThreadsPath) ? chatThreadsPath : null);
        }
        catch
        {
            return null;
        }
    }

    internal static string? BuildPrompt(
        string userPrompt,
        string? chatThreadsPath)
    {
        if (string.IsNullOrWhiteSpace(chatThreadsPath) || !File.Exists(chatThreadsPath))
        {
            return null;
        }

        return ChatThreadsPromptTemplates.BuildPathPrompt(chatThreadsPath, userPrompt);
    }
}
