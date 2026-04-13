using System.Text.Json;
using System.Text.Json.Nodes;
using CodexDesk.Tests.TestSupport;
using CodexDesk.Services;
using FluentAssertions;

namespace CodexDesk.Tests.Browser;

[TestClass]
public sealed class CompletedChatsSupportTests
{
    [TestMethod]
    public void TryParsePartnerTalkDate_should_parse_supported_formats()
    {
        var referenceDate = new DateTime(2026, 4, 6);

        CompletedChatsSupport.TryParsePartnerTalkDate("\uC624\uB298", referenceDate, out var today).Should().BeTrue();
        today.Should().Be(referenceDate);

        CompletedChatsSupport.TryParsePartnerTalkDate("\uC5B4\uC81C", referenceDate, out var yesterday).Should().BeTrue();
        yesterday.Should().Be(referenceDate.AddDays(-1));

        CompletedChatsSupport.TryParsePartnerTalkDate("3\uC6D4 5\uC77C", referenceDate, out var koreanDate).Should().BeTrue();
        koreanDate.Should().Be(new DateTime(2026, 3, 5));

        CompletedChatsSupport.TryParsePartnerTalkDate("2026-04-01", referenceDate, out var isoDate).Should().BeTrue();
        isoDate.Should().Be(new DateTime(2026, 4, 1));

        CompletedChatsSupport.TryParsePartnerTalkDate("26. 4. 2", referenceDate, out var dottedDate).Should().BeTrue();
        dottedDate.Should().Be(new DateTime(2026, 4, 2));
    }

    [TestMethod]
    public async Task LoadFileAsync_should_extract_routes_partner_chat_id_and_latest_date()
    {
        using var tempDirectory = new TemporaryDirectory();
        var sourcePath = System.IO.Path.Combine(tempDirectory.Path, "completed-chats.json");

        var payload = """
        {
          "exportedAt": "2026-04-05T18:30:00+09:00",
          "chats": [
            {
              "route": {
                "href": "https://partner.talk.naver.com/chat/ct/chat-123?state=end",
                "timeLabel": "4\uC6D4 7\uC77C"
              },
              "messages": [
                { "dateLabel": "\uC5B4\uC81C" },
                { "dateLabel": "\uC624\uB298" }
              ]
            }
          ]
        }
        """;

        await File.WriteAllTextAsync(sourcePath, payload);

        var context = await CompletedChatsSupport.LoadFileAsync(sourcePath);

        context.KnownRouteHrefs.Should().ContainSingle()
            .Which.Should().Be("https://partner.talk.naver.com/chat/ct/chat-123?state=end");
        context.PartnerChatId.Should().Be("chat-123");
        context.ExportedAt.Should().Be(new DateTimeOffset(2026, 4, 5, 18, 30, 0, TimeSpan.FromHours(9)));
        context.LatestKnownDate.Should().Be(new DateTime(2026, 4, 5));
    }

    [TestMethod]
    public async Task DetermineCollectionStartDate_should_use_six_month_lookback_when_no_saved_chats_exist()
    {
        using var tempDirectory = new TemporaryDirectory();
        var sourcePath = System.IO.Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(sourcePath, """{ "chats": [] }""");

        var context = await CompletedChatsSupport.LoadFileAsync(sourcePath);
        var result = CompletedChatsSupport.DetermineCollectionStartDate(context, new DateTime(2026, 4, 8));

        result.IsInitialCollection.Should().BeTrue();
        result.FromDateSource.Should().Be("initialSixMonths");
        result.FromDate.Should().Be(new DateTime(2025, 10, 8));
    }

    [TestMethod]
    public async Task DetermineCollectionStartDate_should_prefer_exported_at_when_saved_chats_exist()
    {
        using var tempDirectory = new TemporaryDirectory();
        var sourcePath = System.IO.Path.Combine(tempDirectory.Path, "completed-chats.json");

        var payload = """
        {
          "exportedAt": "2026-04-05T18:30:00+09:00",
          "chats": [
            {
              "route": {
                "href": "https://partner.talk.naver.com/chat/ct/chat-123?state=end",
                "timeLabel": "4\uC6D4 4\uC77C"
              },
              "messages": [
                { "dateLabel": "4\uC6D4 4\uC77C" }
              ]
            }
          ]
        }
        """;

        await File.WriteAllTextAsync(sourcePath, payload);

        var context = await CompletedChatsSupport.LoadFileAsync(sourcePath);
        var result = CompletedChatsSupport.DetermineCollectionStartDate(context, new DateTime(2026, 4, 8));

        result.IsInitialCollection.Should().BeFalse();
        result.FromDateSource.Should().Be("exportedAt");
        result.FromDate.Should().Be(new DateTime(2026, 4, 5));
    }

