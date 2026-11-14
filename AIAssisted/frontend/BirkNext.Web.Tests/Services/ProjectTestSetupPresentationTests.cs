using BirkNext.Interpretation;
using BirkNext.Scopes;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Pure rules of Project Test Setup: view mapping, step states, the next action, member states and suggestions from evidence only.</summary>
public sealed class ProjectTestSetupPresentationTests
{
    private const string Project = "import:demo";

    private static ScopeSummary Area(ScopeState state = ScopeState.Draft, int current = 1, int? approved = null) =>
        new() { Id = Guid.NewGuid(), ProjectKey = Project, Name = "Area", State = state, CurrentRevision = current, ApprovedRevision = approved, MemberCount = 1 };

    private static ScopeDetail Detail(ScopeSummary s, params ScopeMember[] members) =>
        new() { Scope = s, Revisions = [new ScopeRevisionView { Number = s.CurrentRevision, State = s.State == ScopeState.Approved && s.ApprovedRevision == s.CurrentRevision ? ScopeRevisionState.Approved : ScopeRevisionState.Draft, Members = [.. members] }] };

    private static readonly SystemsOverviewView Ready = new() { InterpretationId = Guid.NewGuid(), Systems = [new() { Key = "System|a", Name = "A" }] };

    private static SetupFacts Facts(SystemsPageState state = SystemsPageState.Ready, IReadOnlyList<ScopeSummary>? areas = null, ScopeDetail? selected = null,
        IReadOnlyList<ScopeMemberStatus>? checks = null, bool hasProject = true) =>
        new(hasProject, state, state == SystemsPageState.Ready ? Ready : null, true, null, areas ?? [], selected, checks ?? []);

    [Theory]
    [InlineData(null, null, "setup")]
    [InlineData(null, "systems", "systems")]
    [InlineData(null, "connections", "connections")]
    [InlineData(null, "scopes", "areas")]
    [InlineData("create", "scopes", "create")]
    [InlineData("nonsense", null, "setup")]
    public void Old_tab_links_map_to_the_new_views(string? view, string? tab, string expected) =>
        ProjectTestSetupPresentation.ViewOf(view, tab).Should().Be(expected);

    [Fact]
    public void The_next_action_follows_the_data_state_in_order()
    {
        ProjectTestSetupPresentation.NextAction(Facts(hasProject: false)).Id.Should().Be("import");
        ProjectTestSetupPresentation.NextAction(Facts(SystemsPageState.NoSource)).Id.Should().Be("import-source");
        ProjectTestSetupPresentation.NextAction(Facts(SystemsPageState.NotInterpreted)).Should().Match<SetupNextAction>(a => a.Id == "analyze" && a.Href == "correlation-review");
        ProjectTestSetupPresentation.NextAction(Facts()).Id.Should().Be("create");
        ProjectTestSetupPresentation.NextAction(Facts(SystemsPageState.ZeroSystems)).Id.Should().Be("create", "a test area does not need systems");

        var draft = Area();
        var member = new ScopeMember { Kind = ScopeMemberKind.Scenario, TargetId = "Scenario|s", Label = "S", Provenance = "Selected by a person" };
        ProjectTestSetupPresentation.NextAction(Facts(areas: [draft], selected: Detail(draft, member))).Id.Should().Be("continue");
        ProjectTestSetupPresentation.NextAction(Facts(areas: [draft], selected: Detail(draft, member), checks: [new(member.Key, ScopeMemberCheck.Missing, "gone")])).Id.Should().Be("review-members");

        var approved = Area(ScopeState.Approved, 1, 1);
        var plan = ProjectTestSetupPresentation.NextAction(Facts(areas: [approved], selected: Detail(approved, member)));
        plan.Id.Should().Be("plan");
        plan.Href.Should().Be($"test-plan-suggestions?scope={approved.Id}");
        ProjectTestSetupPresentation.NextAction(Facts(areas: [Area(ScopeState.Approved, 2, 1)], selected: Detail(Area(ScopeState.Approved, 2, 1), member))).Id.Should().Be("continue", "a newer draft is not approved yet");
    }

    [Fact]
    public void Steps_separate_progress_from_evidence_and_never_require_connection_review()
    {
        var withCandidate = Ready with { Connections = [new() { Id = "c", SourceKey = "System|a", TargetKey = "System|b", State = SystemConnectionState.Candidate }] };
        var facts = Facts() with { Overview = withCandidate };
        var steps = ProjectTestSetupPresentation.Steps(facts);
        steps.Select(s => s.StateKey).Should().Equal("Completed", "Available", "Current", "Unavailable", "NotStarted", "Unavailable");
        steps[1].Evidence.Should().Contain("1 connection may need review (optional)").And.NotContain("runtime");
        ProjectTestSetupPresentation.Steps(Facts() with { AreasError = "boom" })[2].StateKey.Should().Be("Error");
        ProjectTestSetupPresentation.Steps(Facts(SystemsPageState.Error))[0].StateKey.Should().Be("Error");
        ProjectTestSetupPresentation.Steps(Facts(hasProject: false)).Select(s => s.StateKey).Should().OnlyContain(s => s == "Unavailable" || s == "NotStarted");
    }

