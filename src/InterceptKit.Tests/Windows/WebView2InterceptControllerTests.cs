namespace InterceptKit.Tests.Windows;

using FluentAssertions;
using InterceptKit.Core;
using InterceptKit.WebView;
using InterceptKit.WebView.Windows;

[TestClass]
public sealed class WebView2InterceptControllerTests
{
    [TestMethod]
    public async Task AttachAsync_DelegatesToPlatformAdapter()
    {
        var store = new InterceptSessionStore();
        var adapter = new FakePlatformAdapter();
        var runtimeState = new InterceptKit.WebView.WebViewInterceptRuntimeState();
        var controller = new WebView2InterceptController(store, adapter, runtimeState: runtimeState);
        var core = new object();

        await controller.SetEnabledAsync(true);
        await controller.AttachAsync(core);

        adapter.AttachedInstance.Should().BeSameAs(core);
        controller.IsEnabled.Should().BeTrue();
        runtimeState.IsAttached.Should().BeTrue();
        runtimeState.StatusText.Should().Be("WebView2 attached. Waiting for requests.");
    }

    [TestMethod]
    public async Task AttachAsync_DoesNotDelegateWhileDisabled()
    {
        var store = new InterceptSessionStore();
        var adapter = new FakePlatformAdapter();
        var runtimeState = new InterceptKit.WebView.WebViewInterceptRuntimeState();
        var controller = new WebView2InterceptController(store, adapter, runtimeState: runtimeState);

        await controller.AttachAsync(new object());

        adapter.AttachedInstance.Should().BeNull();
        controller.IsEnabled.Should().BeFalse();
        runtimeState.StatusText.Should().Be("Intercept OFF.");
    }

    [TestMethod]
    public async Task ForwardDecision_ForPausedRequest_ContinuesThroughAdapterAndResolvesSession()
    {
        var store = new InterceptSessionStore();
        var adapter = new FakePlatformAdapter();
        var runtimeState = new InterceptKit.WebView.WebViewInterceptRuntimeState();
        var controller = new WebView2InterceptController(store, adapter, runtimeState: runtimeState);

        await controller.SetEnabledAsync(true);
        adapter.RaiseRequestPaused(
            """
            {
              "requestId":"fetch-1",
              "request":{
                "method":"POST",
                "url":"https://example.com/api/orders",
                "headers":{
                  "content-type":"application/json"
                },
                "postData":"{\"id\":1}"
              }
            }
            """);

        var session = store.GetPendingSessions().Should().ContainSingle().Subject;

        await controller.SubmitDecisionAsync(session.Id, InterceptDecision.ForwardOriginal());

        adapter.LastForwardContext.Should().NotBeNull();
        adapter.LastForwardContext!.PlatformRequestId.Should().Be("fetch-1");
        adapter.LastEditedRequest.Should().BeNull();
        runtimeState.PausedRequestCount.Should().Be(1);
        runtimeState.LastPausedRequestUrl.Should().Be("https://example.com/api/orders");
        store.GetPendingSessions().Should().BeEmpty();
    }

    [TestMethod]
    public async Task DropDecision_ForPausedRequest_FailsThroughAdapterAndResolvesSession()
    {
        var store = new InterceptSessionStore();
        var adapter = new FakePlatformAdapter();
        var controller = new WebView2InterceptController(store, adapter);

        await controller.SetEnabledAsync(true);
        adapter.RaiseRequestPaused(
            """
            {
              "requestId":"fetch-2",
              "request":{
                "method":"GET",
                "url":"https://example.com/drop",
                "headers":{}
              }
            }
            """);

        var session = store.GetPendingSessions().Should().ContainSingle().Subject;

        await controller.SubmitDecisionAsync(session.Id, InterceptDecision.Drop());

        adapter.LastDroppedContext.Should().NotBeNull();
        adapter.LastDroppedContext!.PlatformRequestId.Should().Be("fetch-2");
        store.GetPendingSessions().Should().BeEmpty();
    }

    [TestMethod]
    public async Task SetEnabledAsync_False_AutoForwardsPendingRequestsWithoutDetachingAdapter()
    {
        var store = new InterceptSessionStore();
        var adapter = new FakePlatformAdapter();
        var runtimeState = new InterceptKit.WebView.WebViewInterceptRuntimeState();
        var controller = new WebView2InterceptController(store, adapter, runtimeState: runtimeState);

        await controller.SetEnabledAsync(true);
        adapter.RaiseRequestPaused(
            """
            {
              "requestId":"fetch-3",
              "request":{
                "method":"GET",
                "url":"https://example.com/queued",
                "headers":{}
              }
            }
            """);

        store.GetPendingSessions().Should().ContainSingle();

        await controller.SetEnabledAsync(false);

        adapter.ForwardedRequestIds.Should().ContainSingle().Which.Should().Be("fetch-3");
        adapter.DisableCallCount.Should().Be(0);
        controller.IsEnabled.Should().BeFalse();
        runtimeState.StatusText.Should().Be("Intercept OFF.");
        store.GetPendingSessions().Should().BeEmpty();
    }

