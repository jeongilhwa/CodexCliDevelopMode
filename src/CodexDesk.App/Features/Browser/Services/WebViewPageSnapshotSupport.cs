namespace CodexDesk.Services;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record WebViewPageAssetSnapshot(
    string Kind,
    string Url,
    string Content,
    string Error);

internal sealed record WebViewPageSnapshotPayload(
    string Url,
    string Title,
    string Html,
    IReadOnlyList<WebViewPageAssetSnapshot> Styles,
    IReadOnlyList<WebViewPageAssetSnapshot> Scripts);

internal sealed record WebViewPageSnapshotSaveResult(
    string DirectoryPath,
    string ManifestPath,
    string PromptPath,
    string Prompt);

internal static class WebViewPageSnapshotScriptBuilder
{
    public static string Build()
    {
        return """
(() => {
    const normalizeError = (error) => error?.message ?? String(error ?? '');

    const readTextFromUrl = (url) => {
        try {
            const request = new XMLHttpRequest();
            request.open('GET', url, false);
            request.withCredentials = true;
            request.send();

            if (request.status < 200 || request.status >= 300) {
                return { content: '', error: `HTTP ${request.status} ${request.statusText}`.trim() };
            }

            return { content: request.responseText ?? '', error: '' };
        } catch (error) {
            return { content: '', error: normalizeError(error) };
        }
    };

    const styles = [];
    document.querySelectorAll('style').forEach((node) => {
        styles.push({
            kind: 'inline',
            url: '',
            content: node.textContent ?? '',
            error: ''
        });
    });

    for (const node of document.querySelectorAll('link[rel="stylesheet"]')) {
        const url = node.href || node.getAttribute('href') || '';
        if (!url) {
            continue;
        }

        const result = readTextFromUrl(url);
        styles.push({
            kind: 'external',
            url,
            content: result.content,
            error: result.error
        });
    }

    const scripts = [];
    for (const node of document.querySelectorAll('script')) {
        const url = node.src || node.getAttribute('src') || '';
        if (url) {
            const result = readTextFromUrl(url);
            scripts.push({
                kind: 'external',
                url,
                content: result.content,
                error: result.error
            });
            continue;
        }

        scripts.push({
            kind: 'inline',
            url: '',
            content: node.textContent ?? '',
            error: ''
        });
    }

    return JSON.stringify({
        url: location.href,
        title: document.title ?? '',
        html: document.documentElement.outerHTML,
        styles,
        scripts
    });
})();
""";
    }
}

