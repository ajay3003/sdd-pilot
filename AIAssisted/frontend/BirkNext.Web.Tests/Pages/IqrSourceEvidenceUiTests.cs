using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

public sealed class IqrSourceEvidenceUiTests : BunitContext
{
    private static IqrSourceSnapshot Snapshot() => new()
    {
        IntegrationId = "person", Archive = new("M2LB-fixture.zip", new string('a', 64), 3), Status = SourceAnalysisStatus.Partial,
        Rules = [new() { Id = "key", Field = "PersonPK", Requirement = "Required for mapper output", Symbol = "PersonMapper.Map", Confidence = SourceConfidence.StrongSourceEvidence, Location = new("src/PersonMapper.cs", 5) }],
        Tests = [new() { Id = "unit", Class = "PersonMapperTests", Method = "MissingKey", Layer = DeveloperTestLayer.Unit, Location = new("tests/PersonMapperTests.cs", 8) }],
        Coverage = [new("key", ["unit"], [SourceCoverageStatus.DeveloperUnitCovered, SourceCoverageStatus.RuntimeGap, SourceCoverageStatus.E2EGap], "Explicit missing input and same null assertion", "Covered by developer test; no same-layer duplicate required")],
        Dataflows = [new("SecurityLevel", ["Raw payload → not resolved", "Guard receives field"], SourceConfidence.Partial, [], "Cross-layer gap: constant zero may bypass classification")],
        Limitations = ["Developer test execution result unavailable", "Formal schema unavailable", "End-to-end processing not exercised"]
    };
    [Fact] public void DetailsDistinguishTestExistenceFromExecutionAndKeepDifferentLayerGaps()
    {
        var cut = Render<IqrSourceDetails>(p => p.Add(c => c.Snapshot, Snapshot()));
        cut.Markup.Should().Contain("Developer unit test exists").And.Contain("no same-layer duplicate").And.Contain("Cross-layer gap").And.Contain("RuntimeGap").And.Contain("E2EGap");
        cut.Markup.Should().Contain("execution results are unavailable").And.NotContain("tests passed");
        cut.FindAll("details > summary").Should().HaveCount(5);
        cut.Find("[role=region]").GetAttribute("tabindex").Should().Be("0");
    }
    [Fact] public void SetupAllowsExplicitExistingSelectionPerIntegration()
    {
        Services.AddSingleton(Mock.Of<IIntegrationCatalogApiService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
        var snapshot = Snapshot();
        var selections = new List<IqrSourceSelection>();
        var cut = Render<IqrSourceSetup>(p => p.Add(c => c.EnvironmentId, "dev").Add(c => c.Integrations, new List<IntegrationDefinition> { new() { Id = "person", DisplayName = "Person Adapter", Enabled = true } })
            .Add(c => c.Snapshots, new List<IqrSourceSnapshot> { snapshot }).Add(c => c.Selections, selections));
        cut.Find("#iqr-source-integration").Change("person");
        cut.Find("#iqr-source-existing").Change(snapshot.Id.ToString());
        selections.Should().ContainSingle(s => s.IntegrationId == "person" && s.SnapshotId == snapshot.Id);
        cut.Find("#iqr-source-file").GetAttribute("accept").Should().Be(".zip");
        cut.FindAll("label[for]").Should().HaveCount(3);
        cut.Find("#iqr-source-existing").Change("");
        selections.Should().BeEmpty();
    }
    [Fact] public void ExportUsesImmutableEvidenceAndExcludesSourceAndSecrets()
    {
        var result = new IntegrationReviewResult { SourceSnapshots = [Snapshot()] };
        var html = new ReportExportService().ExportIntegrationReview(result, "Example");
        html.Should().Contain("Source evidence").And.Contain("PersonPK").And.Contain("PersonMapperTests.MissingKey").And.Contain("Cross-layer gap").And.Contain("RuntimeGap");
        html.Should().Contain("Commit Unknown").And.Contain("Developer tests discovered, not executed");
        html.Should().NotContain("SECRET_SENTINEL_123").And.NotContain("C:\\").And.NotContain("public static class");
    }
}
