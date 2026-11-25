using System.Runtime.CompilerServices;
using BirkNext.Api.Services.Specifications;
using BirkNext.Sdd;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// SDD traceability semantics (2026-11-25): a source reference proves a relationship, not primary ownership, implementation, coverage or a test
/// result. Relationship provenance (how it is known) and role (what it is for) are separate; an incidental reference never decides a group; no
/// numeric FR range or shared word creates a link; identifiers are namespace-safe; scenario references resolve only in their own specification.
/// Numbers follow the task's list (T01–T32).
/// </summary>
public sealed class TraceabilitySemanticsTests
{
    private static readonly RequirementSectionMap Sections = RequirementSections.Read("""
        ## Requirements
        ### Functional Requirements
        **Intake**
        - **FR-001**: a
        - **FR-002**: b
        **Control**
        - **FR-008**: c
        - **FR-009**: d
        **Lists**
        - **FR-041**: e
        """);

    private static CoverageUserStory Story(string id, string[]? named = null, string[]? tasks = null) => new()
    {
        Key = $"spec.md#{id.ToLowerInvariant()}", StoryId = id, Title = id, SpecificationPath = "spec.md", NamedRequirements = [.. named ?? []],
        TaskRequirements = [.. (tasks ?? []).Select((t, i) => new StoryTaskRequirement(t, $"T{i}", $"[{id}] {t}", "tasks.md", i + 1, StoryTaskRequirement.TaskLine))],
    };

    private static StoryGroupingEngine.StoryInputs In(CoverageUserStory s, params string[] scenarioRefs) => new(s, [.. scenarioRefs.Select(r => (r, "scenario 1"))], [], []);

    private static StoryGroupingProfile One(StoryGroupingEngine.StoryInputs input, StoryGroupingDocument? doc = null) => StoryGroupingEngine.Profiles([input], Sections, doc).Single();

    // ── T01–T06 identifiers ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T01_no_word_overlap_links_a_story_to_requirements()
    {
        const string spec = "# F\n\n## User Scenarios & Testing\n\n### User Story 1 - Accept the report for the child (Priority: P1)\n\nThe child report is accepted.\n\n## Requirements\n\n### Functional Requirements\n\n**A**\n\n- **FR-001**: The system MUST accept the report for the child.\n";
        var m = SpecExplorerService.BuildSemanticModel(SpecExplorerService.Parse(spec), spec);
        m.UserStories.Single().LinkedRequirements.Should().BeEmpty("sharing words with a requirement is not a reference");
    }

    [Theory]
    [InlineData("see FR-015a and FR-015", new[] { "FR-015A", "FR-015" })]
    [InlineData("UT-FR-001..005 and [SR-FR-017a]", new string[0])]
    [InlineData("`FR-008` in code and [FR-009](spec.md#fr-009) as a link", new[] { "FR-008", "FR-009" })]
    [InlineData("FR-001, FR-002 and FR-027a in one paragraph", new[] { "FR-001", "FR-002", "FR-027A" })]
    [InlineData("fr-014 lower case", new[] { "FR-014" })]
    [InlineData("FR-0151 is not FR-015", new[] { "FR-0151", "FR-015" })]
    public void T03_T04_T05_identifiers_are_whole_suffix_aware_and_namespace_safe(string text, string[] expected) =>
        SpecificationEvidenceBuilder.NamedRequirements(text).Should().Equal(expected);

    [Fact]
    public void T06_the_sources_display_casing_is_kept_next_to_the_canonical_id()
    {
        var map = RequirementSections.Read("## Requirements\n**A**\n- **FR-027a**: x\n");
        (map.Find("FR-027A")!.RequirementId, map.DisplayId("FR-027A")).Should().Be(("FR-027A", "FR-027a"));
    }

    [Fact]
    public void T02_no_numeric_FR_range_assigns_a_story_in_the_traceability_model()
    {
        // The removed fallback mapped FR-001..007 → US1, FR-008..012 → US2 … for every specification. A requirement without an explicit story id
        // in its context has no story.
        var source = File.ReadAllText(Path.Combine(Repo(), "frontend", "BirkNext.Web", "Services", "TraceabilityModelBuilder.cs"));
        source.Should().NotContain("deterministic FR range").And.NotContain("AddDeterministicScLinks(");
    }