    [TestMethod]
    public void TryExtractPartnerChatIdFromPageText_should_extract_id_from_account_summary_text()
    {
        var pageText = """
        판매처 이름

        샵마인
        wc5ymk

        톡톡 사용중
        """;

        var result = CompletedChatsSupport.TryExtractPartnerChatIdFromPageText(pageText);

        result.Should().Be("wc5ymk");
    }

    [TestMethod]
    public void TryExtractPartnerChatIdFromPageText_should_ignore_numeric_account_id_lines()
    {
        var pageText = """
        \uD310\uB9E4\uCC98 \uC774\uB984

        \uC0F5\uB9C8\uC778
        100268217

        \uD1A1\uD1A1 \uC0AC\uC6A9\uC911
        """;

        var result = CompletedChatsSupport.TryExtractPartnerChatIdFromPageText(pageText);

        result.Should().BeNull();
    }

    [TestMethod]
    public void TryResolvePartnerChatId_should_use_page_text_when_url_and_anchor_do_not_have_ct_id()
    {
        var result = CompletedChatsSupport.TryResolvePartnerChatId(
            "https://partner.talk.naver.com/web/accounts/100268217/chat?state=end&device=pc",
            string.Empty,
            string.Empty,
            """
            \uD310\uB9E4\uCC98 \uC774\uB984
            \uC0F5\uB9C8\uC778
            wc5ymk
            \uD1A1\uD1A1 \uC0AC\uC6A9\uC911
            """);

        result.Should().Be("wc5ymk");
    }

    [TestMethod]
    public void BuildCollectionResult_should_remove_html_and_mark_baseline_updates()
    {
        var collectedChats = JsonNode.Parse("""
        [
          {
            "route": { "href": "https://partner.talk.naver.com/chat/ct/chat-123" },
            "messages": [
              { "dateLabel": "\uC624\uB298", "html": "<div>hidden</div>", "text": "visible" }
            ]
          }
        ]
        """)!.AsArray();

        var result = CompletedChatsSupport.BuildCollectionResult(
            succeeded: true,
            summary: "Collected",
            outputPath: @"C:\Data\completed-chats.json",
            baselinePath: @"c:\data\completed-chats.json",
            fromDate: new DateTime(2026, 4, 5),
            discoveredRouteCount: 5,
            newRouteCount: 4,
            savedCount: 3,
            sampleItems: ["chat-123"],
            jsonOptions: new JsonSerializerOptions(),
            collectedChatNodes: collectedChats);

        result.UpdatedBaseline.Should().BeTrue();
        result.SkippedCount.Should().Be(1);
        result.SampleItems.Should().ContainSingle().Which.Should().Be("chat-123");

        var payload = JsonNode.Parse(result.CollectedDataJson)!.AsObject();
        payload["savedCount"]!.GetValue<int>().Should().Be(3);
        payload["chats"]!.AsArray()[0]!["messages"]!.AsArray()[0]!["html"].Should().BeNull();
        payload["chats"]!.AsArray()[0]!["messages"]!.AsArray()[0]!["text"]!.GetValue<string>().Should().Be("visible");
    }

