using System.Text.Json;
using System.Reflection;
using CodexDesk.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using CodexDesk.AppServer;
using CodexDesk.Models;
using CodexDesk.Services;
using FluentAssertions;

namespace CodexDesk.Tests.Codex.AppServer;

[TestClass]
public sealed class CodexAppServerChatServiceTests
{
    [TestMethod]
    public async Task ResolveApprovalAsync_rejection_from_the_chat_modal_should_decline_without_starting_another_turn()
    {
        var client = new FakeClient();
        using var service = new CodexAppServerChatService(client);
        using var services = ChatServices(service);
        await using var renderer = new ChatRenderer(services);
        var home = await renderer.MountAsync();
        var tab = GetProperty(home, "ActiveTab");
        SetProperty(tab, "ThreadId", "thread-1");
        SetProperty(tab, "IsBusy", true);

        client.Request(42, "item/commandExecution/requestApproval", new { threadId = "thread-1", turnId = "turn-1", reason = "Run validation", command = "dotnet test" });
        await renderer.Dispatcher.InvokeAsync(() => { });
        GetProperty(tab, "IsApprovalModalOpen").Should().Be(true);

        await renderer.Dispatcher.InvokeAsync(() => InvokeHomeAsync(home, "RejectApprovalAsync"));

        client.Responses.Should().ContainSingle();
        client.Responses[0].Id.Should().Be(42);
        client.Responses[0].Result.GetRawText().Should().Be("{\"decision\":\"decline\"}");
        client.Calls.Should().BeEmpty("rejecting an approval must not start a chat turn or process");
        GetProperty(tab, "IsBusy").Should().Be(true, "the original turn continues until a terminal event");
        GetProperty(tab, "IsApprovalModalOpen").Should().Be(false);
    }

    [TestMethod]
    public async Task Chat_send_should_stream_into_one_message_and_stop_through_the_existing_thread()
    {
        var client = new FakeClient();
        using var service = new CodexAppServerChatService(client);
        using var services = ChatServices(service);
        await using var renderer = new ChatRenderer(services);
        var home = await renderer.MountAsync();
        var tab = GetProperty(home, "ActiveTab");
        SetProperty(tab, "DraftMessage", "Hello");
        await renderer.Dispatcher.InvokeAsync(() => InvokeHomeAsync(home, "SendAsync"));

        client.Calls.Should().Contain("turn/start:thread-new:Hello");
        GetProperty(tab, "IsBusy").Should().Be(true);
        client.Notify("item/agentMessage/delta", new { threadId = "thread-new", turnId = "turn-new", itemId = "message-1", delta = "Hello " });
        client.Notify("item/agentMessage/delta", new { threadId = "thread-new", turnId = "turn-new", itemId = "message-1", delta = "world" });
        await renderer.Dispatcher.InvokeAsync(() => { });

        var messages = (IList<ChatMessage>)GetProperty(tab, "Messages");
        messages.Where(m => m.Role == "assistant").Should().ContainSingle().Which.Content.Should().Be("Hello world");
        await renderer.Dispatcher.InvokeAsync(() => InvokeHomeAsync(home, "CancelRun"));
        client.Calls.Last().Should().Be("turn/interrupt:thread-new");
        GetProperty(tab, "IsBusy").Should().Be(true, "Stop waits for server confirmation");
        client.Notify("turn/completed", new { threadId = "thread-new", turn = new { id = "turn-new", status = "interrupted" } });
        await renderer.Dispatcher.InvokeAsync(() => { });

        GetProperty(tab, "IsBusy").Should().Be(false);
        messages.Should().Contain(m => m.Role == "system" && m.Content.Contains("interrupted"));
    }

