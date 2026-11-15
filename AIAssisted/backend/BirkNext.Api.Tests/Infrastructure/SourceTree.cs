namespace BirkNext.Api.Tests.Infrastructure;

/// <summary>
/// Locates repository source files for tests that read source text. Walks up from the test output folder to the first ancestor where the
/// requested file exists — the file itself, not a folder name — so stale or empty folders in the build output (for example an
/// <c>AIAssisted/</c> tree left inside <c>bin/</c> by an earlier run) can never stop the search early.
/// </summary>
public static class SourceTree
{
    /// <param name="repositoryRelativePath">Path from the repository root, e.g. <c>AIAssisted/backend/BirkNext.Api/Program.cs</c>.</param>
    public static string File(string repositoryRelativePath)
    {
        var relative = repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Source file '{repositoryRelativePath}' was not found above {AppContext.BaseDirectory}.");
    }

    public static string ReadAllText(string repositoryRelativePath) => System.IO.File.ReadAllText(File(repositoryRelativePath));
}
