using BirkNext.Api.Data;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.Api.Services.SetupProfiles;
using BirkNext.Delivery;
using BirkNext.Integrations;
using BirkNext.PerformanceTests;
using BirkNext.PipelineReview;
using BirkNext.SourceDomains;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// Performance Test Review remediation: pipeline gates are scoped to the target under test (an unresolved mapping is unresolved, never "every
/// pipeline"); performance setup profiles describe desired state only (scenario, workload, thresholds, target reference) and reject readiness,
/// network/TLS, run, baseline and execution evidence; the shared Guided Setup model derives the Performance steps from readiness.
/// </summary>
public sealed class PerformanceSetupAndDeliveryScopeTests
{
    // ── Target-scoped pipeline gates ───────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly DeliveryProvenance Prov = new("pipelines/x.yml", 1, "x", null, null, null, "Azure Pipelines");

    private static (DeliveryPipeline Pipeline, DeliveryDeployment Deployment, DeliveryTestDefinition? Test) Pipeline(string id, string name, string deploys, bool performanceGate = false) =>
        (new DeliveryPipeline(id, name, $"pipelines/{id}.yml", PipelinePlatform.AzurePipelines, false, "Delivery", ["main"], ["dev", "prod"], "fp-" + id),
         new DeliveryDeployment($"{id}-dev", id, name, "DeployDev", "deploy", "dev", SourceEnvironmentKind.Development, [deploys], ArtifactStrategy.BuiltInSameRun, "same run",
             performanceGate ? [new DeliveryGate(ValidationCategory.Performance, "k6 smoke", GateState.Gates, DeliveryEvidenceProjection.GateText(GateState.Gates), Prov)] : [], [], "none", null, Prov),
         performanceGate ? new DeliveryTestDefinition("k6 smoke", ValidationCategory.Performance, id, "Test", "k6", [], [$"{id}-dev"], false, false, null, "Gates DeployDev") : null);

    /// <summary>A monorepo snapshot like M2LB.zip: many component pipelines sharing the product name.</summary>
    private static DeliveryEvidence Monorepo(params (DeliveryPipeline Pipeline, DeliveryDeployment Deployment, DeliveryTestDefinition? Test)[] extra)
    {
        var all = new[]
        {
            Pipeline("autorisasjon", "M2LB.Autorisasjon", "M2LB.Autorisasjon.Api"),
            Pipeline("hendelse", "M2LB.Hendelse", "M2LB.Hendelse.Worker"),
            Pipeline("person", "M2LB.Person", "M2LB.Person.Api", performanceGate: true),
            Pipeline("adapters", "M2LB.SkoleAdapter", "M2LB.SkoleAdapter.Worker"),
        }.Concat(extra).ToList();
        return new DeliveryEvidence
        {
            Archive = "M2LB.zip", Pipelines = [.. all.Select(p => p.Pipeline)], Deployments = [.. all.Select(p => p.Deployment)], Tests = [.. all.Select(p => p.Test).OfType<DeliveryTestDefinition>()],
        };
    }

    [Fact]
    public void PipelineGates_ForAMonorepoTarget_AreUnresolved_NeverEveryPipeline()
    {
        var scope = DeliveryTargetScope.Resolve(Monorepo(), "https://m2lbdev.bufetat.no", "Development", "Development", [], ValidationCategory.Performance);
        scope.State.Should().Be(DeliveryTargetMappingState.Ambiguous);
        scope.Relevant.Should().BeEmpty("unrelated autorisasjon/hendelse/person/adapter pipelines are never primary evidence for another target");
        scope.Summary.Should().StartWith("Pipeline source available · target-specific pipeline mapping unresolved");
        scope.Candidates.Select(p => p.PipelineId).Should().BeEquivalentTo(["autorisasjon", "hendelse", "person", "adapters"]);
        scope.Execution.Should().Be("Configured · not executed/observed");
    }

    [Fact]
    public void PipelineGates_AConfirmedMapping_ShowsOnlyThatPipeline_ConfiguredNeverPassed()
    {
        var scope = DeliveryTargetScope.Resolve(Monorepo(), "https://m2lbdev.bufetat.no", "Development", "Development", ["person"], ValidationCategory.Performance);
        scope.State.Should().Be(DeliveryTargetMappingState.Confirmed);
        scope.Relevant.Should().ContainSingle(p => p.PipelineId == "person");
        scope.Other.Select(p => p.PipelineId).Should().BeEquivalentTo(["autorisasjon", "hendelse", "adapters"], "other pipelines stay available, secondary");
        var summary = DeliveryTargetScope.GateSummary(scope.Relevant[0], scope.Execution);
        summary.Should().Contain("1 test step configured").And.Contain("Gate configured").And.EndWith("Configured · not executed/observed").And.NotContain("passed");
    }

