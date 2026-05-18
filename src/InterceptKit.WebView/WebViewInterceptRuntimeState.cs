namespace InterceptKit.WebView;

using InterceptKit.Core;

public sealed class WebViewInterceptRuntimeState
{
    public event EventHandler? Changed;

    public bool IsEnabled { get; private set; }

    public bool IsAttached { get; private set; }

    public int PausedRequestCount { get; private set; }

    public string? LastPausedRequestUrl { get; private set; }

    public string? LastError { get; private set; }

    public string StatusText { get; private set; } = "Intercept OFF.";

    public void MarkDisabled()
    {
        IsEnabled = false;
        IsAttached = false;
        PausedRequestCount = 0;
        LastPausedRequestUrl = null;
        LastError = null;
        StatusText = "Intercept OFF.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkWaitingForAttach()
    {
        IsEnabled = true;
        IsAttached = false;
        PausedRequestCount = 0;
        LastPausedRequestUrl = null;
        LastError = null;
        StatusText = "Intercept ON. Waiting for WebView2 attach.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkAttached()
    {
        IsEnabled = true;
        IsAttached = true;
        LastError = null;
        StatusText = "WebView2 attached. Waiting for requests.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkPaused(InterceptedRequest request)
    {
        PausedRequestCount++;
        LastPausedRequestUrl = request.Url;
        LastError = null;
        StatusText = $"Paused {request.Method} {request.Url}";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkError(string message)
    {
        LastError = message;
        StatusText = message;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
