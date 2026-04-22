namespace CodexDesk.App.Tests.Browser;

using CodexDesk.Services;
using FluentAssertions;

[TestClass]
public sealed class WebViewFetchRequestScriptBuilderTests
{
    [TestMethod]
    public void Build_UsesSameOriginCredentialsForRelativeUrlWithoutBody()
    {
        var script = WebViewFetchRequestScriptBuilder.Build("/Home/ExtendLoginTime", "POST", string.Empty);

        script.Should().Contain("\"url\":\"/Home/ExtendLoginTime\"");
        script.Should().Contain("\"method\":\"POST\"");
        script.Should().Contain("credentials: url.startsWith('http') ? 'include' : 'same-origin'");
        script.Should().Contain("if (body.length > 0)");
    }

    [TestMethod]
    public void Build_IncludesBodyAndJsonContentTypeWhenBodyIsProvided()
    {
        var script = WebViewFetchRequestScriptBuilder.Build(
            "https://www.esmplus.com/Home/ExtendLoginTime",
            "post",
            "{\"keepAlive\":true}");

        script.Should().Contain("\"method\":\"POST\"");
        script.Should().Contain("\\\"keepAlive\\\":true");
        script.Should().Contain("'Content-Type': 'application/json'");
    }
}
