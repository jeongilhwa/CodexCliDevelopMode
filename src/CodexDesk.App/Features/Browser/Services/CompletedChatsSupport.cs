using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexDesk.Services;

public static class CompletedChatsSupport
{
    private static readonly JsonSerializerOptions CompletedChatsWriteOptions = new()
    {
        WriteIndented = true
    };

    private static readonly Regex PartnerChatIdPattern = new(
        @"^[a-z0-9][a-z0-9_-]{2,127}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static CompletedChatCollectionResult BuildCollectionResult(
        bool succeeded,
        string summary,
        string outputPath,
        string baselinePath,
        DateTime fromDate,
        int discoveredRouteCount,
        int newRouteCount,
        int savedCount,
        IReadOnlyList<string> sampleItems,
        JsonSerializerOptions jsonOptions,
        JsonArray? collectedChatNodes = null,
        JsonObject? debugInfo = null)
    {
        var skippedCount = Math.Max(0, newRouteCount - savedCount);
        var collectedPayload = new JsonObject
        {
            ["fromDate"] = fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["baselinePath"] = baselinePath,
            ["outputPath"] = outputPath,
            ["discoveredRouteCount"] = discoveredRouteCount,
            ["newRouteCount"] = newRouteCount,
            ["savedCount"] = savedCount,
            ["skippedCount"] = skippedCount,
            ["debug"] = debugInfo?.DeepClone() ?? new JsonObject(),
            ["chats"] = BuildReadableCollectedChats(collectedChatNodes)
        };

        return new CompletedChatCollectionResult(
            DateTimeOffset.Now,
            succeeded,
            summary,
            outputPath,
            baselinePath,
            succeeded && savedCount > 0 && PathsEqual(outputPath, baselinePath),
            fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            discoveredRouteCount,
            newRouteCount,
            savedCount,
            skippedCount,
            sampleItems.ToArray(),
            collectedPayload.ToJsonString(jsonOptions));
    }

    public static string BuildPendingCollectionFilePath(string baselinePath)
    {
        var directory = Path.GetDirectoryName(baselinePath);
        var fileName = Path.GetFileNameWithoutExtension(baselinePath);
        var extension = Path.GetExtension(baselinePath);

        return Path.Combine(
            string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory,
            $"{fileName}.pending{extension}");
    }

    public static async Task<CompletedChatsFileContext> LoadFileAsync(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            return new CompletedChatsFileContext(
                new JsonObject { ["chats"] = new JsonArray() },
                new JsonArray(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                null,
                null,
                null);
        }

        var root = JsonNode.Parse(await File.ReadAllTextAsync(sourcePath)) as JsonObject ?? new JsonObject();
        var chats = root["chats"] as JsonArray ?? new JsonArray();
        root["chats"] = chats;

        DateTimeOffset? exportedAt = null;
        if (root["exportedAt"]?.GetValue<string>() is { } exportedAtText &&
            DateTimeOffset.TryParse(exportedAtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedExportedAt))
        {
            exportedAt = parsedExportedAt;
        }

        var knownRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chatNode in chats)
        {
            if (chatNode?["route"]?["href"]?.GetValue<string>() is { Length: > 0 } href)
            {
                knownRoutes.Add(href);
            }
        }

        string? partnerChatId = null;
        DateTime? latestKnownDate = null;
        var dateReference = exportedAt?.ToLocalTime().DateTime.Date ?? DateTime.Today;
        foreach (var chatNode in chats)
        {
            if (partnerChatId is null &&
                chatNode?["route"]?["href"]?.GetValue<string>() is { Length: > 0 } href)
            {
                partnerChatId = TryExtractPartnerChatId(href);
            }

            if (chatNode?["messages"] is JsonArray messages)
            {
                foreach (var messageNode in messages)
                {
                    var dateText = messageNode?["dateLabel"]?.GetValue<string>();
                    if (!TryParsePartnerTalkDate(dateText, dateReference, out var parsed))
                    {
                        continue;
                    }

                    latestKnownDate = !latestKnownDate.HasValue || parsed > latestKnownDate.Value
                        ? parsed
                        : latestKnownDate;
                }
            }

            if (chatNode?["route"]?["timeLabel"]?.GetValue<string>() is { Length: > 0 } routeTimeLabel &&
                TryParsePartnerTalkDate(routeTimeLabel, dateReference, out var routeDate))
            {
                latestKnownDate = !latestKnownDate.HasValue || routeDate > latestKnownDate.Value
                    ? routeDate
                    : latestKnownDate;
            }
        }

