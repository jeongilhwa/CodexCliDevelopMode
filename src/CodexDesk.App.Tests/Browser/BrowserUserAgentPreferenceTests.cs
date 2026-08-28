namespace CodexDesk.App.Tests.Browser;

using CodexDesk.Services;
using FluentAssertions;

[TestClass]
public sealed class BrowserUserAgentPreferenceTests
{
    [TestMethod]
    [DataRow(null, BrowserUserAgentPreference.DefaultPresetKey)]
    [DataRow("", BrowserUserAgentPreference.DefaultPresetKey)]
    [DataRow("unknown", BrowserUserAgentPreference.DefaultPresetKey)]
    [DataRow(" DEFAULT ", BrowserUserAgentPreference.DefaultPresetKey)]
    [DataRow(" Chrome ", BrowserUserAgentPreference.ChromePresetKey)]
    [DataRow(" CUSTOM ", BrowserUserAgentPreference.CustomPresetKey)]
    public void Normalize_NormalizesPresetOrFallsBackToDefault(string? presetKey, string expectedPresetKey)
    {
        var preference = BrowserUserAgentPreference.Normalize(presetKey, null);

        preference.PresetKey.Should().Be(expectedPresetKey);
        preference.CustomUserAgent.Should().BeEmpty();
    }

    [TestMethod]
    public void Normalize_TrimsCustomUserAgent()
    {
        var preference = BrowserUserAgentPreference.Normalize(
            BrowserUserAgentPreference.CustomPresetKey,
            "  Mozilla/5.0 TestBrowser/1.0  ");

        preference.Should().Be(
            new BrowserUserAgentPreference(
                BrowserUserAgentPreference.CustomPresetKey,
                "Mozilla/5.0 TestBrowser/1.0"));
    }

    [TestMethod]
    public void ApplyUserAgentCommand_HasNoNavigationUrlByDefault()
    {
        var command = new BrowserCommand(BrowserCommandType.ApplyUserAgent);

        command.Type.Should().Be(BrowserCommandType.ApplyUserAgent);
        command.Url.Should().BeNull();
    }
}
