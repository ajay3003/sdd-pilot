using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.DeliveryReporting;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.DeliveryReporting;

/// <summary>
/// Source-backed traceability and comparison integrity: English/Norwegian headings, functional requirements kept separate, explicit [USn] links
/// only (unresolved references stay visible, untagged tasks stay feature-level), [P] markers, "no task list" never 0/0, direction review before
/// generation (fewer features/tasks needs confirmation, never auto-reversed), period coverage that never implies a week from one day, period
/// metrics separated from inventory and quality, completed-this-period = matched [ ]→[x] only, and audited effective-date corrections.
/// </summary>
public sealed class DeliveryTraceabilityTests
{
    private static readonly TimeZoneInfo Oslo = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Oslo");

    private const string NorwegianSpec = """
        # Funksjonsspesifikasjon: Barnesøk

        ### Brukerhistorie 1 - Saksbehandler søker etter barn (Prioritet: P2)

        En saksbehandler søker.

        **Akseptansescenarier**:

        1. **Gitt** en saksbehandler, **Når** de søker, **Så** vises barn

        ## Krav

        - **FR-001**: Systemet MÅ støtte søk.
        - **FR-002**: Systemet MÅ logge søk.
        """;

    private const string NorwegianTasks = """
        # Oppgaver

        ## Fase 1: Oppsett

        - [x] T001 [P] Opprett prosjekt
        - [ ] T002 [US1] Lag søkeside
        - [ ] T003 [US9] Ukjent historie
        - [x] T004 Delt infrastruktur
        """;

    private static ParsedDeliverySnapshot Snapshot(params (string, string)[] files) => Parse(files);

    [Fact]
    public void Norwegian_headings_stories_priorities_phases_and_requirements_are_parsed_from_source()
    {
        var feature = Snapshot(("Frontend/specs/001-barnesok/spec.md", NorwegianSpec), ("Frontend/specs/001-barnesok/tasks.md", NorwegianTasks)).Features.Single();
        feature.Stories.Should().ContainSingle().Which.Should().Match<ParsedDeliveryStory>(s => s.Key == "US1" && s.Priority == "P2");
        feature.Phases.Should().ContainSingle().Which.Key.Should().Be("phase-1", "\"Fase 1\" is a phase heading");
        feature.Requirements.Select(r => r.Key).Should().Equal("FR-001", "FR-002");
        feature.Criteria.Should().NotContain(c => c.Text.Contains("FR-001"), "functional requirements are never acceptance criteria");
        var tasks = feature.Tasks.ToDictionary(t => t.TaskId!);
        tasks["T001"].Parallel.Should().BeTrue();
        tasks["T002"].UserStoryKey.Should().Be("US1");
        tasks["T003"].Should().Match<ParsedDeliveryTask>(t => t.UserStoryKey == null && t.UserStory == "US9", "an unknown reference is kept as written, never re-pointed");
        tasks["T004"].UserStoryKey.Should().BeNull("an untagged task is not assigned to a story");
        tasks.Values.Should().OnlyContain(t => t.PhaseKey == "phase-1", "untagged tasks keep their phase");
    }

    [Fact]
    public void The_hierarchy_keeps_unresolved_references_visible_and_requirements_feature_level()
    {
        var parsed = Snapshot(("Frontend/specs/001-barnesok/spec.md", NorwegianSpec), ("Frontend/specs/001-barnesok/tasks.md", NorwegianTasks));
        var content = Content(parsed, Friday).Features.Single();
        var h = DeliverySnapshotComparison.Hierarchy(null, content);
        h.FunctionalRequirements.Select(r => r.Key).Should().Equal("FR-001", "FR-002");
        h.Tasks.Single(t => t.TaskId == "T003").UnresolvedStoryReference.Should().Be("US9");
        h.Tasks.Single(t => t.TaskId == "T004").Should().Match<DeliveryTaskRow>(t => t.UserStoryKey == null && t.UnresolvedStoryReference == null);
        h.Tasks.Single(t => t.TaskId == "T001").Parallel.Should().BeTrue();
        h.HasTaskList.Should().BeTrue();
    }

    [Fact]
    public void A_specification_without_tasks_says_no_task_list_never_zero_of_zero()
    {
        var parsed = Snapshot(("Frontend/specs/004-settings-devmode/spec.md", Spec("Developer mode", DefaultAcceptance)));
        var baseline = Content(parsed, Monday);
        var current = Content(parsed, Friday);
        var row = DeliverySnapshotComparison.Compare(baseline, current).Stories.Single();
        row.Hierarchy!.HasTaskList.Should().BeFalse();
        row.Reasons.Should().ContainSingle().Which.Should().Be("Unchanged · no task list found (progress cannot be derived from tasks)");
        string.Join(" ", row.Reasons).Should().NotContain("0/0");
        DeliveryReportStories.TaskProgress(0, 0, hasTaskList: false).Should().Be("No task list found — progress cannot be derived from tasks");
    }