        if (exportedAt.HasValue && latestKnownDate.HasValue)
        {
            var exportedDate = exportedAt.Value.ToLocalTime().DateTime.Date;
            if (latestKnownDate.Value > exportedDate)
            {
                latestKnownDate = exportedDate;
            }
        }

        return new CompletedChatsFileContext(root, chats, knownRoutes, exportedAt, latestKnownDate, partnerChatId);
    }

    public static async Task MergeCompletedChatsFilesAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Completed chats source file was not found.", sourcePath);
        }

        var sourceRoot = await LoadCompletedChatsRootAsync(sourcePath, cancellationToken);
        var targetRoot = File.Exists(targetPath)
            ? await LoadCompletedChatsRootAsync(targetPath, cancellationToken)
            : new JsonObject { ["chats"] = new JsonArray() };

        var mergedRoot = MergeCompletedChatsPayload(sourceRoot, targetRoot);
        await File.WriteAllTextAsync(targetPath, mergedRoot.ToJsonString(CompletedChatsWriteOptions), cancellationToken);
    }

    internal static JsonObject MergeCompletedChatsPayload(JsonObject sourceRoot, JsonObject targetRoot)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(targetRoot);

        var sourceChats = EnsureChatsArray(sourceRoot);
        var targetChats = EnsureChatsArray(targetRoot);

        var mergedChats = new JsonArray();
        var knownHrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var chatNode in sourceChats)
        {
            if (chatNode is null)
            {
                continue;
            }

            mergedChats.Add(chatNode.DeepClone());

            var href = TryGetChatRouteHref(chatNode);
            if (!string.IsNullOrWhiteSpace(href))
            {
                knownHrefs.Add(href);
            }
        }

        foreach (var chatNode in targetChats)
        {
            if (chatNode is null)
            {
                continue;
            }

            var href = TryGetChatRouteHref(chatNode);
            if (!string.IsNullOrWhiteSpace(href) && knownHrefs.Contains(href))
            {
                continue;
            }

            mergedChats.Add(chatNode.DeepClone());
            if (!string.IsNullOrWhiteSpace(href))
            {
                knownHrefs.Add(href);
            }
        }

        sourceRoot["chats"] = mergedChats;
        sourceRoot["totalRoutes"] = mergedChats.Count;
        sourceRoot["exportedChats"] = mergedChats.Count;
        sourceRoot["errors"] ??= new JsonArray();

        return sourceRoot;
    }

    internal static (DateTime FromDate, string FromDateSource, bool IsInitialCollection) DetermineCollectionStartDate(
        CompletedChatsFileContext existing,
        DateTime today)
    {
        ArgumentNullException.ThrowIfNull(existing);

        var referenceToday = today.Date;
        if (existing.Chats.Count == 0)
        {
            return (referenceToday.AddMonths(-6), "initialSixMonths", true);
        }

        if (existing.ExportedAt.HasValue)
        {
            return (existing.ExportedAt.Value.ToLocalTime().DateTime.Date, "exportedAt", false);
        }

        if (existing.LatestKnownDate.HasValue)
        {
            return (existing.LatestKnownDate.Value.Date, "latestKnownDate", false);
        }

        return (referenceToday, "today", false);
    }

    public static string ResolveProjectDataPath(string fileName)
    {
        return ResolveProjectDataPath(fileName, AppContext.BaseDirectory, null);
    }

    public static string ResolveCollectionFilePath(string fileName)
    {
        if (Path.IsPathRooted(fileName))
        {
            return fileName;
        }

        return ResolveProjectDataPath(fileName);
    }

    internal static string ResolveProjectDataPath(string fileName, string baseDirectory, string? localAppDataRoot)
    {
        return CodexDeskDataPathResolver.ResolveDataPath(fileName, baseDirectory, localAppDataRoot);
    }

    /// <summary>
    /// partner talk route URL 또는 경로 문자열에서 ct 세그먼트를 찾아 partner chat id를 추출합니다.
    /// </summary>
    internal static string? TryExtractPartnerChatId(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var path = href;
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
        {
            path = absolute.AbsolutePath;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (string.Equals(segments[index], "ct", StringComparison.OrdinalIgnoreCase))
            {
                return TryNormalizePartnerChatId(segments[index + 1]);
            }
        }

        return null;
    }

    /// <summary>
    /// 현재 페이지의 URL, path 값, anchor href, 본문 텍스트를 순서대로 확인해서
    /// 가장 신뢰할 수 있는 partner chat id 하나를 결정합니다.
    /// </summary>
    internal static string? TryResolvePartnerChatId(
        string? pageUrl,
        string? pathPartnerChatId,
        string? anchorHref,
        string? pageText)
    {
        return TryExtractPartnerChatId(pageUrl)
            ?? TryNormalizePartnerChatId(pathPartnerChatId)
            ?? TryExtractPartnerChatId(anchorHref)
            ?? TryExtractPartnerChatIdFromPageText(pageText);
    }

    /// <summary>
    /// 페이지 본문 텍스트에서 account id가 아니라 실제 ct id만 골라냅니다.
    /// 판매처 이름 영역 같은 구조가 보이면 그 구간을 우선 사용하고, 마지막에만 전체 라인 후보를 검사합니다.
    /// </summary>
    internal static string? TryExtractPartnerChatIdFromPageText(string? pageText)
    {
        if (string.IsNullOrWhiteSpace(pageText))
        {
            return null;
        }

        var lines = NormalizePageText(pageText)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var candidateFromSummary = TryExtractPartnerChatIdFromAccountSummary(lines);
        if (!string.IsNullOrWhiteSpace(candidateFromSummary))
        {
            return candidateFromSummary;
        }

        var candidateLines = lines
            .Select(line => TryNormalizePartnerChatId(line, allowNumericOnly: false))
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidateLines.Count == 1)
        {
            return candidateLines[0];
        }

        return null;
