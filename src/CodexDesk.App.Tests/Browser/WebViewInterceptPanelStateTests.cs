namespace CodexDesk.Tests.Browser;

using FluentAssertions;
using InterceptKit.Core;
using InterceptKit.Presentation;
using InterceptKit.WebView;

[TestClass]
public sealed class WebViewInterceptPanelStateTests
{
    [TestMethod]
    public void PanelState_DefaultsToDisabledStatus()
    {
        var store = new InterceptSessionStore();
        var runtimeState = new WebViewInterceptRuntimeState();
        var workspace = new InterceptWorkspaceService(store);
        var panelState = new WebViewInterceptPanelState(workspace, runtimeState);

        panelState.IsEnabled.Should().BeFalse();
        panelState.StatusText.Should().Be("Intercept OFF.");
        panelState.ToggleButtonText.Should().Be("Intercept OFF");
    }

    [TestMethod]
    public void PanelState_ExposesRuntimeStatusAndPendingCount()
    {
        var store = new InterceptSessionStore();
        var runtimeState = new WebViewInterceptRuntimeState();
        var workspace = new InterceptWorkspaceService(store);
        var panelState = new WebViewInterceptPanelState(workspace, runtimeState);

        runtimeState.MarkWaitingForAttach();
        runtimeState.MarkAttached();
        runtimeState.MarkPaused(new InterceptedRequest(
            "request-1",
            "GET",
            "https://example.com/live",
            new Dictionary<string, string>(),
            null,
            DateTimeOffset.UtcNow));
        store.CreatePending(new InterceptedRequest(
            "request-2",
            "POST",
            "https://example.com/orders",
            new Dictionary<string, string>(),
            "{}",
            DateTimeOffset.UtcNow));

        panelState.StatusText.Should().Be("Paused GET https://example.com/live");
        panelState.DetailText.Should().Contain("1 pending");
        panelState.DetailText.Should().Contain("1 paused");
        panelState.DetailText.Should().Contain("https://example.com/live");
        panelState.CountText.Should().Be("1 pending");
    }

    [TestMethod]
    public void PanelState_SelectsAndExposesTheCurrentInterceptSession()
    {
        var store = new InterceptSessionStore();
        var runtimeState = new WebViewInterceptRuntimeState();
        var workspace = new InterceptWorkspaceService(store);
        var panelState = new WebViewInterceptPanelState(workspace, runtimeState);
        var first = store.CreatePending(new InterceptedRequest(
            "request-1",
            "GET",
            "https://example.com/one",
            new Dictionary<string, string>(),
            null,
            DateTimeOffset.UtcNow));
        var second = store.CreatePending(new InterceptedRequest(
            "request-2",
            "POST",
            "https://example.com/two",
            new Dictionary<string, string> { ["content-type"] = "application/json" },
            "{\"id\":2}",
            DateTimeOffset.UtcNow));

        panelState.SelectSession(second.Id);

        panelState.GetPendingItems().Should().HaveCount(2);
        panelState.SelectedSessionId.Should().Be(second.Id);
        panelState.GetSelectedSession()!.OriginalRequest.Url.Should().Be("https://example.com/two");
        panelState.GetSelectedSession()!.OriginalRequest.Method.Should().Be("POST");
        panelState.GetSelectedSession()!.OriginalRequest.Body.Should().Be("{\"id\":2}");
        panelState.SelectedSummaryText.Should().Be("POST https://example.com/two");
        first.Id.Should().NotBe(second.Id);
    }

    [TestMethod]
    public async Task PanelState_ForwardAndDropDelegateToWorkspace()
    {
        var store = new InterceptSessionStore();
        var runtimeState = new WebViewInterceptRuntimeState();
        var sink = new FakeDecisionSink();
        var workspace = new InterceptWorkspaceService(store, sink);
        var panelState = new WebViewInterceptPanelState(workspace, runtimeState);
        var session = store.CreatePending(new InterceptedRequest(
            "request-1",
            "GET",
            "https://example.com/path",
            new Dictionary<string, string>(),
            null,
            DateTimeOffset.UtcNow));

        panelState.SelectSession(session.Id);
        await panelState.ForwardSelectedAsync();
        sink.LastDecision!.Kind.Should().Be(InterceptDecisionKind.ForwardOriginal);

        await panelState.DropSelectedAsync();
        sink.LastDecision!.Kind.Should().Be(InterceptDecisionKind.Drop);
    }

