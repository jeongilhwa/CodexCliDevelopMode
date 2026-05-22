namespace CodexDesk.App.Tests.Browser;

using CodexDesk.Services;
using FluentAssertions;

[TestClass]
public sealed class DomainCookieSessionStoreTests
{
    [TestMethod]
    public void UpsertSnapshot_ReplacesExistingOriginAndKeepsOtherOrigins()
    {
        var initialJson = DomainCookieSessionStore.UpsertSnapshot(
            null,
            "https://www.example.com",
            [new PersistedBrowserCookie("sid", "one", ".example.com", "/", null, true, false, "None")]);
        var appendedJson = DomainCookieSessionStore.UpsertSnapshot(
            initialJson,
            "https://shop.example.com",
            [new PersistedBrowserCookie("theme", "dark", "shop.example.com", "/", null, true, false, null)]);

        var updatedJson = DomainCookieSessionStore.UpsertSnapshot(
            appendedJson,
            "https://www.example.com",
            [new PersistedBrowserCookie("sid", "two", ".example.com", "/", null, true, true, "Lax")]);

        var snapshots = DomainCookieSessionStore.ParseSnapshots(updatedJson);

        snapshots.Should().HaveCount(2);
        snapshots.Should().ContainSingle(snapshot => snapshot.TargetOrigin == "https://www.example.com");
        snapshots.Should().ContainSingle(snapshot => snapshot.TargetOrigin == "https://shop.example.com");
        snapshots.Single(snapshot => snapshot.TargetOrigin == "https://www.example.com")
            .Cookies.Should().BeEquivalentTo(
                [new PersistedBrowserCookie("sid", "two", ".example.com", "/", null, true, true, "Lax")]);
    }

    [TestMethod]
    public void ParseSnapshots_ReturnsEmptyListForInvalidJson()
    {
        var snapshots = DomainCookieSessionStore.ParseSnapshots("not-json");

        snapshots.Should().BeEmpty();
    }
}
