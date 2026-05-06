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

    [TestMethod]
    public void BuildCookieDetails_IncludesTargetOriginAndCookieScopes()
    {
        var details = WebViewCookieExportSupport.BuildCookieDetails(
            "https://www.example.com",
            [
                new WebViewCookieExportItem("sid", "root", "www.example.com", "/"),
                new WebViewCookieExportItem("theme", "dark", ".example.com", "/account")
            ]);

        details.Should().Be(
            NormalizeLineEndings(
                """
                Target Origin: https://www.example.com
                Target Host: www.example.com
                
                Name: sid
                Value: root
                Domain: www.example.com
                Path: /
                Scope URL: https://www.example.com/
                
                Name: theme
                Value: dark
                Domain: .example.com
                Path: /account
                Scope URL: https://example.com/account
                """));
    }

    [TestMethod]
    public void BuildCookieDetails_ReturnsSummaryWhenCookieListIsEmpty()
    {
        var details = WebViewCookieExportSupport.BuildCookieDetails("https://www.example.com", []);

        details.Should().Be(
            NormalizeLineEndings(
                """
                Target Origin: https://www.example.com
                Target Host: www.example.com
                
                No matching cookies found.
                """));
    }

    [TestMethod]
    public void ParseCookieImportText_ParsesDetailedCookieBlocks()
    {
        var cookies = WebViewCookieExportSupport.ParseCookieImportText(
            NormalizeLineEndings(
                """
                Target Origin: https://www.example.com
                Target Host: www.example.com
                
                Name: sid
                Value: root
                Domain: www.example.com
                Path: /
                Scope URL: https://www.example.com/
                
                Name: theme
                Value: dark
                Domain: .example.com
                Path: /account
                Scope URL: https://example.com/account
                """),
            "https://fallback.example.com");

        cookies.Should().BeEquivalentTo(
            [
                new WebViewCookieImportItem("sid", "root", "www.example.com", "/", true, false, null),
                new WebViewCookieImportItem("theme", "dark", ".example.com", "/account", true, false, null)
            ]);
    }

    [TestMethod]
    public void ParseCookieImportText_ParsesPlainCookieHeaderUsingFallbackOrigin()
    {
        var cookies = WebViewCookieExportSupport.ParseCookieImportText(
            "sid=root; theme=dark",
            "http://shop.example.com");

        cookies.Should().BeEquivalentTo(
            [
                new WebViewCookieImportItem("sid", "root", "shop.example.com", "/", false, false, null),
                new WebViewCookieImportItem("theme", "dark", "shop.example.com", "/", false, false, null)
            ]);
    }

    [TestMethod]
    public void ParseCookieImportText_ParsesWebView2DevToolsCookieTableBlocks()
    {
        var cookies = WebViewCookieExportSupport.ParseCookieImportText(
            NormalizeLineEndings(
                """
                cna
                59fHIbzVqnECAd5k2kx0y/wa
                .aliexpress.com
                /
                2027-04-03T03:25:14.939Z
                27
                
                ✓
                None
                
                
                Medium
                JSESSIONID
                2684B969BF5D7913D2CB5AA0DC038AF8
                gsp.aliexpress.com
                /
                Session
                42
                ✓
                
                
                
                Medium
                af_ss_b
                1
                .aliexpress.com
                /
                2027-05-05T22:56:50.960Z
                8
                
                
                Lax
                
                
                Medium
                """),
            "https://fallback.example.com");

        cookies.Should().BeEquivalentTo(
            [
                new WebViewCookieImportItem("cna", "59fHIbzVqnECAd5k2kx0y/wa", ".aliexpress.com", "/", true, false, "None"),
                new WebViewCookieImportItem("JSESSIONID", "2684B969BF5D7913D2CB5AA0DC038AF8", "gsp.aliexpress.com", "/", false, true, null),
                new WebViewCookieImportItem("af_ss_b", "1", ".aliexpress.com", "/", false, false, "Lax")
            ]);
    }

    [TestMethod]
    public void ExtractTargetOrigin_UsesFirstDevToolsCookieDomain()
    {
        var origin = WebViewCookieExportSupport.ExtractTargetOrigin(
            NormalizeLineEndings(
                """
                cna
                59fHIbzVqnECAd5k2kx0y/wa
                .aliexpress.com
                /
                2027-04-03T03:25:14.939Z
                27
                """));

        origin.Should().Be("https://aliexpress.com");
    }

    [TestMethod]
    public void BuildImportPlan_ReplacesStaleScopedCookiesAndReportsAddedModifiedCounts()
    {
        var plan = WebViewCookieExportSupport.BuildImportPlan(
            [
                new WebViewCookieImportItem("sid", "fresh-root", ".example.com", "/", true, false, "None"),
                new WebViewCookieImportItem("sid", "fresh-gsp", "gsp.example.com", "/", true, true, null),
                new WebViewCookieImportItem("theme", "dark", ".example.com", "/", true, false, null),
                new WebViewCookieImportItem("locale", "ko_KR", ".example.com", "/", true, false, null)
            ],
            [
                new WebViewCookieImportItem("sid", "old-root", ".example.com", "/", true, false, "Lax"),
                new WebViewCookieImportItem("sid", "old-account", ".example.com", "/account", true, false, null),
                new WebViewCookieImportItem("sid", "old-gsp", "gsp.example.com", "/", true, false, null),
                new WebViewCookieImportItem("theme", "dark", ".example.com", "/", true, false, null),
                new WebViewCookieImportItem("other", "keep", ".other.com", "/", true, false, null)
            ]);

        plan.CookiesToApply.Should().BeEquivalentTo(
            [
                new WebViewCookieImportItem("sid", "fresh-root", ".example.com", "/", true, false, "None"),
                new WebViewCookieImportItem("sid", "fresh-gsp", "gsp.example.com", "/", true, true, null),
                new WebViewCookieImportItem("theme", "dark", ".example.com", "/", true, false, null),
                new WebViewCookieImportItem("locale", "ko_KR", ".example.com", "/", true, false, null)
            ]);

        plan.CookiesToDelete.Should().BeEquivalentTo(
            [
                new WebViewCookieImportItem("sid", "old-account", ".example.com", "/account", true, false, null)
            ]);
        plan.CookiesToReplace.Should().BeEquivalentTo(
            [
                new WebViewCookieImportItem("sid", "old-root", ".example.com", "/", true, false, "Lax"),
                new WebViewCookieImportItem("sid", "old-gsp", "gsp.example.com", "/", true, false, null)
            ]);

        plan.AddedCount.Should().Be(1);
        plan.ModifiedCount.Should().Be(2);
        plan.UnchangedCount.Should().Be(1);
        plan.RemovedCount.Should().Be(1);
    }

    [TestMethod]
    public void BuildImportPlan_UsesLastImportedCookieWhenExactScopeAppearsMultipleTimes()
    {
        var plan = WebViewCookieExportSupport.BuildImportPlan(
            [
                new WebViewCookieImportItem("sid", "old", ".example.com", "/", true, false, null),
                new WebViewCookieImportItem("sid", "new", ".example.com", "/", true, false, "None")
            ],
            []);

        plan.CookiesToApply.Should().ContainSingle();
        plan.CookiesToApply[0].Value.Should().Be("new");
        plan.CookiesToApply[0].SameSite.Should().Be("None");
        plan.CookiesToReplace.Should().BeEmpty();
        plan.AddedCount.Should().Be(1);
        plan.ModifiedCount.Should().Be(0);
        plan.RemovedCount.Should().Be(0);
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.ReplaceLineEndings(Environment.NewLine);
    }
}
