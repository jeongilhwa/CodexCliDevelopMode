using System.Text.Json.Nodes;
using CodexDesk.Services;
using CodexDesk.Tests.TestSupport;
using FluentAssertions;

namespace CodexDesk.Tests.Browser;

[TestClass]
public sealed class CompletedChatsCollectorTests
{
    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_complete_when_only_known_routes_are_returned()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(
            baselinePath,
            """
            {
              "exportedAt": "2026-04-05T18:30:00+09:00",
              "chats": [
                {
                  "route": {
                    "href": "https://partner.talk.naver.com/chat/ct/chat-123?state=end",
                    "timeLabel": "2026-04-05"
                  },
                  "messages": [
                    { "dateLabel": "2026-04-05" }
                  ]
                }
              ]
            }
            """);

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession
        {
            PartnerChatId = "chat-123",
            PartnerChatIdSource = "baseline",
            RouteLoadResult = new CompletedChatsRouteLoadResult(
                Routes:
                [
                    new CompletedChatRoute(
                        "https://partner.talk.naver.com/chat/ct/chat-123?state=end",
                        "known chat",
                        "2026-04-05",
                        "preview",
                        "https://partner.talk.naver.com/chat/ct/chat-123?state=end&device=pc",
                        1712246400000)
                ],
                ApiAttempted: true,
                ApiOk: true)
        };

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        browserService.LastCompletedChatCollectionResult.Should().NotBeNull();
        browserService.LastCompletedChatCollectionResult!.Succeeded.Should().BeTrue();
        browserService.LastCompletedChatCollectionResult.NewRouteCount.Should().Be(0);
        browserService.LastCompletedChatCollectionResult.SavedCount.Should().Be(0);
        session.CaptureRouteDiagnosticsCalls.Should().Be(0);
        session.LoadDetailCalls.Should().Be(0);
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_write_pending_result_when_new_chat_is_collected()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(baselinePath, """{ "chats": [] }""");

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession
        {
            PartnerChatId = "chat-456",
            PartnerChatIdSource = "pageText",
            RouteLoadResult = new CompletedChatsRouteLoadResult(
                Routes:
                [
                    new CompletedChatRoute(
                        "/chat/ct/chat-456/room-1",
                        "new chat",
                        "2026-04-08",
                        "preview text",
                        "https://partner.talk.naver.com/chat/ct/chat-456/room-1?state=end&device=pc",
                        1712530800000)
                ],
                ApiAttempted: true,
                ApiOk: true),
            DetailByHref =
            {
                ["/chat/ct/chat-456/room-1"] = new CompletedChatDetail(
                    "Detail Title",
                    "https://partner.talk.naver.com/chat/ct/chat-456/room-1?state=end&device=pc",
                    "Header",
                    [new CompletedChatInfoRow("Order", "12345")],
                    [new CompletedChatMessage("message", 12, "2026-04-08", "customer", "Kim", "Hello", "09:00", "", "balloon_item", "<li>msg</li>")])
            }
        };

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        var result = browserService.LastCompletedChatCollectionResult;
        result.Should().NotBeNull();
        result!.Succeeded.Should().BeTrue();
        result.SavedCount.Should().Be(1);
        File.Exists(result.OutputPath).Should().BeTrue();

