using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceImpact;
using BirkNext.SourceDomains;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services;

public sealed class SourceChangeImpactServiceTests
{
    [Fact]
    public async Task Technical_impact_survives_when_traceability_is_absent()
    {
        await using var db = Db();
        var (service, before, after) = Setup(db);
        var report = await service.AnalyzeAsync(Request(before, after), default);
        report.Should().NotBeNull();
        report!.Changes.Should().Contain(c => c.Domain == BirkNext.SourceImpact.ImpactChangeDomain.Architecture);
        report.TechnicalImpacts.Should().Contain(i => i.DisplayName == "Orders API" && i.Level == BirkNext.SourceImpact.TechnicalImpactLevel.Direct);
        report.Requirements.Should().BeEmpty();
        report.RecommendedTests.Should().BeEmpty();
        report.CoverageGaps.Should().Contain(x => x.Contains("Technical impact exists", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Technical_impact_can_run_without_a_selected_workspace_project()
    {
        await using var db = Db();
        var (service, before, after) = Setup(db);
        var report = await service.AnalyzeAsync(Request(before, after) with { ProjectId = "" }, default);
        report.Should().NotBeNull();
        report!.TechnicalImpacts.Should().Contain(i => i.DisplayName == "Orders API");
        report.Requirements.Should().BeEmpty();
        report.Limitations.Should().Contain(x => x.Contains("No workspace project was selected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Source_snapshot_comparison_does_not_require_a_target_environment()
    {
        await using var db = Db();
        var (service, before, after) = Setup(db);

        var report = await service.AnalyzeAsync(Request(before, after) with { EnvironmentId = string.Empty }, default);

        report.Should().NotBeNull();
        report!.BaselineSnapshotId.Should().Be(before.Id);
        report.TargetSnapshotId.Should().Be(after.Id);
    }

    [Fact]
    public async Task History_reopens_the_original_snapshot_bound_result()
    {
        await using var db = Db();
        var (service, before, after) = Setup(db);
        var created = await service.AnalyzeAsync(Request(before, after) with { ProjectDisplayName = "Orders workspace" }, default);

        var history = await service.ListHistoryAsync("generic", null, default);
        var reopened = await service.GetHistoryAsync(created!.RunId, default);

        history.Should().ContainSingle(x => x.RunId == created.RunId && x.BaselineSnapshotId == before.Id && x.CurrentSnapshotId == after.Id);
        reopened.Should().NotBeNull();
        reopened!.RunId.Should().Be(created.RunId);
        reopened.BaselineFingerprint.Should().Be(created.BaselineFingerprint);
        reopened.TargetFingerprint.Should().Be(created.TargetFingerprint);
        reopened.TechnicalImpacts.Should().BeEquivalentTo(created.TechnicalImpacts);
    }

    [Fact]
    public async Task Imported_project_target_must_be_the_exact_import_snapshot()
    {
        await using var db = Db();
        var before = Snapshot("v1", []) with
        {
            ProjectImport = new BirkNext.ProjectImport.ProjectImportProvenance { ImportId = "import-current" }
        };
        var after = Snapshot("v2", []) with
        {
            ProjectImport = new BirkNext.ProjectImport.ProjectImportProvenance { ImportId = "import-current" }
        };
        var otherImportBaseline = Snapshot("v0", []) with
        {
            ProjectImport = new BirkNext.ProjectImport.ProjectImportProvenance { ImportId = "another-import" }
        };
        var service = new SourceChangeImpactService(db, new SnapshotProvider([otherImportBaseline, before, after]));

        var accepted = await service.AnalyzeAsync(Request(before, after) with { EnvironmentId = string.Empty, ProjectImportId = "import-current" }, default);
        var mismatched = await service.AnalyzeAsync(Request(before, after) with { EnvironmentId = string.Empty, ProjectImportId = "another-import" }, default);
        var crossImportBaseline = await service.AnalyzeAsync(Request(otherImportBaseline, after) with { EnvironmentId = string.Empty, ProjectImportId = "import-current" }, default);

        accepted.Should().NotBeNull();
        accepted!.ProjectImportId.Should().Be("import-current");
        mismatched.Should().BeNull();
        crossImportBaseline.Should().BeNull("both snapshots must belong to the current imported project");
    }

    [Fact]
    public async Task Component_impact_expands_to_a_configured_depth_and_stops_there()
    {
        await using var db = Db();
        var before = Snapshot("v1", []) with { Architecture = ImpactGraph("v1") };
        var after = Snapshot("v2", []) with { Architecture = ImpactGraph("v2") };
        var service = new SourceChangeImpactService(db, new SnapshotProvider([before, after]));

        var depthOne = await service.AnalyzeAsync(Request(before, after) with { MaxImpactDepth = 1 }, default);
        var depthTwo = await service.AnalyzeAsync(Request(before, after) with { MaxImpactDepth = 2 }, default);

        depthOne!.TechnicalImpacts.Should().Contain(i => i.EntityId == "api" && i.Depth == 0);
        depthOne.TechnicalImpacts.Should().Contain(i => i.EntityId == "adapter" && i.Depth == 1 && i.Level == TechnicalImpactLevel.Indirect);
        depthOne.TechnicalImpacts.Should().NotContain(i => i.EntityId == "worker");
        depthTwo!.TechnicalImpacts.Should().Contain(i => i.EntityId == "worker" && i.Depth == 2);
    }

    [Fact]
    public async Task Source_journey_discovery_is_bounded_and_cycle_safe()
    {
        await using var db = Db();
        var before = Snapshot("v1", []) with { Architecture = ImpactGraph("v1") };
        var after = Snapshot("v2", []) with
        {
            Architecture = ImpactGraph("v2") with
            {
                MessagingChannels =
                [
                    new MessagingChannel { Id = "orders", Name = "orders", Type = MessagingChannelType.KafkaTopic,
                        Producers = [new("api", ChannelRole.Producer, ArchitectureEvidenceState.Confirmed, "Kafka", [])],
                        Consumers = [new("worker", ChannelRole.Consumer, ArchitectureEvidenceState.Confirmed, "Kafka", [])] }
                ]
            }
        };
        var service = new SourceChangeImpactService(db, new SnapshotProvider([before, after]));

        var report = await service.AnalyzeAsync(Request(before, after) with { MaxImpactDepth = 3 }, default);

        report!.Journeys.Should().NotBeEmpty();
        report.Journeys.Select(x => x.Id).Should().OnlyHaveUniqueItems();
        report.Journeys.Should().OnlyContain(x => x.Steps.Count <= 5 && x.Limitation.Contains("does not verify", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Requirement_and_direct_source_paths_recommend_one_test_with_both_reasons()
    {
        await using var db = Db();
        var (service, before, after) = Setup(db);
        var requirement = new Scenario { Id = Guid.NewGuid(), ProjectId = "generic", Kind = ScenarioKind.Requirement, Title = "Create order" };
        var test = new Scenario { Id = Guid.NewGuid(), ProjectId = "generic", Kind = ScenarioKind.Test, Title = "Create order API test" };
        db.Scenarios.AddRange(requirement, test);
        var file = new CodeFile { Id = Guid.NewGuid(), ProjectId = "generic", FilePath = "src/OrdersApi.cs", FileName = "OrdersApi.cs" };
        db.CodeFiles.Add(file);
        db.CodeLinks.AddRange(
            new CodeLink { Id = Guid.NewGuid(), ProjectId = "generic", CodeFileId = file.Id, ScenarioId = requirement.Id, ScenarioKind = "Requirement" },
            new CodeLink { Id = Guid.NewGuid(), ProjectId = "generic", CodeFileId = file.Id, ScenarioId = test.Id, ScenarioKind = "Test" });
        db.TraceLinks.Add(new TraceLink { ProjectId = "generic", SourceId = test.Id, TargetId = requirement.Id, SourceKind = TraceLinkArtifactKind.Scenario,
            TargetKind = TraceLinkArtifactKind.Scenario, LinkType = TraceLinkType.Covers });
        await db.SaveChangesAsync();

        var report = await service.AnalyzeAsync(Request(before, after), default);
        report!.Requirements.Should().ContainSingle(x => x.ScenarioId == requirement.Id);
        report.RecommendedTests.Should().ContainSingle(x => x.TestId == test.Id);
        report.RecommendedTests.Single().Reason.Should().Contain("Test source file linked").And.Contain("Covers impacted requirement");
    }

    [Fact]
    public async Task Fingerprint_change_without_supported_structured_diff_is_needs_review_not_no_impact()
    {
        await using var db = Db();
        var before = Snapshot("v1", []);
        var after = Snapshot("v1", [] ) with { Archive = new SourceArchive("Acme.Orders.zip", new string('c', 64), 1) };
        var provider = new SnapshotProvider([before, after]);
        var service = new SourceChangeImpactService(db, provider);
        var report = await service.AnalyzeAsync(Request(before, after), default);
        report!.Changes.Should().ContainSingle(x => x.Domain == ImpactChangeDomain.Unclassified);
        report.TechnicalImpacts.Should().ContainSingle(x => x.Level == TechnicalImpactLevel.NeedsReview);
        report.CoverageGaps.Should().Contain(x => x.Contains("Technical impact exists", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Contract_change_flows_through_explicit_component_link_to_requirement_and_test()
    {
        await using var db = Db();
        var evidence = new ArchitectureEvidence(ArchitectureEvidenceKind.ApplicationSource, "src/OrdersApi.cs", 8, "OrdersApi", "fixture", "component source");
        var before = Snapshot("v1", [evidence]) with { EvidenceDomains = Domains() };
        var after = Snapshot("v1", [evidence]) with { EvidenceDomains = Domains(new SourceContract { Id = "openapi:orders", Name = "Orders API", File = "contracts/orders.yaml" },
            new SourceEvidenceLink { Id = "contract-producer", Type = SourceEvidenceLinkType.ContractProducedByComponent, FromId = "contract:openapi:orders", ToId = "arch:component:orders-api", ToLabel = "Orders API", State = ArchitectureEvidenceState.Confirmed, Basis = "Contract file in API project" }) };
        var requirement = new Scenario { Id = Guid.NewGuid(), ProjectId = "generic", Kind = ScenarioKind.Requirement, Title = "Create order" };
        var test = new Scenario { Id = Guid.NewGuid(), ProjectId = "generic", Kind = ScenarioKind.Test, Title = "Create order API test" };
        db.Scenarios.AddRange(requirement, test);
        var file = new CodeFile { Id = Guid.NewGuid(), ProjectId = "generic", FilePath = "src/OrdersApi.cs", FileName = "OrdersApi.cs" };
        db.CodeFiles.Add(file);
        db.CodeLinks.Add(new CodeLink { Id = Guid.NewGuid(), ProjectId = "generic", CodeFileId = file.Id, ScenarioId = requirement.Id, ScenarioKind = "Requirement" });
        db.TraceLinks.Add(new TraceLink { ProjectId = "generic", SourceId = test.Id, TargetId = requirement.Id, SourceKind = TraceLinkArtifactKind.Scenario,
            TargetKind = TraceLinkArtifactKind.Scenario, LinkType = TraceLinkType.Covers });
        await db.SaveChangesAsync();

        var service = new SourceChangeImpactService(db, new SnapshotProvider([before, after]));
        var report = await service.AnalyzeAsync(Request(before, after), default);
        report!.Changes.Should().Contain(c => c.Domain == ImpactChangeDomain.Contracts && c.Kind == ImpactChangeKind.Added);
        report.TechnicalImpacts.Should().Contain(i => i.EntityType == "Component" && i.EntityId == "orders-api" && i.Level == TechnicalImpactLevel.Direct);
        report.Requirements.Should().ContainSingle(x => x.ScenarioId == requirement.Id);
        report.RecommendedTests.Should().ContainSingle(x => x.TestId == test.Id && x.Reason.Contains("Covers impacted requirement", StringComparison.Ordinal));
    }

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static SourceChangeImpactRequest Request(IqrSourceSnapshot before, IqrSourceSnapshot after) => new("dev", "generic", before.Id, after.Id);

    private static (SourceChangeImpactService Service, IqrSourceSnapshot Before, IqrSourceSnapshot After) Setup(AppDbContext db)
    {
        var before = Snapshot("v1", []);
        var after = Snapshot("v2", [new ArchitectureEvidence(ArchitectureEvidenceKind.ApplicationSource, "src/OrdersApi.cs", 8, "OrdersApi", "test", "API implementation")]);
        var provider = new SnapshotProvider([before, after]);
        return (new SourceChangeImpactService(db, provider), before, after);
    }

    private static IqrSourceSnapshot Snapshot(string technology, List<ArchitectureEvidence> evidence) => new()
    {
        IntegrationId = "source-analysis",
        Archive = new SourceArchive("Acme.Orders.zip", new string(technology == "v1" ? 'a' : 'b', 64), 1),
        AnalyzedAt = DateTimeOffset.UtcNow.AddDays(technology == "v1" ? -1 : 0),
        Status = SourceAnalysisStatus.Ready,
        Architecture = new ArchitectureSnapshot
        {
            Components = [new ArchitectureComponent { Id = "orders-api", Name = "Orders API", ComponentType = ArchitectureComponentType.Api,
                Technologies = [technology], Evidence = evidence }]
        }
    };

    private static ArchitectureSnapshot ImpactGraph(string changedTechnology) => new()
    {
        Components =
        [
            new ArchitectureComponent { Id = "adapter", Name = "Adapter", Technologies = ["adapter"] },
            new ArchitectureComponent { Id = "api", Name = "API", Technologies = [changedTechnology] },
            new ArchitectureComponent { Id = "worker", Name = "Worker", Technologies = ["worker"] }
        ],
        Dependencies =
        [
            new ArchitectureDependency { Id = "adapter-api", FromComponentId = "adapter", ToId = "api", DependencyType = ArchitectureDependencyType.Http, EvidenceState = ArchitectureEvidenceState.Confirmed },
            new ArchitectureDependency { Id = "worker-adapter", FromComponentId = "worker", ToId = "adapter", DependencyType = ArchitectureDependencyType.ProjectReference, EvidenceState = ArchitectureEvidenceState.Confirmed },
            new ArchitectureDependency { Id = "api-adapter", FromComponentId = "api", ToId = "adapter", DependencyType = ArchitectureDependencyType.ProjectReference, EvidenceState = ArchitectureEvidenceState.Confirmed }
        ]
    };

    private static SourceEvidenceDomainsSnapshot Domains(SourceContract? contract = null, SourceEvidenceLink? link = null) => new()
    {
        Contracts = new ContractEvidence { Contracts = contract is null ? [] : [contract] },
        CrossDomain = new CrossDomainEvidence { Links = link is null ? [] : [link] }
    };

    private sealed class SnapshotProvider(IReadOnlyList<IqrSourceSnapshot> snapshots) : IReviewSourceEvidenceProvider
    {
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(snapshots);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult(snapshots.FirstOrDefault(s => s.Id == snapshotId));
    }
}
