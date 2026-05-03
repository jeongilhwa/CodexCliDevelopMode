namespace CodexDesk.App.Tests.Browser;

using CodexDesk.Services;
using FluentAssertions;

[TestClass]
public sealed class WebViewCookieExportSupportTests
{
    [TestMethod]
    public void MatchesHost_IncludesHostDomainAndSubdomainCookiesRegardlessOfPath()
    {
        WebViewCookieExportSupport.MatchesHost("example.com", "example.com").Should().BeTrue();
        WebViewCookieExportSupport.MatchesHost("example.com", ".example.com").Should().BeTrue();
        WebViewCookieExportSupport.MatchesHost("www.example.com", ".example.com").Should().BeTrue();
        WebViewCookieExportSupport.MatchesHost("example.com", "cdn.example.com").Should().BeFalse();
        WebViewCookieExportSupport.MatchesHost("badexample.com", ".example.com").Should().BeFalse();
    }

    [TestMethod]
    public void BuildCookieHeader_PreservesDuplicateNamesFromDifferentCookieScopes()
    {
        var header = WebViewCookieExportSupport.BuildCookieHeader(
            [
                new WebViewCookieExportItem("sid", "root", "example.com", "/"),
                new WebViewCookieExportItem("sid", "account", "example.com", "/account"),
                new WebViewCookieExportItem("theme", "dark", ".example.com", "/")
            ]);

        header.Should().Be("sid=root; sid=account; theme=dark");
    }
}
