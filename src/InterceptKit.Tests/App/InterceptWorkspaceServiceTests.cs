namespace InterceptKit.Tests.App;

using FluentAssertions;

[TestClass]
public sealed class InterceptWorkspaceServiceTests
{
    [TestMethod]
    public void WorkspaceService_MapsPendingSessionsForDisplay()
    {
        var store = new InterceptKit.Core.InterceptSessionStore();
        store.CreatePending(new InterceptKit.Core.InterceptedRequest(
            "request-1",
            "POST",
            "https://example.com/api/orders",
            new Dictionary<string, string> { ["content-type"] = "application/json" },
            "{\"id\":1}",
            DateTimeOffset.UtcNow));

        var service = new InterceptKit.Presentation.InterceptWorkspaceService(store);

        var item = service.GetPendingItems().Should().ContainSingle().Subject;
        item.Method.Should().Be("POST");
        item.Url.Should().Be("https://example.com/api/orders");
        item.Status.Should().Be("Pending");
    }

    [TestMethod]
    public async Task ForwardSelectedAsync_SubmitsForwardOriginalDecision()
    {
        var store = new InterceptKit.Core.InterceptSessionStore();
        var session = store.CreatePending(new InterceptKit.Core.InterceptedRequest(
            "request-2",
            "GET",
            "https://example.com/forward",
            new Dictionary<string, string>(),
            null,
            DateTimeOffset.UtcNow));
        var sink = new FakeDecisionSink();
        var service = new InterceptKit.Presentation.InterceptWorkspaceService(store, sink);

        service.SelectSession(session.Id);
        await service.ForwardSelectedAsync();

        sink.LastSessionId.Should().Be(session.Id);
        sink.LastDecision!.Kind.Should().Be(InterceptKit.Core.InterceptDecisionKind.ForwardOriginal);
    }

    [TestMethod]
    public async Task DropSelectedAsync_SubmitsDropDecision()
    {
        var store = new InterceptKit.Core.InterceptSessionStore();
        var session = store.CreatePending(new InterceptKit.Core.InterceptedRequest(
            "request-3",
            "GET",
            "https://example.com/drop",
            new Dictionary<string, string>(),
            null,
            DateTimeOffset.UtcNow));
        var sink = new FakeDecisionSink();
        var service = new InterceptKit.Presentation.InterceptWorkspaceService(store, sink);

        service.SelectSession(session.Id);
        await service.DropSelectedAsync();

        sink.LastSessionId.Should().Be(session.Id);
        sink.LastDecision!.Kind.Should().Be(InterceptKit.Core.InterceptDecisionKind.Drop);
    }

    [TestMethod]
    public async Task ForwardSelectedAsync_RemovesCurrentItemAndSelectsNextPendingSession()
    {
        var store = new InterceptKit.Core.InterceptSessionStore();
        var first = store.CreatePending(new InterceptKit.Core.InterceptedRequest(
            "request-5",
            "GET",
            "https://example.com/one",
            new Dictionary<string, string>(),
            null,
            DateTimeOffset.UtcNow));
        var second = store.CreatePending(new InterceptKit.Core.InterceptedRequest(
            "request-6",
            "GET",
            "https://example.com/two",
            new Dictionary<string, string>(),
            null,
            DateTimeOffset.UtcNow));
        var sink = new ResolvingDecisionSink(store);
        var service = new InterceptKit.Presentation.InterceptWorkspaceService(store, sink);

        service.SelectSession(first.Id);
        await service.ForwardSelectedAsync();

        service.GetPendingItems().Should().ContainSingle();
        service.SelectedSessionId.Should().Be(second.Id);
        service.GetSelectedSession()!.OriginalRequest.Url.Should().Be("https://example.com/two");
    }

    [TestMethod]
    public void WorkspaceService_RaisesChangedWhenStoreChanges()
    {
        var store = new InterceptKit.Core.InterceptSessionStore();
        var service = new InterceptKit.Presentation.InterceptWorkspaceService(store);
        var raised = 0;

        service.Changed += (_, _) => raised++;
        store.CreatePending(new InterceptKit.Core.InterceptedRequest(
            "request-4",
            "GET",
            "https://example.com/live",
            new Dictionary<string, string>(),
            null,
            DateTimeOffset.UtcNow));

        raised.Should().Be(1);
    }

    private sealed class FakeDecisionSink : InterceptKit.Core.IInterceptDecisionSink
    {
        public Guid LastSessionId { get; private set; }

        public InterceptKit.Core.InterceptDecision? LastDecision { get; private set; }

        public Task SubmitDecisionAsync(
            Guid sessionId,
            InterceptKit.Core.InterceptDecision decision,
            CancellationToken cancellationToken = default)
        {
            LastSessionId = sessionId;
            LastDecision = decision;
            return Task.CompletedTask;
        }
    }

    private sealed class ResolvingDecisionSink : InterceptKit.Core.IInterceptDecisionSink
    {
        private readonly InterceptKit.Core.InterceptSessionStore _store;

        public ResolvingDecisionSink(InterceptKit.Core.InterceptSessionStore store)
        {
            _store = store;
        }

        public Task SubmitDecisionAsync(
            Guid sessionId,
            InterceptKit.Core.InterceptDecision decision,
            CancellationToken cancellationToken = default)
        {
            _store.Resolve(sessionId, decision, null);
            return Task.CompletedTask;
        }
    }
}
