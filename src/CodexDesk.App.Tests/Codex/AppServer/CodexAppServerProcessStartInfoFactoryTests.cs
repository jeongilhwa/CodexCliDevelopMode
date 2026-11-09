using System.Diagnostics;
using System.Text;
using FluentAssertions;

namespace CodexDesk.Tests.Codex.AppServer;

[TestClass]
public sealed class CodexAppServerProcessStartInfoFactoryTests
{
    [TestMethod]
    public void Create_should_configure_a_stdio_app_server_process_for_the_requested_workspace()
    {
        // This test fails if the factory stops launching the app-server over stdio,
        // which would break the JSON-RPC client transport.
        var optionsType = Type.GetType("CodexDesk.AppServer.CodexAppServerOptions, CodexDesk");
        var factoryType = Type.GetType("CodexDesk.AppServer.CodexAppServerProcessStartInfoFactory, CodexDesk");

        optionsType.Should().NotBeNull();
        factoryType.Should().NotBeNull();

        var options = Activator.CreateInstance(optionsType!);
        optionsType!.GetProperty("CommandPath")!.SetValue(options, "C:\\tools\\codex.cmd");
        optionsType.GetProperty("WorkspacePath")!.SetValue(options, @"D:\\workspaces\\demo");

        var factory = Activator.CreateInstance(factoryType!);
        var startInfo = factoryType!.GetMethod("Create")!.Invoke(factory, [options]) as ProcessStartInfo;

        startInfo.Should().NotBeNull();
        startInfo!.FileName.Should().Be("C:\\tools\\codex.cmd");
        startInfo.WorkingDirectory.Should().Be(@"D:\\workspaces\\demo");
        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.RedirectStandardInput.Should().BeTrue();
        startInfo.RedirectStandardOutput.Should().BeTrue();
        startInfo.RedirectStandardError.Should().BeTrue();
        startInfo.StandardInputEncoding.Should().BeOfType<UTF8Encoding>().Which.GetPreamble().Should().BeEmpty();
        startInfo.StandardOutputEncoding.Should().BeOfType<UTF8Encoding>().Which.GetPreamble().Should().BeEmpty();
        startInfo.StandardErrorEncoding.Should().BeOfType<UTF8Encoding>().Which.GetPreamble().Should().BeEmpty();
        startInfo.ArgumentList.Should().ContainInOrder("app-server", "--listen", "stdio://");
    }
}