    [Fact]
    public void PipelineGates_APipelineThatNamesTheHost_IsAStrongCandidate_AComponentMatchACandidate()
    {
        var web = Pipeline("web", "M2LB.Web", "m2lbdev.bufetat.no");
        DeliveryTargetScope.Resolve(Monorepo(web), "https://m2lbdev.bufetat.no", "Development", "Development", [], ValidationCategory.Performance)
            .Should().Match<DeliveryTargetScopeResult>(s => s.State == DeliveryTargetMappingState.StrongCandidate && s.Relevant.Count == 1 && s.Relevant[0].PipelineId == "web");
        var payments = new DeliveryEvidence { Pipelines = [Pipeline("pay", "PaymentHub.Api", "PaymentHub.Api").Pipeline, Pipeline("orders", "Orders.Api", "Orders.Api").Pipeline] };
        DeliveryTargetScope.Resolve(payments, "https://paymenthub-dev.example.test", "Development", "Development", [], ValidationCategory.Performance)
            .Should().Match<DeliveryTargetScopeResult>(s => s.State == DeliveryTargetMappingState.Candidate && s.Relevant.Single().PipelineId == "pay");
        DeliveryTargetScope.Resolve(new DeliveryEvidence(), "https://x.example.test", "Development", "Development", [], ValidationCategory.Performance).State.Should().Be(DeliveryTargetMappingState.NoPipelines);
    }

    [Fact]
    public void TargetTokens_StripEnvironmentWords_NotComponentNames()
    {
        DeliveryTargetScope.TargetTokens("https://m2lbdev.bufetat.no").Should().Equal("m2lb");
        DeliveryTargetScope.TargetTokens("https://paymenthub-qa.example.test").Should().Equal("paymenthub");
        DeliveryTargetScope.TargetTokens("https://forecast.example.test").Should().BeEquivalentTo(new[] { "forecast" }, "short environment words are never stripped from the end of a name");
    }

    // ── Performance setup profiles ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly PerformanceSetupTarget Target = new("https://m2lb-dev.example.test", "Development", "M2LB Development");

