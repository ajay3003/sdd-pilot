using BirkNext.Delivery;
using BirkNext.PipelineReview;
using BirkNext.SourceDomains;
using BirkNext.Web.Components;
using BirkNext.Web.Components.SourceDomains;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Shared delivery evidence on consumer pages: quality reviews see gates configured (never passed), Target Environments and Environment
/// Analysis see pipeline labels as mapping candidates with an evidence ladder, Source Analysis CI/CD shows the same deployments Pipeline
/// Review derives. The snapshot is always chosen explicitly.
/// </summary>
public sealed class DeliveryEvidenceUiTests : BunitContext
{
    private static readonly Guid SnapshotId = Guid.Parse("de000000-0000-0000-0000-000000000014");
    private static readonly DeliveryProvenance Where = new("pipelines/release.yml", 12, "pipelines/release.yml", "DeployQA", "deploy", null, "azure-pipelines-yaml");

    private static DeliveryEvidence Evidence() => new()
    {
        SnapshotId = SnapshotId, Archive = "shop.zip", State = "Reviewed",
        Deployments =
        [
            new("d1", "pipelines/release.yml", "shop-release", "DeployQA", "deploy", "shop-qa", SourceEnvironmentKind.QA, ["Application"], ArtifactStrategy.BuiltInSameRun, "Consumes an artifact built earlier in the same run.",
                [new(ValidationCategory.Api, "API tests", GateState.Gates, "Gate configured", Where), new(ValidationCategory.Integration, "Integration tests", GateState.NotGating, "Configured · does not gate deployment", Where)],
                [new(ValidationCategory.Smoke, "Smoke test", GateState.After, "Post-deploy validation configured", Where)], "Not declared in source", null, Where),
            new("d2", "pipelines/release.yml", "shop-release", "Production", "deploy", "shop-prod", SourceEnvironmentKind.Production, ["Application"], ArtifactStrategy.RebuiltForEnvironment, "Source shows shop-prod builds its own artifact.",
                [], [], "Manual approval declared", null, Where),
        ],
    };

    public DeliveryEvidenceUiTests()
    {
        var api = new Mock<IPipelineReviewApiService>();
        api.Setup(a => a.SourcesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PipelineReviewSourceList(true,
            [new(SnapshotId, "shop.zip", "fp", DateTimeOffset.Parse("2026-10-22T08:00:00Z"), 3, 2, 1, "shop"), new(Guid.NewGuid(), "docs.zip", "fp2", DateTimeOffset.Parse("2026-10-22T09:00:00Z"), 3, 0, 0, null)], SnapshotId));
        api.Setup(a => a.DeliveryAsync(It.IsAny<string>(), SnapshotId, It.IsAny<CancellationToken>())).ReturnsAsync(Evidence());
        Services.AddSingleton(api.Object);
    }

    [Fact]
    public void Quality_gates_need_an_explicit_snapshot_and_read_as_configured_never_passed()
    {
        var cut = Render<DeliveryEvidencePanel>(p => p.Add(x => x.Categories, [ValidationCategory.Api, ValidationCategory.Smoke]));
        cut.WaitForAssertion(() => cut.Find("[data-testid=delivery-snapshot]"));
        cut.Find("[data-testid=delivery-snapshot]").GetAttribute("value").Should().BeEmpty("the default/newest snapshot is never chosen for the reader");
        cut.FindAll("[data-testid=delivery-snapshot] option").Should().HaveCount(2, "only snapshots with pipelines (plus the placeholder)");
        cut.Find("[data-testid=delivery-snapshot]").Change(SnapshotId.ToString());
        var gates = cut.FindAll("[data-testid=delivery-gate]").Select(g => g.TextContent).ToList();
        gates[0].Should().Contain("shop-qa").And.Contain("API tests — Gate configured").And.Contain("After deployment: Smoke test").And.NotContain("Integration tests", "only the review's categories");
        gates[1].Should().Contain("shop-prod").And.Contain("no such check on the delivery path");
        cut.Find("[data-testid=delivery-runtime]").TextContent.Should().Contain("not executed, passed or deployed");
        cut.Markup.Should().NotContain("Passed").And.NotContain("Healthy");
    }

    [Fact]
    public void Environments_are_mapping_candidates_with_a_ladder_that_never_says_deployed()
    {
        var cut = Render<DeliveryEvidencePanel>(p => p.Add(x => x.Environments, true).Add(x => x.Targets, [("QA", "Test"), ("QA2", "Test"), ("shop-prod", "Production")]));
        cut.WaitForAssertion(() => cut.Find("[data-testid=delivery-snapshot]"));
        cut.Find("[data-testid=delivery-snapshot]").Change(SnapshotId.ToString());
        var envs = cut.FindAll("[data-testid=delivery-environment]");
        envs.Select(e => e.GetAttribute("data-mapping")).Should().Equal("Ambiguous", "StrongCandidate");
        envs[0].TextContent.Should().Contain("none is chosen");
        cut.FindAll("[data-testid=delivery-ladder]").Should().OnlyContain(l => l.TextContent.Contains("Delivery path defined") && l.TextContent.Contains("Not observed"));
    }

    [Fact]
    public void Source_analysis_delivery_summary_shows_the_same_derived_deployments()
    {
        var cut = Render<DeliverySummary>(p => p.Add(x => x.SnapshotId, SnapshotId));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sd-delivery-deployment]").Should().HaveCount(2));
        var rows = cut.FindAll("[data-testid=sd-delivery-deployment]").Select(r => r.TextContent).ToList();
        rows[0].Should().Contain("Deployment defined").And.Contain("Built in the same run").And.Contain("API tests (Gate configured)").And.Contain("Smoke test");
        rows[1].Should().Contain("Rebuilt for this environment").And.Contain("No validation on the dependency path").And.Contain("No post-deploy validation");
        cut.Find("[data-testid=sd-delivery-doc-text]").TextContent.Should().Contain("## Delivery — shop.zip").And.Contain("nothing here says a stage ran");
    }
}
