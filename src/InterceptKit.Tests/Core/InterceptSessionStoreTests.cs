namespace InterceptKit.Tests.Core;

using FluentAssertions;

[TestClass]
public sealed class InterceptSessionStoreTests
{
    [TestMethod]
    public void AddPendingSession_StoresSessionAtQueueTail()
    {
        var store = new InterceptKit.Core.InterceptSessionStore();

        var firstSession = store.CreatePending(BuildRequest("https://example.com/first"));
        var secondSession = store.CreatePending(BuildRequest("https://example.com/second"));

        store.GetPendingSessions().Select(session => session.Id)
            .Should()
            .ContainInOrder(firstSession.Id, secondSession.Id);
    }

    [TestMethod]
    public void ResolveForwardModified_UpdatesDraftAndStatus()
    {
        var store = new InterceptKit.Core.InterceptSessionStore();
        var session = store.CreatePending(BuildRequest("https://example.com/original"));
        var edited = new InterceptKit.Core.EditableInterceptedRequest(
            "POST",
            "https://example.com/edited",
            new Dictionary<string, string> { ["x-test"] = "edited" },
            "{\"ok\":true}");
        var decision = InterceptKit.Core.InterceptDecision.ForwardModified(edited);

        var resolved = store.Resolve(session.Id, decision, null);

        resolved.Status.Should().Be(InterceptKit.Core.InterceptSessionStatus.Forwarded);
        resolved.EditedRequest.Should().BeEquivalentTo(edited);
        resolved.Decision.Kind.Should().Be(InterceptKit.Core.InterceptDecisionKind.ForwardModified);
    }

    [TestMethod]
    public void ResolveDrop_UpdatesStatusWithoutResponse()
    {
        var store = new InterceptKit.Core.InterceptSessionStore();
        var session = store.CreatePending(BuildRequest("https://example.com/drop"));

        var resolved = store.Resolve(session.Id, InterceptKit.Core.InterceptDecision.Drop(), null);

        resolved.Status.Should().Be(InterceptKit.Core.InterceptSessionStatus.Dropped);
        resolved.Response.Should().BeNull();
        store.GetPendingSessions().Should().BeEmpty();
    }

    private static InterceptKit.Core.InterceptedRequest BuildRequest(string url)
    {
        return new InterceptKit.Core.InterceptedRequest(
            Guid.NewGuid().ToString("N"),
            "GET",
            url,
            new Dictionary<string, string> { ["accept"] = "*/*" },
            null,
            DateTimeOffset.UtcNow);
    }
}