internal static partial class WebViewPageSnapshotWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static async Task<WebViewPageSnapshotSaveResult> SaveAsync(
        WebViewPageSnapshotPayload snapshot,
        string baseDirectory,
        CancellationToken cancellationToken = default)
    {
        var root = ResolveSnapshotRoot(baseDirectory);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(root, timestamp);
        var stylesDirectory = Path.Combine(directory, "styles");
        var scriptsDirectory = Path.Combine(directory, "scripts");

        Directory.CreateDirectory(stylesDirectory);
        Directory.CreateDirectory(scriptsDirectory);

        var pagePath = Path.Combine(directory, "page.html");
        await File.WriteAllTextAsync(pagePath, snapshot.Html ?? string.Empty, Utf8NoBom, cancellationToken);

        var styleFiles = await WriteAssetsAsync(stylesDirectory, "css", snapshot.Styles, cancellationToken);
        var scriptFiles = await WriteAssetsAsync(scriptsDirectory, "js", snapshot.Scripts, cancellationToken);
        var prompt = BuildPrompt(snapshot, directory, styleFiles, scriptFiles);
        var promptPath = Path.Combine(directory, "analysis-prompt.md");
        var manifestPath = Path.Combine(directory, "manifest.json");

        var manifest = new
        {
            capturedAt = DateTimeOffset.Now,
            snapshot.Url,

            snapshot.Title,
            page = "page.html",
            styles = styleFiles,
            scripts = scriptFiles
        };

        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Utf8NoBom, cancellationToken);
        await File.WriteAllTextAsync(promptPath, prompt, Utf8NoBom, cancellationToken);

        return new WebViewPageSnapshotSaveResult(directory, manifestPath, promptPath, prompt);
    }

    private static string ResolveSnapshotRoot(string baseDirectory)
    {
        var root = TryFindWorkspaceRoot(baseDirectory) ??
            CodexDeskDataPathResolver.TryFindProjectRoot(baseDirectory) ??
            Path.GetFullPath(baseDirectory);

        return Path.Combine(root, ".tmp-build", "web-snapshots");
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

    private static async Task<IReadOnlyList<object>> WriteAssetsAsync(
        string directory,
        string extension,
        IReadOnlyList<WebViewPageAssetSnapshot>? assets,
        CancellationToken cancellationToken)
    {
        var written = new List<object>();
        if (assets is null)
        {
            return written;
        }

        for (var index = 0; index < assets.Count; index++)
        {
            var asset = assets[index];
            if (asset is null)
            {
                continue;
            }

            var fileName = BuildAssetFileName(index + 1, asset, extension);
            var path = Path.Combine(directory, fileName);
            var content = string.IsNullOrEmpty(asset.Content)
                ? $"/* unavailable: {asset.Error} */"
                : asset.Content;

            await File.WriteAllTextAsync(path, content, Utf8NoBom, cancellationToken);
            written.Add(new
            {
                file = Path.Combine(Path.GetFileName(directory), fileName).Replace('\\', '/'),
                asset.Kind,
                asset.Url,
                length = asset.Content?.Length ?? 0,
                asset.Error
            });
        }

        return written;
    }

    private static string BuildAssetFileName(int index, WebViewPageAssetSnapshot asset, string extension)
    {
        var indexPrefix = index.ToString("D3", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(asset.Url))
        {
            return $"{indexPrefix}-{asset.Kind}-{BuildShortHash(asset.Url)}.{extension}";
        }

        var name = "inline";
        if (string.IsNullOrWhiteSpace(name))
        {
            name = asset.Kind;
        }

        name = UnsafeFileNamePattern().Replace(name, "-").Trim('-', '.');
        if (string.IsNullOrWhiteSpace(name))
        {
            name = asset.Kind;
        }

        return $"{indexPrefix}-{name}.{extension}";
    }

    private static string BuildShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }

    private static string BuildPrompt(
        WebViewPageSnapshotPayload snapshot,
        string directory,
        IReadOnlyList<object> styleFiles,
        IReadOnlyList<object> scriptFiles)
    {
        var builder = new StringBuilder();
        builder.AppendLine("현재 WebView 페이지 스냅샷을 분석해줘.");
        builder.AppendLine();
        builder.AppendLine($"URL: {snapshot.Url}");
        builder.AppendLine($"Title: {snapshot.Title}");
        builder.AppendLine($"Snapshot folder: {Path.GetFileName(directory)}");
        builder.AppendLine($"Snapshot path: {directory}");
        builder.AppendLine();
        builder.AppendLine("읽을 파일:");
        builder.AppendLine("- page.html");
        builder.AppendLine("- manifest.json");
        builder.AppendLine($"- styles/*.css ({styleFiles.Count.ToString(CultureInfo.InvariantCulture)} files)");
        builder.AppendLine($"- scripts/*.js ({scriptFiles.Count.ToString(CultureInfo.InvariantCulture)} files)");
        builder.AppendLine();
        builder.AppendLine("분석할 때 다음을 중점적으로 봐줘:");
        builder.AppendLine("1. 화면 구조와 주요 DOM 요소");
        builder.AppendLine("2. 버튼/입력/테이블 등 자동화 가능한 셀렉터");
        builder.AppendLine("3. API 호출이나 세션 연장 같은 동작과 관련된 JS 흐름");
        builder.AppendLine("4. 보안상 저장하면 안 되는 민감정보가 포함됐는지");

        return builder.ToString();
    }

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex UnsafeFileNamePattern();
}
