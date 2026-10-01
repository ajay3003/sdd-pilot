using System.Text.Json.Nodes;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Source Analysis is an optional Analysis feature, enabled by default — also when an older configuration has no key for it. Feature
/// visibility only hides navigation: saving it writes one flag to appsettings.Local.json and never touches stored source snapshots.
/// </summary>
public sealed class SourceAnalysisFeatureVisibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "birknext-sa-feature-" + Guid.NewGuid().ToString("N"));
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public SourceAnalysisFeatureVisibilityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AdminService Service(Dictionary<string, string?>? values = null)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        env.SetupGet(e => e.ContentRootPath).Returns(_root);
        return new AdminService(new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build(), env.Object, _db, NullLogger<AdminService>.Instance);
    }

    [Fact]
    public void MissingKey_ResolvesToEnabled()
    {
        var service = Service();

        service.BuildFeatureVisibility().SourceAnalysis.Should().BeTrue("an older configuration without the key keeps Source Analysis visible");
        var entry = service.BuildEditableSettings().FeatureVisibility.Core.Single(e => e.Key == "SourceAnalysis");
        (entry.Label, entry.Value, entry.Locked).Should().Be(("Source Analysis", true, false));
    }

    [Fact]
    public void IsListedWithTheOtherOptionalAnalysisFeatures_AfterImplementationTraceability()
    {
        var keys = Service().BuildEditableSettings().FeatureVisibility.Core.Select(e => e.Key).ToList();

        keys.Should().ContainSingle(k => k == "SourceAnalysis");
        keys.IndexOf("SourceAnalysis").Should().Be(keys.IndexOf("ImplementationTraceability") + 1);
        Service().BuildEditableSettings().FeatureVisibility.Advanced.Select(e => e.Key).Should().NotContain("SourceAnalysis");
    }

    [Fact]
    public void ExplicitSettingIsRespected()
    {
        var service = Service(new() { ["FeatureVisibility:SourceAnalysis"] = "false" });

        service.BuildFeatureVisibility().SourceAnalysis.Should().BeFalse();
        service.BuildEditableSettings().FeatureVisibility.Core.Single(e => e.Key == "SourceAnalysis").Value.Should().BeFalse();
    }

    [Fact]
    public void DefaultConfigurationEnablesIt()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BirkNext.Api", "appsettings.json"))) dir = dir.Parent;
        dir.Should().NotBeNull("the backend appsettings.json is found above the test output");
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "BirkNext.Api", "appsettings.json")))!;

        json["FeatureVisibility"]!["SourceAnalysis"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task HidingAndShowingWritesOnlyTheFlag_AndKeepsEverySourceSnapshot()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids)
            _db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = id, EnvironmentId = "dev", IntegrationId = "source-analysis", AnalyzedAt = DateTimeOffset.UtcNow, EvidenceJson = "{\"architecture\":{},\"databaseArchitecture\":{}}" });
        await _db.SaveChangesAsync();
        var before = _db.IqrSourceSnapshots.AsNoTracking().OrderBy(s => s.Id).Select(s => new { s.Id, s.EvidenceJson }).ToList();

        (await Service().SaveSettingsAsync(new SaveSettingsRequest { FeatureVisibility = new() { ["SourceAnalysis"] = false } })).Success.Should().BeTrue();
        var local = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "appsettings.Local.json")))!;
        local["FeatureVisibility"]!["SourceAnalysis"]!.GetValue<bool>().Should().BeFalse();
        _db.IqrSourceSnapshots.AsNoTracking().OrderBy(s => s.Id).Select(s => new { s.Id, s.EvidenceJson }).ToList().Should().BeEquivalentTo(before, "hiding the feature deletes nothing");

        (await Service().SaveSettingsAsync(new SaveSettingsRequest { FeatureVisibility = new() { ["SourceAnalysis"] = true } })).Success.Should().BeTrue();
        JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "appsettings.Local.json")))!["FeatureVisibility"]!["SourceAnalysis"]!.GetValue<bool>().Should().BeTrue();
        _db.IqrSourceSnapshots.AsNoTracking().OrderBy(s => s.Id).Select(s => new { s.Id, s.EvidenceJson }).ToList().Should().BeEquivalentTo(before, "re-enabling needs no re-upload");
    }
}
