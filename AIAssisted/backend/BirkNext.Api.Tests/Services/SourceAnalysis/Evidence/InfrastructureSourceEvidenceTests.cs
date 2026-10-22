using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.SourceDomains;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.SourceAnalysis.Evidence;

/// <summary>
/// Infrastructure Source Evidence: Terraform and infrastructure scripts are sibling providers feeding ONE normalized evidence per snapshot.
/// "Contoso.Platform" is a generic layered Terraform repository with validation, inventory and helper scripts, a pre-commit hook and two
/// pipelines. Nothing is executed: scripts are read statically, Terraform is never initialised, and secrets planted as SECRET_SENTINEL never
/// leave the analysis.
/// </summary>
public sealed class InfrastructureSourceEvidenceTests
{
    private const string Sentinel = SourceEvidenceFixtures.Sentinel;

    private const string Network = """
        terraform {
          required_version = ">= 1.9.0"
          required_providers {
            azurerm = { source = "hashicorp/azurerm", version = "~> 4.0" }
          }
          backend "azurerm" { use_azuread_auth = true }
        }
        resource "azurerm_resource_group" "network" {
          name     = "rg-contoso-network"
          location = "norwayeast"
        }
        resource "azurerm_virtual_network" "main" {
          name                = "vnet-contoso"
          resource_group_name = azurerm_resource_group.network.name
          location            = "norwayeast"
        }
        output "subnet_id" {
          value = azurerm_virtual_network.main.id
        }
        """;

    private const string Lock = """
        # This file is maintained automatically by "terraform init".
        provider "registry.terraform.io/hashicorp/azurerm" {
          version     = "4.1.0"
          constraints = "~> 4.0"
          hashes = ["h1:abc="]
        }
        """;

    private const string Messaging = """
        terraform {
          required_version = ">= 1.9.0"
        }
        variable "enable_dlq" {
          type = bool
        }
        data "terraform_remote_state" "network" {
          backend = "azurerm"
          config = {
            container_name = "10-network"
            key            = "terraform.tfstate"
          }
        }
        resource "azurerm_servicebus_namespace" "main" {
          name     = "sb-contoso"
          location = "norwayeast"
          sku      = "Standard"
        }
        resource "azurerm_servicebus_topic" "orders" {
          name         = "orders"
          namespace_id = azurerm_servicebus_namespace.main.id
        }
        resource "azurerm_servicebus_queue" "dlq" {
          count        = var.enable_dlq ? 1 : 0
          name         = "orders-dlq"
          namespace_id = azurerm_servicebus_namespace.main.id
        }
        resource "azurerm_role_assignment" "worker_sb_sender" {
          scope                = azurerm_servicebus_namespace.main.id
          role_definition_name = "Azure Service Bus Data Sender"
          principal_id         = data.terraform_remote_state.network.outputs.subnet_id
        }
        resource "azurerm_role_assignment" "reader" {
          scope                = azurerm_servicebus_namespace.main.id
          role_definition_name = "Reader"
          principal_id         = data.terraform_remote_state.network.outputs.missing_output
        }
        module "kv" {
          source  = "Azure/avm-res-keyvault-vault/azurerm"
          version = "~> 0.9"
        }
        """;

    private const string RoleNames = $$"""
        #!/usr/bin/env python3
        # Pre-commit hook: validates role assignment names.
        import os
        import re
        import sys
        from pathlib import Path

        RESOURCE_RE = re.compile(r'resource\s+"azurerm_role_assignment"\s+"(?P<name>[A-Za-z0-9_]+)"')
        CLIENT_SECRET = "{{Sentinel}}_PY"
        TOKEN = os.environ.get("CLIENT_SECRET")
        ROOT = os.getenv("TF_ROOT")


        def check(path: Path) -> int:
            violations = 0
            for m in RESOURCE_RE.finditer(path.read_text()):
                name = m.group("name")
                if "_" not in name:
                    print(f"{path}:1: azurerm_role_assignment.{name}: resource name must have at least two segments", file=sys.stderr)
                    violations += 1
            return violations


        def main(argv) -> int:
            total = sum(check(Path(a)) for a in argv if a.endswith(".tf"))
            return 1 if total else 0


        if __name__ == "__main__":
            sys.exit(main(sys.argv[1:]))
        """;

