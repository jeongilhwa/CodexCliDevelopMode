using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexDesk.Services;

/// <summary>
/// 완료 대화 수집을 Playwright 백그라운드 브라우저에서 실행하는 서비스입니다.
/// WebView는 계속 사용자 작업용으로 남겨 두고, 수집 작업만 별도 브라우저에서 처리합니다.
/// </summary>
public sealed class PlaywrightCompletedChatsCollectionService
{
    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly CompletedChatsCollector _collector;
    private readonly NaverTalkBrowserService _naverTalkBrowser;

    /// <summary>
    /// Playwright 수집 서비스에 필요한 의존성을 주입받습니다.
    /// </summary>
    public PlaywrightCompletedChatsCollectionService(
        CompletedChatsCollector collector,
        NaverTalkBrowserService naverTalkBrowser)
    {
        _collector = collector;
        _naverTalkBrowser = naverTalkBrowser;
    }

    /// <summary>
    /// Playwright 브라우저를 열어 완료 대화 수집 전체 흐름을 실행합니다.
    /// </summary>
    public async Task CollectAsync(CancellationToken cancellationToken = default)
    {
        await _naverTalkBrowser.EnsureInitializedAsync();

        if (_naverTalkBrowser.IsCollectingCompletedChats)
        {
            return;
        }

        await _naverTalkBrowser.StartCompletedChatsCollectionAsync();
        _naverTalkBrowser.SetCompletedChatsCollectionProgress(
            "Preparing Playwright collection session.",
            0,
            0,
            _naverTalkBrowser.CurrentUrl);

        try
        {
            await using var browserSession = await PlaywrightCompletedChatsSession.CreateAsync(
                _naverTalkBrowser,
                cancellationToken);

            await _collector.CollectCompletedChatsAsync(
                browserSession,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            var status = $"Playwright collection failed before scraping started: {ex.Message}";
            _naverTalkBrowser.FailCompletedChatsCollection(
                status,
                _naverTalkBrowser.CurrentUrl,
                CreateStartupFailureResult(status, ex));
        }
    }

    /// <summary>
    /// Playwright 실행 자체가 실패했을 때도 UI에서 결과를 확인할 수 있도록 기본 실패 결과를 만듭니다.
    /// </summary>
    private static CompletedChatCollectionResult CreateStartupFailureResult(string summary, Exception exception)
    {
        var baselinePath = CompletedChatsSupport.ResolveCollectionFilePath("completed-chats.json");
        var outputPath = CompletedChatsSupport.BuildPendingCollectionFilePath(baselinePath);
        var debugInfo = new JsonObject
        {
            ["executionMode"] = "playwright",
            ["startupException"] = exception.ToString()
        };

        return CompletedChatsSupport.BuildCollectionResult(
            succeeded: false,
            summary,
            outputPath,
            baselinePath,
            DateTime.Today,
            discoveredRouteCount: 0,
            newRouteCount: 0,
            savedCount: 0,
            sampleItems: Array.Empty<string>(),
            jsonOptions: ResultJsonOptions,
            collectedChatNodes: new JsonArray(),
            debugInfo: debugInfo);
    }
}
