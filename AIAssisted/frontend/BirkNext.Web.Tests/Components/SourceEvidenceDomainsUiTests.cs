using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using BirkNext.Web.Components.SourceDomains;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ConfigurationEntry = BirkNext.SourceDomains.ConfigurationEntry;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Source Analysis → source-evidence domains in the UI: compact landing rows (no giant cards), grouped area navigation, summary-first domain
/// pages with drill-down, honest Not detected / Not analyzed states, the source boundary on every page, secret values never rendered, and
/// source changes labelled as source changes. Generic fixture — no project-specific names.
/// </summary>
public sealed class SourceEvidenceDomainsUiTests : BunitContext
{
    private readonly Mock<IIntegrationCatalogApiService> _api = new();
    private List<IqrSourceSnapshot> _snapshots = [];

    public SourceEvidenceDomainsUiTests()
    {
        _api.Setup(a => a.ListSourceSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _snapshots.ToList());
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev" } });
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(context.Object);
        Services.AddSingleton<IReportExportService, ReportExportService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static readonly Guid SnapshotId = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private const string Fp = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    private static T Env<T>(T r, SourceEvidenceDomain d, SourceDomainStatus status, params string[] tech) where T : SourceDomainResult =>
        r with { Domain = d, Status = status, SourceSnapshotId = SnapshotId, SourceFingerprint = Fp, AnalyzerVersion = 1, ExtractedAt = DateTimeOffset.Parse("2026-10-02T10:00:00Z"), Technologies = [.. tech],
            Limitations = [SourceDomainText.SourceBoundary] };

    internal static SourceEvidenceDomainsSnapshot Evidence(bool infrastructure = true) => new()
    {
        SourceSnapshotId = SnapshotId, SourceFingerprint = Fp, ExtractedAt = DateTimeOffset.Parse("2026-10-02T10:00:00Z"),
        Capabilities = [new(SourceEvidenceDomain.Infrastructure, "Terraform", DomainSupport.Supported, "HCL"), new(SourceEvidenceDomain.Infrastructure, "Bicep", DomainSupport.Partial, "pattern"),
            new(SourceEvidenceDomain.CiCd, "Azure Pipelines", DomainSupport.Partial, "subset")],
        Infrastructure = infrastructure
            ? Env(new InfrastructureEvidence
            {
                Formats = [InfrastructureFormat.Terraform],
                Resources =
                [
                    new() { Id = "infra/azurerm_servicebus_topic.orders", Provider = "azurerm", ResourceType = "azurerm_servicebus_topic", LogicalName = "orders", DeclaredName = "orders-created",
                        Category = InfrastructureCategory.Messaging, CategoryDetail = "Service Bus topic", File = "infra/messaging.tf", Line = 3, Settings = [new("max_delivery_count", "10", "Messaging", true)] },
                    new() { Id = "infra/azurerm_linux_web_app.api", Provider = "azurerm", ResourceType = "azurerm_linux_web_app", LogicalName = "api", DeclaredName = "app-shop-api",
                        Category = InfrastructureCategory.Compute, CategoryDetail = "App Service (Linux)", File = "infra/main.tf", Line = 10,
                        Settings = [new("public_network_access_enabled", "false", "Security", true), new("administrator_password", "[sensitive — value not shown]", "Security", false)] },
                    new() { Id = "infra/azurerm_private_endpoint.db", Provider = "azurerm", ResourceType = "azurerm_private_endpoint", LogicalName = "db", Category = InfrastructureCategory.Networking, CategoryDetail = "Private endpoint", File = "infra/main.tf", Line = 30 },
                ],
                Dependencies = [new("infra/azurerm_private_endpoint.db", "infra/azurerm_linux_web_app.api", "reference", ArchitectureEvidenceState.Confirmed, "infra/main.tf", 31)],
                AccessAssignments = [new AccessAssignment { ResourceId = "infra/azurerm_role_assignment.a", Role = "Azure Service Bus Data Sender", ScopeReference = "infra/azurerm_servicebus_topic.orders", PrincipalReference = "(principal)", EvidenceState = ArchitectureEvidenceState.Confirmed }],
                Variables = [new InfrastructureVariable { Name = "db_password", DefaultState = "redacted", Sensitive = true, File = "infra/main.tf", Line = 2,
                    Values = [new(new SourceEnvironmentLabel(SourceEnvironmentKind.QA, "qa"), "infra/qa.tfvars", 1, "redacted", null)] }],
                Modules = [new InfrastructureModule { Id = "infra/module.naming", Name = "naming", Source = "Azure/naming/azurerm", Note = "External module not analyzed — module sources are never fetched." }],
                Environments = [new(new SourceEnvironmentLabel(SourceEnvironmentKind.QA, "qa"), ["infra/qa.tfvars"], "tfvars file name")],
            }, SourceEvidenceDomain.Infrastructure, SourceDomainStatus.Partial, "Terraform")
            : Env(new InfrastructureEvidence { StatusReason = "No supported Infrastructure as Code files detected in the selected source. Infrastructure may live in another repository." }, SourceEvidenceDomain.Infrastructure, SourceDomainStatus.NotDetected),
        Configuration = Env(new ConfigurationEvidence
        {
            Files = [new ConfigurationFile { Path = "src/Api/appsettings.json", Format = "json", Technology = "ASP.NET Core appsettings", Entries = 3 }, new ConfigurationFile { Path = "src/Api/appsettings.QA.json", Format = "json", Technology = "ASP.NET Core appsettings", Environment = new(SourceEnvironmentKind.QA, "QA"), EnvironmentBasis = "file-name suffix", Entries = 1 }],
            Entries =
            [
                new ConfigurationEntry { Id = "e1", Key = "ServiceBus:Topic", NormalizedKey = "servicebus:topic", Category = ConfigurationCategory.Messaging, ValueKind = ConfigurationValueKind.EntityName, ValuePreviewSafe = "orders-created", File = "src/Api/appsettings.json", Line = 4, Technology = "ASP.NET Core appsettings", Component = "Api" },
                new ConfigurationEntry { Id = "e2", Key = "ConnectionStrings:Orders", NormalizedKey = "connectionstrings:orders", Category = ConfigurationCategory.Database, ValueKind = ConfigurationValueKind.ConnectionString, Sensitivity = ConfigurationSensitivity.Sensitive, File = "src/Api/appsettings.json", Line = 2, Technology = "ASP.NET Core appsettings" },
                new ConfigurationEntry { Id = "e3", Key = "Logging:LogLevel:Default", NormalizedKey = "logging:loglevel:default", Category = ConfigurationCategory.Observability, ValueKind = ConfigurationValueKind.Text, ValuePreviewSafe = "Information", File = "src/Api/appsettings.json", Line = 6, Technology = "ASP.NET Core appsettings" },
                new ConfigurationEntry { Id = "e4", Key = "Logging:LogLevel:Default", NormalizedKey = "logging:loglevel:default", Category = ConfigurationCategory.Observability, ValueKind = ConfigurationValueKind.Text, ValuePreviewSafe = "Debug", Environment = new(SourceEnvironmentKind.QA, "QA"), File = "src/Api/appsettings.QA.json", Line = 1, Technology = "ASP.NET Core appsettings" },
            ],
            Environments = [SourceEnvironmentLabel.Default, new(SourceEnvironmentKind.QA, "QA")],
            Conflicts = [new ConfigurationConflict("orders:topic", SourceEnvironmentLabel.Default, ["src/Api/.env", "src/Api/appsettings.json"], "Same key and environment with different values in these files (values not shown).")],
        }, SourceEvidenceDomain.Configuration, SourceDomainStatus.Complete, "ASP.NET Core appsettings"),
        CiCd = Env(new PipelineEvidence
        {
            Pipelines =
            [
                new PipelineDefinition { Id = "ci.yml", File = "ci.yml", Name = "ci", Platform = PipelinePlatform.AzurePipelines, Templates = ["templates/test.yml"],
                    Triggers = [new PipelineTrigger { Type = "push", BranchesInclude = ["main"], PathsInclude = ["src/*"] }],
                    Stages = [new PipelineStage("Deploy", null, [], true, "qa", 20)], Environments = ["qa"], SecretReferences = ["variable group: shop-secrets"],
                    Steps = [new PipelineStep { Id = "ci.yml#1", Name = "Build", Kind = PipelineStepKind.Build, Tool = "dotnet build" }, new PipelineStep { Id = "ci.yml#2", Name = "Deploy", Kind = PipelineStepKind.ApplicationDeploy, Tool = "Azure Web App" },
                        new PipelineStep { Id = "ci.yml#3", Name = "template templates/test.yml", Kind = PipelineStepKind.Template, Tool = "template" }] },
                new PipelineDefinition { Id = "templates/test.yml", File = "templates/test.yml", Name = "test", Platform = PipelinePlatform.AzurePipelines, IsTemplate = true, Templates = ["ci.yml"],
                    Steps = [new PipelineStep { Id = "templates/test.yml#1", Name = "Tests", Kind = PipelineStepKind.UnitTest, Tool = "dotnet test" }, new PipelineStep { Id = "templates/test.yml#2", Name = "Scan", Kind = PipelineStepKind.DependencyScan, Tool = "dependency scan" }] },
            ],
        }, SourceEvidenceDomain.CiCd, SourceDomainStatus.Partial, "Azure Pipelines"),
        Contracts = Env(new ContractEvidence
        {
            Contracts =
            [
                new SourceContract { Id = "openapi:api.yaml", Type = SourceContractType.OpenApi, Name = "Shop API", Version = "1.0", File = "src/Api/openapi.yaml", Line = 1, Producer = "Api", ProducerBasis = "Inside the component's project",
                    Operations = [new("GET /orders/{id}", "GET", "/orders/{id}", ["id"])], Types = [new("Order", "object", [new("id", "string", true), new("total", "number", false)])], ParseSupport = DomainSupport.Partial, EvidenceState = ArchitectureEvidenceState.Inferred },
                new SourceContract { Id = "message:OrderPlaced", Type = SourceContractType.MessageContract, Name = "OrderPlaced", File = "src/Domain/OrderPlaced.cs", Line = 3, ParseSupport = DomainSupport.Partial },
            ],
        }, SourceEvidenceDomain.Contracts, SourceDomainStatus.Partial, "OpenAPI"),
        CrossDomain = Env(new CrossDomainEvidence
        {
            Links =
            [
                new SourceEvidenceLink { Id = "l1", Type = SourceEvidenceLinkType.ConfigurationReferencesInfrastructureResource, FromId = "config:e1", FromLabel = "ServiceBus:Topic", ToId = "infra/azurerm_servicebus_topic.orders", ToLabel = "Service Bus topic orders-created", State = ArchitectureEvidenceState.StronglySupported, Basis = "Configured entity name matches the declared resource name." },
                new SourceEvidenceLink { Id = "l2", Type = SourceEvidenceLinkType.ConfigurationReferencesInfrastructureResource, FromId = "config:e9", FromLabel = "Inventory:Topic", ToLabel = "inventory-changed", State = ArchitectureEvidenceState.Unresolved, Basis = "No matching declaration found in the selected source — it may be managed elsewhere." },
            ],
            EnvironmentMappings = [new SourceEnvironmentMapping { Kind = SourceEnvironmentKind.QA, State = ArchitectureEvidenceState.Confirmed }],
            ObservabilityLayers = [new("Application code", "Detected", "1 component", 1), new("Runtime telemetry", SourceDomainText.RuntimeNotAssessed, "never claimed", 0)],
        }, SourceEvidenceDomain.CrossDomain, SourceDomainStatus.Partial),
    };

    private IqrSourceSnapshot Snapshot(SourceEvidenceDomainsSnapshot? evidence, string file = "shop.zip") => new()
    {
        Id = SnapshotId, IntegrationId = "source-analysis", Archive = new SourceArchive(file, Fp, 40), AnalyzedAt = DateTimeOffset.Parse("2026-10-02T10:00:00Z"), Status = SourceAnalysisStatus.Ready,
        Architecture = new ArchitectureSnapshot { Status = ArchitectureStatus.Complete, SourceSnapshotId = SnapshotId }, EvidenceDomains = evidence,
    };

    private IRenderedComponent<SourceAnalysis> Page(IqrSourceSnapshot snapshot)
    {
        _snapshots = [snapshot];
        var cut = Render<SourceAnalysis>();
        cut.WaitForElement("[data-testid=sa-current]");
        return cut;
    }

    // ── Landing ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Landing_shows_four_compact_evidence_rows_and_keeps_the_three_core_cards()
    {
        var cut = Page(Snapshot(Evidence()));
        cut.FindAll("[data-testid=sa-area-card]").Should().HaveCount(3, "core analysis cards are unchanged");
        var rows = cut.FindAll("[data-testid=sa-evidence-row]");
        rows.Select(r => r.GetAttribute("data-area")).Should().Equal("Infrastructure", "Configuration", "CI/CD", "Contracts");
        rows.Select(r => r.GetAttribute("data-status")).Should().Equal("Partial", "Complete", "Partial", "Partial");
        cut.Find("[data-testid=sa-evidence-row][data-area=Infrastructure] [data-metric=Resources] dd").TextContent.Should().Be("3");
        cut.Find("[data-testid=sa-evidence-row][data-area='CI/CD'] [data-metric=Pipelines] dd").TextContent.Should().Be("1", "templates are counted separately");
        cut.Find("[data-testid=sa-overview-evidence]").TextContent.Should().Contain(SourceDomainText.SourceBoundary);
    }

    [Fact]
    public void Cross_source_summary_counts_linked_and_unresolved_without_dumping_links()
    {
        var cut = Page(Snapshot(Evidence()));
        var row = cut.Find("[data-testid=sa-cross-domain-row]");
        row.QuerySelector("th")!.TextContent.Should().Be("Configuration ↔ Infrastructure");
        row.QuerySelectorAll("td").Select(td => td.TextContent).Should().Equal("1", "1");
        cut.Find("[data-testid=sa-cross-domain]").TextContent.Should().Contain("may be managed elsewhere").And.Contain("not a failure");
        cut.Find("[data-testid=sa-cross-domain-links] .disclosure-body").HasAttribute("hidden").Should().BeTrue("links are drill-down");
    }

    [Fact]
    public void Historical_snapshots_show_not_analyzed_rows_and_an_explicit_note_in_each_area()
    {
        var cut = Page(Snapshot(null));
        cut.FindAll("[data-testid=sa-evidence-row]").Should().OnlyContain(r => r.GetAttribute("data-status") == "Not analyzed");
        cut.FindAll("[data-testid=sa-evidence-metric]").Should().BeEmpty("a not-analyzed domain has no counts");
        cut.Find("[data-testid=area-infrastructure]").Click();
        cut.Find("[data-testid=evidence-missing]").TextContent.Should().Contain("predates the source-evidence domains");
    }

    [Fact]
    public void Navigation_groups_core_analysis_and_source_evidence_and_opens_each_domain()
    {
        var cut = Page(Snapshot(Evidence()));
        cut.FindAll("[data-testid=sa-area-group]").Select(g => g.GetAttribute("aria-label")).Should().Equal("Core analysis", "Source evidence");
        foreach (var (id, workspace) in new[] { ("area-infrastructure", "infrastructure-workspace"), ("area-configuration", "configuration-workspace"), ("area-cicd", "cicd-workspace"), ("area-contracts", "contracts-workspace") })
        {
            cut.Find($"[data-testid={id}]").Click();
            cut.Find($"[data-testid={id}]").GetAttribute("aria-pressed").Should().Be("true");
            cut.Find($"[data-testid={workspace}]").TextContent.Should().Contain(SourceDomainText.SourceBoundary);
        }
        cut.Find("[data-testid=area-overview]").Click();
        cut.Find("[data-testid=sa-open-cicd]").Click();
        cut.Find("[data-testid=cicd-workspace]");
    }

    // ── Domain pages ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Infrastructure_page_is_summary_first_with_filterable_resources_and_expandable_settings()
    {
        var cut = Render<InfrastructureWorkspace>(p => p.Add(x => x.Evidence, Evidence()));
        cut.Find("[data-testid=sd-infra-status]").TextContent.Should().Contain("Partial");
        cut.Find("[data-testid=sd-infra-provenance]").TextContent.Should().Contain("abcdef01").And.Contain("analyzer v1");
        cut.Find("[data-testid=sd-infra-overview]").TextContent.Should().Contain("Messaging");
        cut.Find("[data-testid=sd-infra-tab-resources]").Click();
        cut.FindAll("[data-testid=sd-infra-resource]").Should().HaveCount(3);
        cut.Find("[data-testid=sd-infra-filter-category]").Change("Messaging");
        cut.FindAll("[data-testid=sd-infra-resource]").Should().ContainSingle();
        cut.Find("[data-testid=sd-infra-settings-toggle]").Click();
        cut.Find("[data-testid=sd-infra-settings]").TextContent.Should().Contain("max_delivery_count").And.Contain(SourceDomainText.DeclaredNotVerified);
        cut.Find("[data-testid=sd-infra-tab-identity]").Click();
        cut.Find("[data-testid=sd-infra-access-row]").TextContent.Should().Contain("Azure Service Bus Data Sender").And.Contain("Effective permission not verified");
        cut.Find("[data-testid=sd-infra-tab-variables]").Click();
        cut.Find("[data-testid=sd-infra-variable]").TextContent.Should().Contain("Redacted").And.Contain("qa: redacted");
        cut.Find("[data-testid=sd-infra-tab-evidence]").Click();
        cut.Find("[data-testid=sd-infra-module]").TextContent.Should().Contain("never fetched");
    }

    [Fact]
    public void No_iac_is_reported_as_not_detected_without_tabs_or_zero_counts()
    {
        var cut = Render<InfrastructureWorkspace>(p => p.Add(x => x.Evidence, Evidence(infrastructure: false)));
        cut.Find("[data-testid=sd-infra-status]").TextContent.Should().Contain("Not detected");
        cut.Find("[data-testid=sd-infra-not-assessed]").TextContent.Should().Contain("not a failure");
        cut.FindAll("[data-testid=sd-infra-overview]").Should().BeEmpty();
        cut.FindAll("[data-testid^=sd-infra-tab-]").Should().BeEmpty();
    }

    [Fact]
    public void Configuration_page_never_renders_sensitive_values_and_filters_by_environment()
    {
        var cut = Render<ConfigurationWorkspace>(p => p.Add(x => x.Evidence, Evidence()));
        cut.Find("[data-testid=sd-config-sensitive]").TextContent.Should().Be("1");
        cut.Find("[data-testid=sd-config-conflict]").TextContent.Should().Contain("orders:topic");
        cut.Find("[data-testid=sd-config-tab-entries]").Click();
        cut.Find("[data-testid=sd-config-entry][data-sensitive=true]").TextContent.Should().Contain("Connection string detected — value not shown");
        cut.Find("[data-testid=sd-config-filter-environment]").Change("QA:qa");
        cut.FindAll("[data-testid=sd-config-entry]").Should().ContainSingle().Which.TextContent.Should().Contain("Debug");
        cut.Find("[data-testid=sd-config-shown]").TextContent.Should().Be("1 of 4 shown");
    }

    [Fact]
    public void Pipeline_page_counts_template_steps_and_never_claims_execution()
    {
        var cut = Render<PipelineWorkspace>(p => p.Add(x => x.Evidence, Evidence()));
        var row = cut.Find("[data-testid=sd-cicd-pipeline][data-template=false]");
        row.TextContent.Should().Contain("Push · branches main · paths src/*");
        row.QuerySelectorAll("td")[1].TextContent.Should().Be("1", "the included template's unit-test step counts (cycle-safe)");
        row.QuerySelectorAll("td")[2].TextContent.Should().Be("1");
        cut.Find("[data-testid=sd-cicd-open]").Click();
        cut.Find("[data-testid=sd-cicd-secrets]").TextContent.Should().Contain("variable group: shop-secrets").And.Contain("Names only");
        cut.FindAll("[data-testid=sd-cicd-step]").Select(s => s.GetAttribute("data-kind")).Should().Contain(["Build", "UnitTest", "DependencyScan", "ApplicationDeploy"]);
        cut.Find("[data-testid=sd-cicd-detail]").TextContent.Should().Contain(SourceDomainText.StepNotExecuted);
    }

    [Fact]
    public void Contracts_page_shows_owner_basis_and_requiredness_on_drill_down()
    {
        var cut = Render<ContractsWorkspace>(p => p.Add(x => x.Evidence, Evidence()));
        cut.FindAll("[data-testid=sd-contract]").Should().HaveCount(2);
        cut.Find("[data-testid=sd-contracts-filter-type]").Change("OpenApi");
        cut.FindAll("[data-testid=sd-contract]").Should().ContainSingle().Which.TextContent.Should().Contain("Producer: Api");
        cut.Find("[data-testid=sd-contract-toggle]").Click();
        cut.Find("[data-testid=sd-contract-detail]").TextContent.Should().Contain("id: string (required)").And.Contain("total: number").And.Contain(SourceDomainText.ContractNotVerified);
    }

    [Fact]
    public void Changes_are_source_changes_against_the_same_repository_only()
    {
        var current = Evidence();
        var previous = current with { Id = Guid.NewGuid(), Infrastructure = current.Infrastructure with { Resources = current.Infrastructure.Resources.Take(2).ToList() } };
        var cut = Render<SourceDomainChanges>(p => p.Add(x => x.Slug, "infra").Add(x => x.Label, "Infrastructure").Add(x => x.Domain, SourceEvidenceDomain.Infrastructure)
            .Add(x => x.Current, current).Add(x => x.Previous, ("shop.zip · earlier", previous)));
        cut.Find("[data-testid=sd-infra-change]").TextContent.Should().Contain("Added").And.Contain("db");
        cut.Markup.Should().Contain("not runtime or deployment drift");
        var none = Render<SourceDomainChanges>(p => p.Add(x => x.Slug, "infra").Add(x => x.Label, "Infrastructure").Add(x => x.Domain, SourceEvidenceDomain.Infrastructure).Add(x => x.Current, current));
        none.Find("[data-testid=sd-infra-changes-empty]");
    }

    [Fact]
    public void Observability_page_shows_source_evidence_layers_with_runtime_never_assessed()
    {
        var cut = Render<BirkNext.Web.Components.ObservabilityWorkspace>(p => p.Add(x => x.Snapshot, new BirkNext.SourceObservability.SourceObservabilitySnapshot { Status = ArchitectureStatus.Complete, SourceFingerprint = Fp })
            .Add(x => x.Layers, Evidence().CrossDomain.ObservabilityLayers));
        cut.FindAll("[data-testid=obs-layer]").Select(l => l.GetAttribute("data-layer")).Should().Equal("Application code", "Runtime telemetry");
        cut.Find("[data-testid=obs-layer][data-layer='Runtime telemetry']").GetAttribute("data-state").Should().Be(SourceDomainText.RuntimeNotAssessed);
    }

    [Fact]
    public void Presentation_value_text_never_shows_a_sensitive_value()
    {
        var entry = new ConfigurationEntry { Key = "Payments:ApiKey", ValueKind = ConfigurationValueKind.Secret, Sensitivity = ConfigurationSensitivity.Sensitive, ValuePreviewSafe = "should-never-show" };
        SourceEvidenceDomainsPresentation.ValueText(entry).Should().Be("Sensitive value — not shown");
    }
}
