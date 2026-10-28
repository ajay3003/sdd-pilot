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
        cut.Find("[data-testid=delivery-runtime]").TextContent.Should().Contain("Configured in source · execution result not available").And.Contain("never a pass or a failure");
        cut.Markup.Should().NotContain("Passed").And.NotContain("Healthy");
    }

    [Fact]
    public void A_known_snapshot_is_offered_as_a_suggestion_and_used_only_on_explicit_acceptance()
    {
        var cut = Render<DeliveryEvidencePanel>(p => p.Add(x => x.Categories, [ValidationCategory.Performance]).Add(x => x.SuggestedSnapshotId, SnapshotId).Add(x => x.SuggestedReason, "this scenario's contract source"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=delivery-suggested]"));
        cut.Find("[data-testid=delivery-suggested]").TextContent.Should().Contain("Suggested source snapshot").And.Contain("shop.zip").And.Contain("this scenario's contract source");
        cut.Find("[data-testid=delivery-snapshot]").GetAttribute("value").Should().BeEmpty("a suggestion is never selected silently");
        cut.Find("[data-testid=delivery-use-suggested]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=delivery-suggested]").Should().BeEmpty());
        cut.FindAll("[data-testid=delivery-gate-state]").Should().BeEmpty("the accepted snapshot is now the explicit choice");
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

    // ── Target-scoped gates (Performance Test Review): a monorepo source is never presented as "every pipeline is this target's" ──────

    private static readonly Guid MonorepoId = Guid.Parse("de000000-0000-0000-0000-000000000099");

    private static DeliveryEvidence Monorepo()
    {
        (DeliveryPipeline, DeliveryDeployment) P(string id, string name, bool gate = false) =>
            (new DeliveryPipeline(id, name, $"pipelines/{id}.yml", PipelinePlatform.AzurePipelines, false, "Delivery", ["main"], ["dev"], "fp"),
             new DeliveryDeployment($"{id}-dev", id, name, "DeployDev", "deploy", "dev", SourceEnvironmentKind.Development, [name], ArtifactStrategy.BuiltInSameRun, "same run",
                 gate ? [new(ValidationCategory.Performance, "k6 smoke", GateState.Gates, "Gate configured", Where)] : [], [], "none", null, Where));
        var all = new[] { P("autorisasjon", "M2LB.Autorisasjon"), P("hendelse", "M2LB.Hendelse"), P("person", "M2LB.Person", gate: true), P("adapters", "M2LB.SkoleAdapter") };
        return new DeliveryEvidence { SnapshotId = MonorepoId, Archive = "M2LB.zip", Pipelines = [.. all.Select(x => x.Item1)], Deployments = [.. all.Select(x => x.Item2)] };
    }

    private IRenderedComponent<DeliveryEvidencePanel> Scoped(IReadOnlyList<string> confirmed, Action<IReadOnlyList<string>> onConfirm)
    {
        var api = new Mock<IPipelineReviewApiService>();
        api.Setup(a => a.SourcesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PipelineReviewSourceList(true,
            [new(MonorepoId, "M2LB.zip", "fp", DateTimeOffset.Parse("2026-10-22T08:00:00Z"), 4, 4, 0, "m2lb")], MonorepoId));
        api.Setup(a => a.DeliveryAsync(It.IsAny<string>(), MonorepoId, It.IsAny<CancellationToken>())).ReturnsAsync(Monorepo());
        Services.AddSingleton(api.Object);
        var cut = Render<DeliveryEvidencePanel>(p => p.Add(x => x.Categories, [ValidationCategory.Performance]).Add(x => x.ScopeTargetOrigin, "https://m2lbdev.bufetat.no")
            .Add(x => x.ScopeEnvironmentName, "Development").Add(x => x.ScopeEnvironmentType, "Development").Add(x => x.ConfirmedPipelineIds, confirmed)
            .Add(x => x.OnConfirmPipelines, onConfirm));
        cut.WaitForAssertion(() => cut.Find("[data-testid=delivery-snapshot]"));
        cut.Find("[data-testid=delivery-snapshot]").GetAttribute("value").Should().BeEmpty("no snapshot is chosen for the reader");
        cut.Find("[data-testid=delivery-snapshot]").Change(MonorepoId.ToString());
        return cut;
    }

    [Fact]
    public void Performance_target_gates_are_unresolved_for_a_monorepo_and_never_list_every_pipeline_as_primary()
    {
        IReadOnlyList<string>? confirmed = null;
        var cut = Scoped([], ids => confirmed = ids);
        cut.Find("[data-testid=delivery-target-scope]").GetAttribute("data-state").Should().Be("Ambiguous");
        cut.Find("[data-testid=delivery-scope-summary]").TextContent.Should().Contain("Pipeline source available · target-specific pipeline mapping unresolved");
        cut.FindAll("[data-testid=delivery-target-pipeline]").Should().BeEmpty("autorisasjon, hendelse, person and adapters are not primary gates for this target");
        cut.FindAll("[data-testid=delivery-gate]").Should().BeEmpty("the repository-wide list is not shown in target mode");
        var review = cut.Find("[data-testid=delivery-review-mapping]");
        review.ClassList.Should().Contain("btn-primary");
        review.Click();
        cut.FindAll("[data-testid=delivery-mapping-option]").Should().HaveCount(4);
        cut.FindAll("[data-testid=delivery-mapping-option][checked]").Should().BeEmpty("nothing is selected for the person");
        cut.Find("[data-testid=delivery-mapping-option][data-pipeline=person]").Change(true);
        cut.Find("[data-testid=delivery-mapping-save]").Click();
        confirmed.Should().Equal(["person"]);
    }

    [Fact]
    public void Performance_target_gates_for_a_confirmed_pipeline_read_configured_not_executed()
    {
        var cut = Scoped(["person"], _ => { });
        cut.Find("[data-testid=delivery-target-scope]").GetAttribute("data-state").Should().Be("Confirmed");
        var pipeline = cut.Find("[data-testid=delivery-target-pipeline]");
        pipeline.GetAttribute("data-pipeline").Should().Be("person");
        cut.Find("[data-testid=delivery-target-gates]").TextContent.Should().Contain("k6 smoke — Gate configured").And.Contain("Configured · not executed/observed");
        cut.Find("[data-testid=delivery-other-pipelines]").TextContent.Should().Contain("Other pipelines in this source (3)");
    }
}
