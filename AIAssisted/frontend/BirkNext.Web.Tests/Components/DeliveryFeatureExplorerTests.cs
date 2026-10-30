using BirkNext.DeliveryReporting;
using BirkNext.Web.Components;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Delivery Reporting → Stories as a business-facing hierarchy: features grouped by module (changed first), scope changes apart from attention,
/// a feature detail with Overview / Phases (only when explicit) / User Stories / Acceptance Criteria / Tasks / Changes / Evidence, user stories
/// under their parent feature (and phase), acceptance criteria per story with Added/Changed badges and the baseline only when changed, and
/// tasks grouped by explicit traceability. A v1 report without hierarchy still renders.
/// </summary>
public sealed class DeliveryFeatureExplorerTests : BunitContext
{
    public DeliveryFeatureExplorerTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly DeliveryFeatureHierarchy Registration = new()
    {
        Captured = true,
        Phases =
        [
            new("phase-1", "Phase 1: Foundation", DeliveryItemChange.Unchanged) { Position = 0, TaskTotal = 3, TaskCompleted = 3, StoryKeys = ["US1", "US2"] },
            new("phase-2", "Phase 2: Hardening", DeliveryItemChange.Changed) { Position = 1, TaskTotal = 2, TaskCompleted = 1, StoryKeys = ["US3"], Changes = 1 },
        ],
        Stories =
        [
            new("US1", "Register child", DeliveryItemChange.Changed)
            {
                Description = "A caseworker registers a child.", Priority = "P1", PhaseKeys = ["phase-1"], TaskTotal = 1, TaskCompleted = 1, CriteriaChanged = true,
                Criteria = [new("AC-1", "Child is saved and confirmed", DeliveryItemChange.Changed) { StoryKey = "US1", BaselineText = "Child is saved" }, new("AC-2", "Missing name shows an error", DeliveryItemChange.Unchanged) { StoryKey = "US1" }],
            },
            new("US2", "Handle unknown category", DeliveryItemChange.Unchanged) { PhaseKeys = ["phase-1"], TaskTotal = 1, TaskCompleted = 1, Criteria = [new("AC-1", "Unknown category is flagged", DeliveryItemChange.Unchanged) { StoryKey = "US2" }] },
            new("US3", "Audit registrations", DeliveryItemChange.Added) { PhaseKeys = ["phase-2"], TaskTotal = 1, Criteria = [new("AC-1", "An audit entry exists", DeliveryItemChange.Added) { StoryKey = "US3" }] },
        ],
        FeatureCriteria = [new("SC-001", "Registrations are audited", DeliveryItemChange.Unchanged)],
        Tasks =
        [
            new("T001", "T001", "Create registration form", true, DeliveryTaskChange.Unchanged) { PhaseKey = "phase-1", UserStoryKey = "US1", SourcePath = "Frontend/specs/002-registration/tasks.md" },
            new("T002", "T002", "Map categories", true, DeliveryTaskChange.Unchanged) { PhaseKey = "phase-1", UserStoryKey = "US2" },
            new("T003", "T003", "Configure logging", true, DeliveryTaskChange.Unchanged) { PhaseKey = "phase-1" },
            new("T010", "T010", "Write audit entries", true, DeliveryTaskChange.CompletedThisPeriod) { PhaseKey = "phase-2", UserStoryKey = "US3" },
            new("T011", "T011", "Review validation messages", false, DeliveryTaskChange.Unchanged) { PhaseKey = "phase-2" },
            new("T020", "T020", "Update documentation", false, DeliveryTaskChange.New),
        ],
        CriteriaBaseline = 4, CriteriaCurrent = 5, CriteriaAdded = 1, CriteriaChanged = 1, StoriesAdded = 1, StoriesChanged = 1, TasksAdded = 1,
    };

