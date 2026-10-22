using BirkNext.Api.Data;
using BirkNext.Api.Services;
using BirkNext.Api.Services.PipelineReview;
using BirkNext.Delivery;
using BirkNext.Integrations;
using BirkNext.PipelineReview;
using BirkNext.SourceDomains;
using BirkNext.SourceImpact;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BirkNext.Api.Tests.Services.PipelineReview;

/// <summary>
/// Shared Pipeline &amp; Delivery Evidence over the generic Contoso.Shop fixture: one parser (stored CI/CD evidence), one derivation (gates,
/// lineage), normalized delivery evidence every consumer reads. Defined ≠ executed, configured test ≠ gating test ≠ passed test, artifact
/// definition ≠ published artifact, pipeline label ≠ Target Environment until mapped; runtime metadata optional and separate.
/// </summary>
public sealed class DeliveryEvidenceArchitectureTests
{
    private static readonly IqrSourceSnapshot Shop = PipelineReviewFixtures.Snapshot(PipelineReviewFixtures.Shop());

    [Fact]
    public void Providers_keep_source_derivation_and_optional_runtime_metadata_apart()
    {
        DeliveryEvidenceProviders.All.Where(p => p.Kind == "Source").Should().OnlyContain(p => !p.RequiresNetwork, "source-only review works offline");
        var metadata = DeliveryEvidenceProviders.Get("azure-devops-metadata");
        (metadata.Support, metadata.RequiresNetwork).Should().Be((DeliveryCapabilitySupport.Optional, true));
        metadata.CannotProve.Should().Contain("Run history").And.Contain("source-only review works fully without it");
        DeliveryEvidenceProviders.Get("delivery-derivation").Proves.Should().Contain("never from ordering");
        new DeliveryDerivationProvider().Descriptor.Key.Should().Be("delivery-derivation");
        new SourcePipelineEvidenceProvider(PipelinePlatform.GitHubActions).Descriptor.Support.Should().Be(DeliveryCapabilitySupport.Partial);
    }

    [Fact]
    public void Delivery_evidence_is_declared_deployments_gates_lineage_and_templates_never_execution()
    {
        var e = DeliveryDerivationProvider.For(Shop);
        e.Pipelines.Should().Contain(p => p.IsTemplate).And.Contain(p => !p.IsTemplate, "templates are separate from runnable pipelines");
        e.Pipelines.Where(p => !p.IsTemplate).Should().OnlyContain(p => p.DefinitionFingerprint.Length == 16);
        e.Deployments.Should().NotBeEmpty().And.OnlyContain(d => d.State == "Deployment defined");
        e.Tests.Should().NotBeEmpty().And.OnlyContain(t => t.Execution.StartsWith("Configured — not executed"));
        e.Runtime.Should().Be(DeliveryRuntimeState.NoRuntimeMetadata);
        e.RuntimeText.Should().Contain("not read");

        var qa = e.Deployments.Single(d => d.EnvironmentLabel == "shop-qa");
        qa.Before.Should().NotContain(g => g.Category == ValidationCategory.Integration && g.State == GateState.Gates, "QA does not depend on the integration tests");
        e.Tests.Single(t => t.Category == ValidationCategory.Integration).Gating.Should().NotContain("shop-qa");
        e.Deployments.Single(d => d.EnvironmentLabel == "shop-prod").Artifacts.Should().Be(ArtifactStrategy.RebuiltForEnvironment, "production builds its own artifact in source");
        e.Deployments.Should().OnlyContain(d => d.Provenance.File.Length > 0 && d.Provenance.Line > 0);
        DeliveryDocumentation.Markdown(e).Should().Contain("shop-prod").And.Contain("Rebuilt").And.NotContain("$(").And.NotContain("password");
    }

    [Fact]
    public void Pipeline_labels_map_to_target_environments_only_by_confirmation_and_the_ladder_never_collapses()
    {
        var targets = new[] { ("QA", "Test"), ("QA hotfix", "Test"), ("shop-prod", "Production") };
        DeliveryEvidenceQueries.Map("shop-qa", SourceEnvironmentKind.QA, targets).State.Should().Be(EnvironmentMappingState.Ambiguous, "a 'qa' label never maps itself when several environments could be QA");
        DeliveryEvidenceQueries.Map("shop-qa", SourceEnvironmentKind.QA, [("QA", "Test")]).State.Should().Be(EnvironmentMappingState.NeedsConfirmation);
        DeliveryEvidenceQueries.Map("shop-prod", SourceEnvironmentKind.Production, targets).State.Should().Be(EnvironmentMappingState.StrongCandidate);
        DeliveryEvidenceQueries.Map("shop-prod", SourceEnvironmentKind.Production, targets, new Dictionary<string, string> { ["shop-prod"] = "shop-prod" }).State.Should().Be(EnvironmentMappingState.Mapped);
        DeliveryEvidenceQueries.Map("sandbox", SourceEnvironmentKind.Custom, targets).State.Should().Be(EnvironmentMappingState.Unmapped);

        var ladder = DeliveryEvidenceQueries.Ladder("shop-qa", true, true, DeliveryDerivationProvider.For(Shop));
        ladder.DeliveryPath.Should().StartWith("Delivery path defined");
        ladder.DeploymentObserved.Should().StartWith("Not observed");
        (ladder.RuntimeObserved, ladder.Validated).Should().Be(("Not assessed", "Not assessed"));
    }