    private const string ProviderVersions = """
        #!/usr/bin/env python3
        # Report: locked provider versions vs the registry. Always exits 0.
        import json
        import sys
        import urllib.request
        from pathlib import Path

        ROOT = Path(sys.argv[1] if len(sys.argv) > 1 else "terraform/layers")
        API = "https://registry.terraform.io/v1/providers/{}/versions"


        def main() -> int:
            for lock in sorted(ROOT.glob("*/.terraform.lock.hcl")):
                text = lock.read_text()
                print(f"=== {lock.parent.name}")
            return 0


        if __name__ == "__main__":
            sys.exit(main())
        """;

    private const string FmtCheck = """
        #!/usr/bin/env bash
        # Formatting gate for all layers.
        set -euo pipefail
        if ! terraform fmt -check -recursive terraform/; then
          echo "terraform files are not formatted" >&2
          exit 1
        fi
        """;

    private const string Inventory = """
        #!/usr/bin/env python3
        import json
        import sys
        from pathlib import Path

        REPO_ROOT = Path(__file__).resolve().parent.parent
        LAYERS = REPO_ROOT / "terraform" / "layers"
        OUT = REPO_ROOT / "docs" / "rbac-inventory.json"


        def main() -> int:
            rows = []
            for tf in LAYERS.rglob("*.tf"):
                if "azurerm_role_assignment" in tf.read_text():
                    rows.append(str(tf))
            with open(OUT, "w") as f:
                json.dump({"inventory": rows}, f)
            return 0


        if __name__ == "__main__":
            sys.exit(main())
        """;

    private const string Dynamic = """
        import sys
        RULES = "azurerm_storage_account"
        for rule in sys.argv[1:]:
            eval(rule)
        """;

    private const string Helper = $$"""
        param([string]$Root = "terraform")
        $ErrorActionPreference = 'Stop'
        $secret = $env:ARM_CLIENT_SECRET
        Get-ChildItem -Path terraform -Filter *.tf -Recurse | Out-Null
        terraform validate
        if ($LASTEXITCODE -ne 0) { Write-Error "Terraform validation failed for $Root"; exit 1 }
        $token = "{{Sentinel}}_PS"
        """;

    private const string AppTool = """
        import flask
        print("building the app")
        """;

    private const string PreCommit = """
        repos:
          - repo: local
            hooks:
              - id: check-role-names
                name: Role assignment names
                entry: scripts/check-role-names.py
                language: python
                files: \.tf$
        """;

    private const string Pipeline = """
        trigger: none
        schedules:
          - cron: "0 6 * * 1"
            branches: { include: [main] }
        stages:
          - stage: validate
            jobs:
              - job: checks
                steps:
                  - script: |
                      python3 scripts/check-provider-versions.py || \
                        echo "report incomplete"
                    displayName: Provider report
                  - script: bash ./scripts/fmt-check.sh
                    displayName: Format check
                  - task: TerraformTaskV4@4
                    displayName: Plan
                    inputs:
                      command: plan
                      workingDirectory: terraform/layers/20-messaging
        """;

    private const string AppPipeline = """
        trigger:
          branches: { include: [main] }
        steps:
          - task: PythonScript@0
            displayName: Build helper
            inputs:
              scriptSource: filePath
              scriptPath: tools/build_app.py
        """;

