using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Configuration;
using BirkNext.Api.Services.PipelineReview;
using BirkNext.PipelineReview;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static BirkNext.Api.Tests.Services.PipelineReview.PipelineReviewFixtures;

namespace BirkNext.Api.Tests.Services.PipelineReview;

/// <summary>
/// Pipeline Review over Source Analysis CI/CD evidence (generic Contoso.Shop fixture plus focused single-pipeline cases): sequence from dependsOn
/// (not YAML order), parallelism, conditions, test presence ≠ test gating, soft gates, post-deployment validation, artifact lineage and promotion,
/// environment progression, cross-pipeline relationships, infrastructure/application order, template composition, unresolved-template suppression,
/// trigger/path coverage (shared library, contract consumer), the path probe, source changes, Azure DevOps metadata, and redaction.
/// </summary>
public sealed class PipelineReviewTests
{
    private static readonly Lazy<PipelineReviewResult> ShopReview = new(() => PipelineReviewBuilder.Build(Snapshot(Shop())));

    private static PipelineReviewResult Review(params (string Path, string Content)[] pipelineFiles) =>
        PipelineReviewBuilder.Build(Snapshot([("repo/App.sln", ""), .. pipelineFiles.Select(f => ($"repo/{f.Path}", f.Content))]));

    private static PipelineReviewFinding? Finding(PipelineReviewResult r, PipelineFindingCategory category, string titleContains) =>
        r.Findings.FirstOrDefault(f => f.Category == category && f.Title.Contains(titleContains, StringComparison.Ordinal));

    private static DeploymentReview Deployment(PipelineReviewResult r, string environment) => r.Deployments.Single(d => d.Environment == environment);

    // ── Evidence (Source Analysis owns parsing) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Source_Analysis_CI_CD_evidence_v2_carries_the_structure_the_review_needs()
    {
        var cicd = Snapshot(Shop()).EvidenceDomains!.CiCd;
        cicd.AnalyzerVersion.Should().Be(2);
        var build = cicd.Pipelines.Single(p => p.Name == "shop-build");
        build.Stages.Single(s => s.Name == "DeployQA").Should().Match<PipelineStage>(s => s.DependsOnDeclared && s.DependsOn.SequenceEqual(new[] { "DeployDev", "InfraQA" }) && s.Order == 4);
        build.JobDetails.Single(j => j.Name == "DependencyScan").Condition.Should().Be("eq(variables['Build.Reason'], 'Schedule')");
        build.Steps.Single(s => s.Kind == PipelineStepKind.DependencyScan).ContinueOnError.Should().BeTrue();
        build.Steps.Single(s => s.Kind == PipelineStepKind.Publish && s.Tool == "publish").ArtifactsPublished.Should().Equal("app");
        build.Resources.Should().ContainSingle(r => r.Kind == "repository" && r.Alias == "platform" && r.Source == "Platform/pipeline-templates");
        build.TemplateUses.Should().Contain(u => u.Level == "jobs" && u.Stage == "DeployQA" && u.Parameters["environment"] == "shop-qa" && u.ResolvedPath == "shop/pipelines/templates/deploy-webapp.yml");
        build.TemplateUses.Should().Contain(u => u.RepositoryAlias == "platform" && !u.Resolved);
        var release = cicd.Pipelines.Single(p => p.Name == "shop-release");
        release.Resources.Should().ContainSingle(r => r.Kind == "pipeline" && r.Alias == "shopBuild" && r.Source == "shop-build" && !r.TriggerDeclared);
        release.Steps.Should().Contain(s => s.Kind == PipelineStepKind.ArtifactDownload && s.ArtifactsConsumed.Single() == new PipelineArtifactUse("app", "current") && s.StrategyPhase == "deploy");
        release.JobDetails.Single(j => j.Deployment).Should().Match<PipelineJob>(j => j.Deployment && j.Environment == "shop-prod" && j.Strategy == "runOnce");
        cicd.Pipelines.Single(p => p.Name == "deploy-webapp").Parameters.Should().Equal("environment");
    }