        var savedPayload = JsonNode.Parse(await File.ReadAllTextAsync(result.OutputPath))!.AsObject();
        savedPayload["exportedChats"]!.GetValue<int>().Should().Be(1);
        savedPayload["chats"]!.AsArray().Should().HaveCount(1);
        var threadProjectionPath = Path.Combine(tempDirectory.Path, "completed-chat-threads.jsonl");
        File.Exists(threadProjectionPath).Should().BeTrue();
        session.LoadDetailCalls.Should().Be(1);
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_capture_diagnostics_when_no_routes_are_found()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(baselinePath, """{ "chats": [] }""");

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession
        {
            PartnerChatId = "chat-789",
            PartnerChatIdSource = "settingsTargetUrl",
            RouteLoadResult = new CompletedChatsRouteLoadResult(
                Routes: [],
                ApiAttempted: true,
                ApiOk: false,
                ApiError: "list api failed: 500")
        };

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        var result = browserService.LastCompletedChatCollectionResult;
        result.Should().NotBeNull();
        result!.Succeeded.Should().BeFalse();
        session.CaptureRouteDiagnosticsCalls.Should().Be(1);
        result.CollectedDataJson.Should().Contain("list api failed: 500");
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_use_up_to_three_parallel_workers_for_detail_scraping()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(baselinePath, """{ "chats": [] }""");

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession
        {
            RouteLoadResult = new CompletedChatsRouteLoadResult(
                Routes:
                [
                    CreateRoute("chat-1", "room-1", "chat one", 3000),
                    CreateRoute("chat-1", "room-2", "chat two", 2000),
                    CreateRoute("chat-1", "room-3", "chat three", 1000),
                    CreateRoute("chat-1", "room-4", "chat four", 500)
                ],
                ApiAttempted: true,
                ApiOk: true)
        };

        foreach (var route in session.RouteLoadResult.Routes)
        {
            session.DetailByHref[route.Href] = CreateDetail(route.AbsoluteUrl, route.Name);
            session.DelayByHref[route.Href] = TimeSpan.FromMilliseconds(150);
        }

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        session.LastRequestedWorkerCount.Should().Be(3);
        session.MaxConcurrentLoadDetailCalls.Should().BeGreaterThan(1);
        browserService.LastCompletedChatCollectionResult!.SavedCount.Should().Be(4);
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_request_single_worker_when_only_one_route_exists()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(baselinePath, """{ "chats": [] }""");

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var route = CreateRoute("chat-2", "room-1", "single chat", 1000);
        var session = new FakeCompletedChatsBrowserSession
        {
            RouteLoadResult = new CompletedChatsRouteLoadResult([route], ApiAttempted: true, ApiOk: true),
            DetailByHref =
            {
                [route.Href] = CreateDetail(route.AbsoluteUrl, route.Name)
            }
        };

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        session.LastRequestedWorkerCount.Should().Be(1);
        browserService.LastCompletedChatCollectionResult!.SavedCount.Should().Be(1);
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_honor_custom_output_path_when_provided()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");
        var customOutputPath = Path.Combine(tempDirectory.Path, "custom-output.json");

        await File.WriteAllTextAsync(baselinePath, """{ "chats": [] }""");

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var route = CreateRoute("chat-3", "room-1", "custom output chat", 1000);
        var session = new FakeCompletedChatsBrowserSession
        {
            RouteLoadResult = new CompletedChatsRouteLoadResult([route], ApiAttempted: true, ApiOk: true),
            DetailByHref =
            {
                [route.Href] = CreateDetail(route.AbsoluteUrl, route.Name)
            }
        };

        await collector.CollectCompletedChatsAsync(
            session,
            baselineFileName: baselinePath,
            outputFileName: customOutputPath);

        var result = browserService.LastCompletedChatCollectionResult!;
        result.OutputPath.Should().Be(customOutputPath);
        File.Exists(customOutputPath).Should().BeTrue();
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_fail_when_every_detail_is_empty()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(baselinePath, """{ "chats": [] }""");

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession
        {
            RouteLoadResult = new CompletedChatsRouteLoadResult(
                Routes:
                [
                    CreateRoute("chat-4", "room-1", "empty one", 2000),
                    CreateRoute("chat-4", "room-2", "empty two", 1000)
                ],
                ApiAttempted: true,
                ApiOk: true)
        };

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        var result = browserService.LastCompletedChatCollectionResult!;
        result.Succeeded.Should().BeFalse();
        result.SavedCount.Should().Be(0);
        File.Exists(result.OutputPath).Should().BeFalse();
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_continue_when_one_detail_worker_fails()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(baselinePath, """{ "chats": [] }""");

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var successfulRoute = CreateRoute("chat-5", "room-1", "survived", 2000);
        var failedRoute = CreateRoute("chat-5", "room-2", "failed", 1000);
        var session = new FakeCompletedChatsBrowserSession
        {
            RouteLoadResult = new CompletedChatsRouteLoadResult(
                [successfulRoute, failedRoute],
                ApiAttempted: true,
                ApiOk: true),
            DetailByHref =
            {
                [successfulRoute.Href] = CreateDetail(successfulRoute.AbsoluteUrl, successfulRoute.Name)
            },
            ExceptionByHref =
            {
                [failedRoute.Href] = new InvalidOperationException("detail boom")
            }
        };

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        var result = browserService.LastCompletedChatCollectionResult!;
        result.Succeeded.Should().BeTrue();
        result.SavedCount.Should().Be(1);
        result.CollectedDataJson.Should().Contain("detail boom");
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_limit_sample_items_to_eight()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(baselinePath, """{ "chats": [] }""");

