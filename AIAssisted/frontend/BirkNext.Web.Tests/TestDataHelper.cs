using System.IO;

namespace BirkNext.Web.Tests;

/// <summary>
/// Portable test data path resolver.
/// Resolves SampleData paths relative to the repository root in a cross-platform manner.
/// Supports both Windows (C:\...) and Linux (/home/vsts/...) CI environments.
/// </summary>
internal static class TestDataHelper
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    /// <summary>
    /// Resolves a SampleData path relative to the repository root.
    /// Example: ResolveSampleDataPath("autorisasjon", "plan.md")
    /// Returns: {RepositoryRoot}/SampleData/autorisasjon/plan.md
    /// </summary>
    public static string ResolveSampleDataPath(params string[] pathSegments)
    {
        var segments = new[] { RepositoryRoot, "SampleData" }
            .Concat(pathSegments)
            .ToArray();

        return Path.Combine(segments);
    }

    /// <summary>
    /// Finds the repository root by looking for azure-pipelines.yml in ancestor directories.
    /// Works on both Windows and Linux CI environments.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var currentDir = AppContext.BaseDirectory;

        while (!string.IsNullOrEmpty(currentDir))
        {
            var candidate = Path.Combine(currentDir, "azure-pipelines.yml");
            if (File.Exists(candidate))
            {
                return currentDir;
            }

            var parent = Directory.GetParent(currentDir);
            if (parent?.FullName == currentDir)
            {
                // Reached filesystem root
                break;
            }

            currentDir = parent?.FullName ?? string.Empty;
        }

        throw new InvalidOperationException(
            $"Could not find repository root starting from {AppContext.BaseDirectory}. " +
            "Expected azure-pipelines.yml in ancestor directories.");
    }
}
