using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: CompletedChatThreadsExporter <source-json> <output-jsonl>");
    return 1;
}

var sourcePath = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[1]);

if (!File.Exists(sourcePath))
{
    Console.Error.WriteLine($"Source file not found: {sourcePath}");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory);

var serializerOptions = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
};

await using var sourceStream = File.OpenRead(sourcePath);
using var document = await JsonDocument.ParseAsync(sourceStream);

if (!document.RootElement.TryGetProperty("chats", out var chatsElement) ||
    chatsElement.ValueKind != JsonValueKind.Array)
{
    Console.Error.WriteLine("The source JSON does not contain a chats array.");
    return 1;
}

await using var outputStream = File.Create(outputPath);
await using var writer = new StreamWriter(outputStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

var exportedCount = 0;
foreach (var chatElement in chatsElement.EnumerateArray())
{
    var projectedThread = CompletedChatThreadProjector.Project(chatElement);
    if (projectedThread is null)
    {
        continue;
    }

    await writer.WriteLineAsync(JsonSerializer.Serialize(projectedThread, serializerOptions));
    exportedCount++;
}

await writer.FlushAsync();

var outputInfo = new FileInfo(outputPath);
Console.WriteLine($"Exported {exportedCount} threads to {outputInfo.FullName} ({outputInfo.Length} bytes).");
return 0;

/// <summary>
/// completed-chats.json 한 건을 빠르게 읽기 좋은 thread JSONL 레코드로 변환합니다.
/// 원본 보관용 필드는 버리고, 대화 읽기와 재검색에 필요한 최소 정보만 남깁니다.
/// </summary>
internal static partial class CompletedChatThreadProjector
{
    private static readonly Regex KoreanDatePattern = CreateKoreanDatePattern();
    private static readonly Regex DottedDatePattern = CreateDottedDatePattern();
    private static readonly Regex IsoDatePattern = CreateIsoDatePattern();
    private static readonly Regex TimeInHtmlPattern = CreateTimeInHtmlPattern();
    private static readonly Regex TimeLabelPattern = CreateTimeLabelPattern();
    private static readonly TimeSpan KoreaOffset = TimeSpan.FromHours(9);

    /// <summary>
    /// source chat 노드에서 route, info, message를 읽어 경량 thread 레코드를 만듭니다.
    /// 메시지는 customer / partner 본문만 남기고 system marker나 HTML 원문은 저장하지 않습니다.
    /// </summary>
    public static CompletedChatThreadRecord? Project(JsonElement chatElement)
    {
        if (!chatElement.TryGetProperty("route", out var routeElement) ||
            !routeElement.TryGetProperty("href", out var hrefElement))
        {
            return null;
        }

        var routeHref = hrefElement.GetString();
        if (string.IsNullOrWhiteSpace(routeHref))
        {
            return null;
        }

        var (partnerChatId, roomId) = SplitRoute(routeHref);
        var routeName = TryGetString(routeElement, "name");
        var routeTimeLabel = TryGetString(routeElement, "timeLabel");
        var previewText = TryGetString(routeElement, "previewText");
        var info = BuildInfo(chatElement);
        var messages = BuildMessages(chatElement);
        var conversationText = BuildConversationText(messages);

        return new CompletedChatThreadRecord(
            ThreadId: BuildThreadId(partnerChatId, roomId, routeHref),
            PartnerChatId: partnerChatId,
            RoomId: roomId,
            RouteHref: routeHref,
            CustomerName: routeName,
            RouteName: routeName,
            RouteTimeLabel: routeTimeLabel,
            PreviewText: previewText,
            FirstOccurredAt: messages.FirstOrDefault(static message => message.OccurredAt is not null)?.OccurredAt,
            LastOccurredAt: messages.LastOrDefault(static message => message.OccurredAt is not null)?.OccurredAt,
            MessageCount: messages.Count,
            CustomerMessageCount: messages.Count(static message => string.Equals(message.Role, "customer", StringComparison.Ordinal)),
            PartnerMessageCount: messages.Count(static message => string.Equals(message.Role, "partner", StringComparison.Ordinal)),
            Info: info.Count == 0 ? null : info,
            Messages: messages,
            ConversationText: conversationText);
    }

    private static string BuildThreadId(string? partnerChatId, string? roomId, string routeHref)
    {
        if (!string.IsNullOrWhiteSpace(partnerChatId) && !string.IsNullOrWhiteSpace(roomId))
        {
            return $"{partnerChatId}/{roomId}";
        }

        return routeHref;
    }

    private static Dictionary<string, string> BuildInfo(JsonElement chatElement)
    {
        var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!chatElement.TryGetProperty("infoRows", out var infoRowsElement) ||
            infoRowsElement.ValueKind != JsonValueKind.Array)
        {
            return info;
        }

        foreach (var rowElement in infoRowsElement.EnumerateArray())
        {
            var label = TryGetString(rowElement, "label");
            var value = TryGetString(rowElement, "value");
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            info[label.Trim()] = value.Trim();
        }

        return info;
    }

    private static List<CompletedChatMiniMessageRecord> BuildMessages(JsonElement chatElement)
    {
        var messages = new List<CompletedChatMiniMessageRecord>();
        if (!chatElement.TryGetProperty("messages", out var messagesElement) ||
            messagesElement.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        var sequence = 0;
        DateTime? currentDate = null;
        DateTimeOffset? lastOccurredAt = null;

        foreach (var messageElement in messagesElement.EnumerateArray())
        {
            var kind = TryGetString(messageElement, "kind");
            if (!string.Equals(kind, "message", StringComparison.Ordinal))
            {
                continue;
            }

            var direction = TryGetString(messageElement, "direction");
            var role = NormalizeRole(direction);
            if (role is null)
            {
                continue;
            }

            var text = TryGetString(messageElement, "text");
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var dateLabel = TryGetString(messageElement, "dateLabel");
            if (TryParseDateLabel(dateLabel, currentDate ?? DateTime.Today, out var parsedDate))
            {
                currentDate = parsedDate;
            }

            var timeLabel = TryGetString(messageElement, "timeLabel");
            if (string.IsNullOrWhiteSpace(timeLabel))
            {
                timeLabel = TryExtractTimeLabelFromHtml(TryGetString(messageElement, "html"));
            }

            var occurredAt = TryBuildOccurredAt(currentDate, timeLabel);
            if (occurredAt is null && lastOccurredAt is not null && currentDate == lastOccurredAt.Value.Date)
            {
                occurredAt = lastOccurredAt;
            }

            if (occurredAt is not null)
            {
                lastOccurredAt = occurredAt;
            }

            sequence++;
            messages.Add(new CompletedChatMiniMessageRecord(
                Seq: sequence,
                Role: role,
                Text: text.Trim(),
                OccurredAt: occurredAt?.ToString("O", CultureInfo.InvariantCulture)));
        }

        return messages;
    }

    private static string BuildConversationText(IReadOnlyList<CompletedChatMiniMessageRecord> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append('[');
            builder.Append(message.Role);
            builder.Append("] ");
            builder.Append(message.Text);
        }

        return builder.ToString();
    }

    private static string? NormalizeRole(string? direction)
    {
        return direction?.Trim() switch
        {
            "customer" => "customer",
            "partner" => "partner",
            _ => null
        };
    }

    private static DateTimeOffset? TryBuildOccurredAt(DateTime? currentDate, string? timeLabel)
    {
        if (!currentDate.HasValue || string.IsNullOrWhiteSpace(timeLabel))
        {
            return null;
        }

        var match = TimeLabelPattern.Match(timeLabel.Trim());
        if (!match.Success)
        {
            return null;
        }

        var meridiem = match.Groups[1].Value;
        var hour = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

        if (string.Equals(meridiem, "오후", StringComparison.Ordinal) && hour < 12)
        {
            hour += 12;
        }
        else if (string.Equals(meridiem, "오전", StringComparison.Ordinal) && hour == 12)
        {
            hour = 0;
        }

        var localDateTime = new DateTime(
            currentDate.Value.Year,
            currentDate.Value.Month,
            currentDate.Value.Day,
            hour,
            minute,
            0,
            DateTimeKind.Unspecified);

        return new DateTimeOffset(localDateTime, KoreaOffset);
    }

    private static string? TryExtractTimeLabelFromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = TimeInHtmlPattern.Match(html);
        if (!match.Success)
        {
            return null;
        }

        return $"{match.Groups[1].Value} {match.Groups[2].Value}:{match.Groups[3].Value}";
    }

    private static bool TryParseDateLabel(string? value, DateTime referenceDate, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        var iso = IsoDatePattern.Match(text);
        if (iso.Success &&
            TryCreateDate(iso.Groups[1].Value, iso.Groups[2].Value, iso.Groups[3].Value, out date))
        {
            return true;
        }

        var dotted = DottedDatePattern.Match(text);
        if (dotted.Success &&
            TryCreateDate(dotted.Groups[1].Value, dotted.Groups[2].Value, dotted.Groups[3].Value, out date))
        {
            return true;
        }

        var korean = KoreanDatePattern.Match(text);
        if (korean.Success &&
            TryCreateDate(referenceDate.Year.ToString(CultureInfo.InvariantCulture), korean.Groups[1].Value, korean.Groups[2].Value, out date))
        {
            return true;
        }

        if (text.Contains("오늘", StringComparison.Ordinal))
        {
            date = referenceDate.Date;
            return true;
        }

        if (text.Contains("어제", StringComparison.Ordinal))
        {
            date = referenceDate.Date.AddDays(-1);
            return true;
        }

        return false;
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

    private static (string? PartnerChatId, string? RoomId) SplitRoute(string routeHref)
    {
        var path = routeHref;
        if (Uri.TryCreate(routeHref, UriKind.Absolute, out var absoluteUri))
        {
            path = absoluteUri.AbsolutePath;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 2; index++)
        {
            if (!string.Equals(segments[index], "ct", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return (segments[index + 1], segments[index + 2]);
        }

        return (null, null);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null => null,
            _ => property.ToString()
        };
    }

    [GeneratedRegex(@"(\d{1,2})\s*월\s*(\d{1,2})\s*일", RegexOptions.CultureInvariant)]
    private static partial Regex CreateKoreanDatePattern();

    [GeneratedRegex(@"(\d{2,4})\.\s*(\d{1,2})\.\s*(\d{1,2})", RegexOptions.CultureInvariant)]
    private static partial Regex CreateDottedDatePattern();

    [GeneratedRegex(@"(\d{4})-(\d{2})-(\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex CreateIsoDatePattern();

    [GeneratedRegex(@"<em>(오전|오후)</em>\s*<span>(\d{1,2})</span>\s*:\s*<span>(\d{2})</span>", RegexOptions.CultureInvariant)]
    private static partial Regex CreateTimeInHtmlPattern();

    [GeneratedRegex(@"(오전|오후)\s*(\d{1,2}):(\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex CreateTimeLabelPattern();
}

/// <summary>
/// 상담방 한 개를 빠르게 읽기 위한 JSONL 출력 레코드입니다.
/// route 단위 메타데이터와 최소 메시지 목록, 완성된 conversationText를 함께 보관합니다.
/// </summary>
internal sealed record CompletedChatThreadRecord(
    string ThreadId,
    string? PartnerChatId,
    string? RoomId,
    string RouteHref,
    string? CustomerName,
    string? RouteName,
    string? RouteTimeLabel,
    string? PreviewText,
    string? FirstOccurredAt,
    string? LastOccurredAt,
    int MessageCount,
    int CustomerMessageCount,
    int PartnerMessageCount,
    IReadOnlyDictionary<string, string>? Info,
    IReadOnlyList<CompletedChatMiniMessageRecord> Messages,
    string ConversationText);

/// <summary>
/// 대화 읽기와 검색에 필요한 최소 메시지 형태입니다.
/// sequence, 역할, 본문, 발생 시각만 남겨 원본보다 훨씬 가볍게 유지합니다.
/// </summary>
internal sealed record CompletedChatMiniMessageRecord(
    int Seq,
    string Role,
    string Text,
    string? OccurredAt);
