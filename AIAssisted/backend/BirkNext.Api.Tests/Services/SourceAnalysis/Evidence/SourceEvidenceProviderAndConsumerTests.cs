using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.DependencyReview;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SecurityClassification;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.Integrations;
using BirkNext.SecurityExpectations;
using BirkNext.SourceDomains;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static BirkNext.Api.Tests.Services.SourceAnalysis.Evidence.SourceEvidenceFixtures;

namespace BirkNext.Api.Tests.Services.SourceAnalysis.Evidence;

/// <summary>The evidence domains as part of Source Analysis ingestion (same immutable snapshot, analyzer versions, no rescans) and their
/// consumers: the shared query API, IQR, Security Expectations, Security Classification and Dependency Review — each keeping its own semantics.</summary>
public sealed class SourceEvidenceProviderAndConsumerTests
{
    private static async Task<IqrSourceSnapshot> Ingest(params (string Path, string Content)[] files)
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var (snapshot, error) = await new IqrSourceStore(db).AnalyzeAsync("dev", IqrSourceStore.SourceAnalysisOwner, "acme.zip", Zip(files));
        error.Should().BeNull();
        return snapshot!;
    }

    // ── Registry ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_orders_analyzers_by_declared_dependencies_not_registration_order()
    {
        var registry = new SourceEvidenceAnalyzerRegistry([new CrossDomainLinker(), new ContractAnalyzer(), new ConfigurationAnalyzer(), new PipelineAnalyzer(), new InfrastructureAnalyzer()]);
        var order = registry.Ordered.Select(a => a.Info.Domain).ToList();
        order.IndexOf(SourceEvidenceDomain.Configuration).Should().BeGreaterThan(order.IndexOf(SourceEvidenceDomain.Infrastructure)).And.BeGreaterThan(order.IndexOf(SourceEvidenceDomain.CiCd));
        order.Last().Should().Be(SourceEvidenceDomain.CrossDomain);
        registry.Ordered.Should().OnlyContain(a => a.Info.Version >= 1 && a.Info.Produces.Count > 0);
    }

    private sealed class Fake(SourceEvidenceDomain domain, int stage, params SourceEvidenceDomain[] dependsOn) : ISourceEvidenceDomainAnalyzer
    {
        public DomainAnalyzerInfo Info { get; } = new(domain, $"fake {domain}", 1, stage, [], [.. dependsOn], ["x"]);
        public void Analyze(SourceEvidenceContext context, CancellationToken ct) => throw new InvalidOperationException("SECRET_SENTINEL in an exception message");
        public void Failed(SourceEvidenceContext context, string reason) => context.Contracts = context.Envelope(new ContractEvidence { Status = SourceDomainStatus.FailedAnalysis, StatusReason = reason }, domain, 1);
    }

    [Fact]
    public void Registry_rejects_cycles_and_a_failing_analyzer_fails_only_its_own_domain()
    {
        var cycle = () => new SourceEvidenceAnalyzerRegistry([new Fake(SourceEvidenceDomain.Infrastructure, 1, SourceEvidenceDomain.Configuration), new Fake(SourceEvidenceDomain.Configuration, 1, SourceEvidenceDomain.Infrastructure)]);
        cycle.Should().Throw<InvalidOperationException>().WithMessage("*cycle*");

        var (workspace, _) = IqrSourceArchiveReader.Read("x.zip", Zip(SourceEvidenceFixtures.Acme()));
        var input = BirkNext.Api.Services.SourceArchitecture.ArchitectureInput.From(Guid.NewGuid(), workspace!);
        var registry = new SourceEvidenceAnalyzerRegistry([new InfrastructureAnalyzer(), new Fake(SourceEvidenceDomain.Contracts, 1)]);
        var result = SourceEvidenceAnalyzer.Analyze(Guid.NewGuid(), workspace!, input, DateTimeOffset.UtcNow, null, null, null, null, default, out _, registry);
        result.Contracts.Status.Should().Be(SourceDomainStatus.FailedAnalysis);
        result.Contracts.StatusReason.Should().NotContain(Sentinel, "exception text could quote source");
        result.Infrastructure.Status.Should().Be(SourceDomainStatus.Partial, "other domains are unaffected");
    }

    // ── Ingestion, snapshot binding, history ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ingestion_binds_every_domain_to_the_same_snapshot_and_never_persists_secrets()
    {
        var s = await Ingest(SourceEvidenceFixtures.Acme());
        var e = s.EvidenceDomains!;
        e.SourceSnapshotId.Should().Be(s.Id);
        e.SourceFingerprint.Should().Be(s.Archive.Sha256);
        e.Domains.Should().OnlyContain(d => d.SourceSnapshotId == s.Id && d.SourceFingerprint == s.Archive.Sha256 && d.AnalyzerVersion >= 1 && d.ExtractedAt == s.AnalyzedAt);
        e.Analyzers.Select(a => a.Domain).Should().Contain([SourceEvidenceDomain.Infrastructure, SourceEvidenceDomain.Configuration, SourceEvidenceDomain.CiCd, SourceEvidenceDomain.Contracts, SourceEvidenceDomain.CrossDomain]);
        e.Capabilities.Should().Contain(c => c.Technology == "Terraform" && c.Support == DomainSupport.Supported)
            .And.Contain(c => c.Technology == "Bicep" && c.Support == DomainSupport.Partial)
            .And.Contain(c => c.Support == DomainSupport.Unsupported);
        e.FileRoles.Should().Contain(r => r.Role == SourceFileRole.InfrastructureAsCode && r.Files == 6);
        var json = JsonSerializer.Serialize(s, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().NotContain(Sentinel);
        s.Limitations.Should().NotContain(l => l.Contains(".tf source"), "Terraform is analyzed now, not reported as an unsupported language");
    }

    [Fact]
    public void A_historical_snapshot_without_evidence_domains_is_reported_not_reinterpreted()
    {
        var historical = new IqrSourceSnapshot { Archive = new("old.zip", "abc12345", 1) };
        var slice = SourceEvidenceQueries.MessagingInfrastructure(historical);
        slice.Available.Should().BeFalse();
        slice.Items.Should().BeEmpty();
        slice.Limitations.Should().Contain(SourceEvidenceQueries.NotAnalyzed);
    }

    [Fact]
    public async Task Queries_return_only_the_requested_slice_with_provenance()
    {
        var s = await Ingest(SourceEvidenceFixtures.Acme());
        var messaging = SourceEvidenceQueries.MessagingInfrastructure(s);
        messaging.Items.Should().OnlyContain(r => r.Category == InfrastructureCategory.Messaging).And.HaveCount(3);
        messaging.SourceSnapshotId.Should().Be(s.Id);
        messaging.ShortFingerprint.Should().Be(s.Archive.Sha256[..8]);
        SourceEvidenceQueries.Configuration(s, SourceEnvironmentKind.QA).Items.Should().OnlyContain(e => e.Environment.Kind == SourceEnvironmentKind.QA);
        SourceEvidenceQueries.DependencyChecks(s).Items.Should().ContainSingle(x => x.Step.Kind == PipelineStepKind.DependencyScan);
        SourceEvidenceQueries.IdentityInfrastructure(s).Items.Should().ContainSingle();
        SourceEvidenceQueries.Contracts(s, SourceContractType.OpenApi).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Changes_between_snapshots_are_source_changes_never_drift()
    {
        var a = await Ingest(SourceEvidenceFixtures.Acme());
        var b = await Ingest(SourceEvidenceFixtures.Acme().Select(f => f.Path == "Infrastructure/messaging.tf" ? (f.Path, f.Content.Replace("max_delivery_count = 10", "max_delivery_count = 5")) : f).ToArray());
        var changes = SourceEvidenceDiff.Compare(a.EvidenceDomains!, b.EvidenceDomains!);
        changes.Should().ContainSingle(c => c.Domain == SourceEvidenceDomain.Infrastructure && c.Kind == SourceEvidenceChangeKind.Changed && c.Key.EndsWith("azurerm_servicebus_subscription.fulfilment"));
        a.EvidenceDomains!.Id.Should().NotBe(b.EvidenceDomains!.Id, "a newer snapshot never rewrites historical evidence");
    }

    // ── Consumers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Iqr_reads_source_domains_from_the_snapshot_and_keeps_runtime_independent()
    {
        var s = await Ingest(SourceEvidenceFixtures.Acme());
        var domains = Enum.GetValues<IntegrationReviewDomain>().Select(d => new IntegrationDomainResult { Domain = d, StateLabel = "Not assessed" }).ToList();
        var result = IqrSourceReview.Augment(new IntegrationReviewResult { Domains = domains }, [s]);
        var flow = result.Domains.Single(d => d.Domain == IntegrationReviewDomain.MessageFlow);
        flow.Observed.Should().Contain(o => o.Contains("Messaging declared in IaC") && o.Contains("Service Bus topic"));
        flow.Missing.Should().Contain(IqrSourceDomainsReview.RuntimeLimitation);
        flow.StateLabel.Should().Be("Partially assessed", "source evidence never makes a domain fully assessed");
        result.Domains.Single(d => d.Domain == IntegrationReviewDomain.Configuration).Observed.Should().Contain(o => o.StartsWith("Configured in source"));
        result.Domains.Single(d => d.Domain == IntegrationReviewDomain.Connectivity).Missing.Should().Contain(m => m.Contains("reachability"));
        result.Domains.Single(d => d.Domain == IntegrationReviewDomain.Security).Observed.Should().Contain(o => o.Contains("access assignment"));
        result.Domains.Single(d => d.Domain == IntegrationReviewDomain.Observability).Observed.Should().Contain(o => o.Contains("Runtime telemetry — Not assessed"));
        result.Domains.SelectMany(d => d.Observed).Should().NotContain(o => o.Contains("Observed in Azure") || o.Contains("Pass"));
    }

    [Fact]
    public async Task Security_expectations_consume_normalized_configuration_and_keep_candidate_semantics()
    {
        var files = SourceEvidenceFixtures.Acme().Append(("src/Acme.Ordering.Web/.env", "AzureAd__Authority=https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0\n")).ToArray();
        var s = await Ingest(files);
        var candidates = s.SecurityExpectationsEvidence!.Candidates;
        candidates.Should().Contain(c => c.FieldType == SecurityExpectationField.ClientId && c.SourceFile.EndsWith("appsettings.json"));
        candidates.Should().Contain(c => c.FieldType == SecurityExpectationField.Authority && c.SourceFile.EndsWith(".env"), "normalized configuration includes the project's .env");
        candidates.Should().OnlyContain(c => c.SourceSnapshotId == s.Id && c.CandidateState != SecurityCandidateState.Accepted);
        candidates.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        candidates.Should().NotContain(c => c.Value.Contains(Sentinel));
    }

    [Fact]
    public async Task Classification_and_dependency_review_get_context_only_summaries()
    {
        var s = await Ingest(SourceEvidenceFixtures.Acme());
        ClassificationSourceScopeService.SourceSecurityContext(s).Should().Contain("IaC access assignment");
        DependencyReviewSourceScopeService.Evidence(s).Summary.Should().Contain("pipeline dependency/security check(s) defined");
        DependencyReviewSourceScopeService.PipelineChecks(new IqrSourceSnapshot { Archive = new("old.zip", "abc", 1) }).Should().BeEmpty("historical snapshots keep their old summary");
    }

    // ── Genericity ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Evidence_domain_production_code_has_no_project_specific_branches()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../BirkNext.Api/Services/SourceAnalysis"));
        var files = Directory.GetFiles(Path.Combine(root, "Evidence"), "*.cs").Append(Path.Combine(root, "SourceEvidenceQueries.cs")).ToList();
        files.Should().NotBeEmpty();
        foreach (var file in files)
            File.ReadAllText(file).Should().NotContainAny(["M2LB", "m2lb", "Bufdir", "bufdir", "BiRK", "Hendelse"], $"{Path.GetFileName(file)} must stay generic");
    }
}
