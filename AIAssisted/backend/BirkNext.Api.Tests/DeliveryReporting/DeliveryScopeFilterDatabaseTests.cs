using BirkNext.Api.Data;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Data.ScopeRegistry;
using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.Api.Services.ScopeRegistry;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.DeliveryReporting;
using BirkNext.Scopes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.DeliveryReporting;

/// <summary>
/// Report generation end to end against a real PostgreSQL (the same temporary-database harness as <see cref="DeliveryReportingDatabaseTests"/>): two
/// snapshots imported through the real import service (in-test ZIPs), an approved shared scope, a whole-project report, a scope-context report and a
/// filtered report generated, persisted, reloaded and compared; a filtered Functional Quality Scope snapshot frozen; older reports and snapshots never
/// change when newer ones are created.
/// </summary>
public sealed class DeliveryScopeFilterDatabaseTests
{
    private const string ProjectKey = "import:m2lb";

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    private static string LinkedTasks(int total, int completed) =>
        "# Tasks\n\n## Phase 1: User Story 1\n\n" + string.Join("\n", Enumerable.Range(1, total).Select(i => $"- [{(i <= completed ? "x" : " ")}] T{i:000} [US1] Implement step {i} of the story"))
        + "\n\n## Phase 2: Polish\n\n- [ ] T100 Shared clean-up\n";

    private static byte[] TwoFeatures(int childDone, int caseDone) => Zip(
    [
        ("Frontend/specs/001-child-search/spec.md", Spec("Child search", DefaultAcceptance)),
        ("Frontend/specs/001-child-search/tasks.md", LinkedTasks(4, childDone)),
        ("Frontend/specs/002-case-view/spec.md", Spec("Case view", DefaultAcceptance)),
        ("Frontend/specs/002-case-view/tasks.md", Tasks(6, caseDone)),
    ]);

    private static int? Metric(WeeklyDeliveryReport r, string key) => r.Metrics.FirstOrDefault(m => m.Key == key)?.Value;

