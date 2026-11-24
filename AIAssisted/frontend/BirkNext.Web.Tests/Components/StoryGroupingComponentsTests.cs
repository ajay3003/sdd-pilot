using BirkNext.Sdd;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Components.ManualTesting;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// «Tilknyttede krav» / «Administrer kravkoblinger» and «Administrer grupper» (2026-11-24): provenance is visible, a suggestion never looks like a
/// link, only real requirements of the specification can be added, a source link can only be disputed, every change is previewed before it is
/// saved, and grouping changes never touch requirement links.
/// </summary>
public sealed class StoryGroupingComponentsTests : BunitContext
{
    private const string Path = "SkoleService/specs/001-skoleservice/spec.md";

    private static readonly RequirementSectionMap Sections = RequirementSections.Read("""
        ## Requirements
        ### Functional Requirements
        **Intake and identity**
        - **FR-001**: Accept a valid report.
        **Quality control (accept and flag)**
        - **FR-014**: Merge duplicate absence codes.
        - **FR-015**: Flag inconsistent totals.
        - **FR-015a**: Flag attendance above plan.
        """);

    private static readonly List<ProjectRequirementOption> Catalog =
        [new("FR-001", "Accept a valid report.", Path), new("FR-014", "Merge duplicate absence codes.", Path), new("FR-015", "Flag inconsistent totals.", Path), new("FR-015a", "Flag attendance above plan.", Path)];

    private static readonly CoverageUserStory Us1 = new()
    {
        Key = CoverageUserStory.KeyOf(Path, "us1"), StoryId = "US1", Ordinal = 1, Title = "Accept a valid report", SpecificationPath = Path, NamedRequirements = ["FR-015"],
        TaskRequirements = [new("FR-001", "T1", "[US1] intake", "tasks.md", 3)],
    };

    private static List<StoryGroupingEngine.StoryInputs> Inputs() => [new(Us1, [], [], [("FR-014", "Same rule: merge")])];

    private (IRenderedComponent<StoryRequirementLinks> Cut, List<IReadOnlyList<StoryGroupingOperation>> Saved) Links(StoryGroupingDocument? doc = null, bool canEdit = true, string? refusal = null)
    {
        doc ??= new StoryGroupingDocument { ProjectKey = "m2lb", SpecificationPath = Path };
        var profile = StoryGroupingEngine.Profiles(Inputs(), Sections, doc).Single();
        var saved = new List<IReadOnlyList<StoryGroupingOperation>>();
        var cut = Render<StoryRequirementLinks>(p => p.Add(x => x.Profile, profile).Add(x => x.Document, doc).Add(x => x.Sections, Sections).Add(x => x.Inputs, Inputs())
            .Add(x => x.Catalog, Catalog).Add(x => x.CanEdit, canEdit).Add(x => x.OnApply, ops => { saved.Add(ops); return Task.FromResult(refusal); }));
        return (cut, saved);
    }

    [Fact]
    public void C01_links_are_grouped_by_their_source_section_with_their_provenance()
    {
        var (cut, _) = Links();
        cut.FindAll("[data-testid=srl-area]").Select(a => a.GetAttribute("data-area")).Should().Equal("Intake and identity", "Quality control (accept and flag)");
        var basis = cut.FindAll("[data-testid=srl-link]").ToDictionary(l => l.GetAttribute("data-requirement")!, l => l.GetAttribute("data-basis"));
        basis.Should().BeEquivalentTo(new Dictionary<string, string?> { ["FR-015"] = "SourceExplicit", ["FR-001"] = "Structural", ["FR-014"] = "Suggested" });
        cut.Find("[data-testid=srl-link][data-requirement=FR-014]").TextContent.Should().Contain("Foreslått", "a suggestion never looks like a link");
        cut.Find("[data-testid=srl-link][data-requirement=FR-015]").TextContent.Should().Contain("Nevnt i brukerhistorien");
    }

    [Fact]
    public void C02_without_permission_there_is_no_editor()
    {
        var (cut, _) = Links(canEdit: false);
        cut.FindAll("[data-testid=srl-manage]").Should().BeEmpty();
    }

    [Fact]
    public void C03_confirm_a_suggestion_is_previewed_then_saved_as_one_operation()
    {
        var (cut, saved) = Links();
        cut.Find("[data-testid=srl-manage]").Click();
        cut.Find("[data-testid=srl-edit-row][data-requirement=FR-014] [data-testid=srl-confirm]").Click();

        cut.FindAll("[data-testid=srl-preview-line]").Select(l => l.TextContent).Should().Contain(l => l.Contains("+FR-014"));
        saved.Should().BeEmpty("nothing is saved before «Lagre»");
        cut.Find("[data-testid=srl-save]").Click();

        saved.Should().ContainSingle().Which.Single().Should().Match<StoryGroupingOperation>(o => o.Kind == StoryGroupingOperationKind.ConfirmLink && o.RequirementId == "FR-014");
    }

