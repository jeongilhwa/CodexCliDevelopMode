namespace CodexDesk.App.Tests.Browser;

using CodexDesk.Services;
using FluentAssertions;

[TestClass]
public sealed class BrowserPasswordAutosavePreferenceTests
{
    [TestMethod]
    public void New_browser_state_defaults_password_autosave_to_enabled()
    {
        var state = new BrowserStateService();

        state.PasswordAutosaveEnabled.Should().BeTrue();
    }

    [TestMethod]
    public void Apply_password_autosave_command_has_no_navigation_url()
    {
        var command = new BrowserCommand(BrowserCommandType.ApplyPasswordAutosave);

        command.Type.Should().Be(BrowserCommandType.ApplyPasswordAutosave);
        command.Url.Should().BeNull();
    }
}
