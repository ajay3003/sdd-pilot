using System.Text.Json;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using FluentAssertions;
using static BirkNext.Api.Tests.Services.SourceAnalysis.Evidence.SourceEvidenceFixtures;

namespace BirkNext.Api.Tests.Services.SourceAnalysis.Evidence;

/// <summary>
/// Terraform-derived values reach consumers only as Source Analysis Infrastructure evidence: per-environment declared names, the shared
/// configured-vs-source comparer, IQR comparisons stored with the run, read-only suggestions, and no direct Terraform parsing anywhere else.
/// Declared ≠ Configured ≠ Observed ≠ Verified. Generic "Acme.Payments" fixture with two environments — no project-specific names.
/// </summary>
public sealed class InfrastructureConsumerMigrationTests
{
    private const string Main = """
        variable "env" { default = "dev" }
        variable "event_secret" {
          sensitive = true
          default   = "SECRET_SENTINEL_TF_EVENT"
        }
        resource "azurerm_eventhub_namespace" "payments" { name = "evhns-payments-${var.env}" }
        resource "azurerm_eventhub" "settlements" {
          name         = "settlements"
          namespace_id = azurerm_eventhub_namespace.payments.id
        }
        resource "azurerm_eventhub_consumer_group" "ledger" {
          name        = "ledger-${var.env}"
          eventhub_id = azurerm_eventhub.settlements.id
        }
        resource "azurerm_storage_account" "checkpoints" { name = "stpay${var.env}ckpt" }
        resource "azurerm_storage_container" "checkpoints" { name = "ledger-checkpoints" }
        resource "azurerm_application_insights" "main" { name = "appi-payments-${var.env}" }
        resource "azurerm_servicebus_namespace" "bus" { name = "sbns-payments-${var.env}" }
        resource "azurerm_servicebus_topic" "paid" {
          name         = "payment-completed"
          namespace_id = azurerm_servicebus_namespace.bus.id
        }
        resource "azurerm_postgresql_flexible_server" "db" { name = "psql-payments-${var.env}" }
        resource "azurerm_user_assigned_identity" "worker" { name = "id-payments-worker-${var.env}" }
        resource "azurerm_private_endpoint" "db" { name = "pe-payments-db-${var.env}" }
        resource "azurerm_redis_cache" "unresolved" { name = lower(format("redis-%s", var.env)) }
        """;

    private static (string, string)[] Payments() =>
    [
        ("Acme.Payments.sln", ""),
        ("Infrastructure/main.tf", Main),
        ("Infrastructure/qa.tfvars", "env = \"qa\"\nevent_secret = \"SECRET_SENTINEL_TFVARS\"\n"),
        ("Infrastructure/prod.tfvars", "env = \"prod\"\n"),
    ];

    private static InfrastructureEvidence Infra() => Analyze(Payments()).Infrastructure;

    [Fact]
    public void Declared_names_are_resolved_per_environment_from_tfvars()
    {
        var ns = Infra().Resources.Single(r => r.ResourceType == "azurerm_eventhub_namespace");
        ns.DeclaredName.Should().Be("evhns-payments-dev", "the default comes from the variable default");
        ns.EnvironmentNames.Should().BeEquivalentTo([
            new InfrastructureEnvironmentName(new(SourceEnvironmentKind.QA, "qa"), "evhns-payments-qa", "tfvars Infrastructure/qa.tfvars"),
            new InfrastructureEnvironmentName(new(SourceEnvironmentKind.Production, "prod"), "evhns-payments-prod", "tfvars Infrastructure/prod.tfvars")]);
        Infra().AnalyzerVersion.Should().Be(2);
        JsonSerializer.Serialize(Infra()).Should().NotContain(Sentinel);
    }