    private sealed class Harness : IAsyncDisposable
    {
        public ServiceProvider Services { get; }
        public Harness()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var database = Guid.NewGuid().ToString();
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(database));
            services.AddSingleton(new PerformanceTestOptions());
            services.AddScoped<PerformanceTestStore>();
            services.AddScoped<PerformanceSetupProfileService>();
            Services = services.BuildServiceProvider();
        }
        public PerformanceSetupProfileService Profiles() => Services.CreateScope().ServiceProvider.GetRequiredService<PerformanceSetupProfileService>();
        public PerformanceTestStore Store() => Services.CreateScope().ServiceProvider.GetRequiredService<PerformanceTestStore>();
        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private const string Profile = """
        version: 1
        profile:
          name: "Performance baseline"
          testPack: "performance-test-review"
          environment: "M2LB Development"
        performance:
          targetRef: "m2lb-dev"
          definition:
            name: "Baseline check"
          scenario:
            type: "rest-http"
            steps:
              - name: "home"
                method: "GET"
                path: "/"
                expectedStatusCodes: [200]
                thinkTimeMs: 500
          workload:
            purpose: "baseline"
            mode: "virtual-users"
            virtualUsers: 5
            warmUpSeconds: 30
            rampUpSeconds: 30
            steadyStateSeconds: 120
            rampDownSeconds: 15
          thresholds:
            - metric: "p95Latency"
              operator: "<="
              value: 500
              unit: "ms"
              severity: "required"
            - metric: "errorRate"
              operator: "<="
              value: 1
              unit: "percent"
              severity: "required"
          localExecution:
            caBundleRequired: true
        """;

    [Fact]
    public async Task PerformanceProfile_PreviewIsReadOnly_ApplySavesV1_AndNothingRuns()
    {
        await using var h = new Harness();
        var preview = await h.Profiles().PreviewAsync("m2lb-dev", new SetupProfileRequest("perf.yaml", Profile), Target, default);
        preview.Valid.Should().BeTrue(string.Join(" ", preview.Issues.Select(i => i.Message)));
        var change = preview.Changes.Single(c => c.Id == "performance.definition");
        change.Should().Match<SetupChange>(c => c.Classification == SetupChangeClassification.New && c.Preselected && c.SafeToApply);
        change.Proposed.Should().Contain("1 step(s)").And.Contain("2 threshold(s)");
        preview.Changes.Single(c => c.Id == "performance.targetRef").Classification.Should().Be(SetupChangeClassification.ReferenceOnly);
        preview.ManualRequirements.Should().Contain(m => m.Contains("PerformanceTests:Container:CaBundlePath"), "the profile may say a CA bundle is required; never provide it");
        (await h.Store().DefinitionsAsync("m2lb-dev")).Should().BeEmpty("preview never mutates");

        var applied = await h.Profiles().ApplyAsync("m2lb-dev", new SetupApplyRequest("perf.yaml", Profile, ["performance.definition"]), Target, default);
        applied.Items.Single().Outcome.Should().Be(SetupApplyOutcome.Applied);
        var saved = (await h.Store().DefinitionsAsync("m2lb-dev")).Single();
        saved.Should().Match<PerformanceTestDefinition>(d => d.Version == 1 && d.TargetOrigin == Target.TargetOrigin && d.EnvironmentType == "Development" && d.Workload.VirtualUsers == 5
            && d.Workload.SteadyStateSeconds == 120 && d.Thresholds.Count == 2 && d.Thresholds.All(t => t.Operator == ThresholdOperator.LessThanOrEqual));
        (await h.Store().RunsAsync("m2lb-dev", null, 10)).Should().BeEmpty("applying a profile never runs anything");
    }

    [Fact]
    public async Task PerformanceProfile_ExportReimportsAsSame_AndExcludesEvidence()
    {
        await using var h = new Harness();
        await h.Profiles().ApplyAsync("m2lb-dev", new SetupApplyRequest("perf.yaml", Profile, ["performance.definition"]), Target, default);
        var export = (await h.Profiles().ExportAsync("m2lb-dev", null, SetupProfileFormat.Yaml, default))!;
        export.Content.Should().Contain("performance:").And.Contain("p95Latency").And.Contain("expectedStatusCodes: [200]");
        export.Content.Should().NotContainAny("readiness", "reachab", "passed", "promoted", "BEGIN CERTIFICATE", "approved:", "https://m2lb-dev.example.test");
        export.Excluded.Should().Contain(e => e.Contains("CA certificate content"));
        var again = await h.Profiles().PreviewAsync("m2lb-dev", new SetupProfileRequest(export.FileName, export.Content), Target, default);
        again.Valid.Should().BeTrue(string.Join(" ", again.Issues.Select(i => i.Message)));
        again.Changes.Single(c => c.Id == "performance.definition").Classification.Should().Be(SetupChangeClassification.Same);
    }

    [Theory]
    [InlineData("readinessPassed: true")]
    [InlineData("tlsTrusted: true")]
    [InlineData("networkReady: true")]
    [InlineData("performancePassed: true")]
    [InlineData("baselinePromoted: true")]
    [InlineData("caBundlePath: \"C:/certs/corp.pem\"")]
    [InlineData("executionApproved: true")]
    public async Task PerformanceProfile_EvidenceAndTrustState_IsRejected_NeverImported(string line)
    {
        await using var h = new Harness();
        var content = Profile.Replace("  targetRef: \"m2lb-dev\"", $"  targetRef: \"m2lb-dev\"\n  {line}");
        var preview = await h.Profiles().PreviewAsync("m2lb-dev", new SetupProfileRequest("perf.yaml", content), Target, default);
        var path = "performance." + line[..line.IndexOf(':')];
        preview.Issues.Should().Contain(i => i.Severity == SetupIssueSeverity.Rejected && i.Path == path);
        preview.Changes.Should().NotContain(c => c.Proposed != null && c.Proposed.Contains("corp.pem"));
    }

    [Fact]
    public async Task PerformanceProfile_UpdatingASavedDefinition_NeedsConfirmation_AndProductionRefusesEverything()
    {
        await using var h = new Harness();
        await h.Profiles().ApplyAsync("m2lb-dev", new SetupApplyRequest("perf.yaml", Profile, ["performance.definition"]), Target, default);
        var changed = Profile.Replace("virtualUsers: 5", "virtualUsers: 8");
        var preview = await h.Profiles().PreviewAsync("m2lb-dev", new SetupProfileRequest("perf.yaml", changed), Target, default);
        preview.Changes.Single(c => c.Id == "performance.definition").Should().Match<SetupChange>(c => c.Classification == SetupChangeClassification.RequiresConfirmation && !c.Preselected
            && c.Reason.Contains("creates v2"));
        var refused = await h.Profiles().ApplyAsync("m2lb-dev", new SetupApplyRequest("perf.yaml", changed, ["performance.definition"]), Target, default);
        refused.Items.Single().Outcome.Should().Be(SetupApplyOutcome.Refused);
        var confirmed = await h.Profiles().ApplyAsync("m2lb-dev", new SetupApplyRequest("perf.yaml", changed, ["performance.definition"], ["performance.definition"]), Target, default);
        confirmed.Items.Single().Outcome.Should().Be(SetupApplyOutcome.Applied);
        (await h.Store().DefinitionsAsync("m2lb-dev")).Single().Version.Should().Be(2, "earlier runs keep v1");

        var production = await h.Profiles().PreviewAsync("prod", new SetupProfileRequest("perf.yaml", Profile), Target with { EnvironmentType = "Production" }, default);
        production.Changes.Single(c => c.Id == "performance.definition").Classification.Should().Be(SetupChangeClassification.CannotImport);
    }

    // ── Shared Guided Setup over readiness ─────────────────────────────────────────────────────────────────────────────────────────

    private static PerformanceTestDefinition Definition() => PerformanceTestReviewTests.Definition(d => d with { Version = 1, Scenario = d.Scenario with { TestDataProfileId = null } });

    [Fact]
    public void GuidedSetup_NewDraft_StartsAtTheDefinitionStep_AndNothingIsRuntimeVerified()
    {
        var setup = PerformanceSetupModel.Build(Definition(), isNew: true, dirty: false, draftValid: true, readiness: null);
        setup.PackId.Should().Be(PerformanceSetupModel.PackId);
        setup.NextStep.Should().Be(JourneySetupStepKind.Target);
        setup.Steps.Single(s => s.Kind == JourneySetupStepKind.Scenario).Blocker.Should().Contain("saved automatically as v1");
        setup.Steps.Should().OnlyContain(s => !s.Facts.RuntimeVerified);
        setup.SetupComplete.Should().BeFalse();
        setup.Execution.Should().Be("Blocked");
    }

    [Fact]
    public void GuidedSetup_OneCaBlocker_IsTheNextStep_AndReadyMeansConfirmationNotPass()
    {
        PerformanceReadinessItem Ready(string key, string category) => new(key, key, PerformanceReadinessState.Ready, "ok", false) { Category = category };
        var items = new List<PerformanceReadinessItem>
        {
            Ready("target", "Definition"), Ready("environment", "Safety"), Ready("runtime", "Execution setup"), Ready("image", "Execution setup"), Ready("provider", "Execution setup"),
            new("network", "Container network", PerformanceReadinessState.Optional, "TLS", false) { Category = "Reachability", Action = PerformanceReadinessActions.CheckNetwork },
            new("ca-trust", "Corporate CA trust", PerformanceReadinessState.NeedsConfiguration, "untrusted", true)
                { Category = "Execution setup", Status = "Missing", Headline = "Container TLS trust is not configured", Owner = "Local execution configuration" },
            Ready("tls", "Safety"), Ready("scenario", "Definition"), Ready("authentication", "Definition"), Ready("workload", "Definition"), Ready("limits", "Safety"), Ready("thresholds", "Definition"),
            new("baseline", "Baseline", PerformanceReadinessState.Optional, "none", false) { Category = "Optional" },
        };
        var blocked = PerformanceSetupModel.Build(Definition(), false, false, true, new PerformanceTestReadiness { Items = items, Ready = false });
        blocked.NextStep.Should().Be(JourneySetupStepKind.CaTrust);
        blocked.NextAction.Should().StartWith("Corporate CA trust: Container TLS trust is not configured");
        blocked.Steps.Single(s => s.Kind == JourneySetupStepKind.CaTrust).ManualOnly.Should().BeTrue("a CA bundle is local configuration a person provides");
        blocked.Steps.Single(s => s.Kind == JourneySetupStepKind.Baseline).Required.Should().BeFalse();
        (blocked.RequiredReady, blocked.RequiredSteps).Should().Be((blocked.RequiredSteps - 2, blocked.RequiredSteps), "CA trust and Ready to run are open");

        items[6] = items[6] with { State = PerformanceReadinessState.Ready, Blocking = false, Status = "Ready" };
        items[5] = items[5] with { State = PerformanceReadinessState.Ready };
        var ready = PerformanceSetupModel.Build(Definition(), false, false, true, new PerformanceTestReadiness { Items = items, Ready = true });
        ready.SetupComplete.Should().BeTrue();
        ready.Execution.Should().Be("Ready to run after explicit confirmation (not a pass)");
        ready.Steps.Single(s => s.Kind == JourneySetupStepKind.NetworkReachability).Facts.RuntimeVerified.Should().BeTrue("the container check ran from the load generator");
    }
}
