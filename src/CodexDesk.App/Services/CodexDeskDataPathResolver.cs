namespace CodexDesk.Services;

internal static class CodexDeskDataPathResolver
{
    private const string AppDataDirectoryName = "CodexDesk";

    private static readonly string[] ProjectFileNames =
    [
        "CodexDesk.App.csproj",
        "CodexDesk.csproj"
    ];

    internal static string ResolveDataPath(string fileName, string baseDirectory, string? localAppDataRoot = null)
    {
        if (Path.IsPathRooted(fileName))
        {
            return fileName;
        }

        var normalizedBaseDirectory = NormalizeBaseDirectory(baseDirectory);
#if DEBUG
        var developmentPath = TryResolveDevelopmentPath(fileName, normalizedBaseDirectory);
        if (!string.IsNullOrWhiteSpace(developmentPath))
        {
            return developmentPath;
        }
#endif

        return ResolveInstalledDataPath(fileName, normalizedBaseDirectory, localAppDataRoot);
    }

    private static string ResolveInstalledDataPath(string fileName, string baseDirectory, string? localAppDataRoot)
    {
        var appDataDirectory = GetAppDataDirectory(localAppDataRoot);
        var targetPath = Path.Combine(appDataDirectory, fileName);

        EnsureParentDirectory(targetPath);
        SeedPackagedFileIfMissing(fileName, baseDirectory, targetPath);
        return targetPath;
    }

    private static string? TryResolveDevelopmentPath(string fileName, string baseDirectory)
    {
        var projectRoot = TryFindProjectRoot(baseDirectory);
        return string.IsNullOrWhiteSpace(projectRoot)
            ? null
            : Path.Combine(projectRoot, fileName);
    }

    internal static string? TryFindProjectRoot(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        var current = new DirectoryInfo(NormalizeBaseDirectory(startDirectory));
        while (current is not null)
        {
            if (ProjectFileNames.Any(projectFileName => File.Exists(Path.Combine(current.FullName, projectFileName))))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string GetAppDataDirectory(string? localAppDataRoot)
    {
        var root = string.IsNullOrWhiteSpace(localAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataRoot;

        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetTempPath();
        }

        var directory = Path.Combine(root, AppDataDirectoryName);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void SeedPackagedFileIfMissing(string fileName, string baseDirectory, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            return;
        }

        var packagedPath = Path.Combine(baseDirectory, fileName);
        if (!File.Exists(packagedPath))
        {
            return;
        }

        File.Copy(packagedPath, targetPath, overwrite: false);
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string NormalizeBaseDirectory(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return Path.GetFullPath(AppContext.BaseDirectory);
        }

        return Path.GetFullPath(baseDirectory);
    }
}