    [TestMethod]
    public async Task StopAsync_AutoForwardsPendingRequestsAndDisablesAdapter()
    {
        var store = new InterceptSessionStore();
        var adapter = new FakePlatformAdapter();
        var runtimeState = new InterceptKit.WebView.WebViewInterceptRuntimeState();
        var controller = new WebView2InterceptController(store, adapter, runtimeState: runtimeState);

        await controller.SetEnabledAsync(true);
        adapter.RaiseRequestPaused(
            """
            {
              "requestId":"fetch-stop",
              "request":{
                "method":"GET",
                "url":"https://example.com/stop",
                "headers":{}
              }
            }
            """);

        await controller.StopAsync();

        adapter.ForwardedRequestIds.Should().Contain("fetch-stop");
        adapter.DisableCallCount.Should().Be(1);
        controller.IsEnabled.Should().BeFalse();
        runtimeState.StatusText.Should().Be("Intercept OFF.");
        store.GetPendingSessions().Should().BeEmpty();
    }

    [TestMethod]
    public async Task RequestPaused_AfterDisable_IsImmediatelyForwarded()
    {
        var store = new InterceptSessionStore();
        var adapter = new FakePlatformAdapter();
        var runtimeState = new InterceptKit.WebView.WebViewInterceptRuntimeState();
        var controller = new WebView2InterceptController(store, adapter, runtimeState: runtimeState);

        await controller.SetEnabledAsync(true);
        await controller.SetEnabledAsync(false);

        adapter.RaiseRequestPaused(
            """
            {
              "requestId":"fetch-late",
              "request":{
                "method":"GET",
                "url":"https://example.com/late",
                "headers":{}
              }
            }
            """);

        adapter.ForwardedRequestIds.Should().Contain("fetch-late");
        store.GetPendingSessions().Should().BeEmpty();
        runtimeState.StatusText.Should().Be("Intercept OFF.");
    }

    [TestMethod]
    public async Task NonIncludedResourceType_IsImmediatelyForwardedWithoutCreatingPendingSession()
    {
        var store = new InterceptSessionStore();
        var adapter = new FakePlatformAdapter();
        var runtimeState = new InterceptKit.WebView.WebViewInterceptRuntimeState();
        var controller = new WebView2InterceptController(store, adapter, runtimeState: runtimeState);

        await controller.SetEnabledAsync(true);
        adapter.RaiseRequestPaused(
            """
            {
              "requestId":"fetch-image",
              "resourceType":"Image",
              "request":{
                "method":"GET",
                "url":"https://example.com/logo.png",
                "headers":{}
              }
            }
            """);

        adapter.ForwardedRequestIds.Should().Contain("fetch-image");
        store.GetPendingSessions().Should().BeEmpty();
        runtimeState.PausedRequestCount.Should().Be(0);
    }

    [TestMethod]
    public async Task IncludedResourceType_CreatesPendingSession()
    {
        var store = new InterceptSessionStore();
        var adapter = new FakePlatformAdapter();
        var runtimeState = new InterceptKit.WebView.WebViewInterceptRuntimeState();
        var controller = new WebView2InterceptController(store, adapter, runtimeState: runtimeState);

        await controller.SetEnabledAsync(true);
        adapter.RaiseRequestPaused(
            """
            {
              "requestId":"fetch-document",
              "resourceType":"Document",
              "request":{
                "method":"GET",
                "url":"https://example.com/page",
                "headers":{}
              }
            }
            """);

        adapter.ForwardedRequestIds.Should().NotContain("fetch-document");
        store.GetPendingSessions().Should().ContainSingle();
        runtimeState.PausedRequestCount.Should().Be(1);
    }

    [TestMethod]
    public async Task InstagramOneTapRedirect_IsImmediatelyForwardedWithoutCreatingPendingSession()
    {
        var store = new InterceptSessionStore();
        var adapter = new FakePlatformAdapter();
        var controller = new WebView2InterceptController(store, adapter);

        await controller.SetEnabledAsync(true);
        adapter.RaiseRequestPaused(
            """
            {
              "requestId":"fetch-onetap",
              "resourceType":"Document",
              "request":{
                "method":"GET",
                "url":"https://www.instagram.com/accounts/onetap/?lsrc=ci",
                "headers":{}
              }
            }
            """);

        adapter.ForwardedRequestIds.Should().Contain("fetch-onetap");
        store.GetPendingSessions().Should().BeEmpty();
    }

