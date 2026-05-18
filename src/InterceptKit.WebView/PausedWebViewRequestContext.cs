namespace InterceptKit.WebView;

using InterceptKit.Core;

public sealed record PausedWebViewRequestContext(
    Guid SessionId,
    InterceptCapabilities Capabilities,
    string? PlatformRequestId = null);