    private static WeeklyDeliveryReport Report() => new()
    {
        Version = 2, ProjectName = "M2LB", WeekLabel = "2026-W44", Baseline = new() { Label = "M2LB (1)(9).zip" }, Current = new() { Label = "M2LB(20261030-100817).zip" },
        Stories =
        [
            new() { FeatureKey = "skoleservice::001-skoleservice", Title = "School attendance reporting", Module = "SkoleService", Movement = DeliveryMovement.NeedsReview, SpecificationChanged = true,
                BaselineStatus = DeliveryStatus.InProgress, CurrentStatus = DeliveryStatus.InProgress, CurrentTaskCompleted = 173, CurrentTaskTotal = 175, BaselineTaskCompleted = 173, BaselineTaskTotal = 175,
                Hierarchy = Registration, Evidence = new() { SpecPath = "SkoleService/specs/001-skoleservice/spec.md", TasksPath = "SkoleService/specs/001-skoleservice/tasks.md" } },
            new() { FeatureKey = "tjeneste::014-children-in-placements-per-period", Title = "Children in Placements per Period", Module = "Tjeneste", Movement = DeliveryMovement.Removed,
                RemovedFeature = true, BaselineStatus = DeliveryStatus.InProgress, BaselineTaskCompleted = 54, BaselineTaskTotal = 56,
                Hierarchy = new() { Captured = true, Stories = [new("US1", "List placements", DeliveryItemChange.Unchanged)] } },
            new() { FeatureKey = "tjeneste::002-quiet", Title = "Quiet feature", Module = "Tjeneste", Movement = DeliveryMovement.NoSignificantChange, CurrentTaskCompleted = 2, CurrentTaskTotal = 2 },
        ],
        ScopeChanges = [new("Removed", "tjeneste::014-children-in-placements-per-period", "Children in Placements per Period", "Tjeneste", "In the baseline, not in the current snapshot. Last known baseline: 54/56 tasks.")],
    };

    private IRenderedComponent<DeliveryFeatureExplorer> Render(WeeklyDeliveryReport? report = null) =>
        Render<DeliveryFeatureExplorer>(p => p.Add(x => x.Report, report ?? Report()).Add(x => x.ProjectName, "M2LB"));

    private static void Tab(IRenderedComponent<DeliveryFeatureExplorer> cut, string tab) => cut.FindAll("[data-testid=dlr-feature-tab]").Single(t => t.GetAttribute("data-tab") == tab).Click();

    [Fact]
    public void Changed_features_come_first_grouped_by_module_with_scope_changes_apart()
    {
        var cut = Render();
        cut.FindAll("[data-testid=dlr-story-row]").Select(r => r.GetAttribute("data-feature")).Should().Equal("skoleservice::001-skoleservice", "tjeneste::014-children-in-placements-per-period");
        cut.FindAll("[data-testid=dlr-module-group] h3").Select(h => h.TextContent).Should().Equal("SkoleService", "Tjeneste");
        var skole = cut.Find("[data-testid=dlr-story-row][data-feature='skoleservice::001-skoleservice']");
        skole.QuerySelector("[data-testid=dlr-feature-change]")!.TextContent.Should().Be("Specification changed", "nothing else moved");
        skole.QuerySelector("[data-testid=dlr-feature-stories]")!.TextContent.Should().Be("3 · 2 changed");
        skole.QuerySelector("[data-testid=dlr-feature-criteria]")!.TextContent.Should().Be("4 → 5 · 1 added, 1 changed");
        skole.QuerySelector("[data-testid=dlr-progress]")!.TextContent.Should().Contain("173 / 175 tasks · 2 open");
        var removed = cut.Find("[data-testid=dlr-story-row][data-change=Removed]");
        removed.TextContent.Should().Contain("Removed — review").And.Contain("54 / 56 tasks").And.Contain("View baseline feature");
        cut.Find("[data-testid=dlr-scope-change]").TextContent.Should().Contain("Removed — review").And.Contain("Children in Placements per Period");
        cut.Find("[data-testid=dlr-story-filter]").Change("all");
        cut.FindAll("[data-testid=dlr-story-row]").Should().HaveCount(3);
        cut.Find("[data-testid=dlr-story-phase]").Change("phase-2");
        cut.FindAll("[data-testid=dlr-story-row]").Should().ContainSingle();
    }

