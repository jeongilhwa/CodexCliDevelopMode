namespace CodexDesk;

using CodexDesk.Services;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
#if WINDOWS
using Microsoft.Web.WebView2.Core;
using UI = Microsoft.UI.Xaml;
#endif

public partial class MainPage : ContentPage
{
    /// <summary>
    /// 완료 대화 수집 진입점을 collector로 위임합니다.
    /// MainPage는 브라우저 실행 환경만 제공하고, 실제 수집 순서는 서비스가 담당합니다.
    /// </summary>
    private Task ExecuteCompletedChatsCollectionAsync(
        string? baselineFileName = null,
        string? outputFileName = null,
        CancellationToken cancellationToken = default)
    {
        return _completedChatsCollector.CollectCompletedChatsAsync(
            new MainPageCompletedChatsBrowserSession(this),
            baselineFileName,
            outputFileName,
            cancellationToken);
    }

    /// <summary>
    /// 완료 대화 목록 화면으로 이동할 때 사용할 URL을 만듭니다.
    /// partner chat id가 있으면 명시적으로 사용하고, 없으면 현재 설정 URL을 기반으로 종료 상태 목록 URL을 조합합니다.
    /// </summary>
    private string BuildCompletedChatsListUrl(string? partnerChatId = null)
    {
        if (!string.IsNullOrWhiteSpace(partnerChatId))
        {
            return $"https://partner.talk.naver.com/chat/ct/{partnerChatId}?state=end&isMobile=false&device=pc";
        }

        var sourceUrl = string.IsNullOrWhiteSpace(_naverTalkBrowser.Settings.TargetUrl)
            ? CodexDesk.Models.NaverTalkSettings.DefaultTargetUrl
            : _naverTalkBrowser.Settings.TargetUrl;

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return CodexDesk.Models.NaverTalkSettings.DefaultTargetUrl.Replace("state=wait", "state=end", StringComparison.OrdinalIgnoreCase);
        }

