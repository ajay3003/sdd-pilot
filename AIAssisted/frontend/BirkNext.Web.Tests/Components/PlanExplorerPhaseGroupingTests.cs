using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

public sealed class PlanExplorerPhaseGroupingTests : BunitContext
{
    private readonly PlanAnalysisService _analysisService = new();

    public PlanExplorerPhaseGroupingTests()
    {
        Services.AddSingleton<IPlanAnalysisService>(_analysisService);
    }

    private IRenderedComponent<PlanExplorerPanel> RenderPhases(string markdown) =>
        Render<PlanExplorerPanel>(parameters => parameters
            .Add(component => component.ParsedPlan, _analysisService.Parse(markdown))
            .Add(component => component.InitialView, "phases"));

    [Fact]
    public void DuplicatePhase0Headings_RenderBothWithNeutralNote()
    {
        var cut = RenderPhases("""
            # Implementation Plan: Dup

            ## Implementation Phases

            ### Phase 0 — Foundation
            - Task A

            ### Phase 0 — Setup
            - Task B
            """);

        cut.FindAll(".pe-phase-item").Should().HaveCount(2);
        cut.Markup.Should().Contain("Foundation").And.Contain("Setup");

        var note = cut.Find(".pe-phase-shared-note");
        note.GetAttribute("role").Should().Be("note");
        note.TextContent.Should().Contain("2 source sections map to Phase 0");
    }

    [Fact]
    public void LabelledPhases_RenderOwnLabel_NotPre()
    {
        var path = TestDataHelper.ResolveFixturePath("person-module", "plan.md");
        var cut = RenderPhases(File.ReadAllText(path));

        cut.FindAll(".pe-phase-item").Should().HaveCount(6);
        cut.FindAll(".pe-phase-number").Select(e => e.TextContent).Should().Equal("A", "B", "C", "D", "E", "F");
        cut.FindAll(".pe-phase-item.is-pre").Should().BeEmpty();
        cut.FindAll(".pe-phase-shared-note").Should().BeEmpty();
    }

    [Fact]
    public void Plan008_RendersAllGroups()
    {
        var path = TestDataHelper.ResolveFixturePath("..", "specs", "008-traceability-first", "plan.md");
        var cut = RenderPhases(File.ReadAllText(path));

        cut.FindAll(".pe-phase-item").Should().HaveCount(7);
        cut.FindAll(".pe-phase-number").Select(e => e.TextContent).Should().Equal("A", "B", "C", "D", "E", "F", "G");
    }

    [Fact]
    public void UnnumberedPhase_RendersNeutralMarker()
    {
        var cut = RenderPhases("""
            # Implementation Plan: Unnumbered

            ## Implementation Phases

            ### Phase 1: Core
            - Task A

            ### Rollout
            - Task R
            """);

        cut.FindAll(".pe-phase-number").Select(e => e.TextContent).Should().Equal("P1", "–");
    }
}
