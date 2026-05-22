namespace CodexDesk.Services;

internal static class CodexWorkspaceLocator
{
    internal static IEnumerable<string> GetCandidates()
    {
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    internal static string? FindWorkspaceRoot(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
        {
            return null;
        }

        var directory = new DirectoryInfo(candidate);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                Directory.EnumerateFiles(directory.FullName, "*.sln").Any() ||
                Directory.EnumerateFiles(directory.FullName, "*.slnx").Any())
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