    [Fact]
    public void Shared_identity_normalizes_hosts_and_kinds_once()
    {
        InfrastructureIdentity.NormalizeName("https://StPayQaCkpt.blob.core.windows.net/").Should().Be("stpayqackpt");
        InfrastructureIdentity.NormalizeName("sb://sbns-payments-qa.servicebus.windows.net/").Should().Be("sbns-payments-qa");
        InfrastructureIdentity.HostKind("x.vault.azure.net").Should().Be(InfrastructureResourceKind.SecretStore);
        var cg = Infra().Resources.Single(r => r.ResourceType == "azurerm_eventhub_consumer_group");
        InfrastructureIdentity.Identity(cg).Should().Match<InfrastructureResourceIdentity>(i => i.Kind == InfrastructureResourceKind.ConsumerGroup && i.Parent == "Infrastructure/azurerm_eventhub.settlements");
        SourceEnvironments.FromName("payments-qa-env")!.Kind.Should().Be(SourceEnvironmentKind.QA);
        SourceEnvironments.FromName("customer-portal").Should().BeNull();
    }

    [Fact]
    public void Comparison_states_are_neutral_and_environment_scoped()
    {
        var infra = Infra();
        var id = Guid.NewGuid();
        SourceInfrastructureComparison C(string? configured, SourceEnvironmentKind? env, InfrastructureResourceKind kind = InfrastructureResourceKind.EventHubNamespace) =>
            SourceInfrastructureComparer.Compare("Namespace", configured, infra, id, kind, env);

        var match = C("evhns-payments-qa.servicebus.windows.net", SourceEnvironmentKind.QA);
        match.State.Should().Be(SourceComparisonState.Matches);
        match.EnvironmentBasis.Should().Be("explicit environment variable file");
        match.SourceSnapshotId.Should().Be(id);
        C("evhns-other", SourceEnvironmentKind.QA).State.Should().Be(SourceComparisonState.Differs, "a difference needs review — it is not a failure");
        var empty = C(null, SourceEnvironmentKind.QA);
        empty.State.Should().Be(SourceComparisonState.NoConfiguredValue);
        empty.Candidates.Should().ContainSingle().Which.Name.Should().Be("evhns-payments-qa", "QA is never answered with the Production name by list order");
        C(null, null).State.Should().Be(SourceComparisonState.MultipleSourceCandidates, "without a target environment the dev/qa/prod names need explicit selection");
        C(null, SourceEnvironmentKind.QA, InfrastructureResourceKind.MessagingNamespace).State.Should().Be(SourceComparisonState.MultipleSourceCandidates, "a bare *.servicebus.windows.net kind fits both namespaces — selection needed, not a guess");
        C(null, SourceEnvironmentKind.Staging).Candidates.Should().BeEmpty("no declaration is tied to Staging and every one is tied to another environment");
        C("redis-qa", SourceEnvironmentKind.QA, InfrastructureResourceKind.Cache).State.Should().Be(SourceComparisonState.Unresolved, "a computed name is unresolved, not guessed by a consumer");
        SourceInfrastructureComparer.Compare("Namespace", "x", null, null, InfrastructureResourceKind.MessagingNamespace, null).State.Should().Be(SourceComparisonState.SourceUnavailable);
    }

    private static IntegrationCatalog Catalog(string? ns = "evhns-payments-qa", string? group = null) => new()
    {
        EnvironmentId = "payments-qa",
        Platforms =
        [
            new IntegrationPlatform
            {
                Id = "eh", Name = "Payments Event Hubs", Kind = IntegrationKind.EventHub, Namespace = ns,
                RuntimeEvidence = new IntegrationRuntimeEvidenceSettings { ExpectedConsumerGroup = group, CheckpointBlobEndpoint = "https://stpayqackpt.blob.core.windows.net/", ApplicationInsightsResourceName = "appi-legacy" },
            },
            new IntegrationPlatform
            {
                Id = "sb", Name = "Payments Service Bus", Kind = IntegrationKind.ServiceBus, Namespace = "sbns-payments-qa",
                ServiceBusTopology = new() { Source = "Seeded audit (no snapshot)", Entities = [new() { EntityType = BirkNext.Integrations.ServiceBusEntityType.Topic, Name = "payment-completed" }] },
            },
        ],
        Integrations = [new IntegrationDefinition { Id = "settlements", PlatformId = "eh", Kind = IntegrationKind.EventHub, EndpointOrTopic = "settlements" }],
    };

