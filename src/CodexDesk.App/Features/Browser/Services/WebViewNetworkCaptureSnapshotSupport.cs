namespace CodexDesk.Services;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record WebViewNetworkCaptureSaveResult(
    string DirectoryPath,
    string ManifestPath,
    string PromptPath,
    string Prompt);

internal static partial class WebViewNetworkCaptureSnapshotWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static async Task<WebViewNetworkCaptureSaveResult> SaveAsync(
        IReadOnlyList<WebViewNetworkCaptureEntrySnapshot> entries,
        string baseDirectory,
        string scope,
        string detailMode,
        CancellationToken cancellationToken = default)
    {
        var root = ResolveSnapshotRoot(baseDirectory);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(root, timestamp);
        var requestsDirectory = Path.Combine(directory, "requests");
        Directory.CreateDirectory(requestsDirectory);

        var requestFiles = new List<object>();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var requestDirectoryName = BuildRequestDirectoryName(index + 1, entry);
            var requestDirectory = Path.Combine(requestsDirectory, requestDirectoryName);
            Directory.CreateDirectory(requestDirectory);

            await File.WriteAllTextAsync(Path.Combine(requestDirectory, "summary.txt"), entry.Summary, Utf8NoBom, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(requestDirectory, "request.txt"), entry.RequestDetails, Utf8NoBom, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(requestDirectory, "response.txt"), entry.ResponseDetails, Utf8NoBom, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(requestDirectory, "request-headers.txt"), entry.RequestHeaders, Utf8NoBom, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(requestDirectory, "request-body.txt"), entry.RequestBody, Utf8NoBom, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(requestDirectory, "response-headers.txt"), entry.ResponseHeaders, Utf8NoBom, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(requestDirectory, "response-body.txt"), entry.ResponseBody, Utf8NoBom, cancellationToken);

            requestFiles.Add(new
            {
                folder = Path.Combine("requests", requestDirectoryName).Replace('\\', '/'),
                entry.Sequence,
                entry.Method,
                entry.Status,
                entry.Host,
                entry.Path,
                entry.Time,
                entry.Size
            });
        }

        var summaryPath = Path.Combine(directory, "summary.md");
        var manifestPath = Path.Combine(directory, "manifest.json");
        var promptPath = Path.Combine(directory, "analysis-prompt.md");
        var prompt = BuildPrompt(directory, entries, scope, detailMode);

        await File.WriteAllTextAsync(summaryPath, BuildSummary(entries, scope, detailMode), Utf8NoBom, cancellationToken);
        await File.WriteAllTextAsync(promptPath, prompt, Utf8NoBom, cancellationToken);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
        {
            capturedAt = DateTimeOffset.Now,
            scope,
            detailMode,
            count = entries.Count,
            requests = requestFiles
        }, JsonOptions), Utf8NoBom, cancellationToken);

        return new WebViewNetworkCaptureSaveResult(directory, manifestPath, promptPath, prompt);
    }

    private static string ResolveSnapshotRoot(string baseDirectory)
    {
        var root = TryFindWorkspaceRoot(baseDirectory) ??
            CodexDeskDataPathResolver.TryFindProjectRoot(baseDirectory) ??
            Path.GetFullPath(baseDirectory);

        return Path.Combine(root, ".tmp-build", "network-captures");
    }

    private static string? TryFindWorkspaceRoot(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, "CodexDevelopMode.sln")) ||
                File.Exists(Path.Combine(current.FullName, "CodexDevelopMode.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string BuildRequestDirectoryName(int index, WebViewNetworkCaptureEntrySnapshot entry)
    {
        var method = SafePathPart(entry.Method, fallback: "request", maxLength: 12);
        var host = SafePathPart(entry.Host, fallback: "host", maxLength: 36);
        var hash = BuildShortHash($"{entry.Method}|{entry.Host}|{entry.Path}|{entry.Sequence}|{entry.Summary}");
        return $"{index.ToString("D3", CultureInfo.InvariantCulture)}-{method}-{host}-{hash}";
    }

    private static string SafePathPart(string? value, string fallback, int maxLength)
    {
        var normalized = UnsafeFileNamePattern().Replace(value ?? string.Empty, "-").Trim('-', '.');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = fallback;
        }

        return normalized.Length <= maxLength ? normalized : normalized[..maxLength].Trim('-', '.');
    }

    private static string BuildShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }

    private static string BuildSummary(
        IReadOnlyList<WebViewNetworkCaptureEntrySnapshot> entries,
        string scope,
        string detailMode)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# WebView Network Capture");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Scope: {scope}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Detail: {detailMode}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Requests: {entries.Count}");
        builder.AppendLine();

        foreach (var entry in entries)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"- #{entry.Sequence} {entry.Method} {entry.Status} {entry.Time} {entry.Size} {entry.Host}{entry.Path}");
        }

        return builder.ToString();
    }

    private static string BuildPrompt(
        string directory,
        IReadOnlyList<WebViewNetworkCaptureEntrySnapshot> entries,
        string scope,
        string detailMode)
    {
        var builder = new StringBuilder();
        builder.AppendLine("현재 WebView 네트워크 캡쳐 파일을 분석해줘.");
        builder.AppendLine();
        builder.AppendLine($"Network capture folder: {Path.GetFileName(directory)}");
        builder.AppendLine($"Network capture path: {directory}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Request count: {entries.Count}");
        builder.AppendLine($"Scope: {scope}");
        builder.AppendLine($"Detail mode: {detailMode}");
        builder.AppendLine();
        builder.AppendLine("읽을 파일:");
        builder.AppendLine("- manifest.json");
        builder.AppendLine("- summary.md");
        builder.AppendLine("- requests/*/request.txt");
        builder.AppendLine("- requests/*/response.txt");
        builder.AppendLine("- requests/*/request-headers.txt");
        builder.AppendLine("- requests/*/request-body.txt");
        builder.AppendLine("- requests/*/response-headers.txt");
        builder.AppendLine("- requests/*/response-body.txt");
        builder.AppendLine();
        builder.AppendLine("분석할 때 다음을 중점적으로 봐줘:");
        builder.AppendLine("1. 실패/지연/중복 호출/인증 또는 CORS 의심 지점");
        builder.AppendLine("2. request header/body와 response header/body의 실제 흐름");
        builder.AppendLine("3. 세션 연장, 로그인, API 호출 순서와 자동화 가능성");
        builder.AppendLine("4. 민감정보가 파일에 포함됐는지와 마스킹 필요 여부");

        return builder.ToString();
    }

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex UnsafeFileNamePattern();
}

internal sealed record WebViewNetworkCaptureEntrySnapshot(
    int Sequence,
    string Summary,
    string Method,
    string Status,
    string Host,
    string Path,
    string Size,
    string Time,
    string RequestDetails,
    string ResponseDetails,
    string RequestHeaders,
    string RequestBody,
    string ResponseHeaders,
    string ResponseBody);