    [TestMethod]
    public void MergeCompletedChatsPayload_should_keep_source_order_and_append_missing_target_chats()
    {
        var sourceRoot = JsonNode.Parse(
            """
            {
              "exportedAt": "2026-04-10T00:00:00+09:00",
              "chats": [
                { "route": { "href": "/chat/ct/a/1", "name": "new" } },
                { "route": { "href": "/chat/ct/a/2", "name": "existing-updated" } }
              ]
            }
            """)!.AsObject();

        var targetRoot = JsonNode.Parse(
            """
            {
              "chats": [
                { "route": { "href": "/chat/ct/a/2", "name": "existing-old" } },
                { "route": { "href": "/chat/ct/a/3", "name": "legacy-only" } }
              ]
            }
            """)!.AsObject();

        var merged = CompletedChatsSupport.MergeCompletedChatsPayload(sourceRoot, targetRoot);
        var chats = merged["chats"]!.AsArray();

        chats.Should().HaveCount(3);
        chats[0]!["route"]!["href"]!.GetValue<string>().Should().Be("/chat/ct/a/1");
        chats[1]!["route"]!["href"]!.GetValue<string>().Should().Be("/chat/ct/a/2");
        chats[2]!["route"]!["href"]!.GetValue<string>().Should().Be("/chat/ct/a/3");
        merged["totalRoutes"]!.GetValue<int>().Should().Be(3);
        merged["exportedChats"]!.GetValue<int>().Should().Be(3);
    }

    [TestMethod]
    public async Task MergeCompletedChatsFilesAsync_should_write_merged_payload_to_target_file()
    {
        using var tempDirectory = new TemporaryDirectory();
        var sourcePath = System.IO.Path.Combine(tempDirectory.Path, "completed-chats.pending.json");
        var targetPath = System.IO.Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(
            sourcePath,
            """
            {
              "chats": [
                { "route": { "href": "/chat/ct/a/10", "name": "new" } }
              ]
            }
            """);

        await File.WriteAllTextAsync(
            targetPath,
            """
            {
              "chats": [
                { "route": { "href": "/chat/ct/a/11", "name": "old" } }
              ]
            }
            """);

        await CompletedChatsSupport.MergeCompletedChatsFilesAsync(sourcePath, targetPath);

        var merged = JsonNode.Parse(await File.ReadAllTextAsync(targetPath))!.AsObject();
        var chats = merged["chats"]!.AsArray();
        chats.Should().HaveCount(2);
        chats[0]!["route"]!["href"]!.GetValue<string>().Should().Be("/chat/ct/a/10");
        chats[1]!["route"]!["href"]!.GetValue<string>().Should().Be("/chat/ct/a/11");
    }

    [TestMethod]
    public void ResolveProjectDataPath_should_use_project_root_in_development_layout()
    {
        using var tempDirectory = new TemporaryDirectory();
        var projectRoot = System.IO.Path.Combine(tempDirectory.Path, "src", "CodexDesk.App");
        var baseDirectory = System.IO.Path.Combine(projectRoot, "bin", "Debug", "net10.0-windows10.0.19041.0", "win-x64");
        var localAppDataRoot = System.IO.Path.Combine(tempDirectory.Path, "user-data");

        Directory.CreateDirectory(baseDirectory);
        File.WriteAllText(System.IO.Path.Combine(projectRoot, "CodexDesk.App.csproj"), "<Project />");

        var resolvedPath = CompletedChatsSupport.ResolveProjectDataPath("completed-chats.json", baseDirectory, localAppDataRoot);

        resolvedPath.Should().Be(System.IO.Path.Combine(projectRoot, "completed-chats.json"));
        Directory.Exists(System.IO.Path.Combine(localAppDataRoot, "CodexDesk")).Should().BeFalse();
    }

    [TestMethod]
    public void ResolveProjectDataPath_should_copy_seed_file_to_user_data_in_installed_layout()
    {
        using var tempDirectory = new TemporaryDirectory();
        var installDirectory = System.IO.Path.Combine(tempDirectory.Path, "Program Files", "Vendor", "CodexDesk");
        var localAppDataRoot = System.IO.Path.Combine(tempDirectory.Path, "user-data");
        var packagedFilePath = System.IO.Path.Combine(installDirectory, "completed-chats.json");

        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(packagedFilePath, "{\"seed\":true}");

        var resolvedPath = CompletedChatsSupport.ResolveProjectDataPath("completed-chats.json", installDirectory, localAppDataRoot);

        resolvedPath.Should().Be(System.IO.Path.Combine(localAppDataRoot, "CodexDesk", "completed-chats.json"));
        File.Exists(resolvedPath).Should().BeTrue();
        File.ReadAllText(resolvedPath).Should().Be("{\"seed\":true}");
    }
}
