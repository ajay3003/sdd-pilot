using BirkNext.SourceDomains;
using BirkNext.Web.Components.SourceDomains;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Source Analysis → Infrastructure exposes Terraform and infrastructure automation together: overview counts, an Automation &amp; checks view
/// (check, type, script, scope, pipeline usage, execution evidence), remote-state layer dependencies and conditional declarations. Source
/// logic is never shown as executed or passed, and a snapshot analyzed before the script provider says so instead of showing zeros.
/// </summary>
public sealed class InfrastructureAutomationUiTests : BunitContext
{
    private static readonly InfrastructureScript Naming = new()
    {
        Id = "scripts/check-rbac-naming.py", Path = "scripts/check-rbac-naming.py", Language = InfrastructureScriptLanguage.Python, Relevance = InfrastructureScriptRelevance.Strong,
        RelevanceBasis = ["References Terraform resource types (azurerm_role_assignment)"], PrimaryPurpose = InfrastructureScriptPurpose.NamingConventionCheck,
        Purposes = [InfrastructureScriptPurpose.NamingConventionCheck], PurposeBasis = ["Reports naming/convention problems (2 message(s))"],
        ValidationRules = ["resource name must have ≥2 segments"], RulesState = "Extracted", Enforcement = InfrastructureCheckEnforcement.FailsOnViolation, EnforcementBasis = "Exit status is computed from its findings",
        TerraformConcepts = ["azurerm_role_assignment", "*.tf"], EnvironmentVariables = [new("CLIENT_SECRET", true, 9)],
    };

    private static readonly InfrastructureScript Report = new()
    {
        Id = "scripts/check-provider-versions.py", Path = "scripts/check-provider-versions.py", Language = InfrastructureScriptLanguage.Python, Relevance = InfrastructureScriptRelevance.Strong,
        PrimaryPurpose = InfrastructureScriptPurpose.VersionCheck, Purposes = [InfrastructureScriptPurpose.VersionCheck], Enforcement = InfrastructureCheckEnforcement.ReportOnly,
        Capabilities = [new("May access network", "URL host registry.terraform.io (not contacted)", 20)],
    };

    private static readonly InfrastructureScript Inventory = new()
    {
        Id = "scripts/generate-rbac-inventory.py", Path = "scripts/generate-rbac-inventory.py", Language = InfrastructureScriptLanguage.Python, Relevance = InfrastructureScriptRelevance.Strong,
        PrimaryPurpose = InfrastructureScriptPurpose.InventoryGeneration, Purposes = [InfrastructureScriptPurpose.InventoryGeneration],
        Outputs = [new("File write", "wiki/reference/rbac-inventory.md", 30)], TerraformConcepts = ["azurerm_role_assignment"],
    };

    private static SourceEvidenceDomainsSnapshot Evidence(int version = 4)
    {
        var e = SourceEvidenceDomainsUiTests.Evidence();
        var i = e.Infrastructure;
        return e with
        {
            Infrastructure = i with
            {
                AnalyzerVersion = version,
                Resources = [.. i.Resources, new() { Id = "infra/azurerm_servicebus_queue.dlq", Provider = "azurerm", ResourceType = "azurerm_servicebus_queue", LogicalName = "dlq",
                    Category = InfrastructureCategory.Messaging, CategoryDetail = "Service Bus queue", File = "infra/messaging.tf", Line = 20, Conditional = true, InstanceExpression = "count = var.enable_dlq ? 1 : 0" }],
                Scripts = version >= 4 ? [Naming, Report, Inventory] : [],
                Checks = version >= 4 ?
                [
                    new() { Id = "check:rbac-naming-validation", Name = "RBAC naming validation", Type = InfrastructureScriptPurpose.NamingConventionCheck, Scope = "azurerm_role_assignment declarations",
                        Subjects = ["azurerm_role_assignment"], Implementations = [Naming.Id], Rules = ["resource name must have ≥2 segments"], RulesState = "Extracted",
                        Enforcement = InfrastructureCheckEnforcement.FailsOnViolation, MatchingDeclarations = 20 },
                    new() { Id = "check:provider-version-report", Name = "Provider version report", Type = InfrastructureScriptPurpose.VersionCheck, Scope = "provider lock files",
                        Implementations = [Report.Id], RulesState = "Not applicable", Enforcement = InfrastructureCheckEnforcement.ReportOnly, MatchingDeclarations = 7 },
                ] : [],
                ScriptInvocations = version >= 4 ?
                [
                    new() { ScriptId = Report.Id, Source = "Pipeline", Reference = "pipelines/version-check.yml", Pipeline = "version-check", Step = "Provider report",
                        Blocking = InfrastructureInvocationBlocking.FailureTolerated, BlockingBasis = "The step continues when the script fails (|| …).", Trigger = "schedule 0 6 * * 1" },
                    new() { ScriptId = Naming.Id, Source = "Pre-commit hook", Reference = "check-rbac-naming", Blocking = InfrastructureInvocationBlocking.Gate,
                        BlockingBasis = "pre-commit blocks the commit when the hook exits non-zero (developer machines; not CI).", Trigger = @"on commit of files matching \.tf$" },
                ] : [],
                GeneratedArtifacts = version >= 4 ? [new() { Id = "a", Kind = "Inventory", Name = "rbac-inventory.md", PathTemplate = "wiki/reference/rbac-inventory.md", GeneratedBy = Inventory.Id,
                    PresentInSource = true, PresenceNote = "A checked-in copy exists in this source package; it was not produced by this analysis." }] : [],
                ScriptRelationships = version >= 4 ? [new(Naming.Id, "Validates role assignment declarations", "azurerm_role_assignment", "Resource type", 20, "pattern")] : [],
                RemoteStates = [new() { Id = "layers/02/data.terraform_remote_state.network", Name = "network", UsedBy = "layers/02-shared", SourceLayer = "layers/01-network",
                    Resolution = "Strong source match", ResolutionBasis = "state container_name equals the layer directory name; not verified", ReferencedOutputs = ["vnet_id", "gone"], OutputsNotDeclared = ["gone"], File = "layers/02-shared/data.tf", Line = 1 }],
                ProviderRuns = version >= 4 ? [new("terraform", "Terraform evidence provider", 4, SourceDomainStatus.Complete, 3, "2 .tf, 1 lock file(s)."),
                    new("infrastructure-scripts", "Infrastructure script evidence provider", 1, SourceDomainStatus.Complete, 4, "4 script(s) read statically; 3 infrastructure automation, 1 other. None executed.")] : [],
                ExcludedFiles = [new("layers/02-shared/terraform.tfstate", InfrastructureAutomationText.StateExcluded)],
                OtherScripts = version >= 4 ? [new("tools/build.py", InfrastructureScriptLanguage.Python, "No infrastructure source, Terraform/Azure command or infrastructure pipeline/hook context in its content.")] : [],
                OtherScriptCount = version >= 4 ? 1 : 0,
            },
        };
    }