    private static IqrSourceSnapshot Snapshot()
    {
        var evidence = Analyze(Payments());
        return new IqrSourceSnapshot { Id = evidence.SourceSnapshotId, Archive = new("payments.zip", evidence.SourceFingerprint, 4), EvidenceDomains = evidence };
    }

    [Fact]
    public void Iqr_compares_the_configured_catalog_with_source_and_never_overwrites_it()
    {
        var catalog = Catalog();
        var before = JsonSerializer.Serialize(catalog);
        var snapshot = Snapshot();
        var result = IqrSourceReview.Augment(new IntegrationReviewResult { EnvironmentName = "Payments QA", ConfigurationSnapshot = catalog,
            Domains = [new IntegrationDomainResult { Domain = IntegrationReviewDomain.Configuration, StateLabel = "Not assessed" }] }, [snapshot]);
        JsonSerializer.Serialize(result.ConfigurationSnapshot).Should().Be(before, "configured values stay configured — source never writes them");
        var rows = result.SourceInfrastructureComparisons.ToDictionary(r => r.Comparison.Field, r => r.Comparison);
        rows["Event Hubs namespace"].State.Should().Be(SourceComparisonState.Matches);
        rows["Event Hub"].State.Should().Be(SourceComparisonState.Matches);
        rows["Consumer group"].State.Should().Be(SourceComparisonState.NoConfiguredValue);
        rows["Consumer group"].Candidates.Single().Name.Should().Be("ledger-qa");
        rows["Checkpoint storage account"].State.Should().Be(SourceComparisonState.Matches);
        rows["Application Insights"].State.Should().Be(SourceComparisonState.Differs);
        rows["Service Bus namespace"].State.Should().Be(SourceComparisonState.Matches);
        rows["Service Bus topic payment-completed"].State.Should().Be(SourceComparisonState.Matches);
        result.SourceInfrastructureComparisons.Should().OnlyContain(c => c.Comparison.SourceSnapshotId == snapshot.Id && c.Comparison.SourceFingerprint == snapshot.Archive.Sha256 && c.Comparison.AnalyzerVersion == 2);
        result.SourceInfrastructureComparisons.Should().OnlyContain(c => c.Comparison.RuntimeState.Contains("assessed separately"));
        result.EvidenceSources.Should().Contain(IntegrationEvidenceSource.SourceInfrastructure);
        result.Domains.Single().Observed.Should().Contain(o => o.StartsWith("Configured vs. declared in source"));
        JsonSerializer.Serialize(result.SourceInfrastructureComparisons).Should().NotContain(Sentinel);
    }

    [Fact]
    public void Runtime_without_source_and_legacy_seeded_values_stay_valid_without_fake_provenance()
    {
        var catalog = Catalog();
        var legacy = new IqrSourceSnapshot { Archive = new("old.zip", "0123456789abcdef", 1) };
        var result = IqrSourceReview.Augment(new IntegrationReviewResult { ConfigurationSnapshot = catalog }, [legacy]);
        result.SourceInfrastructureComparisons.Should().BeEmpty("no Infrastructure evidence → no comparison, nothing invented");
        result.EvidenceSources.Should().NotContain(IntegrationEvidenceSource.SourceInfrastructure);
        result.ConfigurationSnapshot.Platforms.Single(p => p.Id == "sb").ServiceBusTopology!.Source.Should().Be("Seeded audit (no snapshot)");
        IqrSourceReview.Augment(new IntegrationReviewResult { ConfigurationSnapshot = catalog }, []).SourceInfrastructureComparisons.Should().BeEmpty();
        IntegrationRuntimeEvidenceSettings.ProvenanceLabel(IntegrationValueProvenance.SourceConfigurationVerified).Should().Contain("not deployment-verified");
        IntegrationReviewLabels.Source(IntegrationEvidenceSource.Infrastructure).Should().Be("Audited infrastructure configuration");
    }