    private static (string, string)[] Feature(string folder, int total, int completed) =>
        [($"Frontend/specs/{folder}/spec.md", Spec(folder, DefaultAcceptance)), ($"Frontend/specs/{folder}/tasks.md", Tasks(total, completed))];

    [Fact]
    public void Fewer_features_and_tasks_in_current_needs_direction_confirmation_and_is_never_auto_reversed()
    {
        var baseline = Content(Parse([.. Feature("001-a", 10, 9), .. Feature("002-b", 6, 6)]), Monday);
        var current = Content(Parse([.. Feature("001-a", 10, 9)]), Friday);
        var preview = WeeklyReportBuilder.Preview(baseline, current, Oslo);
        preview.Direction.Should().Be(DeliveryDirectionState.InventoryReduced);
        (preview.FeatureDelta, preview.TaskDelta, preview.CheckedDelta).Should().Be((-1, -6, -6));
        preview.MissingFeatures.Should().ContainSingle();
        preview.RequiresConfirmation.Should().BeTrue();
        preview.CanGenerate.Should().BeTrue("a scope reduction can be legitimate — it is reviewed, not refused");
        preview.Warnings[0].Should().Contain("fewer features and tasks than Baseline").And.Contain("Verify that snapshot direction is correct");
        preview.Baseline.SnapshotId.Should().Be(baseline.Ref.SnapshotId, "the preview never swaps the pair itself");

        var inverted = WeeklyReportBuilder.Preview(current, baseline, Oslo);
        inverted.Direction.Should().Be(DeliveryDirectionState.BaselineLater);
        inverted.CanGenerate.Should().BeFalse();

        var growth = WeeklyReportBuilder.Preview(current, baseline with { Ref = baseline.Ref with { EffectiveAt = Friday.AddDays(1) } }, Oslo);
        growth.Direction.Should().Be(DeliveryDirectionState.Chronological);
        growth.RequiresConfirmation.Should().BeFalse();
    }

    [Fact]
    public void Same_day_snapshots_are_a_same_day_comparison_never_a_week_and_full_weeks_are_titled_as_such()
    {
        var parsed = Parse(Feature("001-a", 4, 2));
        var morning = Content(parsed, Friday.AddHours(-6));
        var evening = Content(parsed, Friday);
        var sameDay = Build(morning, evening);
        sameDay.PeriodCoverage.Should().Be(DeliveryPeriodCoverage.SameDay);
        sameDay.CoverageText.Should().Be("Coverage: 30 Oct only (same-day comparison)");
        DeliveryReportTitles.Display(sameDay).Should().Be("M2LB Delivery Change Report — same-day comparison (W44)").And.NotContain("Weekly Delivery Status");

        var week = Build(Content(parsed, Monday), evening);
        week.PeriodCoverage.Should().Be(DeliveryPeriodCoverage.FullPeriod);
        DeliveryReportTitles.Display(week).Should().Be("M2LB Weekly Delivery Status — W44");
        DeliveryReportTitles.Display(week with { Version = 2 }).Should().Be($"{week.Title} — {week.WeekLabel}", "older frozen reports keep their own title");
    }

    private static WeeklyDeliveryReport Build(DeliverySnapshotContent b, DeliverySnapshotContent c, DeliveryDirectionReview? direction = null) =>
        WeeklyReportBuilder.Build(Guid.NewGuid(), Guid.NewGuid(), "M2LB", Friday, b, c, DeliverySnapshotComparison.Compare(b, c), Oslo, direction);

    [Fact]
    public void Completed_this_period_counts_matched_transitions_only_and_metrics_keep_period_inventory_and_quality_apart()
    {
        var baseline = Content(Parse([.. Feature("001-a", 4, 1)]), Monday);
        // 001-a: T002 completed this period; new feature 002-b arrives with 5 already-checked tasks.
        var current = Content(Parse([.. Feature("001-a", 4, 2), .. Feature("002-b", 5, 5)]), Friday);
        var report = Build(baseline, current);
        report.Metrics.Single(m => m.Key == "tasks-completed").Value.Should().Be(1, "checked tasks of a newly imported feature are not work completed this period");
        report.Metrics.Single(m => m.Key == "inventory-tasks").Should().Match<DeliveryReportMetric>(m => m.Value == 9 && m.Group == DeliveryMetricGroups.Inventory);
        report.Metrics.Single(m => m.Key == "inventory-checked").Value.Should().Be(7);
        report.Metrics.Single(m => m.Key == "quality-scope-review").Group.Should().Be(DeliveryMetricGroups.Quality);
        report.Metrics.Single(m => m.Key == "tasks-completed").Group.Should().Be(DeliveryMetricGroups.Period);
        report.Version.Should().Be(WeeklyDeliveryReport.CurrentVersion);
        report.ParserVersion.Should().Be(DeliverySnapshotParser.ParserVersion);

        var reopened = Build(Content(Parse([.. Feature("001-a", 4, 3)]), Monday), Content(Parse([.. Feature("001-a", 3, 2)]), Friday));
        reopened.Metrics.Single(m => m.Key == "tasks-reopened").Value.Should().Be(1, "T003 went from checked to unchecked; T004 was removed, which is never counted as reopened");
        reopened.Metrics.Single(m => m.Key == "tasks-removed").Value.Should().Be(1);
    }

