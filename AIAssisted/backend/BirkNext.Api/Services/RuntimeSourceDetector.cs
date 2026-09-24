namespace BirkNext.Api.Services;

/// <summary>
/// Whether the running backend is a source build (<c>dotnet run</c> / <c>dotnet build</c>) or a published artifact.
/// A source build runs from MSBuild's output layout: a folder below <c>bin/</c> whose parent holds the project file.
/// Anything else — a tester package, a deployed folder, or <c>bin/…/publish</c> — is a published artifact.
/// </summary>
public static class RuntimeSourceDetector
{
    public static bool IsSourceBuild(string baseDirectory, string projectFileName)
    {
        try
        {
            var dir = new DirectoryInfo(baseDirectory);
            if (dir.Name.Equals("publish", StringComparison.OrdinalIgnoreCase))
                return false;

            for (var current = dir; current is not null; current = current.Parent)
            {
                if (!current.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) || current.Parent is null)
                    continue;
                if (File.Exists(System.IO.Path.Combine(current.Parent.FullName, projectFileName)))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
