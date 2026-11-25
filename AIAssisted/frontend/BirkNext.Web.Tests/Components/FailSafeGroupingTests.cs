using System.Reflection;
using BirkNext.Sdd;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Components.ManualTesting;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using G = BirkNext.Web.Services.StoryGroupingPresentation;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Fail-safe grouping in Finn og forstå (2026-11-25): a story without a group is still a valid story. The permanent «Uten gruppe» with its reason, the
/// review of stories that need a person, failures that never look like success and never hide a story, and grouping that never gates preparation.
/// U01–U35 follow the task's frontend list; page-level flows are verified in the real browser walkthrough.
/// </summary>
public sealed class FailSafeGroupingTests : BunitContext
{
    private const string Path = "SkoleService/specs/001-skoleservice/spec.md";

    private static readonly RequirementSectionMap Sections = RequirementSections.Read("""
        ## Requirements
        ### Functional Requirements
        **Intake and identity**
        - **FR-001**: Accept a valid report.
        - **FR-002**: Resolve the child.
        **Quality control (accept and flag)**
        - **FR-014**: Merge duplicate absence codes.
        - **FR-015**: Flag inconsistent totals.
        **Period lock**
        - **FR-024**: Lock the period.
        """);

    private static CoverageUserStory Story(int n, params string[] tasks) => new()
    {
        Key = CoverageUserStory.KeyOf(Path, $"us{n}"), StoryId = $"US{n}", Ordinal = n, Title = $"Story {n}", SpecificationPath = Path,
        TaskRequirements = [.. tasks.Select((t, i) => new StoryTaskRequirement(t, $"T{n}{i}", $"[US{n}] task {t}", "tasks.md", n * 10 + i, StoryTaskRequirement.TaskLine))],
    };

    private static readonly CoverageUserStory Grouped = Story(1, "FR-014", "FR-015");      // clear: Quality
    private static readonly CoverageUserStory NoLinks = Story(2);                           // no requirements
    private static readonly CoverageUserStory Tie = Story(3, "FR-001", "FR-024");           // Intake vs Period lock
    private static readonly CoverageUserStory Thin = Story(4, "FR-024");                    // single task, no other area: clear

    private static DiscoveredScenario C(string id, CoverageUserStory? story, int n) => new()
    {
        CandidateId = id, ProjectKey = "m2lb", CapabilityId = ScenarioOwnership.Specification, Title = id, Purpose = id, Type = ScenarioCandidateType.BusinessFunctional,
        Confidence = CandidateConfidence.ExplicitSpecification, ExpectedBehaviour = "ok", Story = story, NumberInStory = story is null ? null : n,
        Evidence = [new("specification", $"{Path}#{id}", "Akseptansescenario")],
    };

    private static List<S.Item> Items() => S.Items([C("US1-AS1", Grouped, 1), C("US1-AS2", Grouped, 2), C("US2-AS1", NoLinks, 1), C("US3-AS1", Tie, 1),
        C("US4-AS1", Thin, 1), C("loose-1", null, 0)], new Dictionary<string, S.Prepared>());

    private static List<StoryGroupingEngine.StoryInputs> Inputs() => G.Inputs(Items(), null, null);
    private static List<StoryGroupingProfile> Profiles(StoryGroupingDocument? doc = null) => StoryGroupingEngine.Profiles(Inputs(), Sections, doc);
    private static StoryGroupingProfile P(int n, StoryGroupingDocument? doc = null) => Profiles(doc).Single(p => p.StoryKey == CoverageUserStory.KeyOf(Path, $"us{n}"));
    private static StoryGroupingDocument Doc(params StoryGroupingOperation[] ops) =>
        StoryGroupingEngine.Apply(new StoryGroupingDocument { ProjectKey = "m2lb", SpecificationPath = Path }, ops, "anna", DateTimeOffset.UtcNow, _ => true);

