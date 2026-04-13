using System.Text.Json.Nodes;
using CodexDesk.Services;
using CodexDesk.Tests.TestSupport;
using FluentAssertions;

namespace CodexDesk.Tests.Browser;

[TestClass]
public sealed class CompletedChatThreadsJsonlConverterTests
{
    [TestMethod]
    public async Task ConvertFromCompletedChatsJsonAsync_should_project_chat_to_single_jsonl_line()
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
                    "href": "/chat/ct/wc5ymk/alHwu",
                    "name": "테스트고객"
                  },
                  "infoRows": [
                    { "label": "order", "value": "12345" }
                  ],
                  "messages": [
                    {
                      "kind": "message",
                      "direction": "customer",
                      "text": "내일 확인 부탁드립니다",
                      "dateLabel": "2026-04-01",
                      "timeLabel": "오후 5:27"
                    },
                    {
                      "kind": "message",
                      "direction": "partner",
                      "text": "네 알겠습니다",
                      "dateLabel": "2026-04-01",
                      "timeLabel": "오후 5:28"
                    }
                  ]
                }
              ]
            }
            """);

        await CompletedChatThreadsJsonlConverter.ConvertFromCompletedChatsJsonAsync(sourcePath, outputPath);

        var lines = await File.ReadAllLinesAsync(outputPath);
        lines.Should().HaveCount(1);

        var thread = JsonNode.Parse(lines[0])!.AsObject();
        thread["threadId"]!.GetValue<string>().Should().Be("wc5ymk/alHwu");
        thread["partnerChatId"]!.GetValue<string>().Should().Be("wc5ymk");
        thread["roomId"]!.GetValue<string>().Should().Be("alHwu");
        thread["routeHref"]!.GetValue<string>().Should().Be("/chat/ct/wc5ymk/alHwu");
        thread["customerName"]!.GetValue<string>().Should().Be("테스트고객");
        thread["messageCount"]!.GetValue<int>().Should().Be(2);
        thread["info"]!["order"]!.GetValue<string>().Should().Be("12345");
        thread["messages"]!.AsArray().Should().HaveCount(2);
        thread["conversationText"]!.GetValue<string>().Should().Contain("[customer] 내일 확인 부탁드립니다");
    }
}
