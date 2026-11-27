namespace CodexDesk.App.Tests.Workspace;

using CodexDesk.Services;
using FluentAssertions;

[TestClass]
public sealed class WorkspaceNavigationServiceTests
{
    [TestMethod]
    public void RequestSettings_opens_codex_chat_and_notifies_settings_view()
    {
        var navigation = new WorkspaceNavigationService();
        navigation.Request(WorkspaceDestination.Browser);
        var destinations = new List<WorkspaceDestination>();
        var routes = new List<string>();
        var settingsRequests = 0;
        navigation.DestinationRequested += (_, destination) => destinations.Add(destination);
        navigation.ContentRouteRequested += (_, route) => routes.Add(route);
        navigation.SettingsRequested += (_, _) => settingsRequests++;

        navigation.RequestSettings("browser");

        navigation.CurrentDestination.Should().Be(WorkspaceDestination.Codex);
        destinations.Should().ContainSingle().Which.Should().Be(WorkspaceDestination.Codex);
        routes.Should().ContainSingle().Which.Should().Be("/chat");
        settingsRequests.Should().Be(1);
    }

    [TestMethod]
    public void TakePendingSettingsTab_returns_requested_tab_once()
    {
        var navigation = new WorkspaceNavigationService();

        navigation.RequestSettings("browser");

        navigation.TakePendingSettingsTab().Should().Be("browser");
        navigation.TakePendingSettingsTab().Should().BeNull();
    }
}
