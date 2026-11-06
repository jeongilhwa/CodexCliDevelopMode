namespace CodexDesk.App.Tests.Browser;

using CodexDesk.Services;
using FluentAssertions;

[TestClass]
public sealed class InstagramLoginRedirectScriptBuilderTests
{
    [TestMethod]
    public void Build_OnlyFollowsTheExpectedSuccessfulInstagramOneTapRedirect()
    {
        var script = InstagramLoginRedirectScriptBuilder.Build();

        script.Should().Contain("login?.ig_authenticated !== true");
        script.Should().Contain("target.hostname !== 'www.instagram.com'");
        script.Should().Contain("target.pathname.startsWith('/accounts/onetap')");
        script.Should().Contain("window.location.assign(target.href)");
        script.Should().Contain("this.__codexRequestUrl.includes('/api/graphql')");
    }
}
