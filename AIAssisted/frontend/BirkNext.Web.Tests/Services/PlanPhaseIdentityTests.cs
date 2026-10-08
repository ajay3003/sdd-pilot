using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using System.IO;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Regression: Plan Explorer crashed with "An item with the same key has already been added.
/// Key: Phase0" because every unnumbered phase heading defaulted to PhaseNumber 0 and
/// BuildSemanticModel built PhaseToTasks with ToDictionary on "Phase{n}".
/// </summary>
public sealed class PlanPhaseIdentityTests
{
    private readonly PlanAnalysisService _svc = new();

    private static string Plan(string phases) => $"""
        # Implementation Plan: Phases

        ## Implementation Phases

        {phases}
        """;

    [Fact]
    public void NormalPhases_UnchangedNumberingAndKeys()
    {
        var doc = _svc.Parse(Plan("""
            ### Phase 0: Setup
            - T001 bootstrap

            ### Phase 1: Core
            - T002 core

            ### Phase 2: Polish
            - T003 polish
            """));

        doc.Phases.Select(p => p.PhaseNumber).Should().Equal(0, 1, 2);
        doc.Phases.Select(p => p.PhaseKey).Should().Equal("Phase0", "Phase1", "Phase2");
        doc.Phases.Should().OnlyContain(p => p.IdentityKind == PlanPhaseIdentityKind.Numbered);

        var model = PlanAnalysisService.BuildSemanticModel(doc);
        model.PhaseToTasks.Keys.Should().Equal("Phase0", "Phase1", "Phase2");
        model.PhaseToTasks["Phase1"].Should().Equal("T002 core");
        model.PhaseGroups.Should().OnlyContain(g => !g.HasMultipleSources);
    }

    [Fact]
    public void MultipleItemsInOnePhase_AllRetainedInOneGroup()
    {
        var doc = _svc.Parse(Plan("""
            ### Phase 0: Foundation
            - Task A
            - Task B
            - Task C
            """));

        var model = PlanAnalysisService.BuildSemanticModel(doc);

        model.PhaseGroups.Should().ContainSingle();
        model.PhaseToTasks["Phase0"].Should().Equal("Task A", "Task B", "Task C");
    }

    [Fact]
    public void DuplicatePhaseHeadings_BothRetained_NoException()
    {
        var doc = _svc.Parse(Plan("""
            ### Phase 0 — Foundation
            - Task A

            ### Phase 1 — Core
            - Task X

            ### Phase 0 — Setup
            - Task B
            """));

        doc.Phases.Should().HaveCount(3);
        // Numeric order, ties in source order.
        doc.Phases.Select(p => p.Title).Should().Equal("Foundation", "Setup", "Core");

        var model = PlanAnalysisService.BuildSemanticModel(doc);

        var phase0 = model.PhaseGroups.Single(g => g.PhaseKey == "Phase0");
        phase0.HasMultipleSources.Should().BeTrue();
        phase0.DisplayLabel.Should().Be("Phase 0");
        phase0.Sources.Select(s => s.SourceHeading).Should().Equal("Phase 0 — Foundation", "Phase 0 — Setup");
        phase0.Sources.Select(s => s.SourceOrder).Should().Equal(0, 2);
        phase0.Sources.Should().OnlyContain(s => s.SourceSection == "Implementation Phases" && s.SourceLine > 0);
        model.PhaseToTasks["Phase0"].Should().Equal("Task A", "Task B");
    }

    [Fact]
    public void NormalizationCollision_Phase0AndPhase0NoSpace_ShareGroupKeepBothSources()
    {
        var doc = _svc.Parse(Plan("""
            ### Phase 0 — Foundation
            - Task A

            ### Phase0 — Setup
            - Task B
            """));

        doc.Phases.Should().HaveCount(2).And.OnlyContain(p => p.PhaseNumber == 0 && p.PhaseKey == "Phase0");

        var model = PlanAnalysisService.BuildSemanticModel(doc);
        model.PhaseGroups.Should().ContainSingle()
            .Which.Sources.Select(s => s.SourceHeading).Should().Equal("Phase 0 — Foundation", "Phase0 — Setup");
    }

    [Fact]
    public void UnnumberedPhase_IsNotPhase0()
    {
        var doc = _svc.Parse(Plan("""
            ### Phase 0: Setup
            - Task A

            ### Rollout
            - Task R

            ### Hardening
            - Task H
            """));

        var rollout = doc.Phases.Single(p => p.SourceHeading == "Rollout");
        rollout.PhaseNumber.Should().BeNull();
        rollout.IdentityKind.Should().Be(PlanPhaseIdentityKind.Unnumbered);
        rollout.PhaseKey.Should().NotBe("Phase0");

        var model = PlanAnalysisService.BuildSemanticModel(doc);
        model.PhaseToTasks["Phase0"].Should().Equal("Task A");
        model.PhaseToTasks.Should().HaveCount(3, "each unnumbered heading is its own entry");
        doc.Phases.Select(p => p.SourceHeading).Should().Equal("Phase 0: Setup", "Rollout", "Hardening");
    }

