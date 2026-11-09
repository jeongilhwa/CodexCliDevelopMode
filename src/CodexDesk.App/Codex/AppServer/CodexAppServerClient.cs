using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexDesk.AppServer;

/// <summary>Owns one local Codex app-server process and its stdio JSON-RPC connection.</summary>
public sealed class CodexAppServerClient(ICodexAppServerProcessStartInfoFactory startInfoFactory) : ICodexAppServerClient
{
    private readonly ICodexAppServerProcessStartInfoFactory _startInfoFactory = startInfoFactory ?? throw new ArgumentNullException(nameof(startInfoFactory));
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pendingRequests = [];
    private Process? _process;
    private CancellationTokenSource? _readerCancellation;
    private long _nextRequestId;

    /// <inheritdoc />
    public event EventHandler<CodexAppServerEvent>? EventReceived;

    /// <inheritdoc />
    public event EventHandler<CodexAppServerServerRequest>? ServerRequestReceived;

    /// <inheritdoc />
    public Task RespondAsync(long requestId, object result, CancellationToken cancellationToken = default) =>
        SendAsync(new { jsonrpc = "2.0", id = requestId, result }, cancellationToken);

    /// <inheritdoc />
    public bool IsRunning => _process is { HasExited: false };

    /// <inheritdoc />
    public async Task StartAsync(CodexAppServerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfRunning();

        var process = new Process { StartInfo = _startInfoFactory.Create(options), EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Codex app-server could not be started.");
        }

        _process = process;
        _readerCancellation = new CancellationTokenSource();
        _ = ReadOutputAsync(process, _readerCancellation.Token);
        _ = ReadErrorAsync(process, _readerCancellation.Token);

        try
        {
            await SendRequestAsync("initialize", new
            {
                clientInfo = new { name = options.ClientName, title = options.ClientTitle, version = options.ClientVersion },                                
                capabilities = new { }
            }, cancellationToken).ConfigureAwait(false);
            await SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<JsonElement> StartThreadAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        return SendRequestAsync("thread/start", string.IsNullOrWhiteSpace(model) ? new { } : new { model }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<JsonElement> ResumeThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return SendRequestAsync("thread/resume", new { threadId }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<JsonElement> StartTurnAsync(string threadId, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return SendRequestAsync("turn/start", new { threadId, input = new[] { new { type = "text", text = message } } }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task InterruptTurnAsync(string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        await SendRequestAsync("turn/interrupt", new { threadId }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _readerCancellation?.Cancel();
        _readerCancellation?.Dispose();
        _readerCancellation = null;

        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                process.Dispose();
                _process = null;
            }
        }

        FailPendingRequests(new ObjectDisposedException(nameof(CodexAppServerClient)));
        _writeLock.Dispose();
    }

    private async Task<JsonElement> SendRequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingRequests)
        {
            _pendingRequests.Add(requestId, completion);
        }

        try
        {
            await SendAsync(new { jsonrpc = "2.0", id = requestId, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingRequests)
            {
                _pendingRequests.Remove(requestId);
            }
        }
    }

    private Task SendNotificationAsync(string method, object parameters, CancellationToken cancellationToken) =>
        SendAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            throw new InvalidOperationException("Codex app-server is not running.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadOutputAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                HandleServerMessage(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailPendingRequests(exception);
        }
    }

    private async Task ReadErrorAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                EventReceived?.Invoke(this, new CodexAppServerEvent("server/stderr", JsonSerializer.SerializeToElement(new { message = line })));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void HandleServerMessage(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var hasRequestId = root.TryGetProperty("id", out var id) && id.TryGetInt64(out _);
            if (root.TryGetProperty("method", out var method))
            {
                var parameters = root.TryGetProperty("params", out var value) ? value.Clone() : JsonSerializer.SerializeToElement(new { });
                if (hasRequestId)
                {
                    ServerRequestReceived?.Invoke(this, new CodexAppServerServerRequest(id.GetInt64(), method.GetString() ?? "unknown", parameters));
                }
                else
                {
                    EventReceived?.Invoke(this, new CodexAppServerEvent(method.GetString() ?? "unknown", parameters));
                }

                return;
            }

            if (hasRequestId)
            {
                HandleResponse(id.GetInt64(), root);
            }
        }
        catch (JsonException exception)
        {
            FailPendingRequests(new InvalidOperationException("Codex app-server emitted invalid JSON-RPC.", exception));
        }
    }

    private void HandleResponse(long requestId, JsonElement response)
    {
        TaskCompletionSource<JsonElement>? completion;
        lock (_pendingRequests)
        {
            _pendingRequests.TryGetValue(requestId, out completion);
        }

        if (completion is null)
        {
            return;
        }

        if (response.TryGetProperty("error", out var error))
        {
            completion.TrySetException(new InvalidOperationException(error.GetRawText()));
            return;
        }

        completion.TrySetResult(response.TryGetProperty("result", out var result) ? result.Clone() : JsonSerializer.SerializeToElement(new { }));
    }

    private void FailPendingRequests(Exception exception)
    {
        TaskCompletionSource<JsonElement>[] pending;
        lock (_pendingRequests)
        {
            pending = _pendingRequests.Values.ToArray();
            _pendingRequests.Clear();
        }

        foreach (var request in pending)
        {
            request.TrySetException(exception);
        }
    }

    private void ThrowIfRunning()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Codex app-server is already running.");
        }
    }
}
