using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
using BirkNext.SourceImpact;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services;

public sealed class ImpactAnalysisRunServiceTests
{
    [Fact]
    public async Task Imported_requirement_run_persists_and_reopens_one_unified_report()
    {
        await using var db = Db();
        const string importId = "import-project-42";
        var projectId = $"import:{importId}";
        var requirement = new Scenario { Id = Guid.NewGuid(), ProjectId = projectId, Kind = ScenarioKind.Requirement, Title = "Create an order" };
        var test = new Scenario { Id = Guid.NewGuid(), ProjectId = projectId, Kind = ScenarioKind.Test, Title = "Order API test" };
        db.Scenarios.AddRange(requirement, test);
        db.TraceLinks.Add(new TraceLink { ProjectId = projectId, SourceId = test.Id, TargetId = requirement.Id,
            SourceKind = TraceLinkArtifactKind.Scenario, TargetKind = TraceLinkArtifactKind.Scenario, LinkType = TraceLinkType.Covers });
        await db.SaveChangesAsync();

        var evidence = new NoSnapshots();
        var service = new ImpactAnalysisRunService(db, new SourceChangeImpactService(db, evidence), evidence);
        var report = await service.RunAsync(new(projectId, "Imported Orders", importId, [requirement.Id], null, null), default);
        var history = await service.HistoryAsync(projectId, importId, default);
        var reopened = await service.HistoryItemAsync(report.RunId, default);

        report.ProjectImportId.Should().Be(importId);
        report.ChangeSet.ChangeOrigin.Should().Be("RequirementSelection");
        report.Findings.Should().ContainSingle(x => x.Kind == ImpactAnalysisFindingKind.Test && x.DisplayName == "Order API test" && x.VerificationState == "ExplicitTraceLink");
        report.Findings.Single(x => x.Kind == ImpactAnalysisFindingKind.Test).Reason.Should().Contain("not verified");
        report.DomainAssessments.Should().Contain(x => x.Domain == "Source" && x.Status == ImpactAnalysisEvidenceStatus.NotEvaluated);
        report.DomainAssessments.Should().Contain(x => x.Domain == "Requirements" && x.Status == ImpactAnalysisEvidenceStatus.Evaluated);
        history.Should().ContainSingle(x => x.RunId == report.RunId && x.ProjectImportId == importId);
        reopened.Should().BeEquivalentTo(report);
    }

