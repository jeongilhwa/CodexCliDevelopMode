namespace CodexDesk.Services;

/// <summary>Coordinates workspace requests raised by the Blazor shell and handled by the native MAUI host.</summary>
public sealed class WorkspaceNavigationService
{
    /// <summary>Gets the workspace most recently requested by the shell.</summary>
    public WorkspaceDestination CurrentDestination { get; private set; } = WorkspaceDestination.Codex;

    /// <summary>Occurs when the Blazor shell requests a native workspace panel.</summary>
    public event EventHandler<WorkspaceDestination>? DestinationRequested;

    /// <summary>Occurs when the sidebar requests navigation in the main Blazor content view.</summary>
    public event EventHandler<string>? ContentRouteRequested;

    /// <summary>Requests that the native host display a workspace destination.</summary>
    /// <param name="destination">The destination to display.</param>
    public void Request(WorkspaceDestination destination)
    {
        CurrentDestination = destination;
        DestinationRequested?.Invoke(this, destination);
    }

    /// <summary>Requests a destination and navigation in the separate main BlazorWebView.</summary>
    /// <param name="destination">The workspace destination to display.</param>
    /// <param name="route">The route to load in the main content view.</param>
    public void RequestContentRoute(WorkspaceDestination destination, string route)
    {
        Request(destination);
        ContentRouteRequested?.Invoke(this, route);
    }
}

/// <summary>Native workspace panels available from the Blazor shell.</summary>
public enum WorkspaceDestination
{
    /// <summary>The Blazor Codex workspace.</summary>
    Codex,

    /// <summary>The native WebView2 browser workspace.</summary>
    Browser,

    /// <summary>The native network-capture workspace.</summary>
    NetworkCaptures
}
