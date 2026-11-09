using System.Diagnostics;
using System.Text;
using CodexDesk.AppServer;
using FluentAssertions;

namespace CodexDesk.Tests.Codex.AppServer;

[TestClass]
public sealed class CodexAppServerClientTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("{\"command\":\"dotnet test\"}")]
    public async Task Server_requests_should_be_raised_before_matching_client_responses(string? parameters)
    {
        // Routing method + id as a response loses approvals, even if ids overlap.
        var requestJson = "{\"id\":9,\"method\":\"commandExecution/requestApproval\""
            + (parameters is null ? "}" : ",\"params\":" + parameters + "}");
        var script = $$$"""
            [Console]::ReadLine() | Out-Null
            [Console]::Out.WriteLine('{{{requestJson}}}')
            [Console]::Out.WriteLine('{"id":1,"method":"commandExecution/requestApproval","params":{"collision":true}}')
            [Console]::Out.WriteLine('{"id":1,"result":{"ready":true}}')
            [Console]::ReadLine() | Out-Null
            while ($null -ne [Console]::ReadLine()) { }
            """;
        await using var client = new CodexAppServerClient(new ScriptProcessFactory(script));
        var requests = new List<CodexAppServerServerRequest>();
        var notifications = new List<CodexAppServerEvent>();
        client.ServerRequestReceived += (_, request) => requests.Add(request);
        client.EventReceived += (_, notification) => notifications.Add(notification);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await client.StartAsync(new CodexAppServerOptions(), timeout.Token);

        requests.Should().HaveCount(2);
        requests[0].RequestId.Should().Be(9);
        requests[0].Method.Should().Be("commandExecution/requestApproval");
        requests[0].Parameters.GetRawText().Should().Be(parameters ?? "{}");
        requests[1].RequestId.Should().Be(1);
        requests[1].Parameters.GetProperty("collision").GetBoolean().Should().BeTrue();
        notifications.Should().BeEmpty();
    }

    [TestMethod]
    public async Task RespondAsync_should_write_a_correlated_json_rpc_result_and_preserve_other_message_routing()
    {
        // A wrong response envelope or id prevents the server resolving approval.
        const string script = """
            [Console]::ReadLine() | Out-Null
            [Console]::Out.WriteLine('{"id":1,"result":{}}')
            [Console]::ReadLine() | Out-Null
            $response = [Console]::ReadLine()
            [Console]::Out.WriteLine('{"method":"test/response","params":' + $response + '}')
            $request = [Console]::ReadLine() | ConvertFrom-Json
            [Console]::Out.WriteLine('{"id":' + $request.id + ',"result":{"threadId":"thread-test"}}')
            while ($null -ne [Console]::ReadLine()) { }
            """;
        await using var client = new CodexAppServerClient(new ScriptProcessFactory(script));
        var received = new TaskCompletionSource<CodexAppServerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += (_, notification) => received.TrySetResult(notification);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await client.StartAsync(new CodexAppServerOptions(), timeout.Token);

        await client.RespondAsync(9, new { decision = "accept" }, timeout.Token);
        var notification = await received.Task.WaitAsync(timeout.Token);
        var thread = await client.StartThreadAsync(cancellationToken: timeout.Token);

        notification.Method.Should().Be("test/response");
        var response = notification.Parameters;
        response.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("jsonrpc", "id", "result");
        response.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        response.GetProperty("id").GetInt64().Should().Be(9);
        response.GetProperty("result").GetProperty("decision").GetString().Should().Be("accept");
        thread.GetProperty("threadId").GetString().Should().Be("thread-test");
    }

    private sealed class ScriptProcessFactory(string script) : ICodexAppServerProcessStartInfoFactory
    {
        public ProcessStartInfo Create(CodexAppServerOptions options)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            return startInfo;
        }
    }
}