    // ── «Uten gruppe» ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void U01_U33_the_permanent_Uten_gruppe_is_in_every_grouped_mode_once_also_when_empty()
    {
        foreach (var mode in new[] { G.Mode.Functional, G.Mode.Business })
        {
            var tree = G.Tree(mode, Profiles(), null, Sections);
            tree.Should().ContainSingle(n => n.Key == G.UngroupedKey).Which.Should().Match<G.GroupNode>(n => n.Title == "Uten gruppe" && n.Kind == "system");
            G.NavigationViolations(Items(), Profiles(), tree, mode).Should().BeEmpty();
        }
        var allPlaced = Doc(new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetPrimaryArea, StoryKey = NoLinks.Key, PrimaryArea = "Intake and identity" },
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetPrimaryArea, StoryKey = Tie.Key, PrimaryArea = "Period lock" });
        var empty = G.Tree(G.Mode.Functional, Profiles(allPlaced), allPlaced, Sections).Single(n => n.Key == G.UngroupedKey);
        empty.Stories.Should().BeEmpty();
        empty.Detail.Should().Be("Alle brukerhistorier er gruppert.");
    }

    [Fact]
    public void U02_U03_each_ungrouped_story_says_why_in_the_testers_words()
    {
        G.UngroupedReason(P(2), G.Mode.Functional, null).Should().Be("Ingen tilknyttede funksjonelle krav");
        G.UngroupedReason(P(3), G.Mode.Functional, null).Should().Be("Flere mulige grupper");
        P(3).Candidates.Should().BeEquivalentTo(["Intake and identity", "Period lock"]);
        G.UngroupedReason(P(1), G.Mode.Functional, null).Should().BeNull("a grouped story has no ungrouped reason");
        G.StatusLabel(P(1)).Should().Be("Gruppert");
        G.StatusBasis(P(1)).Should().Be("foreslått fra kilden", "a suggestion is never presented as confirmed");
        G.StatusLabel(P(2)).Should().Be("Uten gruppe");
        G.StatusLabel(P(3)).Should().Be("Flere mulige grupper");
    }

    [Fact]
    public void U05_U06_a_failed_read_is_grouping_failed_a_host_without_storage_is_not_and_a_missing_source_is_named()
    {
        G.LoadOutcome(null, "Grupperingstjenesten svarer ikke. Prøv igjen.", ITestPlanSuggestionsApiService.NotAvailable)
            .Should().Be(((StoryGroupingDocument?)null, "Grupperingstjenesten svarer ikke. Prøv igjen.", false));
        G.LoadOutcome(null, null, ITestPlanSuggestionsApiService.NotAvailable).Error.Should().Be("Grupperingen kunne ikke leses.", "a failure is never an empty document");
        G.LoadOutcome(null, ITestPlanSuggestionsApiService.NotAvailable, ITestPlanSuggestionsApiService.NotAvailable).Should().Be(((StoryGroupingDocument?)null, (string?)null, false));
        var doc = new StoryGroupingDocument { Revision = 3 };
        G.LoadOutcome(doc, null, ITestPlanSuggestionsApiService.NotAvailable).Should().Be((doc, (string?)null, true));
        G.SourceMissing(hasStories: true, specificationText: "  ").Should().BeTrue();
        G.SourceMissing(hasStories: true, specificationText: "## Requirements").Should().BeFalse();
    }

    [Fact]
    public void U07_U14_a_confirmed_group_stays_visible_and_a_deliberate_ungrouped_decision_falls_back_with_its_reason()
    {
        var confirmed = Doc(new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetPrimaryArea, StoryKey = Tie.Key, PrimaryArea = "Period lock" });
        var tree = G.Tree(G.Mode.Functional, Profiles(confirmed), confirmed, Sections);
        tree.Single(n => n.Key == "area:Period lock").Stories.Should().Contain(p => p.StoryKey == Tie.Key, "the person's decision stands although the rules see a tie");

        var released = StoryGroupingEngine.Apply(confirmed, [new StoryGroupingOperation { Kind = StoryGroupingOperationKind.LeaveUngrouped, StoryKey = Tie.Key }], "anna", DateTimeOffset.UtcNow, _ => true);
        var after = G.Tree(G.Mode.Functional, Profiles(released), released, Sections);
        after.Single(n => n.Key == G.UngroupedKey).Stories.Should().Contain(p => p.StoryKey == Tie.Key);
        G.UngroupedReason(P(3, released), G.Mode.Functional, released).Should().Be("Bevisst uten gruppe");
        G.ReviewQueue(Profiles(released)).Should().NotContain(p => p.StoryKey == Tie.Key, "a reviewed decision is not asked again");
    }

    [Fact]
    public void U22_a_business_group_that_no_longer_exists_puts_its_stories_under_Uten_gruppe_and_into_review()
    {
        var doc = new StoryGroupingDocument { Groups = [], Placements = [new() { StoryKey = Grouped.Key, GroupId = "gone", By = "anna" }] };
        var p = P(1, doc);
        G.UngroupedReason(p, G.Mode.Business, doc).Should().Be("Gruppen finnes ikke lenger");
        G.ReviewQueue(Profiles(doc)).Should().Contain(x => x.StoryKey == Grouped.Key);
        G.Tree(G.Mode.Business, Profiles(doc), doc, Sections).Single().Stories.Should().HaveCount(4, "every story is under «Uten gruppe» when no group exists");
    }

    // ── «Gjennomgå gruppering» ───────────────────────────────────────────────────────────────────────────────

    private (IRenderedComponent<GroupingReview> Cut, List<IReadOnlyList<StoryGroupingOperation>> Saved) Review(StoryGroupingDocument? doc = null, Func<string?>? answer = null, bool canEdit = true)
    {
        doc ??= new StoryGroupingDocument { ProjectKey = "m2lb", SpecificationPath = Path, Groups = [new() { Id = "g1", Name = "Mottak", Order = 0 }] };
        var saved = new List<IReadOnlyList<StoryGroupingOperation>>();
        var cut = Render<GroupingReview>(p => p.Add(x => x.Queue, G.ReviewQueue(Profiles(doc))).Add(x => x.Document, doc).Add(x => x.Sections, Sections)
            .Add(x => x.Inputs, Inputs()).Add(x => x.CanEdit, canEdit)
            .Add(x => x.OnApply, ops => { saved.Add(ops); return Task.FromResult(answer?.Invoke()); }));
        return (cut, saved);
    }

    [Fact]
    public void U04_U08_U20_the_review_lists_stories_that_need_a_person_with_their_candidates_and_real_evidence()
    {
        var (cut, _) = Review();
        cut.FindAll("[data-testid=grv-row]").Select(r => r.GetAttribute("data-story")).Should().BeEquivalentTo([NoLinks.Key, Tie.Key]);
        var tie = cut.Find($"[data-testid=grv-row][data-story='{Tie.Key}']");
        tie.QuerySelectorAll("[data-testid=grv-choose]").Select(b => b.GetAttribute("data-area")).Should().BeEquivalentTo(["Intake and identity", "Period lock"]);
        tie.QuerySelector("[data-testid=grv-reason]")!.TextContent.Should().Contain("Flere mulige grupper").And.Contain("FR-001").And.Contain("FR-024");
        tie.QuerySelectorAll("[data-testid=grv-area]").Select(a => a.TextContent).Should().Contain(t => t.Contains("FR-024") && t.Contains("Fra oppgavene"));
        cut.Find($"[data-testid=grv-row][data-story='{NoLinks.Key}'] [data-testid=grv-reason]").TextContent.Should().Contain("Ingen tilknyttede funksjonelle krav");
    }

    [Fact]
    public void U09_U12_choosing_a_candidate_previews_then_saves_area_and_group_together()
    {
        var (cut, saved) = Review();
        cut.Find($"[data-testid=grv-row][data-story='{Tie.Key}'] [data-testid=grv-choose][data-area='Period lock']").Click();
        cut.FindAll("[data-testid=grv-preview-line]").Select(l => l.TextContent).Should().Contain(l => l.Contains("endres ikke"));
        saved.Should().BeEmpty("nothing is saved before «Lagre»");
        cut.Find("[data-testid=grv-save]").Click();
        saved.Single().Select(o => o.Kind).Should().Equal(StoryGroupingOperationKind.SetPrimaryArea, StoryGroupingOperationKind.PlaceStory);
        saved.Single()[0].PrimaryArea.Should().Be("Period lock");
    }

    [Fact]
    public void U10_U18_creating_a_group_from_the_review_validates_the_name_first()
    {
        var (cut, saved) = Review();
        var row = $"[data-testid=grv-row][data-story='{NoLinks.Key}']";
        cut.Find($"{row} [data-testid=grv-new-name]").Input("mottak");
        cut.Find($"{row} [data-testid=grv-create]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=grv-name-problem]").TextContent.Should().Be("Det finnes allerede en gruppe som heter «mottak».");
        cut.Find($"{row} [data-testid=grv-new-name]").Input("Graderte barn");
        cut.Find($"{row} [data-testid=grv-create]").Click();
        cut.Find("[data-testid=grv-save]").Click();
        var ops = saved.Single();
        ops.Select(o => o.Kind).Should().Equal(StoryGroupingOperationKind.CreateGroup, StoryGroupingOperationKind.PlaceStory);
        ops[1].GroupId.Should().Be(ops[0].GroupId);
    }

    [Fact]
    public void U13_leave_ungrouped_and_defer_are_both_available_and_differ()
    {
        var (cut, saved) = Review();
        cut.Find($"[data-testid=grv-row][data-story='{NoLinks.Key}'] [data-testid=grv-defer]").Click();
        cut.FindAll("[data-testid=grv-row]").Select(r => r.GetAttribute("data-story")).Should().Equal(Tie.Key);
        saved.Should().BeEmpty("«Utsett» stores nothing");
        cut.Find($"[data-testid=grv-row][data-story='{Tie.Key}'] [data-testid=grv-leave]").Click();
        cut.Find("[data-testid=grv-save]").Click();
        saved.Single().Single().Kind.Should().Be(StoryGroupingOperationKind.LeaveUngrouped);
    }

    [Fact]
    public void U15_U16_U17_a_failed_save_keeps_the_staged_change_shows_why_and_retries_the_same_change()
    {
        var answers = new Queue<string?>(["Grupperingstjenesten svarte ikke. Endringen er kanskje ikke lagret; «Prøv igjen» lagrer den bare én gang.", null]);
        var (cut, saved) = Review(answer: () => answers.Dequeue());
        cut.Find($"[data-testid=grv-row][data-story='{Tie.Key}'] [data-testid=grv-choose][data-area='Period lock']").Click();
        cut.Find("[data-testid=grv-save]").Click();

        cut.Find("[data-testid=grv-error]").TextContent.Should().Contain("svarte ikke");
        cut.FindAll("[data-testid=grv-preview]").Should().ContainSingle("the change stays staged after a failure");
        cut.Find("[data-testid=grv-save]").TextContent.Should().Be("Prøv igjen");
        cut.Markup.Should().NotContain("Lagret", "no success is claimed");

        cut.Find("[data-testid=grv-save]").Click();
        saved.Should().HaveCount(2);
        saved[1].Should().BeEquivalentTo(saved[0], o => o.WithStrictOrdering(), "the retry sends the same change");
        G.RequestId(4, saved[0]).Should().Be(G.RequestId(4, saved[1]), "so the server stores it once");
        G.RequestId(5, saved[0]).Should().NotBe(G.RequestId(4, saved[0]));
        cut.FindAll("[data-testid=grv-preview]").Should().BeEmpty();
    }

    [Fact]
    public void U29_U30_every_review_action_is_a_native_labelled_control()
    {
        var (cut, _) = Review();
        foreach (var e in cut.FindAll("[data-testid=grv-row] button, [data-testid=grv-row] select, [data-testid=grv-row] input"))
        {
            e.TagName.Should().BeOneOf("BUTTON", "SELECT", "INPUT");
            (e.TextContent.Trim().Length > 0 || e.HasAttribute("aria-label")).Should().BeTrue($"{e.GetAttribute("data-testid")} has a name for keyboard and screen-reader users");
        }
    }

    [Fact]
    public void U02_without_permission_the_review_explains_but_offers_no_changes()
    {
        var (cut, _) = Review(canEdit: false);
        cut.FindAll("[data-testid=grv-reason]").Should().HaveCount(2);
        cut.FindAll("[data-testid=grv-choose], [data-testid=grv-leave], [data-testid=grv-create]").Should().BeEmpty();
    }

    // ── «Administrer grupper»: deletion and rename ───────────────────────────────────────────────────────────

    [Fact]
    public void U11_U19_deleting_a_group_with_stories_previews_the_count_and_needs_a_choice()
    {
        var doc = Doc(new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, GroupId = "a", Name = "Mottak" }, new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, GroupId = "b", Name = "Lås" },
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.PlaceStory, StoryKey = Grouped.Key, GroupId = "a" });
        var saved = new List<IReadOnlyList<StoryGroupingOperation>>();
        var cut = Render<GroupOrganizer>(p => p.Add(x => x.Document, doc).Add(x => x.Sections, Sections).Add(x => x.Inputs, Inputs()).Add(x => x.Profiles, Profiles(doc))
            .Add(x => x.OnApply, ops => { saved.Add(ops); return Task.FromResult<string?>(null); }));
        var a = "[data-testid=gor-group][data-group=a]";
        cut.FindAll($"{a} [data-testid=gor-delete]").Should().BeEmpty("«Slett tom gruppe» is only for empty groups");
        cut.Find($"{a} [data-testid=gor-delete-members]").Click();
        cut.Find("[data-testid=gor-delete-count]").TextContent.Should().Contain("1 brukerhistorie").And.Contain("beholdes");
        cut.Find("[data-testid=gor-delete-release]").Click();
        cut.FindAll("[data-testid=gor-preview-line]").Select(l => l.TextContent).Should().Contain("Gruppen «Mottak» fjernes (bare grupperingen).");
        cut.Find("[data-testid=gor-save]").Click();
        saved.Single().Single().Should().Match<StoryGroupingOperation>(o => o.Kind == StoryGroupingOperationKind.DeleteGroup && o.ReleaseMembers);

        cut.Find("[data-testid=gor-group][data-group=b] [data-testid=gor-rename]").Change("Periodelås");
        cut.Find("[data-testid=gor-save]").Click();
        saved[1].Single().Should().Match<StoryGroupingOperation>(o => o.Kind == StoryGroupingOperationKind.RenameGroup && o.Name == "Periodelås");
    }

    [Fact]
    public void U18_the_organizer_refuses_a_duplicate_name_before_staging()
    {
        var doc = Doc(new StoryGroupingOperation { Kind = StoryGroupingOperationKind.CreateGroup, GroupId = "a", Name = "Mottak" });
        var cut = Render<GroupOrganizer>(p => p.Add(x => x.Document, doc).Add(x => x.Sections, Sections).Add(x => x.Inputs, Inputs()).Add(x => x.Profiles, Profiles(doc)));
        cut.Find("[data-testid=gor-new-name]").Input("MOTTAK");
        cut.Find("[data-testid=gor-create]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=gor-name-problem]").TextContent.Should().Contain("Det finnes allerede en gruppe som heter «MOTTAK»");
    }

    // ── Invariants: nothing hidden, nothing duplicated, nothing gated ─────────────────────────────────────────

    [Fact]
    public void U23_U24_U34_every_story_and_scenario_stays_reachable_in_every_mode_and_nothing_is_duplicated()
    {
        var items = Items();
        foreach (var mode in Enum.GetValues<G.Mode>())
            G.NavigationViolations(items, Profiles(), G.Tree(mode, Profiles(), null, Sections), mode).Should().BeEmpty($"mode {mode}");
        items.Should().Contain(i => i.Candidate.Story == null, "a scenario without a user story stays in «Uten brukerhistorie» and «Alle scenarioer»");
        var duplicated = items.Append(items[0]).ToList();
        G.NavigationViolations(duplicated, Profiles(), G.Tree(G.Mode.Functional, Profiles(), null, Sections), G.Mode.Functional).Should().ContainSingle(v => v.Contains("flere ganger"));
    }

    [Fact]
    public void U28_U35_grouping_is_navigation_only_preparation_and_readiness_never_read_it()
    {
        // No preparation, readiness or checklist API of the workspace takes a grouping profile or state: grouping cannot gate testing.
        var groupingTypes = new[] { typeof(StoryGroupingProfile), typeof(GroupingState), typeof(StoryGroupingDocument) };
        foreach (var t in new[] { typeof(ScenarioWorkspacePresentation), typeof(ScenarioBrowserPresentation), typeof(PlanPreparePresentation) })
            t.GetMethods(BindingFlags.Public | BindingFlags.Static).SelectMany(m => m.GetParameters()).Select(p => p.ParameterType)
                .Should().NotContain(groupingTypes, $"{t.Name} must not depend on grouping");
        var lines = G.Preview(Inputs(), Sections, new StoryGroupingDocument(), [new StoryGroupingOperation { Kind = StoryGroupingOperationKind.LeaveUngrouped, StoryKey = Grouped.Key }], _ => true);
        lines.Last().Should().Be("Akseptansescenarioer, lagrede testtilfeller, testplaner og kjøringer endres ikke.");
    }
}