    [Fact]
    public void Member_states_distinguish_selected_kept_candidate_and_unresolved()
    {
        var picked = new ScopeMember { Kind = ScopeMemberKind.System, TargetId = "System|a", Provenance = "Selected by a person from Canonical interpretation" };
        var kept = ProjectTestSetupPresentation.Keep(new ScopeCatalogItem { Kind = ScopeMemberKind.Requirement, TargetId = "Requirement|m/f/1", Label = "R1" }, "the specification");
        var candidate = picked with { Inclusion = ScopeMemberInclusion.Candidate };
        ProjectTestSetupPresentation.MemberState(picked, null).Should().Be(AreaMemberState.ExplicitlySelected);
        kept.Inclusion.Should().Be(ScopeMemberInclusion.Confirmed);
        ProjectTestSetupPresentation.MemberState(kept, null).Should().Be(AreaMemberState.HumanConfirmed);
        ProjectTestSetupPresentation.MemberState(candidate, null).Should().Be(AreaMemberState.Candidate);
        ProjectTestSetupPresentation.MemberState(picked, new(picked.Key, ScopeMemberCheck.OtherContext, "other env")).Should().Be(AreaMemberState.Unresolved);
        ProjectTestSetupPresentation.MemberState(picked, new(picked.Key, ScopeMemberCheck.NotCheckable, "no catalog")).Should().Be(AreaMemberState.ExplicitlySelected, "not checkable is not missing");
    }

    [Fact]
    public void Suggestions_come_only_from_project_evidence_grouped_by_module_and_feature()
    {
        var catalogs = new Dictionary<ScopeMemberKind, IReadOnlyList<ScopeCatalogItem>>
        {
            [ScopeMemberKind.Requirement] =
            [
                new() { Kind = ScopeMemberKind.Requirement, TargetId = "Requirement|intake/report/fr-1", Label = "FR-1", Description = "intake/report/FR-1" },
                new() { Kind = ScopeMemberKind.Requirement, TargetId = "Requirement|intake/report/fr-2", Label = "FR-2", Description = "intake/report/FR-2" },
                new() { Kind = ScopeMemberKind.Requirement, TargetId = "Requirement|fr-9", Label = "FR-9", Description = "FR-9" },
            ],
            [ScopeMemberKind.Feature] = [new() { Kind = ScopeMemberKind.Feature, TargetId = "Feature|intake/report", Label = "Report intake", Description = "intake/report" }],
            [ScopeMemberKind.Journey] =
            [
                new() { Kind = ScopeMemberKind.Journey, TargetId = "j-mine", Label = "Report flow", SourceModule = ProjectTestSetupPresentation.DeclaredJourneyModule, Description = $"Project {Project} · Portal → Intake API" },
                new() { Kind = ScopeMemberKind.Journey, TargetId = "j-other", Label = "Other flow", SourceModule = ProjectTestSetupPresentation.DeclaredJourneyModule, Description = "Project import:other · X → Y" },
                new() { Kind = ScopeMemberKind.Journey, TargetId = "pack:j", Label = "Pack journey", SourceModule = "Integration Journeys", Description = "Some pack" },
            ],
        };
        SystemOverviewItem[] systems = [new() { Key = "System|intake-api", Name = "Intake API" }];

        var suggestions = ProjectTestSetupPresentation.Suggest(Project, catalogs, systems, "dev");

        suggestions.Select(s => s.Id).Should().BeEquivalentTo(["path:intake/report", "journey:j-mine"], "an id without a path, another project's journey and pack journeys are not suggested");
        var feature = suggestions.Single(s => s.Id == "path:intake/report");
        feature.Title.Should().Be("Report intake", "a feature at the same path names the suggestion");
        feature.Members.Select(m => m.TargetId).Should().BeEquivalentTo(["Requirement|intake/report/fr-1", "Requirement|intake/report/fr-2", "Feature|intake/report"]);
        var journey = suggestions.Single(s => s.Id == "journey:j-mine");
        journey.Members.Select(m => m.TargetId).Should().Equal("j-mine", "System|intake-api");
        journey.Members[1].CatalogScope.Should().Be("dev");

        ProjectTestSetupPresentation.Suggest(Project, new Dictionary<ScopeMemberKind, IReadOnlyList<ScopeCatalogItem>>(), [], "dev").Should().BeEmpty("no evidence, no suggestion");
    }

    [Fact]
    public void Use_links_are_only_scope_consuming_modules_and_journeys_appear_only_when_named()
    {
        var area = Area(ScopeState.Approved, 1, 1);
        var links = ProjectTestSetupPresentation.UseLinks(Detail(area, new ScopeMember { Kind = ScopeMemberKind.Scenario, TargetId = "Scenario|s" }));
        links.Where(l => l.Behaviour == "filters").Select(l => l.Href).Should().OnlyContain(h => h.Contains($"scope={area.Id}"));
        links.Select(l => l.Module).Should().NotContain("integration-journeys");
        links.Single(l => l.Module == "correlation-review").Behaviour.Should().Be("context");
        ProjectTestSetupPresentation.UseLinks(Detail(area, new ScopeMember { Kind = ScopeMemberKind.Journey, TargetId = "j" }))
            .Single(l => l.Module == "integration-journeys").Behaviour.Should().Be("context");
    }
}