    [Fact]
    public void A_run_of_an_earlier_definition_never_describes_the_current_yaml()
    {
        var current = DeliveryDerivationProvider.For(Shop).Pipelines.First(p => !p.IsTemplate);
        DeliveryEvidenceQueries.RunApplies(current.DefinitionFingerprint, current).Applies.Should().BeTrue();
        DeliveryEvidenceQueries.RunApplies("0000000000000000", current).Text.Should().Contain("earlier pipeline definition");
        DeliveryEvidenceQueries.RunApplies(null, current).Applies.Should().BeFalse();
    }

    [Fact]
    public void Declared_pipeline_changes_are_classified_and_reach_the_delivery_path_in_impact_analysis()
    {
        var before = Shop.EvidenceDomains!.CiCd.Pipelines.Single(p => p.File.EndsWith("shop-build.yml"));
        var weakened = before with
        {
            Steps = [.. before.Steps.Where(s => s.Kind != PipelineStepKind.IntegrationTest).Select(s => s.Kind == PipelineStepKind.UnitTest ? s with { ContinueOnError = true } : s)],
            Environments = [.. before.Environments, "shop-staging"],
        };
        var changes = DeliveryEvidenceQueries.Changes(before, weakened);
        changes.Should().Contain(c => c.Kind == "Validation step removed" && c.Classification.StartsWith("Potential gate removed"));
        changes.Should().Contain(c => c.Kind == "Deployment target added" && c.Detail == "shop-staging");
        DeliveryEvidenceQueries.Changes(before, before).Should().BeEmpty();
        DeliveryTraceability.For(DeliveryDerivationProvider.For(Shop), "Tests must pass before deployment to production").Should()
            .ContainSingle(t => t.Control == "Tests gate deployment" && t.Execution == "Not assessed");
    }

    [Fact]
    public async Task Impact_analysis_maps_a_pipeline_change_to_the_environments_it_deploys_to_without_claiming_a_deployment()
    {
        var changedYaml = PipelineReviewFixtures.Shop().Single(f => f.Path.EndsWith("shop-build.yml")).Content.Replace("continueOnError: true", "continueOnError: false");
        var after = PipelineReviewFixtures.Snapshot(PipelineReviewFixtures.Shop(("shop/pipelines/shop-build.yml", changedYaml.Replace("dotnet test tests/Shop.IntegrationTests/Shop.IntegrationTests.csproj", "echo skipped"))))
            with { Id = Guid.NewGuid(), Archive = new SourceArchive("shop.zip", new string('e', 64), 1) };
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var report = await new SourceChangeImpactService(db, new Snapshots([Shop, after])).AnalyzeAsync(new SourceChangeImpactRequest("dev", "generic", Shop.Id, after.Id), default);
        report!.TechnicalImpacts.Should().Contain(i => i.EntityType == "Environment delivery path" && i.Level == TechnicalImpactLevel.Potential
            && i.Reason.StartsWith("Potential delivery impact") && i.Reason.Contains("no deployment or test result is implied"));
    }

    [Fact]
    public async Task The_service_reads_one_exact_snapshot_and_never_substitutes_another()
    {
        var reviews = new PipelineReviewService(new Snapshots([Shop]), new NoMetadata(), new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var service = new DeliveryEvidenceService(reviews, new Snapshots([Shop]));
        (await service.GetAsync("dev", Guid.NewGuid(), default)).Should().BeNull();
        (await service.GetAsync("dev", Shop.Id, default))!.SnapshotId.Should().Be(Shop.Id);
        DeliveryEvidenceQueries.TestsFor(DeliveryDerivationProvider.For(Shop), "Shop.IntegrationTests").Should().NotBeEmpty();
    }

    private sealed class Snapshots(IReadOnlyList<IqrSourceSnapshot> all) : BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider
    {
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(all);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult(all.FirstOrDefault(s => s.Id == snapshotId));
    }

    private sealed class NoMetadata : IPipelineMetadataSource
    {
        public Task<PipelineMetadataSummary> GetAsync(CancellationToken ct) => Task.FromResult(new PipelineMetadataSummary());
    }
}
