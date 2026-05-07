namespace CodexDesk.Services;

internal sealed record NetworkCaptureSelectionResolution<T>(
    T? SelectedEntry,
    bool KeepCurrentDetails)
    where T : class;

internal static class NetworkCaptureSelectionSupport
{
    public static NetworkCaptureSelectionResolution<T> ResolveSelectionAfterRefresh<T>(
        T? currentControlSelection,
        T? rememberedSelection,
        IReadOnlyList<T> visibleEntries,
        bool preserveSelection)
        where T : class
    {
        if (preserveSelection)
        {
            if (rememberedSelection is not null && visibleEntries.Contains(rememberedSelection))
            {
                return new NetworkCaptureSelectionResolution<T>(rememberedSelection, KeepCurrentDetails: true);
            }

            if (currentControlSelection is not null && visibleEntries.Contains(currentControlSelection))
            {
                return new NetworkCaptureSelectionResolution<T>(currentControlSelection, KeepCurrentDetails: true);
            }

            if (rememberedSelection is not null || currentControlSelection is not null)
            {
                return new NetworkCaptureSelectionResolution<T>(null, KeepCurrentDetails: true);
            }
        }

        return new NetworkCaptureSelectionResolution<T>(
            visibleEntries.FirstOrDefault(),
            KeepCurrentDetails: false);
    }
}
