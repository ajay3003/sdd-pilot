using System.Text.Json;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Services.IntegrationTopology;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.DatabaseArchitecture;
using BirkNext.Integrations;
using BirkNext.IntegrationTopology;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using static BirkNext.Api.Tests.Services.SourceAnalysis.Evidence.SourceEvidenceFixtures;

namespace BirkNext.Api.Tests.Services.IntegrationTopology;

/// <summary>
/// The generic cross-source relationship resolver and topology source coverage. A synthetic non-Skolenærvær pack ("Orders": application A,
/// application B, Service Bus, Terraform, an external application reference) proves the same source-role model, capability coverage, resolver and
/// missing-source guidance; the Skolenærvær fixtures mirror the current packages by content under arbitrary archive names. Expected topology,
/// role assignments and similar names never resolve a relationship; snapshots are never written; nothing is fetched.
/// </summary>
public sealed class TopologyResolutionTests : IAsyncLifetime
{
    private const string Env = BirkNext.Api.Tests.Services.ActiveEventTesting.ActiveEventTestHarness.Env;
    private readonly IntegrationJourneyHarness _harness = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private sealed class NoHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network in topology tests."); }

    /// <summary>The canonical IQR bindings, read only (Topology never writes one). Null = no binding store (legacy "Available").</summary>
    private sealed class Bindings(List<IntegrationEvidenceBinding> rows) : BirkNext.Api.Services.Integrations.SourceEvidence.IIntegrationEvidenceBindingStore
    {
        public int Writes { get; private set; }
        public Task<IReadOnlyList<IntegrationEvidenceBinding>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IntegrationEvidenceBinding>>(rows);
        public Task<(IntegrationEvidenceBinding? Binding, string? Error)> SetAsync(string environmentId, IntegrationEvidenceBindingRequest request, CancellationToken ct = default)
        {
            Writes++;
            throw new InvalidOperationException("Topology must never bind a source.");
        }
    }

    private Bindings? _bindings;

    private IntegrationTopologyService Service(ITopologyProfile profile) => new(
        [new ProfileTopologyProvider([profile]), new TargetEnvironmentTopologyProvider(), new SourceArchitectureTopologyProvider(), new IntegrationCatalogTopologyProvider(),
         new OpenApiTopologyProvider(new NoHttp(), new OpenApiExtractor(NullLogger<OpenApiExtractor>.Instance), TimeProvider.System),
         new CrossSourceTopologyProvider([profile]), new ObservationTopologyProvider()],
        [profile], TrustedExecutionEnvironmentRegistry.From(_harness.Active.Configuration()), _harness.Active.Catalog, _harness.Sources, TimeProvider.System, _bindings);

    private void Bind(params IqrSourceSnapshot[] snapshots) => _bindings = new([.. snapshots.Select((snapshot, i) =>
        new IntegrationEvidenceBinding { EnvironmentId = Env, IntegrationId = $"integration-{i}", SnapshotId = snapshot.Id, ArchiveName = snapshot.Archive.FileName })]);

    private async Task<TopologyCoverageView> Coverage(ITopologyProfile profile) => (await Service(profile).CoverageAsync(Env, profile.Id, new(null), default))!;
    private async Task<IntegrationTopologyView> View(ITopologyProfile profile) => (await Service(profile).BuildAsync(Env, new(null), default)).View;
    private static TopologyRelationshipResult Rel(TopologyCoverageView coverage, string id) => coverage.Relationships.Single(item => item.Id == id);
    private static TopologySourceRoleCoverage Role(TopologyCoverageView coverage, string id) => coverage.Roles.Single(item => item.RoleId == id);

