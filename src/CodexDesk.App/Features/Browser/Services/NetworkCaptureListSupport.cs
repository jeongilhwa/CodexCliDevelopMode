namespace CodexDesk.Services;

internal static class NetworkCaptureListSupport
{
    public static void AppendEntry<T>(IList<T> entries, T entry, int maxEntries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        entries.Add(entry);

        while (entries.Count > maxEntries)
        {
            entries.RemoveAt(0);
        }
    }

    public static bool IsPinnedToBottom(
        int lastVisibleItemIndex,
        int itemCount,
        int trailingItemThreshold = 1)
    {
        if (itemCount <= 0)
        {
            return true;
        }

        if (lastVisibleItemIndex < 0)
        {
            return false;
        }

        var threshold = Math.Max(0, trailingItemThreshold);
        return lastVisibleItemIndex >= itemCount - 1 - threshold;
    }

    public static T? ResolveAutoSelectedEntryAfterAppend<T>(IReadOnlyList<T> visibleEntries)
        where T : class
    {
        return visibleEntries.LastOrDefault();
    }
}
