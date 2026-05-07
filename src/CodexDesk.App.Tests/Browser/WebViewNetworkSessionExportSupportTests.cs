namespace CodexDesk.App.Tests.Browser;

using CodexDesk.Services;
using FluentAssertions;

[TestClass]
public sealed class WebViewNetworkSessionExportSupportTests
{
    [TestMethod]
    public void BuildRawSessionText_FormatsRequestAndResponseSections()
    {
        var raw = WebViewNetworkSessionExportSupport.BuildRawSessionText(
            "POST",
            "https://play.google.com/log?hasfast=true",
            "200",
            "OK",
            """
            URL: https://play.google.com/log?hasfast=true
            TYPE: xhr
            MIME: text/plain
            HEADERS:
            Host: play.google.com
            Content-Type: text/plain;charset=UTF-8
            Cookie: SID=abc
            BODY:
            hello=world
            """,
            """
            STATUS: 200 (OK)
            DURATION: 14.2ms
            HEADERS:
            Content-Type: text/plain; charset=UTF-8
            Set-Cookie: SIDCC=updated
            BODY:
            ["-1",null]
            """);

        raw.Should().Be(
            NormalizeLineEndings(
                """
                POST https://play.google.com/log?hasfast=true HTTP/1.1
                Host: play.google.com
                Content-Type: text/plain;charset=UTF-8
                Cookie: SID=abc
                
                hello=world
                HTTP/1.1 200 OK
                Content-Type: text/plain; charset=UTF-8
                Set-Cookie: SIDCC=updated
                
                ["-1",null]
                """));
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.ReplaceLineEndings(Environment.NewLine);
    }
}