    // ── T07–T14 provenance, role, incidental references ─────────────────────────────────────────────────────────

    [Fact]
    public void T07_T08_T13_provenance_and_role_are_independent()
    {
        var p = One(In(Story("US1", named: ["FR-001"], tasks: ["FR-002"]), "FR-041"));
        var direct = p.Requirements.Single(r => r.RequirementId == "FR-001");
        (direct.Basis, direct.Role).Should().Be((StoryLinkBasis.SourceExplicit, StoryLinkRole.Unknown), "a source-explicit link has no known role until reviewed");
        var scenario = p.Requirements.Single(r => r.RequirementId == "FR-041");
        (scenario.Basis, scenario.Role, scenario.ResponsibilityEvidence).Should().Be((StoryLinkBasis.ScenarioExplicit, StoryLinkRole.Unknown, false));
        var doc = Doc(new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetLinkRole, StoryKey = "spec.md#us1", RequirementId = "FR-001", Role = StoryLinkRole.Supporting });
        var reviewed = One(In(Story("US1", named: ["FR-001"], tasks: ["FR-002"]), "FR-041"), doc).Requirements.Single(r => r.RequirementId == "FR-001");
        (reviewed.Basis, reviewed.Role, reviewed.RoleReviewed).Should().Be((StoryLinkBasis.SourceExplicit, StoryLinkRole.Supporting, true), "the role changes, the provenance does not");
    }

    [Fact]
    public void T09_T10_supporting_and_cross_cutting_roles_are_derived_only_where_evidence_shows_them()
    {
        var detail = new CoverageUserStory { Key = "spec.md#us1", StoryId = "US1", Title = "US1", SpecificationPath = "spec.md",
            TaskRequirements = [new("FR-008", "T1", "  - detail", "tasks.md", 5, StoryTaskRequirement.TaskDetail), new("FR-001", "T1", "[US1] x", "tasks.md", 4, StoryTaskRequirement.TaskLine)] };
        One(In(detail)).Requirements.Single(r => r.RequirementId == "FR-008").Role.Should().Be(StoryLinkRole.Supporting);
        var many = new[] { In(Story("US1", tasks: ["FR-008", "FR-001"])), In(Story("US2", tasks: ["FR-008", "FR-002"])), In(Story("US3", tasks: ["FR-009", "FR-041"])), In(Story("US4", tasks: ["FR-041"])) };
        StoryGroupingEngine.Profiles(many, Sections, null)[0].Requirements.Single(r => r.RequirementId == "FR-008").Role.Should().Be(StoryLinkRole.CrossCutting);
    }

    [Fact]
    public void T11_T12_contextual_is_a_reviewer_decision_and_unknown_needs_review_when_it_is_all_there_is()
    {
        var only = One(In(Story("US8"), "FR-009"));
        only.Requirements.Single().Role.Should().Be(StoryLinkRole.Unknown);
        (only.State, only.PrimaryArea).Should().Be((GroupingState.NeedsReview, (string?)null));
        only.Explanation.Should().Contain("bare nevnt i akseptansescenarioer");
        var doc = Doc(new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetLinkRole, StoryKey = "spec.md#us8", RequirementId = "FR-009", Role = StoryLinkRole.Contextual });
        One(In(Story("US8"), "FR-009"), doc).Requirements.Single().Role.Should().Be(StoryLinkRole.Contextual);
    }

    [Fact]
    public void T14_an_incidental_scenario_reference_never_decides_the_primary_area_and_stays_visible()
    {
        // Two own task lines in «Intake»; three scenarios mention «Control» requirements in passing.
        var p = One(In(Story("US3", tasks: ["FR-001", "FR-002"]), "FR-008", "FR-009", "FR-041"));
        (p.State, p.SuggestedArea).Should().Be((GroupingState.Suggested, "Intake"));
        p.Requirements.Select(r => r.RequirementId).Should().Contain(["FR-008", "FR-009", "FR-041"], "references stay visible as related evidence");
        p.SecondaryAreas.Should().Contain(["Control", "Lists"]);
    }

    [Fact]
    public void T_a_reviewer_can_make_a_reference_primary_and_that_decides()
    {
        var doc = Doc(new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetLinkRole, StoryKey = "spec.md#us8", RequirementId = "FR-041", Role = StoryLinkRole.Primary });
        var p = One(In(Story("US8"), "FR-041"), doc);
        (p.State, p.SuggestedArea).Should().Be((GroupingState.Suggested, "Lists"));
        doc.History.Last().Action.Should().Be("Endret kravrolle");
    }

    // ── T15–T17 real SkoleService ───────────────────────────────────────────────────────────────────────────────

    private static readonly Lazy<Dictionary<string, StoryGroupingProfile>> Real = new(() =>
    {
        var spec = RequirementSectionsTests.Spec("SkoleService/specs/001-skoleservice/spec.md");
        var tasks = RequirementSectionsTests.Spec("SkoleService/specs/001-skoleservice/tasks.md");
        var evidence = SpecificationEvidenceBuilder.Build("SkoleService/specs/001-skoleservice/spec.md", spec, null, [("SkoleService/specs/001-skoleservice/tasks.md", tasks)]);
        var inputs = evidence.Stories.Select(s => new StoryGroupingEngine.StoryInputs(s,
            [.. evidence.AcceptanceScenarios.Where(a => a.StoryKey == s.Key).SelectMany(a => a.RequirementIds.Select(id => (id, $"scenario {a.NumberInStory}")))], [], [])).ToList();
        return StoryGroupingEngine.Profiles(inputs, RequirementSections.Read(spec), null).ToDictionary(p => evidence.Stories.Single(s => s.Key == p.StoryKey).StoryId!);
    });

    [Theory]
    [InlineData("US3", "FR-009", "Classification")]
    [InlineData("US6", "FR-041", "Follow-up lists")]
    [InlineData("US8", "FR-009", "Classification")]
    public void T15_T16_T17_the_real_incidental_references_stay_traceable_and_do_not_decide(string story, string requirement, string area)
    {
        var p = Real.Value[story];
        var rel = p.Requirements.Single(r => r.RequirementId == requirement);
        rel.Evidence.Should().Contain(e => e.Kind == "scenario", "the scenario's own text names it");
        rel.ResponsibilityEvidence.Should().BeFalse();
        p.SuggestedArea.Should().NotBe(area, $"{requirement} is mentioned in a scenario of {story}; that does not make «{area}» its primary area");
    }

    [Fact]
    public void T15_real_US3_and_US6_are_decided_by_their_own_tasks()
    {
        Real.Value["US3"].SuggestedArea.Should().Be("Period lock");
        Real.Value["US6"].SuggestedArea.Should().Be("Expected population and coverage");
    }

    // ── T18–T20, T29 decisions ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T18_T29_a_confirmed_placement_survives_role_changes_and_roles_never_touch_links()
    {
        var doc = Doc(new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetPrimaryArea, StoryKey = "spec.md#us1", PrimaryArea = "Lists" },
            new StoryGroupingOperation { Kind = StoryGroupingOperationKind.AddLink, StoryKey = "spec.md#us1", RequirementId = "FR-001" });
        var after = StoryGroupingEngine.Apply(doc, [new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetLinkRole, StoryKey = "spec.md#us1", RequirementId = "FR-001", Role = StoryLinkRole.Primary }],
            "bo", DateTimeOffset.UtcNow, _ => true);
        after.Decisions.Should().BeEquivalentTo(doc.Decisions);
        after.Placements.Should().BeEquivalentTo(doc.Placements);
        One(In(Story("US1", tasks: ["FR-002"])), after).PrimaryArea.Should().Be("Lists");
    }

    // ── T21–T24 tasks and cross-artifact evidence ──────────────────────────────────────────────────────────────

    [Fact]
    public void T21_T22_T23_evidence_kinds_keep_their_path_and_a_detail_or_plan_mention_is_not_a_task_of_the_story()
    {
        var real = RequirementSectionsTests.Spec("SkoleService/specs/001-skoleservice/tasks.md");
        var us8 = StoryTaskContext.Read("tasks.md", real, "US8", taskLines: true);
        us8.Should().Contain(t => t.RequirementId == "FR-011" && t.Kind == StoryTaskRequirement.TaskDetail && t.TaskId != null, "an «As built» note under a [US8] task");
        us8.Where(t => t.Kind == StoryTaskRequirement.TaskLine).Should().OnlyContain(t => t.TaskText.Contains("[US8]"));
        var us1 = StoryTaskContext.Read("tasks.md", real, "US1", taskLines: true);
        us1.Should().NotContain(t => t.TaskText.StartsWith("**Goal**", StringComparison.Ordinal) && t.TaskText.Contains("US2"), "US2's goal says «Processing is US1's»; that is not a US1 task");
    }

    [Fact]
    public void T24_a_task_link_is_traceability_evidence_only_never_implementation_coverage_or_a_result()
    {
        // The relation records where it is written; it carries no implemented / tested / passed state at all.
        typeof(StoryRequirementRelation).GetProperties().Select(p => p.Name).Should().NotContain(["Implemented", "Covered", "Passed", "Tested", "Executed"]);
        One(In(Story("US1", tasks: ["FR-001"]))).Requirements.Single().Evidence.Single().Kind.Should().Be(StoryTaskRequirement.TaskLine);
    }

    // ── T25–T26 USn-k scenario references ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void T25_scenario_references_of_the_project_convention_are_parsed_verbatim()
    {
        SourceScenarioReferences.Parse("covers US5-2 and US4-7; authorisation (SC-004, US2 scenarios 2–3); see User Story 3 scenario 3; not TT-US1-2")
            .Select(r => $"{r.StoryId}-{r.Number}").Should().Equal("US5-2", "US4-7", "US2-2", "US2-3", "US3-3");
    }

    [Fact]
    public void T26_a_reference_resolves_only_in_its_own_specification()
    {
        // SkoleService: US3 has 6 scenarios; "US3-14" belongs to another feature and must not resolve here; US9 does not exist here.
        var counts = new Dictionary<string, int> { ["US2"] = 8, ["US3"] = 6 };
        var resolved = SourceScenarioReferences.Resolve(SourceScenarioReferences.Parse("US3-14, US2-3 and US9-1"), "SkoleService/specs/001-skoleservice/spec.md",
            s => counts.TryGetValue(s, out var c) ? c : null);
        resolved.Select(r => (r.Reference.StoryId, r.Reference.Number, r.Resolved)).Should().Equal(("US3", 14, false), ("US2", 3, true), ("US9", 1, false));
        resolved.Where(r => !r.Resolved).Should().OnlyContain(r => r.Reason != null, "an unresolved reference says why");
    }

    // ── T27–T28 names never carry identity ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void T27_T28_a_historical_contract_name_changes_neither_links_nor_story_or_scenario_identity()
    {
        static SpecificationSemanticModel M(string name)
        {
            var s = $"# F\n\n## User Scenarios & Testing\n\n### User Story 3 - Lock (Priority: P1)\n\nLock.\n\n**Acceptance Scenarios**:\n\n1. **Given** a period has locked, **When** it ends, **Then** one `{name}` notification is published (FR-009).\n\n## Requirements\n\n### Functional Requirements\n\n**A**\n\n- **FR-009**: x\n";
            return SpecExplorerService.BuildSemanticModel(SpecExplorerService.Parse(s), s);
        }
        var old = M("SkolenaervaersperiodeLukket"); var current = M("SkolenaervaerPeriodClosed");
        (old.AcceptanceScenarios.Single().ParentStoryKey, old.AcceptanceScenarios.Single().NumberInStory).Should().Be((current.AcceptanceScenarios.Single().ParentStoryKey, current.AcceptanceScenarios.Single().NumberInStory));
        old.AcceptanceScenarios.Single().LinkedRequirements.Select(r => r.Id).Should().Equal(current.AcceptanceScenarios.Single().LinkedRequirements.Select(r => r.Id));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static StoryGroupingDocument Doc(params StoryGroupingOperation[] ops) =>
        StoryGroupingEngine.Apply(new StoryGroupingDocument { ProjectKey = "p", SpecificationPath = "spec.md" }, ops, "anna", DateTimeOffset.UtcNow, _ => true);

    private static string Repo([CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "frontend"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