/*

        var normalized = pageText
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\u00a0", " ", StringComparison.Ordinal)
            .Trim();

        var labeledMatch = Regex.Match(
            normalized,
            @"판매처\s*이름\s+([^\n]+)\s+([a-z0-9][a-z0-9_-]{3,})\s+톡톡\s*사용중",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (labeledMatch.Success)
        {
            return labeledMatch.Groups[2].Value.Trim();
        }

        var lineCandidates = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => Regex.IsMatch(line, @"^[a-z0-9][a-z0-9_-]{3,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();

        if (lineCandidates.Count == 1)
        {
            return lineCandidates[0];
        }

        return null;
*/
    }

    internal static bool TryParsePartnerTalkDate(string? value, out DateTime date)
    {
        return TryParsePartnerTalkDate(value, DateTime.Today, out date);
    }

    internal static bool TryParsePartnerTalkDate(string? value, DateTime referenceDate, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        var dotted = Regex.Match(text, @"(\d{2,4})\.\s*(\d{1,2})\.\s*(\d{1,2})");
        if (dotted.Success &&
            TryCreateDate(dotted.Groups[1].Value, dotted.Groups[2].Value, dotted.Groups[3].Value, out date))
        {
            return true;
        }

        var dottedMonthDay = Regex.Match(text, @"(?<!\d)(\d{1,2})\.\s*(\d{1,2})\.(?!\d)");
        if (dottedMonthDay.Success)
        {
            return TryCreateDate(
                referenceDate.Year.ToString(CultureInfo.InvariantCulture),
                dottedMonthDay.Groups[1].Value,
                dottedMonthDay.Groups[2].Value,
                out date);
        }

        var iso = Regex.Match(text, @"(\d{4})-(\d{2})-(\d{2})");
        if (iso.Success &&
            TryCreateDate(iso.Groups[1].Value, iso.Groups[2].Value, iso.Groups[3].Value, out date))
        {
            return true;
        }

        var korean = Regex.Match(text, @"(\d{1,2})\s*\uC6D4\s*(\d{1,2})\s*\uC77C");
        if (korean.Success)
        {
            return TryCreateDate(
                referenceDate.Year.ToString(CultureInfo.InvariantCulture),
                korean.Groups[1].Value,
                korean.Groups[2].Value,
                out date);
        }

        if (text.Contains("\uC624\uB298", StringComparison.Ordinal))
        {
            date = referenceDate.Date;
            return true;
        }

        if (text.Contains("\uC5B4\uC81C", StringComparison.Ordinal))
        {
            date = referenceDate.Date.AddDays(-1);
            return true;
        }

        return false;
    }

    private static JsonArray BuildReadableCollectedChats(JsonArray? collectedChatNodes)
    {
        var readableChats = new JsonArray();
        if (collectedChatNodes is null)
        {
            return readableChats;
        }

        foreach (var chatNode in collectedChatNodes)
        {
            if (chatNode?.DeepClone() is not JsonObject chat)
            {
                continue;
            }

            if (chat["messages"] is JsonArray messages)
            {
                foreach (var messageNode in messages)
                {
                    if (messageNode is JsonObject message)
                    {
                        message.Remove("html");
                    }
                }
            }

            readableChats.Add(chat);
        }

        return readableChats;
    }

    /// <summary>
    /// DOM 속성이나 페이지 텍스트에서 읽은 값을 partner chat id 형식으로 정규화합니다.
    /// pageText에서 추론할 때는 숫자만 있는 account id를 잘못 집지 않도록 allowNumericOnly=false를 사용합니다.
    /// </summary>
    internal static string? TryNormalizePartnerChatId(string? value, bool allowNumericOnly = true)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (!PartnerChatIdPattern.IsMatch(normalized))
        {
            return null;
        }

        if (!allowNumericOnly && normalized.All(char.IsDigit))
        {
            return null;
        }

        if (!allowNumericOnly && !normalized.Any(static ch => char.IsAsciiLetter(ch)))
        {
            return null;
        }

        return normalized;
    }

    private static string NormalizePageText(string pageText)
    {
        return pageText
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\u00a0", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static string? TryExtractPartnerChatIdFromAccountSummary(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        const string merchantNameLabel = "\uD310\uB9E4\uCC98 \uC774\uB984";
        const string talkActiveLabel = "\uD1A1\uD1A1 \uC0AC\uC6A9\uC911";

        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].Contains(merchantNameLabel, StringComparison.Ordinal))
            {
                continue;
            }

            for (var lookAhead = index + 1; lookAhead < lines.Count && lookAhead <= index + 6; lookAhead++)
            {
                var line = lines[lookAhead];
                if (line.Contains(talkActiveLabel, StringComparison.Ordinal))
                {
                    break;
                }

                var candidate = TryNormalizePartnerChatId(line, allowNumericOnly: false);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            var normalizedLeft = Path.GetFullPath(left)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedRight = Path.GetFullPath(right)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<JsonObject> LoadCompletedChatsRootAsync(string path, CancellationToken cancellationToken)
    {
        var parsed = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject;
        return parsed ?? new JsonObject { ["chats"] = new JsonArray() };
    }

    private static JsonArray EnsureChatsArray(JsonObject root)
    {
        var chats = root["chats"] as JsonArray ?? new JsonArray();
        root["chats"] = chats;
        return chats;
    }

    private static string? TryGetChatRouteHref(JsonNode chatNode)
    {
        if (chatNode is not JsonObject chatObject)
        {
            return null;
        }

        return chatObject["route"]?["href"]?.GetValue<string>();
    }

    private static bool TryCreateDate(string yearText, string monthText, string dayText, out DateTime date)
    {
        date = default;
        if (!int.TryParse(yearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(monthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(dayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day))
        {
            return false;
        }

        if (year < 100)
        {
            year += 2000;
        }

        try
        {
            date = new DateTime(year, month, day);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record CompletedChatsFileContext(
    JsonObject Root,
    JsonArray Chats,
    HashSet<string> KnownRouteHrefs,
    DateTimeOffset? ExportedAt,
    DateTime? LatestKnownDate,
    string? PartnerChatId);