        var routes = Enumerable.Range(1, 10)
            .Select(index => CreateRoute("chat-6", $"room-{index}", $"chat {index}", 10000 - index))
            .ToArray();

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession
        {
            RouteLoadResult = new CompletedChatsRouteLoadResult(routes, ApiAttempted: true, ApiOk: true)
        };

        foreach (var route in routes)
        {
            session.DetailByHref[route.Href] = CreateDetail(route.AbsoluteUrl, route.Name);
        }

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        browserService.LastCompletedChatCollectionResult!.SampleItems.Should().HaveCount(8);
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_preserve_route_order_even_when_parallel_workers_finish_out_of_order()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(baselinePath, """{ "chats": [] }""");

        var newestRoute = CreateRoute("chat-7", "room-newest", "newest", 3000);
        var middleRoute = CreateRoute("chat-7", "room-middle", "middle", 2000);
        var oldestRoute = CreateRoute("chat-7", "room-oldest", "oldest", 1000);

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession
        {
            RouteLoadResult = new CompletedChatsRouteLoadResult(
                [newestRoute, middleRoute, oldestRoute],
                ApiAttempted: true,
                ApiOk: true),
            DelayByHref =
            {
                [newestRoute.Href] = TimeSpan.FromMilliseconds(300),
                [middleRoute.Href] = TimeSpan.FromMilliseconds(200),
                [oldestRoute.Href] = TimeSpan.FromMilliseconds(50)
            }
        };

        session.DetailByHref[newestRoute.Href] = CreateDetail(newestRoute.AbsoluteUrl, newestRoute.Name);
        session.DetailByHref[middleRoute.Href] = CreateDetail(middleRoute.AbsoluteUrl, middleRoute.Name);
        session.DetailByHref[oldestRoute.Href] = CreateDetail(oldestRoute.AbsoluteUrl, oldestRoute.Name);

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        var outputPath = browserService.LastCompletedChatCollectionResult!.OutputPath;
        var savedPayload = JsonNode.Parse(await File.ReadAllTextAsync(outputPath))!.AsObject();
        var savedNames = savedPayload["chats"]!.AsArray()
            .Select(node => node!["route"]!["name"]!.GetValue<string>())
            .ToArray();

        savedNames.Should().ContainInOrder("newest", "middle", "oldest");
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_write_pending_file_next_to_baseline_by_default()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(baselinePath, """{ "chats": [] }""");

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var route = CreateRoute("chat-8", "room-1", "pending path chat", 1000);
        var session = new FakeCompletedChatsBrowserSession
        {
            RouteLoadResult = new CompletedChatsRouteLoadResult([route], ApiAttempted: true, ApiOk: true),
            DetailByHref =
            {
                [route.Href] = CreateDetail(route.AbsoluteUrl, route.Name)
            }
        };

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        browserService.LastCompletedChatCollectionResult!.OutputPath
            .Should().Be(Path.Combine(tempDirectory.Path, "completed-chats.pending.json"));
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_record_detail_worker_statistics_in_result_debug_payload()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(baselinePath, """{ "chats": [] }""");

