using System.Text;

namespace CodexDesk.Services;

internal sealed record WebViewCookieExportItem(
    string Name,
    string Value,
    string Domain,
    string Path);

internal sealed record WebViewCookieImportItem(
    string Name,
    string Value,
    string Domain,
    string Path,
    bool IsSecure,
    bool IsHttpOnly,
    string? SameSite);

internal sealed record WebViewCookieImportPlan(
    IReadOnlyList<WebViewCookieImportItem> CookiesToApply,
    IReadOnlyList<WebViewCookieImportItem> CookiesToDelete,
    IReadOnlyList<WebViewCookieImportItem> CookiesToReplace,
    int AddedCount,
    int ModifiedCount,
    int UnchangedCount,
    int RemovedCount);

internal static class WebViewCookieExportSupport
{
    public static bool MatchesHost(string? targetHost, string? cookieDomain)
    {
        if (string.IsNullOrWhiteSpace(targetHost) || string.IsNullOrWhiteSpace(cookieDomain))
        {
            return false;
        }

        var host = targetHost.Trim().TrimEnd('.').ToLowerInvariant();
        var domain = cookieDomain.Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.StartsWith(".", StringComparison.Ordinal))
        {
            var bareDomain = domain[1..];
            return string.Equals(host, bareDomain, StringComparison.Ordinal) ||
                   host.EndsWith($".{bareDomain}", StringComparison.Ordinal);
        }

