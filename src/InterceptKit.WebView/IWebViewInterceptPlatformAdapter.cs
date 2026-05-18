namespace InterceptKit.WebView;

using InterceptKit.Core;

public interface IWebViewInterceptPlatformAdapter
{
    event EventHandler<string>? RequestPaused;

    Task AttachAsync(object platformView, CancellationToken cancellationToken = default);

    Task DisableAsync();

    Task ForwardAsync(
        PausedWebViewRequestContext context,
        EditableInterceptedRequest? editedRequest,
        CancellationToken cancellationToken = default);

    Task DropAsync(
        PausedWebViewRequestContext context,
        CancellationToken cancellationToken = default);
}