        var firstRoute = CreateRoute("chat-9", "room-1", "saved", 2000);
        var secondRoute = CreateRoute("chat-9", "room-2", "errored", 1000);

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession
        {
            RouteLoadResult = new CompletedChatsRouteLoadResult([firstRoute, secondRoute], ApiAttempted: true, ApiOk: true),
            DetailByHref =
            {
                [firstRoute.Href] = CreateDetail(firstRoute.AbsoluteUrl, firstRoute.Name)
            },
            ExceptionByHref =
            {
                [secondRoute.Href] = new InvalidOperationException("worker failure")
            }
        };

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        var payload = JsonNode.Parse(browserService.LastCompletedChatCollectionResult!.CollectedDataJson)!.AsObject();
        payload["debug"]!["detailWorkerCount"]!.GetValue<int>().Should().Be(2);
        payload["debug"]!["detailErrorCount"]!.GetValue<int>().Should().Be(1);
        payload["debug"]!["detailErrors"]!.AsArray().Should().HaveCount(1);
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_use_initial_six_month_window_when_baseline_file_is_missing()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession();

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        session.LastFromDate.Should().Be(new DateTime(2025, 10, 8));
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_use_exported_at_date_for_incremental_collection()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(
            baselinePath,
            """
            {
              "exportedAt": "2026-04-03T09:30:00+09:00",
              "chats": [
                {
                  "route": {
                    "href": "https://partner.talk.naver.com/chat/ct/chat-10?state=end",
                    "timeLabel": "2026-04-02"
                  },
                  "messages": []
                }
              ]
            }
            """);

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession();

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        session.LastFromDate.Should().Be(new DateTime(2026, 4, 3));
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_use_latest_known_date_when_exported_at_is_missing()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(
            baselinePath,
            """
            {
              "chats": [
                {
                  "route": {
                    "href": "https://partner.talk.naver.com/chat/ct/chat-11?state=end",
                    "timeLabel": "2026-04-04"
                  },
                  "messages": [
                    { "dateLabel": "2026-04-04" }
                  ]
                }
              ]
            }
            """);

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession();

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        session.LastFromDate.Should().Be(new DateTime(2026, 4, 4));
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_pass_known_routes_to_route_loader()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(
            baselinePath,
            """
            {
              "chats": [
                {
                  "route": {
                    "href": "/chat/ct/chat-12/room-1",
                    "timeLabel": "2026-04-04"
                  },
                  "messages": []
                },
                {
                  "route": {
                    "href": "/chat/ct/chat-12/room-2",
                    "timeLabel": "2026-04-05"
                  },
                  "messages": []
                }
              ]
            }
            """);

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession();

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        session.LastKnownRoutes.Should().BeEquivalentTo(
            ["/chat/ct/chat-12/room-1", "/chat/ct/chat-12/room-2"]);
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_pass_baseline_partner_chat_id_to_resolver()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(
            baselinePath,
            """
            {
              "chats": [
                {
                  "route": {
                    "href": "/chat/ct/chat-13/room-1",
                    "timeLabel": "2026-04-05"
                  },
                  "messages": []
                }
              ]
            }
            """);

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession
        {
            PartnerChatId = null
        };

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        session.LastBaselinePartnerChatId.Should().Be("chat-13");
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_preserve_existing_chats_and_update_totals_when_saving()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(
            baselinePath,
            """
            {
              "exportedAt": "2026-04-05T09:30:00+09:00",
              "chats": [
                {
                  "route": {
                    "href": "/chat/ct/chat-14/room-existing",
                    "name": "existing",
                    "timeLabel": "2026-04-05",
                    "previewText": "old preview"
                  },
                  "messages": [
                    { "kind": "message", "dateLabel": "2026-04-05", "text": "old" }
                  ]
                }
              ]
            }
            """);

        var route = CreateRoute("chat-14", "room-new", "newest", 2000);
        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession
        {
            RouteLoadResult = new CompletedChatsRouteLoadResult([route], ApiAttempted: true, ApiOk: true),
            DetailByHref =
            {
                [route.Href] = CreateDetail(route.AbsoluteUrl, route.Name)
            }
        };

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        var result = browserService.LastCompletedChatCollectionResult!;
        var savedPayload = JsonNode.Parse(await File.ReadAllTextAsync(result.OutputPath))!.AsObject();

