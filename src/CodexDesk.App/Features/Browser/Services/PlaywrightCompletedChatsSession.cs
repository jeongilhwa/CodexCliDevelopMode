using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexDesk.Models;
using Microsoft.Playwright;
using PlaywrightBrowser = Microsoft.Playwright.IBrowser;

namespace CodexDesk.Services;

/// <summary>
/// 완료 대화 수집에 필요한 네이버 톡 브라우저 동작을 Playwright로 구현한 세션입니다.
/// collector는 이 세션을 통해 route 목록과 상세 대화를 읽고, WebView는 사용하지 않습니다.
/// </summary>
internal sealed partial class PlaywrightCompletedChatsSession : ICompletedChatsBrowserSession, IAsyncDisposable
{
    private static readonly JsonSerializerOptions CollectionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly NaverTalkBrowserService _naverTalkBrowser;
    private readonly IPlaywright _playwright;
    private readonly PlaywrightBrowser _browser;
    private readonly IBrowserContext _browserContext;
    private readonly IPage _page;

    private PlaywrightCompletedChatsSession(
        NaverTalkBrowserService naverTalkBrowser,
        IPlaywright playwright,
        PlaywrightBrowser browser,
        IBrowserContext browserContext,
        IPage page)
    {
        _naverTalkBrowser = naverTalkBrowser;
        _playwright = playwright;
        _browser = browser;
        _browserContext = browserContext;
        _page = page;
    }

