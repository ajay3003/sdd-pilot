using System.IO.Compression;
using System.Text.Json;
using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.PipelineReview;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Models;
using BirkNext.Integrations;
using BirkNext.PipelineReview;
using BirkNext.Technology;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace BirkNext.Api.Tests.Services.PipelineReviewFreshness;

/// <summary>
/// Outdated CI/CD evidence is "needs refresh", not "no pipelines": the coverage endpoint reports it for the latest snapshot so the shared
/// evaluator (sidebar) and Pipeline Review (page) agree. Legacy snapshots without a CI/CD domain, and v1 evidence with pipelines, are outdated.
/// </summary>
public sealed class PipelineEvidenceFreshnessTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public void Dispose() => _db.Dispose();

    private static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files) { using var w = new StreamWriter(zip.CreateEntry(path).Open()); w.Write(content); }
        return buffer.ToArray();
    }

    private static readonly byte[] WithPipeline = Zip(
        ("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"),
        ("App/Program.cs", "class Program { static void Main() {} }"),
        ("azure-pipelines.yml", "trigger:\n  branches:\n    include: [main]\npool:\n  vmImage: ubuntu-latest\nsteps:\n- script: dotnet build\n  displayName: Build\n- script: dotnet test\n  displayName: Unit tests\n"));

    private static readonly byte[] WithoutPipeline = Zip(("App/Program.cs", "class Program { static void Main() {} }"));

    private async Task<IqrSourceSnapshot> Analyze(byte[] bytes)
    {
        var (snapshot, error) = await new IqrSourceStore(_db).AnalyzeAsync("env", IqrSourceStore.SourceAnalysisOwner, "app.zip", bytes);
        error.Should().BeNull();
        return snapshot!;
    }

    /// <summary>Rewrites the stored snapshot, as an older analyzer would have stored it.</summary>
    private async Task Rewrite(IqrSourceSnapshot snapshot)
    {
        var record = await _db.IqrSourceSnapshots.SingleAsync(r => r.Id == snapshot.Id);
        _db.IqrSourceSnapshots.Remove(record);
        await _db.SaveChangesAsync();
        _db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = record.Id, EnvironmentId = record.EnvironmentId, IntegrationId = record.IntegrationId,
            AnalyzedAt = snapshot.AnalyzedAt, EvidenceJson = JsonSerializer.Serialize(snapshot, Json) });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<ProjectTechnologyCoverage> Coverage()
    {
        var catalog = new Mock<IIntegrationCatalogService>();
        catalog.Setup(c => c.GetAsync("env", null, null, It.IsAny<CancellationToken>())).ReturnsAsync(new IntegrationCatalog());
        var result = await new TechnologyCoverageController(new IqrSourceStore(_db), catalog.Object).Get("env", default);
        return (ProjectTechnologyCoverage)((OkObjectResult)result.Result!).Value!;
    }

    [Fact]
    public async Task CurrentEvidence_IsNotOutdated()
    {
        var snapshot = await Analyze(WithPipeline);
        snapshot.EvidenceDomains!.CiCd.AnalyzerVersion.Should().BeGreaterThanOrEqualTo(PipelineReviewText.RequiredCiCdVersion);

        var coverage = await Coverage();
        coverage.CiCdEvidenceOutdated.Should().BeFalse();
        coverage.CiCdEvidenceVersion.Should().Be(snapshot.EvidenceDomains.CiCd.AnalyzerVersion);
    }

    [Fact]
    public async Task LegacySnapshotWithoutCiCdEvidence_IsOutdated()
    {
        var snapshot = await Analyze(WithPipeline);
        await Rewrite(snapshot with { EvidenceDomains = null, TechnologyCoverage = null });

        var coverage = await Coverage();
        coverage.CiCdEvidenceOutdated.Should().BeTrue("a snapshot analyzed before CI/CD evidence existed must be re-analyzed, not reported as N/A");
        coverage.CiCdEvidenceVersion.Should().BeNull();
    }

    [Fact]
    public async Task V1EvidenceWithPipelines_IsOutdated_ButV1WithoutPipelinesIsNot()
    {
        var snapshot = await Analyze(WithPipeline);
        await Rewrite(snapshot with { EvidenceDomains = snapshot.EvidenceDomains! with { CiCd = snapshot.EvidenceDomains.CiCd with { AnalyzerVersion = 1 } } });
        (await Coverage()).CiCdEvidenceOutdated.Should().BeTrue();

        var empty = await Analyze(WithoutPipeline);
        await Rewrite(empty with { AnalyzedAt = DateTimeOffset.UtcNow.AddMinutes(5), EvidenceDomains = empty.EvidenceDomains! with { CiCd = empty.EvidenceDomains.CiCd with { AnalyzerVersion = 1 } } });
        (await Coverage()).CiCdEvidenceOutdated.Should().BeFalse("v1 already detected pipeline files; none means N/A, as Pipeline Review says");
    }

    [Fact]
    public async Task NoSnapshot_IsNotOutdated()
    {
        var coverage = await Coverage();
        coverage.CiCdEvidenceOutdated.Should().BeFalse();
        coverage.SourceSnapshotId.Should().BeNull();
    }

    [Fact]
    public async Task PipelineReview_UsesTheSameRequiredVersion_AndSourcesCarryStatuses()
    {
        var snapshot = await Analyze(WithPipeline);
        PipelineReviewBuilder.Build(snapshot with { EvidenceDomains = snapshot.EvidenceDomains! with { CiCd = snapshot.EvidenceDomains.CiCd with { AnalyzerVersion = PipelineReviewText.RequiredCiCdVersion - 1 } } })
            .State.Should().Be("NeedsReanalysis");
        PipelineReviewBuilder.Build(snapshot).State.Should().Be("Reviewed");

        var service = new PipelineReviewService(new ReviewSourceEvidenceProvider(new IqrSourceStore(_db)), Mock.Of<IPipelineMetadataSource>(),
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var sources = await service.SourcesAsync("env");
        sources.Snapshots.Should().ContainSingle().Which.Should().Match<PipelineReviewSource>(s => s.Status == snapshot.Status.ToString() && s.CiCdStatus == snapshot.EvidenceDomains.CiCd.Status.ToString());
    }
}