    internal static (string, string)[] Repository(params (string, string)[] extra) =>
    [
        ("terraform/layers/10-network/main.tf", Network),
        ("terraform/layers/10-network/.terraform.lock.hcl", Lock),
        ("terraform/layers/20-messaging/main.tf", Messaging),
        ("terraform/layers/20-messaging/terraform.tfstate", "{\"secret\": \"" + Sentinel + "_STATE\"}"),
        ("scripts/check-role-names.py", RoleNames),
        ("scripts/check-provider-versions.py", ProviderVersions),
        ("scripts/fmt-check.sh", FmtCheck),
        ("scripts/generate-rbac-inventory.py", Inventory),
        ("scripts/dynamic-rules.py", Dynamic),
        ("scripts/validate.ps1", Helper),
        ("tools/build_app.py", AppTool),
        (".pre-commit-config.yaml", PreCommit),
        ("azure-pipelines.yml", Pipeline),
        ("ci/app-pipeline.yml", AppPipeline),
        .. extra,
    ];

    private static readonly Lazy<SourceEvidenceDomainsSnapshot> Contoso = new(() => SourceEvidenceFixtures.Analyze(Repository()));
    private static InfrastructureEvidence I => Contoso.Value.Infrastructure;
    private static InfrastructureScript Script(string path) => I.Scripts.Single(s => s.Path == path);

    [Fact]
    public void Terraform_and_scripts_are_sibling_providers_feeding_one_normalized_evidence()
    {
        I.AnalyzerVersion.Should().Be(4);
        I.AutomationAnalyzed.Should().BeTrue();
        I.ProviderRuns.Select(r => r.Key).Should().Equal("terraform", "declarative-manifests", "infrastructure-scripts");
        I.ProviderRuns.Single(r => r.Key == "infrastructure-scripts").Detail.Should().Contain("None executed");
        InfrastructureAnalyzer.Providers.Should().HaveCount(3);
        Contoso.Value.Analyzers.Single(a => a.Domain == SourceEvidenceDomain.Infrastructure).DependsOn.Should().Equal(SourceEvidenceDomain.CiCd);
        Contoso.Value.Capabilities.Should().Contain(c => c.Technology == "Script execution" && c.Support == DomainSupport.Unsupported && c.Detail.Contains("never run"));
        I.Resources.Should().Contain(r => r.ResourceType == "azurerm_servicebus_topic" && r.DeclaredName == "orders");
        I.Scripts.Should().NotContain(s => s.Path.StartsWith("tools/"), "a Python file is not infrastructure automation because of its language");
    }

    [Fact]
    public void Terraform_required_version_lock_files_remote_state_conditional_declarations_and_excluded_state()
    {
        I.RequiredVersions.Should().HaveCount(2).And.OnlyContain(r => r.Constraint == ">= 1.9.0");
        I.LockedProviders.Should().ContainSingle(p => p.Address == "registry.terraform.io/hashicorp/azurerm" && p.Version == "4.1.0" && p.Constraints == "~> 4.0");
        var remote = I.RemoteStates.Single();
        remote.UsedBy.Should().Be("terraform/layers/20-messaging");
        remote.SourceLayer.Should().Be("terraform/layers/10-network");
        remote.Resolution.Should().Be("Strong source match", "a state name equal to a layer directory is a source match, never a backend lookup");
        remote.ResolutionBasis.Should().Contain("not verified");
        remote.ReferencedOutputs.Should().Equal("missing_output", "subnet_id");
        remote.OutputsNotDeclared.Should().Equal("missing_output");
        var dlq = I.Resources.Single(r => r.LogicalName == "dlq");
        dlq.Conditional.Should().BeTrue("count = var.enable_dlq ? 1 : 0 is a conditional declaration, not definite existence");
        dlq.InstanceExpression.Should().StartWith("count = var.enable_dlq");
        I.Resources.Count(r => r.LogicalName == "dlq").Should().Be(1, "instances are never expanded or invented");
        I.ExcludedFiles.Should().ContainSingle(x => x.Path == "terraform/layers/20-messaging/terraform.tfstate" && x.Reason == InfrastructureAutomationText.StateExcluded);
    }

