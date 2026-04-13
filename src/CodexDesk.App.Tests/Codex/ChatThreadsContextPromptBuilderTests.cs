using CodexDesk.Services;
using CodexDesk.Tests.TestSupport;
using FluentAssertions;

namespace CodexDesk.Tests.Codex;

[TestClass]
public sealed class ChatThreadsContextPromptBuilderTests
{
    [TestMethod]
    public void BuildPathPrompt_should_point_to_chat_threads_only()
    {
        const string chatThreadsPath = @"C:\data\completed-chat-threads.jsonl";
        var prompt = ChatThreadsPromptTemplates.BuildPathPrompt(chatThreadsPath);

        prompt.Should().Contain("completed-chat-threads.jsonl");
        prompt.Should().NotContain("completed-chats-keyword-index");
        prompt.Should().Contain(chatThreadsPath);
    }

    [TestMethod]
    public void BuildPathPrompt_should_append_question_inline_when_provided()
    {
        const string question = "Can I upload an order list from Excel and print invoices?";
        const string chatThreadsPath = @"C:\data\completed-chat-threads.jsonl";

        var prompt = ChatThreadsPromptTemplates.BuildPathPrompt(chatThreadsPath, question);

        prompt.Should().Contain(question);
        prompt.Should().EndWith(question);
    }

    [TestMethod]
    public void BuildPrompt_should_return_chat_threads_prompt_when_file_exists()
    {
        using var tempDirectory = new TemporaryDirectory();
        var chatThreadsPath = Path.Combine(tempDirectory.Path, "completed-chat-threads.jsonl");
        File.WriteAllText(chatThreadsPath, "{}");
        const string question = "test question";

        var prompt = ChatThreadsContextPromptBuilder.BuildPrompt(
            question,
            chatThreadsPath);

        prompt.Should().NotBeNull();
        prompt.Should().Contain(chatThreadsPath);
        prompt.Should().Contain(question);
    }

    [TestMethod]
    public void BuildPrompt_should_return_null_when_chat_threads_file_is_missing()
    {
        var prompt = ChatThreadsContextPromptBuilder.BuildPrompt(
            "Can I register a banner via URL?",
            Path.Combine("C:\\missing", "completed-chat-threads.jsonl"));

        prompt.Should().BeNull();
    }
}
