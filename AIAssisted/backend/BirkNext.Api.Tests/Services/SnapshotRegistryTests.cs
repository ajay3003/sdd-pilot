using BirkNext.Api.Data;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Data.SnapshotRegistry;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Snapshots;
using BirkNext.Snapshots;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// The shared Snapshot Registry over existing stores: stable ids (the owners' own), idempotent metadata-only sync, metadata-only listing, computed
/// latest vs explicit current/approved pointers, retention and the deletion check, and lineage from stored ids only — including a frozen Delivery
/// report resolving its exact snapshots after new imports.
/// </summary>
public sealed class SnapshotRegistryTests : IDisposable
{
    private readonly string _name = Guid.NewGuid().ToString("N");
    private readonly AppDbContext _app;
    private readonly DeliveryReportingDbContext _delivery;
    private readonly CanonicalInterpretationDbContext _canonical;
    private readonly SnapshotRegistryDbContext _registry;

    public SnapshotRegistryTests()
    {
        _app = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"app-{_name}").Options);
        _delivery = new(new DbContextOptionsBuilder<DeliveryReportingDbContext>().UseInMemoryDatabase($"dlr-{_name}").Options);
        _canonical = new(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase($"canon-{_name}").Options);
        _registry = new(new DbContextOptionsBuilder<SnapshotRegistryDbContext>().UseInMemoryDatabase($"reg-{_name}").Options);
        SnapshotRegistryService.ResetThrottle();
    }

    public void Dispose() { _app.Dispose(); _delivery.Dispose(); _canonical.Dispose(); _registry.Dispose(); }

    private SnapshotRegistryService Registry() => new(_registry,
        [new SourceAnalysisSnapshotSource(_app), new DeliverySnapshotSource(_delivery), new InterpretationSnapshotSource(_canonical), new ReviewRunSnapshotSource(_app), new ExecutionSnapshotSource(_app)],
        TimeProvider.System);

    /// <param name="status">The application-code analyzer's outcome. "Failed" is a genuinely failed analysis: production projects that could not be read.</param>
    /// <param name="extra">Further snapshot JSON members (e.g. evidence domains), starting with a comma.</param>
    /// <param name="productionProjects">Whether the package has a production project (as every analyzed application package does). With status
    /// Failed it could not be read — a real failure; without one (an infrastructure-only package) the application-code analyzer still says Failed,
    /// but there is no application code to analyze.</param>
    private Guid SourceAnalysis(string archive, DateTimeOffset at, string commit = "Unknown", string? importId = null, string status = "Ready", string extra = "",
        bool productionProjects = true)
    {
        var id = Guid.NewGuid();
        var import = importId is null ? "" : $$""","projectImport":{"importId":"{{importId}}","archiveFileName":"{{archive}}"}""";
        var projects = productionProjects
            ? ""","projects":[{"name":"Api","path":"src/Api/Api.csproj","isTest":false,"classification":"Application/service","confidence":"Confirmed"}]""" : "";
        _app.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord
        {
            Id = id, IntegrationId = "source-analysis", AnalyzedAt = at,
            EvidenceJson = $$"""{"id":"{{id}}","commit":"{{commit}}","analyzerVersion":2,"status":"{{status}}","archive":{"fileName":"{{archive}}","sha256":"sha-{{archive}}","filesAnalyzed":3}{{import}}{{projects}}{{extra}}}""",
        });
        _app.SaveChanges();
        return id;
    }

    private (Guid Project, Guid Baseline, Guid Current, Guid Report) DeliveryReport()
    {
        var project = Guid.NewGuid();
        var source = Guid.NewGuid();
        _delivery.Projects.Add(new DeliveryProjectRecord { Id = project, Name = "M2LB " + _name });
        _delivery.ReportSources.Add(new DeliveryReportSourceRecord { Id = source, ProjectId = project, Fingerprint = "fp-archive", GitCommit = "abc123", OriginalFileName = "specs.zip" });
        var baseline = new DeliverySnapshotRecord { Id = Guid.NewGuid(), ProjectId = project, ReportSourceId = source, Label = "Week 43", ImportedAt = DateTimeOffset.Parse("2026-10-20T08:00:00Z"), EffectiveAt = DateTimeOffset.Parse("2026-10-24T00:00:00Z"), ParseStatus = "Parsed", ParserVersion = 4 };
        var current = new DeliverySnapshotRecord { Id = Guid.NewGuid(), ProjectId = project, ReportSourceId = source, Label = "Week 44", ImportedAt = DateTimeOffset.Parse("2026-10-27T08:00:00Z"), EffectiveAt = DateTimeOffset.Parse("2026-10-31T00:00:00Z"), ParseStatus = "Parsed", ParserVersion = 4 };
        var comparison = new DeliverySnapshotComparisonRecord { Id = Guid.NewGuid(), ProjectId = project, BaselineSnapshotId = baseline.Id, CurrentSnapshotId = current.Id, CreatedAt = DateTimeOffset.Parse("2026-10-31T09:00:00Z") };
        var report = new DeliveryWeeklyReportRecord
        {
            Id = Guid.NewGuid(), ProjectId = project, ComparisonId = comparison.Id, BaselineSnapshotId = baseline.Id, CurrentSnapshotId = current.Id, Title = "Week 44 report",
            GeneratedAt = DateTimeOffset.Parse("2026-10-31T09:00:00Z"), PeriodEnd = DateTimeOffset.Parse("2026-10-31T00:00:00Z"), ContentVersion = 3,
            ContentJson = """{"calculationVersion":4,"interpretationRuleSet":"work-items/1"}""",
        };
        _delivery.Snapshots.AddRange(baseline, current);
        _delivery.Comparisons.Add(comparison);
        _delivery.WeeklyReports.Add(report);
        _delivery.SaveChanges();
        return (project, baseline.Id, current.Id, report.Id);
    }

    [Fact]
    public async Task Sync_registers_existing_ids_with_metadata_once_and_is_idempotent()
    {
        var id = SourceAnalysis("M2LB.zip", DateTimeOffset.Parse("2026-11-03T09:00:00Z"), importId: "import-1");
        var registry = Registry();

        var first = await registry.SyncAsync(true, default);
        var second = await registry.SyncAsync(true, default);

        first.Registered.Should().Be(1);
        second.Registered.Should().Be(0, "sync is idempotent");
        var metadata = (await registry.GetAsync(SnapshotType.SourceAnalysis, id.ToString(), default))!;
        metadata.Id.Should().Be(id.ToString(), "the owner's snapshot id is preserved");
        metadata.ContentFingerprint.Should().Be("sha-M2LB.zip");
        metadata.SourceRevision.Should().BeNull("an unknown commit is never invented");
        metadata.EffectiveAt.Should().BeNull("Source Analysis has no source-effective date; the import time is not one");
        metadata.AnalysisVersion.Should().Be("analyzer 2");
        metadata.Frozen.Should().BeTrue();
        (await registry.LineageAsync(SnapshotType.SourceAnalysis, id.ToString(), 2, default))!.Upstream
            .Should().ContainSingle(e => e.Kind == SnapshotLineageKind.ImportedFrom && e.To.Id == "import-1");
    }

    [Fact]
    public async Task A_registered_entry_whose_payload_is_gone_is_marked_unavailable_never_deleted_and_never_listed_for_selection()
    {
        var kept = SourceAnalysis("M2LB.zip", DateTimeOffset.Parse("2026-11-03T09:00:00Z"));
        var gone = SourceAnalysis("shop.zip", DateTimeOffset.Parse("2026-11-03T10:00:00Z"));
        var registry = Registry();
        await registry.SyncAsync(true, default);
        (await registry.SourceAnalysisSummariesAsync(50, default))!.Select(s => s.Id).Should().BeEquivalentTo([kept, gone]);

        // The payload disappears outside the registry (e.g. a registry row written for a store it does not describe).
        _app.IqrSourceSnapshots.Remove(_app.IqrSourceSnapshots.Single(r => r.Id == gone));
        _app.SaveChanges();
        var sync = await registry.SyncAsync(true, default);

        sync.Problems.Should().ContainSingle(p => p.Contains("shop.zip") && p.Contains("marked unavailable"));
        (await registry.SourceAnalysisSummariesAsync(50, default))!.Select(s => s.Id).Should().Equal([kept], "an entry without a payload is never offered for selection");
        var metadata = (await registry.GetAsync(SnapshotType.SourceAnalysis, gone.ToString(), default))!;
        metadata.PayloadAvailable.Should().BeFalse("the metadata and lineage stay; only availability changes");
        (await registry.SyncAsync(true, default)).Problems.Should().BeEmpty("already reconciled entries are not reported again");
    }

    private const string InfrastructureDomains = ""","evidenceDomains":{"infrastructure":{"status":"Complete"},"configuration":{"status":"Complete"},"ciCd":{"status":"Partial"}}""";

    [Fact]
    public async Task An_infrastructure_only_package_is_not_a_failed_analysis_its_status_is_the_outcome_of_the_areas_it_contains()
    {
        // The application-code analyzer says Failed (no production project); Infrastructure as Code and Configuration completed, CI/CD partly.
        var terraform = SourceAnalysis("Terraform-iac-m2lb.zip", DateTimeOffset.Parse("2026-11-03T09:15:00Z"), status: "Failed", extra: InfrastructureDomains, productionProjects: false);
        var complete = SourceAnalysis("Terraform-complete.zip", DateTimeOffset.Parse("2026-11-03T09:16:00Z"), status: "Failed", productionProjects: false,
            extra: ""","evidenceDomains":{"infrastructure":{"status":"Complete"},"configuration":{"status":"Complete"}}""");
        var failed = SourceAnalysis("Broken.zip", DateTimeOffset.Parse("2026-11-03T09:17:00Z"), status: "Failed");
        var registry = Registry();
        await registry.SyncAsync(true, default);

        (await registry.GetAsync(SnapshotType.SourceAnalysis, terraform.ToString(), default))!.Status.Should().Be(SnapshotProcessingStatus.Partial,
            "infrastructure and configuration completed, CI/CD partly — never Failed for missing application code, never rounded up to Completed");
        (await registry.GetAsync(SnapshotType.SourceAnalysis, complete.ToString(), default))!.Status.Should().Be(SnapshotProcessingStatus.Completed);
        (await registry.GetAsync(SnapshotType.SourceAnalysis, failed.ToString(), default))!.Status.Should().Be(SnapshotProcessingStatus.Failed,
            "production projects that could not be read: a real failure stays a failure");
    }

    [Theory]
    [InlineData(new[] { "NotDetected", "Unsupported", "Complete", "Complete", "Partial", "NotDetected" }, SnapshotProcessingStatus.Partial)]
    [InlineData(new[] { "NotDetected", "NotAnalyzed", "Complete", "Complete", "NotDetected", "NotDetected" }, SnapshotProcessingStatus.Completed)]
    [InlineData(new[] { "Failed", "NotAnalyzed", "NotDetected", "NotDetected", "NotDetected", "NotDetected" }, SnapshotProcessingStatus.Failed)]
    // Nothing usable: CI/CD failed and architecture unsupported (no application code) is a failure, never Partial.
    [InlineData(new[] { "NotDetected", "Unsupported", "NotDetected", "NotDetected", "Failed", "NotDetected" }, SnapshotProcessingStatus.Failed)]
    // Only an analyzer limitation, nothing failed: Partial.
    [InlineData(new[] { "NotDetected", "Unsupported", "NotDetected", "NotDetected", "NotDetected", "NotDetected" }, SnapshotProcessingStatus.Partial)]
    [InlineData(new[] { "Partial", "Complete", "NotDetected", "Partial", "Partial", "NotDetected" }, SnapshotProcessingStatus.Partial)]
    [InlineData(new[] { "NotDetected", "NotAnalyzed", "NotDetected", "NotDetected", "NotDetected", "NotDetected" }, SnapshotProcessingStatus.Unknown)]
    public void The_analysis_outcome_counts_only_the_areas_the_package_contains(string[] statuses, SnapshotProcessingStatus expected)
    {
        string[] names = ["Application code", "Architecture", "Infrastructure as Code", "Configuration", "CI/CD", "Contracts"];
        BirkNext.Api.Services.IntegrationJourneys.SourceSetupAreas.Overall([.. names.Zip(statuses, (n, st) => new BirkNext.Integrations.SourceSetupArea(n, st))])
            .Should().Be(expected);
    }

    [Fact]
    public async Task Entries_registered_with_the_old_rule_are_re_read_once_metadata_only()
    {
        const string domains = ""","evidenceDomains":{"infrastructure":{"status":"Complete"},"configuration":{"status":"Complete"},"ciCd":{"status":"Partial"}}""";
        var id = SourceAnalysis("Terraform-BasicOpsInfra.zip", DateTimeOffset.Parse("2026-11-03T09:15:00Z"), status: "Failed", extra: domains, productionProjects: false);
        var registry = Registry();
        await registry.SyncAsync(true, default);
        var row = _registry.Entries.Single(e => e.Id == id.ToString());
        (row.Status, row.MetadataVersion) = (nameof(SnapshotProcessingStatus.Failed), 1);   // as registered before this rule
        var created = row.CreatedAt;
        _registry.SaveChanges();

        var sync = await registry.SyncAsync(true, default);

        var metadata = (await registry.GetAsync(SnapshotType.SourceAnalysis, id.ToString(), default))!;
        metadata.Status.Should().Be(SnapshotProcessingStatus.Partial, "infrastructure completed, CI/CD partly; no application code is not a failure");
        (metadata.MetadataVersion, metadata.CreatedAt, metadata.Frozen).Should().Be((2, created, true), "identity, timestamps and immutability never change");
        sync.Updated.Should().BeGreaterThan(0);
        (await registry.SyncAsync(true, default)).Updated.Should().Be(0, "re-read once");
    }

    [Fact]
    public async Task Listing_is_metadata_only_paged_filtered_and_marks_latest_and_current_pointers()
    {
        var older = SourceAnalysis("M2LB.zip", DateTimeOffset.Parse("2026-11-01T09:00:00Z"));
        var failed = SourceAnalysis("M2LB.zip", DateTimeOffset.Parse("2026-11-03T09:00:00Z"), status: "Failed");
        SourceAnalysis("Terraform.zip", DateTimeOffset.Parse("2026-11-02T09:00:00Z"));
        DeliveryReport();
        var registry = Registry();

        var page = await registry.ListAsync(null, SnapshotType.SourceAnalysis, null, null, null, null, null, 0, 2, default);
        page.Total.Should().Be(3);
        page.Items.Should().HaveCount(2);
        page.Items[0].Metadata.Id.Should().Be(failed.ToString(), "newest first");
        page.Items[0].Pointers.Should().Contain(SnapshotPointerKind.LatestCreated).And.NotContain(SnapshotPointerKind.LatestCompleted,
            "the latest-created snapshot failed: it is not the latest valid one");
        (await registry.ListAsync(null, null, "delivery-report", null, null, null, null, 0, 10, default)).Items.Should().ContainSingle();
        (await registry.ListAsync(null, SnapshotType.SourceAnalysis, null, SnapshotProcessingStatus.Failed, null, null, null, 0, 10, default)).Total.Should().Be(1);
        (await registry.ListAsync(null, SnapshotType.SourceAnalysis, null, null, DateTimeOffset.Parse("2026-11-02T00:00:00Z"), null, "created", 0, 10, default)).Items
            .Select(i => i.Metadata.Label).Should().Equal("Terraform.zip", "M2LB.zip");

        var pointers = await registry.PointersAsync(null, SnapshotType.SourceAnalysis, "source-analysis", default);
        pointers.Single(p => p.Kind == SnapshotPointerKind.LatestCreated).Snapshot!.Id.Should().Be(failed.ToString());
        pointers.Single(p => p.Kind == SnapshotPointerKind.LatestCompleted).Snapshot!.Id.Should().NotBe(failed.ToString());
        pointers.Single(p => p.Kind == SnapshotPointerKind.CurrentSelected).Snapshot.Should().BeNull("nothing is current until a person selects it");

        (await registry.SetPointerAsync(new(null, SnapshotType.SourceAnalysis, "source-analysis", SnapshotPointerKind.CurrentApproved, failed.ToString()), "tester", default))
            .Should().Contain("completed", "a failed snapshot is never approved as current");
        (await registry.SetPointerAsync(new(null, SnapshotType.SourceAnalysis, "source-analysis", SnapshotPointerKind.CurrentSelected, older.ToString()), "tester", default)).Should().BeNull();
        (await registry.SetPointerAsync(new("another-project", SnapshotType.SourceAnalysis, "source-analysis", SnapshotPointerKind.CurrentSelected, older.ToString()), "tester", default))
            .Should().Contain("never cross");
        await registry.LineageAsync(SnapshotType.SourceAnalysis, failed.ToString(), 2, default); // viewing another snapshot…
        (await registry.PointersAsync(null, SnapshotType.SourceAnalysis, "source-analysis", default)).Single(p => p.Kind == SnapshotPointerKind.CurrentSelected).Snapshot!.Id
            .Should().Be(older.ToString(), "…never moves the current pointer");
        (await registry.SetPointerAsync(new(null, SnapshotType.SourceAnalysis, "source-analysis", SnapshotPointerKind.LatestCreated, older.ToString()), "tester", default))
            .Should().Contain("computed");
    }

    [Fact]
    public async Task Scenario_E_a_frozen_report_keeps_resolving_its_exact_snapshots_after_new_imports()
    {
        var (project, baseline, current, report) = DeliveryReport();
        var registry = Registry();
        var before = (await registry.LineageAsync(SnapshotType.GeneratedReport, report.ToString(), 3, default))!;

        // A newer import arrives; the report's lineage and metadata must not move.
        _delivery.Snapshots.Add(new DeliverySnapshotRecord { Id = Guid.NewGuid(), ProjectId = project, ReportSourceId = _delivery.ReportSources.First().Id, Label = "Week 45", ImportedAt = DateTimeOffset.Parse("2026-11-03T08:00:00Z"), ParseStatus = "Parsed" });
        _delivery.SaveChanges();
        await registry.SyncAsync(true, default);
        var after = (await registry.LineageAsync(SnapshotType.GeneratedReport, report.ToString(), 3, default))!;

        after.Upstream.Select(e => (e.Kind, e.To.Id)).Should().BeEquivalentTo(before.Upstream.Select(e => (e.Kind, e.To.Id)));
        after.Upstream.Should().Contain(e => e.Kind == SnapshotLineageKind.ComparedAgainst && e.To.Id == baseline.ToString())
            .And.Contain(e => e.Kind == SnapshotLineageKind.GeneratedReportFrom && e.To.Id == current.ToString());
        after.Snapshot.AnalysisVersion.Should().Be("calculation 4 · work-items/1", "the rule set the report was calculated with is read from its frozen content");
        after.Snapshot.Retention.Should().Be(SnapshotRetentionClass.FrozenReportEvidence);
        after.Snapshot.EffectiveAt.Should().Be(DateTimeOffset.Parse("2026-10-31T00:00:00Z"), "effective (period end) is kept apart from generation time");

        (await registry.GetAsync(SnapshotType.Delivery, baseline.ToString(), default))!.SourceRevision.Should().Be("abc123");
        (await registry.SetRetentionAsync(SnapshotType.GeneratedReport, report.ToString(), SnapshotRetentionClass.Working, default)).Should().NotBeNull("frozen evidence is never downgraded");
    }

    [Fact]
    public async Task Deletion_checks_protection_frozen_evidence_and_inbound_references()
    {
        var (_, baseline, _, report) = DeliveryReport();
        var registry = Registry();

        var check = (await registry.DeletionCheckAsync(SnapshotType.Delivery, baseline.ToString(), null, default))!;
        check.Allowed.Should().BeTrue("Delivery keeps the report's frozen copy when a snapshot is deleted");
        check.Notes.Should().Contain(n => n.Contains("frozen copy"));

        (await registry.SetRetentionAsync(SnapshotType.Delivery, baseline.ToString(), SnapshotRetentionClass.Protected, default)).Should().BeNull();
        var protectedCheck = (await registry.DeletionCheckAsync(SnapshotType.Delivery, baseline.ToString(), null, default))!;
        protectedCheck.Allowed.Should().BeFalse();
        protectedCheck.Blocking.Should().Contain(b => b.StartsWith("Protected"));
        await registry.SyncAsync(true, default);
        (await registry.GetAsync(SnapshotType.Delivery, baseline.ToString(), default))!.Retention.Should().Be(SnapshotRetentionClass.Protected, "sync never overwrites a person's retention");

        (await registry.DeletionCheckAsync(SnapshotType.GeneratedReport, report.ToString(), null, default))!.Allowed.Should().BeFalse("frozen report evidence is never deleted through the registry");
        var sa = SourceAnalysis("M2LB.zip", DateTimeOffset.UtcNow);
        (await registry.DeletionCheckAsync(SnapshotType.SourceAnalysis, sa.ToString(), SnapshotReferenceIndex.UncheckedSourceAnalysisReferrers, default))!.NotChecked
            .Should().NotBeEmpty("stores whose references cannot be checked are always named");
    }

    [Fact]
    public async Task Scenario_D_source_snapshot_to_interpretation_and_review_lineage()
    {
        var sa = SourceAnalysis("M2LB.zip", DateTimeOffset.Parse("2026-11-03T09:00:00Z"), commit: "f00d", importId: "import-7");
        var interpretation = new InterpretationSnapshotRecord
        {
            Id = Guid.NewGuid(), Scope = "dev", CreatedAt = DateTimeOffset.Parse("2026-11-04T08:00:00Z"), RuleSetVersions = "correlation/1",
            SourceSnapshotsJson = $$"""[{"type":"SourceAnalysis","id":"{{sa}}","label":"M2LB.zip"}]""", ContentFingerprint = "c1",
        };
        var next = new InterpretationSnapshotRecord
        {
            Id = Guid.NewGuid(), Scope = "dev", CreatedAt = DateTimeOffset.Parse("2026-11-04T09:00:00Z"), RuleSetVersions = "correlation/1",
            SourceSnapshotsJson = $$"""[{"type":"SourceAnalysis","id":"{{sa}}"}]""", ContentFingerprint = "c2", PreviousId = interpretation.Id,
        };
        _canonical.Snapshots.AddRange(interpretation, next);
        _canonical.SaveChanges();
        _app.AiCodeReviewRuns.Add(new AiCodeReviewRunRecord { Id = Guid.NewGuid(), CurrentSnapshotId = sa, Label = "CRR", CompletedAt = DateTimeOffset.Parse("2026-11-04T10:00:00Z") });
        _app.SaveChanges();
        var registry = Registry();

        var lineage = (await registry.LineageAsync(SnapshotType.SourceAnalysis, sa.ToString(), 3, default))!;

        lineage.Upstream.Should().ContainSingle(e => e.Kind == SnapshotLineageKind.ImportedFrom && e.To.Type == SnapshotType.SourceImport);
        lineage.Downstream.Where(e => e.Kind == SnapshotLineageKind.InterpretedUsing).Select(e => e.From.Id)
            .Should().BeEquivalentTo([interpretation.Id.ToString(), next.Id.ToString()]);
        lineage.Downstream.Should().Contain(e => e.Kind == SnapshotLineageKind.DerivedFrom && e.From.Type == SnapshotType.Review);
        lineage.Downstream.Should().Contain(e => e.Kind == SnapshotLineageKind.Supersedes && e.From.Id == next.Id.ToString(), "the newer interpretation supersedes the older (depth 2)");
        lineage.Related.Should().Contain(m => m.Type == SnapshotType.Interpretation && m.AnalysisVersion == "correlation/1" && m.Project == "dev");
        lineage.Snapshot.SourceRevision.Should().Be("f00d");
    }

    [Fact]
    public async Task A_source_that_cannot_be_read_is_reported_and_the_others_still_sync()
    {
        _app.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = Guid.NewGuid(), IntegrationId = "source-analysis", AnalyzedAt = DateTimeOffset.UtcNow, EvidenceJson = "{ not json" });
        _app.SaveChanges();
        DeliveryReport();

        var result = await Registry().SyncAsync(true, default);

        result.Problems.Should().ContainSingle(p => p.StartsWith("Source Analysis"));
        result.Registered.Should().BeGreaterThan(0, "Delivery still registered");
        (await Registry().GetAsync(SnapshotType.GeneratedReport, _delivery.WeeklyReports.Single().Id.ToString(), default)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_running_execution_updates_its_status_but_never_its_identity()
    {
        var run = new PerformanceTestRunRecord { Id = Guid.NewGuid(), EnvironmentId = "dev", DefinitionId = "checkout", State = "Running", CreatedAt = DateTimeOffset.UtcNow, DocumentJson = "{}" };
        _app.PerformanceTestRuns.Add(run);
        _app.SaveChanges();
        var registry = Registry();
        await registry.SyncAsync(true, default);
        (await registry.GetAsync(SnapshotType.Execution, run.Id.ToString(), default))!.Status.Should().Be(SnapshotProcessingStatus.Running);

        run.State = "Completed";
        _app.SaveChanges();
        var sync = await registry.SyncAsync(true, default);

        sync.Updated.Should().Be(1);
        var after = (await registry.GetAsync(SnapshotType.Execution, run.Id.ToString(), default))!;
        after.Status.Should().Be(SnapshotProcessingStatus.Completed);
        after.Project.Should().Be("dev");
    }

    [Fact]
    public async Task Source_Analysis_selector_summaries_come_from_the_registry_without_loading_payloads_again()
    {
        var partial = SourceAnalysis("M2LB.zip", DateTimeOffset.Parse("2026-11-03T09:12:00Z"), status: "Partial");
        var failed = SourceAnalysis("Terraform.zip", DateTimeOffset.Parse("2026-11-03T09:15:00Z"), status: "Failed");
        var registry = Registry();
        await registry.SyncAsync(true, default);
        (await registry.SetPointerAsync(new(null, SnapshotType.SourceAnalysis, "source-analysis", SnapshotPointerKind.CurrentSelected, partial.ToString()), "tester", default)).Should().BeNull();

        // Break every payload: listing must still work because it never reads them again.
        foreach (var row in _app.IqrSourceSnapshots) _app.Entry(row).Property(r => r.EvidenceJson).CurrentValue = "{ broken";
        _app.SaveChanges();
        var list = (await registry.SourceAnalysisSummariesAsync(50, default))!;

        list.Select(s => s.Archive.FileName).Should().Equal("Terraform.zip", "M2LB.zip");
        list.Single(s => s.Id == failed).RegistryPointers.Should().Contain("LatestCreated").And.NotContain("LatestCompleted",
            "a Failed snapshot is never latest completed");
        list.Single(s => s.Id == partial).RegistryPointers.Should().Contain("CurrentSelected").And.NotContain("LatestCompleted", "Partial is not completed either: no fallback");
        list.Single(s => s.Id == partial).Status.Should().Be(BirkNext.Integrations.SourceAnalysisStatus.Partial);
    }

    [Fact]
    public async Task Entries_registered_before_summaries_are_backfilled_once()
    {
        var id = SourceAnalysis("M2LB.zip", DateTimeOffset.Parse("2026-11-03T09:12:00Z"));
        var registry = Registry();
        await registry.SyncAsync(true, default);
        _registry.Entries.Single().SummaryJson = null; // as registered by the earlier version
        _registry.SaveChanges();

        (await registry.SourceAnalysisSummariesAsync(50, default))!.Single().Id.Should().Be(id);
        _registry.Entries.AsNoTracking().Single().SummaryJson.Should().NotBeNull();
    }

    [Fact]
    public void The_schema_is_self_contained()
    {
        var options = new DbContextOptionsBuilder<SnapshotRegistryDbContext>();
        SnapshotRegistryDbContext.Configure(options, "Host=localhost;Database=unused");
        using var db = new SnapshotRegistryDbContext(options.Options);
        db.Model.GetEntityTypes().Should().OnlyContain(e => e.GetSchema() == SnapshotRegistryDbContext.Schema);
    }
}
