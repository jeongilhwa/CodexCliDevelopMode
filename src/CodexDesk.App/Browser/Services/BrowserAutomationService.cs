using System.Text.Json;

namespace CodexDesk.Services;

/// <summary>Validated browser actions available to the in-app Codex conversation.</summary>
public interface IBrowserAutomationService
{
    Task<BrowserToolResult> ExecuteAsync(string tool, JsonElement arguments, CancellationToken cancellationToken = default);
}

public sealed record BrowserToolResult(bool Success, string Text, int? CaptureCount = null);

internal sealed record BrowserAutomationAction(
    string Tool,
    string? Url = null,
    string? Selector = null,
    string? Text = null,
    int Pixels = 0,
    int AfterSequence = 0,
    int Limit = 50);

/// <summary>Validates model supplied arguments before they reach the native WebView2 host.</summary>
public sealed class BrowserAutomationService : IBrowserAutomationService
{
    private Func<BrowserAutomationAction, CancellationToken, Task<BrowserToolResult>>? _host;
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    internal void Attach(Func<BrowserAutomationAction, CancellationToken, Task<BrowserToolResult>> host) =>
        Interlocked.Exchange(ref _host, host ?? throw new ArgumentNullException(nameof(host)));

    public async Task<BrowserToolResult> ExecuteAsync(string tool, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        BrowserAutomationAction action;
        try
        {
            action = CreateAction(tool, arguments);
        }
        catch (ArgumentException exception)
        {
            return new BrowserToolResult(false, exception.Message);
        }

        var host = Volatile.Read(ref _host);
        if (host is null)
        {
            return new BrowserToolResult(false, "The embedded browser is not ready.");
        }

        var lockAcquired = false;
        try
        {
            await _executionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockAcquired = true;
            return await host(action, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new BrowserToolResult(false, "Browser action was cancelled.");
        }
        catch (Exception exception)
        {
            return new BrowserToolResult(false, $"Browser action failed: {exception.Message}");
        }
        finally
        {
            if (lockAcquired) _executionLock.Release();
        }
    }

    private static BrowserAutomationAction CreateAction(string tool, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Browser tool arguments must be an object.");
        }

        switch (tool)
        {
            case "browser_navigate":
            {
                var url = RequiredString(arguments, "url", 4096);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps ||
                    string.IsNullOrWhiteSpace(uri.Host))
                {
                    throw new ArgumentException("The URL must be an absolute HTTP or HTTPS address.");
                }

                return new BrowserAutomationAction(tool, Url: uri.AbsoluteUri);
            }
            case "browser_search":
            {
                var query = RequiredString(arguments, "query", 512);
                var engine = OptionalString(arguments, "engine", 16)?.ToLowerInvariant() ?? "naver";
                var url = engine switch
                {
                    "naver" => $"https://search.naver.com/search.naver?query={Uri.EscapeDataString(query)}",
                    "google" => $"https://www.google.com/search?q={Uri.EscapeDataString(query)}",
                    _ => throw new ArgumentException("The search engine must be 'naver' or 'google'.")
                };
                return new BrowserAutomationAction(tool, Url: url);
            }
            case "browser_read_page":
                return new BrowserAutomationAction(tool);
            case "network_capture_start":
            case "network_capture_stop":
                return new BrowserAutomationAction(tool);
            case "network_capture_list":
            {
                var afterSequence = OptionalInt32(arguments, "afterSequence", 0);
                var limit = OptionalInt32(arguments, "limit", 50);
                if (afterSequence < 0 || limit is < 1 or > 100)
                {
                    throw new ArgumentException("Capture pagination requires afterSequence >= 0 and limit from 1 to 100.");
                }

                return new BrowserAutomationAction(tool, AfterSequence: afterSequence, Limit: limit);
            }
            case "browser_click":
                return new BrowserAutomationAction(tool, Selector: RequiredString(arguments, "selector", 512));
            case "browser_type":
                return new BrowserAutomationAction(tool,
                    Selector: RequiredString(arguments, "selector", 512),
                    Text: RequiredString(arguments, "text", 10000, allowEmpty: true, trim: false));
            case "browser_scroll":
            {
                if (!arguments.TryGetProperty("pixels", out var pixelsElement) ||
                    pixelsElement.ValueKind != JsonValueKind.Number ||
                    !pixelsElement.TryGetInt32(out var pixels) || pixels is < -5000 or > 5000)
                {
                    throw new ArgumentException("Scroll pixels must be an integer from -5000 to 5000.");
                }

                return new BrowserAutomationAction(tool, Pixels: pixels);
            }
            default:
                throw new ArgumentException($"Unsupported browser tool: {tool}");
        }
    }

    private static string RequiredString(JsonElement arguments, string name, int maxLength, bool allowEmpty = false, bool trim = true)
    {
        var value = OptionalString(arguments, name, maxLength, trim);
        if (value is null || !allowEmpty && value.Length == 0)
        {
            throw new ArgumentException($"'{name}' is required.");
        }

        return value;
    }

    private static int OptionalInt32(JsonElement arguments, string name, int defaultValue)
    {
        if (!arguments.TryGetProperty(name, out var element)) return defaultValue;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            throw new ArgumentException($"'{name}' must be an integer.");
        }

        return value;
    }

    private static string? OptionalString(JsonElement arguments, string name, int maxLength, bool trim = true)
    {
        if (!arguments.TryGetProperty(name, out var element)) return null;
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"'{name}' must be text.");
        }

        var value = element.GetString() ?? string.Empty;
        if (trim) value = value.Trim();
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"'{name}' exceeds {maxLength} characters.");
        }

        return value;
    }
}
