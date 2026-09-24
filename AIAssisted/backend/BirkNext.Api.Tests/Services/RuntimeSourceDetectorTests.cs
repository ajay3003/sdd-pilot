using BirkNext.Api.Services;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Source builds run from MSBuild's <c>bin/</c> output beside the project file; everything else is a published
/// artifact. The old rule searched below the base directory for a .csproj, which a build output never contains, so
/// <c>dotnet run</c> was reported as a published Tester Package.
/// </summary>
public sealed class RuntimeSourceDetectorTests : IDisposable
{
    private const string Project = "BirkNext.Api.csproj";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "birknext-runtime-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private void ProjectFileIn(params string[] parts) => File.WriteAllText(Path.Combine(Dir(parts), Project), "<Project />");

    [Fact]
    public void DotnetRunOutputIsASourceBuild()
    {
        ProjectFileIn("BirkNext.Api");
        RuntimeSourceDetector.IsSourceBuild(Dir("BirkNext.Api", "bin", "Debug", "net8.0"), Project).Should().BeTrue();
    }

    [Fact]
    public void CustomOutputFolderBelowBinIsASourceBuild()
    {
        ProjectFileIn("BirkNext.Api");
        RuntimeSourceDetector.IsSourceBuild(Dir("BirkNext.Api", "bin", "apiqr", "Release", "net8.0"), Project).Should().BeTrue();
    }

    [Fact]
    public void PublishedTesterPackageIsNotASourceBuild()
    {
        var backend = Dir("BirkNext-Tester-Package", "AIAssisted", "backend");
        File.WriteAllText(Path.Combine(backend, "BirkNext.Api.dll"), "");
        RuntimeSourceDetector.IsSourceBuild(backend, Project).Should().BeFalse();
    }

    [Fact]
    public void DeployedFolderIsNotASourceBuild()
    {
        RuntimeSourceDetector.IsSourceBuild(Dir("srv", "birknext", "api"), Project).Should().BeFalse();
    }

    [Fact]
    public void PublishOutputInsideTheSourceTreeIsStillPublished()
    {
        ProjectFileIn("BirkNext.Api");
        RuntimeSourceDetector.IsSourceBuild(Dir("BirkNext.Api", "bin", "Release", "net8.0", "publish"), Project).Should().BeFalse();
    }

    [Fact]
    public void ABinFolderWithoutThisProjectIsNotASourceBuild()
    {
        ProjectFileIn("Other");
        File.Move(Path.Combine(_root, "Other", Project), Path.Combine(_root, "Other", "Other.csproj"));
        RuntimeSourceDetector.IsSourceBuild(Dir("Other", "bin", "Debug", "net8.0"), Project).Should().BeFalse();
    }

    [Fact]
    public void TheRunningTestHostIsASourceBuild()
    {
        RuntimeSourceDetector.IsSourceBuild(AppContext.BaseDirectory, "BirkNext.Api.Tests.csproj").Should().BeTrue();
    }
}
