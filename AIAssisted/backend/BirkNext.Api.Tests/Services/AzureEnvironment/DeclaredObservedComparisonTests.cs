using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Services.AzureEnvironment;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.AzureEnvironment;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static BirkNext.Api.Tests.Services.AzureEnvironment.AzureEnvironmentFixtures;
using SourceFixtures = BirkNext.Api.Tests.Services.SourceAnalysis.Evidence.SourceEvidenceFixtures;

namespace BirkNext.Api.Tests.Services.AzureEnvironment;

/// <summary>
/// Declared (Source Analysis Infrastructure evidence of the generic "Contoso.Orders" Terraform, analyzed by the real Source Analysis pipeline —
/// not re-parsed here) vs Observed (the fake Azure DEV scope). Every state is neutral and explained; the environment picks per-environment names.
/// </summary>
public sealed class DeclaredObservedComparisonTests
{
    private const string Main = """
        variable "env" { default = "dev" }
        variable "location" { default = "norwayeast" }
        resource "azurerm_linux_web_app" "api" {
          name       = "app-orders-${var.env}"
          location   = var.location
          https_only = true
        }
        resource "azurerm_eventhub_namespace" "events" {
          name                = "evhns-orders-${var.env}"
          location            = "norwayeast"
          sku                 = "Standard"
          minimum_tls_version = "1.2"
        }
        resource "azurerm_eventhub" "orders" {
          name            = "order-events"
          namespace_id    = azurerm_eventhub_namespace.events.id
          partition_count = 4
        }
        resource "azurerm_eventhub_consumer_group" "fulfillment" {
          name        = "fulfillment-${var.env}"
          eventhub_id = azurerm_eventhub.orders.id
        }
        resource "azurerm_servicebus_namespace" "bus" {
          name               = "sbns-orders-${var.env}"
          sku                = "Standard"
          local_auth_enabled = false
        }
        resource "azurerm_servicebus_topic" "placed" {
          name         = "order-placed"
          namespace_id = azurerm_servicebus_namespace.bus.id
        }
        resource "azurerm_storage_account" "ckpt" {
          name            = "storders${var.env}ckpt"
          min_tls_version = "TLS1_2"
        }
        resource "azurerm_postgresql_flexible_server" "db" {
          name    = "psql-orders-${var.env}"
          version = "15"
        }
        resource "azurerm_key_vault" "kv" { name = "kv-orders-${var.env}" }
        resource "azurerm_redis_cache" "cache" { name = "redis-orders-${var.env}" }
        resource "azurerm_user_assigned_identity" "worker" { name = "id-orders-${var.env}" }
        resource "azurerm_application_insights" "ai" { name = lower(format("appi-%s", var.env)) }
        resource "azurerm_role_assignment" "receiver" {
          scope                = azurerm_eventhub_namespace.events.id
          role_definition_name = "Azure Event Hubs Data Receiver"
          principal_id         = azurerm_user_assigned_identity.worker.principal_id
        }
        resource "aws_sqs_queue" "legacy" { name = "orders-legacy" }
        """;

    private static (string, string)[] Orders() =>
    [
        ("Contoso.Orders.sln", ""), ("infra/main.tf", Main), ("infra/dev.tfvars", "env = \"dev\"\n"), ("infra/qa.tfvars", "env = \"qa\"\n"),
    ];

    private static readonly Lazy<SourceEvidenceDomainsSnapshot> Domains = new(() => SourceFixtures.Analyze(Orders()));

    private static async Task<AzureEnvironmentSnapshot> Observed(FakeAzureManagementClient? fake = null) => await Collector(fake ?? Full()).CollectAsync(Request(), default);

    private static DeclaredObservedComparison Compare(AzureEnvironmentSnapshot observed, string environment = "dev") =>
        DeclaredObservedComparer.Compare(Domains.Value.Infrastructure, Guid.NewGuid(), Domains.Value.SourceFingerprint, observed, SourceEnvironments.Normalize(environment), "chosen");

    private static DeclaredObservedItem Item(DeclaredObservedComparison c, string name) => c.Items.Single(i => i.Name == name && i.DeclaredId is not null);

    [Fact]
    public async Task Declared_and_observed_resources_match_on_kind_and_environment_name()
    {
        var c = Compare(await Observed());
        foreach (var name in new[] { "app-orders-dev", "sbns-orders-dev", "order-placed", "stordersdevckpt", "kv-orders-dev", "id-orders-dev", "fulfillment-dev", "evhns-orders-dev" })
            Item(c, name).State.Should().Be(DeclaredObservedState.DeclaredAndObserved, name);
        Item(c, "app-orders-dev").Should().Match<DeclaredObservedItem>(i => i.Kind == InfrastructureResourceKind.ComputeApp && i.ObservedIds.Single() == Site && i.DeclaredFile == "infra/main.tf");
        Item(c, "app-orders-dev").NameBasis.Should().Contain("dev.tfvars");
        Item(c, "sbns-orders-dev").Differences.Should().BeEmpty("local_auth_enabled = false is the same fact as disableLocalAuth = true");
    }