    [Fact]
    public async Task Imported_project_identity_mismatch_is_rejected_before_persistence()
    {
        await using var db = Db();
        var evidence = new NoSnapshots();
        var service = new ImpactAnalysisRunService(db, new SourceChangeImpactService(db, evidence), evidence);

        var act = () => service.RunAsync(new("sample-project", "Imported", "import-1", [Guid.NewGuid()], null, null), default);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*does not match*");
        (await db.ImpactAnalysisRuns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Empty_requirement_traceability_is_reported_without_claiming_no_impact()
    {
        await using var db = Db();
        const string importId = "empty-import";
        var evidence = new NoSnapshots();
        var service = new ImpactAnalysisRunService(db, new SourceChangeImpactService(db, evidence), evidence);
        var report = await service.RunAsync(new($"import:{importId}", "Imported", importId, [Guid.NewGuid()], null, null), default);

        report.DomainAssessments.Should().Contain(x => x.Domain == "Requirements" && x.Status == ImpactAnalysisEvidenceStatus.PartiallyEvaluated);
        report.DomainAssessments.Should().Contain(x => x.Domain == "Tests" && x.Status == ImpactAnalysisEvidenceStatus.NotEvaluated && x.Reason.Contains("absence of evidence does not mean no tests", StringComparison.OrdinalIgnoreCase));
        report.Limitations.Should().Contain(x => x.Contains("not found", StringComparison.OrdinalIgnoreCase));
        report.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task Combined_run_keeps_exact_workspace_snapshot_pair_in_history()
    {
        await using var db = Db();
        var baseline = Snapshot('a', DateTimeOffset.UtcNow.AddDays(-1));
        var current = Snapshot('b', DateTimeOffset.UtcNow);
        var evidence = new SnapshotProvider([baseline, current]);
        var service = new ImpactAnalysisRunService(db, new SourceChangeImpactService(db, evidence), evidence);

        var report = await service.RunAsync(new("orders", "Orders", null, [], baseline.Id, current.Id), default);
        var reopened = await service.HistoryItemAsync(report.RunId, default);

        report.ChangeSet.BaselineSnapshotId.Should().Be(baseline.Id);
        report.ChangeSet.CurrentSnapshotId.Should().Be(current.Id);
        report.ChangeSet.BaselineFingerprint.Should().Be(baseline.Archive.Sha256);
        report.ChangeSet.CurrentFingerprint.Should().Be(current.Archive.Sha256);
        report.Findings.Should().Contain(x => x.Classification == ImpactAnalysisClassification.SelectedChange && x.Kind == ImpactAnalysisFindingKind.Source);
        report.DomainAssessments.Should().Contain(x => x.Domain == "Source" && x.Status == ImpactAnalysisEvidenceStatus.Evaluated);
        reopened.Should().BeEquivalentTo(report);
    }

    [Fact]
    public async Task Unified_history_redacts_secret_like_values_from_source_evidence()
    {
        await using var db = Db();
        const string secret = "should-never-be-persisted-xyz";
        var baseline = Snapshot('a', DateTimeOffset.UtcNow.AddDays(-1)) with { EvidenceDomains = Infra("Password=old-value") };
        var current = Snapshot('b', DateTimeOffset.UtcNow) with { EvidenceDomains = Infra($"Password={secret}") };
        var evidence = new SnapshotProvider([baseline, current]);
        var service = new ImpactAnalysisRunService(db, new SourceChangeImpactService(db, evidence), evidence);

        var report = await service.RunAsync(new("orders", "Orders", null, [], baseline.Id, current.Id), default);
        var saved = await db.ImpactAnalysisRuns.SingleAsync(x => x.Id == report.RunId);

        report.SourceComparison!.Changes.Should().NotContain(x => x.Detail.Contains(secret, StringComparison.Ordinal));
        saved.ResultJson.Should().NotContain(secret);
    }

    [Fact]
    public async Task Source_run_reports_available_integration_and_documentation_evidence_as_partial()
    {
        await using var db = Db();
        var baseline = Snapshot('a', DateTimeOffset.UtcNow.AddDays(-1));
        var current = Snapshot('b', DateTimeOffset.UtcNow);
        current = current with
        {
            GeneratedDocumentation = new BirkNext.GeneratedDocumentation.GeneratedDocumentationSnapshot
            {
                SourceSnapshotId = current.Id,
                SourceFingerprint = current.Archive.Sha256
            }
        };
        var evidence = new SnapshotProvider([baseline, current]);
        var service = new ImpactAnalysisRunService(db, new SourceChangeImpactService(db, evidence), evidence);

        var report = await service.RunAsync(new("orders", "Orders", null, [], baseline.Id, current.Id), default);

        report.DomainAssessments.Should().Contain(x => x.Domain == "Integrations and cross-service contracts" && x.Status == ImpactAnalysisEvidenceStatus.PartiallyEvaluated);
        report.DomainAssessments.Should().Contain(x => x.Domain == "Documentation" && x.Status == ImpactAnalysisEvidenceStatus.PartiallyEvaluated);
        report.DomainAssessments.Should().Contain(x => x.Domain == "Security/authentication" && x.Status == ImpactAnalysisEvidenceStatus.NotEvaluated);
    }

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IqrSourceSnapshot Snapshot(char fingerprint, DateTimeOffset analyzedAt) => new()
    {
        IntegrationId = "source-analysis",
        Archive = new SourceArchive("Orders.zip", new string(fingerprint, 64), 1),
        AnalyzedAt = analyzedAt,
        Status = SourceAnalysisStatus.Ready,
        Architecture = new ArchitectureSnapshot()
    };

    private static SourceEvidenceDomainsSnapshot Infra(string setting) => new()
    {
        Infrastructure = new InfrastructureEvidence
        {
            Domain = SourceEvidenceDomain.Infrastructure,
            Resources = [new InfrastructureResource { Id = "bus", LogicalName = "Orders bus", ResourceType = "eventHub", Settings = [new("connectionString", setting, "Messaging", true)] }]
        }
    };

    private sealed class NoSnapshots : IReviewSourceEvidenceProvider
    {
        public bool SourceAnalysisEnabled => false;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([]);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    private sealed class SnapshotProvider(IReadOnlyList<IqrSourceSnapshot> snapshots) : IReviewSourceEvidenceProvider
    {
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(snapshots);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult(snapshots.FirstOrDefault(s => s.Id == snapshotId));
    }
}
