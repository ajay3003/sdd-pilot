using System.Text.Json;
using BirkNext.SourceDomains;
using FluentAssertions;
using static BirkNext.Api.Tests.Services.SourceAnalysis.Evidence.SourceEvidenceFixtures;

namespace BirkNext.Api.Tests.Services.SourceAnalysis.Evidence;

/// <summary>Source Analysis → CI/CD: pipeline definitions read statically. A step is what the pipeline intends to run — never that it ran or
/// passed; scripts, variable values and secrets are never stored.</summary>
public sealed class PipelineEvidenceTests
{
    [Fact]
    public void Azure_pipeline_triggers_stages_steps_and_environments_are_extracted()
    {
        var p = Analyze(SourceEvidenceFixtures.Acme()).CiCd.Pipelines.Single();
        p.Platform.Should().Be(PipelinePlatform.AzurePipelines);
        p.IsTemplate.Should().BeFalse();
        p.Triggers.Should().Contain(t => t.Type == "push" && t.BranchesInclude.SequenceEqual(new[] { "main" }) && t.PathsInclude.SequenceEqual(new[] { "src/*", "Infrastructure/*" }));
        p.Triggers.Should().Contain(t => t.Type == "pull-request");
        p.Stages.Select(s => s.Name).Should().Equal("Build", "DeployQa");
        p.Stages.Single(s => s.Name == "DeployQa").Should().Match<PipelineStage>(s => s.Deployment && s.Environment == "qa" && s.DependsOn.Contains("Build"));
        var kinds = p.Steps.Select(s => s.Kind).ToList();
        kinds.Should().Contain([PipelineStepKind.Restore, PipelineStepKind.Build, PipelineStepKind.UnitTest, PipelineStepKind.Coverage, PipelineStepKind.DependencyScan,
            PipelineStepKind.SecretRetrieval, PipelineStepKind.InfrastructureDeploy, PipelineStepKind.ApplicationDeploy]);
        p.Steps.Single(s => s.Kind == PipelineStepKind.UnitTest).Targets.Should().Equal("tests/Acme.Ordering.UnitTests/Acme.Ordering.UnitTests.csproj");
        p.Steps.Single(s => s.Kind == PipelineStepKind.InfrastructureDeploy).Targets.Should().BeEquivalentTo(["Infrastructure", "Infrastructure/qa.tfvars"]);
        p.Steps.Should().OnlyContain(s => s.ExecutionState == SourceDomainText.StepNotExecuted);
    }

    [Fact]
    public void Secrets_are_names_only_and_script_text_is_never_stored()
    {
        var evidence = Analyze(SourceEvidenceFixtures.Acme()).CiCd;
        var p = evidence.Pipelines.Single();
        p.SecretReferences.Should().Contain(["variable group: ordering-secrets", "Key Vault task: kv-acme", "secret variable: DbAdminPassword"]);
        var json = JsonSerializer.Serialize(evidence);
        json.Should().NotContain("echo").And.NotContain("-auto-approve").And.NotContain("/dev/null");
        evidence.Limitations.Should().Contain(l => l.Contains("not that it ran or passed"));
        evidence.DependencyAutomation.Should().Equal("renovate.json");
    }

    [Fact]
    public void GitHub_actions_with_oidc_paths_and_actions_are_recognized()
    {
        var p = Analyze(AwsKubernetes()).CiCd.Pipelines.Single();
        p.Platform.Should().Be(PipelinePlatform.GitHubActions);
        p.Triggers.Should().Contain(t => t.Type == "push" && t.PathsInclude.SequenceEqual(new[] { "infra/**" }));
        p.Triggers.Should().Contain(t => t.Type == "manual");
        p.UsesFederatedCredentials.Should().BeTrue();
        p.Environments.Should().Equal("production");
        p.SecretReferences.Should().Equal("secret: DEPLOY_TOKEN");
        p.Steps.Should().Contain(s => s.Kind == PipelineStepKind.InfrastructureDeploy && s.Targets.Contains("infra"));
        p.Steps.Should().Contain(s => s.Kind == PipelineStepKind.SecurityScan && s.Tool == "Trivy");
        p.Steps.Should().Contain(s => s.Kind == PipelineStepKind.E2ETest);
        p.ApprovalsDeclared.Should().BeFalse("environment protection rules live in the CI system, not in source");
    }

    [Fact]
    public void Templates_and_tool_yaml_are_distinguished_from_runnable_pipelines()
    {
        var e = Analyze(
            (".pipeline/build.yml", "trigger: none\nsteps:\n  - template: templates/test.yml\n"),
            (".pipeline/templates/test.yml", "parameters:\n  - name: project\n    type: string\nsteps:\n  - script: dotnet test ${{ parameters.project }}\n"),
            (".specify/workflows/speckit/workflow.yml", "name: speckit\nsteps:\n  - id: plan\n    run: echo plan\n"),
            ("docs/example-pipeline.yml", "notes: true\n")).CiCd;
        e.Pipelines.Should().HaveCount(2);
        e.Pipelines.Single(p => p.File.EndsWith("templates/test.yml")).IsTemplate.Should().BeTrue();
        e.Pipelines.Single(p => p.File == ".pipeline/build.yml").Templates.Should().Equal(".pipeline/templates/test.yml");
        e.Pipelines.Single(p => p.File == ".pipeline/build.yml").Triggers.Single().Type.Should().Be("none");
        e.Status.Should().Be(SourceDomainStatus.Partial, "pipeline reading is honest about its subset reader");
    }

    [Fact]
    public void Missing_pipelines_are_not_detected_in_the_selected_source()
    {
        var e = Analyze(("src/App/appsettings.json", "{}")).CiCd;
        e.Status.Should().Be(SourceDomainStatus.NotDetected);
        e.StatusReason.Should().Contain("may be defined elsewhere");
    }
}