        return $"{uri.GetLeftPart(UriPartial.Path)}?state=end&device=pc";
    }

    /// <summary>
    /// 수집에 필요한 partner chat id를 우선순위에 따라 찾습니다.
    /// baseline, 현재 URL, 저장된 설정 URL, 현재 페이지 텍스트 순으로 시도합니다.
    /// </summary>
    private async Task<(string? PartnerChatId, string Source)> ResolvePartnerChatIdForCompletedChatsAsync(string? baselinePartnerChatId)
    {
        if (!string.IsNullOrWhiteSpace(baselinePartnerChatId))
        {
            return (baselinePartnerChatId.Trim(), "baseline");
        }

        var currentUrlPartnerChatId = CompletedChatsSupport.TryExtractPartnerChatId(_naverTalkBrowser.CurrentUrl);
        if (!string.IsNullOrWhiteSpace(currentUrlPartnerChatId))
        {
            return (currentUrlPartnerChatId, "currentUrl");
        }

        var targetUrl = _naverTalkBrowser.Settings.TargetUrl;
        var targetUrlPartnerChatId = CompletedChatsSupport.TryExtractPartnerChatId(targetUrl);
        if (!string.IsNullOrWhiteSpace(targetUrlPartnerChatId))
        {
            return (targetUrlPartnerChatId, "settingsTargetUrl");
        }

        if (!string.IsNullOrWhiteSpace(targetUrl))
        {
            await _naverTalkBrowser.NavigateAsync(targetUrl);
            await WaitForPartnerTalkReadyAsync(targetUrl);

            var resolvedFromPage = await TryReadPartnerChatIdFromCurrentPageAsync();
            if (!string.IsNullOrWhiteSpace(resolvedFromPage))
            {
                return (resolvedFromPage, "pageText");
            }

            var completedListUrl = BuildCompletedChatsListUrl();
            if (!string.Equals(targetUrl, completedListUrl, StringComparison.OrdinalIgnoreCase))
            {
                await _naverTalkBrowser.NavigateAsync(completedListUrl);
                await WaitForPartnerTalkReadyAsync(completedListUrl);

                resolvedFromPage = await TryReadPartnerChatIdFromCurrentPageAsync();
                if (!string.IsNullOrWhiteSpace(resolvedFromPage))
                {
                    return (resolvedFromPage, "completedListPageText");
                }
            }
        }

        return (null, string.Empty);
    }

    /// <summary>
    /// 현재 페이지의 URL, anchor, 본문 텍스트를 읽어 partner chat id를 추론합니다.
    /// </summary>
    private async Task<string?> TryReadPartnerChatIdFromCurrentPageAsync()
    {
        const string script = """
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

        var raw = await RunBrowserScriptAsync(script);
        var state = ParseScriptJson<PartnerChatIdResolutionState>(raw);
        if (state is null)
        {
            return null;
        }

        return CompletedChatsSupport.TryResolvePartnerChatId(
            state.Url,
            state.PathPartnerChatId,
            state.AnchorHref,
            state.BodyText);
    }

    /// <summary>
    /// 현재 완료 대화 목록 화면을 여러 번 스크롤하면서 신규 route 후보를 모읍니다.
    /// 이미 알고 있는 route만 반복해서 보이면 조기 종료해 불필요한 DOM 스캔을 줄입니다.
    /// </summary>
    private async Task<List<ScrapedRoute>> LoadCompletedChatRoutesFromPageAsync(HashSet<string> knownRoutes)
    {
        var routes = new Dictionary<string, ScrapedRoute>(StringComparer.OrdinalIgnoreCase);
        var stagnantIterations = 0;
        var knownOnlyIterations = 0;

        for (var iteration = 0; iteration < 24; iteration++)
        {
            var batch = await ReadVisibleCompletedChatRoutesAsync();
            foreach (var route in batch)
            {
                if (!routes.ContainsKey(route.Href))
                {
                    routes.Add(route.Href, route);
                }
            }

            var newCount = routes.Values.Count(route => !knownRoutes.Contains(route.Href));
            if (newCount == 0 && batch.Count > 0 && batch.All(route => knownRoutes.Contains(route.Href)))
            {
                knownOnlyIterations++;
            }
            else
            {
                knownOnlyIterations = 0;
            }

            var beforeScrollCount = routes.Count;
            await ScrollCompletedChatsListAsync();
            await Task.Delay(600);

            var afterBatch = await ReadVisibleCompletedChatRoutesAsync();
            foreach (var route in afterBatch)
            {
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

    /// <summary>
    /// 완료 대화 목록 API를 직접 호출해 route 후보를 가져옵니다.
    /// DOM 구조가 바뀌어도 API가 살아 있으면 안정적으로 수집할 수 있도록 준비한 우선 경로입니다.
    /// </summary>
    private async Task<ApiCompletedRouteCollectionResult?> LoadCompletedChatRoutesFromApiAsync(DateTime fromDate, string partnerChatId)
    {
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

        var raw = await RunBrowserScriptAsync(script);
        var result = ParseScriptJson<ApiCompletedRouteCollectionResult>(raw);
        if (result?.Routes is not null)
        {
            foreach (var route in result.Routes)
            {
                route.AbsoluteUrl = BuildAbsolutePartnerTalkEndUrl(route.Href);
            }
        }

        return result;
    }

    /// <summary>
    /// 현재 화면에 보이는 완료 대화 anchor만 읽어 스크래핑용 route 목록으로 변환합니다.
    /// DOM fallback 경로에서 사용하며, 텍스트를 함께 읽어 미리보기와 시간 라벨도 채웁니다.
    /// </summary>
    private async Task<List<ScrapedRoute>> ReadVisibleCompletedChatRoutesAsync()
    {
        const string script = """
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

        var raw = await RunBrowserScriptAsync(script);
        var routes = ParseScriptJson<List<ScrapedRoute>>(raw) ?? new List<ScrapedRoute>();
        foreach (var route in routes)
        {
            route.AbsoluteUrl = BuildAbsolutePartnerTalkEndUrl(route.Href);
        }

        return routes
            .Where(route => !string.IsNullOrWhiteSpace(route.Href))
            .ToList();
    }

    /// <summary>
    /// 완료 대화 목록 컨테이너를 맨 아래까지 내려 다음 route가 로드되도록 유도합니다.
    /// </summary>
    private async Task ScrollCompletedChatsListAsync()
    {
        const string script = """
(() => {
    const candidates = [...document.querySelectorAll('*')]
        .filter(el => el.scrollHeight > el.clientHeight + 120)
        .sort((a, b) => (b.clientHeight * b.clientWidth) - (a.clientHeight * a.clientWidth));
    const target = candidates[0] || document.scrollingElement || document.body;
    target.scrollTop = target.scrollHeight;
    return JSON.stringify({ ok: true, scrollTop: target.scrollTop, scrollHeight: target.scrollHeight });
})();
""";

        await RunBrowserScriptAsync(script);
    }

    /// <summary>
    /// route를 한 건도 찾지 못했을 때 현재 페이지 구조를 파일로 남깁니다.
    /// 셀렉터가 깨졌는지, 다른 화면에 와 있는지 나중에 바로 확인할 수 있게 하는 진단 단계입니다.
    /// </summary>
    private async Task WriteCompletedChatRouteDiagnosticsAsync()
    {
        const string script = """
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

        var debugPath = CompletedChatsSupport.ResolveProjectDataPath("completed-route-debug.json");
        var rawPath = CompletedChatsSupport.ResolveProjectDataPath("completed-route-debug-raw.txt");
        try
        {
            var raw = await RunBrowserScriptAsync(script);
            await File.WriteAllTextAsync(rawPath, raw ?? string.Empty);
            var parsed = ParseScriptJson<JsonNode>(raw);
            if (parsed is null)
            {
                await File.WriteAllTextAsync(debugPath, "{\"error\":\"ParseScriptJson returned null.\"}");
                return;
            }

            await File.WriteAllTextAsync(debugPath, parsed.ToJsonString(AppJsonOptions));
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(debugPath, JsonSerializer.Serialize(new
            {
                error = ex.Message,
                url = _naverTalkBrowser.CurrentUrl
            }, AppJsonOptions));
        }
    }

    /// <summary>
    /// 브라우저 엔진 종류와 상관없이 자바스크립트를 실행하고 원시 결과 문자열을 돌려줍니다.
    /// partial class 전반에서 공통으로 쓰는 WebView 스크립트 실행 진입점입니다.
    /// </summary>
    private async Task<string> RunBrowserScriptAsync(string script)
    {
#if WINDOWS
        if (NaverTalkWebView.Handler?.PlatformView is UI.Controls.WebView2 platformWebView)
        {
            return await platformWebView.ExecuteScriptAsync(script);
        }
#endif

        return await NaverTalkWebView.EvaluateJavaScriptAsync(script);
    }

    /// <summary>
    /// 현재 열린 완료 대화 상세 화면에서 메시지와 주문 정보 행을 함께 읽어 옵니다.
    /// 상세 본문은 먼저 전체 히스토리를 로드한 뒤 스크래핑해 누락을 줄입니다.
    /// </summary>
    private async Task<ScrapedChatDetail?> ReadCompletedChatDetailAsync()
    {
        await LoadEntireCompletedChatHistoryAsync();

        const string script = """
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

        var raw = await RunBrowserScriptAsync(script);
        return ParseScriptJson<ScrapedChatDetail>(raw);
    }

    /// <summary>
    /// 상세 대화창을 위로 계속 스크롤해 과거 메시지가 더 이상 늘어나지 않을 때까지 로드합니다.
    /// </summary>
    private async Task LoadEntireCompletedChatHistoryAsync()
    {
        var stableIterations = 0;
        var lastCount = -1;

        for (var attempt = 0; attempt < 30; attempt++)
        {
            var before = await CountCompletedChatBalloonsAsync();
            if (before <= 0)
            {
                return;
            }

            const string scrollScript = """
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

            await RunBrowserScriptAsync(scrollScript);
            await Task.Delay(450);

            var after = await CountCompletedChatBalloonsAsync();
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

    /// <summary>
    /// 현재 상세 대화 화면에 렌더링된 말풍선 개수를 세어 추가 로딩 여부를 판단합니다.
    /// </summary>
    private async Task<int> CountCompletedChatBalloonsAsync()
    {
        const string countScript = """
(() => JSON.stringify({
    count: document.querySelectorAll('li.balloon_item').length
}))();
""";

        var raw = await RunBrowserScriptAsync(countScript);
        var state = ParseScriptJson<CompletedChatBalloonCountState>(raw);
        return state?.Count ?? 0;
    }

    /// <summary>
    /// 스크래핑 결과를 completed-chats.json 포맷과 맞는 단일 채팅 노드로 변환합니다.
    /// collector와 저장 파일이 같은 구조를 쓰도록 MainPage 쪽에서도 동일한 규칙을 유지합니다.
    /// </summary>
    private JsonNode CreateCompletedChatNode(ScrapedRoute route, ScrapedChatDetail detail)
    {
        var detailMessages = detail.Messages ?? new List<ScrapedMessage>();
        var infoRows = detail.InfoRows ?? new List<ScrapedInfoRow>();
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
        }, AppJsonOptions) ?? new JsonObject();
    }

    /// <summary>
    /// 상대 경로나 중간 상세 URL을 완료 대화 전용 절대 URL로 정규화합니다.
    /// </summary>
    private static string BuildAbsolutePartnerTalkEndUrl(string href)
    {
        var absolute = Uri.TryCreate(href, UriKind.Absolute, out var existingAbsolute)
            ? existingAbsolute
            : new Uri($"https://partner.talk.naver.com{href}");

        return $"{absolute.GetLeftPart(UriPartial.Path)}?state=end&device=pc";
    }

    /// <summary>
    /// 파트너톡 화면이 실제로 열리고 DOM ready 상태가 안정화될 때까지 기다립니다.
    /// </summary>
    private async Task WaitForPartnerTalkReadyAsync(string? expectedUrl = null)
    {
        var expectedPath = string.Empty;
        if (Uri.TryCreate(expectedUrl, UriKind.Absolute, out var expectedUri))
        {
            expectedPath = expectedUri.AbsolutePath;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            const string script = """
(() => JSON.stringify({
    url: location.href || '',
    readyState: document.readyState || ''
}))();
""";

            var raw = await RunBrowserScriptAsync(script);
            var state = ParseScriptJson<PartnerTalkReadyState>(raw);
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
                await Task.Delay(350);
                return;
            }

            await Task.Delay(350);
        }

        await Task.Delay(600);
    }

    /// <summary>
    /// 완료 대화 상세 화면에서 헤더나 메시지 같은 핵심 신호가 보일 때까지 기다립니다.
    /// route 이동 직후 바로 스크래핑해서 빈 결과가 나오는 것을 막는 용도입니다.
    /// </summary>
    private async Task WaitForCompletedChatDetailPageReadyAsync(string expectedUrl)
    {
        await WaitForPartnerTalkReadyAsync(expectedUrl);

        var expectedPath = string.Empty;
        if (Uri.TryCreate(expectedUrl, UriKind.Absolute, out var absolute))
        {
            expectedPath = absolute.AbsolutePath;
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            const string script = """
(() => JSON.stringify({
    url: location.href || '',
    balloonCount: document.querySelectorAll('li.balloon_item').length,
    systemCount: document.querySelectorAll('li.system_item').length,
    infoCount: document.querySelectorAll('dl dt, dl dd').length,
    hasHeader: !!document.querySelector('.detail_header, .chat_detail_info, .order_information, .ly_info_wrap')
}))();
""";

            var raw = await RunBrowserScriptAsync(script);
            var state = ParseScriptJson<CompletedChatDetailReadyState>(raw);
            var urlMatches = string.IsNullOrWhiteSpace(expectedPath) ||
                             (!string.IsNullOrWhiteSpace(state?.Url) &&
                              state.Url.Contains(expectedPath, StringComparison.OrdinalIgnoreCase));
            var hasDetailSignals = (state?.BalloonCount ?? 0) > 0 ||
                                   (state?.SystemCount ?? 0) > 0 ||
                                   (state?.InfoCount ?? 0) > 0 ||
                                   (state?.HasHeader ?? false);
            if (urlMatches && hasDetailSignals)
            {
                await Task.Delay(500);
                return;
            }

            await Task.Delay(400);
        }
    }

    /// <summary>
    /// WebView 자바스크립트 실행 결과를 안전하게 역직렬화합니다.
    /// WebView2처럼 JSON 문자열을 한 번 더 감싸는 환경과 일반 JSON 반환 환경을 모두 처리합니다.
    /// </summary>
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
                return JsonSerializer.Deserialize<T>(inner, AppJsonOptions);
            }
        }
        catch
        {
        }

        try
        {
            return JsonSerializer.Deserialize<T>(raw, AppJsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private void UpdateBrowserChrome()
    {
        BrowserStatusLabel.Text = _naverTalkBrowser.Status;
        BrowserUrlLabel.Text = _naverTalkBrowser.CurrentUrl;
        if (BrowserAddressEntry is not null &&
            !BrowserAddressEntry.IsFocused &&
            !string.Equals(BrowserAddressEntry.Text, _naverTalkBrowser.CurrentUrl, StringComparison.Ordinal))
        {
            BrowserAddressEntry.Text = _naverTalkBrowser.CurrentUrl;
        }
    }

    private bool WasRecentAutoLoginAttempt(string currentUrl)
    {
        if (!string.Equals(_lastAutoLoginAttemptUrl, currentUrl, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return DateTimeOffset.UtcNow - _lastAutoLoginAttemptAt < TimeSpan.FromSeconds(8);
    }

    private void RememberAutoLoginAttempt(string currentUrl)
    {
        _lastAutoLoginAttemptUrl = currentUrl;
        _lastAutoLoginAttemptAt = DateTimeOffset.UtcNow;
    }

    private static bool IsLoginPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("nid.naver.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("login", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPartnerTalkPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("partner.talk.naver.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// MainPage에 남아 있는 WebView 제어 메서드를 collector가 사용할 수 있도록 묶어 주는 어댑터입니다.
    /// 수집 서비스는 이 객체를 통해서만 브라우저에 접근합니다.
    /// </summary>
    private sealed class MainPageCompletedChatsBrowserSession(MainPage page) : ICompletedChatsBrowserSession, ICompletedChatsDetailSession
    {
        /// <summary>
        /// MainPage가 이미 가지고 있는 partner chat id 탐색 로직을 collector 계약으로 변환합니다.
        /// </summary>
        public async Task<PartnerChatIdResolution> ResolvePartnerChatIdAsync(
            string? baselinePartnerChatId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (partnerChatId, source) = await page.ResolvePartnerChatIdForCompletedChatsAsync(baselinePartnerChatId);
            return new PartnerChatIdResolution(partnerChatId, source);
        }

        /// <summary>
        /// 완료 대화 route 목록을 불러옵니다.
        /// API 수집이 가능하면 우선 사용하고, 실패하면 기존 DOM 스크래핑 경로로 자연스럽게 fallback 합니다.
        /// </summary>
        public async Task<CompletedChatsRouteLoadResult> LoadRoutesAsync(
            DateTime fromDate,
            string? partnerChatId,
            IReadOnlyCollection<string> knownRoutes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var completedUrl = page.BuildCompletedChatsListUrl(partnerChatId);
            await page._naverTalkBrowser.NavigateAsync(completedUrl);
            await page.WaitForPartnerTalkReadyAsync(completedUrl);

            ApiCompletedRouteCollectionResult? apiRoutes = null;
            if (!string.IsNullOrWhiteSpace(partnerChatId))
            {
                apiRoutes = await page.LoadCompletedChatRoutesFromApiAsync(fromDate, partnerChatId);
            }

            IReadOnlyList<CompletedChatRoute> routes = apiRoutes?.Ok == true
                ? apiRoutes.Routes
                    .Where(route => !string.IsNullOrWhiteSpace(route.Href))
                    .OrderByDescending(route => route.DateMs ?? 0)
                    .Select(ToRoute)
                    .ToList()
                : (await page.LoadCompletedChatRoutesFromPageAsync(new HashSet<string>(knownRoutes, StringComparer.OrdinalIgnoreCase)))
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
        /// route 탐색이 실패했을 때 진단용 덤프 파일을 생성합니다.
        /// </summary>
        public Task CaptureRouteDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return page.WriteCompletedChatRouteDiagnosticsAsync();
        }

        /// <summary>
        /// WebView 경로는 병렬 page를 만들 수 없으므로 현재 세션 자신을 단일 detail worker로 반환합니다.
        /// </summary>
        public Task<IReadOnlyList<ICompletedChatsDetailSession>> CreateDetailSessionsAsync(
            int desiredWorkerCount,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ICompletedChatsDetailSession>>([this]);
        }

        /// <summary>
        /// 단일 대화 route를 열고 상세 메시지/정보 행을 collector가 이해하는 DTO로 변환합니다.
        /// </summary>
        public async Task<CompletedChatDetail?> LoadDetailAsync(
            CompletedChatRoute route,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await page._naverTalkBrowser.NavigateAsync(route.AbsoluteUrl);
            await page.WaitForCompletedChatDetailPageReadyAsync(route.AbsoluteUrl);

            var detail = await page.ReadCompletedChatDetailAsync();
            return detail is null
                ? null
                : new CompletedChatDetail(
                    detail.PageTitle,
                    detail.PageUrl,
                    detail.HeaderText,
                    detail.InfoRows?.Select(static row => new CompletedChatInfoRow(row.Label, row.Value)).ToList(),
                    detail.Messages?.Select(static message => new CompletedChatMessage(
                        message.Kind,
                        message.MsgId,
                        message.DateLabel,
                        message.Direction,
                        message.Sender,
                        message.Text,
                        message.TimeLabel,
                        message.ReadLabel,
                        message.ClassName,
                        message.Html)).ToList());
        }

        /// <summary>
        /// MainPage 내부 스크래핑 모델을 collector 전용 route 모델로 변환합니다.
        /// </summary>
        private static CompletedChatRoute ToRoute(ScrapedRoute route)
        {
            return new CompletedChatRoute(
                route.Href,
                route.Name,
                route.TimeLabel,
                route.PreviewText,
                route.AbsoluteUrl,
                route.DateMs);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed record ApiCompletedRouteCollectionResult(
        bool Ok,
        string? ErrorMessage,
        List<ScrapedRoute> Routes,
        int? PagesFetched = null,
        string? LastCursor = null,
        bool? HasNextPageAtEnd = null);

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

    private sealed record PartnerTalkReadyState(
        string Url,
        string ReadyState);

    private sealed record CompletedChatBalloonCountState(int Count);

    private sealed record ScrapedRoute(
        string Href,
        string Name,
        string TimeLabel,
        string PreviewText)
    {
        public string AbsoluteUrl { get; set; } = string.Empty;

        public long? DateMs { get; init; }
    }

    private sealed record ScrapedInfoRow(string Label, string Value);

    private sealed record ScrapedMessage(
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

    private sealed record ScrapedChatDetail(
        string PageTitle,
        string PageUrl,
        string HeaderText,
        List<ScrapedInfoRow> InfoRows,
        List<ScrapedMessage> Messages);
}


