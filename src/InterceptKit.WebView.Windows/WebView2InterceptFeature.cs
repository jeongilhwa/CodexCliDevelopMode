namespace InterceptKit.WebView.Windows;

using InterceptKit.Core;

public sealed record WebView2InterceptFeature(
    string Name,
    InterceptCapabilities Capabilities)
{
    public static WebView2InterceptFeature Default { get; } = new(
        "WebView2",
        InterceptCapabilities.EditHeaders |
        InterceptCapabilities.EditBody |
        InterceptCapabilities.CaptureResponse);
}