    [Fact]
    public void A_validation_script_is_classified_from_content_with_rules_scope_and_never_a_pass_claim()
    {
        var script = Script("scripts/check-role-names.py");
        script.Language.Should().Be(InfrastructureScriptLanguage.Python);
        script.Relevance.Should().Be(InfrastructureScriptRelevance.Strong);
        script.PrimaryPurpose.Should().Be(InfrastructureScriptPurpose.NamingConventionCheck);
        script.Enforcement.Should().Be(InfrastructureCheckEnforcement.FailsOnViolation);
        script.ValidationRules.Should().Contain("resource name must have at least two segments");
        script.RulesState.Should().Be("Extracted");
        script.EntryPoint.Should().Be("__main__ guard");
        script.Functions.Should().Contain(["check", "main"]);
        script.ExecutionState.Should().Be(InfrastructureAutomationText.NotExecuted);
        var check = I.Checks.Single(c => c.Implementations.Contains(script.Id));
        check.Name.Should().Be("RBAC naming validation");
        check.Scope.Should().Be("azurerm_role_assignment declarations");
        check.MatchingDeclarations.Should().Be(2);
        check.ImplementationState.Should().Be(InfrastructureAutomationText.CheckImplemented);
        check.ExecutionState.Should().Be("Not executed by analysis");
        I.ScriptRelationships.Should().ContainSingle(r => r.ScriptId == script.Id && r.Relation == "Validates role assignment declarations" && r.Matches == 2);
    }

    [Fact]
    public void Environment_variable_names_are_captured_without_values_and_sensitive_names_are_flagged()
    {
        var env = Script("scripts/check-role-names.py").EnvironmentVariables;
        env.Should().ContainSingle(e => e.Name == "CLIENT_SECRET").Which.Sensitive.Should().BeTrue();
        env.Should().ContainSingle(e => e.Name == "TF_ROOT").Which.Sensitive.Should().BeFalse();
        Script("scripts/validate.ps1").EnvironmentVariables.Should().ContainSingle(e => e.Name == "ARM_CLIENT_SECRET" && e.Sensitive);
        var json = JsonSerializer.Serialize(Contoso.Value);
        json.Should().NotContain(Sentinel, "script constants, Terraform state and environment values never leave the analysis");
    }

    [Fact]
    public void A_shell_command_reference_is_detected_and_classified_without_running_it()
    {
        var script = Script("scripts/fmt-check.sh");
        script.Commands.Should().ContainSingle(c => c.Command == "terraform" && c.Subcommand == "fmt -check -recursive" && c.ExternalTool);
        script.PrimaryPurpose.Should().Be(InfrastructureScriptPurpose.Validation);
        script.Enforcement.Should().Be(InfrastructureCheckEnforcement.FailsOnViolation);
        script.ExecutionState.Should().Be("Not executed by analysis");
        script.Commands.Select(c => c.Command).Should().NotContain(["then", "fi", "echo", "exit"], "keywords and builtins are not external commands");
    }

    [Fact]
    public void A_version_report_reads_lock_files_and_notes_network_access_without_contacting_it()
    {
        var script = Script("scripts/check-provider-versions.py");
        script.PrimaryPurpose.Should().Be(InfrastructureScriptPurpose.VersionCheck);
        script.Enforcement.Should().Be(InfrastructureCheckEnforcement.ReportOnly);
        script.Inputs.Should().Contain(i => i.Kind == "File pattern" && i.Value == "terraform/layers/*/.terraform.lock.hcl");
        script.Capabilities.Should().Contain(c => c.Capability == "May access network" && c.Detail.Contains("registry.terraform.io") && c.Detail.Contains("not contacted"));
        script.CliArguments.Should().Contain("positional arguments (sys.argv)");
        I.Checks.Single(c => c.Implementations.Contains(script.Id)).Name.Should().Be("Provider version report");
        I.ScriptRelationships.Should().Contain(r => r.ScriptId == script.Id && r.Relation == "Reads provider lock files" && r.Matches == 1);
    }

