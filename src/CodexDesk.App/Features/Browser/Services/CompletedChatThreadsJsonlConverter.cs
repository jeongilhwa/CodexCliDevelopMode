using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexDesk.Services;

/// <summary>
/// completed-chats ê³„ì—´ JSON ?Œì¼???€??ê²€??ì¡°íšŒ??ê²½ëŸ‰ JSONL ?¬ë§·?¼ë¡œ ë³€?˜í•©?ˆë‹¤.
/// ?˜ì§‘ ê²°ê³¼(pending ?¬í•¨)?ì„œ ?„ìš”???„ë“œë§?ì¶”ë ¤ completed-chat-threads.jsonl???ì„±?©ë‹ˆ??
/// </summary>
internal static class CompletedChatThreadsJsonlConverter
{
    private static readonly JsonSerializerOptions JsonlSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly TimeSpan KoreaOffset = TimeSpan.FromHours(9);
    private static readonly Regex HtmlStatusTimeRegex = new(
        @"status_time[\s\S]*?(?:<em>\s*(?<meridiem>[^<\s]+)\s*</em>)?[\s\S]*?<span>\s*(?<time>\d{1,2}:\d{2})\s*</span>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PlainTimeRegex = new(
        @"(?:(?<meridiem>\uC624\uC804|\uC624\uD6C4|am|pm)\s*)?(?<time>\d{1,2}:\d{2})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// source JSON??chats ë°°ì—´???½ì–´ thread ?¨ìœ„ JSONL ?Œì¼ë¡?ë³€?˜í•©?ˆë‹¤.
    /// ë³€???¤íŒ¨ ???ˆì™¸ë¥??˜ì?ë©? ?¸ì¶œ ì¸¡ì—???˜ì§‘ ?¤íŒ¨ ?¬ë??€ ë¶„ë¦¬??ì²˜ë¦¬?????ˆìŠµ?ˆë‹¤.
    /// </summary>
    public static async Task ConvertFromCompletedChatsJsonAsync(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Completed chats source file was not found.", sourcePath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(sourcePath, cancellationToken)) as JsonObject
                   ?? new JsonObject();
        var chats = root["chats"] as JsonArray ?? new JsonArray();

        await using var outputStream = File.Create(outputPath);
        await using var writer = new StreamWriter(outputStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        foreach (var chatNode in chats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (chatNode is not JsonObject chatObject)
            {
                continue;
            }

            var projection = BuildThreadProjection(chatObject);
            if (projection is null)
            {
                continue;
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(projection, JsonlSerializerOptions));
        }

        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// ?¨ì¼ chat ?¸ë“œë¥?thread ?ˆì½”???˜ë‚˜ë¡??¬ì˜?©ë‹ˆ??
    /// route, infoRows, messagesë¥??½ì–´ ìµœì†Œ ì§ˆì˜ ?„ë“œ?€ conversationTextë¥?êµ¬ì„±?©ë‹ˆ??
    /// </summary>
    private static CompletedChatThreadProjection? BuildThreadProjection(JsonObject chat)
    {
        var route = chat["route"] as JsonObject;
        var routeHref = route?["href"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(routeHref))
        {
            return null;
        }

        var (partnerChatId, roomId) = SplitRoute(routeHref);
        var miniMessages = BuildMiniMessages(chat["messages"] as JsonArray);
        var info = BuildInfo(chat["infoRows"] as JsonArray);

        return new CompletedChatThreadProjection(
            ThreadId: BuildThreadId(partnerChatId, roomId, routeHref),
            PartnerChatId: partnerChatId,
            RoomId: roomId,
            RouteHref: routeHref,
            CustomerName: route?["name"]?.GetValue<string>(),
            FirstOccurredAt: miniMessages.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.OccurredAt))?.OccurredAt,
            LastOccurredAt: miniMessages.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.OccurredAt))?.OccurredAt,
            MessageCount: miniMessages.Count,
            Info: info.Count == 0 ? null : info,
            Messages: miniMessages,
            ConversationText: BuildConversationText(miniMessages));
    }

    /// <summary>
    /// infoRows(label/value) ë°°ì—´??key/value ?•ì…”?ˆë¦¬ë¡?ë³€?˜í•©?ˆë‹¤.
    /// </summary>
    private static Dictionary<string, string> BuildInfo(JsonArray? infoRows)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (infoRows is null)
        {
            return result;
        }

        foreach (var rowNode in infoRows)
        {
            if (rowNode is not JsonObject row)
            {
                continue;
            }

            var key = row["label"]?.GetValue<string>()?.Trim();
            var value = row["value"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// ?ë³¸ message ë°°ì—´?ì„œ ?¤ì œ customer/partner ë©”ì‹œì§€ë§?ì¶”ë ¤
    /// seq/role/text/occurredAt?¼ë¡œ ì¶•ì•½??ëª©ë¡??ë§Œë“­?ˆë‹¤.
    /// </summary>
    private static List<CompletedChatMiniMessageProjection> BuildMiniMessages(JsonArray? messages)
    {
        var result = new List<CompletedChatMiniMessageProjection>();
        if (messages is null)
        {
            return result;
        }

        DateTime referenceDate = DateTime.Today;
        var seq = 0;

        foreach (var messageNode in messages)
        {
            if (messageNode is not JsonObject message)
            {
                continue;
            }

            var kind = message["kind"]?.GetValue<string>();
            if (!string.Equals(kind, "message", StringComparison.Ordinal))
            {
                continue;
            }

            var role = NormalizeRole(message["direction"]?.GetValue<string>());
            if (role is null)
            {
                continue;
            }

            var text = message["text"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var dateLabel = message["dateLabel"]?.GetValue<string>();
            if (CompletedChatsSupport.TryParsePartnerTalkDate(dateLabel, referenceDate, out var parsedDate))
            {
                referenceDate = parsedDate;
            }

            var timeLabel = ResolveTimeLabel(message);
            var occurredAt = TryBuildOccurredAt(referenceDate, timeLabel);

            seq++;
            result.Add(new CompletedChatMiniMessageProjection(
                Seq: seq,
                Role: role,
                Text: text,
                OccurredAt: occurredAt?.ToString("O", CultureInfo.InvariantCulture)));
        }

        return result;
    }

    /// <summary>
    /// ë¯¸ë‹ˆ ë©”ì‹œì§€ ëª©ë¡??[role] text ì¤„ë°”ê¿??•íƒœ??conversationTextë¡?ì§ë ¬?”í•©?ˆë‹¤.
    /// </summary>
    private static string BuildConversationText(IReadOnlyList<CompletedChatMiniMessageProjection> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append('[')
                .Append(message.Role)
                .Append("] ")
                .Append(message.Text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// route?ì„œ ct/{partnerChatId}/{roomId} ?¸ê·¸ë¨¼íŠ¸ë¥?ë¶„ë¦¬?©ë‹ˆ??
    /// </summary>
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

    /// <summary>
    /// partnerChatId/roomIdê°€ ëª¨ë‘ ?ˆìœ¼ë©??œì? threadIdë¥?ë§Œë“¤ê³?
    /// ?†ìœ¼ë©?routeHref ?ì²´ë¥?threadIdë¡??¬ìš©?©ë‹ˆ??
    /// </summary>
    private static string BuildThreadId(string? partnerChatId, string? roomId, string routeHref)
    {
        return !string.IsNullOrWhiteSpace(partnerChatId) && !string.IsNullOrWhiteSpace(roomId)
            ? $"{partnerChatId}/{roomId}"
            : routeHref;
    }

    /// <summary>
    /// direction ê°’ì„ customer/partner ?•ê·œ????•  ê°’ìœ¼ë¡?ë³€?˜í•©?ˆë‹¤.
    /// </summary>
    private static string? NormalizeRole(string? direction)
    {
        return direction?.Trim() switch
        {
            "customer" => "customer",
            "partner" => "partner",
            _ => null
        };
    }

    /// <summary>
    /// ê¸°ì? ? ì§œ?€ timeLabel(?? ?¤ì „ 9:10, ?¤í›„ 3:40, 14:20)??ì¡°í•©??occurredAt???ì„±?©ë‹ˆ??
    /// </summary>
    private static DateTimeOffset? TryBuildOccurredAt(DateTime date, string? timeLabel)
    {
        if (string.IsNullOrWhiteSpace(timeLabel))
        {
            return null;
        }

        var text = timeLabel.Trim();
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hasMeridiem = tokens.Any(static t => IsAmToken(t) || IsPmToken(t));
        var timeToken = tokens.LastOrDefault(t => t.Contains(':', StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(timeToken))
        {
            return null;
        }

        var hhmm = timeToken.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (hhmm.Length != 2 ||
            !int.TryParse(hhmm[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour) ||
            !int.TryParse(hhmm[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute))
        {
            return null;
        }

        if (hasMeridiem)
        {
            var meridiem = tokens.FirstOrDefault(static t => IsAmToken(t) || IsPmToken(t));

            if (IsPmToken(meridiem))
            {
                if (hour < 12)
                {
                    hour += 12;
                }
            }
            else if (hour == 12)
            {
                hour = 0;
            }
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            return null;
        }

        var local = new DateTime(date.Year, date.Month, date.Day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, KoreaOffset);
    }

    private static string? ResolveTimeLabel(JsonObject message)
    {
        var explicitLabel = message["timeLabel"]?.GetValue<string>()?.Trim();
        if (!string.IsNullOrWhiteSpace(explicitLabel))
        {
            return explicitLabel;
        }

        var html = message["html"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var decodedHtml = WebUtility.HtmlDecode(html);
        var statusMatch = HtmlStatusTimeRegex.Match(decodedHtml);
        if (statusMatch.Success)
        {
            return BuildNormalizedTimeLabel(
                statusMatch.Groups["meridiem"].Value,
                statusMatch.Groups["time"].Value);
        }

        var plainMatch = PlainTimeRegex.Match(decodedHtml);
        if (!plainMatch.Success)
        {
            return null;
        }

        return BuildNormalizedTimeLabel(
            plainMatch.Groups["meridiem"].Value,
            plainMatch.Groups["time"].Value);
    }

    private static string? BuildNormalizedTimeLabel(string? meridiem, string? hhmm)
    {
        if (string.IsNullOrWhiteSpace(hhmm))
        {
            return null;
        }

        if (IsPmToken(meridiem))
        {
            return $"pm {hhmm.Trim()}";
        }

        if (IsAmToken(meridiem))
        {
            return $"am {hhmm.Trim()}";
        }

        return hhmm.Trim();
    }

    private static bool IsAmToken(string? token)
    {
        return string.Equals(token?.Trim(), "am", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token?.Trim(), "\uC624\uC804", StringComparison.Ordinal);
    }

    private static bool IsPmToken(string? token)
    {
        return string.Equals(token?.Trim(), "pm", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token?.Trim(), "\uC624\uD6C4", StringComparison.Ordinal);
    }
}

/// <summary>
/// completed-chat-threads.jsonl ??ì¤„ì„ êµ¬ì„±?˜ëŠ” ê²½ëŸ‰ thread ?ˆì½”?œì…?ˆë‹¤.
/// </summary>
internal sealed record CompletedChatThreadProjection(
    string ThreadId,
    string? PartnerChatId,
    string? RoomId,
    string RouteHref,
    string? CustomerName,
    string? FirstOccurredAt,
    string? LastOccurredAt,
    int MessageCount,
    IReadOnlyDictionary<string, string>? Info,
    IReadOnlyList<CompletedChatMiniMessageProjection> Messages,
    string ConversationText);

/// <summary>
/// thread ?ˆì½”???´ë???ìµœì†Œ ë©”ì‹œì§€ ?ˆì½”?œì…?ˆë‹¤.
/// </summary>
internal sealed record CompletedChatMiniMessageProjection(
    int Seq,
    string Role,
    string Text,
    string? OccurredAt);
