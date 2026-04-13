using System.Text.Json.Nodes;
using CodexDesk.Services;
using CodexDesk.Tests.TestSupport;
using FluentAssertions;

namespace CodexDesk.Tests.Browser;

[TestClass]
public sealed class CompletedChatThreadsJsonlConverterTimeFallbackTests
{
    [TestMethod]
    public async Task ConvertFromCompletedChatsJsonAsync_should_use_html_status_time_when_time_label_is_empty()
    {
        using var tempDirectory = new TemporaryDirectory();
        var sourcePath = Path.Combine(tempDirectory.Path, "completed-chats.pending.json");
        var outputPath = Path.Combine(tempDirectory.Path, "completed-chat-threads.jsonl");

        await File.WriteAllTextAsync(
            sourcePath,
            """
            {
              "chats": [
                {
                  "route": {
                    "href": "/chat/ct/chat-1/room-1",
                    "name": "fallback"
                  },
                  "messages": [
                    {
                      "kind": "message",
                      "direction": "customer",
                      "text": "hello",
                      "dateLabel": "2026-04-10",
                      "timeLabel": "",
                      "html": "<span class=\"status_time _time\"><em>\uC624\uD6C4</em> <span>5:27</span></span>"
                    }
                  ]
                }
              ]
            }
            """);

        await CompletedChatThreadsJsonlConverter.ConvertFromCompletedChatsJsonAsync(sourcePath, outputPath);

        var line = (await File.ReadAllLinesAsync(outputPath)).Should().ContainSingle().Subject;
        var thread = JsonNode.Parse(line)!.AsObject();
        var message = thread["messages"]!.AsArray()[0]!.AsObject();

        message["occurredAt"]!.GetValue<string>().Should().Be("2026-04-10T17:27:00.0000000+09:00");
        thread["firstOccurredAt"]!.GetValue<string>().Should().Be("2026-04-10T17:27:00.0000000+09:00");
        thread["lastOccurredAt"]!.GetValue<string>().Should().Be("2026-04-10T17:27:00.0000000+09:00");
    }
}