    [Fact]
    public void A_generated_artifact_is_a_definition_and_never_claimed_to_exist()
    {
        var script = Script("scripts/generate-rbac-inventory.py");
        script.PrimaryPurpose.Should().Be(InfrastructureScriptPurpose.InventoryGeneration);
        var artifact = I.GeneratedArtifacts.Single(a => a.GeneratedBy == script.Id);
        artifact.PathTemplate.Should().Be("docs/rbac-inventory.json");
        artifact.Kind.Should().Be("Inventory");
        artifact.PresentInSource.Should().BeFalse();
        artifact.PresenceNote.Should().Be("Not present in this source package.");
        var withCopy = SourceEvidenceFixtures.Analyze(Repository(("docs/rbac-inventory.json", "{}"))).Infrastructure;
        withCopy.GeneratedArtifacts.Single(a => a.GeneratedBy == script.Id).PresenceNote.Should().Contain("checked-in copy").And.Contain("not produced by this analysis");
    }

    [Fact]
    public void Dynamic_execution_makes_the_analysis_partial_and_is_never_run()
    {
        var script = Script("scripts/dynamic-rules.py");
        script.DynamicExecution.Should().BeTrue();
        script.AnalysisState.Should().Be(InfrastructureStaticAnalysisState.Partial);
        script.Capabilities.Should().Contain(c => c.Capability == "Dynamic execution detected");
        I.Status.Should().Be(SourceDomainStatus.Partial);
    }

    [Fact]
    public void PowerShell_is_read_statically_for_commands_messages_and_exit_status()
    {
        var script = Script("scripts/validate.ps1");
        script.Language.Should().Be(InfrastructureScriptLanguage.PowerShell);
        script.Commands.Should().Contain(c => c.Command == "terraform" && c.Subcommand == "validate");
        script.PrimaryPurpose.Should().Be(InfrastructureScriptPurpose.Validation);
        script.Enforcement.Should().Be(InfrastructureCheckEnforcement.FailsOnViolation);
        script.CliArguments.Should().Contain("-Root");
        script.EntryPoint.Should().Be("param() block");
    }

    [Fact]
    public void Scripts_that_are_not_infrastructure_automation_are_listed_apart_with_the_reason()
    {
        I.OtherScripts.Should().ContainSingle(o => o.Path == "tools/build_app.py").Which.Reason.Should().Contain("No infrastructure source");
        I.OtherScriptCount.Should().Be(1);
        I.ScriptInvocations.Should().NotContain(i => i.ScriptId == "tools/build_app.py", "a pipeline that only builds an application gives no infrastructure context");
    }

    [Fact]
    public void Pipeline_and_hook_invocations_are_configuration_never_execution_or_a_pass()
    {
        var report = I.ScriptInvocations.Single(i => i.ScriptId == "scripts/check-provider-versions.py");
        report.Source.Should().Be("Pipeline");
        report.Blocking.Should().Be(InfrastructureInvocationBlocking.FailureTolerated, "python3 … || echo tolerates the script's failure");
        report.Trigger.Should().Contain("schedule");
        report.ExecutionState.Should().Be(InfrastructureAutomationText.ConfiguredNotAssessed);
        var gate = I.ScriptInvocations.Single(i => i.ScriptId == "scripts/fmt-check.sh");
        gate.Blocking.Should().Be(InfrastructureInvocationBlocking.Gate);
        gate.Step.Should().Be("Format check");
        var hook = I.ScriptInvocations.Single(i => i.ScriptId == "scripts/check-role-names.py");
        hook.Source.Should().Be("Pre-commit hook");
        hook.Trigger.Should().Be(@"on commit of files matching \.tf$");
        var gates = InfrastructureEvidenceQueries.PipelineGates(I).ToList();
        gates.Should().ContainSingle().Which.Check.Name.Should().Be("Terraform formatting validation");
        var steps = Contoso.Value.CiCd.Pipelines.SelectMany(p => p.Steps).Where(s => s.Kind == PipelineStepKind.LocalScript).ToList();
        steps.Should().Contain(s => s.Script!.Path == "scripts/fmt-check.sh" && s.Script.ResolvedInSnapshot && !s.Script.FailureTolerated);
        steps.Should().Contain(s => s.Script!.Path == "tools/build_app.py" && s.Script.Interpreter == "python");
        steps.Should().OnlyContain(s => s.ExecutionState == SourceDomainText.StepNotExecuted);
    }