    [Fact]
    public void Suggestions_use_the_exact_snapshot_or_the_newest_with_evidence_and_never_substitute()
    {
        var older = Snapshot() with { AnalyzedAt = DateTimeOffset.Parse("2026-10-01T00:00:00Z") };
        var newer = Snapshot() with { Id = Guid.NewGuid(), AnalyzedAt = DateTimeOffset.Parse("2026-10-02T00:00:00Z") };
        var latest = SourceInfrastructureSuggestions.Suggest([newer, older], null, InfrastructureResourceKind.ConsumerGroup, "Consumer group", null, "payments-qa", "settlements");
        latest.SnapshotId.Should().Be(newer.Id);
        latest.SnapshotBasis.Should().Be("newest snapshot with infrastructure evidence");
        latest.Comparison.Candidates.Single().Name.Should().Be("ledger-qa");
        var exact = SourceInfrastructureSuggestions.Suggest([newer, older], older.Id, InfrastructureResourceKind.ConsumerGroup, "Consumer group", null, "payments-qa", null);
        exact.SnapshotId.Should().Be(older.Id, "a review bound to snapshot A keeps A even when B is newer");
        var missing = SourceInfrastructureSuggestions.Suggest([newer], Guid.NewGuid(), InfrastructureResourceKind.ConsumerGroup, "Consumer group", null, null, null);
        missing.Comparison.State.Should().Be(SourceComparisonState.SourceUnavailable);
        missing.SnapshotId.Should().BeNull("nothing is substituted");
        SourceInfrastructureSuggestions.Suggest([newer], null, InfrastructureResourceKind.ConsumerGroup, "Consumer group", null, null, null, sourceAnalysisEnabled: false)
            .SourceAnalysisEnabled.Should().BeFalse();
    }

    [Fact]
    public void Terraform_state_files_are_never_read()
    {
        var (workspace, error) = IqrSourceArchiveReader.Read("x.zip", Zip(("infra/terraform.tfstate", """{ "outputs": { "key": { "value": "SECRET_SENTINEL_STATE" } } }"""), ("infra/main.tf", Main)));
        error.Should().BeNull();
        workspace!.Limitations.Should().Contain(l => l.Contains("Terraform state file present but not read"));
        workspace.Files.Concat(workspace.ConfigurationFiles ?? []).Concat(workspace.EvidenceFiles ?? []).Should().NotContain(f => f.Content.Contains(Sentinel + "_STATE"));
    }

    /// <summary>Architecture guard: only the Source Analysis Infrastructure analyzer parses Terraform/HCL; every other reference is allow-listed with a reason.</summary>
    [Fact]
    public void No_consumer_parses_terraform_outside_source_analysis()
    {
        var api = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../BirkNext.Api"));
        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Services/Integrations/SourceEvidence/IqrSourceArchiveReader.cs"] = "archive intake: keeps .tf/.tfvars in memory for Source Analysis, skips .tfstate",
            ["Services/Integrations/M2lbDevIntegrationSeed.cs"] = "DEV seed of configured values from a developer-side audit (comments only; nothing parsed)",
            ["Services/Integrations/M2lbDevScimSeed.cs"] = "DEV seed of configured values from a developer-side audit (comments only; nothing parsed)",
            ["Services/Integrations/M2lbDevServiceBusSeed.cs"] = "DEV seed of configured values from a developer-side audit (comments only; nothing parsed)",
        };
        var offenders = Directory.GetFiles(api, "*.cs", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(api, f).Replace('\\', '/')))
            .Where(f => !f.Rel.StartsWith("obj/") && !f.Rel.StartsWith("bin/") && !f.Rel.StartsWith("Services/SourceAnalysis/Evidence/") && !allowed.ContainsKey(f.Rel))
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f.Full), @"HclReader|""\.tfvars?""|\.tfvars\b|EndsWith\(""\.tf""|\bresource\s+""azurerm_|terraform\s+(show|output|state|plan)"))
            .Select(f => f.Rel).ToList();
        offenders.Should().BeEmpty("Terraform is parsed only by Source Analysis Infrastructure; consumers read shared evidence");
    }
}