    [TestMethod]
    public async Task PanelState_ForwardSelected_RemovesCurrentItemAndSelectsNextPendingRequest()
    {
        var store = new InterceptSessionStore();
        var runtimeState = new WebViewInterceptRuntimeState();
        var sink = new ResolvingDecisionSink(store);
        var workspace = new InterceptWorkspaceService(store, sink);
        var panelState = new WebViewInterceptPanelState(workspace, runtimeState);

        var first = store.CreatePending(new InterceptedRequest(
            "request-1",
            "GET",
            "https://example.com/one",
            new Dictionary<string, string>(),
            null,
            DateTimeOffset.UtcNow));
        var second = store.CreatePending(new InterceptedRequest(
            "request-2",
            "GET",
            "https://example.com/two",
            new Dictionary<string, string>(),
            null,
            DateTimeOffset.UtcNow));

        panelState.SelectSession(first.Id);

        await panelState.ForwardSelectedAsync();

        panelState.GetPendingItems().Should().ContainSingle();
        panelState.SelectedSessionId.Should().Be(second.Id);
        panelState.GetSelectedSession()!.OriginalRequest.Url.Should().Be("https://example.com/two");
    }

    [TestMethod]
    public async Task PanelState_ForwardAllDelegatesEveryPendingRequest()
    {
        var store = new InterceptSessionStore();
        var runtimeState = new WebViewInterceptRuntimeState();
        var sink = new FakeDecisionSink();
        var workspace = new InterceptWorkspaceService(store, sink);
        var panelState = new WebViewInterceptPanelState(workspace, runtimeState);

        store.CreatePending(new InterceptedRequest(
            "request-1",
            "GET",
            "https://example.com/one",
            new Dictionary<string, string>(),
            null,
            DateTimeOffset.UtcNow));
        store.CreatePending(new InterceptedRequest(
            "request-2",
            "GET",
            "https://example.com/two",
            new Dictionary<string, string>(),
            null,
            DateTimeOffset.UtcNow));

        await panelState.ForwardAllAsync();

        sink.Decisions.Should().HaveCount(2);
        sink.Decisions.Should().OnlyContain(decision => decision.Kind == InterceptDecisionKind.ForwardOriginal);
    }

    [TestMethod]
    public async Task PanelState_ForwardAll_DrainsFollowUpPendingRequestsCreatedDuringForwarding()
    {
        var store = new InterceptSessionStore();
        var runtimeState = new WebViewInterceptRuntimeState();
        var sink = new CascadingDecisionSink(store);
        var workspace = new InterceptWorkspaceService(store, sink);
        var panelState = new WebViewInterceptPanelState(workspace, runtimeState);

        store.CreatePending(new InterceptedRequest(
            "request-1",
            "GET",
            "https://example.com/start",
            new Dictionary<string, string>(),
            null,
            DateTimeOffset.UtcNow));

        await panelState.ForwardAllAsync();

        sink.ForwardedSessionIds.Should().HaveCount(2);
        store.GetPendingSessions().Should().BeEmpty();
    }

    private sealed class FakeDecisionSink : IInterceptDecisionSink
    {
        public Guid LastSessionId { get; private set; }

        public InterceptDecision? LastDecision { get; private set; }

        public List<InterceptDecision> Decisions { get; } = [];

        public Task SubmitDecisionAsync(Guid sessionId, InterceptDecision decision, CancellationToken cancellationToken = default)
        {
            LastSessionId = sessionId;
            LastDecision = decision;
            Decisions.Add(decision);
            return Task.CompletedTask;
        }
    }

    private sealed class CascadingDecisionSink : IInterceptDecisionSink
    {
        private readonly InterceptSessionStore _store;
        private bool _createdFollowUp;

        public CascadingDecisionSink(InterceptSessionStore store)
        {
            _store = store;
        }

        public List<Guid> ForwardedSessionIds { get; } = [];

        public Task SubmitDecisionAsync(Guid sessionId, InterceptDecision decision, CancellationToken cancellationToken = default)
        {
            ForwardedSessionIds.Add(sessionId);
            _store.Resolve(sessionId, decision, null);

            if (!_createdFollowUp)
            {
                _createdFollowUp = true;
                _store.CreatePending(new InterceptedRequest(
                    "request-2",
                    "GET",
                    "https://example.com/follow-up",
                    new Dictionary<string, string>(),
                    null,
                    DateTimeOffset.UtcNow));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ResolvingDecisionSink : IInterceptDecisionSink
    {
        private readonly InterceptSessionStore _store;

        public ResolvingDecisionSink(InterceptSessionStore store)
        {
            _store = store;
        }

        public Task SubmitDecisionAsync(Guid sessionId, InterceptDecision decision, CancellationToken cancellationToken = default)
        {
            _store.Resolve(sessionId, decision, null);
            return Task.CompletedTask;
        }
    }
}