    [Fact]
    public void Pipeline_review_describes_a_script_step_by_the_infrastructure_check_it_runs_never_as_passed()
    {
        var review = BirkNext.Api.Services.PipelineReview.PipelineReviewBuilder.Build(BirkNext.Api.Tests.Services.PipelineReview.PipelineReviewFixtures.Snapshot(Repository()));
        var fmt = review.Tests.Single(t => t.Script == "scripts/fmt-check.sh");
        fmt.Category.Should().Be(BirkNext.PipelineReview.ValidationCategory.InfrastructureCheck);
        fmt.InfrastructureChecks.Should().Equal("Terraform formatting validation");
        fmt.ScriptGate.Should().Be("Configured as gate");
        var report = review.Tests.Single(t => t.Script == "scripts/check-provider-versions.py");
        report.ScriptGate.Should().Be("Failure tolerated");
        report.ScriptPurpose.Should().Be("Version check · Report only (exits 0)");
        review.Tests.Should().NotContain(t => t.Tool == "python script" && t.Script == null, "an application script is not an infrastructure check");
    }

    [Fact]
    public void Technology_coverage_distinguishes_static_script_analysis_from_execution()
    {
        var (workspace, _) = BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceArchiveReader.Read("contoso.zip", SourceEvidenceFixtures.Zip(Repository()));
        var coverage = BirkNext.Api.Services.SourceAnalysis.Technology.TechnologyInventory.Detect(workspace!, null, Contoso.Value);
        coverage.Technologies.Select(x => x.TechnologyId).Should().Contain(["iac.terraform", "iac.scripts.python", "iac.scripts.shell", "iac.scripts.powershell"]);
        var provider = BirkNext.Technology.TechnologySupportRegistry.Providers.Single(p => p.ProviderId == "source.iac.scripts");
        provider.Description.Should().Contain("Script execution: not performed");
        BirkNext.Technology.TechnologySupportRegistry.Find("iac.scripts.powershell")!.SourceAnalysis.Should().Be(BirkNext.Technology.SupportLevel.Partial);
    }

    [Fact]
    public void Iqr_shows_declaration_source_validation_and_runtime_as_separate_layers()
    {
        var snapshot = BirkNext.Api.Tests.Services.PipelineReview.PipelineReviewFixtures.Snapshot(Repository());
        var (observed, missing) = BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceDomainsReview.Domain(BirkNext.Integrations.IntegrationReviewDomain.Security, [snapshot]);
        observed.Should().Contain(l => l.StartsWith("Security declarations in IaC"));
        observed.Should().Contain(l => l.StartsWith("Source validation logic") && l.Contains("RBAC naming validation") && l.Contains("pre-commit hook"));
        missing.Should().Contain(BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceDomainsReview.ValidationNotAssessed);
        missing.Should().Contain(BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceDomainsReview.RuntimeLimitation);
        observed.Should().NotContain(l => l.Contains("passed", StringComparison.OrdinalIgnoreCase) || l.Contains("Validated", StringComparison.Ordinal));
    }

    [Fact]
    public void Shared_queries_answer_consumer_questions_from_stored_evidence()
    {
        InfrastructureEvidenceQueries.FindResourceByIdentifier(I, "orders")!.ResourceType.Should().Be("azurerm_servicebus_topic");
        InfrastructureEvidenceQueries.FindResourceByIdentifier(I, "azurerm_servicebus_topic.orders").Should().NotBeNull();
        InfrastructureEvidenceQueries.FindChecksForResourceType(I, "azurerm_role_assignment").Should().ContainSingle(c => c.Name == "RBAC naming validation");
        var layers = InfrastructureEvidenceQueries.Layers(I, "azurerm_role_assignment");
        layers.DeclaredInTerraform.Should().BeTrue();
        layers.ValidationLogicAvailable.Should().BeTrue();
        layers.ReferencedByPipeline.Should().BeFalse("only a pre-commit hook runs it");
        layers.HookReferences.Should().ContainSingle();
        layers.Execution.Should().Be("Not assessed");
        layers.Runtime.Should().Be("Not assessed");
        InfrastructureEvidenceQueries.FindScriptsUsedByPipeline(I, "azure-pipelines.yml").Select(x => x.Script.Id).Should().BeEquivalentTo(["scripts/check-provider-versions.py", "scripts/fmt-check.sh"]);
        InfrastructureEvidenceQueries.ForPipelineScript(I, "./scripts/fmt-check.sh")!.Value.Checks.Should().ContainSingle();
    }

