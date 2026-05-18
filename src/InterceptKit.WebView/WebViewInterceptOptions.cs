namespace InterceptKit.WebView;

public sealed record WebViewInterceptOptions
{
    private static readonly HashSet<string> DefaultResourceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Document",
        "Fetch",
        "XHR"
    };

    public bool InterceptRequests { get; init; } = true;

    public bool CaptureResponses { get; init; } = true;

    public int MaxCapturedBodyLength { get; init; } = 32 * 1024;

    // Default scope stays narrow enough to keep interactive browsing responsive.
    public ISet<string> IncludedResourceTypes { get; init; } = new HashSet<string>(DefaultResourceTypes, StringComparer.OrdinalIgnoreCase);
}
