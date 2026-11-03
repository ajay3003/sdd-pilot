using BirkNext.DeliveryReporting;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Delivery Overview dashboard semantics: current state apart from period change; matched completions apart from the net checked delta; a checked
/// checklist is never QA, acceptance or a schedule claim; unknown states are words, never 0%; the requirement model is said as it is.
/// </summary>
public sealed class DeliveryDashboardTests
{
    private static DeliveryStoryRow Feature(string key, int baseDone, int baseTotal, int done, int total, bool isNew = false, int completedThisPeriod = 0,
        DeliveryFeatureHierarchy? h = null, DeliveryStatus status = DeliveryStatus.InProgress, List<string>? attention = null) => new()
    {
        FeatureKey = key, Title = key, Module = "M", BaselineTaskCompleted = baseDone, BaselineTaskTotal = baseTotal, CurrentTaskCompleted = done, CurrentTaskTotal = total,
        NewFeature = isNew, TasksCompletedThisPeriod = completedThisPeriod, Hierarchy = h, CurrentStatus = status, Attention = attention ?? [],
        Movement = isNew ? DeliveryMovement.NewThisPeriod : completedThisPeriod > 0 ? DeliveryMovement.Progressed : DeliveryMovement.NoSignificantChange,
        Evidence = new() { SpecPath = "spec.md", TasksPath = "tasks.md" },
    };

    private static WeeklyDeliveryReport Report(params DeliveryStoryRow[] stories) => new()
    {
        ProjectName = "M2LB", Stories = [.. stories], PeriodCoverage = DeliveryPeriodCoverage.SameDay, CoverageText = "Coverage: 3 Nov only (same-day comparison)",
        Current = new() { Label = "M2LB-language" }, Baseline = new() { Label = "M2LB-main" },
        Metrics = [new("new-features", "New features", stories.Count(s => s.NewFeature)), new("tasks-completed", "Tasks completed this period", stories.Sum(s => s.TasksCompletedThisPeriod)), new("tasks-added", "Tasks added", 0)],
    };

    [Fact]
    public void Net_checked_change_is_not_called_completed_and_differs_from_matched_transitions()
    {
        // A retained feature unchanged (10/10) and a new feature arriving with 44/45 checked: +44 net checked, 0 matched completions.
        var report = Report(Feature("a", 10, 10, 10, 10), Feature("b", 0, 0, 44, 45, isNew: true));
        var tasks = DeliveryDashboard.TaskChanges(report).ToDictionary(b => b.Key);
        tasks["matched-checked"].Value.Should().Be(0);
        tasks["net-checked"].Value.Should().Be(44);
        tasks["net-checked"].Label.Should().Be("Net checked-task change").And.NotContain("completed");
        tasks["tasks-added"].Value.Should().Be(45, "the new feature's tasks are new task identities");
    }

    [Fact]
    public void Quality_states_are_words_and_checklist_complete_is_not_acceptance_or_qa()
    {
        var view = DeliveryBacklog.FromReport(Report(Feature("a", 0, 0, 5, 5, status: DeliveryStatus.TasksComplete)));
        var q = DeliveryDashboard.Quality(view).ToDictionary(x => x.Key);
        (q["implementation"].State, q["qa"].State, q["acceptance"].State).Should().Be(("Not recorded", "Not assessed", "Not recorded"));
        q.Values.Should().OnlyContain(x => !x.State.Contains('%'), "a category is never turned into a percentage");
        DeliveryBacklog.Label(BacklogStatus.Accepted).Should().Be("Stated done", "a Done status line is a statement, not a recorded acceptance");
        DeliveryDashboard.Current(view).Should().NotContain(k => k.Label.Contains("On track"));
    }

    [Fact]
    public void Same_day_header_says_so_and_order_is_not_confirmed()
    {
        var report = Report(Feature("a", 1, 2, 2, 2, completedThisPeriod: 1));
        var header = DeliveryDashboard.Header(DeliveryBacklog.FromReport(report), report, "M2LB");
        (header.SameDay, header.OrderUnconfirmed, header.Coverage).Should().Be((true, true, "3 Nov only (same-day comparison)"));
        header.Title.Should().NotContain("Weekly");
    }

    [Fact]
    public void Requirement_model_says_functional_groups_scenarios_or_stories_as_they_are()
    {
        var functional = new DeliveryFeatureHierarchy { Captured = true, Groups = [new("US1", DeliveryTaskRelationship.FunctionalGroup, "US1") { TaskTotal = 9 }, new("US2", DeliveryTaskRelationship.FunctionalGroup, "US2") { TaskTotal = 6 }] };
        var scenarios = new DeliveryFeatureHierarchy { Captured = true, Stories = [new("S1", "Timeline", DeliveryItemChange.Unchanged) { Kind = "Scenario" }] };
        var stories = new DeliveryFeatureHierarchy { Captured = true, CriteriaCurrent = 13, Stories = [new("US1", "A", DeliveryItemChange.Unchanged), new("US2", "B", DeliveryItemChange.Unchanged), new("US3", "C", DeliveryItemChange.Unchanged) { Removed = true }] };
        var item = new BacklogItem("k", "t", "m", BacklogStatus.InProgress, "", 0, 0, 0, 0, 0, null) { HasSpecification = true };

        DeliveryDashboard.RequirementModel(Feature("f", 0, 0, 0, 0, h: functional), item).Should().Be("2 functional groups · 15 tasks");
        DeliveryDashboard.RequirementModel(Feature("s", 0, 0, 0, 0, h: scenarios), item).Should().Be("Scenario-based requirements · 1 scenario");
        DeliveryDashboard.RequirementModel(Feature("u", 0, 0, 0, 0, h: stories), item).Should().Be("2 User Stories · 13 criteria", "a removed story is not counted");
    }

    [Fact]
    public void Rows_put_blocked_review_new_and_changed_first_with_change_badges()
    {
        var report = Report(Feature("stable", 5, 5, 5, 5, status: DeliveryStatus.TasksComplete), Feature("new", 0, 0, 1, 2, isNew: true),
            Feature("review", 1, 2, 1, 2, attention: ["Unresolved story reference"]), Feature("blocked", 1, 2, 1, 2, status: DeliveryStatus.Blocked));
        var rows = DeliveryDashboard.Rows(DeliveryBacklog.FromReport(report), report);
        rows.Select(r => r.Item.FeatureKey).Should().Equal("blocked", "review", "new", "stable");
        rows.Single(r => r.Item.FeatureKey == "new").Change.Should().Be("NEW");
        rows.Single(r => r.Item.FeatureKey == "stable").Change.Should().Be("UNCHANGED");
        rows.Single(r => r.Item.FeatureKey == "review").StrongestAttention.Should().Be("Unresolved story reference");
    }
}