    [Fact]
    public void Script_constants_never_become_configuration_values()
    {
        var snapshot = SourceEvidenceFixtures.Analyze(out var configuration, Repository(("scripts/names.sh", "#!/bin/bash\nNAMESPACE=\"sb-contoso-prod\"\nterraform plan -var \"namespace=$NAMESPACE\" terraform/\n")));
        snapshot.Infrastructure.Scripts.Should().Contain(s => s.Path == "scripts/names.sh");
        snapshot.Configuration.Entries.Should().NotContain(e => e.File.StartsWith("scripts/"));
        configuration.Values.Should().NotContain(v => v.File.StartsWith("scripts/"));
        snapshot.ConfigurationValues?.Values.Should().NotContain(v => v.Artifact.StartsWith("scripts/") || (v.Value ?? "").Contains("sb-contoso-prod"));
    }

    [Fact]
    public void Application_only_source_has_no_infrastructure_evidence_and_that_is_not_a_defect()
    {
        var app = SourceEvidenceFixtures.Analyze(("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"), ("tools/hello.py", "print('hi')\n")).Infrastructure;
        app.Status.Should().Be(SourceDomainStatus.NotDetected);
        app.StatusReason.Should().Contain("Infrastructure may live in another repository");
        app.OtherScripts.Should().ContainSingle(o => o.Path == "tools/hello.py");
    }

    [Fact]
    public void Automation_changes_are_source_changes_compared_only_between_snapshots_that_had_the_script_provider()
    {
        var before = Contoso.Value;
        var after = SourceEvidenceFixtures.Analyze(Repository()
            .Where(f => f.Item1 != "scripts/fmt-check.sh")
            .Select(f => f.Item1 == "scripts/check-role-names.py" ? (f.Item1, f.Item2.Replace("must have at least two segments", "must have at least three segments")) : f).ToArray());
        var changes = SourceEvidenceDiff.Compare(before, after);
        changes.Should().Contain(c => c.Area == "Infrastructure script" && c.Kind == SourceEvidenceChangeKind.Removed && c.Key == "scripts/fmt-check.sh");
        changes.Should().Contain(c => c.Area == "Validation check" && c.Kind == SourceEvidenceChangeKind.Changed && c.Detail.Contains("three segments"));
        changes.Should().Contain(c => c.Area == "Script invocation" && c.Kind == SourceEvidenceChangeKind.Removed);
        var historical = before with { Infrastructure = before.Infrastructure with { AnalyzerVersion = 3, Scripts = [], Checks = [], ScriptInvocations = [], GeneratedArtifacts = [] } };
        SourceEvidenceDiff.Compare(historical, after).Should().NotContain(c => c.Area == "Infrastructure script" || c.Area == "Validation check",
            "a snapshot analyzed before the script provider never read its scripts — they are not 'added'");
        historical.Infrastructure.AutomationAnalyzed.Should().BeFalse();
    }

    [Fact]
    public void Dependency_review_and_project_import_show_automation_apart_from_packages_and_iac_formats()
    {
        var snapshot = BirkNext.Api.Tests.Services.PipelineReview.PipelineReviewFixtures.Snapshot(Repository());
        BirkNext.Api.Services.DependencyReview.DependencyReviewSourceScopeService.IacAutomation(snapshot).Should()
            .Contain("separate from package health").And.Contain("1 locked Terraform provider(s)").And.Contain("1 external module(s)").And.Contain("terraform");
        BirkNext.Api.Services.IntegrationJourneys.TestPackSources.InfrastructureAutomation(snapshot).Should().StartWith("Python, Shell, PowerShell scripts · ").And.EndWith("source check(s) · read statically, not executed");
        BirkNext.Api.Services.IntegrationJourneys.TestPackSources.InfrastructureFormats(snapshot).Should().Equal("Terraform");
    }