    // ── fixture helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    private static ConfigurationEntry Entry(string key, string value, string file, string? component, ConfigurationValueKind kind = ConfigurationValueKind.Url,
        string technology = "ASP.NET Core appsettings", SourceEnvironmentLabel? environment = null) =>
        new() { Key = key, NormalizedKey = key.ToLowerInvariant(), ValuePreviewSafe = value, File = file, Component = component, ValueKind = kind, Technology = technology,
            Environment = environment ?? SourceEnvironmentLabel.Default };

    private static SourceEvidenceDomainsSnapshot Domains(params ConfigurationEntry[] entries) =>
        new() { Configuration = new ConfigurationEvidence { Domain = SourceEvidenceDomain.Configuration, Entries = [.. entries] } };

    private static ArchitectureComponent App(string name, string project, ArchitectureComponentType type = ArchitectureComponentType.Api) =>
        new() { Id = $"component:{name}", Name = name, ComponentType = type, SourceProject = project };

    private static MessagingChannel Channel(string entity, MessagingChannelType type, string? producer, string? consumer, ArchitectureEvidenceState state = ArchitectureEvidenceState.Confirmed) => new()
    {
        Id = $"channel:{entity}", Name = entity, EntityName = entity, Type = type,
        Producers = producer is null ? [] : [new($"component:{producer}", ChannelRole.Producer, state, "Wolverine", [])],
        Consumers = consumer is null ? [] : [new($"component:{consumer}", ChannelRole.Consumer, state, "Wolverine", [])],
    };

    private static ArchitectureDependency Client(string from, string name) => new()
    {
        Id = $"{from}|Http|{name}", FromComponentId = $"component:{from}", TargetReference = $"HttpClient '{name}'", DependencyType = ArchitectureDependencyType.Http, Protocol = "HTTP",
        EvidenceState = ArchitectureEvidenceState.Unresolved, Framework = "Named HttpClient",
    };

    private IqrSourceSnapshot Add(string archive, ArchitectureSnapshot? architecture, SourceEvidenceDomainsSnapshot? domains, DatabaseArchitectureSnapshot? database = null)
    {
        var snapshot = new IqrSourceSnapshot { Archive = new SourceArchive(archive, "sha-" + archive, 1), AnalyzedAt = DateTimeOffset.UtcNow, Architecture = architecture, EvidenceDomains = domains,
            DatabaseArchitecture = database };
        _harness.Sources.Snapshots.Add(snapshot);
        return snapshot;
    }

    // ── generic (non-Skolenærvær) pack ──────────────────────────────────────────────────────────────────────────────

    private sealed class OrdersPack : ITopologyProfile
    {
        public string Id => "orders";
        public string Label => "Orders";
        public IReadOnlyList<TopologyExpectedComponent> Components { get; } =
            [new("Orders", ["Orders.Api"], "Order intake."), new("Billing", ["Billing.Worker"], "Billing."), new("Payments", [], "External payment app.")];
        public IReadOnlyList<TopologyExpectedPath> Paths { get; } = [];
        public IReadOnlyList<TopologySourceRoleDefinition> SourceRoles { get; } =
        [
            new("orders", "Orders application", TopologySourceRequirement.Required) { Match = new() { Components = ["Orders.Api"] }, Capabilities = [TopologyCapabilities.ServiceBus] },
            new("billing", "Billing application", TopologySourceRequirement.Required) { Match = new() { Components = ["Billing.Worker"] }, Capabilities = [TopologyCapabilities.HttpClient] },
            new("iac", "Infrastructure as Code", TopologySourceRequirement.Recommended) { Match = new() { InfrastructureFormats = [InfrastructureFormat.Terraform] }, Capabilities = [TopologyCapabilities.Terraform] },
            new("payments", "Payments app reference", TopologySourceRequirement.BuiltInReference),
            new("ledger", "Ledger consumer", TopologySourceRequirement.Conditional) { Match = new() { Components = ["Ledger"] }, Consequence = "Service Bus → Ledger may remain unresolved." },
            new("docs", "Documentation site", TopologySourceRequirement.Optional) { Match = new() { Components = ["Docs.Site"] } },
        ];
        public IReadOnlyList<TopologyExpectedRelationship> Relationships { get; } =
        [
            new("billing-orders", "Billing → Orders", "Billing", "Orders", "HTTP", TopologyDimensions.Application) { EvidenceRoles = ["billing", "iac"] },
            new("orders-bus", "Orders → Service Bus", "Orders", "resource:servicebus", "Service Bus", TopologyDimensions.Messaging) { EvidenceRoles = ["orders"] },
            new("orders-billing", "Orders → Billing", "Orders", "Billing", "Service Bus", TopologyDimensions.Messaging) { EvidenceRoles = ["orders", "billing"] },
            new("payments-billing", "Payments → Billing", "Payments", "Billing", "External application", TopologyDimensions.Application) { EvidenceRoles = ["billing", "payments"] },
            new("bus-ledger", "Service Bus → Ledger", "resource:servicebus", "role:ledger", "Service Bus", TopologyDimensions.ExternalConsumer)
                { Via = "Orders", EvidenceRoles = ["ledger"], MissingEvidence = "A Ledger consumer of an entity Orders produces." },
        ];
        public IReadOnlyList<TopologyExternalReference> References { get; } =
            [new("payments", "Payments", () => new(["pay/checkout"], ["https://pay.example.test/pay/checkout"], "App pay/checkout (reference)."))];
    }

    private const string ModuleApp = """
        variable "container_app_name" {}
        resource "azurerm_container_app" "this" { name = var.container_app_name }
        """;

    private const string Infra = """
        variable "env" {}
        locals {
          names = {
            orders  = "ca-orders-${var.env}-001"
            billing = "ca-billing-${var.env}-001"
          }
        }
        module "orders_api" {
          source             = "./modules/app"
          container_app_name = local.names.orders
        }
        module "billing_worker" {
          source             = "./modules/app"
          container_app_name = local.names.billing
          additional_env_vars = {
            "OrdersApi__BaseUrl" = "https://${module.orders_api.container_app_fqdn}"
          }
        }
        resource "azurerm_servicebus_topic" "created" { name = "orders.created" }
        resource "azurerm_role_assignment" "billing_receiver" {
          scope        = azurerm_servicebus_topic.created.id
          principal_id = module.billing_worker.identity
        }
        """;

    private static SourceEvidenceDomainsSnapshot InfrastructurePackage() =>
        Analyze(("Platform.sln", ""), ("infra/main.tf", Infra), ("infra/modules/app/main.tf", ModuleApp));

    private void Orders(bool billing = true, bool billingAddress = true, bool iac = true)
    {
        Add("a1.zip", new ArchitectureSnapshot
        {
            Components = [App("Orders.Api", "Orders/src/Orders.Api/Orders.Api.csproj")],
            MessagingChannels = [Channel("orders.created", MessagingChannelType.ServiceBusTopic, "Orders.Api", null)],
        }, Domains(Entry("containerAppName", "ca-orders-dev-001", "Orders/.pipeline/orders-dev.yml", null, ConfigurationValueKind.Text, "Azure Pipelines")));
        if (billing)
            Add("b2.zip", new ArchitectureSnapshot
            {
                Components = [App("Billing.Worker", "Billing/src/Billing.Worker/Billing.Worker.csproj", ArchitectureComponentType.Worker)],
                Dependencies = [Client("Billing.Worker", "OrdersApi")],
            }, Domains([
                .. billingAddress ? [Entry("OrdersApi:BaseUrl", "https://ca-orders-dev-001.internal.example.test", "Billing/src/Billing.Worker/appsettings.json", "Billing.Worker")] : Array.Empty<ConfigurationEntry>(),
                Entry("Messaging:Inbound:TopicName", "orders.created", "Billing/src/Billing.Worker/appsettings.json", "Billing.Worker", ConfigurationValueKind.EntityName),
                Entry("Payments:Webhook:SourceFilter", "https://pay.example.test/pay/checkout", "Billing/src/Billing.Worker/appsettings.json", "Billing.Worker"),
                Entry("Payments:Webhook:Endpoint", "https://billing.example.test/hooks/payment", "Billing/src/Billing.Worker/appsettings.json", "Billing.Worker"),
                Entry("containerAppName", "ca-billing-dev-001", "Billing/.pipeline/billing-dev.yml", null, ConfigurationValueKind.Text, "Azure Pipelines")]));
        if (iac) Add("c3.zip", null, InfrastructurePackage());
    }

    [Fact]
    public void TerraformAnalyzer_RecordsModuleDeploymentNamesAndEndpointInjections()
    {
        var infrastructure = InfrastructurePackage().Infrastructure;
        infrastructure.AnalyzerVersion.Should().Be(4, "v4 adds the provider family; v3 module deployment names and endpoint injections remain");
        infrastructure.Modules.Single(module => module.Name == "orders_api").DeploymentName.Should().Be("ca-orders-*-001", "the per-environment variable is a wildcard");
        infrastructure.EndpointInjections.Should().ContainSingle().Which.Should().Match<InfrastructureEndpointInjection>(item =>
            item.FromId.EndsWith("module.billing_worker") && item.Key == "OrdersApi:BaseUrl" && item.ToId.EndsWith("module.orders_api") && item.Attribute == "container_app_fqdn");
        CrossSourceTopologyProvider.TemplateMatches("ca-orders-*-001", "ca-orders-dev-001").Should().BeTrue();
        CrossSourceTopologyProvider.TemplateMatches("ca-orders-*-001", "ca-ordersx-dev-001").Should().BeFalse();
    }

    [Fact]
    public async Task GenericPack_SameRoleModelResolverAndGuidance_ResolvesFromIndependentSources()
    {
        Orders();
        var coverage = await Coverage(new OrdersPack());

        coverage.Roles.Select(role => (role.RoleId, role.State)).Should().BeEquivalentTo([
            ("orders", TopologySourceRoleState.Available), ("billing", TopologySourceRoleState.Available), ("iac", TopologySourceRoleState.Available),
            ("payments", TopologySourceRoleState.ReferenceAvailable), ("ledger", TopologySourceRoleState.NotProvided), ("docs", TopologySourceRoleState.NotProvided)]);
        Role(coverage, "billing").MatchingSources.Should().Equal(["b2.zip"], "the archive name is provenance, matched by content");
        Role(coverage, "payments").Detail.Should().Contain("no import required");
        coverage.CoreSourcesComplete.Should().BeTrue("a missing optional or conditional role does not make core topology partial");

        var http = Rel(coverage, "billing-orders");
        http.State.Should().Be(TopologyRelationshipState.Resolved);
        http.Evidence.Select(item => item.Basis).Should().Contain(["http-client-target-host", "iac-address-injection"], "application source and IaC independently name the target");
        http.Evidence.Select(item => item.Source).Distinct().Should().Contain(["Application source", "Infrastructure as Code"]);
        Rel(coverage, "orders-bus").State.Should().Be(TopologyRelationshipState.Resolved);
        var messaging = Rel(coverage, "orders-billing");
        messaging.State.Should().Be(TopologyRelationshipState.Resolved);
        messaging.Route.Should().Be("Orders → orders.created → Billing", "resource-centred: producer → topic → consumer");
        messaging.Evidence.Should().Contain(item => item.Basis == "iac-declares-resource");
        Rel(coverage, "payments-billing").Should().Match<TopologyRelationshipResult>(item => item.State == TopologyRelationshipState.Resolved
            && item.Evidence.Any(evidence => evidence.Basis == "configured-subscription"));

        var ledger = Rel(coverage, "bus-ledger");
        ledger.State.Should().Be(TopologyRelationshipState.ExpectedUnresolved);
        (ledger.SourceRoleName, ledger.SourceRoleState, ledger.Action).Should().Be(("Ledger consumer", TopologySourceRoleState.NotProvided, TopologyGapAction.ImportSource));
        ledger.Owner.Should().Be("Project Import");
        ledger.MissingEvidence.Should().Be("A Ledger consumer of an entity Orders produces.");
        ledger.AvailableEvidence.Should().Contain("Orders produces to orders.created");
        coverage.Completeness.Single(item => item.Dimension == TopologyDimensions.ExternalConsumer).State.Should().Be(TopologyCompletenessState.Partial);
        coverage.Completeness.Single(item => item.Dimension == "Runtime observation").State.Should().Be(TopologyCompletenessState.NotObserved);
    }

    [Fact]
    public async Task RoleAssignmentsAndSimilarNames_NeverResolve_AndAClientWithoutAnAddressStaysACandidate()
    {
        Orders(billingAddress: false, iac: false);
        _harness.Sources.Snapshots.Add(new IqrSourceSnapshot { Archive = new("c3.zip", "sha", 1), AnalyzedAt = DateTimeOffset.UtcNow,
            EvidenceDomains = Analyze(("Platform.sln", ""), ("infra/main.tf", Infra.Replace("\"OrdersApi__BaseUrl\"", "\"Unrelated__Setting\""))) });
        var coverage = await Coverage(new OrdersPack());
        var view = await View(new OrdersPack());

        Rel(coverage, "billing-orders").State.Should().Be(TopologyRelationshipState.Candidate, "the client name resembles the target, which proves nothing");
        Rel(coverage, "billing-orders").AvailableEvidence.Should().Contain("declares client HttpClient 'OrdersApi', but its target is not resolved");
        Rel(coverage, "billing-orders").Action.Should().Be(TopologyGapAction.ViewMissingEvidence, "the source exists; the evidence is not in it — never import again");
        view.Connections.Should().NotContain(item => item.ProducerComponentId == "billing" && item.ConsumerComponentId == "orders");
        view.Connections.SelectMany(item => item.Evidence).Should().NotContain(item => item.Basis.Contains("role", StringComparison.OrdinalIgnoreCase),
            "a role assignment to the topic is not a consumer");
    }

    [Fact]
    public async Task MissingRequiredRole_MakesCoreSourcesPartial_AndNamesTheSourceToImport()
    {
        Orders(billing: false);
        var coverage = await Coverage(new OrdersPack());
        coverage.CoreSourcesComplete.Should().BeFalse();
        Role(coverage, "billing").State.Should().Be(TopologySourceRoleState.NotProvided);
        var http = Rel(coverage, "billing-orders");
        (http.State, http.SourceRoleName, http.Action).Should().Be((TopologyRelationshipState.ExpectedUnresolved, "Billing application", TopologyGapAction.ImportSource));
    }

    [Fact]
    public async Task ExpectedTopologyAlone_ResolvesNothing()
    {
        var coverage = await Coverage(new OrdersPack());
        coverage.Relationships.Should().OnlyContain(item => item.State == TopologyRelationshipState.ExpectedUnresolved);
        (await View(new OrdersPack())).Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task ExistingSnapshots_AreReadNotWritten_AndOlderAnalyzerOutputStillResolves()
    {
        Orders(iac: false);
        var before = JsonSerializer.Serialize(_harness.Sources.Snapshots);
        var view = await View(new OrdersPack());
        JsonSerializer.Serialize(_harness.Sources.Snapshots).Should().Be(before, "the projection is derived; snapshots are immutable");
        view.ResolverVersion.Should().Be(CrossSourceTopologyProvider.ResolverVersion);
        view.Connections.Should().Contain(item => item.ProducerComponentId == "billing" && item.ConsumerComponentId == "orders" && item.Relationship == TopologyRelationshipState.Resolved,
            "stored configuration and pipeline facts resolve without re-import (no IaC injection needed)");
    }

    [Theory]
    [InlineData("/\\other.example.test/swagger.json")]
    [InlineData("/swagger.json?redirect=https://other.example.test")]
    [InlineData("/@other.example.test/swagger.json")]
    public void Discovery_StaysOnTheComponentsOwnOrigin(string path) =>
        TrustedComponentPolicy.OpenApiAddress(new TrustedComponent("A", "https://a.example.test:8443/", path)).Should().BeNull();

    [Fact]
    public void DiscoveryPlan_ListsEligibleHostsWithoutCallingThem()
    {
        var plan = IntegrationTopologyService.DiscoveryPlan(new TrustedExecutionEnvironment("dev", "Dev", "Development", true, "https://a.example.test/", [], new Dictionary<string, string>())
        {
            Components = [new("A", "https://a.example.test:8443/", "/swagger/v1/swagger.json"), new("B", "https://b.example.test/", "https://evil.example.test/x.json")],
        });
        plan.Eligible.Should().ContainSingle().Which.Should().Be(new TopologyDiscoveryTarget("A", "a.example.test:8443", "/swagger/v1/swagger.json"));
        plan.Hosts.Should().Equal(["a.example.test:8443"]);
        plan.Ineligible.Should().ContainSingle(item => item.Component == "B");
    }

    // ── Skolenærvær (current packages by content; archive names arbitrary) ─────────────────────────────────────────

    private void CurrentPackages(bool skoleAdapter = true)
    {
        var reference = new SkoleNaervaerTopologyProfile().References.Single().Load()!;
        Add("u.zip", new ArchitectureSnapshot { Components = [App("Service", "Buf.Meldingsutveksler.Service/Buf.Meldingsutveksler.Service.csproj")] }, Domains(
            Entry("AltinnWebhookSettings:Subscriptions:0:SourceFilter", reference.Addresses[0], "Buf.Meldingsutveksler.Service/appsettings.Development.json", "Service"),
            Entry("AltinnWebhookSettings:Subscriptions:0:Endpoint", "https://mu.example.test/api/hooks", "Buf.Meldingsutveksler.Service/appsettings.Development.json", "Service")));
        var local = new SourceEnvironmentLabel(SourceEnvironmentKind.Local, "Local");
        Add("m.zip", new ArchitectureSnapshot
        {
            Components =
            [
                App("M2LB.Meldingsutvekslermottak.Host", "Meldingsutvekslermottak/src/M2LB.Meldingsutvekslermottak.Host/M2LB.Meldingsutvekslermottak.Host.csproj", ArchitectureComponentType.Worker),
                App("M2LB.Skole.Api", "Skole/src/M2LB.Skole.Api/M2LB.Skole.Api.csproj"),
                .. skoleAdapter ? [App("M2LB.Skole.BiRK.Worker", "SkoleAdapter/src/M2LB.Skole.BiRK.Worker/M2LB.Skole.BiRK.Worker.csproj", ArchitectureComponentType.Worker)] : Array.Empty<ArchitectureComponent>(),
            ],
            Dependencies = skoleAdapter ? [Client("M2LB.Skole.BiRK.Worker", "SkoletjenestenApi")] : [],
            MessagingChannels =
            [
                Channel("skole.rapporter", MessagingChannelType.ServiceBusTopic, "M2LB.Skole.Api", null),
                .. skoleAdapter ? [Channel("m2lb-cdc-dev.BirkM2LB.dbo.Utdanning", MessagingChannelType.EventHub, null, "M2LB.Skole.BiRK.Worker", ArchitectureEvidenceState.Inferred)] : Array.Empty<MessagingChannel>(),
            ],
        }, Domains([
            Entry("MessagingChannels:Channels:0:InboundToSystem:QueueName", "m2lb_skolenaervaer", "Meldingsutvekslermottak/src/M2LB.Meldingsutvekslermottak.Host/appsettings.Local.json",
                "M2LB.Meldingsutvekslermottak.Host", ConfigurationValueKind.EntityName, environment: local),
            Entry("http:applicationUrl", "http://localhost:5019", "Skole/src/M2LB.Skole.Api/Properties/launchSettings.json", "M2LB.Skole.Api", environment: local),
            .. skoleAdapter ? [Entry("SkoletjenestenApi:BaseUrl", "http://localhost:5019", "SkoleAdapter/src/M2LB.Skole.BiRK.Worker/appsettings.Local.json", "M2LB.Skole.BiRK.Worker", environment: local)]
                : Array.Empty<ConfigurationEntry>()]));
        var iac = Domains(Entry("debezium_topic_prefix", "m2lb-cdc-dev", "terraform/environments/dev.tfvars", null, ConfigurationValueKind.Text, "Terraform variables"));
        Add("i.zip", null, iac with { Infrastructure = new InfrastructureEvidence { Domain = SourceEvidenceDomain.Infrastructure, Formats = [InfrastructureFormat.Terraform], AnalyzerVersion = 3 } });
    }

    [Fact]
    public async Task CurrentPackages_RolesByContent_BiRKAndBufdataConditional_NothingFabricated()
    {
        CurrentPackages();
        var profile = new SkoleNaervaerTopologyProfile();
        var coverage = await Coverage(profile);

        Role(coverage, SkoleNaervaerTopologyProfile.Mu).Should().Match<TopologySourceRoleCoverage>(role => role.State == TopologySourceRoleState.Available && role.MatchingSources.Single() == "u.zip");
        Role(coverage, SkoleNaervaerTopologyProfile.M2lb).State.Should().Be(TopologySourceRoleState.Available);
        Role(coverage, SkoleNaervaerTopologyProfile.Iac).Should().Match<TopologySourceRoleCoverage>(role => role.State == TopologySourceRoleState.Available && role.Requirement == TopologySourceRequirement.Recommended);
        Role(coverage, SkoleNaervaerJourneyPack.AltinnAppSourceRole).State.Should().Be(TopologySourceRoleState.ReferenceAvailable, "the Altinn reference needs no import");
        Role(coverage, SkoleNaervaerTopologyProfile.Birk).Should().Match<TopologySourceRoleCoverage>(role => role.State == TopologySourceRoleState.NotProvided && role.Requirement == TopologySourceRequirement.Conditional);
        Role(coverage, SkoleNaervaerTopologyProfile.BufdataRole).Requirement.Should().Be(TopologySourceRequirement.Conditional);
        coverage.CoreSourcesComplete.Should().BeTrue();

        foreach (var resolved in new[] { "altinn-mu", "skoleadapter-skoletjenesten", "debezium-eventhub", "eventhub-skoleadapter", "rapport-servicebus" })
            Rel(coverage, resolved).State.Should().Be(TopologyRelationshipState.Resolved, resolved);
        Rel(coverage, "mu-mm").Should().Match<TopologyRelationshipResult>(item => item.State == TopologyRelationshipState.Candidate && item.AvailableEvidence!.Contains("MM consumes m2lb_skolenaervaer"));

        var birk = Rel(coverage, "birk-cdc");
        (birk.State, birk.SourceRoleName, birk.Action).Should().Be((TopologyRelationshipState.ExpectedUnresolved, "BiRK application source", TopologyGapAction.ImportSource));
        birk.AvailableEvidence.Should().Contain("Debezium connector", "CDC infrastructure exists without the application side");
        var bufdata = Rel(coverage, "servicebus-bufdata");
        (bufdata.State, bufdata.SourceRoleName).Should().Be((TopologyRelationshipState.ExpectedUnresolved, "Bufdata consumer evidence"));
        var view = await View(profile);
        view.Connections.Should().NotContain(item => item.ConsumerComponentId == "bufdata" || item.ProducerComponentId == "birk", "no BiRK or Bufdata relationship is invented");
    }

    [Fact]
    public async Task M2lbWithoutSkoleAdapter_IsPartlyAvailable_NamingTheMissingPart()
    {
        CurrentPackages(skoleAdapter: false);
        var role = Role(await Coverage(new SkoleNaervaerTopologyProfile()), SkoleNaervaerTopologyProfile.M2lb);
        (role.State, role.MissingParts.Single()).Should().Be((TopologySourceRoleState.PartlyAvailable, "Skole.BiRK or SkoleAdapter.Worker"));
    }

    [Fact]
    public async Task BiRKSourceWithTheCapturedTable_ResolvesBiRKToCdc()
    {
        CurrentPackages();
        Add("x.zip", new ArchitectureSnapshot { Components = [App("Birk.Server", "Birk.Server/Birk.Server.csproj")] }, null, new DatabaseArchitectureSnapshot
        {
            Databases = [new DatabaseModel { Id = "db", LogicalName = "BirkM2LB", ConfiguredDatabaseName = "BirkM2LB", SourceProject = "Birk.Server/Birk.Server.csproj",
                Schemas = [new SchemaModel { Name = "dbo", Tables = [new TableModel { Id = "t", LogicalName = "Utdanning", PhysicalName = "Utdanning", SchemaName = "dbo", SourceProject = "Birk.Server/Birk.Server.csproj" }] }] }],
        });
        var coverage = await Coverage(new SkoleNaervaerTopologyProfile());
        Role(coverage, SkoleNaervaerTopologyProfile.Birk).State.Should().Be(TopologySourceRoleState.Available);
        Rel(coverage, "birk-cdc").Should().Match<TopologyRelationshipResult>(item => item.State == TopologyRelationshipState.Resolved && item.Route!.Contains("BirkM2LB.dbo.Utdanning"));
    }

    [Fact]
    public async Task BufdataConsumerEvidence_ResolvesServiceBusToBufdata()
    {
        CurrentPackages();
        Add("y.zip", new ArchitectureSnapshot
        {
            Components = [App("Bufdata.Mottak", "Bufdata.Mottak/Bufdata.Mottak.csproj", ArchitectureComponentType.Worker)],
            MessagingChannels = [Channel("skole.rapporter", MessagingChannelType.ServiceBusTopic, null, "Bufdata.Mottak")],
        }, null);
        var coverage = await Coverage(new SkoleNaervaerTopologyProfile());
        Rel(coverage, "servicebus-bufdata").Should().Match<TopologyRelationshipResult>(item => item.State == TopologyRelationshipState.Resolved && item.Route!.Contains("skole.rapporter"));
    }

    // ── Source presence vs canonical binding ──────────────────────────────────────────────────────────────────────

    /// <summary>The M2LB implementation as currently named (MeldingsutvekslerReceiver, SkoleService, SkoleAdapter) — the real M2LB.zip.</summary>
    private IqrSourceSnapshot RenamedM2lb(string archive = "M2LB.zip", string sha = "sha-m2lb")
    {
        var local = new SourceEnvironmentLabel(SourceEnvironmentKind.Local, "Local");
        var snapshot = Add(archive, new ArchitectureSnapshot
        {
            Components =
            [
                App("M2LB.MeldingsutvekslerReceiver.Host", "MeldingsutvekslerReceiver/src/M2LB.MeldingsutvekslerReceiver.Host/M2LB.MeldingsutvekslerReceiver.Host.csproj", ArchitectureComponentType.Worker),
                App("M2LB.SkoleService.Api", "SkoleService/src/M2LB.SkoleService.Api/M2LB.SkoleService.Api.csproj"),
                App("M2LB.SkoleAdapter.Worker", "SkoleAdapter/src/M2LB.SkoleAdapter.Worker/M2LB.SkoleAdapter.Worker.csproj", ArchitectureComponentType.Worker),
                App("M2LB.Person.Api", "Person/src/M2LB.Person.Api/M2LB.Person.Api.csproj"),
            ],
            Dependencies = [Client("M2LB.SkoleAdapter.Worker", "SkoletjenestenApi")],
        }, Domains(
            Entry("http:applicationUrl", "http://localhost:5019", "SkoleService/src/M2LB.SkoleService.Api/Properties/launchSettings.json", "M2LB.SkoleService.Api", environment: local),
            Entry("SkoletjenestenApi:BaseUrl", "http://localhost:5019", "SkoleAdapter/src/M2LB.SkoleAdapter.Worker/appsettings.Local.json", "M2LB.SkoleAdapter.Worker", environment: local)));
        _harness.Sources.Snapshots.Remove(snapshot);
        snapshot = snapshot with { Archive = new SourceArchive(archive, sha, 1) };
        _harness.Sources.Snapshots.Add(snapshot);
        return snapshot;
    }

    [Fact]
    public async Task M2lbSnapshotPresent_ButUnbound_IsAvailableNeedsBinding_NotNotProvided()
    {
        RenamedM2lb();
        Bind();
        var coverage = await Coverage(new SkoleNaervaerTopologyProfile());
        var m2lb = Role(coverage, SkoleNaervaerTopologyProfile.M2lb);
        (m2lb.State, m2lb.Action, m2lb.MatchingSources.Single(), m2lb.BoundSources.Count).Should().Be(
            (TopologySourceRoleState.AvailableNeedsBinding, TopologyGapAction.BindSource, "M2LB.zip", 0),
            "the renamed implementation is recognized by content; presence is not binding");
        m2lb.Detail.Should().Contain("not bound");
        var mm = Rel(coverage, "mm-skoletjenesten");
        mm.Action.Should().NotBe(TopologyGapAction.ImportSource, "M2LB already exists — no duplicate import is suggested");
        coverage.Relationships.Where(item => item.SourceRoleId == SkoleNaervaerTopologyProfile.M2lb).Should().OnlyContain(item => item.Action != TopologyGapAction.ImportSource);
    }

    [Fact]
    public async Task RenamedM2lbProjects_AreTheProfilesSystems_ByExplicitAlias()
    {
        RenamedM2lb();
        var coverage = await Coverage(new SkoleNaervaerTopologyProfile());
        Role(coverage, SkoleNaervaerTopologyProfile.M2lb).State.Should().Be(TopologySourceRoleState.Available, "without a binding store, recognized reads Available as before");
        Rel(coverage, "skoleadapter-skoletjenesten").State.Should().Be(TopologyRelationshipState.Resolved, "SkoleAdapter.Worker → SkoleService.Api local launch address");
        var view = await View(new SkoleNaervaerTopologyProfile());
        view.Components.Should().Contain(item => item.Id == "skoletjenesten").And.NotContain(item => item.Name == "M2LB.SkoleService.Api",
            "the profile's exact alias merges the renamed project into Skoletjenesten");
        view.Components.Should().Contain(item => item.Name == "M2LB.Person.Api" || item.Id == "person");
    }

    [Fact]
    public async Task M2lbAbsent_IsNotProvided_AndOffersImport()
    {
        Bind();
        var coverage = await Coverage(new SkoleNaervaerTopologyProfile());
        var m2lb = Role(coverage, SkoleNaervaerTopologyProfile.M2lb);
        (m2lb.State, m2lb.Action).Should().Be((TopologySourceRoleState.NotProvided, TopologyGapAction.ImportSource));
        Rel(coverage, "rapport-servicebus").Action.Should().Be(TopologyGapAction.ImportSource);
    }

    [Fact]
    public async Task SeveralDistinctM2lbCandidates_AreAmbiguous_AndTheNewestIsNeverChosen()
    {
        RenamedM2lb("M2LB-old.zip", "sha-a");
        RenamedM2lb("M2LB.zip", "sha-b");
        Bind();
        var role = Role(await Coverage(new SkoleNaervaerTopologyProfile()), SkoleNaervaerTopologyProfile.M2lb);
        (role.State, role.Action, role.BoundSources.Count).Should().Be((TopologySourceRoleState.Ambiguous, TopologyGapAction.ReviewCandidates, 0));
        role.MatchingSources.Should().BeEquivalentTo(["M2LB-old.zip", "M2LB.zip"]);
        role.Detail.Should().Contain("never chosen automatically");
    }

    [Fact]
    public async Task BoundM2lb_IsBound_RelationshipsRecomputedFromStoredFacts_AndNothingIsWritten()
    {
        var snapshot = RenamedM2lb();
        var unbound = await Coverage(new SkoleNaervaerTopologyProfile());
        Bind(snapshot);
        var before = JsonSerializer.Serialize(_harness.Sources.Snapshots);
        var coverage = await Coverage(new SkoleNaervaerTopologyProfile());
        var role = Role(coverage, SkoleNaervaerTopologyProfile.M2lb);
        (role.State, role.BoundSources.Single(), role.Action).Should().Be((TopologySourceRoleState.Bound, "M2LB.zip", TopologyGapAction.ViewEvidence));
        coverage.Relationships.Select(item => (item.Id, item.State)).Should().BeEquivalentTo(unbound.Relationships.Select(item => (item.Id, item.State)),
            "binding is provenance for the canonical source; the projection is rebuilt from the same stored facts");
        JsonSerializer.Serialize(_harness.Sources.Snapshots).Should().Be(before);
        _bindings!.Writes.Should().Be(0);
    }

    [Fact]
    public async Task BoundSourcesWithoutTheAddress_PointToConfigureIntegration_NotAnotherSource()
    {
        var snapshot = RenamedM2lb();
        CurrentPackages();
        Bind(snapshot, _harness.Sources.Snapshots.Single(item => item.Archive.FileName == "u.zip"));
        var coverage = await Coverage(new SkoleNaervaerTopologyProfile());
        var mm = Rel(coverage, "mm-skoletjenesten");
        mm.State.Should().NotBe(TopologyRelationshipState.Resolved);
        (mm.Action, mm.Owner).Should().Be((TopologyGapAction.ConfigureIntegration, "Target Environment → Integrations"));
    }

    [Fact]
    public async Task InfrastructureRole_NeedsNoBinding_AndAnInfrastructureResourceAloneResolvesNoApplicationConnection()
    {
        CurrentPackages();
        Bind();
        var coverage = await Coverage(new SkoleNaervaerTopologyProfile());
        var iac = Role(coverage, SkoleNaervaerTopologyProfile.Iac);
        (iac.State, iac.Action).Should().Be((TopologySourceRoleState.Available, TopologyGapAction.ViewEvidence), "Infrastructure Evidence is consumed environment-wide");
        Rel(coverage, "servicebus-bufdata").State.Should().Be(TopologyRelationshipState.ExpectedUnresolved, "a declared resource is not an application relationship");
    }

    [Fact]
    public void DiscoveryPlan_DeduplicatesIdenticalTargets()
    {
        var plan = IntegrationTopologyService.DiscoveryPlan(new TrustedExecutionEnvironment("dev", "Dev", "Development", true, "https://a.example.test/", [], new Dictionary<string, string>())
        {
            Components = [new("A", "https://a.example.test/", "/swagger.json"), new("A", "https://A.example.test/", "/swagger.json"), new("B", "https://a.example.test/", "/swagger.json")],
        });
        plan.Eligible.Select(item => item.Component).Should().Equal(["A", "B"]);
        plan.Hosts.Should().Equal(["a.example.test"]);
    }
}