    [Fact]
    public void The_review_never_parses_YAML_or_reads_source_files()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../BirkNext.Api/Services/PipelineReview"));
        foreach (var file in Directory.GetFiles(dir, "*.cs"))
            Regex.IsMatch(File.ReadAllText(file), @"\bMiniYaml\b|\bYamlNode\b|\bHclReader\b|ZipArchive|IqrSourceArchiveReader|File\.Read|\bEvidenceFile\b")
                .Should().BeFalse($"{Path.GetFileName(file)} must consume normalized Source Analysis evidence only");
    }

    [Fact]
    public void A_snapshot_from_before_CI_CD_v2_asks_for_reanalysis_and_one_without_pipelines_says_so()
    {
        var snapshot = Snapshot(Shop());
        var old = snapshot with { EvidenceDomains = snapshot.EvidenceDomains! with { CiCd = snapshot.EvidenceDomains.CiCd with { AnalyzerVersion = 1 } } };
        PipelineReviewBuilder.Build(old).Should().Match<PipelineReviewResult>(r => r.State == "NeedsReanalysis" && r.Findings.Count == 0);
        PipelineReviewBuilder.Build(Snapshot(("repo/App.sln", ""), ("repo/src/a.cs", "class A {}"))).State.Should().Be("NoPipelines");
        PipelineReviewBuilder.Build(snapshot with { EvidenceDomains = null }).State.Should().Be("NeedsReanalysis");
    }

    // ── Flow ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_delivery_story_is_deterministic_and_evidence_backed()
    {
        var first = PipelineReviewBuilder.Build(Snapshot(Shop()));
        var second = PipelineReviewBuilder.Build(Snapshot(Shop()));
        JsonSerializer.Serialize(first).Should().Be(JsonSerializer.Serialize(second));
        first.Story.Select(s => s.Text).Should().Equal(
            "Pull requests to `main` (only changes under `src/Shop.Api`, `tests`) run pr-validation (`shop/pipelines/pr-validation.yml`).",
            "Stage `Validate` builds (`dotnet build`) and runs unit tests (`dotnet test`, from `shop/pipelines/templates/dotnet-tests.yml`).",
            "Pushes to `main` (only changes under `src`, `infra`) start shop-build (`shop/pipelines/shop-build.yml`).",
            "Stage `Build` builds (`dotnet build`), scans dependencies (`dependency scan`) only when `eq(variables['Build.Reason'], 'Schedule')` (continueOnError), runs unit tests (`dotnet test`, from `shop/pipelines/templates/dotnet-tests.yml`) and packages the application (`dotnet publish/pack`); it publishes artifact `app`.",
            "Stage `IntegrationTests` (after `DeployDev`) runs integration tests (`dotnet test`) (continueOnError).",
            "Stage `Compliance` (after `Build`) runs steps from template `compliance/checks.yml@platform`, which is not analyzed.",
            "Stage `Deploy DEV` deploys application to DEV (`shop-dev`) after `Build`. Gated by: Unit tests.",
            "It deploys artifact `app` from the current run of `shop-build` (stage `Build`).",
            "After deploying to DEV: Integration tests.",
            "Stage `Infrastructure QA` deploys infrastructure to `QA` after `Build`. Gated by: Unit tests.",
            "Stage `Deploy QA` deploys application to QA (`shop-qa`) after `DeployDev`, `InfraQA`. Gated by: Unit tests. Integration tests run in this pipeline but are not on its path. Infrastructure for QA (`InfraQA`) is deployed before it.",
            "It deploys artifact `app` from the current run of `shop-build` (stage `Build`).",
            "No post-deployment validation was detected after the QA deployment.",
            "Pushes to `main` (only changes under `src/Shop.Worker`) start worker-build (`shop/pipelines/worker-build.yml`).",
            "It builds (`dotnet build`) and runs unit tests (`dotnet test`).",
            "shop-release has no automatic trigger; it appears to require manual start.",
            "Stage `BuildProd` packages the application (`dotnet publish/pack`); it publishes artifact `app`.",
            "Stage `Production` deploys application to PROD (`shop-prod`) after `BuildProd`. No test was detected on its dependency path.",
            "It deploys artifact `app` from the current run of `shop-release` (stage `BuildProd`).",
            "No post-deployment validation was detected after the PROD deployment.",
            "Production is handled by a separate pipeline, shop-release.",
            "shop-release declares shop-build as a pipeline resource, but without a trigger and without downloading its artifacts: which run it follows is chosen when it is started.",
            "Production and QA appear to use different builds: The artifact tested in QA may not be the same artifact deployed to Production.");
        first.Story.Should().OnlyContain(s => s.Evidence.Count > 0 && s.Evidence.All(e => e.File.Length > 0));
        first.Story.Single(s => s.Text.StartsWith("shop-release declares", StringComparison.Ordinal)).State.Should().Be(ArchitectureEvidenceState.Inferred);
        first.DeliveryPath.Should().Equal("pr-validation", "shop-build", "worker-build", "DEV", "QA", "⋯", "PROD");
    }

    private const string Unordered = """
        trigger:
          - main
        pool:
          vmImage: ubuntu-latest
        stages:
          - stage: Deploy
            dependsOn: Test
            jobs:
              - deployment: Web
                environment: app-qa
                strategy:
                  runOnce:
                    deploy:
                      steps:
                        - script: az webapp deploy --name app-qa --src-path app.zip
          - stage: Build
            dependsOn: []
            jobs:
              - job: Build
                steps:
                  - script: dotnet build
          - stage: Test
            jobs:
              - job: Unit
                steps:
                  - script: dotnet test tests/App.UnitTests/App.UnitTests.csproj
        """;

    [Fact]
    public void Sequence_comes_from_dependsOn_and_the_implicit_previous_stage_not_from_YAML_order()
    {
        var r = Review(("pipelines/ci.yml", Unordered));
        var qa = Deployment(r, "app-qa");
        qa.Before.Should().Contain(g => g.Category == ValidationCategory.Unit && g.State == GateState.Gates, "Test runs after Build (no dependsOn = previous stage), Deploy after Test");
        qa.Before.Should().Contain(g => g.Category == ValidationCategory.Build);
        r.Edges.Should().Contain(e => e.FromId.EndsWith("|Build") && e.ToId.EndsWith("|Test") && e.State == ArchitectureEvidenceState.StronglySupported && e.Basis.Contains("previous stage"));
        r.Edges.Should().Contain(e => e.FromId.EndsWith("|Test") && e.ToId.EndsWith("|Deploy") && e.State == ArchitectureEvidenceState.Confirmed);
    }

    private const string ParallelDeploy = """
        trigger:
          - main
        pool:
          vmImage: ubuntu-latest
        stages:
          - stage: Test
            jobs:
              - job: Unit
                steps:
                  - script: dotnet test tests/App.UnitTests/App.UnitTests.csproj
          - stage: Lint
            dependsOn: []
            jobs:
              - job: Lint
                steps:
                  - script: dotnet format --verify-no-changes
          - stage: DeployQA
            dependsOn: []
            jobs:
              - deployment: Web
                environment: app-qa
                strategy:
                  runOnce:
                    deploy:
                      steps:
                        - script: az webapp deploy --name app-qa --src-path app.zip
          - stage: DeployProd
            dependsOn: Test
            jobs:
              - deployment: Web
                environment: app-prod
                strategy:
                  runOnce:
                    deploy:
                      steps:
                        - script: az webapp deploy --name app-prod --src-path app.zip
        """;

    [Fact]
    public void Parallel_stages_are_explained_and_tests_that_exist_but_do_not_gate_are_reported_separately_from_no_tests()
    {
        var r = Review(("pipelines/ci.yml", ParallelDeploy));
        r.Story.Should().Contain(s => s.Text == "Stages `Test` and `Lint` do not depend on each other and can run in parallel." || s.Text.Contains("can run in parallel"));
        var notGating = Finding(r, PipelineFindingCategory.TestGatingGap, "Tests exist but do not gate the QA deployment");
        notGating.Should().NotBeNull();
        notGating!.Severity.Should().Be(PipelineFindingSeverity.High);
        Finding(r, PipelineFindingCategory.TestGatingGap, "reachable without any detected test").Should().BeNull("tests exist — they only do not gate");
        Finding(r, PipelineFindingCategory.EnvironmentProgressionGap, "Production does not depend on the QA deployment")!.Severity.Should().Be(PipelineFindingSeverity.High);
        Deployment(r, "app-prod").Before.Should().Contain(g => g.Category == ValidationCategory.Unit && g.State == GateState.Gates);
    }

    private const string Conditions = """
        trigger:
          - main
        pool:
          vmImage: ubuntu-latest
        stages:
          - stage: Test
            jobs:
              - job: Unit
                steps:
                  - script: dotnet test tests/App.UnitTests/App.UnitTests.csproj
                  - script: dotnet test tests/App.IntegrationTests/App.IntegrationTests.csproj
                    condition: eq(variables['Build.SourceBranch'], 'refs/heads/main')
                  - script: npx playwright test
                    continueOnError: true
          - stage: DeployQA
            condition: eq(variables['Build.SourceBranch'], 'refs/heads/main')
            jobs:
              - deployment: Web
                environment: app-qa
                strategy:
                  runOnce:
                    deploy:
                      steps:
                        - script: az webapp deploy --name app-qa --src-path app.zip
                    routeTraffic:
                      steps:
                        - script: curl -f https://app-qa.example.test/health
        """;

    [Fact]
    public void Conditions_and_continueOnError_are_kept_and_a_custom_condition_without_succeeded_is_a_bypass()
    {
        var r = Review(("pipelines/ci.yml", Conditions));
        var qa = Deployment(r, "app-qa");
        qa.Before.Single(g => g.Category == ValidationCategory.Unit).State.Should().Be(GateState.SoftGate, "the stage condition replaces succeeded(): it runs even when Test fails");
        qa.Before.Single(g => g.Category == ValidationCategory.Integration).Should().Match<ValidationGate>(g => g.State == GateState.Conditional && g.Condition!.Contains("refs/heads/main"));
        qa.Before.Single(g => g.Category == ValidationCategory.E2E).State.Should().Be(GateState.SoftGate);
        Finding(r, PipelineFindingCategory.ConditionCoverageGap, "can run even if earlier validation fails")!.Severity.Should().Be(PipelineFindingSeverity.High);
        Finding(r, PipelineFindingCategory.ConditionCoverageGap, "only run under a condition").Should().NotBeNull();
        Finding(r, PipelineFindingCategory.ConditionCoverageGap, "may not block").Should().NotBeNull();
        qa.After.Should().ContainSingle(g => g.Category == ValidationCategory.HealthCheck, "strategy phases after deploy are post-deployment");
        Finding(r, PipelineFindingCategory.PostDeployValidationGap, "QA").Should().BeNull();
        qa.Condition.Should().Contain("refs/heads/main");
    }

    // ── Tests, environments, artifacts ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Test_presence_is_not_test_gating_and_post_deployment_validation_is_per_environment()
    {
        var r = ShopReview.Value;
        var qa = Deployment(r, "shop-qa");
        qa.Before.Where(g => PipelineReviewBuilder.IsTest(g.Category)).Should().OnlyContain(g => g.Category == ValidationCategory.Unit && g.State == GateState.Gates);
        Finding(r, PipelineFindingCategory.TestGatingGap, "Integration tests do not gate the QA deployment")!.Should()
            .Match<PipelineReviewFinding>(f => f.Severity == PipelineFindingSeverity.Medium && f.EvidenceState == ArchitectureEvidenceState.Confirmed && f.Evidence.Count >= 2);
        Deployment(r, "shop-dev").After.Should().ContainSingle(g => g.Category == ValidationCategory.Integration && g.State == GateState.After);
        Finding(r, PipelineFindingCategory.PostDeployValidationGap, "after the QA deployment").Should().NotBeNull();
        Finding(r, PipelineFindingCategory.PostDeployValidationGap, "after the DEV deployment").Should().BeNull();
        Finding(r, PipelineFindingCategory.TestGatingGap, "PROD deployment is reachable without any detected test")!.Severity.Should().Be(PipelineFindingSeverity.High);
        var matrix = r.TestMatrix;
        matrix.Columns.Should().Equal("PR", "DEV", "QA", "PROD");
        matrix.Rows.Single(x => x.Category == ValidationCategory.Unit).Cells.Should().Contain(new KeyValuePair<string, GateState?>("QA", GateState.Gates)).And.Contain(new KeyValuePair<string, GateState?>("PROD", null));
        matrix.Rows.Single(x => x.Category == ValidationCategory.Integration).Cells["DEV"].Should().Be(GateState.After);
        r.Tests.Single(t => t.Category == ValidationCategory.Integration).Should().Match<ReviewedTest>(t => t.ContinueOnError && t.Gates.Count == 0 && t.RunsAfter.Count == 1);
    }

    [Fact]
    public void Approval_and_rollback_are_stated_conservatively()
    {
        var prod = Deployment(ShopReview.Value, "shop-prod");
        prod.Approval.Should().Contain("not assessable from YAML alone").And.NotContain("No approval");
        prod.Rollback.Should().StartWith("No rollback mechanism detected").And.Contain("does not mean rollback is impossible");
        Finding(ShopReview.Value, PipelineFindingCategory.RollbackGap, "PROD")!.Severity.Should().Be(PipelineFindingSeverity.Info);
        var manual = Review(("pipelines/ci.yml", """
            trigger: none
            pool:
              vmImage: ubuntu-latest
            jobs:
              - job: Approve
                pool: server
                steps:
                  - task: ManualValidation@0
                    displayName: Approve production
              - job: Deploy
                dependsOn: Approve
                steps:
                  - script: kubectl apply -f k8s/prod
                  - script: kubectl rollout undo deployment/app
                    condition: failed()
            """));
        manual.Deployments.Single().Approval.Should().Contain("manual validation step (`Approve production`)");
        manual.Deployments.Single().Rollback.Should().Contain("Rollback step");
    }

    [Fact]
    public void Rebuild_per_environment_is_reported_and_unresolved_lineage_is_not_called_different()
    {
        var r = ShopReview.Value;
        Finding(r, PipelineFindingCategory.ArtifactLineageGap, "Production and QA appear to use different builds")!.EvidenceState.Should().Be(ArchitectureEvidenceState.StronglySupported);
        r.Findings.Should().NotContain(f => f.Title.Contains("PROD rebuilds"), "production vs QA lineage is one finding, not repeated against DEV");
        Deployment(r, "shop-qa").Artifacts.Single().Should().Match<ArtifactLineageItem>(a => a.Artifact == "app" && a.ProducerStage == "Build" && a.State == ArchitectureEvidenceState.StronglySupported);
        Deployment(r, "shop-prod").Artifacts.Single().Should().Match<ArtifactLineageItem>(a => a.State == ArchitectureEvidenceState.Confirmed && a.ProducerStage == "BuildProd");

        var unresolved = Review(("pipelines/build.yml", "trigger:\n  - main\npool:\n  vmImage: ubuntu-latest\nstages:\n  - stage: QA\n    jobs:\n      - job: Test\n        steps:\n          - script: dotnet test tests/A.UnitTests/A.UnitTests.csproj\n      - deployment: Web\n        dependsOn: Test\n        environment: app-qa\n        strategy:\n          runOnce:\n            deploy:\n              steps:\n                - script: az webapp deploy --name a\n"),
            ("pipelines/release.yml", "trigger: none\npool:\n  vmImage: ubuntu-latest\nresources:\n  pipelines:\n    - pipeline: upstream\n      source: some-other-repo-build\nstages:\n  - stage: Prod\n    jobs:\n      - deployment: Web\n        environment: app-prod\n        strategy:\n          runOnce:\n            deploy:\n              steps:\n                - download: upstream\n                  artifact: web\n                - script: az webapp deploy --name a\n"));
        Finding(unresolved, PipelineFindingCategory.ArtifactLineageGap, "Artifact lineage to Production unresolved")!.EvidenceState.Should().Be(ArchitectureEvidenceState.Unresolved);
        unresolved.Findings.Should().NotContain(f => f.Title.Contains("different builds"));
        Finding(unresolved, PipelineFindingCategory.CrossPipelineDependencyGap, "upstream").Should().NotBeNull();
    }

    private const string PromoteBuild = """
        trigger:
          - main
        pool:
          vmImage: ubuntu-latest
        stages:
          - stage: Build
            jobs:
              - job: Build
                steps:
                  - script: dotnet test tests/App.UnitTests/App.UnitTests.csproj
                  - publish: out
                    artifact: web
          - stage: QA
            jobs:
              - deployment: Web
                environment: app-qa
                strategy:
                  runOnce:
                    deploy:
                      steps:
                        - script: az webapp deploy --name app-qa
                        - script: dotnet test tests/App.SmokeTests/App.SmokeTests.csproj
        """;

    private const string PromoteRelease = """
        trigger: none
        pool:
          vmImage: ubuntu-latest
        resources:
          pipelines:
            - pipeline: build
              source: app-build
              trigger:
                branches:
                  include:
                    - main
        stages:
          - stage: Production
            jobs:
              - deployment: Web
                environment: app-prod
                strategy:
                  runOnce:
                    deploy:
                      steps:
                        - download: build
                          artifact: web
                        - script: az webapp deploy --name app-prod
        """;

    [Fact]
    public void A_pipeline_resource_trigger_with_artifact_download_promotes_the_tested_artifact()
    {
        var r = Review(("pipelines/app-build.yml", PromoteBuild), ("pipelines/app-release.yml", PromoteRelease));
        var prod = Deployment(r, "app-prod");
        prod.Artifacts.Single().Should().Match<ArtifactLineageItem>(a => a.Artifact == "web" && a.ProducerStage == "Build" && a.State == ArchitectureEvidenceState.StronglySupported);
        prod.Before.Should().Contain(g => g.Category == ValidationCategory.Unit && g.State == GateState.Inherited, "unit tests gated the producer of the promoted artifact");
        r.Dependencies.Should().Contain(d => d.Kind == PipelineEdgeKind.Triggers && d.ToPipeline == "repo/pipelines/app-release.yml" && d.FromPipeline == "repo/pipelines/app-build.yml" && d.State == ArchitectureEvidenceState.StronglySupported);
        r.Dependencies.Should().Contain(d => d.Kind == PipelineEdgeKind.ConsumesArtifactFrom && d.Basis.Contains("`web`"));
        r.Progression.Should().ContainSingle(s => s.From == "QA" && s.To == "PROD" && s.State == ArchitectureEvidenceState.StronglySupported);
        r.Findings.Should().NotContain(f => f.Category == PipelineFindingCategory.ArtifactLineageGap || f.Category == PipelineFindingCategory.EnvironmentProgressionGap);
        r.Findings.Should().NotContain(f => f.Category == PipelineFindingCategory.TestGatingGap && f.Environment == "PROD");
        r.Story.Should().Contain(s => s.Text == "app-release starts when app-build completes (pipeline resource `build`)." && s.State == ArchitectureEvidenceState.StronglySupported);
        r.TestMatrix.Rows.Single(x => x.Category == ValidationCategory.Unit).Cells["PROD"].Should().Be(GateState.Inherited);
        Deployment(r, "app-qa").After.Should().ContainSingle(g => g.Category == ValidationCategory.Smoke);
    }

    [Fact]
    public void A_pipeline_resource_name_is_mapped_to_a_file_only_by_metadata_or_name_never_by_similarity_alone()
    {
        var metadata = new PipelineMetadataSummary { State = "Available", Definitions = [new("7", "Contoso App CI", null, "/pipelines/app-build.yml", "app")],
            Environments = [new("app-prod", ["Approval", "Branch control"])] };
        var release = PromoteRelease.Replace("source: app-build", "source: Contoso App CI");
        var withAdo = PipelineReviewBuilder.Build(Snapshot([("repo/App.sln", ""), ("repo/pipelines/app-build.yml", PromoteBuild), ("repo/pipelines/app-release.yml", release)]), metadata);
        withAdo.Dependencies.Should().Contain(d => d.Kind == PipelineEdgeKind.Triggers && d.State == ArchitectureEvidenceState.Confirmed && d.Basis.Contains("Azure DevOps pipeline 'Contoso App CI'"));
        withAdo.Pipelines.Single(p => p.File == "repo/pipelines/app-build.yml").Name.Should().Be("Contoso App CI");
        Deployment(withAdo, "app-prod").Approval.Should().Contain("Approval, Branch control").And.Contain("not whether they were approved");
        var withoutAdo = Review(("pipelines/app-build.yml", PromoteBuild), ("pipelines/app-release.yml", release));
        withoutAdo.Dependencies.Should().Contain(d => d.Kind == PipelineEdgeKind.Triggers && d.State == ArchitectureEvidenceState.Unresolved);
        withoutAdo.Progression.Should().BeEmpty();
    }

    // ── Infrastructure and templates ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Infrastructure_before_application_is_confirmed_and_the_reverse_or_apply_without_plan_needs_review()
    {
        ShopReview.Value.Findings.Should().NotContain(f => f.Category == PipelineFindingCategory.InfrastructureSequenceGap);
        Deployment(ShopReview.Value, "QA").DeploysWhat.Should().Equal("Infrastructure");
        var reversed = Review(("pipelines/ci.yml", """
            trigger:
              - main
            pool:
              vmImage: ubuntu-latest
            stages:
              - stage: AppQA
                jobs:
                  - deployment: Web
                    environment: app-qa
                    strategy:
                      runOnce:
                        deploy:
                          steps:
                            - script: dotnet test tests/A.UnitTests/A.UnitTests.csproj
                            - script: az webapp deploy --name a
              - stage: InfraQA
                displayName: Infrastructure QA
                jobs:
                  - job: Apply
                    steps:
                      - script: terraform -chdir=infra apply -auto-approve -var-file=qa.tfvars
            """));
        Finding(reversed, PipelineFindingCategory.InfrastructureSequenceGap, "Application deploys before infrastructure in QA").Should().NotBeNull();
        Finding(reversed, PipelineFindingCategory.InfrastructureSequenceGap, "terraform apply in QA without a preceding plan").Should().NotBeNull();
    }

    [Fact]
    public void Local_templates_are_composed_with_their_origin_and_parameters()
    {
        var r = ShopReview.Value;
        Deployment(r, "shop-qa").Should().Match<DeploymentReview>(d => d.Evidence.TemplateOrigin == "shop/pipelines/templates/deploy-webapp.yml" && d.Stage == "DeployQA" && d.EnvironmentKind == SourceEnvironmentKind.QA);
        Deployment(r, "shop-dev").Before.Single(g => g.Category == ValidationCategory.Unit).Evidence.TemplateOrigin.Should().Be("shop/pipelines/templates/dotnet-tests.yml");
        r.Tests.Where(t => t.Category == ValidationCategory.Unit).Should().Contain(t => t.Targets.Contains("tests/Shop.Api.UnitTests/Shop.Api.UnitTests.csproj"));
        r.Dependencies.Should().Contain(d => d.Kind == PipelineEdgeKind.IncludesTemplate && d.ToPipeline == "shop/pipelines/templates/terraform.yml" && d.State == ArchitectureEvidenceState.Confirmed);
        Finding(r, PipelineFindingCategory.TemplateResolutionGap, "Unreferenced pipeline template `shop/pipelines/templates/unused.yml`")!.Severity.Should().Be(PipelineFindingSeverity.Info);
    }

    [Fact]
    public void An_unresolved_template_on_the_deployment_path_suppresses_missing_test_findings()
    {
        var r = Review(("pipelines/ci.yml", """
            trigger:
              - main
            pool:
              vmImage: ubuntu-latest
            resources:
              repositories:
                - repository: shared
                  type: git
                  name: Platform/shared-templates
            stages:
              - stage: Build
                jobs:
                  - template: jobs/build-and-test.yml@shared
              - stage: Prod
                jobs:
                  - deployment: Web
                    environment: app-prod
                    strategy:
                      runOnce:
                        deploy:
                          steps:
                            - script: az webapp deploy --name app-prod
            """));
        r.Findings.Should().NotContain(f => f.Category == PipelineFindingCategory.TestGatingGap, "tests may be hidden in the external template");
        r.Findings.Should().NotContain(f => f.Category == PipelineFindingCategory.PostDeployValidationGap && f.Environment == "PROD" && f.EvidenceState == ArchitectureEvidenceState.Confirmed && false);
        Finding(r, PipelineFindingCategory.UnresolvedFlow, "Test gating of PROD deployment cannot be assessed")!.Severity.Should().Be(PipelineFindingSeverity.Info);
        Deployment(r, "app-prod").Before.Should().Contain(g => g.State == GateState.Unknown);
        r.UnresolvedTemplates.Should().ContainSingle(u => u.Template == "jobs/build-and-test.yml@shared" && u.Reason.Contains("repository resource 'shared'"));
        r.TestMatrix.Rows.Should().Contain(x => x.Cells.Values.Contains(GateState.Unknown));
    }

    // ── Coverage ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Path_filters_that_exclude_a_shared_library_or_a_consumed_contract_are_coverage_gaps()
    {
        var r = ShopReview.Value;
        Finding(r, PipelineFindingCategory.TriggerCoverageGap, "shared library Shop.Common do not start `pr-validation`")!.EvidenceState.Should().Be(ArchitectureEvidenceState.StronglySupported);
        Finding(r, PipelineFindingCategory.TriggerCoverageGap, "shared library Shop.Common do not start `worker-build`").Should().NotBeNull();
        r.Findings.Should().NotContain(f => f.Title.Contains("`shop-build`") && f.Category == PipelineFindingCategory.TriggerCoverageGap, "shop-build includes all of src");
        Finding(r, PipelineFindingCategory.ContractValidationGap, "contract Shop API may not start validation of consumer Worker")!.Explanation.Should().Contain("shop/src/Shop.Api/openapi.json");
        var common = r.PathCoverage.Single(x => x.Path == "src/Shop.Common" && x.AreaKind == "Shared library");
        common.Pipelines.Single(c => c.Pipeline.EndsWith("shop-build.yml")).Triggers.Should().Be(TriggerCoverage.Yes);
        common.Pipelines.Single(c => c.Pipeline.EndsWith("pr-validation.yml")).Triggers.Should().Be(TriggerCoverage.No);
        r.PathCoverage.Should().Contain(x => x.AreaKind == "Infrastructure" && x.Path == "infra" && x.Pipelines.Any(c => c.Pipeline.EndsWith("shop-build.yml") && c.Triggers == TriggerCoverage.Yes));
    }

    [Fact]
    public void The_path_probe_answers_which_pipelines_and_validations_follow_a_change()
    {
        var snapshot = Snapshot(Shop());
        var review = PipelineReviewBuilder.Build(snapshot);
        var probe = PipelineReviewBuilder.Probe(snapshot, review, "/src/Shop.Common/Money.cs");
        probe.Path.Should().Be("src/Shop.Common/Money.cs");
        probe.Pipelines.Single(p => p.PipelineName == "shop-build").Should().Match<PathProbePipeline>(p => p.Triggers == TriggerCoverage.Yes && p.Deployments.Count == 3);
        probe.Pipelines.Single(p => p.PipelineName == "pr-validation").Triggers.Should().Be(TriggerCoverage.No);
        probe.Pipelines.Single(p => p.PipelineName == "shop-release").Basis.Should().Contain("manual");
        var api = PipelineReviewBuilder.Probe(snapshot, review, "src/Shop.Api/Program.cs");
        api.Pipelines.Single(p => p.PipelineName == "pr-validation").Triggers.Should().Be(TriggerCoverage.Yes);
        api.Pipelines.Single(p => p.PipelineName == "shop-build").Deployments.Single(d => d.Environment == "shop-qa").Before.Should().Contain(g => g.Category == ValidationCategory.Unit);
    }

    [Theory]
    [InlineData("src/api", "src/api/x.cs", true)]
    [InlineData("src/api", "src/apix/x.cs", false)]
    [InlineData("/src/api/", "src/api/x.cs", true)]
    [InlineData("src/*/Program.cs", "src/api/Program.cs", true)]
    [InlineData("**/*.md", "docs/a/readme.md", true)]
    [InlineData("docs", "src/docs.cs", false)]
    public void Path_filter_semantics_match_Azure_Pipelines(string filter, string path, bool expected) => PipelinePathFilters.Matches(filter, path).Should().Be(expected);

    [Fact]
    public void Conditional_security_scans_do_not_count_as_gating()
    {
        var f = Finding(ShopReview.Value, PipelineFindingCategory.SecurityValidationGap, "do not appear to gate normal delivery");
        f!.Explanation.Should().Contain("only when `eq(variables['Build.Reason'], 'Schedule')`").And.Contain("continueOnError");
        f.Severity.Should().Be(PipelineFindingSeverity.Low);
    }

    private const string ServiceDev = """
        trigger:
          branches:
            include:
              - main
        pool:
          vmImage: ubuntu-latest
        stages:
          - stage: Build
            jobs:
              - job: BuildPushImage
                displayName: Build, test and push image
                steps:
                  - script: dotnet test Orders/tests/Orders.UnitTests/Orders.UnitTests.csproj
                  - script: dotnet ef migrations bundle --project Orders/src/Orders.Api -o efbundle
                  - script: docker build -t orders:$(Build.BuildId) Orders/src/Orders.Api
                  - script: docker push orders:$(Build.BuildId)
          - stage: Deploy
            jobs:
              - template: templates/deploy.yml
        """;

    private const string DeployTemplate2 = """
        parameters:
          - name: environment
            type: string
            default: orders-dev
        jobs:
          - deployment: DeployToContainerApps
            environment: ${{ parameters.environment }}
            strategy:
              runOnce:
                deploy:
                  steps:
                    - script: az containerapp update --name orders --image orders:$(Build.BuildId)
        """;

    [Fact]
    public void A_migration_bundle_build_is_not_a_deployment_and_template_parameter_defaults_apply()
    {
        var qa = ServiceDev.ReplaceLineEndings("\n").Replace("trigger:\n  branches:\n    include:\n      - main", "trigger: none\npr: none").Replace("- template: templates/deploy.yml", "- template: templates/deploy.yml\n        parameters:\n          environment: orders-qa");
        var r = Review(("pipelines/orders-dev.yml", ServiceDev), ("pipelines/orders-qa.yml", qa), ("pipelines/templates/deploy.yml", DeployTemplate2));
        r.Deployments.Should().HaveCount(2, "building a migration bundle is build work, not a database deployment");
        r.Deployments.Select(d => d.Environment).Should().BeEquivalentTo(["orders-dev", "orders-qa"], "the template default applies when no parameter is passed");
        Deployment(r, "orders-dev").Artifacts.Single().Should().Match<ArtifactLineageItem>(a => a.Artifact == "(container image)" && a.ProducerStage == "Build" && a.Basis.Contains("not statically derivable"));
        r.Pipelines.Single(p => p.Name == "orders-qa").Triggers.Select(t => t.Text).Should().Equal("No push trigger (`trigger: none`)", "No pull-request trigger (`pr: none`)");
        var rebuild = Finding(r, PipelineFindingCategory.ArtifactLineageGap, "QA rebuilds instead of promoting the DEV-tested build (`orders-qa`)");
        rebuild!.Should().Match<PipelineReviewFinding>(f => f.EvidenceState == ArchitectureEvidenceState.StronglySupported && f.Severity == PipelineFindingSeverity.Medium && f.Explanation.Contains("`Orders/src/Orders.Api`"));
    }

    // ── Changes, metadata, secrets ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Changes_between_snapshots_are_interpreted_as_source_changes()
    {
        var gated = ShopBuild.ReplaceLineEndings("\n").Replace("      - DeployDev\n      - InfraQA", "      - IntegrationTests\n      - InfraQA");
        gated.Should().NotBe(ShopBuild.ReplaceLineEndings("\n"));
        var previous = PipelineReviewBuilder.Build(Snapshot(Shop(("shop/pipelines/shop-build.yml", gated))));
        var current = PipelineReviewBuilder.Build(Snapshot(Shop(("shop/pipelines/worker-build.yml", WorkerBuild.Replace("- src/Shop.Worker", "- src/Shop.Worker/Jobs")))));
        var diff = PipelineReviewDiff.Compare(previous, current);
        diff.Changes.Should().Contain(c => c.Kind == PipelineChangeKind.GatingChanged && c.Detail.StartsWith("Integration tests are no longer on the QA deployment path") && c.Severity == PipelineFindingSeverity.High);
        diff.Changes.Should().Contain(c => c.Kind == PipelineChangeKind.PathFilterChanged && c.Pipeline == "worker-build");
        diff.Boundary.Should().Contain("not pipeline runtime failures");
        PipelineReviewDiff.Compare(current, current).Changes.Should().BeEmpty();
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Requests.Add(request); return Task.FromResult(respond(request)); }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };

    [Fact]
    public async Task Azure_DevOps_metadata_is_read_only_optional_and_never_exposes_the_PAT()
    {
        var options = Options.Create(new AzureDevOpsOptions { Enabled = true, OrganizationUrl = "https://dev.azure.com/contoso", Project = "Shop", Pat = "SECRET_SENTINEL_PAT" });
        var handler = new Handler(r => r.RequestUri!.AbsolutePath switch
        {
            "/contoso/Shop/_apis/pipelines" => Json("""{"value":[{"id":7,"name":"Shop Build","folder":"\\"}]}"""),
            "/contoso/Shop/_apis/pipelines/7" => Json("""{"id":7,"configuration":{"path":"/pipelines/shop-build.yml","repository":{"name":"shop"}}}"""),
            "/contoso/Shop/_apis/distributedtask/environments" => Json("""{"value":[{"id":3,"name":"shop-prod"}]}"""),
            "/contoso/Shop/_apis/pipelines/checks/configurations" => Json("""{"value":[{"type":{"name":"Approval"}}]}"""),
            _ => Json("{}", HttpStatusCode.NotFound),
        });
        var source = new AzureDevOpsPipelineMetadataSource(new HttpClient(handler), options, NullLogger<AzureDevOpsPipelineMetadataSource>.Instance);
        var summary = await source.GetAsync(default);
        summary.Should().Match<PipelineMetadataSummary>(s => s.State == "Available" && s.Definitions.Single().YamlPath == "/pipelines/shop-build.yml" && s.Environments.Single().Checks.Single() == "Approval");
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
        JsonSerializer.Serialize(summary).Should().NotContain("SECRET_SENTINEL");
        var review = PipelineReviewBuilder.Build(Snapshot(Shop()), summary);
        review.Pipelines.Single(p => p.File.EndsWith("shop-build.yml")).Name.Should().Be("Shop Build");
        Deployment(review, "shop-prod").Approval.Should().Contain("checks on environment `shop-prod`: Approval");

        (await new AzureDevOpsPipelineMetadataSource(new HttpClient(handler), Options.Create(new AzureDevOpsOptions()), NullLogger<AzureDevOpsPipelineMetadataSource>.Instance).GetAsync(default))
            .State.Should().Be("NotConfigured");
        (await new AzureDevOpsPipelineMetadataSource(new HttpClient(new Handler(_ => Json("{}", HttpStatusCode.Unauthorized))), options, NullLogger<AzureDevOpsPipelineMetadataSource>.Instance).GetAsync(default))
            .State.Should().Be("NotAuthorized");
    }

    [Fact]
    public void Secret_values_never_reach_the_review_and_service_connections_stay_names()
    {
        var json = JsonSerializer.Serialize(ShopReview.Value);
        json.Should().NotContain("SECRET_SENTINEL");
        Snapshot(Shop()).EvidenceDomains!.CiCd.Pipelines.SelectMany(p => p.SecretReferences).Should().Contain(s => s.Contains("service connection: shop-prod-connection"));
    }
}