    [Fact]
    public void C04_a_source_link_can_only_be_disputed_with_a_reason_and_its_evidence_stays()
    {
        var (cut, saved) = Links();
        cut.Find("[data-testid=srl-manage]").Click();
        var row = cut.Find("[data-testid=srl-edit-row][data-requirement=FR-015]");
        row.QuerySelectorAll("[data-testid=srl-remove]").Should().BeEmpty("a source-stated link is never removable");
        row.QuerySelectorAll("[data-testid=srl-replace]").Should().BeEmpty();
        cut.Find("[data-testid=srl-edit-row][data-requirement=FR-015] [data-testid=srl-dispute]").Click();
        cut.Find("[data-testid=srl-dispute-stage]").HasAttribute("disabled").Should().BeTrue("a dispute needs a reason");
        cut.Find("[data-testid=srl-dispute-reason]").Input("The story is about attendance above plan");
        cut.Find("[data-testid=srl-dispute-with]").Change("FR-015a");
        cut.Find("[data-testid=srl-dispute-stage]").Click();
        cut.Find("[data-testid=srl-save]").Click();

        var op = saved.Single().Single();
        (op.Kind, op.RequirementId, op.NewRequirementId, op.NewDisplayId).Should().Be((StoryGroupingOperationKind.DisputeLink, "FR-015", "FR-015a", "FR-015a"));
    }

    [Fact]
    public void C05_search_finds_only_this_specifications_requirements_and_adds_one()
    {
        var (cut, saved) = Links();
        cut.Find("[data-testid=srl-manage]").Click();
        cut.Find("[data-testid=srl-search]").Input("above plan");
        cut.FindAll("[data-testid=srl-result]").Select(r => r.GetAttribute("data-requirement")).Should().Equal("FR-015a");
        cut.Find("[data-testid=srl-result] [data-testid=srl-add]").Click();
        cut.Find("[data-testid=srl-save]").Click();
        saved.Single().Single().Should().Match<StoryGroupingOperation>(o => o.Kind == StoryGroupingOperationKind.AddLink && o.RequirementId == "FR-015a" && o.DisplayId == "FR-015a");
    }

    [Fact]
    public void C06_a_manual_link_can_be_replaced_and_a_refused_save_is_shown_and_kept()
    {
        var doc = StoryGroupingEngine.Apply(new StoryGroupingDocument { ProjectKey = "m2lb", SpecificationPath = Path },
            [new() { Kind = StoryGroupingOperationKind.AddLink, StoryKey = Us1.Key, RequirementId = "FR-014", DisplayId = "FR-014" }], "anna", DateTimeOffset.UtcNow, _ => true);
        var (cut, saved) = Links(doc, refusal: "Grupperingen er endret av noen andre siden du åpnet den.");
        cut.Find("[data-testid=srl-manage]").Click();
        cut.Find("[data-testid=srl-edit-row][data-requirement=FR-014] [data-testid=srl-replace]").Click();
        cut.Find("[data-testid=srl-replace-with]").Change("FR-015a");
        cut.Find("[data-testid=srl-replace-stage]").Click();
        cut.FindAll("[data-testid=srl-preview-line]").Should().NotBeEmpty();
        cut.Find("[data-testid=srl-save]").Click();

        saved.Single().Single().Kind.Should().Be(StoryGroupingOperationKind.ReplaceLink);
        cut.Find("[data-testid=srl-error]").TextContent.Should().Contain("endret av noen andre");
        cut.FindAll("[data-testid=srl-preview]").Should().ContainSingle("a refused change stays staged");
        cut.FindAll("[data-testid=srl-history-item]").Should().ContainSingle(h => h.TextContent.Contains("Lagt til") && h.TextContent.Contains("anna"));
    }

    // ── Group organizer ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void C10_create_a_group_assign_an_area_place_a_story_preview_and_save_together()
    {
        var doc = new StoryGroupingDocument { ProjectKey = "m2lb", SpecificationPath = Path };
        var profiles = StoryGroupingEngine.Profiles(Inputs(), Sections, doc);
        var saved = new List<IReadOnlyList<StoryGroupingOperation>>();
        var cut = Render<GroupOrganizer>(p => p.Add(x => x.Document, doc).Add(x => x.Sections, Sections).Add(x => x.Inputs, Inputs()).Add(x => x.Profiles, profiles)
            .Add(x => x.OnApply, ops => { saved.Add(ops); return Task.FromResult<string?>(null); }));

        cut.Find("[data-testid=gor-new-name]").Input("Mottak og behandling");
        cut.Find("[data-testid=gor-create]").Click();
        cut.Find("[data-testid=gor-group] [data-testid=gor-area][data-area='Intake and identity']").Change(true);
        cut.Find("[data-testid=gor-story] [data-testid=gor-place]").Change(cut.Find("[data-testid=gor-group]").GetAttribute("data-group"));

        cut.FindAll("[data-testid=gor-preview-line]").Select(l => l.TextContent).Should().Contain("Ny gruppe «Mottak og behandling».")
            .And.Contain(l => l.Contains("endres ikke"));
        cut.Find("[data-testid=gor-save]").Click();

        var ops = saved.Single();
        ops.Select(o => o.Kind).Should().Equal(StoryGroupingOperationKind.CreateGroup, StoryGroupingOperationKind.SetGroupAreas, StoryGroupingOperationKind.PlaceStory);
        ops[2].GroupId.Should().Be(ops[0].GroupId, "a group created in the same change is referred to by the id it was staged with");
        ops.Select(o => o.Kind).Should().NotContain([StoryGroupingOperationKind.AddLink, StoryGroupingOperationKind.RemoveLink, StoryGroupingOperationKind.ReplaceLink],
            "grouping never changes requirement links");
    }
}