    /// <summary>
    /// Playwright 브라우저와 컨텍스트를 만들고, 저장된 네이버 쿠키 세션을 주입한 뒤 수집 준비를 마칩니다.
    /// </summary>
    public static async Task<PlaywrightCompletedChatsSession> CreateAsync(
        NaverTalkBrowserService naverTalkBrowser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(naverTalkBrowser);

        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(CreateBrowserLaunchOptions());

        var browserContext = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            Locale = "ko-KR"
        });

        await browserContext.AddInitScriptAsync(
            """
            Object.defineProperty(navigator, 'webdriver', {
                get: () => undefined
            });
            """);

        var savedCookiesJson = await naverTalkBrowser.TryGetSavedCookieSessionAsync();
        if (!string.IsNullOrWhiteSpace(savedCookiesJson))
        {
            var persistedCookies = JsonSerializer.Deserialize<List<NaverTalkPersistedCookie>>(savedCookiesJson);
            if (persistedCookies is { Count: > 0 })
            {
                await browserContext.AddCookiesAsync(
                    persistedCookies.Select(ToPlaywrightCookie).ToArray());
            }
        }

        var page = await browserContext.NewPageAsync();
        ConfigurePage(page);

        cancellationToken.ThrowIfCancellationRequested();
        return new PlaywrightCompletedChatsSession(
            naverTalkBrowser,
            playwright,
            browser,
            browserContext,
            page);
    }

    /// <summary>
    /// baseline, 저장된 URL, 현재 URL, 페이지 본문 순으로 partner chat id를 결정합니다.
    /// </summary>
    public async Task<PartnerChatIdResolution> ResolvePartnerChatIdAsync(
        string? baselinePartnerChatId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(baselinePartnerChatId))
        {
            return new PartnerChatIdResolution(baselinePartnerChatId.Trim(), "baseline");
        }

        var currentUrlPartnerChatId = CompletedChatsSupport.TryExtractPartnerChatId(_naverTalkBrowser.CurrentUrl);
        if (!string.IsNullOrWhiteSpace(currentUrlPartnerChatId))
        {
            return new PartnerChatIdResolution(currentUrlPartnerChatId, "currentUrl");
        }

        var targetUrl = _naverTalkBrowser.Settings.TargetUrl;
        var targetUrlPartnerChatId = CompletedChatsSupport.TryExtractPartnerChatId(targetUrl);
        if (!string.IsNullOrWhiteSpace(targetUrlPartnerChatId))
        {
            return new PartnerChatIdResolution(targetUrlPartnerChatId, "settingsTargetUrl");
        }

        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return new PartnerChatIdResolution(null, string.Empty);
        }

        var resolution = await ResolvePartnerChatIdFromPageAsync(targetUrl, "page", cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolution.PartnerChatId))
        {
            return resolution;
        }

        var completedListUrl = BuildCompletedChatsListUrl(targetUrl, null);
        if (!string.Equals(targetUrl, completedListUrl, StringComparison.OrdinalIgnoreCase))
        {
            resolution = await ResolvePartnerChatIdFromPageAsync(completedListUrl, "completedListPage", cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolution.PartnerChatId))
            {
                return resolution;
            }
        }

        return new PartnerChatIdResolution(null, string.Empty);
    }

    /// <summary>
    /// 완료 대화 route 목록을 가져옵니다. API를 먼저 시도하고, 실패하면 DOM 스크래핑으로 fallback 합니다.
    /// </summary>
    /// <summary>
    /// 지정한 페이지를 열고 현재 DOM에서 partner chat id를 다시 계산합니다.
    /// WebView 수집기와 같은 우선순위로 URL, path, anchor, bodyText를 함께 확인합니다.
    /// </summary>
    private async Task<PartnerChatIdResolution> ResolvePartnerChatIdFromPageAsync(
        string url,
        string sourcePrefix,
        CancellationToken cancellationToken)
    {
        await NavigateAsync(url, cancellationToken);
        await WaitForPartnerTalkReadyAsync(url, cancellationToken);

        var state = await EvaluateJsonAsync<PartnerChatIdResolutionState>(BuildPartnerChatIdResolutionScript());
        return TryBuildPartnerChatIdResolution(state, sourcePrefix);
    }

    private static PartnerChatIdResolution TryBuildPartnerChatIdResolution(
        PartnerChatIdResolutionState? state,
        string sourcePrefix)
    {
        if (state is null)
        {
            return new PartnerChatIdResolution(null, string.Empty);
        }

        var resolvedFromUrl = CompletedChatsSupport.TryExtractPartnerChatId(state.Url);
        if (!string.IsNullOrWhiteSpace(resolvedFromUrl))
        {
            return new PartnerChatIdResolution(resolvedFromUrl, $"{sourcePrefix}Url");
        }

        var resolvedFromPath = CompletedChatsSupport.TryNormalizePartnerChatId(state.PathPartnerChatId);
        if (!string.IsNullOrWhiteSpace(resolvedFromPath))
        {
            return new PartnerChatIdResolution(resolvedFromPath, $"{sourcePrefix}Path");
        }

        var resolvedFromAnchor = CompletedChatsSupport.TryExtractPartnerChatId(state.AnchorHref);
        if (!string.IsNullOrWhiteSpace(resolvedFromAnchor))
        {
            return new PartnerChatIdResolution(resolvedFromAnchor, $"{sourcePrefix}Anchor");
        }

        var resolvedFromPageText = CompletedChatsSupport.TryExtractPartnerChatIdFromPageText(state.BodyText);
        return new PartnerChatIdResolution(
            resolvedFromPageText,
            string.IsNullOrWhiteSpace(resolvedFromPageText) ? string.Empty : $"{sourcePrefix}Text");
    }

    public async Task<CompletedChatsRouteLoadResult> LoadRoutesAsync(
        DateTime fromDate,
        string? partnerChatId,
        IReadOnlyCollection<string> knownRoutes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var completedUrl = BuildCompletedChatsListUrl(_naverTalkBrowser.Settings.TargetUrl, partnerChatId);
        await NavigateAsync(completedUrl, cancellationToken);
        await WaitForPartnerTalkReadyAsync(completedUrl, cancellationToken);

        ApiCompletedRouteCollectionResult? apiRoutes = null;
        if (!string.IsNullOrWhiteSpace(partnerChatId))
        {
            apiRoutes = await LoadCompletedChatRoutesFromApiAsync(fromDate, partnerChatId, cancellationToken);
        }

        IReadOnlyList<CompletedChatRoute> routes = apiRoutes?.Ok == true
            ? apiRoutes.Routes
                .Where(static route => !string.IsNullOrWhiteSpace(route.Href))
                .OrderByDescending(static route => route.DateMs ?? 0)
                .Select(ToRoute)
                .ToList()
            : (await LoadCompletedChatRoutesFromPageAsync(new HashSet<string>(knownRoutes, StringComparer.OrdinalIgnoreCase), cancellationToken))
                .Select(ToRoute)
                .ToList();

        return new CompletedChatsRouteLoadResult(
            routes,
            ApiAttempted: !string.IsNullOrWhiteSpace(partnerChatId),
            ApiOk: apiRoutes?.Ok,
            ApiError: apiRoutes?.ErrorMessage,
            PagesFetched: apiRoutes?.PagesFetched,
            LastCursor: apiRoutes?.LastCursor,
            HasNextPageAtEnd: apiRoutes?.HasNextPageAtEnd);
    }

    /// <summary>
    /// route 탐색이 실패했을 때 현재 페이지 구조를 진단 파일로 남깁니다.
    /// </summary>
    public async Task CaptureRouteDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var debugPath = CompletedChatsSupport.ResolveProjectDataPath("completed-route-debug.json");
        var rawPath = CompletedChatsSupport.ResolveProjectDataPath("completed-route-debug-raw.txt");

        try
        {
            var raw = await _page.EvaluateAsync<string>(BuildRouteDiagnosticsScript());
            await File.WriteAllTextAsync(rawPath, raw ?? string.Empty, cancellationToken);

            var parsed = ParseScriptJson<JsonNode>(raw);
            if (parsed is null)
            {
                await File.WriteAllTextAsync(debugPath, "{\"error\":\"ParseScriptJson returned null.\"}", cancellationToken);
                return;
            }

            await File.WriteAllTextAsync(debugPath, parsed.ToJsonString(CollectionJsonOptions), cancellationToken);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(
                debugPath,
                JsonSerializer.Serialize(new
                {
                    error = ex.Message,
                    url = _page.Url
                }, CollectionJsonOptions),
                cancellationToken);
        }
    }

    /// <summary>
    /// 단일 완료 대화 상세 페이지를 열고, 메시지와 정보 행을 collector DTO로 반환합니다.
    /// </summary>
    /// <summary>
    /// 같은 browser/context 아래 detail 전용 page worker를 여러 개 생성합니다.
    /// route 목록 수집은 coordinator page 하나로 유지하고, 상세 스크래핑만 병렬화합니다.
    /// </summary>
    public async Task<IReadOnlyList<ICompletedChatsDetailSession>> CreateDetailSessionsAsync(
        int desiredWorkerCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var workerCount = Math.Max(1, desiredWorkerCount);
        var workers = new List<ICompletedChatsDetailSession>(workerCount);

        try
        {
            for (var index = 0; index < workerCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = await _browserContext.NewPageAsync();
                ConfigurePage(page);
                workers.Add(new PlaywrightCompletedChatsDetailSession(this, page));
            }

            return workers;
        }
        catch
        {
            foreach (var worker in workers)
            {
                await worker.DisposeAsync();
            }

            throw;
        }
    }

    public async Task<CompletedChatDetail?> LoadDetailAsync(
        CompletedChatRoute route,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await NavigateAsync(route.AbsoluteUrl, cancellationToken);
        await WaitForCompletedChatDetailPageReadyAsync(route.AbsoluteUrl, cancellationToken);
        await LoadEntireCompletedChatHistoryAsync(cancellationToken);

        var detail = await EvaluateJsonAsync<ScrapedChatDetail>(BuildCompletedChatDetailScript());
        return detail is null
            ? null
            : new CompletedChatDetail(
                detail.PageTitle ?? string.Empty,
                detail.PageUrl ?? string.Empty,
                detail.HeaderText ?? string.Empty,
                detail.InfoRows?.Select(static row => new CompletedChatInfoRow(row.Label ?? string.Empty, row.Value ?? string.Empty)).ToList(),
                detail.Messages?.Select(static message => new CompletedChatMessage(
                    message.Kind ?? string.Empty,
                    message.MsgId,
                    message.DateLabel ?? string.Empty,
                    message.Direction ?? string.Empty,
                    message.Sender ?? string.Empty,
                    message.Text ?? string.Empty,
                    message.TimeLabel ?? string.Empty,
                    message.ReadLabel ?? string.Empty,
                    message.ClassName ?? string.Empty,
                    message.Html ?? string.Empty)).ToList());
    }

    /// <summary>
    /// Playwright 브라우저 자원을 역순으로 정리합니다.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _browserContext.DisposeAsync();
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    private static void ConfigurePage(IPage page)
    {
        page.SetDefaultNavigationTimeout(30000);
        page.SetDefaultTimeout(15000);
    }

    private Task NavigateAsync(string url, CancellationToken cancellationToken)
    {
        return NavigateAsync(_page, url, reportNavigationStatus: true, cancellationToken);
    }

    private async Task NavigateAsync(
        IPage page,
        string url,
        bool reportNavigationStatus,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reportNavigationStatus)
        {
            _naverTalkBrowser.SetStatus($"Playwright navigating: {url}", url);
        }

        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 30000
        });
    }

    private Task WaitForPartnerTalkReadyAsync(string? expectedUrl, CancellationToken cancellationToken)
    {
        return WaitForPartnerTalkReadyAsync(_page, expectedUrl, cancellationToken);
    }

    private async Task WaitForPartnerTalkReadyAsync(
        IPage page,
        string? expectedUrl,
        CancellationToken cancellationToken)
    {
        var expectedPath = string.Empty;
        if (Uri.TryCreate(expectedUrl, UriKind.Absolute, out var expectedUri))
        {
            expectedPath = expectedUri.AbsolutePath;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = await EvaluateJsonAsync<PartnerTalkReadyState>(page, BuildPartnerTalkReadyScript());
            var actualUrl = state?.Url ?? string.Empty;
            var actualReadyState = state?.ReadyState ?? string.Empty;
            var urlMatches = string.IsNullOrWhiteSpace(expectedPath) ||
                             (!string.IsNullOrWhiteSpace(actualUrl) &&
                              Uri.TryCreate(actualUrl, UriKind.Absolute, out var actualUri) &&
                              string.Equals(actualUri.AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase));

            if (IsPartnerTalkPage(actualUrl) &&
                urlMatches &&
                (actualReadyState.Contains("interactive", StringComparison.OrdinalIgnoreCase) ||
                 actualReadyState.Contains("complete", StringComparison.OrdinalIgnoreCase)))
            {
                await Task.Delay(350, cancellationToken);
                return;
            }

            await Task.Delay(350, cancellationToken);
        }

        await Task.Delay(600, cancellationToken);
    }

    private Task WaitForCompletedChatDetailPageReadyAsync(string expectedUrl, CancellationToken cancellationToken)
    {
        return WaitForCompletedChatDetailPageReadyAsync(_page, expectedUrl, cancellationToken);
    }

    private async Task WaitForCompletedChatDetailPageReadyAsync(
        IPage page,
        string expectedUrl,
        CancellationToken cancellationToken)
    {
        await WaitForPartnerTalkReadyAsync(page, expectedUrl, cancellationToken);

        var expectedPath = string.Empty;
        if (Uri.TryCreate(expectedUrl, UriKind.Absolute, out var absolute))
        {
            expectedPath = absolute.AbsolutePath;
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = await EvaluateJsonAsync<CompletedChatDetailReadyState>(page, BuildCompletedChatDetailReadyScript());
            var urlMatches = string.IsNullOrWhiteSpace(expectedPath) ||
                             (!string.IsNullOrWhiteSpace(state?.Url) &&
                              state.Url.Contains(expectedPath, StringComparison.OrdinalIgnoreCase));
            var hasDetailSignals = (state?.BalloonCount ?? 0) > 0 ||
                                   (state?.SystemCount ?? 0) > 0 ||
                                   (state?.InfoCount ?? 0) > 0 ||
                                   (state?.HasHeader ?? false);

            if (urlMatches && hasDetailSignals)
            {
                await Task.Delay(500, cancellationToken);
                return;
            }

            await Task.Delay(400, cancellationToken);
        }
    }

    private async Task<List<ScrapedRoute>> LoadCompletedChatRoutesFromPageAsync(
        HashSet<string> knownRoutes,
        CancellationToken cancellationToken)
    {
        var routes = new Dictionary<string, ScrapedRoute>(StringComparer.OrdinalIgnoreCase);
        var stagnantIterations = 0;
        var knownOnlyIterations = 0;

        for (var iteration = 0; iteration < 24; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await EvaluateJsonAsync<List<ScrapedRoute>>(BuildVisibleRoutesScript()) ?? [];
            foreach (var route in batch)
            {
                route.Href ??= string.Empty;
                route.AbsoluteUrl = BuildAbsolutePartnerTalkEndUrl(route.Href);
                if (!routes.ContainsKey(route.Href))
                {
                    routes.Add(route.Href, route);
                }
            }

            var newCount = routes.Values.Count(route => !knownRoutes.Contains(route.Href ?? string.Empty));
            if (newCount == 0 && batch.Count > 0 && batch.All(route => knownRoutes.Contains(route.Href ?? string.Empty)))
            {
                knownOnlyIterations++;
            }
            else
            {
                knownOnlyIterations = 0;
            }

            var beforeScrollCount = routes.Count;
            await _page.EvaluateAsync<string>(BuildScrollCompletedChatsListScript());
            await Task.Delay(600, cancellationToken);

            var afterBatch = await EvaluateJsonAsync<List<ScrapedRoute>>(BuildVisibleRoutesScript()) ?? [];
            foreach (var route in afterBatch)
            {
                route.Href ??= string.Empty;
                route.AbsoluteUrl = BuildAbsolutePartnerTalkEndUrl(route.Href);
                if (!routes.ContainsKey(route.Href))
                {
                    routes.Add(route.Href, route);
                }
            }

            stagnantIterations = routes.Count == beforeScrollCount ? stagnantIterations + 1 : 0;
            if (stagnantIterations >= 3 || knownOnlyIterations >= 2)
            {
                break;
            }
        }

        return routes.Values.ToList();
    }

    private async Task<ApiCompletedRouteCollectionResult?> LoadCompletedChatRoutesFromApiAsync(
        DateTime fromDate,
        string partnerChatId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fromDateCompact = fromDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var script = $$"""
(() => {
    try {
        const fromDateCompact = {{JsonSerializer.Serialize(fromDateCompact)}};
        const chatId = {{JsonSerializer.Serialize(partnerChatId)}};
        const maxPages = 500;

        function toYmd(epochMs) {
            if (!Number.isFinite(epochMs)) {
                return '';
            }

            const d = new Date(epochMs);
            if (Number.isNaN(d.getTime())) {
                return '';
            }

            const y = d.getFullYear();
            const m = String(d.getMonth() + 1).padStart(2, '0');
            const day = String(d.getDate()).padStart(2, '0');
            return `${y}-${m}-${day}`;
        }

        const routes = [];
        const seen = new Set();
        let minCursor = null;
        let pagesFetched = 0;
        let hasNextPageAtEnd = false;

        function requestPage(nextCursor) {
            const fd = new FormData();
            fd.append('fromDate', fromDateCompact);
            fd.append('order', 'lstDesc');
            fd.append('filter', 'end');
            if (nextCursor != null) {
                fd.append('min', String(nextCursor));
            }

            const xhr = new XMLHttpRequest();
            xhr.open('POST', `/chatapi/ct/partner/${chatId}/list`, false);
            xhr.withCredentials = true;
            xhr.send(fd);

            if (xhr.status < 200 || xhr.status >= 300) {
                return {
                    ok: false,
                    errorMessage: `list api failed: ${xhr.status}`
                };
            }

            const json = JSON.parse(xhr.responseText || '{}');
            const ret = json?.htReturnValue || {};
            if (!json?.bSuccess) {
                return {
                    ok: false,
                    errorMessage: json?.sErrorCode || 'list api returned bSuccess=false'
                };
            }

            return {
                ok: true,
                hasNextPage: !!ret.hasNextPage,
                list: Array.isArray(ret.chatList) ? ret.chatList : []
            };
        }

        for (let pageIndex = 0; pageIndex < maxPages; pageIndex += 1) {
            const page = requestPage(minCursor);
            if (!page.ok) {
                return JSON.stringify({
                    ok: false,
                    errorMessage: page.errorMessage || 'list api request failed'
                });
            }

            const list = Array.isArray(page.list) ? page.list : [];
            pagesFetched += 1;
            hasNextPageAtEnd = !!page.hasNextPage;
            if (list.length === 0) {
                break;
            }

            for (const item of list) {
                const href = `/chat/ct/${chatId}/${item.chatUrl}`;
                if (!href || seen.has(href)) {
                    continue;
                }

                seen.add(href);
                routes.push({
                    href,
                    name: item.name || '',
                    previewText: item.text || '',
                    timeLabel: toYmd(Number(item.date || 0)),
                    dateMs: Number(item.date || 0)
                });
            }

            minCursor = list[list.length - 1]?.date || list[list.length - 1]?.no || null;
            if (!page.hasNextPage || minCursor == null) {
                break;
            }
        }

        return JSON.stringify({
            ok: true,
            routes,
            pagesFetched,
            lastCursor: minCursor == null ? '' : String(minCursor),
            hasNextPageAtEnd
        });
    } catch (error) {
        return JSON.stringify({
            ok: false,
            errorMessage: String(error && error.message ? error.message : error)
        });
    }
})();
""";

        var result = await EvaluateJsonAsync<ApiCompletedRouteCollectionResult>(script);
        if (result?.Routes is not null)
        {
            foreach (var route in result.Routes)
            {
                route.Href ??= string.Empty;
                route.AbsoluteUrl = BuildAbsolutePartnerTalkEndUrl(route.Href);
            }
        }

        return result;
    }

    private Task LoadEntireCompletedChatHistoryAsync(CancellationToken cancellationToken)
    {
        return LoadEntireCompletedChatHistoryAsync(_page, cancellationToken);
    }

    private async Task LoadEntireCompletedChatHistoryAsync(IPage page, CancellationToken cancellationToken)
    {
        var stableIterations = 0;
        var lastCount = -1;

        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var before = await CountCompletedChatBalloonsAsync(page);
            if (before <= 0)
            {
                return;
            }

            await page.EvaluateAsync<string>(BuildLoadHistoryScrollScript());
            await Task.Delay(450, cancellationToken);

            var after = await CountCompletedChatBalloonsAsync(page);
            if (after == lastCount)
            {
                stableIterations++;
            }
            else
            {
                stableIterations = 0;
                lastCount = after;
            }

            if (after <= before || stableIterations >= 4)
            {
                if (stableIterations >= 4 || after <= 0)
                {
                    break;
                }
            }
        }
    }

    private Task<int> CountCompletedChatBalloonsAsync()
    {
        return CountCompletedChatBalloonsAsync(_page);
    }

    private async Task<int> CountCompletedChatBalloonsAsync(IPage page)
    {
        var state = await EvaluateJsonAsync<CompletedChatBalloonCountState>(
            page,
            """
            (() => JSON.stringify({
                count: document.querySelectorAll('li.balloon_item').length
            }))();
            """);

        return state?.Count ?? 0;
    }

    private Task<T?> EvaluateJsonAsync<T>(string script)
    {
        return EvaluateJsonAsync<T>(_page, script);
    }

    private async Task<T?> EvaluateJsonAsync<T>(IPage page, string script)
    {
        var raw = await page.EvaluateAsync<string>(script);
        return ParseScriptJson<T>(raw);
    }

    private static BrowserTypeLaunchOptions CreateBrowserLaunchOptions()
    {
        var launchOptions = new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args =
            [
                "--disable-blink-features=AutomationControlled"
            ]
        };

        var executablePath = TryFindChromiumExecutablePath();
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            launchOptions.ExecutablePath = executablePath;
        }
        else
        {
            launchOptions.Channel = "msedge";
        }

        return launchOptions;
    }

    private static string? TryFindChromiumExecutablePath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string[] candidatePaths =
        [
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
        ];

        return candidatePaths.FirstOrDefault(File.Exists);
    }

    private static string BuildCompletedChatsListUrl(string? configuredTargetUrl, string? partnerChatId)
    {
        if (!string.IsNullOrWhiteSpace(partnerChatId))
        {
            return $"https://partner.talk.naver.com/chat/ct/{partnerChatId}?state=end&isMobile=false&device=pc";
        }

        var sourceUrl = string.IsNullOrWhiteSpace(configuredTargetUrl)
            ? NaverTalkSettings.DefaultTargetUrl
            : configuredTargetUrl;

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return NaverTalkSettings.DefaultTargetUrl.Replace("state=wait", "state=end", StringComparison.OrdinalIgnoreCase);
        }

        return $"{uri.GetLeftPart(UriPartial.Path)}?state=end&device=pc";
    }

    private static string BuildAbsolutePartnerTalkEndUrl(string href)
    {
        var absolute = Uri.TryCreate(href, UriKind.Absolute, out var existingAbsolute)
            ? existingAbsolute
            : new Uri($"https://partner.talk.naver.com{href}");

        return $"{absolute.GetLeftPart(UriPartial.Path)}?state=end&device=pc";
    }

    private static bool IsPartnerTalkPage(string? url)
    {
        return !string.IsNullOrWhiteSpace(url) &&
               url.Contains("partner.talk.naver.com", StringComparison.OrdinalIgnoreCase);
    }

    private static Cookie ToPlaywrightCookie(NaverTalkPersistedCookie cookie)
    {
        return new Cookie
        {
            Name = cookie.Name,
            Value = cookie.Value,
            Domain = cookie.Domain,
            Path = cookie.Path,
            Expires = (float?)cookie.ExpiresAtUnixSeconds,
            Secure = cookie.IsSecure,
            HttpOnly = cookie.IsHttpOnly,
            SameSite = ParseSameSite(cookie.SameSite)
        };
    }

    private static SameSiteAttribute? ParseSameSite(string? sameSite)
    {
        return sameSite switch
        {
            "Strict" => SameSiteAttribute.Strict,
            "Lax" => SameSiteAttribute.Lax,
            "None" => SameSiteAttribute.None,
            _ => null
        };
    }

    private static T? ParseScriptJson<T>(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return default;
        }

        try
        {
            var inner = JsonSerializer.Deserialize<string>(raw);
            if (!string.IsNullOrWhiteSpace(inner))
            {
                return JsonSerializer.Deserialize<T>(inner, CollectionJsonOptions);
            }
        }
        catch
        {
        }

        try
        {
            return JsonSerializer.Deserialize<T>(raw, CollectionJsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private static CompletedChatRoute ToRoute(ScrapedRoute route)
    {
        return new CompletedChatRoute(
            route.Href ?? string.Empty,
            route.Name ?? string.Empty,
            route.TimeLabel ?? string.Empty,
            route.PreviewText ?? string.Empty,
            route.AbsoluteUrl ?? string.Empty,
            route.DateMs);
    }

    private static string BuildPartnerChatIdResolutionScript()
    {
        return """
(() => {
    try {
        const pathMatch = String(location.pathname || '').match(/\/chat\/ct\/([^/?#]+)/i);
        const anchor = document.querySelector('a[href*="/chat/ct/"]');
        return JSON.stringify({
            url: location.href || '',
            bodyText: String(document.body?.innerText || document.body?.textContent || ''),
            pathPartnerChatId: pathMatch ? pathMatch[1] : '',
            anchorHref: anchor?.getAttribute('href') || ''
        });
    } catch (error) {
        return JSON.stringify({
            url: location.href || '',
            bodyText: '',
            pathPartnerChatId: '',
            anchorHref: '',
            error: String(error && error.message ? error.message : error)
        });
    }
})();
""";
    }

    private static string BuildPartnerTalkReadyScript()
    {
        return """
(() => JSON.stringify({
    url: location.href || '',
    readyState: document.readyState || ''
}))();
""";
    }

    private static string BuildVisibleRoutesScript()
    {
        return """
(() => {
    try {
        const anchors = document.querySelectorAll('a[href*="/chat/ct/"]');
        const seen = {};
        const items = [];
        for (let index = 0; index < anchors.length; index++) {
            const anchor = anchors[index];
            const href = anchor.getAttribute('href');
            if (!href || seen[href]) {
                continue;
            }

            seen[href] = true;
            const text = (anchor.innerText || anchor.textContent || '').trim();
            const lines = text.split(/\n+/).map(value => value.trim()).filter(Boolean);
            const name = lines.length > 0 ? lines[0] : '';
            const looksLikeTime = (value) => /^(오늘|어제|\d{4}-\d{2}-\d{2}|(?:오전|오후)\s*\d{1,2}:\d{2}|\d{1,2}\.\s*\d{1,2}\.|[\d\s월일:\-]+)$/.test(value || '');
            const secondLine = lines.length > 1 ? lines[1] : '';
            const lastLine = lines.length > 1 ? lines[lines.length - 1] : '';
            const timeLabel = looksLikeTime(secondLine) && !looksLikeTime(lastLine) ? secondLine : lastLine;
            const previewText = looksLikeTime(secondLine) && lines.length > 2
                ? lines.slice(2).join(' ')
                : (lines.length > 2 ? lines.slice(1, lines.length - 1).join(' ') : (lines.length > 1 ? lines[1] : ''));
            items.push({ href, name, timeLabel, previewText });
        }

        return JSON.stringify(items);
    } catch (error) {
        return JSON.stringify([{ href: '', name: 'SCRIPT_ERROR', timeLabel: '', previewText: String(error && error.message ? error.message : error) }]);
    }
})();
""";
    }

    private static string BuildScrollCompletedChatsListScript()
    {
        return """
(() => {
    const candidates = [...document.querySelectorAll('*')]
        .filter(el => el.scrollHeight > el.clientHeight + 120)
        .sort((a, b) => (b.clientHeight * b.clientWidth) - (a.clientHeight * a.clientWidth));
    const target = candidates[0] || document.scrollingElement || document.body;
    target.scrollTop = target.scrollHeight;
    return JSON.stringify({ ok: true, scrollTop: target.scrollTop, scrollHeight: target.scrollHeight });
})();
""";
    }

    private static string BuildRouteDiagnosticsScript()
    {
        return """
(() => {
    try {
        function textOf(el) {
            return String((el && (el.innerText || el.textContent)) || '').replace(/\u00a0/g, ' ').trim();
        }

        const anchors = [];
        const anchorNodes = document.querySelectorAll('a');
        for (let index = 0; index < anchorNodes.length && index < 120; index++) {
            const el = anchorNodes[index];
            anchors.push({
                href: el.getAttribute('href') || '',
                text: textOf(el).slice(0, 300),
                className: String(el.className || '')
            });
        }

        const buttons = [];
        const buttonNodes = document.querySelectorAll('button,[role="button"]');
        for (let index = 0; index < buttonNodes.length && index < 120; index++) {
            const el = buttonNodes[index];
            buttons.push({
                text: textOf(el).slice(0, 300),
                className: String(el.className || ''),
                dataHref: el.getAttribute('data-href') || '',
                onclick: (el.getAttribute('onclick') || '').slice(0, 300)
            });
        }

        return JSON.stringify({
            title: document.title || '',
            url: location.href,
            readyState: document.readyState || '',
            anchorCount: anchorNodes.length,
            buttonCount: buttonNodes.length,
            bodyTextSample: textOf(document.body).slice(0, 4000),
            anchors,
            buttons
        });
    } catch (error) {
        return JSON.stringify({
            error: String(error && error.message ? error.message : error),
            url: location.href,
            readyState: document.readyState || ''
        });
    }
})();
""";
    }

    private static string BuildCompletedChatDetailReadyScript()
    {
        return """
(() => JSON.stringify({
    url: location.href || '',
    balloonCount: document.querySelectorAll('li.balloon_item').length,
    systemCount: document.querySelectorAll('li.system_item').length,
    infoCount: document.querySelectorAll('dl dt, dl dd').length,
    hasHeader: !!document.querySelector('.detail_header, .chat_detail_info, .order_information, .ly_info_wrap')
}))();
""";
    }

    private static string BuildCompletedChatDetailScript()
    {
        return """
(() => {
    function normalize(value) {
        return String(value || '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
    }

    function textOf(el) {
        return normalize(el?.innerText || el?.textContent || '');
    }

    function parseMsgId(node) {
        const className = node.className || '';
        const match = className.match(/_msgId(\d+)/);
        if (match) {
            return Number(match[1]);
        }

        const attr = node.getAttribute?.('data-msg-id');
        return attr ? Number(attr) : null;
    }

    const infoRows = [];
    for (const row of document.querySelectorAll('dl')) {
        const labels = [...row.querySelectorAll('dt')];
        const values = [...row.querySelectorAll('dd')];
        const length = Math.min(labels.length, values.length);
        for (let index = 0; index < length; index += 1) {
            infoRows.push({
                label: textOf(labels[index]),
                value: textOf(values[index])
            });
        }
    }

    const nodes = Array.from(document.querySelectorAll('li.system_item, li.balloon_item'));
    const messages = [];
    let currentDate = '';
    for (const node of nodes) {
        if (node.classList.contains('system_item')) {
            const dateLabel =
                textOf(node.querySelector('.date_marker, ._date_marker, .date_marker_area')) ||
                textOf(node);
            if (dateLabel) {
                currentDate = dateLabel;
                messages.push({
                    kind: 'system',
                    msgId: null,
                    dateLabel,
                    direction: '',
                    sender: '',
                    text: '',
                    timeLabel: '',
                    readLabel: '',
                    className: node.className || '',
                    html: node.outerHTML || ''
                });
            }

            continue;
        }

        const className = node.className || '';
        const msgId = parseMsgId(node);
        const bodyText =
            textOf(node.querySelector('.title_content ._copy_area, .title_content, .composite_text.-detail, .composite_text.-title')) ||
            textOf(node);
        if (!bodyText) {
            continue;
        }

        const direction = className.includes('_my') ? 'partner' : 'customer';
        messages.push({
            kind: 'message',
            msgId,
            dateLabel: currentDate,
            direction,
            sender: textOf(node.querySelector('.writer_name, .name, .text_name')),
            text: bodyText,
            timeLabel: textOf(node.querySelector('.time_area, .font_hv, .text_time')),
            readLabel: textOf(node.querySelector('.text_read, .read, ._read')),
            className,
            html: node.outerHTML || ''
        });
    }

    return JSON.stringify({
        pageTitle: document.title || '',
        pageUrl: location.href,
        headerText: textOf(document.querySelector('.detail_header, .chat_detail_info, .order_information, .ly_info_wrap')),
        infoRows,
        messages
    });
})();
""";
    }

    private static string BuildLoadHistoryScrollScript()
    {
        return """
(() => {
    const balloons = document.querySelectorAll('li.balloon_item');
    if (balloons.length === 0) {
        return JSON.stringify({ ok: false, count: 0 });
    }

    let el = balloons[0].parentElement;
    while (el) {
        const style = getComputedStyle(el);
        const canScroll = /(auto|scroll)/.test(style.overflowY || '');
        if (canScroll && el.scrollHeight > el.clientHeight + 20) {
            el.scrollTop = 0;
            return JSON.stringify({ ok: true, count: balloons.length, scrollTop: el.scrollTop });
        }

        el = el.parentElement;
    }

    return JSON.stringify({ ok: false, count: balloons.length });
})();
""";
    }

    /// <summary>
    /// 같은 browser context를 공유하면서 detail 페이지 하나만 담당하는 Playwright worker입니다.
    /// route 목록 수집과 분리해서 여러 page가 동시에 상세를 읽을 수 있게 합니다.
    /// </summary>
    private sealed class PlaywrightCompletedChatsDetailSession(
        PlaywrightCompletedChatsSession owner,
        IPage page) : ICompletedChatsDetailSession
    {
        public async Task<CompletedChatDetail?> LoadDetailAsync(
            CompletedChatRoute route,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await owner.NavigateAsync(page, route.AbsoluteUrl, reportNavigationStatus: false, cancellationToken);
            await owner.WaitForCompletedChatDetailPageReadyAsync(page, route.AbsoluteUrl, cancellationToken);
            await owner.LoadEntireCompletedChatHistoryAsync(page, cancellationToken);

            var detail = await owner.EvaluateJsonAsync<ScrapedChatDetail>(page, BuildCompletedChatDetailScript());
            return detail is null
                ? null
                : new CompletedChatDetail(
                    detail.PageTitle ?? string.Empty,
                    detail.PageUrl ?? string.Empty,
                    detail.HeaderText ?? string.Empty,
                    detail.InfoRows?.Select(static row => new CompletedChatInfoRow(row.Label ?? string.Empty, row.Value ?? string.Empty)).ToList(),
                    detail.Messages?.Select(static message => new CompletedChatMessage(
                        message.Kind ?? string.Empty,
                        message.MsgId,
                        message.DateLabel ?? string.Empty,
                        message.Direction ?? string.Empty,
                        message.Sender ?? string.Empty,
                        message.Text ?? string.Empty,
                        message.TimeLabel ?? string.Empty,
                        message.ReadLabel ?? string.Empty,
                        message.ClassName ?? string.Empty,
                        message.Html ?? string.Empty)).ToList());
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(page.CloseAsync());
        }
    }

    private sealed record PartnerTalkReadyState(string Url, string ReadyState);

    private sealed record CompletedChatDetailReadyState(
        string Url,
        int BalloonCount,
        int SystemCount,
        int InfoCount,
        bool HasHeader);

    private sealed record PartnerChatIdResolutionState(
        string Url,
        string BodyText,
        string PathPartnerChatId,
        string AnchorHref,
        string? Error = null);

    private sealed record CompletedChatBalloonCountState(int Count);

    private sealed class ApiCompletedRouteCollectionResult
    {
        public bool Ok { get; set; }

        public string? ErrorMessage { get; set; }

        public List<ScrapedRoute> Routes { get; set; } = [];

        public int? PagesFetched { get; set; }

        public string? LastCursor { get; set; }

        public bool? HasNextPageAtEnd { get; set; }
    }

    private sealed class ScrapedRoute
    {
        public string? Href { get; set; }

        public string? Name { get; set; }

        public string? TimeLabel { get; set; }

        public string? PreviewText { get; set; }

        public string? AbsoluteUrl { get; set; }

        public long? DateMs { get; set; }
    }

    private sealed class ScrapedInfoRow
    {
        public string? Label { get; set; }

        public string? Value { get; set; }
    }

    private sealed class ScrapedMessage
    {
        public string? Kind { get; set; }

        public long? MsgId { get; set; }

        public string? DateLabel { get; set; }

        public string? Direction { get; set; }

        public string? Sender { get; set; }

        public string? Text { get; set; }

        public string? TimeLabel { get; set; }

        public string? ReadLabel { get; set; }

        public string? ClassName { get; set; }

        public string? Html { get; set; }
    }

    private sealed class ScrapedChatDetail
    {
        public string? PageTitle { get; set; }

        public string? PageUrl { get; set; }

        public string? HeaderText { get; set; }

        public List<ScrapedInfoRow>? InfoRows { get; set; }

        public List<ScrapedMessage>? Messages { get; set; }
    }
}
