using System.Text.Json;
using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Dependencies;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// GET api/source-analysis lists summaries (identity, status, counts, which evidence each snapshot holds); a full snapshot is fetched by id.
/// The list grew with the evidence of every stored snapshot (17.7 MB for eight) before.
/// </summary>
public sealed class SourceAnalysisListSummaryTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private IqrSourceSnapshot Store(IqrSourceSnapshot snapshot)
    {
        _db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord
        {
            Id = snapshot.Id, IntegrationId = IqrSourceStore.SourceAnalysisOwner, EvidenceJson = JsonSerializer.Serialize(snapshot), AnalyzedAt = snapshot.AnalyzedAt,
        });
        _db.SaveChanges();
        return snapshot;
    }

    private IqrSourceEvidenceController Controller() => new(new IqrSourceStore(_db),
        Moq.Mock.Of<BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider>(), NullLogger<IqrSourceEvidenceController>.Instance);

    private static IqrSourceSnapshot Heavy(string archive, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), IntegrationId = IqrSourceStore.SourceAnalysisOwner, Archive = new(archive, new string('a', 64), 120), AnalyzedAt = at, Status = SourceAnalysisStatus.Ready,
        Rules = [.. Enumerable.Range(0, 400).Select(i => new ImplementationRule { Id = $"rule-{i}", Symbol = $"Rule{i}", Behavior = new string('x', 200) })],
        Limitations = [.. Enumerable.Range(0, 9).Select(i => $"limitation {i}")],
        Repository = new("repo:m2lb", "M2LB", "git remote"),
        ProjectImport = new BirkNext.ProjectImport.ProjectImportProvenance { ImportId = "import-1" },
        DependencyEvidence = new SourceDependencyEvidence(),
    };

    [Fact]
    public async Task The_list_carries_summaries_and_never_the_evidence()
    {
        var stored = Store(Heavy("M2LB.zip", DateTimeOffset.Parse("2026-10-30T10:00:00Z")));
        Store(Heavy("M2LB.Common.zip", DateTimeOffset.Parse("2026-10-28T10:00:00Z")) with { DependencyEvidence = null, Repository = null });

        var list = (await Controller().List("", default)).Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<IReadOnlyList<IqrSourceSnapshotSummary>>().Which;

        list.Should().HaveCount(2);
        var summary = list.Single(s => s.Id == stored.Id);
        (summary.Archive.FileName, summary.Rules, summary.LimitationCount, summary.Limitations.Count).Should().Be(("M2LB.zip", 400, 9, IqrSourceSnapshotSummary.MaxLimitations));
        (summary.RepositoryKey, summary.ProjectImportId, summary.HasDependencyEvidence).Should().Be(("repo:m2lb", "import-1", true));
        JsonSerializer.Serialize(list).Should().NotContain(new string('x', 200), "rule bodies are evidence: the list is identity, status and counts");
        JsonSerializer.Serialize(list).Length.Should().BeLessThan(JsonSerializer.Serialize(stored).Length / 10);
    }

    [Fact]
    public async Task One_snapshot_is_fetched_by_id_with_its_full_evidence_and_a_missing_one_is_not_found()
    {
        var stored = Store(Heavy("M2LB.zip", DateTimeOffset.UtcNow));

        var detail = (await Controller().Get(stored.Id, default)).Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<IqrSourceSnapshot>().Which;
        detail.Rules.Should().HaveCount(400);
        (await Controller().Get(Guid.NewGuid(), default)).Result.Should().BeOfType<NotFoundResult>("an unknown snapshot is not an empty one");
    }

    [Fact]
    public async Task Dependency_sources_carry_only_dependency_evidence_of_the_snapshots_that_have_it()
    {
        var with = Store(Heavy("M2LB.zip", DateTimeOffset.UtcNow));
        Store(Heavy("Terraform.zip", DateTimeOffset.UtcNow) with { DependencyEvidence = null });

        var sources = (await Controller().DependencySources("", default)).Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<IReadOnlyList<DependencySourceEvidence>>().Which;

        sources.Should().ContainSingle().Which.SnapshotId.Should().Be(with.Id);
    }

    [Fact]
    public void A_summary_says_which_evidence_a_snapshot_holds()
    {
        var bare = IqrSourceSnapshotSummary.From(new IqrSourceSnapshot());
        (bare.HasArchitecture, bare.HasDatabaseArchitecture, bare.HasObservability, bare.HasEvidenceDomains, bare.HasDependencyEvidence, bare.HasCodeRiskEvidence, bare.SourceTestDefinitions)
            .Should().Be((false, false, false, false, false, false, (int?)null), "a snapshot that predates an analyzer says so; it is never read as an empty result");
        IqrSourceSnapshotSummary.From(new IqrSourceSnapshot { EvidenceDomains = new() }).OpenApiContracts.Should().Be(0);
    }
}