    [Fact]
    public void A_confirmed_direction_is_frozen_with_the_report()
    {
        var baseline = Content(Parse([.. Feature("001-a", 10, 9), .. Feature("002-b", 6, 6)]), Monday);
        var current = Content(Parse([.. Feature("001-a", 10, 9)]), Friday);
        var preview = WeeklyReportBuilder.Preview(baseline, current, Oslo);
        var report = Build(baseline, current, new DeliveryDirectionReview { State = preview.Direction, Confirmed = true, BaselineInventory = preview.BaselineInventory, CurrentInventory = preview.CurrentInventory });
        report.Direction!.Should().Match<DeliveryDirectionReview>(d => d.State == DeliveryDirectionState.InventoryReduced && d.Confirmed && d.BaselineInventory.Features == 2);
        report.Metrics.Should().Contain(m => m.Key == "quality-direction");
        DeliveryReportExporter.Markdown(report, new DeliveryExportOptions()).Should().Contain("**Snapshot direction:** InventoryReduced (explicitly confirmed)");
    }

    [Theory]
    [InlineData(DeliveryAttentionKind.FeatureRemoved, DeliveryAttentionCategory.ScopeChange)]
    [InlineData(DeliveryAttentionKind.TasksDisappeared, DeliveryAttentionCategory.ScopeChange)]
    [InlineData(DeliveryAttentionKind.BrokenTraceability, DeliveryAttentionCategory.DataQuality)]
    [InlineData(DeliveryAttentionKind.SpecificationWithoutTasks, DeliveryAttentionCategory.DataQuality)]
    [InlineData(DeliveryAttentionKind.AmbiguousFeatureIdentity, DeliveryAttentionCategory.DataQuality)]
    [InlineData(DeliveryAttentionKind.Blocked, DeliveryAttentionCategory.DeliverySignal)]
    [InlineData(DeliveryAttentionKind.TaskCompletionWentBackwards, DeliveryAttentionCategory.DeliverySignal)]
    public void Attention_is_split_into_scope_change_data_quality_and_delivery_signals(DeliveryAttentionKind kind, DeliveryAttentionCategory expected) =>
        DeliveryAttentionCategories.Of(kind).Should().Be(expected);

    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    [Fact]
    public async Task An_effective_date_correction_is_explicit_and_audited_and_never_changes_contents()
    {
        await using var db = new DeliveryReportingDbContext(new DbContextOptionsBuilder<DeliveryReportingDbContext>().UseInMemoryDatabase($"dr-{Guid.NewGuid()}").Options);
        var clock = new Clock(Friday.AddDays(3));
        var store = new DeliverySnapshotStore(db, clock);
        var project = (await store.CreateProjectAsync(new("M2LB", null), default)).Project!.Id;
        var source = new DeliveryReportSourceRecord { Id = Guid.NewGuid(), ProjectId = project, SourceType = nameof(DeliverySourceType.ZipUpload), OriginalFileName = "M2LB.zip", Fingerprint = "f", ImportedAt = Friday };
        var snapshot = new DeliverySnapshotRecord { Id = Guid.NewGuid(), ProjectId = project, ReportSourceId = source.Id, Label = "Current", EffectiveAt = Friday.ToUniversalTime(), ImportedAt = Friday,
            Role = nameof(DeliverySnapshotRole.Current), CoverageStatus = nameof(DeliveryCoverage.Complete), ParseStatus = "Parsed", TaskCount = 7 };
        db.ReportSources.Add(source);
        db.Snapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var corrected = await store.UpdateSnapshotAsync(project, snapshot.Id, new UpdateDeliverySnapshotRequest(null, null, Friday.AddDays(-2), "Archive represents Wednesday's repository"), default);
        corrected!.EffectiveAt.Should().Be(Friday.AddDays(-2).ToUniversalTime());
        corrected.EffectiveAtProvenance.Should().Be("Corrected");
        corrected.EffectiveAtCorrections.Should().ContainSingle().Which.Should().Match<DeliveryEffectiveDateCorrection>(c =>
            c.Previous == Friday.ToUniversalTime() && c.Reason == "Archive represents Wednesday's repository" && c.CorrectedAt == clock.GetUtcNow());
        corrected.ImportedAt.Should().Be(Friday, "imported-at is never overwritten by an effective-date correction");
        corrected.TaskCount.Should().Be(7, "contents never change");
    }
}
