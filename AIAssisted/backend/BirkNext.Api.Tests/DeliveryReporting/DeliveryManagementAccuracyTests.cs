using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.DeliveryReporting;
using FluentAssertions;
using Xunit;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.DeliveryReporting;

/// <summary>
/// Delivery Management accuracy: report kind and comparison warnings (same day, identical content, unknown revision), removals under incomplete
/// coverage, and the counter invariants every view relies on (checked + open = total, phases + unphased = total, no double-counted tasks).
/// </summary>
public sealed class DeliveryManagementAccuracyTests
{
    private static readonly TimeZoneInfo Oslo = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Oslo");

    private static WeeklyDeliveryReport Report(DeliverySnapshotContent b, DeliverySnapshotContent c) =>
        WeeklyReportBuilder.Build(Guid.NewGuid(), Guid.NewGuid(), "M2LB", c.Ref.EffectiveAt, b, c, DeliverySnapshotComparison.Compare(b, c), Oslo);

    private static readonly (string, string)[] Repo = ChildSearch(8, 3);

    [Fact]
    public void SameDayComparison_IsAChangeReport_WithAWarning()
    {
        var report = Report(Content(Parse(Repo), Friday.AddHours(-5), "morning"), Content(Parse(ChildSearch(8, 5)), Friday, "afternoon"));
        report.Kind.Should().Be(DeliveryReportKind.Change);
        report.Title.Should().Be("M2LB Delivery Change Report");
        report.ComparisonWarnings.Should().Contain(w => w.StartsWith("Same-day comparison"));
        report.CalculationVersion.Should().Be(WeeklyReportBuilder.CalculationVersion);
    }

    [Fact]
    public void BaselineAtTheStartOfTheWeek_IsAWeeklyReport()
    {
        var report = Report(Content(Parse(Repo), Monday, "monday"), Content(Parse(ChildSearch(8, 5)), Friday, "friday"));
        report.Kind.Should().Be(DeliveryReportKind.Weekly);
        report.Title.Should().Be("M2LB Weekly Delivery Status");
        report.ComparisonWarnings.Should().NotContain(w => w.StartsWith("Same-day"));
    }

    [Fact]
    public void BaselineWeeksEarlier_IsAChangeReport_NotWeekly()
    {
        var report = Report(Content(Parse(Repo), Monday.AddDays(-21), "old"), Content(Parse(ChildSearch(8, 5)), Friday, "friday"));
        report.Kind.Should().Be(DeliveryReportKind.Change);
    }

    [Fact]
    public void IdenticalContent_AndUnknownRevisions_AreWarned()
    {
        var parsed = Parse(Repo);
        var report = Report(Content(parsed, Monday, "a"), Content(parsed, Friday, "b"));
        report.ComparisonWarnings.Should().Contain(w => w.Contains("identical content"));
        report.ComparisonWarnings.Should().Contain(w => w.Contains("source revision") && w.Contains("unknown"));
    }

    [Fact]
    public void AbsentFeature_UnderIncompleteCoverage_IsMissing_NotAConfirmedRemoval()
    {
        var baseline = Content(Parse([.. Repo, .. ChildSearch(4, 1, module: "Backend")]), Monday);
        var current = Content(Parse(Repo), Friday) with { Coverage = DeliveryCoverage.Partial };
        var report = Report(baseline, current);
        var missing = report.Stories.Single(s => s.RemovedFeature);
        missing.RemovalUnconfirmed.Should().BeTrue();
        missing.Movement.Should().Be(DeliveryMovement.NeedsReview);
        report.ScopeChanges.Single(sc => sc.FeatureKey == missing.FeatureKey).Kind.Should().Be("Missing");

        var confirmed = Report(baseline, current with { Coverage = DeliveryCoverage.Complete });
        confirmed.ScopeChanges.Single(sc => sc.FeatureKey == missing.FeatureKey).Kind.Should().Be("Removed");
        confirmed.Stories.Single(s => s.RemovedFeature).RemovalUnconfirmed.Should().BeFalse();
    }

    [Fact]
    public void DuplicateTaskTexts_GetDistinctKeys_AndAreCountedOnce()
    {
        var tasks = """
            # Tasks
            ## Phase 1: Setup
            - [x] T001 Write the migration
            - [ ] T001 Write the migration
            - [x] Configure logging
            - [x] Configure logging
            ## Notes
            - [ ] Something outside a phase
            """;
        var parsed = Parse([("Frontend/specs/001-child-search/spec.md", Spec("Child search")), ("Frontend/specs/001-child-search/tasks.md", tasks)]);
        var feature = parsed.Features.Single();
        feature.Tasks.Select(t => t.TaskKey).Should().OnlyHaveUniqueItems();
        var h = DeliverySnapshotComparison.Hierarchy(null, Content(parsed, Friday).Features.Single());
        AssertInvariants(feature.TaskTotal, feature.TaskCompleted, h);
        h.Tasks.Count(t => t.PhaseKey is null).Should().Be(1, "a task outside a Phase group is unphased, never invented into a phase");
    }

    /// <summary>Real M2LB archives (only where the developer has them): every feature's counters reconcile with its task records.</summary>
    [Fact]
    public void RealM2lbArchives_EveryFeatureReconciles()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var paths = new[] { "M2LB.zip", "M2LB (1).zip" }.Select(n => Path.Combine(downloads, n)).Where(File.Exists).ToList();
        if (paths.Count < 2) return;
        var b = Content(DeliverySnapshotParser.Parse("M2LB.zip", File.ReadAllBytes(paths[0])).Snapshot!, Monday);
        var c = Content(DeliverySnapshotParser.Parse("M2LB (1).zip", File.ReadAllBytes(paths[1])).Snapshot!, Friday);
        var report = Report(b, c);
        report.Stories.Should().HaveCount(c.Features.Count + report.Stories.Count(s => s.RemovedFeature));
        foreach (var row in report.Stories.Where(s => !s.RemovedFeature))
            AssertInvariants(row.CurrentTaskTotal, row.CurrentTaskCompleted, row.Hierarchy!);
    }

    private static void AssertInvariants(int total, int completed, DeliveryFeatureHierarchy h)
    {
        var live = h.Tasks.Where(t => t.Change != DeliveryTaskChange.Removed).ToList();
        live.Should().HaveCount(total, "the detail task list is the summary's population");
        live.Count(t => t.Completed).Should().Be(completed);
        (live.Count(t => t.Completed) + live.Count(t => !t.Completed)).Should().Be(total);
        live.Select(t => t.Key).Should().OnlyHaveUniqueItems("a task is counted once");
        (h.Phases.Sum(p => p.TaskTotal) + live.Count(t => t.PhaseKey is null)).Should().Be(total, "phases plus unphased tasks reconcile with the inventory");
        h.Stories.Sum(s => s.TaskTotal).Should().BeLessThanOrEqualTo(total, "a task belongs to at most one story, so story totals never exceed the feature");
    }
}