    [Fact]
    public void Overview_counts_scripts_checks_artifacts_and_remote_state_beside_the_declarations()
    {
        var cut = Render<InfrastructureWorkspace>(p => p.Add(x => x.Evidence, Evidence()));
        cut.FindAll("[data-testid=sd-infra-overview] dt").Select(d => d.TextContent).Should().Equal("Resources", "Resource types", "Bindings", "Environment references", "Needs review");
        cut.FindAll("[data-testid=sd-infra-automation-overview] > div").Select(d => d.TextContent).Should()
            .Equal("Infrastructure scripts3", "Source validation checks2", "Generated artifact definitions1", "Remote-state dependencies1");
        cut.Find("[data-testid=sd-infra-automation-summary]").TextContent.Should().Contain("never run").And.Contain("not executed or passed");
        cut.Find("[data-testid=sd-infra-review-automation]").Click();
        cut.Find("[data-testid=sd-infra-automation]").Should().NotBeNull();
    }

    [Fact]
    public void Automation_and_checks_lists_check_type_script_scope_pipeline_usage_and_execution_evidence()
    {
        var cut = Render<InfrastructureWorkspace>(p => p.Add(x => x.Evidence, Evidence()));
        cut.Find("[data-testid=sd-infra-tab-automation]").Click();
        cut.FindAll("[data-testid=sd-infra-automation-table] thead th").Select(h => h.TextContent).Should()
            .Equal("Check / automation", "Type", "Script", "Scope", "Pipeline usage", "Execution evidence");
        var rows = cut.FindAll("[data-testid=sd-infra-automation-row]");
        rows.Select(r => r.GetAttribute("data-kind")).Should().Equal("check", "check", "automation");
        rows[0].TextContent.Should().Contain("Provider version report").And.Contain("Report only");
        rows[0].QuerySelector("[data-testid=sd-infra-automation-usage]")!.TextContent.Should().Be("Pipeline version-check (failure tolerated)");
        rows[0].QuerySelector("[data-testid=sd-infra-automation-execution]")!.TextContent.Should().Be("Referenced by pipeline · not executed by analysis");
        rows[1].TextContent.Should().Contain("RBAC naming validation").And.Contain("20 matching declarations in this source");
        rows[1].QuerySelector("[data-testid=sd-infra-automation-usage]")!.TextContent.Should().Contain("Pre-commit hook check-rbac-naming (commit gate on developer machines)");
        rows[1].QuerySelector("[data-testid=sd-infra-automation-execution]")!.TextContent.Should().Be("Not executed by analysis");
        rows[2].TextContent.Should().Contain("RBAC inventory generation").And.Contain("Inventory generation");
        cut.Find("[data-testid=sd-infra-automation]").TextContent.Should().NotContain("Passed").And.NotContain("Validated ", "logic that exists is never shown as validated or passed");
    }