        savedPayload["totalRoutes"]!.GetValue<int>().Should().Be(2);
        savedPayload["exportedChats"]!.GetValue<int>().Should().Be(2);
        savedPayload["errors"]!.AsArray().Should().BeEmpty();
        savedPayload["chats"]!.AsArray().Should().HaveCount(2);
        savedPayload["chats"]![0]!["route"]!["name"]!.GetValue<string>().Should().Be("newest");
        savedPayload["chats"]![1]!["route"]!["name"]!.GetValue<string>().Should().Be("existing");
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_mark_result_as_baseline_updated_when_saving_directly_to_baseline()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(baselinePath, """{ "chats": [] }""");

        var route = CreateRoute("chat-15", "room-1", "baseline save", 2000);
        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession
        {
            RouteLoadResult = new CompletedChatsRouteLoadResult([route], ApiAttempted: true, ApiOk: true),
            DetailByHref =
            {
                [route.Href] = CreateDetail(route.AbsoluteUrl, route.Name)
            }
        };

        await collector.CollectCompletedChatsAsync(
            session,
            baselineFileName: baselinePath,
            outputFileName: baselinePath);

        var result = browserService.LastCompletedChatCollectionResult!;
        result.UpdatedBaseline.Should().BeTrue();
        result.OutputPath.Should().Be(baselinePath);
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_report_skipped_count_when_some_details_cannot_be_saved()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(baselinePath, """{ "chats": [] }""");

        var savedRoute = CreateRoute("chat-16", "room-1", "saved", 2000);
        var skippedRoute = CreateRoute("chat-16", "room-2", "skipped", 1000);
        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession
        {
            RouteLoadResult = new CompletedChatsRouteLoadResult([savedRoute, skippedRoute], ApiAttempted: true, ApiOk: true),
            DetailByHref =
            {
                [savedRoute.Href] = CreateDetail(savedRoute.AbsoluteUrl, savedRoute.Name)
            }
        };

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        var result = browserService.LastCompletedChatCollectionResult!;
        result.SkippedCount.Should().Be(1);