    [Fact]
    public void LabelledPhases_GetDistinctKeysInSourceOrder()
    {
        var doc = _svc.Parse(Plan("""
            ### Phase B — Second
            - Task B

            ### Phase A — First
            - Task A

            ### Group C — Third
            - Task C
            """));

        doc.Phases.Should().OnlyContain(p => p.IdentityKind == PlanPhaseIdentityKind.Labelled && p.PhaseNumber == null);
        doc.Phases.Select(p => p.PhaseKey).Should().Equal("PhaseB", "PhaseA", "GroupC");
        doc.Phases.Select(p => p.PhaseLabel).Should().Equal("Phase B", "Phase A", "Group C");

        var model = PlanAnalysisService.BuildSemanticModel(doc);
        model.PhaseGroups.Select(g => g.DisplayLabel).Should().Equal("Phase B", "Phase A", "Group C");
    }

    [Fact]
    public void NumericOrder_IsNumericNotLexical()
    {
        var doc = _svc.Parse(Plan("""
            ### Phase 10: Ten
            ### Phase 2: Two
            ### Phase 1: One
            """));

        doc.Phases.Select(p => p.PhaseNumber).Should().Equal(1, 2, 10);
    }

    [Fact]
    public void BarePhaseHeadingWithoutTitle_IsNumbered()
    {
        var doc = _svc.Parse(Plan("""
            ### Phase 4
            - Task
            """));

        doc.Phases.Single().PhaseNumber.Should().Be(4);
        doc.Phases.Single().Title.Should().Be("Phase 4");
    }

    [Fact]
    public void FallbackScan_KeepsDuplicateNumberedHeadingsFromOtherSections()
    {
        var doc = _svc.Parse("""
            # Implementation Plan: Fallback

            ## Project Structure

            ### Phase 0 — Foundation

            ## Technical Context

            ### Phase 0 — Setup
            """);

        doc.Phases.Select(p => p.Title).Should().Equal("Foundation", "Setup");
        doc.Phases.Select(p => p.SourceSection).Should().Equal("Project Structure", "Technical Context");
        PlanAnalysisService.BuildSemanticModel(doc).PhaseGroups.Should().ContainSingle();
    }

    [Fact]
    public void ReviewContext_PlanToTasks_MergesInsteadOfOverwriting()
    {
        var doc = _svc.Parse(Plan("""
            ### Phase 0 — Foundation
            - Task A

            ### Phase 0 — Setup
            - Task B
            """));

        var context = ReviewContextFactory.Create(
            new ConstitutionSemanticModel(), new SpecificationSemanticModel(),
            PlanAnalysisService.BuildSemanticModel(doc), new TaskSemanticModel(), new DataModelSemanticModel());

        context.PlanToTasks["Phase0"].Should().Equal("Task A", "Task B");
    }

    public static TheoryData<string> AllRepositoryPlans()
    {
        return new TheoryData<string> { "autorisasjon/plan.md", "person-module/plan.md" };
    }

    [Theory]
    [MemberData(nameof(AllRepositoryPlans))]
    public void EveryRepositoryPlan_BuildsSemanticModelWithoutLosingPhases(string relativePath)
    {
        var path = TestDataHelper.ResolveFixturePath(relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries));
        var doc = _svc.Parse(File.ReadAllText(path));

        var model = PlanAnalysisService.BuildSemanticModel(doc);

        model.PhaseGroups.Sum(g => g.Sources.Count).Should().Be(doc.Phases.Count);
        model.PhaseToTasks.Values.Sum(t => t.Count).Should().Be(doc.Phases.Sum(p => p.Tasks.Count));
    }

    [Fact]
    public void Plan008_LetteredGroups_AllRetained()
    {
        var path = TestDataHelper.ResolveFixturePath("..", "specs", "008-traceability-first", "plan.md");
        var model = PlanAnalysisService.BuildSemanticModel(_svc.Parse(File.ReadAllText(path)));

        model.PhaseGroups.Select(g => g.PhaseKey).Should()
            .Equal("GroupA", "GroupB", "GroupC", "GroupD", "GroupE", "GroupF", "GroupG");
        model.PhaseToTasks.Values.Sum(t => t.Count).Should().Be(69);
        model.Phases.Should().NotContain(p => p.PhaseNumber == 0);
    }

    [Fact]
    public void PersonModule_LetteredPhases_AllRetained()
    {
        var path = TestDataHelper.ResolveFixturePath("person-module", "plan.md");
        var model = PlanAnalysisService.BuildSemanticModel(_svc.Parse(File.ReadAllText(path)));

        model.PhaseGroups.Select(g => g.PhaseKey).Should()
            .Equal("PhaseA", "PhaseB", "PhaseC", "PhaseD", "PhaseE", "PhaseF");
    }
}