    [Fact]
    public async Task Settings_that_differ_are_listed_with_both_values()
    {
        var c = Compare(await Observed());
        Item(c, "order-events").Should().Match<DeclaredObservedItem>(i => i.State == DeclaredObservedState.ConfigurationDiffers
            && i.Differences.Single().Setting == "partition_count" && i.Differences.Single().Declared == "4" && i.Differences.Single().Observed == "2");
        Item(c, "psql-orders-dev").Differences.Should().ContainSingle(d => d.Setting == "version" && d.Declared == "15" && d.Observed == "16");
        c.Counts[DeclaredObservedState.ConfigurationDiffers].Should().Be(2);
    }

    [Fact]
    public async Task Declared_only_observed_only_and_unable_to_verify_are_neutral_and_explained()
    {
        var c = Compare(await Observed());
        Item(c, "redis-orders-dev").Should().Match<DeclaredObservedItem>(i => i.State == DeclaredObservedState.DeclaredOnly && i.Reason.Contains("Not observed in the selected subscriptions"));
        c.Items.Should().Contain(i => i.State == DeclaredObservedState.UnableToVerify && i.DeclaredType == "azurerm_application_insights" && i.Reason.Contains("computed in source"));
        c.Items.Where(i => i.State == DeclaredObservedState.ObservedOnly).Select(i => i.Name).Should().Contain(["vnet-orders-dev", "pe-orders-psql-dev", "log-orders-dev"]).And.NotContain(["plan-orders-dev", "nsg-orders-dev"]);
        c.Limitations.Should().Contain(l => l.Contains("without a provider-neutral kind"));
        c.Items.Where(i => i.State == DeclaredObservedState.ObservedOnly).Should().OnlyContain(i => i.Reason.Contains("may be managed elsewhere"));
        c.Limitations.Should().Contain(l => l.Contains("other providers")).And.Contain(l => l.Contains("role assignment"));
        c.Boundary.Should().Contain("Neither is a failure");
        c.Items.Should().NotContain(i => i.Name.Contains(Sentinel));
    }

    [Fact]
    public async Task The_environment_selects_the_declared_names()
    {
        var qa = Compare(await Observed(), "qa");
        Item(qa, "app-orders-qa").State.Should().Be(DeclaredObservedState.DeclaredOnly);
        qa.Items.Should().NotContain(i => i.DeclaredId != null && i.State == DeclaredObservedState.DeclaredAndObserved && i.Name.EndsWith("-dev"));
        Item(qa, "order-placed").State.Should().Be(DeclaredObservedState.DeclaredOnly, "the topic name is literal, but its QA namespace (sbns-orders-qa) is not the observed DEV namespace");
    }

    [Fact]
    public async Task Absence_is_not_claimed_when_the_area_could_not_be_read()
    {
        var fake = Full();
        fake.Overrides.Add((u => u.Contains("/eventhubs", StringComparison.OrdinalIgnoreCase), FakeAzureManagementClient.Status(403)));
        var c = Compare(await Observed(fake));
        Item(c, "order-events").Should().Match<DeclaredObservedItem>(i => i.State == DeclaredObservedState.UnableToVerify && i.Reason.Contains("could not be fully read"));
        Item(c, "fulfillment-dev").State.Should().Be(DeclaredObservedState.UnableToVerify);
    }

    [Fact]
    public async Task The_same_name_in_two_subscriptions_is_an_ambiguous_match()
    {
        var observed = await Observed();
        var twin = observed.Resources.Single(r => AzureIds.Same(r.Id, Site)) with { Id = Id("Microsoft.Web/sites", "app-orders-dev", sub: Sub2), SubscriptionId = Sub2 };
        var c = Compare(observed with { Resources = [.. observed.Resources, twin] });
        Item(c, "app-orders-dev").Should().Match<DeclaredObservedItem>(i => i.State == DeclaredObservedState.AmbiguousMatch && i.ObservedIds.Count == 2);
    }

    [Fact]
    public void No_infrastructure_evidence_lists_everything_observed_only_with_a_limitation()
    {
        var observed = new AzureEnvironmentSnapshot { Id = Guid.NewGuid(), Resources = [new ObservedResource { Id = Site, Name = "app-orders-dev", Type = "microsoft.web/sites", ResourceKind = InfrastructureResourceKind.ComputeApp }] };
        var c = DeclaredObservedComparer.Compare(null, Guid.NewGuid(), "", observed, null, "not stated");
        c.Items.Should().ContainSingle(i => i.State == DeclaredObservedState.ObservedOnly);
        c.Limitations.Should().Contain(l => l.Contains("no infrastructure evidence"));
    }

    // ── Shared observed-evidence provider and controller ───────────────────────────────────────────────────────────