        var payload = JsonNode.Parse(result.CollectedDataJson)!.AsObject();
        payload["skippedCount"]!.GetValue<int>().Should().Be(1);
    }

    [TestMethod]
    public async Task CollectCompletedChatsAsync_should_record_partner_chat_id_source_in_debug_payload()
    {
        using var tempDirectory = new TemporaryDirectory();
        var baselinePath = Path.Combine(tempDirectory.Path, "completed-chats.json");

        await File.WriteAllTextAsync(baselinePath, """{ "chats": [] }""");

        var browserService = new NaverTalkBrowserService();
        var collector = new CompletedChatsCollector(browserService, () => new DateTime(2026, 4, 8));
        var session = new FakeCompletedChatsBrowserSession
        {
            PartnerChatId = "chat-17",
            PartnerChatIdSource = "pageText"
        };

        await collector.CollectCompletedChatsAsync(session, baselineFileName: baselinePath);

        var payload = JsonNode.Parse(browserService.LastCompletedChatCollectionResult!.CollectedDataJson)!.AsObject();
        payload["debug"]!["partnerChatId"]!.GetValue<string>().Should().Be("chat-17");
        payload["debug"]!["partnerChatIdSource"]!.GetValue<string>().Should().Be("pageText");
    }

    private static CompletedChatRoute CreateRoute(string chatId, string roomId, string name, long dateMs)
    {
        return new CompletedChatRoute(
            $"/chat/ct/{chatId}/{roomId}",
            name,
            "2026-04-08",
            $"preview {name}",
            $"https://partner.talk.naver.com/chat/ct/{chatId}/{roomId}?state=end&device=pc",
            dateMs);
    }

    private static CompletedChatDetail CreateDetail(string pageUrl, string name)
    {
        return new CompletedChatDetail(
            $"detail {name}",
            pageUrl,
            $"header {name}",
            [new CompletedChatInfoRow("Order", name)],
            [new CompletedChatMessage("message", 1, "2026-04-08", "customer", name, $"hello {name}", "09:00", string.Empty, "balloon_item", "<li />")]);
    }

    private sealed class FakeCompletedChatsBrowserSession : ICompletedChatsBrowserSession
    {
        public string? PartnerChatId { get; init; }

        public string PartnerChatIdSource { get; init; } = string.Empty;

        public CompletedChatsRouteLoadResult RouteLoadResult { get; init; } =
            new([], ApiAttempted: false);

        public Dictionary<string, CompletedChatDetail> DetailByHref { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, TimeSpan> DelayByHref { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Exception> ExceptionByHref { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public int CaptureRouteDiagnosticsCalls { get; private set; }

        public int LoadDetailCalls => _loadDetailCalls;

        public int LastRequestedWorkerCount { get; private set; }

        public int MaxConcurrentLoadDetailCalls => _maxConcurrentLoadDetailCalls;

        public string? LastBaselinePartnerChatId { get; private set; }

        public DateTime? LastFromDate { get; private set; }

        public IReadOnlyCollection<string> LastKnownRoutes { get; private set; } = Array.Empty<string>();

        public Task<PartnerChatIdResolution> ResolvePartnerChatIdAsync(
            string? baselinePartnerChatId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastBaselinePartnerChatId = baselinePartnerChatId;
            var resolvedPartnerChatId = string.IsNullOrWhiteSpace(PartnerChatId)
                ? baselinePartnerChatId
                : PartnerChatId;

            return Task.FromResult(new PartnerChatIdResolution(resolvedPartnerChatId, PartnerChatIdSource));
        }

        public Task<CompletedChatsRouteLoadResult> LoadRoutesAsync(
            DateTime fromDate,
            string? partnerChatId,
            IReadOnlyCollection<string> knownRoutes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastFromDate = fromDate;
            LastKnownRoutes = knownRoutes.ToArray();
            return Task.FromResult(RouteLoadResult);
        }

        public Task CaptureRouteDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureRouteDiagnosticsCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ICompletedChatsDetailSession>> CreateDetailSessionsAsync(
            int desiredWorkerCount,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var workerCount = Math.Max(1, desiredWorkerCount);
            LastRequestedWorkerCount = workerCount;
            IReadOnlyList<ICompletedChatsDetailSession> workers = Enumerable.Range(0, workerCount)
                .Select(_ => (ICompletedChatsDetailSession)new FakeCompletedChatsDetailSession(this))
                .ToArray();

            return Task.FromResult(workers);
        }

        public Task<CompletedChatDetail?> LoadDetailAsync(
            CompletedChatRoute route,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return LoadDetailCoreAsync(route, cancellationToken);
        }

        private int _loadDetailCalls;
        private int _currentConcurrentLoadDetailCalls;
        private int _maxConcurrentLoadDetailCalls;

        private async Task<CompletedChatDetail?> LoadDetailCoreAsync(
            CompletedChatRoute route,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _loadDetailCalls);
            var concurrentCount = Interlocked.Increment(ref _currentConcurrentLoadDetailCalls);
            UpdateMaxConcurrentLoadDetailCalls(concurrentCount);

            try
            {
                if (DelayByHref.TryGetValue(route.Href, out var delay) && delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                if (ExceptionByHref.TryGetValue(route.Href, out var exception))
                {
                    throw exception;
                }

                DetailByHref.TryGetValue(route.Href, out var detail);
                return detail;
            }
            finally
            {
                Interlocked.Decrement(ref _currentConcurrentLoadDetailCalls);
            }
        }

        private void UpdateMaxConcurrentLoadDetailCalls(int concurrentCount)
        {
            while (true)
            {
                var currentMax = _maxConcurrentLoadDetailCalls;
                if (concurrentCount <= currentMax)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _maxConcurrentLoadDetailCalls, concurrentCount, currentMax) == currentMax)
                {
                    return;
                }
            }
        }

        private sealed class FakeCompletedChatsDetailSession(FakeCompletedChatsBrowserSession owner) : ICompletedChatsDetailSession
        {
            public Task<CompletedChatDetail?> LoadDetailAsync(
                CompletedChatRoute route,
                CancellationToken cancellationToken = default)
            {
                return owner.LoadDetailAsync(route, cancellationToken);
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}
