using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CodexDesk.Services;

/// <summary>
/// 완료 대화 수집 과정에서 WebView가 제공해야 하는 최소 동작만 정의한 인터페이스입니다.
/// 수집기는 이 계약만 통해 화면 이동, 목록 수집, 상세 스크래핑을 요청합니다.
/// </summary>
internal interface ICompletedChatsBrowserSession
{
    /// <summary>
    /// 기준 파일, 현재 URL, 현재 페이지 텍스트를 바탕으로 partner chat id를 결정합니다.
    /// </summary>
    Task<PartnerChatIdResolution> ResolvePartnerChatIdAsync(
        string? baselinePartnerChatId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 완료 대화 목록에서 수집 후보 route를 가져옵니다.
    /// API를 먼저 시도하고, 필요하면 DOM 스크래핑으로 대체합니다.
    /// </summary>
    Task<CompletedChatsRouteLoadResult> LoadRoutesAsync(
        DateTime fromDate,
        string? partnerChatId,
        IReadOnlyCollection<string> knownRoutes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// route 후보를 찾지 못했을 때 현재 페이지 구조를 진단용 파일로 남깁니다.
    /// </summary>
    Task CaptureRouteDiagnosticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 상세 대화 스크래핑을 병렬로 처리할 worker 세션들을 만듭니다.
    /// Playwright 구현은 같은 browser/context 아래 여러 page를 만들고, WebView 구현은 단일 worker만 반환합니다.
    /// </summary>
    Task<IReadOnlyList<ICompletedChatsDetailSession>> CreateDetailSessionsAsync(
        int desiredWorkerCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 단일 route에 진입해 상세 대화 내용을 읽어옵니다.
    /// </summary>
    Task<CompletedChatDetail?> LoadDetailAsync(
        CompletedChatRoute route,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 완료 대화 상세 페이지 한 건을 읽는 전용 worker 계약입니다.
/// route 목록 수집과 분리해서 여러 page가 동시에 detail만 처리할 수 있게 합니다.
/// </summary>
internal interface ICompletedChatsDetailSession : IAsyncDisposable
{
    /// <summary>
    /// 단일 route의 상세 화면을 열고, 저장 가능한 형태의 대화/메타데이터를 읽어 옵니다.
    /// </summary>
    Task<CompletedChatDetail?> LoadDetailAsync(
        CompletedChatRoute route,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// completed-chats 기준 파일을 읽고 신규 완료 대화를 찾아 결과 파일로 저장하는 오케스트레이터입니다.
/// WebView 세부 구현은 session에 맡기고, 이 클래스는 수집 흐름과 결과 조립에만 집중합니다.
/// </summary>
public sealed class CompletedChatsCollector
{
    private const int MaxParallelDetailWorkers = 3;

    private static readonly JsonSerializerOptions CollectionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly NaverTalkBrowserService _naverTalkBrowser;
    private readonly Func<DateTime> _todayProvider;

    public CompletedChatsCollector(NaverTalkBrowserService naverTalkBrowser)
        : this(naverTalkBrowser, static () => DateTime.Today)
    {
    }

    /// <summary>
    /// 테스트에서 날짜 기준을 고정할 수 있도록 오늘 날짜 공급자를 분리한 내부 생성자입니다.
    /// </summary>
    internal CompletedChatsCollector(NaverTalkBrowserService naverTalkBrowser, Func<DateTime> todayProvider)
    {
        _naverTalkBrowser = naverTalkBrowser;
        _todayProvider = todayProvider;
    }

    /// <summary>
    /// 완료 대화 수집 전체 흐름을 실행합니다.
    /// 기준 파일 로드, 수집 시작일 계산, route 후보 수집, 상세 대화 저장, 상태 보고를 순서대로 처리합니다.
    /// </summary>
    internal async Task CollectCompletedChatsAsync(
        ICompletedChatsBrowserSession browserSession,
        string? baselineFileName = null,
        string? outputFileName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(browserSession);

        var baselinePath = ResolveBaselineFilePath(baselineFileName);
        var exportPath = ResolveOutputFilePath(baselinePath, outputFileName);
        var today = _todayProvider().Date;
        var fromDateForResult = today;
        var discoveredRouteCount = 0;
        var newRouteCount = 0;
        var insertedCount = 0;
        var sampleItems = new List<string>();
        var collectedChatNodes = new JsonArray();
        var debugInfo = new JsonObject();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existing = await CompletedChatsSupport.LoadFileAsync(baselinePath);
            var collectionWindow = CompletedChatsSupport.DetermineCollectionStartDate(existing, today);
            var fromDate = collectionWindow.FromDate;
            fromDateForResult = fromDate;

            var partnerChat = await browserSession.ResolvePartnerChatIdAsync(existing.PartnerChatId, cancellationToken);

            debugInfo["baselineExportedAt"] = existing.ExportedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;
            debugInfo["baselineLatestKnownDate"] = existing.LatestKnownDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
            debugInfo["fromDateSource"] = collectionWindow.FromDateSource;
            debugInfo["fromDate"] = fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            debugInfo["isInitialCollection"] = collectionWindow.IsInitialCollection;
            debugInfo["partnerChatId"] = partnerChat.PartnerChatId ?? string.Empty;
            debugInfo["partnerChatIdSource"] = partnerChat.Source;
            debugInfo["knownRouteCount"] = existing.KnownRouteHrefs.Count;

            var startStatus = collectionWindow.IsInitialCollection
                ? $"저장된 대화내역이 없습니다. 데이터를 처음부터 수집합니다. 최근 6개월 범위({fromDate:yyyy-MM-dd} ~ {today:yyyy-MM-dd})를 수집합니다. output={Path.GetFileName(exportPath)}"
                : $"Collecting completed chats from {fromDate:yyyy-MM-dd} to {today:yyyy-MM-dd}. output={Path.GetFileName(exportPath)}";
            _naverTalkBrowser.SetCompletedChatsCollectionProgress(startStatus, 0, 0, _naverTalkBrowser.CurrentUrl);

            if (!string.IsNullOrWhiteSpace(partnerChat.PartnerChatId))
            {
                _naverTalkBrowser.SetCompletedChatsCollectionProgress(
                    $"Using partner chat id {partnerChat.PartnerChatId} ({partnerChat.Source}) for completed route collection.",
                    0,
                    0,
                    _naverTalkBrowser.CurrentUrl);
            }

            var routeLoadResult = await browserSession.LoadRoutesAsync(
                fromDate,
                partnerChat.PartnerChatId,
                existing.KnownRouteHrefs,
                cancellationToken);

            debugInfo["apiAttempted"] = routeLoadResult.ApiAttempted;
            debugInfo["apiOk"] = routeLoadResult.ApiOk;
            debugInfo["apiError"] = routeLoadResult.ApiError ?? string.Empty;
            debugInfo["apiPagesFetched"] = routeLoadResult.PagesFetched;
            debugInfo["apiLastCursor"] = routeLoadResult.LastCursor ?? string.Empty;
            debugInfo["apiHasNextPageAtEnd"] = routeLoadResult.HasNextPageAtEnd;
            debugInfo["apiReturnedRouteCount"] = routeLoadResult.ApiOk == true
                ? routeLoadResult.Routes.Count
                : 0;

            if (!string.IsNullOrWhiteSpace(routeLoadResult.ApiError))
            {
                _naverTalkBrowser.SetCompletedChatsCollectionProgress(
                    $"Completed route API fallback: {routeLoadResult.ApiError}",
                    0,
                    0,
                    _naverTalkBrowser.CurrentUrl);
            }

            var routeCandidates = routeLoadResult.Routes
                .Where(static route => !string.IsNullOrWhiteSpace(route.Href))
                .OrderByDescending(route => route.DateMs ?? 0)
                .ToList();

            if (routeCandidates.Count == 0)
            {
                await browserSession.CaptureRouteDiagnosticsAsync(cancellationToken);

                var status = "No completed chat routes were discovered from the current page. Route selectors likely need adjustment.";
                _naverTalkBrowser.FailCompletedChatsCollection(
                    status,
                    _naverTalkBrowser.CurrentUrl,
                    CreateCollectionResult(
                        succeeded: false,
                        summary: status,
                        outputPath: exportPath,
                        baselinePath,
                        fromDate,
                        discoveredRouteCount,
                        newRouteCount,
                        insertedCount,
                        sampleItems,
                        collectedChatNodes,
                        debugInfo));
                return;
            }

            discoveredRouteCount = routeCandidates.Count;
            debugInfo["routeCandidateCount"] = discoveredRouteCount;

            var newRoutes = routeCandidates
                .Where(route => !existing.KnownRouteHrefs.Contains(route.Href))
                .ToList();

            newRouteCount = newRoutes.Count;
            debugInfo["newRouteCount"] = newRouteCount;
            debugInfo["newRouteSample"] = new JsonArray(newRoutes
                .Take(10)
                .Select(route => (JsonNode)new JsonObject
                {
                    ["href"] = route.Href,
                    ["name"] = route.Name,
                    ["timeLabel"] = route.TimeLabel
                })
                .ToArray());

            _naverTalkBrowser.SetCompletedChatsCollectionProgress(
                $"Found {newRoutes.Count} new chats to collect with up to {Math.Min(MaxParallelDetailWorkers, newRoutes.Count)} parallel workers.",
                0,
                newRoutes.Count,
                _naverTalkBrowser.CurrentUrl);

            if (newRoutes.Count == 0)
            {
                var status = $"No new completed chats were found. Existing baseline: {fromDate:yyyy-MM-dd}.";
                _naverTalkBrowser.CompleteCompletedChatsCollection(
                    status,
                    _naverTalkBrowser.CurrentUrl,
                    CreateCollectionResult(
                        succeeded: true,
                        summary: status,
                        outputPath: exportPath,
                        baselinePath,
                        fromDate,
                        discoveredRouteCount,
                        newRouteCount,
                        insertedCount,
                        sampleItems,
                        collectedChatNodes,
                        debugInfo));
                return;
            }

            var detailResults = await LoadDetailsInParallelAsync(browserSession, newRoutes, debugInfo, cancellationToken);

            foreach (var detailResult in detailResults)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (detailResult.ErrorMessage is { Length: > 0 })
                {
                    continue;
                }

                if (!HasCollectibleDetail(detailResult.Detail))
                {
                    continue;
                }

                var chatNode = CreateCollectedChatNode(detailResult.Route, detailResult.Detail!);
                existing.Chats.Insert(insertedCount, chatNode);
                collectedChatNodes.Add(chatNode.DeepClone());
                existing.KnownRouteHrefs.Add(detailResult.Route.Href);
                insertedCount++;

                AddSampleItemIfNeeded(sampleItems, detailResult.Route, detailResult.Detail!);
            }

            debugInfo["insertedCount"] = insertedCount;

            if (insertedCount == 0)
            {
                var status = "Completed chat collection did not gather any complete chats. Detail scraping returned no usable content for every candidate.";
                _naverTalkBrowser.FailCompletedChatsCollection(
                    status,
                    _naverTalkBrowser.CurrentUrl,
                    CreateCollectionResult(
                        succeeded: false,
                        summary: status,
                        outputPath: exportPath,
                        baselinePath,
                        fromDate,
                        discoveredRouteCount,
                        newRouteCount,
                        insertedCount,
                        sampleItems,
                        collectedChatNodes,
                        debugInfo));
                return;
            }

            existing.Root["exportedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            existing.Root["totalRoutes"] = existing.Chats.Count;
            existing.Root["exportedChats"] = existing.Chats.Count;
            existing.Root["errors"] ??= new JsonArray();

            await File.WriteAllTextAsync(exportPath, existing.Root.ToJsonString(CollectionJsonOptions), cancellationToken);
            await TryConvertToCompletedChatThreadsJsonlAsync(exportPath, debugInfo, cancellationToken);

            var completedStatus = $"Collected {insertedCount} new chats. Review the result and press Save to apply it.";
            _naverTalkBrowser.CompleteCompletedChatsCollection(
                completedStatus,
                _naverTalkBrowser.CurrentUrl,
                CreateCollectionResult(
                    succeeded: true,
                    summary: completedStatus,
                    outputPath: exportPath,
                    baselinePath,
                    fromDate,
                    discoveredRouteCount,
                    newRouteCount,
                    insertedCount,
                    sampleItems,
                    collectedChatNodes,
                    debugInfo));
        }
        catch (Exception ex)
        {
            var status = $"Chat collection failed: {ex.Message}";
            debugInfo["exception"] = ex.ToString();
            _naverTalkBrowser.FailCompletedChatsCollection(
                status,
                _naverTalkBrowser.CurrentUrl,
                CreateCollectionResult(
                    succeeded: false,
                    summary: status,
                    outputPath: exportPath,
                    baselinePath,
                    fromDateForResult,
                    discoveredRouteCount,
                    newRouteCount,
                    insertedCount,
                    sampleItems,
                    collectedChatNodes,
                    debugInfo));
        }
    }

    /// <summary>
    /// 사용자가 파일명을 생략했을 때 기본 기준 파일 경로를 확정합니다.
    /// </summary>
    private static string ResolveBaselineFilePath(string? baselineFileName)
    {
        var resolvedName = string.IsNullOrWhiteSpace(baselineFileName)
            ? "completed-chats.json"
            : baselineFileName.Trim();

        return CompletedChatsSupport.ResolveCollectionFilePath(resolvedName);
    }

    /// <summary>
    /// 결과를 임시 pending 파일에 둘지, 사용자가 지정한 경로에 저장할지 결정합니다.
    /// </summary>
    private static string ResolveOutputFilePath(string baselinePath, string? outputFileName)
    {
        if (string.IsNullOrWhiteSpace(outputFileName))
        {
            return CompletedChatsSupport.BuildPendingCollectionFilePath(baselinePath);
        }

        return CompletedChatsSupport.ResolveCollectionFilePath(outputFileName.Trim());
    }

    /// <summary>
    /// UI 결과 모달과 저장 파일에서 공통으로 사용하는 수집 결과 객체를 만듭니다.
    /// </summary>
    private static CompletedChatCollectionResult CreateCollectionResult(
        bool succeeded,
        string summary,
        string outputPath,
        string baselinePath,
        DateTime fromDate,
        int discoveredRouteCount,
        int newRouteCount,
        int savedCount,
        IReadOnlyList<string> sampleItems,
        JsonArray collectedChatNodes,
        JsonObject debugInfo)
    {
        return CompletedChatsSupport.BuildCollectionResult(
            succeeded,
            summary,
            outputPath,
            baselinePath,
            fromDate,
            discoveredRouteCount,
            newRouteCount,
            savedCount,
            sampleItems,
            CollectionJsonOptions,
            collectedChatNodes,
            debugInfo);
    }

    /// <summary>
    /// 상세 화면에서 실제로 저장할 만한 본문이나 메타데이터가 있는지 판별합니다.
    /// 빈 화면, 로딩 실패 화면을 걸러내기 위한 최소 검증입니다.
    /// </summary>
    /// <summary>
    /// 신규 route들을 worker 세션에 분배해서 상세 스크래핑을 병렬로 수행합니다.
    /// 결과 저장 순서는 route 정렬 순서를 유지해야 하므로, worker 완료 순서와 별개로 index 기준으로 다시 정렬합니다.
    /// </summary>
    private async Task<IReadOnlyList<CompletedChatsDetailLoadResult>> LoadDetailsInParallelAsync(
        ICompletedChatsBrowserSession browserSession,
        IReadOnlyList<CompletedChatRoute> newRoutes,
        JsonObject debugInfo,
        CancellationToken cancellationToken)
    {
        var desiredWorkerCount = Math.Min(MaxParallelDetailWorkers, Math.Max(1, newRoutes.Count));
        var detailSessions = await browserSession.CreateDetailSessionsAsync(desiredWorkerCount, cancellationToken);
        if (detailSessions.Count == 0)
        {
            throw new InvalidOperationException("No completed chat detail sessions were created.");
        }

        debugInfo["detailWorkerCount"] = detailSessions.Count;

        var workChannel = Channel.CreateUnbounded<IndexedCompletedChatRoute>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = false
        });

        for (var index = 0; index < newRoutes.Count; index++)
        {
            await workChannel.Writer.WriteAsync(new IndexedCompletedChatRoute(index, newRoutes[index]), cancellationToken);
        }

        workChannel.Writer.Complete();

        var results = new CompletedChatsDetailLoadResult[newRoutes.Count];
        var completedRouteCount = 0;

        try
        {
            var workerTasks = detailSessions
                .Select((session, workerIndex) => ProcessDetailWorkerAsync(
                    session,
                    workerIndex + 1,
                    newRoutes.Count,
                    workChannel.Reader,
                    results,
                    () => Interlocked.Increment(ref completedRouteCount),
                    cancellationToken))
                .ToArray();

            await Task.WhenAll(workerTasks);
        }
        finally
        {
            await DisposeDetailSessionsAsync(detailSessions);
        }

        var orderedResults = results
            .Where(static result => result is not null)
            .OrderBy(static result => result.RouteIndex)
            .ToList();

        debugInfo["detailCompletedCount"] = orderedResults.Count;
        debugInfo["detailErrorCount"] = orderedResults.Count(static result => !string.IsNullOrWhiteSpace(result.ErrorMessage));
        debugInfo["detailSkippedCount"] = orderedResults.Count(static result => string.IsNullOrWhiteSpace(result.ErrorMessage) && !HasCollectibleDetail(result.Detail));
        debugInfo["detailErrors"] = new JsonArray(orderedResults
            .Where(static result => !string.IsNullOrWhiteSpace(result.ErrorMessage))
            .Select(result => (JsonNode)new JsonObject
            {
                ["href"] = result.Route.Href,
                ["name"] = result.Route.Name,
                ["error"] = result.ErrorMessage
            })
            .ToArray());

        return orderedResults;
    }

    /// <summary>
    /// worker 하나가 queue에서 route를 꺼내 상세 페이지를 읽고 결과 배열에 기록합니다.
    /// UI 진행률은 route 완료 기준으로 집계해서 병렬 worker 간 메시지가 지나치게 엇갈리지 않게 유지합니다.
    /// </summary>
    private async Task ProcessDetailWorkerAsync(
        ICompletedChatsDetailSession detailSession,
        int workerNumber,
        int routeCount,
        ChannelReader<IndexedCompletedChatRoute> routeReader,
        CompletedChatsDetailLoadResult[] results,
        Func<int> incrementCompletedRouteCount,
        CancellationToken cancellationToken)
    {
        await foreach (var indexedRoute in routeReader.ReadAllAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            CompletedChatsDetailLoadResult result;
            try
            {
                var detail = await detailSession.LoadDetailAsync(indexedRoute.Route, cancellationToken);
                result = new CompletedChatsDetailLoadResult(
                    indexedRoute.RouteIndex,
                    indexedRoute.Route,
                    detail,
                    null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new CompletedChatsDetailLoadResult(
                    indexedRoute.RouteIndex,
                    indexedRoute.Route,
                    null,
                    ex.Message);
            }

            results[indexedRoute.RouteIndex] = result;

            var completedRouteCount = incrementCompletedRouteCount();
            var progressMessage = !string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? $"Worker {workerNumber} failed {completedRouteCount}/{routeCount}: {indexedRoute.Route.Name} ({result.ErrorMessage})"
                : HasCollectibleDetail(result.Detail)
                    ? $"Worker {workerNumber} collected {completedRouteCount}/{routeCount}: {indexedRoute.Route.Name}"
                    : $"Worker {workerNumber} skipped {completedRouteCount}/{routeCount}: {indexedRoute.Route.Name}";

            _naverTalkBrowser.SetCompletedChatsCollectionProgress(
                progressMessage,
                completedRouteCount,
                routeCount,
                indexedRoute.Route.AbsoluteUrl);
        }
    }

    private static async Task DisposeDetailSessionsAsync(IReadOnlyList<ICompletedChatsDetailSession> detailSessions)
    {
        foreach (var detailSession in detailSessions)
        {
            await detailSession.DisposeAsync();
        }
    }

    private static bool HasCollectibleDetail(CompletedChatDetail? detail)
    {
        return detail is not null &&
               !string.IsNullOrWhiteSpace(detail.PageUrl) &&
               (
                   (detail.Messages?.Count ?? 0) > 0 ||
                   (detail.InfoRows?.Count ?? 0) > 0 ||
                   !string.IsNullOrWhiteSpace(detail.HeaderText) ||
                   !string.IsNullOrWhiteSpace(detail.PageTitle));
    }

    /// <summary>
    /// 방금 저장한 completed-chats 계열 JSON을 같은 폴더의 completed-chat-threads.jsonl로 투영합니다.
    /// 변환 실패는 수집 전체 실패로 보지 않고 debug payload에만 남겨 후속 점검이 가능하게 합니다.
    /// </summary>
    private static async Task TryConvertToCompletedChatThreadsJsonlAsync(
        string completedChatsPath,
        JsonObject debugInfo,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetDirectoryName(completedChatsPath);
        var threadsPath = Path.Combine(
            string.IsNullOrWhiteSpace(outputDirectory) ? AppContext.BaseDirectory : outputDirectory,
            "completed-chat-threads.jsonl");

        try
        {
            await CompletedChatThreadsJsonlConverter.ConvertFromCompletedChatsJsonAsync(
                completedChatsPath,
                threadsPath,
                cancellationToken);

            debugInfo["threadProjectionPath"] = threadsPath;
            debugInfo["threadProjectionSucceeded"] = true;
        }
        catch (Exception ex)
        {
            debugInfo["threadProjectionPath"] = threadsPath;
            debugInfo["threadProjectionSucceeded"] = false;
            debugInfo["threadProjectionError"] = ex.Message;
        }
    }

    /// <summary>
    /// 스크래핑한 route와 메시지를 completed-chats.json 포맷에 맞는 JSON 노드로 변환합니다.
    /// </summary>
    private static JsonNode CreateCollectedChatNode(CompletedChatRoute route, CompletedChatDetail detail)
    {
        var detailMessages = detail.Messages ?? [];
        var infoRows = detail.InfoRows ?? [];
        var contentMessages = detailMessages
            .Where(message => string.Equals(message.Kind, "message", StringComparison.Ordinal))
            .ToList();
        var maxMsgId = contentMessages.Count == 0 ? 0 : contentMessages.Max(message => message.MsgId ?? 0);
        var minMsgId = contentMessages.Count == 0 ? 0 : contentMessages.Min(message => message.MsgId ?? 0);

        return JsonSerializer.SerializeToNode(new
        {
            route = new
            {
                href = route.Href,
                name = route.Name ?? string.Empty,
                timeLabel = route.TimeLabel ?? string.Empty,
                previewText = route.PreviewText ?? string.Empty
            },
            loadedState = new
            {
                count = contentMessages.Count,
                maxMsgId,
                minMsgId
            },
            pageTitle = detail.PageTitle ?? string.Empty,
            pageUrl = detail.PageUrl ?? string.Empty,
            headerText = detail.HeaderText ?? string.Empty,
            infoRows = infoRows.Select(row => new { label = row.Label, value = row.Value }),
            previousHistory = Array.Empty<object>(),
            messages = detailMessages.Select(message => new
            {
                kind = message.Kind,
                msgId = message.MsgId,
                dateLabel = message.DateLabel,
                direction = message.Direction,
                sender = message.Sender,
                text = message.Text,
                timeLabel = message.TimeLabel,
                readLabel = message.ReadLabel,
                className = message.ClassName,
                html = message.Html
            })
        }, CollectionJsonOptions) ?? new JsonObject();
    }

    /// <summary>
    /// 결과 모달에서 빠르게 확인할 수 있도록 대표 샘플 문자열을 최대 8개까지 누적합니다.
    /// </summary>
    private static void AddSampleItemIfNeeded(
        ICollection<string> sampleItems,
        CompletedChatRoute route,
        CompletedChatDetail detail)
    {
        if (sampleItems.Count >= 8)
        {
            return;
        }

        var contentCount = detail.Messages?.Count(message => string.Equals(message.Kind, "message", StringComparison.Ordinal)) ?? 0;
        sampleItems.Add($"{route.Name} | {route.PreviewText} | {contentCount} messages");
    }
}

internal sealed record IndexedCompletedChatRoute(int RouteIndex, CompletedChatRoute Route);

internal sealed record CompletedChatsDetailLoadResult(
    int RouteIndex,
    CompletedChatRoute Route,
    CompletedChatDetail? Detail,
    string? ErrorMessage);

internal sealed record PartnerChatIdResolution(string? PartnerChatId, string Source);

internal sealed record CompletedChatsRouteLoadResult(
    IReadOnlyList<CompletedChatRoute> Routes,
    bool ApiAttempted,
    bool? ApiOk = null,
    string? ApiError = null,
    int? PagesFetched = null,
    string? LastCursor = null,
    bool? HasNextPageAtEnd = null);

internal sealed record CompletedChatRoute(
    string Href,
    string Name,
    string TimeLabel,
    string PreviewText,
    string AbsoluteUrl,
    long? DateMs = null);

internal sealed record CompletedChatInfoRow(string Label, string Value);

internal sealed record CompletedChatMessage(
    string Kind,
    long? MsgId,
    string DateLabel,
    string Direction,
    string Sender,
    string Text,
    string TimeLabel,
    string ReadLabel,
    string ClassName,
    string Html);

internal sealed record CompletedChatDetail(
    string PageTitle,
    string PageUrl,
    string HeaderText,
    IReadOnlyList<CompletedChatInfoRow>? InfoRows,
    IReadOnlyList<CompletedChatMessage>? Messages);