    private sealed class Sources(params IqrSourceSnapshot[] snapshots) : IReviewSourceEvidenceProvider
    {
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>(snapshots);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult(snapshots.FirstOrDefault(s => s.Id == snapshotId));
    }

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"azure-{Guid.NewGuid()}").Options);

    [Fact]
    public async Task The_controller_compares_stored_snapshots_and_offers_lookups_and_suggestions_without_saving()
    {
        using var db = Db();
        var store = new AzureEnvironmentSnapshotStore(db);
        var source = new IqrSourceSnapshot { Id = Guid.NewGuid(), Archive = new("orders.zip", "abc123", 4), AnalyzedAt = FixedClock.Now, EvidenceDomains = Domains.Value };
        var fake = Full();
        var controller = new AzureEnvironmentController(new FakeSignIn(), Collector(fake), store, new AzureEnvironmentEvidenceProvider(store), new Sources(source));

        var analyzed = (OkObjectResult)(await controller.Analyze(Request(), default)).Result!;
        var snapshot = (AzureEnvironmentSnapshot)analyzed.Value!;
        var list = (IReadOnlyList<AzureEnvironmentSnapshotSummary>)((OkObjectResult)(await controller.Snapshots("orders-dev", default)).Result!).Value!;
        list.Should().ContainSingle(s => s.Id == snapshot.Id && s.Resources == snapshot.Resources.Count && s.Environment == "dev");

        var comparison = (DeclaredObservedComparison)((OkObjectResult)(await controller.Comparison("orders-dev", null, source.Id, null, default)).Result!).Value!;
        comparison.Should().Match<DeclaredObservedComparison>(c => c.SourceSnapshotId == source.Id && c.AzureSnapshotId == snapshot.Id && c.Environment!.Kind == SourceEnvironmentKind.Development
            && c.EnvironmentBasis == "stated when the Azure scope was analyzed");
        comparison.Counts[DeclaredObservedState.DeclaredAndObserved].Should().BeGreaterThan(5);

        var lookup = (ObservedResourceLookup)((OkObjectResult)(await controller.Lookup("orders-dev", InfrastructureResourceKind.MessagingNamespace, "evhns-orders-dev.servicebus.windows.net", null, null, default)).Result!).Value!;
        lookup.Should().Match<ObservedResourceLookup>(l => l.State == ObservedLookupState.Observed && l.Resource!.ResourceKind == InfrastructureResourceKind.EventHubNamespace && l.Observations.Count > 0);
        var missing = (ObservedResourceLookup)((OkObjectResult)(await controller.Lookup("orders-dev", InfrastructureResourceKind.ServiceBusNamespace, "sbns-elsewhere", null, null, default)).Result!).Value!;
        missing.State.Should().Be(ObservedLookupState.NotObserved);
        var none = (ObservedResourceLookup)((OkObjectResult)(await controller.Lookup("other-env", InfrastructureResourceKind.ServiceBusNamespace, "x", null, null, default)).Result!).Value!;
        none.State.Should().Be(ObservedLookupState.NoSnapshot);

        var suggestions = (IReadOnlyList<AzureTargetSuggestion>)((OkObjectResult)(await controller.TargetSuggestions("orders-dev", null, default)).Result!).Value!;
        suggestions.Should().Contain(s => s.Field == "Application URL" && s.Value == "https://app-orders-dev.azurewebsites.net")
            .And.Contain(s => s.Field == "Event Hubs namespace host" && s.Value == "evhns-orders-dev.servicebus.windows.net")
            .And.OnlyContain(s => s.Basis.Contains("Not saved"));
        db.ChangeTracker.Clear();
        (await db.AzureEnvironmentSnapshots.CountAsync()).Should().Be(1, "lookups and suggestions never write");
        (await db.AzureEnvironmentSnapshots.SingleAsync()).SnapshotJson.Should().NotContain(Sentinel).And.NotContain("fake-token");
    }

    [Fact]
    public async Task Feature_visibility_off_offers_no_observed_evidence_and_keeps_stored_snapshots()
    {
        using var db = Db();
        var store = new AzureEnvironmentSnapshotStore(db);
        await store.SaveAsync(await Observed(), default);
        var provider = new AzureEnvironmentEvidenceProvider(store, enabled: false);
        (await provider.ListAsync("orders-dev")).Should().BeEmpty();
        (await provider.LookupAsync("orders-dev", InfrastructureResourceKind.ComputeApp, "app-orders-dev", null, null)).State.Should().Be(ObservedLookupState.Disabled);
        var controller = new AzureEnvironmentController(new FakeSignIn(), Collector(Full()), store, provider, new Sources());
        (await controller.Analyze(Request(), default)).Result.Should().BeOfType<ConflictObjectResult>();
        (await db.AzureEnvironmentSnapshots.CountAsync()).Should().Be(1);
    }
}