    [Fact]
    public async Task Whole_project_and_filtered_reports_are_generated_persisted_reloaded_and_compared_without_changing_older_records()
    {
        await WithDatabase(async target =>
        {
            await using var db = Reporting(target);
            await DatabaseMigrationLock.MigrateAsync(db);
            var clock = new Clock(Friday.AddHours(2));
            var registry = new ScopeRegistryService(new ScopeRegistryDbContext(new DbContextOptionsBuilder<ScopeRegistryDbContext>()
                .UseInMemoryDatabase($"scope-filter-db-{Guid.NewGuid():N}").Options), TimeProvider.System);
            var filter = new DeliveryScopeFilterService(registry);
            var store = new DeliverySnapshotStore(db, clock);
            var imports = new DeliveryReportingImportService(db, new DeliveryImportStagingStore(TimeProvider.System), clock);
            var reports = new WeeklyReportService(db, store, clock, new ConfigurationBuilder().Build(), filter);
            var project = (await store.CreateProjectAsync(new("M2LB", null), default)).Project!.Id;

            async Task<DeliverySnapshotSummary> ImportAsync(byte[] zip, DateTimeOffset effectiveAt, DeliverySnapshotRole role, string label)
            {
                var preview = (await imports.PreviewAsync(project, "m2lb.zip", zip, default)).Preview!;
                var outcome = await imports.CommitAsync(preview.StagingId, new DeliveryImportCommitRequest { ProjectId = project, EffectiveAt = effectiveAt, Role = role, Label = label }, default);
                outcome.StatusCode.Should().Be(200, outcome.Result.Error ?? outcome.Result.DuplicateWarning);
                return outcome.Result.Snapshot!;
            }

            var baseline = await ImportAsync(TwoFeatures(1, 2), Monday, DeliverySnapshotRole.Baseline, "Monday");
            var current = await ImportAsync(TwoFeatures(3, 4), Friday, DeliverySnapshotRole.Current, "Friday");

            // 1. A report generated before scope support (no selection): stays readable and says provenance was not recorded.
            var legacy = (await reports.GenerateAsync(new(project, baseline.Id, current.Id), default)).Report!;
            var legacyJson = (await db.WeeklyReports.AsNoTracking().SingleAsync(r => r.Id == legacy.ReportId)).ContentJson;

            // The shared scope: Child search US1 and FR-001 (canonical keys from the report's own rows), one member not in this Delivery population.
            var child = legacy.Stories.Single(s => s.Title.Contains("Child search"));
            var storyKey = DeliveryScopeMembership.CanonicalKeyOf(ScopeArtifactType.UserStory, child.Module, child.FeatureKey, child.Hierarchy!.Stories.First().Key)!;
            var requirementKey = DeliveryScopeMembership.CanonicalKeyOf(ScopeArtifactType.Requirement, child.Module, child.FeatureKey, child.Hierarchy.FunctionalRequirements.First().Key)!;
            var created = (await registry.CreateAsync(new CreateScopeRequest(ProjectKey, "Release 1", null, ScopePurpose.ReleaseDelivery, null,
            [
                new() { Kind = ScopeMemberKind.UserStory, TargetId = storyKey, Label = "US1" },
                new() { Kind = ScopeMemberKind.Requirement, TargetId = requirementKey, Label = "FR-001" },
                new() { Kind = ScopeMemberKind.UserStory, TargetId = "UserStory|frontend/009-gone/US7", Label = "Gone" },
            ]), "tester")).Scope!;
            var scope = (await registry.ApproveAsync(ProjectKey, created.Scope.Id, new ScopeDecisionRequest(created.Scope.Version, 1, null), "lead")).Scope!;
            var revisionId = scope.Revisions.Single(r => r.Number == 1).Id;
            DeliveryScopeProvenance Selection(DeliveryScopeMode mode) => new()
                { Mode = mode, ProjectKey = ProjectKey, Scopes = [new(scope.Scope.Id, "Release 1", 1, revisionId)] };

            // 2. Scope context: recorded only — the same figures as the whole-project report.
            clock.Now = Friday.AddHours(3);
            var context = (await reports.GenerateAsync(new(project, baseline.Id, current.Id) { ScopeProvenance = Selection(DeliveryScopeMode.Context) }, default)).Report!;
            // 3. Filter by scope.
            clock.Now = Friday.AddHours(4);
            var outcome = await reports.GenerateAsync(new(project, baseline.Id, current.Id) { ScopeProvenance = Selection(DeliveryScopeMode.Filter) }, default);
            outcome.StatusCode.Should().Be(200, outcome.Error);

            // Reloaded from the database (the frozen document), not the generated instance.
            var whole = (await reports.GetAsync(project, context.ReportId, default))!;
            var filtered = (await reports.GetAsync(project, outcome.Report!.ReportId, default))!;
            whole.ScopeProvenance!.Filtered.Should().BeFalse();
            whole.Metrics.Should().BeEquivalentTo(legacy.Metrics, "scope context never narrows the report");

            filtered.ScopeProvenance!.Should().Match<DeliveryScopeProvenance>(p => p.Filtered && p.Mode == DeliveryScopeMode.Filter && p.Scopes.Single().RevisionId == revisionId);
            filtered.ScopeProvenance.Counts.Should().Match<DeliveryScopeFilterCounts>(c => c.Included == 2 && c.Excluded == 2 && c.MembersUnresolved == 1 && c.IncludedFeatures == 1);
            filtered.ScopeProvenance.Counts!.NotScopeResolvable.Should().Contain("Tasks completed this period");
            Metric(whole, "inventory-features").Should().Be(2);
            Metric(filtered, "inventory-features").Should().Be(1);
            Metric(filtered, "inventory-stories").Should().Be(1).And.BeLessThan(Metric(whole, "inventory-stories")!.Value);
            var linked = child.Hierarchy.Stories.First();
            Metric(filtered, "inventory-checked").Should().Be(linked.TaskCompleted, "the numerator comes from tasks linked to the in-scope story");
            Metric(filtered, "inventory-tasks").Should().Be(linked.TaskTotal, "and so does the denominator");
            Metric(filtered, "inventory-tasks").Should().BeLessThan(Metric(whole, "inventory-tasks")!.Value);
            Metric(filtered, "tasks-completed").Should().BeNull("not scope-resolvable: never a whole-project number under a scope");
            filtered.Stories.Should().ContainSingle().Which.FeatureKey.Should().Be(child.FeatureKey);
            (await db.WeeklyReportItems.AsNoTracking().Where(i => i.ReportId == filtered.ReportId && i.Section == "Story").Select(i => i.FeatureKey).ToListAsync())
                .Should().OnlyContain(k => k == child.FeatureKey, "the persisted report items are the filtered ones");

            // Legacy report: readable, "not recorded", and byte-for-byte unchanged after newer reports were generated.
            DeliveryScopeProvenanceText.Describe((await reports.GetAsync(project, legacy.ReportId, default))!.ScopeProvenance).Should().Be(DeliveryScopeProvenanceText.NoSelection,
                "a report generated without a selection records that none was chosen (only documents from before scope support say 'not recorded')");
            (await db.WeeklyReports.AsNoTracking().SingleAsync(r => r.Id == legacy.ReportId)).ContentJson.Should().Be(legacyJson);

            var rows = await reports.ListAsync(project, default);
            rows.Single(r => r.Id == filtered.ReportId).ScopeFilterLabel.Should().Be("Filtered by scope: Release 1 rev 1");
            rows.Single(r => r.Id == legacy.ReportId).ScopeFilterLabel.Should().BeEmpty();
            (await reports.ExportAsync(project, filtered.ReportId, DeliveryExportFormat.Markdown, new(), default))!.Content
                .Should().Contain("**Scope filter:** 2 in-scope items · ").And.Contain("containing in-scope items · 2 items excluded (outside scope) · 1 scope member unresolved");

            // Report vs report: whole vs whole is like-for-like; whole vs filtered is not (raw counts, no percentages).
            var likeForLike = (await reports.CompareAsync(project, legacy.ReportId, context.ReportId, default)).Comparison!;
            likeForLike.LikeForLike.Should().BeTrue();
            likeForLike.Metrics.Should().OnlyContain(m => m.Delta == 0);
            var mixed = (await reports.CompareAsync(project, context.ReportId, filtered.ReportId, default)).Comparison!;
            mixed.LikeForLike.Should().BeFalse();
            mixed.Mismatches.Should().Contain("Different scope modes: whole population vs filtered by scope.");
            mixed.Metrics.Single(m => m.Key == "inventory-features").Should().Match<DeliveryReportMetricDifference>(d => d.Baseline == 2 && d.Current == 1 && d.PercentChange == null);
            mixed.Baseline.ReportId.Should().Be(context.ReportId);
            mixed.Current.Filtered.Should().BeTrue();
            (await reports.CompareAsync(project, legacy.ReportId, Guid.NewGuid(), default)).Error.Should().Contain("Both reports");

            // A filtered Functional Quality Scope snapshot of the same project; the earlier snapshot is unchanged.
            var fqs = new FunctionalQualityScopeService(db, clock, scopeFilter: filter);
            var plain = await fqs.FreezeAsync(project, new ScopeQuery($"module:{child.Module}", null, false) { ScopeProvenance = Selection(DeliveryScopeMode.Context) }, default);
            var plainJson = (await db.ScopeSnapshots.AsNoTracking().SingleAsync(s => s.Id == plain.Id)).ItemsJson;
            var narrowed = await fqs.FreezeAsync(project, new ScopeQuery($"module:{child.Module}", null, false) { ScopeProvenance = Selection(DeliveryScopeMode.Filter) }, default);
            narrowed.ItemCount.Should().BeGreaterThan(0).And.BeLessThan(plain.ItemCount);
            narrowed.ScopeProvenance!.Counts.Should().Match<DeliveryScopeFilterCounts>(c => c.MembersMatched == 2 && c.MembersUnresolved == 1 && c.Included == narrowed.ItemCount);
            (await db.ScopeSnapshots.AsNoTracking().SingleAsync(s => s.Id == plain.Id)).ItemsJson.Should().Be(plainJson);
        });
    }

    private static DeliveryReportingDbContext Reporting(string connectionString)
    {
        var options = new DbContextOptionsBuilder<DeliveryReportingDbContext>();
        DeliveryReportingDbContext.Configure(options, connectionString);
        return new DeliveryReportingDbContext(options.Options);
    }

    private static async Task WithDatabase(Func<string, Task> body)
    {
        var database = TestPostgres.TemporaryName("birknext_delivery_scope");
        var admin = TestPostgres.Admin().ConnectionString;
        var target = TestPostgres.Temporary(database).ConnectionString;
        await Execute(admin, $"CREATE DATABASE \"{database}\"");
        try { await body(target); }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Execute(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
