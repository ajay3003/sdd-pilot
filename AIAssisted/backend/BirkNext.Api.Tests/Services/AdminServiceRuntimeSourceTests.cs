using BirkNext.Api.Data;
using BirkNext.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// "Published" is a runtime fact read from the layout; the package mode is a name. A configured name never makes a
/// source run look published, and build/commit/package-root metadata stay absent rather than being invented.
/// </summary>
public sealed class AdminServiceRuntimeSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "birknext-admin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string SourceOutput()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "BirkNext.Api")).FullName;
        File.WriteAllText(Path.Combine(project, "BirkNext.Api.csproj"), "<Project />");
        return Directory.CreateDirectory(Path.Combine(project, "bin", "Debug", "net8.0")).FullName;
    }

    private string PackageOutput() => Directory.CreateDirectory(Path.Combine(_root, "package", "AIAssisted", "backend")).FullName;

    private AdminService Service(string baseDirectory, string? packageMode = null)
    {
        var values = new Dictionary<string, string?> { ["LoggingSettings:LogPath"] = Path.Combine(_root, "logs") };
        if (packageMode is not null) values["RuntimeSettings:PackageMode"] = packageMode;
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        env.SetupGet(e => e.ContentRootPath).Returns(_root);
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        return new AdminService(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), env.Object, db, NullLogger<AdminService>.Instance)
        {
            RuntimeBaseDirectory = baseDirectory,
        };
    }

    [Fact]
    public void DotnetRunIsASourceRun()
    {
        var settings = Service(SourceOutput()).BuildSettings();

        settings.Runtime.RunningFromPublishedArtifact.Should().BeFalse();
        settings.Runtime.PackageMode.Should().Be("Source");
        settings.Application.PackageMode.Should().Be("Source");
    }

    [Fact]
    public void SourceRunHasNoPackagingMetadata_AndNothingIsInvented()
    {
        var settings = Service(SourceOutput()).BuildSettings();

        settings.Application.BuildNumber.Should().BeNull();
        settings.Application.CommitSha.Should().BeNull();
        settings.Runtime.LocalPackageRoot.Should().BeNull();
    }

    [Fact]
    public void PublishedTesterPackageIsPublished()
    {
        var settings = Service(PackageOutput()).BuildSettings();

        settings.Runtime.RunningFromPublishedArtifact.Should().BeTrue();
        settings.Runtime.PackageMode.Should().Be("Tester Package");
    }

    [Fact]
    public void DeployedPackageWithAConfiguredModeIsPublished()
    {
        var settings = Service(PackageOutput(), packageMode: "Deployed").BuildSettings();

        settings.Runtime.RunningFromPublishedArtifact.Should().BeTrue();
        settings.Runtime.PackageMode.Should().Be("Deployed");
    }

    [Fact]
    public void AConfiguredTesterPackageNameDoesNotMakeASourceRunPublished()
    {
        var settings = Service(SourceOutput(), packageMode: "Tester Package").BuildSettings();

        settings.Runtime.PackageMode.Should().Be("Tester Package", "the configured name is the package configuration");
        settings.Runtime.RunningFromPublishedArtifact.Should().BeFalse("the runtime source is still a source build");
    }
}