    [TestMethod]
    public async Task InstagramLoginMutation_IsImmediatelyForwardedWithoutCreatingPendingSession()
    {
        var store = new InterceptSessionStore();
        var adapter = new FakePlatformAdapter();
        var controller = new WebView2InterceptController(store, adapter);

        await controller.SetEnabledAsync(true);
        adapter.RaiseRequestPaused(
            """
            {
              "requestId":"fetch-login",
              "resourceType":"XHR",
              "request":{
                "method":"POST",
                "url":"https://www.instagram.com/api/graphql",
                "headers":{"X-FB-Friendly-Name":"useCDSWebLoginMutation"}
              }
            }
            """);

        adapter.ForwardedRequestIds.Should().Contain("fetch-login");
        store.GetPendingSessions().Should().BeEmpty();
    }

    [TestMethod]
    public async Task ForwardDecision_RemovesPendingSessionBeforeAdapterCompletes()
    {
        var store = new InterceptSessionStore();
        var adapter = new FakePlatformAdapter
        {
            ForwardCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var controller = new WebView2InterceptController(store, adapter);

        await controller.SetEnabledAsync(true);
        adapter.RaiseRequestPaused(
            """
            {
              "requestId":"fetch-slow",
              "resourceType":"Document",
              "request":{
                "method":"GET",
                "url":"https://example.com/slow",
                "headers":{}
              }
            }
            """);

        var session = store.GetPendingSessions().Should().ContainSingle().Subject;

        var forwardTask = controller.SubmitDecisionAsync(session.Id, InterceptDecision.ForwardOriginal());

        store.GetPendingSessions().Should().BeEmpty();
        forwardTask.IsCompleted.Should().BeFalse();

        adapter.ForwardCompletion.SetResult();
        await forwardTask;
    }

    [TestMethod]
    public async Task TemporaryPassThrough_ForwardsFollowUpRequestsWithoutCreatingPendingSession()
    {
        var store = new InterceptSessionStore();
        var adapter = new FakePlatformAdapter();
        var runtimeState = new InterceptKit.WebView.WebViewInterceptRuntimeState();
        var controller = new WebView2InterceptController(store, adapter, runtimeState: runtimeState);

        await controller.SetEnabledAsync(true);
        controller.BeginTemporaryPassThrough(TimeSpan.FromSeconds(5));

        adapter.RaiseRequestPaused(
            """
            {
              "requestId":"fetch-follow-up",
              "resourceType":"Document",
              "request":{
                "method":"GET",
                "url":"https://example.com/follow-up",
                "headers":{}
              }
            }
            """);

        adapter.ForwardedRequestIds.Should().Contain("fetch-follow-up");
        store.GetPendingSessions().Should().BeEmpty();
        runtimeState.PausedRequestCount.Should().Be(0);
    }

    private sealed class FakePlatformAdapter : IWebViewInterceptPlatformAdapter
    {
        public event EventHandler<string>? RequestPaused;

        public object? AttachedInstance { get; private set; }

        public PausedWebViewRequestContext? LastForwardContext { get; private set; }

        public EditableInterceptedRequest? LastEditedRequest { get; private set; }

        public PausedWebViewRequestContext? LastDroppedContext { get; private set; }

        public int DisableCallCount { get; private set; }

        public TaskCompletionSource? ForwardCompletion { get; set; }

        public List<string> ForwardedRequestIds { get; } = [];

        public Task AttachAsync(object platformView, CancellationToken cancellationToken = default)
        {
            AttachedInstance = platformView;
            return Task.CompletedTask;
        }

        public Task DisableAsync()
        {
            DisableCallCount++;
            return Task.CompletedTask;
        }

        public Task ForwardAsync(
            PausedWebViewRequestContext context,
            EditableInterceptedRequest? editedRequest,
            CancellationToken cancellationToken = default)
        {
            LastForwardContext = context;
            LastEditedRequest = editedRequest;
            if (!string.IsNullOrWhiteSpace(context.PlatformRequestId))
            {
                ForwardedRequestIds.Add(context.PlatformRequestId);
            }

            if (ForwardCompletion is not null)
            {
                return ForwardCompletion.Task;
            }

            return Task.CompletedTask;
        }

        public Task DropAsync(
            PausedWebViewRequestContext context,
            CancellationToken cancellationToken = default)
        {
            LastDroppedContext = context;
            return Task.CompletedTask;
        }

        public void RaiseRequestPaused(string payload)
        {
            RequestPaused?.Invoke(this, payload);
        }
    }
}
