using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

public sealed class PlanExplorerConstitutionTabTests : BunitContext
{
    private readonly PlanAnalysisService _analysisService = new();

    public PlanExplorerConstitutionTabTests()
    {
        Services.AddSingleton<IPlanAnalysisService>(_analysisService);
    }

    [Fact]
    public void Constitution_NoEvidenceValues_HidesEvidenceColumn()
    {
        var plan = PlanWithGates([
            Gate("PP-01 Contract", PlanGateStatus.Pass, null, "Contract note"),
            Gate("PP-02 Auth", PlanGateStatus.Warning, "", "Auth note"),
            Gate("GL-11 Network", PlanGateStatus.Pass, "   ", "Network note"),
        ]);

        var cut = RenderConstitution(plan);

        cut.Markup.Should().NotContain("<th>Evidence</th>");
        cut.FindAll("td.pe-gate-evidence").Should().BeEmpty();
    }

    [Fact]
    public void Constitution_AtLeastOneEvidenceValue_ShowsEvidenceColumn()
    {
        var plan = PlanWithGates([
            Gate("PP-01 Contract", PlanGateStatus.Pass, null, "Contract note"),
            Gate("PP-02 Auth", PlanGateStatus.Warning, "Implemented in middleware", "Auth note"),
        ]);

        var cut = RenderConstitution(plan);

        cut.Markup.Should().Contain("<th>Evidence</th>");
        cut.FindAll("tbody tr").Should().HaveCount(2);
        cut.FindAll("td.pe-gate-evidence").Should().HaveCount(2);
        cut.Markup.Should().Contain("Implemented in middleware");
    }

    [Fact]
    public void Constitution_NotesRemainVisibleWhenEvidenceHidden()
    {
        var plan = PlanWithGates([
            Gate("PP-01 Contract", PlanGateStatus.Pass, null, "SCIM inbound contract"),
            Gate("GL-20 Outbox", PlanGateStatus.Warning, "", "GL-20 deviation rationale"),
        ]);

        var cut = RenderConstitution(plan);

        cut.Markup.Should().NotContain("<th>Evidence</th>");
        cut.Markup.Should().Contain("<th>Notes</th>");
        cut.FindAll("td.pe-gate-notes").Should().HaveCount(2);
        cut.Markup.Should().Contain("SCIM inbound contract");
        cut.Markup.Should().Contain("GL-20 deviation rationale");
    }

    [Fact]
    public void Constitution_FilteredRows_ControlEvidenceVisibility()
    {
        var plan = PlanWithGates([
            Gate("Evidence Gate", PlanGateStatus.Pass, "Implemented in tests", "Has evidence"),
            Gate("NoteOnly Gate", PlanGateStatus.Warning, null, "Only notes"),
        ]);

        var cut = RenderConstitution(plan);
        cut.Markup.Should().Contain("<th>Evidence</th>");

        cut.Find("input.pe-search").Input("NoteOnly");

        cut.Markup.Should().NotContain("<th>Evidence</th>");
        cut.FindAll("td.pe-gate-evidence").Should().BeEmpty();
        cut.Markup.Should().Contain("Only notes");
        cut.Markup.Should().NotContain("Implemented in tests");
    }

    [Fact]
    public void Constitution_SCIMPlan_HidesEvidenceColumnAndPreservesGateStateAndNotes()
    {
        var markdown = File.ReadAllText(@"C:\Users\ajaan\source\sdd-repos\BirkNext\SampleData\autorisasjon\plan.md");
        var plan = _analysisService.Parse(markdown);

        plan.Gates.Should().HaveCount(13);
        plan.Gates.Should().OnlyContain(g => string.IsNullOrWhiteSpace(g.Evidence));
        plan.Gates.Should().OnlyContain(g => !string.IsNullOrWhiteSpace(g.Notes));
        plan.Gates.Count(g => g.Status == PlanGateStatus.Pass).Should().Be(10);
        plan.Gates.Count(g => g.Status == PlanGateStatus.Warning).Should().Be(3);
        plan.Gates.Count(g => g.Status == PlanGateStatus.Fail).Should().Be(0);
        plan.Gates.Single(g => g.RuleId == "PP-01").Notes.Should().Contain("SCIM endpoint is the inbound contract");
        plan.Gates.Single(g => g.RuleId == "PP-02").Notes.Should().Contain("Bearer token");
        plan.Gates.Single(g => g.RuleId == "PP-02").Notes.Should().Contain("Key Vault");
        plan.Gates.Single(g => g.RuleId == "GL-18").Notes.Should().Contain("sync-state");
        plan.Gates.Single(g => g.RuleId == "GL-20").Notes.Should().Contain("Polly retry");
        plan.Gates.Single(g => g.RuleId == "PS-01").Notes.Should().Contain("machine-to-machine");

        var cut = RenderConstitution(plan);

        cut.Markup.Should().NotContain("<th>Evidence</th>");
        cut.FindAll("td.pe-gate-evidence").Should().BeEmpty();
        cut.Markup.Should().Contain("<th>Gate</th>");
        cut.Markup.Should().Contain("<th>Status</th>");
        cut.Markup.Should().Contain("<th>Notes</th>");
        cut.FindAll("tbody tr").Should().HaveCount(13);
        cut.Markup.Should().Contain("SCIM endpoint is the inbound contract");
        cut.Markup.Should().Contain("machine-to-machine");
    }

    private IRenderedComponent<PlanExplorerPanel> RenderConstitution(PlanDocument plan) =>
        Render<PlanExplorerPanel>(parameters => parameters
            .Add(component => component.ParsedPlan, plan)
            .Add(component => component.InitialView, "constitution"));

    private static PlanDocument PlanWithGates(List<PlanGate> gates) =>
        new()
        {
            Title = "Implementation Plan: Test",
            Gates = gates,
            Health = new PlanHealth
            {
                TotalConstitutionGates = gates.Count,
                PassedGates = gates.Count(g => g.Status == PlanGateStatus.Pass),
                WarningGates = gates.Count(g => g.Status == PlanGateStatus.Warning),
                FailedGates = gates.Count(g => g.Status == PlanGateStatus.Fail),
            },
        };

    private static PlanGate Gate(string gate, PlanGateStatus status, string? evidence, string? notes) =>
        new()
        {
            Gate = gate,
            RuleId = gate.Split(' ')[0],
            Principle = gate,
            Status = status,
            Evidence = evidence,
            Notes = notes,
        };
}
