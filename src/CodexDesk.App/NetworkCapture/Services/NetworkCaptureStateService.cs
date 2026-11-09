namespace CodexDesk.Services;

/// <summary>
/// Shares the WebView2 capture stream with Razor pages without exposing native controls.
/// </summary>
public sealed class NetworkCaptureStateService
{
    private const int MaxEntries = 500;
    private readonly object _sync = new();
    private readonly List<NetworkCaptureRow> _entries = [];

    public event Action? Changed;

    public bool IsCapturing { get; private set; }

    public IReadOnlyList<NetworkCaptureRow> Entries
    {
        get
        {
            lock (_sync)
            {
                return _entries.ToList();
            }
        }
    }

    public void SetCapturing(bool isCapturing)
    {
        IsCapturing = isCapturing;
        Changed?.Invoke();
    }

    public void Append(NetworkCaptureRow entry)
    {
        lock (_sync)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(0);
            }
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }

        Changed?.Invoke();
    }
}

/// <summary>A capture row safe to render in the Razor network activity view.</summary>
public sealed record NetworkCaptureRow(
    string Name,
    string Status,
    string Method,
    string Type,
    string Size,
    string Duration,
    string Url)
{
    public bool IsFailure => int.TryParse(Status, out var statusCode) && statusCode >= 400;
}