        return string.Equals(host, domain, StringComparison.Ordinal);
    }

    public static string BuildCookieHeader(IEnumerable<WebViewCookieExportItem> cookies)
    {
        return string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }

    public static string BuildCookieDetails(string targetOrigin, IReadOnlyList<WebViewCookieExportItem> cookies)
    {
        var host = Uri.TryCreate(targetOrigin, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : string.Empty;

        var builder = new StringBuilder()
            .AppendLine($"Target Origin: {targetOrigin}")
            .AppendLine($"Target Host: {host}")
            .AppendLine();

        if (cookies.Count == 0)
        {
            return builder
                .Append("No matching cookies found.")
                .ToString();
        }

        for (var index = 0; index < cookies.Count; index++)
        {
            var cookie = cookies[index];
            if (index > 0)
            {
                builder.AppendLine();
            }

            builder
                .AppendLine($"Name: {cookie.Name}")
                .AppendLine($"Value: {cookie.Value}")
                .AppendLine($"Domain: {cookie.Domain}")
                .AppendLine($"Path: {cookie.Path}")
                .AppendLine($"Scope URL: {BuildScopeUrl(targetOrigin, cookie)}");
        }

        return builder.ToString().TrimEnd();
    }

    public static string? ExtractTargetOrigin(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        foreach (var rawLine in input.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Target Origin:", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeOrigin(line["Target Origin:".Length..].Trim());
            }
        }

        var devToolsDomain = ExtractFirstDevToolsDomain(input);
        if (!string.IsNullOrWhiteSpace(devToolsDomain))
        {
            return NormalizeOrigin(devToolsDomain);
        }

        return null;
    }

    public static IReadOnlyList<WebViewCookieImportItem> ParseCookieImportText(string? input, string fallbackTargetOrigin)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        var detailedCookies = ParseDetailedCookieBlocks(input, fallbackTargetOrigin);
        if (detailedCookies.Count > 0)
        {
            return detailedCookies;
        }

        var devToolsRowCookies = ParseDevToolsCookieRows(input);
        if (devToolsRowCookies.Count > 0)
        {
            return devToolsRowCookies;
        }

        var devToolsCookies = ParseDevToolsCookieTable(input, fallbackTargetOrigin);
        if (devToolsCookies.Count > 0)
        {
            return devToolsCookies;
        }

        return ParseCookieHeader(input, fallbackTargetOrigin);
    }

    public static string BuildCookieImportDiagnostics(string? input, string fallbackTargetOrigin)
    {
        var builder = new StringBuilder();
        if (string.IsNullOrWhiteSpace(input))
        {
            return "Cookie import diagnostics\n\nInput is empty.";
        }

        var normalizedInput = input.ReplaceLineEndings("\n");
        var lines = normalizedInput.Split('\n');
        var targetOrigin = NormalizeOrigin(fallbackTargetOrigin) ?? fallbackTargetOrigin;
        var detailedCookies = ParseDetailedCookieBlocks(input, fallbackTargetOrigin);
        var devToolsRowCookies = ParseDevToolsCookieRows(input);
        var devToolsCookies = ParseDevToolsCookieTable(input, fallbackTargetOrigin);
        var headerCookies = ParseCookieHeader(input, fallbackTargetOrigin);

        builder
            .AppendLine("Cookie import diagnostics")
            .AppendLine()
            .AppendLine($"Input lines: {lines.Length}")
            .AppendLine($"Fallback target origin: {targetOrigin}")
            .AppendLine($"Detailed block parser: {detailedCookies.Count} item(s)")
            .AppendLine($"DevTools row parser: {devToolsRowCookies.Count} item(s)")
            .AppendLine($"DevTools table parser: {devToolsCookies.Count} item(s)")
            .AppendLine($"Cookie header parser: {headerCookies.Count} item(s)");

        if (detailedCookies.Count == 0 && devToolsRowCookies.Count == 0 && devToolsCookies.Count == 0 && headerCookies.Count == 0)
        {
            builder
                .AppendLine()
                .AppendLine("No parser matched any cookie items.")
                .AppendLine()
                .AppendLine("First non-empty lines:");

            var nonEmptyLines = lines
                .Select((line, index) => new { Line = line, Index = index })
                .Where(static item => !string.IsNullOrWhiteSpace(item.Line))
                .Take(12)
                .ToList();

            if (nonEmptyLines.Count == 0)
            {
                builder.AppendLine("- (none)");
            }
            else
            {
                foreach (var item in nonEmptyLines)
                {
                    builder.AppendLine($"- L{item.Index + 1}: {item.Line.Trim().Replace('\t', ' ')}");
                }
            }

            builder
                .AppendLine()
                .AppendLine("DevTools row scan:");

            var rowCandidates = lines
                .Select((line, index) => new { Line = line, Index = index })
                .Where(static item => !string.IsNullOrWhiteSpace(item.Line))
                .Take(12)
                .ToList();

            if (rowCandidates.Count == 0)
            {
                builder.AppendLine("- no non-empty rows");
            }
            else
            {
                foreach (var row in rowCandidates)
                {
                    builder.AppendLine($"- row {row.Index + 1}: {AnalyzeDevToolsRow(row.Line)}");
                }
            }

            builder
                .AppendLine()
                .AppendLine("DevTools table scan:");

            var maxCandidateIndex = Math.Min(lines.Length - 4, 11);
            if (maxCandidateIndex < 0)
            {
                builder.AppendLine("- not enough lines for a DevTools cookie block");
            }
            else
            {
                for (var index = 0; index <= maxCandidateIndex; index++)
                {
                    builder.AppendLine($"- candidate at line {index + 1}: {AnalyzeDevToolsCandidate(lines, index)}");
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static WebViewCookieImportPlan BuildImportPlan(
        IReadOnlyList<WebViewCookieImportItem> importedCookies,
        IReadOnlyList<WebViewCookieImportItem> existingCookies)
    {
        var cookiesToApply = importedCookies
            .Where(static cookie => IsValidCookieName(cookie.Name) && !string.IsNullOrWhiteSpace(cookie.Domain))
            .GroupBy(GetCookieKey, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .ToList();

        if (cookiesToApply.Count == 0)
        {
            return new WebViewCookieImportPlan([], [], [], 0, 0, 0, 0);
        }

        var relevantExistingCookies = existingCookies
            .Where(existingCookie => cookiesToApply.Any(
                importedCookie =>
                    string.Equals(importedCookie.Name, existingCookie.Name, StringComparison.Ordinal) &&
                    DomainsOverlap(importedCookie.Domain, existingCookie.Domain)))
            .GroupBy(GetCookieKey, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .ToList();

        var existingByKey = relevantExistingCookies.ToDictionary(GetCookieKey, StringComparer.Ordinal);
        var importedKeys = cookiesToApply
            .Select(GetCookieKey)
            .ToHashSet(StringComparer.Ordinal);
        var cookiesToDelete = relevantExistingCookies
            .Where(existingCookie => !importedKeys.Contains(GetCookieKey(existingCookie)))
            .ToList();

        var addedCount = 0;
        var modifiedCount = 0;
        var unchangedCount = 0;
        var cookiesToReplace = new List<WebViewCookieImportItem>();

        foreach (var importedCookie in cookiesToApply)
        {
            var key = GetCookieKey(importedCookie);
            if (!existingByKey.TryGetValue(key, out var existingCookie))
            {
                addedCount++;
                continue;
            }

            if (CookieContentEquals(existingCookie, importedCookie))
            {
                unchangedCount++;
            }
            else
            {
                modifiedCount++;
                cookiesToReplace.Add(existingCookie);
            }
        }

        return new WebViewCookieImportPlan(
            cookiesToApply,
            cookiesToDelete,
            cookiesToReplace,
            addedCount,
            modifiedCount,
            unchangedCount,
            cookiesToDelete.Count);
    }

    private static string BuildScopeUrl(string targetOrigin, WebViewCookieExportItem cookie)
    {
        if (!Uri.TryCreate(targetOrigin, UriKind.Absolute, out var originUri))
        {
            return cookie.Path;
        }

        var host = cookie.Domain.Trim();
        if (host.StartsWith(".", StringComparison.Ordinal))
        {
            host = host[1..];
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            host = originUri.Host;
        }

        var path = string.IsNullOrWhiteSpace(cookie.Path)
            ? "/"
            : cookie.Path.StartsWith("/", StringComparison.Ordinal)
                ? cookie.Path
                : $"/{cookie.Path}";

        return originUri.IsDefaultPort
            ? $"{originUri.Scheme}://{host}{path}"
            : $"{originUri.Scheme}://{host}:{originUri.Port}{path}";
    }

    private static string GetCookieKey(WebViewCookieImportItem cookie)
    {
        return string.Join("|", cookie.Name, NormalizeCookieDomain(cookie.Domain), NormalizePath(cookie.Path));
    }

    private static bool CookieContentEquals(WebViewCookieImportItem left, WebViewCookieImportItem right)
    {
        return string.Equals(left.Value, right.Value, StringComparison.Ordinal) &&
               left.IsSecure == right.IsSecure &&
               left.IsHttpOnly == right.IsHttpOnly &&
               string.Equals(left.SameSite, right.SameSite, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DomainsOverlap(string leftDomain, string rightDomain)
    {
        var left = NormalizeCookieDomain(leftDomain);
        var right = NormalizeCookieDomain(rightDomain);
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(left, right, StringComparison.Ordinal) ||
               left.EndsWith($".{right}", StringComparison.Ordinal) ||
               right.EndsWith($".{left}", StringComparison.Ordinal);
    }

    private static string NormalizeCookieDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return string.Empty;
        }

        var normalized = domain.Trim().TrimEnd('.').ToLowerInvariant();
        return normalized.StartsWith(".", StringComparison.Ordinal)
            ? normalized[1..]
            : normalized;
    }

    private static IReadOnlyList<WebViewCookieImportItem> ParseDetailedCookieBlocks(string input, string fallbackTargetOrigin)
    {
        var lines = input.ReplaceLineEndings("\n").Split('\n');
        if (!lines.Any(static line => line.TrimStart().StartsWith("Name:", StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        var resolvedTargetOrigin = ExtractTargetOrigin(input) ?? NormalizeOrigin(fallbackTargetOrigin) ?? fallbackTargetOrigin;
        var cookies = new List<WebViewCookieImportItem>();
        string? name = null;
        string? value = null;
        string? domain = null;
        string? path = null;
        string? scopeUrl = null;

        void Flush()
        {
            if (!IsValidCookieName(name) || value is null)
            {
                return;
            }

            var effectiveOrigin = NormalizeOrigin(scopeUrl) ?? NormalizeOrigin(resolvedTargetOrigin) ?? resolvedTargetOrigin;
            Uri.TryCreate(effectiveOrigin, UriKind.Absolute, out var effectiveUri);

            var resolvedDomain = string.IsNullOrWhiteSpace(domain)
                ? effectiveUri?.Host ?? string.Empty
                : domain.Trim();
            var resolvedPath = NormalizePath(path ?? effectiveUri?.AbsolutePath);
            var isSecure = string.Equals(effectiveUri?.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

            cookies.Add(
                new WebViewCookieImportItem(
                    name.Trim(),
                    value.Trim().Trim('"'),
                    resolvedDomain,
                    resolvedPath,
                    isSecure,
                    false,
                    null));
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) ||
                line.StartsWith("Target Origin:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Target Host:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("No matching cookies found.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
            {
                if (name is not null)
                {
                    Flush();
                }

                name = line["Name:".Length..].Trim();
                value = null;
                domain = null;
                path = null;
                scopeUrl = null;
                continue;
            }

            if (line.StartsWith("Value:", StringComparison.OrdinalIgnoreCase))
            {
                value = line["Value:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("Domain:", StringComparison.OrdinalIgnoreCase))
            {
                domain = line["Domain:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("Path:", StringComparison.OrdinalIgnoreCase))
            {
                path = line["Path:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("Scope URL:", StringComparison.OrdinalIgnoreCase))
            {
                scopeUrl = line["Scope URL:".Length..].Trim();
            }
        }

        if (name is not null)
        {
            Flush();
        }

        return cookies;
    }

    private static IReadOnlyList<WebViewCookieImportItem> ParseDevToolsCookieTable(string input, string fallbackTargetOrigin)
    {
        var lines = input.ReplaceLineEndings("\n").Split('\n');
        var cookies = new List<WebViewCookieImportItem>();
        var index = 0;

        while (TryFindDevToolsCookieStart(lines, index, out var startIndex))
        {
            var nextSearchIndex = startIndex + 4;
            TryFindDevToolsCookieStart(lines, nextSearchIndex, out var nextCookieIndex);
            var endIndex = nextCookieIndex >= 0 ? nextCookieIndex : lines.Length;

            var name = lines[startIndex].Trim();
            var value = lines[startIndex + 1].Trim();
            var domain = lines[startIndex + 2].Trim();
            var path = NormalizePath(lines[startIndex + 3].Trim());
            var metadata = lines[(startIndex + 4)..endIndex];

            cookies.Add(
                new WebViewCookieImportItem(
                    name,
                    value,
                    domain,
                    path,
                    GetDevToolsSecure(metadata),
                    GetDevToolsHttpOnly(metadata),
                    GetDevToolsSameSite(metadata)));

            index = endIndex;
        }

        return cookies;
    }

    private static IReadOnlyList<WebViewCookieImportItem> ParseDevToolsCookieRows(string input)
    {
        var lines = input.ReplaceLineEndings("\n").Split('\n');
        var cookies = new List<WebViewCookieImportItem>();

        foreach (var rawLine in lines)
        {
            if (!TryParseDevToolsCookieRow(rawLine, out var cookie))
            {
                continue;
            }

            cookies.Add(cookie);
        }

        return cookies;
    }

    private static IReadOnlyList<WebViewCookieImportItem> ParseCookieHeader(string input, string fallbackTargetOrigin)
    {
        var targetOrigin = NormalizeOrigin(fallbackTargetOrigin) ?? fallbackTargetOrigin;
        Uri.TryCreate(targetOrigin, UriKind.Absolute, out var targetUri);
        if (targetUri is null || string.IsNullOrWhiteSpace(targetUri.Host))
        {
            return [];
        }

        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in input.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var name = segment[..separatorIndex].Trim();
            var value = segment[(separatorIndex + 1)..].Trim();
            if (!IsValidCookieName(name))
            {
                continue;
            }

            cookies[name] = value.Trim('"');
        }

        return
        [
            .. cookies.Select(
                cookie =>
                    new WebViewCookieImportItem(
                        cookie.Key,
                        cookie.Value,
                        targetUri.Host,
                        "/",
                        string.Equals(targetUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase),
                        false,
                        null))
        ];
    }

    private static string? ExtractFirstDevToolsDomain(string input)
    {
        var rowCookies = ParseDevToolsCookieRows(input);
        if (rowCookies.Count > 0)
        {
            var rowDomain = rowCookies[0].Domain.Trim();
            return rowDomain.StartsWith(".", StringComparison.Ordinal) ? rowDomain[1..] : rowDomain;
        }

        var lines = input.ReplaceLineEndings("\n").Split('\n');
        if (!TryFindDevToolsCookieStart(lines, 0, out var startIndex))
        {
            return null;
        }

        var domain = lines[startIndex + 2].Trim();
        return domain.StartsWith(".", StringComparison.Ordinal) ? domain[1..] : domain;
    }

    private static bool TryFindDevToolsCookieStart(string[] lines, int searchStartIndex, out int startIndex)
    {
        for (var index = Math.Max(searchStartIndex, 0); index <= lines.Length - 4; index++)
        {
            if (!IsDevToolsCookieStart(lines, index))
            {
                continue;
            }

            startIndex = index;
            return true;
        }

        startIndex = -1;
        return false;
    }

    private static bool IsDevToolsCookieStart(string[] lines, int index)
    {
        if (index < 0 || index > lines.Length - 4)
        {
            return false;
        }

        var name = lines[index].Trim();
        var value = lines[index + 1].Trim();
        var domain = lines[index + 2].Trim();
        var path = lines[index + 3].Trim();
        return IsValidCookieName(name) &&
               !string.IsNullOrEmpty(value) &&
               LooksLikeCookieDomain(domain) &&
               LooksLikeCookiePath(path);
    }

    private static string AnalyzeDevToolsCandidate(string[] lines, int index)
    {
        if (index < 0 || index > lines.Length - 4)
        {
            return "out of range";
        }

        var name = lines[index].Trim();
        var value = lines[index + 1].Trim();
        var domain = lines[index + 2].Trim();
        var path = lines[index + 3].Trim();
        if (!IsValidCookieName(name))
        {
            return "name line is empty";
        }

        if (string.IsNullOrEmpty(value))
        {
            return "value line is empty";
        }

        if (!LooksLikeCookieDomain(domain))
        {
            return $"domain line is not a host: '{domain}'";
        }

        if (!LooksLikeCookiePath(path))
        {
            return $"path line does not start with '/': '{path}'";
        }

        return "looks like a valid DevTools cookie start";
    }

    private static bool TryParseDevToolsCookieRow(string rawLine, out WebViewCookieImportItem cookie)
    {
        cookie = default!;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        var columns = rawLine.Split('\t');
        if (columns.Length < 4)
        {
            return false;
        }

        var name = columns[0].Trim();
        var value = columns[1].Trim();
        var domain = columns[2].Trim();
        var path = columns[3].Trim();
        if (!IsValidCookieName(name) ||
            string.IsNullOrEmpty(value) ||
            !LooksLikeCookieDomain(domain) ||
            !LooksLikeCookiePath(path))
        {
            return false;
        }

        var metadata = columns.Length > 4
            ? columns[4..]
            : [];

        cookie = new WebViewCookieImportItem(
            name,
            value,
            domain,
            NormalizePath(path),
            GetDevToolsSecure(metadata),
            GetDevToolsHttpOnly(metadata),
            GetDevToolsSameSite(metadata));
        return true;
    }

    private static string AnalyzeDevToolsRow(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return "empty row";
        }

        var columns = rawLine.Split('\t');
        if (columns.Length < 4)
        {
            return $"not tabular: {columns.Length} column(s)";
        }

        var name = columns[0].Trim();
        var value = columns[1].Trim();
        var domain = columns[2].Trim();
        var path = columns[3].Trim();
        if (!IsValidCookieName(name))
        {
            return "name column is empty";
        }

        if (string.IsNullOrEmpty(value))
        {
            return "value column is empty";
        }

        if (!LooksLikeCookieDomain(domain))
        {
            return $"domain column is not a host: '{domain}'";
        }

        if (!LooksLikeCookiePath(path))
        {
            return $"path column does not start with '/': '{path}'";
        }

        return $"looks like a valid DevTools row with {columns.Length} column(s)";
    }

    private static bool LooksLikeCookieDomain(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var domain = value.Trim();
        if (domain.StartsWith(".", StringComparison.Ordinal))
        {
            domain = domain[1..];
        }

        if (string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        return Uri.CheckHostName(domain) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;
    }

    private static bool LooksLikeCookiePath(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Trim().StartsWith("/", StringComparison.Ordinal);
    }

    private static bool GetDevToolsHttpOnly(string[] metadata)
    {
        var markers = GetDevToolsBooleanMarkers(metadata);
        return markers.Count > 0 && markers[0];
    }

    private static bool GetDevToolsSecure(string[] metadata)
    {
        var markers = GetDevToolsBooleanMarkers(metadata);
        return markers.Count > 1 && markers[1];
    }

    private static string? GetDevToolsSameSite(string[] metadata)
    {
        foreach (var item in GetMeaningfulDevToolsMetadata(metadata))
        {
            if (item is "None" or "Lax" or "Strict")
            {
                return item;
            }
        }

        return null;
    }

    private static bool IsCheckmark(string? value)
    {
        return string.Equals(value?.Trim(), "✓", StringComparison.Ordinal);
    }

    private static List<bool> GetDevToolsBooleanMarkers(string[] metadata)
    {
        return GetMeaningfulDevToolsMetadata(metadata)
            .Where(IsDevToolsCheckmark)
            .Select(static _ => true)
            .ToList();
    }

    private static IEnumerable<string> GetMeaningfulDevToolsMetadata(string[] metadata)
    {
        foreach (var raw in metadata)
        {
            var value = raw.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (LooksLikeDevToolsExpiry(value) ||
                LooksLikeDevToolsSize(value) ||
                LooksLikeDevToolsPriority(value))
            {
                continue;
            }

            yield return value;
        }
    }

    private static bool IsDevToolsCheckmark(string? value)
    {
        var normalized = value?.Trim();
        return IsCheckmark(normalized) ||
               normalized is "✓" or "✔" or "true" or "True";
    }

    private static bool LooksLikeDevToolsExpiry(string value)
    {
        return string.Equals(value, "Session", StringComparison.OrdinalIgnoreCase) ||
               value.Contains('T', StringComparison.Ordinal) ||
               value.Contains("GMT", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDevToolsSize(string value)
    {
        return int.TryParse(value, out _);
    }

    private static bool LooksLikeDevToolsPriority(string value)
    {
        return value is "Low" or "Medium" or "High";
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        return path.StartsWith("/", StringComparison.Ordinal)
            ? path
            : $"/{path}";
    }

    private static string? NormalizeOrigin(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal) && trimmed.StartsWith(".", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                trimmed = $"https:{trimmed}";
            }
            else
            {
                trimmed = $"https://{trimmed}";
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out uri))
            {
                return null;
            }
        }

        return uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    private static bool IsValidCookieName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name);
    }
}
