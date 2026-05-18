namespace InterceptKit.WebView.Windows;

using InterceptKit.Core;
using InterceptKit.WebView;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

public sealed class WebView2InterceptAdapter : IWebViewInterceptPlatformAdapter
{
    private static readonly MethodInfo HandleRequestPausedMethod = typeof(WebView2InterceptAdapter)
        .GetMethod(nameof(HandleRequestPaused), BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Unable to resolve WebView2 intercept event handler.");

    private object? _coreWebView2;
    private object? _requestPausedReceiver;
    private EventInfo? _requestPausedEventInfo;
    private Delegate? _requestPausedHandler;

    public event EventHandler<string>? RequestPaused;

    public async Task AttachAsync(object coreWebView2Instance, CancellationToken cancellationToken = default)
    {
        if (coreWebView2Instance is null)
        {
            throw new InvalidOperationException("The provided WebView2 core instance was null.");
        }

        if (ReferenceEquals(_coreWebView2, coreWebView2Instance))
        {
            return;
        }

        await DisableAsync();

        _coreWebView2 = coreWebView2Instance;
        _requestPausedReceiver = InvokeRequiredMethod(
            coreWebView2Instance,
            "GetDevToolsProtocolEventReceiver",
            ["Fetch.requestPaused"]);
        _requestPausedEventInfo = _requestPausedReceiver.GetType().GetEvent("DevToolsProtocolEventReceived")
            ?? throw new InvalidOperationException("WebView2 request receiver did not expose DevToolsProtocolEventReceived.");
        _requestPausedHandler = BuildRequestPausedHandler(_requestPausedEventInfo.EventHandlerType
            ?? throw new InvalidOperationException("WebView2 request receiver event did not expose a handler type."));
        _requestPausedEventInfo.AddEventHandler(_requestPausedReceiver, _requestPausedHandler);

        var args = JsonSerializer.Serialize(new
        {
            patterns = new[]
            {
                new
                {
                    urlPattern = "*",
                    requestStage = "Request"
                }
            }
        });

        await CallDevToolsProtocolMethodAsync(coreWebView2Instance, "Fetch.enable", args);
    }

    public Task<PausedWebViewRequestContext> PauseAsync(
        InterceptedRequest request,
        InterceptCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PausedWebViewRequestContext(Guid.NewGuid(), capabilities));
    }

    public Task ForwardAsync(
        PausedWebViewRequestContext context,
        EditableInterceptedRequest? editedRequest,
        CancellationToken cancellationToken = default)
    {
        if (_coreWebView2 is null || string.IsNullOrWhiteSpace(context.PlatformRequestId))
        {
            throw new InvalidOperationException("WebView2 adapter is not attached to an active paused request.");
        }

        return CallDevToolsProtocolMethodAsync(
            _coreWebView2,
            "Fetch.continueRequest",
            WebView2InterceptPayloadBuilder.BuildContinueRequestPayload(
                context.PlatformRequestId,
                editedRequest,
                interceptResponse: false));
    }

    public Task DropAsync(
        PausedWebViewRequestContext context,
        CancellationToken cancellationToken = default)
    {
        if (_coreWebView2 is null || string.IsNullOrWhiteSpace(context.PlatformRequestId))
        {
            throw new InvalidOperationException("WebView2 adapter is not attached to an active paused request.");
        }

        return CallDevToolsProtocolMethodAsync(
            _coreWebView2,
            "Fetch.failRequest",
            WebView2InterceptPayloadBuilder.BuildFailRequestPayload(context.PlatformRequestId));
    }

    public async Task DisableAsync()
    {
        if (_coreWebView2 is not null)
        {
            try
            {
                await CallDevToolsProtocolMethodAsync(_coreWebView2, "Fetch.disable", "{}");
            }
            catch
            {
            }
        }

        if (_requestPausedReceiver is not null &&
            _requestPausedEventInfo is not null &&
            _requestPausedHandler is not null)
        {
            _requestPausedEventInfo.RemoveEventHandler(_requestPausedReceiver, _requestPausedHandler);
        }

        _requestPausedReceiver = null;
        _requestPausedEventInfo = null;
        _requestPausedHandler = null;
        _coreWebView2 = null;
    }

    private void HandleRequestPaused(object? args)
    {
        var payload = args?
            .GetType()
            .GetProperty("ParameterObjectAsJson", BindingFlags.Instance | BindingFlags.Public)?
            .GetValue(args) as string;

        if (!string.IsNullOrWhiteSpace(payload))
        {
            RequestPaused?.Invoke(this, payload);
        }
    }

    // Reflection keeps the reusable library from forcing a second WebView2 package
    // into a MAUI app that already carries its own WebView2 runtime chain.
    private Delegate BuildRequestPausedHandler(Type handlerType)
    {
        var invokeMethod = handlerType.GetMethod("Invoke")
            ?? throw new InvalidOperationException("WebView2 event handler type did not expose Invoke.");
        var parameters = invokeMethod.GetParameters();
        if (parameters.Length != 2)
        {
            throw new InvalidOperationException("WebView2 event handler signature was unexpected.");
        }

        var senderParameter = Expression.Parameter(parameters[0].ParameterType, "sender");
        var argsParameter = Expression.Parameter(parameters[1].ParameterType, "args");
        var body = Expression.Call(
            Expression.Constant(this),
            HandleRequestPausedMethod,
            Expression.Convert(argsParameter, typeof(object)));

        return Expression.Lambda(handlerType, body, senderParameter, argsParameter).Compile();
    }

    private static async Task CallDevToolsProtocolMethodAsync(object coreWebView2, string methodName, string argsJson)
    {
        var task = InvokeRequiredMethod(coreWebView2, "CallDevToolsProtocolMethodAsync", [methodName, argsJson]) as Task
            ?? throw new InvalidOperationException("WebView2 CallDevToolsProtocolMethodAsync did not return a task.");
        await task.ConfigureAwait(false);
    }

    private static object InvokeRequiredMethod(object target, string methodName, object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, args.Select(static arg => arg?.GetType() ?? typeof(object)).ToArray());
        if (method is null)
        {
            throw new InvalidOperationException($"WebView2 object did not expose method {methodName}.");
        }

        return method.Invoke(target, args)
            ?? throw new InvalidOperationException($"WebView2 method {methodName} returned null.");
    }
}
