namespace BirkNext.Api.Tests.TestInfrastructure;

/// <summary>
/// Gate for acceptance tests that read a real, developer-owned project archive (for example an M2LB export).
///
/// Those archives are not in the repository and are replaced whenever a newer export is downloaded, so they can never be part of the
/// mandatory regression suite: every behaviour they check is also covered by a synthetic fixture the repository owns. They used to look in
/// the developer's Downloads folder and return early (a silent pass) when the file was absent, and to run against whatever file had that
/// name otherwise. Now they run only on explicit opt-in: <see cref="EnvironmentVariableName"/> names a folder holding the archives. When the
/// folder or an archive is missing the test is reported as <b>skipped with the reason</b>, never as a pass.
///
/// Run on demand:
/// <code>
/// set BIRKNEXT_REAL_ARCHIVE_DIR=C:\path\to\archives
/// dotnet test BirkNext.Api.Tests --filter "Category=RealArchive"
/// </code>
/// </summary>
public static class RealArchiveTestGate
{
    public const string EnvironmentVariableName = "BIRKNEXT_REAL_ARCHIVE_DIR";
    public const string Category = "RealArchive";

    public static string? Directory =>
        Environment.GetEnvironmentVariable(EnvironmentVariableName) is { Length: > 0 } value ? value.Trim().Trim('"') : null;

    /// <summary>The archive's path in the opt-in folder, or null when the folder is not set or the file is not in it.</summary>
    public static string? Path(string fileName) =>
        Directory is { } directory && File.Exists(System.IO.Path.Combine(directory, fileName)) ? System.IO.Path.Combine(directory, fileName) : null;

    /// <summary>Null when every archive is available; otherwise why the test cannot run.</summary>
    public static string? SkipReason(IReadOnlyCollection<string> fileNames)
    {
        if (Directory is null)
            return $"Real-archive acceptance test: set {EnvironmentVariableName} to a folder containing {string.Join(", ", fileNames)}. " +
                   "Mandatory coverage of this behaviour is the synthetic-fixture tests.";
        var missing = fileNames.Where(name => Path(name) is null).ToList();
        return missing.Count == 0 ? null : $"Real-archive acceptance test: {string.Join(", ", missing)} not found in {EnvironmentVariableName}.";
    }
}

/// <summary>xUnit fact that runs only when <see cref="RealArchiveTestGate"/> can supply every named archive; otherwise it is skipped with the reason.
/// Tests using it also carry <c>[Trait("Category", RealArchiveTestGate.Category)]</c>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RealArchiveFactAttribute : FactAttribute
{
    public RealArchiveFactAttribute(params string[] fileNames)
    {
        if (RealArchiveTestGate.SkipReason(fileNames) is { } reason) Skip = reason;
    }
}
