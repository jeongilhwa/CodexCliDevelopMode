namespace InterceptKit.WebView;

public interface IWebViewInterceptController
{
    WebViewInterceptOptions Options { get; }

    Task AttachAsync(object platformView, CancellationToken cancellationToken = default);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
