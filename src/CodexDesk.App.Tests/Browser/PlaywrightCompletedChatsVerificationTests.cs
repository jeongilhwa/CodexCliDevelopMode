using CodexDesk.Services;
using FluentAssertions;
using System.Runtime.InteropServices;

namespace CodexDesk.Tests.Browser;

[TestClass]
public sealed class PlaywrightCompletedChatsVerificationTests
{
    public TestContext? TestContext { get; set; }

    [TestMethod]
    public async Task VerifyRoutesCanBeLoadedWithoutSavingResults()
    {
        try
        {
            var browserService = new NaverTalkBrowserService();

            await browserService.EnsureInitializedAsync();

            var savedCookiesJson = await browserService.TryGetSavedCookieSessionAsync();
            if (string.IsNullOrWhiteSpace(savedCookiesJson))
            {
                Assert.Inconclusive("Playwright completed chat verification requires a saved Naver cookie session.");
            }

            await using var session = await PlaywrightCompletedChatsSession.CreateAsync(browserService);

            var partnerChat = await session.ResolvePartnerChatIdAsync(null);
            TestContext?.WriteLine($"partnerChatIdSource={partnerChat.Source}");
            TestContext?.WriteLine($"partnerChatId={partnerChat.PartnerChatId}");

            partnerChat.PartnerChatId.Should().NotBeNullOrWhiteSpace();

            var routes = await session.LoadRoutesAsync(
                DateTime.Today.AddMonths(-6),
                partnerChat.PartnerChatId,
                Array.Empty<string>());

            TestContext?.WriteLine($"apiAttempted={routes.ApiAttempted}");
            TestContext?.WriteLine($"apiOk={routes.ApiOk}");
            TestContext?.WriteLine($"apiError={routes.ApiError}");
            TestContext?.WriteLine($"routeCount={routes.Routes.Count}");
            TestContext?.WriteLine($"pagesFetched={routes.PagesFetched}");
            TestContext?.WriteLine($"lastCursor={routes.LastCursor}");

            foreach (var route in routes.Routes.Take(5))
            {
                TestContext?.WriteLine($"route={route.Href} | {route.TimeLabel} | {route.Name}");
            }

            (routes.ApiOk == true || routes.Routes.Count > 0).Should().BeTrue();
        }
        catch (Exception ex) when (IsEnvironmentDependentFailure(ex))
        {
            Assert.Inconclusive($"Playwright completed chat verification requires the MAUI desktop runtime and an interactive browser session. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsEnvironmentDependentFailure(Exception exception)
    {
        return exception switch
        {
            COMException => true,
            InvalidOperationException => true,
            PlatformNotSupportedException => true,
            TypeInitializationException => true,
            _ when exception.InnerException is not null => IsEnvironmentDependentFailure(exception.InnerException),
            _ => false
        };
    }
}