    [Fact]
    public void Feature_detail_shows_phases_only_when_explicit_and_user_stories_under_their_parent()
    {
        var cut = Render();
        cut.Find("[data-testid=dlr-story-row][data-feature='skoleservice::001-skoleservice'] [data-testid=dlr-feature-open]").Click();
        cut.FindAll("[data-testid=dlr-feature-tab]").Select(t => t.GetAttribute("data-tab")).Should().Equal("overview", "phases", "stories", "criteria", "tasks", "changes", "evidence");
        cut.Find("[data-testid=dlr-feature-summary]").TextContent.Should().Contain("User stories").And.Contain("Phases");
        Tab(cut, "phases");
        cut.FindAll("[data-testid=dlr-phase]").Select(p => p.GetAttribute("data-phase")).Should().Equal("phase-1", "phase-2");
        cut.Find("[data-testid=dlr-phase][data-phase=phase-2]").TextContent.Should().Contain("Phase changed").And.Contain("US3");
        Tab(cut, "stories");
        cut.Find("[data-testid=dlr-user-story][data-story=US3]").TextContent.Should().Contain("New this period");
        cut.Find("[data-testid=dlr-user-story][data-story=US1] [data-testid=dlr-user-story-open]").Click();
        cut.Find("[data-testid=dlr-breadcrumb]").TextContent.Should().Contain("M2LB").And.Contain("SkoleService").And.Contain("School attendance reporting").And.Contain("Phase 1: Foundation").And.Contain("US1");
        cut.Find("[data-testid=dlr-story-detail]").TextContent.Should().Contain("Parent feature").And.Contain("P1 (declared)").And.Contain("Create registration form").And.NotContain("Configure logging", "only explicitly traced tasks");

        var removed = Render();
        removed.Find("[data-testid=dlr-story-row][data-change=Removed] [data-testid=dlr-feature-open]").Click();
        removed.FindAll("[data-testid=dlr-feature-tab]").Select(t => t.GetAttribute("data-tab")).Should().NotContain("phases", "no phase UI without explicit phases");
        removed.Find("[data-testid=dlr-removed-note]").TextContent.Should().Contain("last known in the baseline");
    }

    [Fact]
    public void Acceptance_criteria_are_grouped_by_story_with_badges_only_for_changes_and_baseline_only_when_changed()
    {
        var cut = Render();
        cut.Find("[data-testid=dlr-story-row][data-feature='skoleservice::001-skoleservice'] [data-testid=dlr-feature-open]").Click();
        Tab(cut, "criteria");
        cut.FindAll("[data-testid=dlr-criteria-group]").Select(g => g.GetAttribute("data-story")).Should().Equal("US1", "US2", "US3", "feature");
        var us1 = cut.FindAll("[data-testid=dlr-criteria-group][data-story=US1] [data-testid=dlr-criterion]");
        us1[0].TextContent.Should().Contain("Changed").And.Contain("View baseline difference").And.Contain("Child is saved");
        us1[1].QuerySelector(".dfx-badge").Should().BeNull("unchanged criteria carry no badge");
        cut.FindAll("[data-testid=dlr-criterion-baseline]").Should().ContainSingle("the baseline is shown only for changed criteria");
        cut.Find("[data-testid=dlr-criteria-group][data-story=US3]").TextContent.Should().Contain("Added this period");
    }

    [Fact]
    public void Tasks_are_grouped_by_phase_with_story_chips_and_change_badges_and_filterable()
    {
        var cut = Render();
        cut.Find("[data-testid=dlr-story-row][data-feature='skoleservice::001-skoleservice'] [data-testid=dlr-feature-open]").Click();
        Tab(cut, "tasks");
        cut.FindAll("[data-testid=dlr-task-group]").Select(g => g.GetAttribute("data-phase")).Should().Equal("phase-1", "phase-2", "feature");
        var t010 = cut.FindAll("[data-testid=dlr-task]").Single(t => t.TextContent.Contains("T010"));
        t010.TextContent.Should().Contain("US3").And.Contain("Completed this period").And.Contain("Completed:");
        cut.FindAll("[data-testid=dlr-task]").Single(t => t.TextContent.Contains("T020")).TextContent.Should().Contain("New");
        cut.FindAll("[data-testid=dlr-task-filter]").Single(f => f.GetAttribute("data-filter") == "changed").Change(true);
        cut.FindAll("[data-testid=dlr-task]").Select(t => t.GetAttribute("data-change")).Should().BeEquivalentTo(["CompletedThisPeriod", "New"]);
        Tab(cut, "changes");
        cut.Find("[data-testid=dlr-feature-changes]").TextContent.Should().Contain("1 added · 1 changed · 0 removed").And.Contain("evidence only, never a delivery status");
    }

    [Fact]
    public void A_report_without_hierarchy_still_renders_and_says_so()
    {
        var report = Report() with { Version = 1, ScopeChanges = [], Stories = [Report().Stories[0] with { Hierarchy = null }] };
        var cut = Render(report);
        cut.Find("[data-testid=dlr-feature-open]").Click();
        cut.Find("[data-testid=dlr-hierarchy-not-captured]").TextContent.Should().Contain("not captured");
        cut.FindAll("[data-testid=dlr-feature-tab]").Select(t => t.GetAttribute("data-tab")).Should().NotContain("phases");
    }
}