    [TestMethod]
    public async Task Chat_approval_acceptance_and_failure_should_update_the_modal_and_transcript()
    {
        var client = new FakeClient();
        using var service = new CodexAppServerChatService(client);
        using var services = ChatServices(service);
        await using var renderer = new ChatRenderer(services);
        var home = await renderer.MountAsync();
        var tab = GetProperty(home, "ActiveTab");
        SetProperty(tab, "DraftMessage", "Hello");
        await renderer.Dispatcher.InvokeAsync(() => InvokeHomeAsync(home, "SendAsync"));
        client.Request(99, "item/fileChange/requestApproval", new { threadId = "thread-new", turnId = "turn-new", itemId = "file-1", reason = "Edit source" });
        await renderer.Dispatcher.InvokeAsync(() => { });
        GetProperty(tab, "IsApprovalModalOpen").Should().Be(true);
        await renderer.Dispatcher.InvokeAsync(() => InvokeHomeAsync(home, "SelectApprovalOptionAsync", "accept"));

        client.Responses.Should().ContainSingle();
        client.Responses[0].Result.GetProperty("decision").GetString().Should().Be("accept");
        GetProperty(tab, "IsApprovalModalOpen").Should().Be(false);
        client.Notify("turn/completed", new { threadId = "thread-new", turn = new { id = "turn-new", status = "failed", error = new { message = "Model unavailable" } } });
        await renderer.Dispatcher.InvokeAsync(() => { });

        GetProperty(tab, "IsBusy").Should().Be(false);
        ((IList<ChatMessage>)GetProperty(tab, "Messages")).Should().Contain(m => m.IsError && m.Content == "Model unavailable");
        client.Calls.Count(c => c.StartsWith("turn/start:")).Should().Be(1);
    }

    [TestMethod]
    public async Task New_requests_should_start_one_process_and_keep_threads_available_for_follow_up_turns()
    {
        var client = new FakeClient();
        using var service = new CodexAppServerChatService(client);
        var events = Collect(service);

        var threadId = await service.StartTurnAsync(Request());
        await service.StartTurnAsync(Request(threadId));

        threadId.Should().Be("thread-new");
        client.Calls.Should().Equal("process/start", "thread/start:gpt-test", "turn/start:thread-new:Hello", "turn/start:thread-new:Hello");
        client.Options!.CommandPath.Should().Be("codex-test");
        client.Options.WorkspacePath.Should().Be("D:\\workspace");
        events.Should().Contain(e => e.Kind == CodexAppServerChatEventKind.ThreadReady && e.ThreadId == "thread-new");
        events.Should().Contain(e => e.Kind == CodexAppServerChatEventKind.TurnStarted && e.TurnId == "turn-new");
    }

    [TestMethod]
    public async Task Saved_threads_should_resume_before_sending_the_prompt()
    {
        var client = new FakeClient();
        using var service = new CodexAppServerChatService(client);

        var threadId = await service.StartTurnAsync(Request("thread-saved"));

        threadId.Should().Be("thread-saved");
        client.Calls.Should().Equal("process/start", "thread/resume:thread-saved", "turn/start:thread-saved:Hello");
    }

    [TestMethod]
    [DataRow("item/agentMessage/delta")]
    [DataRow("agentMessage/delta")]
    public void Assistant_deltas_should_retain_thread_turn_and_incremental_text(string method)
    {
        var client = new FakeClient();
        using var service = new CodexAppServerChatService(client);
        var events = Collect(service);

        client.Notify(method, new { threadId = "thread-1", turnId = "turn-1", itemId = "message-1", delta = "Hello " });
        client.Notify(method, new { threadId = "thread-1", turnId = "turn-1", itemId = "message-1", delta = "world" });

        events.Select(e => e.Text).Should().Equal("Hello ", "world");
        events.Should().OnlyContain(e => e.Kind == CodexAppServerChatEventKind.AssistantDelta && e.ThreadId == "thread-1" && e.TurnId == "turn-1");
    }

    [TestMethod]
    [DataRow("completed", CodexAppServerChatEventKind.TurnCompleted)]
    [DataRow("failed", CodexAppServerChatEventKind.TurnFailed)]
    [DataRow("interrupted", CodexAppServerChatEventKind.TurnInterrupted)]
    public void Terminal_turn_status_should_be_reported_to_the_ui(string status, CodexAppServerChatEventKind expected)
    {
        var client = new FakeClient();
        using var service = new CodexAppServerChatService(client);
        var events = Collect(service);

        client.Notify("turn/completed", new { threadId = "thread-1", turn = new { id = "turn-1", status, error = status == "failed" ? new { message = "Model unavailable" } : null } });

        events.Should().ContainSingle().Which.Should().BeEquivalentTo(new CodexAppServerChatEvent(expected, "thread-1", "turn-1", status == "failed" ? "Model unavailable" : null));
    }