    [Fact]
    public void Documentation_generator_scripts_join_generated_documentation_evidence_without_expected_outputs()
    {
        const string docs = """
            #!/bin/bash
            for d in terraform/layers/*/; do
              terraform-docs markdown table --output-file README.md --output-mode inject "$d"
            done
            """;
        var snapshot = BirkNext.Api.Tests.Services.PipelineReview.PipelineReviewFixtures.Snapshot(Repository(("scripts/tf-docs.sh", docs)));
        snapshot.EvidenceDomains!.Infrastructure.Scripts.Single(s => s.Path == "scripts/tf-docs.sh").PrimaryPurpose.Should().Be(InfrastructureScriptPurpose.DocumentationGeneration);
        var generators = BirkNext.Api.Services.SourceAnalysis.GeneratedDocumentation.GeneratedDocumentationAnalyzer.WithInfrastructureGenerators([], snapshot);
        var generator = generators.Should().ContainSingle(g => g.Path == "scripts/tf-docs.sh").Subject;
        generator.GeneratorName.Should().Be("terraform-docs");
        generator.Type.Should().Be(BirkNext.GeneratedDocumentation.GeneratorType.Script);
        generator.DeclaredOutputDirectories.Should().BeEmpty();
        generator.Basis.Should().Contain("not executed");
    }

    [Fact]
    public void Traceability_uses_script_logic_as_automation_implementation_evidence_without_a_pass_claim()
    {
        var naming = InfrastructureTraceability.For(I, "RBAC naming must be validated automatically.");
        naming.Should().ContainSingle().Which.Check.Name.Should().Be("RBAC naming validation");
        naming[0].Declarations.Should().Be(2);
        naming[0].HookReferences.Should().ContainSingle();
        naming[0].Implementation.Should().Contain("check implemented in source");
        naming[0].Execution.Should().Be("Not assessed");
        InfrastructureTraceability.For(I, "Provider/module versions must be controlled.").Select(x => x.Check.Name).Should().Contain("Provider version report");
        InfrastructureTraceability.For(I, "Resources shall have clear names.").Should().BeEmpty("a generic word is never enough: the requirement must name the check's subject");
    }

    [Fact]
    public void Integration_topology_never_turns_utility_scripts_into_nodes()
    {
        var api = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../BirkNext.Api/Services/IntegrationTopology"));
        Directory.GetFiles(api, "*.cs").Where(f => Regex.IsMatch(File.ReadAllText(f), @"\.Scripts\b|ScriptInvocations|GeneratedArtifacts"))
            .Should().BeEmpty("topology nodes come from components, interfaces and declared resources; scripts may only annotate a declaration");
    }

    [Fact]
    public void No_consumer_reparses_scripts_and_no_provider_can_execute_anything()
    {
        var api = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../BirkNext.Api"));
        var files = Directory.GetFiles(api, "*.cs", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(api, f).Replace('\\', '/'))).Where(f => !f.Rel.StartsWith("obj/") && !f.Rel.StartsWith("bin/")).ToList();
        files.Where(f => !f.Rel.StartsWith("Services/SourceAnalysis/Evidence/") && Regex.IsMatch(File.ReadAllText(f.Full), @"\b(PythonStaticReader|ShellStaticReader|PowerShellStaticReader)\b"))
            .Select(f => f.Rel).Should().BeEmpty("scripts are parsed once by Source Analysis; consumers query stored evidence");
        files.Where(f => f.Rel.StartsWith("Services/SourceAnalysis/Evidence/") && Regex.IsMatch(File.ReadAllText(f.Full), @"\bProcess\.Start\(|new ProcessStartInfo|new HttpClient|HttpClient\.|WebRequest\.Create|Dns\.Get"))
            .Select(f => f.Rel).Should().BeEmpty("infrastructure evidence providers never execute, download or contact anything");
    }
}