    [Fact]
    public void A_check_detail_shows_rules_exit_behaviour_relationships_and_protects_sensitive_names()
    {
        var cut = Render<InfrastructureWorkspace>(p => p.Add(x => x.Evidence, Evidence()));
        cut.Find("[data-testid=sd-infra-tab-automation]").Click();
        var toggle = cut.FindAll("[data-testid=sd-infra-automation-toggle]")[1];
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.Click();
        cut.FindAll("[data-testid=sd-infra-automation-toggle]")[1].GetAttribute("aria-expanded").Should().Be("true");
        var detail = cut.Find("[data-testid=sd-infra-automation-detail]");
        detail.QuerySelector("[data-testid=sd-infra-check-rules]")!.TextContent.Should().Contain("resource name must have ≥2 segments");
        detail.QuerySelector("[data-testid=sd-infra-check-facts]")!.TextContent.Should().Contain("Check implemented in source").And.Contain("Not executed by analysis").And.Contain("Fails on violation");
        detail.QuerySelector("[data-testid=sd-infra-script-relations]")!.TextContent.Should().Contain("Validates role assignment declarations").And.Contain("(20 in this source)");
        detail.TextContent.Should().Contain("CLIENT_SECRET").And.Contain("sensitive · value never read");
        detail.QuerySelector("[data-testid=sd-infra-script-invocation]")!.TextContent.Should().Contain("Configured as gate").And.Contain("not CI").And.Contain("execution not assessed");
    }

    [Fact]
    public void Generated_artifacts_and_other_scripts_are_listed_without_claiming_generation()
    {
        var cut = Render<InfrastructureWorkspace>(p => p.Add(x => x.Evidence, Evidence()));
        cut.Find("[data-testid=sd-infra-tab-automation]").Click();
        cut.Find("[data-testid=sd-infra-artifact]").TextContent.Should().Contain("rbac-inventory.md").And.Contain("not produced by this analysis");
        cut.Find("[data-testid=sd-infra-artifacts]").TextContent.Should().Contain("A definition is not a generated file");
        cut.Find("[data-testid=sd-infra-other-scripts-toggle]").Click();
        cut.Find("[data-testid=sd-infra-other-scripts-body]").TextContent.Should().Contain("tools/build.py").And.Contain("No infrastructure source");
    }

    [Fact]
    public void Remote_state_conditional_declarations_providers_and_excluded_state_are_visible()
    {
        var cut = Render<InfrastructureWorkspace>(p => p.Add(x => x.Evidence, Evidence()));
        cut.Find("[data-testid=sd-infra-tab-dependencies]").Click();
        var remote = cut.Find("[data-testid=sd-infra-remote-state]");
        remote.GetAttribute("data-resolution").Should().Be("Strong source match");
        remote.TextContent.Should().Contain("layers/01-network").And.Contain("vnet_id");
        cut.Find("[data-testid=sd-infra-remote-missing]").TextContent.Should().Contain("gone");
        cut.Find("[data-testid=sd-infra-tab-resources]").Click();
        cut.Find("[data-testid=sd-infra-conditional]").TextContent.Should().Be("Conditional declaration");
        cut.Find("[data-testid=sd-infra-tab-evidence]").Click();
        cut.FindAll("[data-testid=sd-infra-provider-run]").Select(r => r.GetAttribute("data-provider")).Should().Equal("terraform", "infrastructure-scripts");
        cut.Find("[data-testid=sd-infra-excluded]").TextContent.Should().Contain("terraform.tfstate").And.Contain("Sensitive Terraform state intentionally excluded.");
    }

    [Fact]
    public void A_snapshot_analyzed_before_the_script_provider_says_so_instead_of_showing_zero()
    {
        var cut = Render<InfrastructureWorkspace>(p => p.Add(x => x.Evidence, Evidence(version: 3)));
        cut.Find("[data-testid=sd-infra-automation-version]").TextContent.Should().Contain(InfrastructureAutomationText.NotAvailableInVersion);
        cut.FindAll("[data-testid=sd-infra-automation-overview]").Should().BeEmpty();
        cut.Find("[data-testid=sd-infra-tab-automation]").Click();
        cut.Find("[data-testid=sd-infra-automation]").TextContent.Should().Contain(InfrastructureAutomationText.NotAvailableInVersion);
        cut.FindAll("[data-testid=sd-infra-automation-row]").Should().BeEmpty();
    }

    [Fact]
    public void Presentation_summary_never_claims_execution()
    {
        var i = Evidence().Infrastructure;
        InfrastructureAutomationPresentation.Summary(i).Should().Be("3 infrastructure script(s) · 2 source check(s) (1 referenced by a pipeline) · execution not assessed");
        InfrastructureAutomationPresentation.Summary(Evidence(3).Infrastructure).Should().Be(InfrastructureAutomationText.NotAvailableInVersion);
        InfrastructureAutomationPresentation.Title(Inventory).Should().Be("RBAC inventory generation");
    }
}