    [TestMethod]
    public async Task Interrupt_should_target_the_existing_thread_without_starting_another_process()
    {
        var client = new FakeClient();
        using var service = new CodexAppServerChatService(client);
        await service.StartTurnAsync(Request());

        await service.InterruptAsync("thread-new");

        client.Calls.Should().Equal("process/start", "thread/start:gpt-test", "turn/start:thread-new:Hello", "turn/interrupt:thread-new");
    }

    [TestMethod]
    [DataRow("commandExecution/requestApproval", CodexAppServerApprovalKind.CommandExecution)]
    [DataRow("item/commandExecution/requestApproval", CodexAppServerApprovalKind.CommandExecution)]
    [DataRow("fileChange/requestApproval", CodexAppServerApprovalKind.FileChange)]
    [DataRow("item/fileChange/requestApproval", CodexAppServerApprovalKind.FileChange)]
    public void Approval_requests_should_expose_the_original_id_and_modal_details(string method, CodexAppServerApprovalKind kind)
    {
        var client = new FakeClient();
        using var service = new CodexAppServerChatService(client);
        var events = Collect(service);

        client.Request(42, method, new { threadId = "thread-1", turnId = "turn-1", itemId = "item-1", reason = "Run validation", command = "dotnet test", cwd = "D:\\workspace", grantRoot = "D:\\workspace" });

        var notification = events.Should().ContainSingle().Subject;
        notification.Kind.Should().Be(CodexAppServerChatEventKind.ApprovalRequired);
        var approval = notification.Approval!;
        approval.Id.Should().Be("42");
        approval.RequestId.Should().Be(42);
        approval.Kind.Should().Be(kind);
        approval.ThreadId.Should().Be("thread-1");
        approval.TurnId.Should().Be("turn-1");
        approval.ItemId.Should().Be("item-1");
        approval.Reason.Should().Be("Run validation");
        approval.Command.Should().Be("dotnet test");
        approval.WorkingDirectory.Should().Be("D:\\workspace");
        approval.GrantRoot.Should().Be("D:\\workspace");
    }

    [TestMethod]
    [DataRow("accept", "accept")]
    [DataRow("reject", "decline")]
    [DataRow("decline", "decline")]
    public async Task Approval_decisions_should_answer_the_original_server_request(string decision, string expected)
    {
        var client = new FakeClient();
        using var service = new CodexAppServerChatService(client);
        var events = Collect(service);
        client.Request(42, "fileChange/requestApproval", new { threadId = "thread-1", turnId = "turn-1", itemId = "item-1" });

        await service.ResolveApprovalAsync("42", decision);

        client.Responses.Should().ContainSingle();
        client.Responses[0].Id.Should().Be(42);
        client.Responses[0].Result.GetProperty("decision").GetString().Should().Be(expected);
        events.Last().Kind.Should().Be(CodexAppServerChatEventKind.ApprovalResolved);
        events.Last().Approval!.Id.Should().Be("42");
        await service.Invoking(s => s.ResolveApprovalAsync("42", decision)).Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    public async Task A_failed_approval_response_should_remain_available_to_retry()
    {
        var client = new FakeClient { ResponseFailure = new IOException("Write failed") };
        using var service = new CodexAppServerChatService(client);
        var events = Collect(service);
        client.Request(42, "commandExecution/requestApproval", new { threadId = "thread-1", turnId = "turn-1" });

        await service.Invoking(s => s.ResolveApprovalAsync("42", "accept")).Should().ThrowAsync<IOException>();
        events.Should().NotContain(e => e.Kind == CodexAppServerChatEventKind.ApprovalResolved);
        client.ResponseFailure = null;
        await service.ResolveApprovalAsync("42", "accept");

        events.Last().Kind.Should().Be(CodexAppServerChatEventKind.ApprovalResolved);
    }

    [TestMethod]
    public async Task Startup_or_turn_failures_should_be_reported_and_propagated()
    {
        var client = new FakeClient { TurnFailure = new InvalidOperationException("Server refused turn") };
        using var service = new CodexAppServerChatService(client);
        var events = Collect(service);

        await service.Invoking(s => s.StartTurnAsync(Request())).Should().ThrowAsync<InvalidOperationException>();

        events.Last().Should().BeEquivalentTo(new CodexAppServerChatEvent(CodexAppServerChatEventKind.TurnFailed, "thread-new", Text: "Server refused turn"));
    }

    [TestMethod]
    public void Dispose_should_unsubscribe_without_disposing_the_shared_client()
    {
        var client = new FakeClient();
        var service = new CodexAppServerChatService(client);
        var events = Collect(service);

        service.Dispose();
        client.Notify("agentMessage/delta", new { threadId = "thread-1", delta = "late" });

        events.Should().BeEmpty();
        client.Disposed.Should().BeFalse();
    }

    private static CodexChatRequest Request(string? threadId = null) => new("Hello", "D:\\workspace", "codex-test", "gpt-test", threadId, true);

    private static object GetProperty(object target, string name) => target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static Task InvokeHomeAsync(Home home, string name, params object[] args) => (Task)typeof(Home).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(home, args)!;

    private static ServiceProvider ChatServices(CodexAppServerChatService service) => new ServiceCollection().AddLogging()
        .AddSingleton(service).AddSingleton<CodexInteractiveChatService>()
        .AddSingleton<CodexPromptMacroService>().AddSingleton<BrowserStateService>()
        .AddSingleton<IJSRuntime, UnusedJsRuntime>().BuildServiceProvider();

#pragma warning disable BL0006 // Test renderer hosts the real Razor component without a native MAUI window.
    private sealed class ChatRenderer(IServiceProvider services) : Renderer(services, services.GetRequiredService<ILoggerFactory>())
    {
        public override Microsoft.AspNetCore.Components.Dispatcher Dispatcher { get; } = Microsoft.AspNetCore.Components.Dispatcher.CreateDefault();
        protected override void HandleException(Exception exception) => throw exception;
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        public Task<Home> MountAsync() => Dispatcher.InvokeAsync(async () =>
        {
            var home = (Home)InstantiateComponent(typeof(Home));
            await RenderRootComponentAsync(AssignRootComponentId(home));
            return home;
        });
    }
#pragma warning restore BL0006

    private sealed class UnusedJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }

    private static List<CodexAppServerChatEvent> Collect(CodexAppServerChatService service)
    {
        var events = new List<CodexAppServerChatEvent>();
        service.EventReceived += (_, notification) => events.Add(notification);
        return events;
    }

    private sealed class FakeClient : ICodexAppServerClient
    {
        public event EventHandler<CodexAppServerEvent>? EventReceived;
        public event EventHandler<CodexAppServerServerRequest>? ServerRequestReceived;
        public bool IsRunning { get; private set; }
        public bool Disposed { get; private set; }
        public CodexAppServerOptions? Options { get; private set; }
        public List<string> Calls { get; } = [];
        public List<(long Id, JsonElement Result)> Responses { get; } = [];
        public Exception? TurnFailure { get; set; }
        public Exception? ResponseFailure { get; set; }

        public Task StartAsync(CodexAppServerOptions options, CancellationToken cancellationToken = default)
        {
            Calls.Add("process/start");
            Options = options;
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task<JsonElement> StartThreadAsync(string? model = null, CancellationToken cancellationToken = default)
        {
            Calls.Add($"thread/start:{model}");
            return Task.FromResult(JsonSerializer.SerializeToElement(new { thread = new { id = "thread-new" } }));
        }

        public Task<JsonElement> ResumeThreadAsync(string threadId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"thread/resume:{threadId}");
            return Task.FromResult(JsonSerializer.SerializeToElement(new { thread = new { id = threadId } }));
        }

        public Task<JsonElement> StartTurnAsync(string threadId, string message, CancellationToken cancellationToken = default)
        {
            Calls.Add($"turn/start:{threadId}:{message}");
            return TurnFailure is null
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { turn = new { id = "turn-new", status = "inProgress" } }))
                : Task.FromException<JsonElement>(TurnFailure);
        }

        public Task InterruptTurnAsync(string threadId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"turn/interrupt:{threadId}");
            return Task.CompletedTask;
        }

        public Task RespondAsync(long requestId, object result, CancellationToken cancellationToken = default)
        {
            if (ResponseFailure is not null) return Task.FromException(ResponseFailure);
            Responses.Add((requestId, JsonSerializer.SerializeToElement(result)));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public void Notify(string method, object payload) => EventReceived?.Invoke(this, new(method, JsonSerializer.SerializeToElement(payload)));
        public void Request(long id, string method, object payload) => ServerRequestReceived?.Invoke(this, new(id, method, JsonSerializer.SerializeToElement(payload)));
    }
}
