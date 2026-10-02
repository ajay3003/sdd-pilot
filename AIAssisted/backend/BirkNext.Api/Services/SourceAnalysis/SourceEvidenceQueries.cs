using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.SourceAnalysis;

/// <summary>A typed slice of one domain's evidence of ONE snapshot, with the provenance every consumer shows (snapshot, fingerprint, analyzer version, status).</summary>
public sealed record SourceEvidenceSlice<T>(Guid SourceSnapshotId, string SourceFingerprint, SourceEvidenceDomain Domain, int AnalyzerVersion, SourceDomainStatus Status,
    IReadOnlyList<T> Items, IReadOnlyList<string> Limitations)
{
    public bool Available => Status is SourceDomainStatus.Complete or SourceDomainStatus.Partial;
    public string ShortFingerprint => SourceFingerprint[..Math.Min(8, SourceFingerprint.Length)];
}

/// <summary>
/// The read side of the shared source-evidence provider for the evidence domains: consumers ask for exactly the evidence they need, by
/// domain, category, environment, technology or link type, from an exact snapshot (resolved through <see cref="IReviewSourceEvidenceProvider"/>).
/// Pure functions over the stored snapshot — nothing is re-analyzed and nothing is interpreted: "declared", "configured" and "defined" stay
/// source facts, and what they mean for a review (exposure, readiness, compatibility) is decided by that review.
/// A snapshot analyzed before the evidence domains existed yields Status NotDetected with an explicit "not analyzed" limitation.
/// </summary>
public static class SourceEvidenceQueries
{
    public const string NotAnalyzed = "This source snapshot predates the source-evidence domains; analyze the archive again in Source Analysis.";

    private static SourceEvidenceSlice<T> Slice<T>(IqrSourceSnapshot snapshot, Func<SourceEvidenceDomainsSnapshot, SourceDomainResult> domain, Func<SourceEvidenceDomainsSnapshot, IEnumerable<T>> items, SourceEvidenceDomain kind)
    {
        if (snapshot.EvidenceDomains is not { } e) return new(snapshot.Id, snapshot.Archive.Sha256, kind, 0, SourceDomainStatus.NotDetected, [], [NotAnalyzed]);
        var d = domain(e);
        return new(snapshot.Id, e.SourceFingerprint, kind, d.AnalyzerVersion, d.Status, items(e).ToList(), d.Limitations);
    }

    // ── Infrastructure ──────────────────────────────────────────────────────────────────────────────────────────────────

    public static SourceEvidenceSlice<InfrastructureResource> Infrastructure(IqrSourceSnapshot s, params InfrastructureCategory[] categories) =>
        Slice(s, e => e.Infrastructure, e => e.Infrastructure.Resources.Where(r => categories.Length == 0 || categories.Contains(r.Category)), SourceEvidenceDomain.Infrastructure);

    public static SourceEvidenceSlice<InfrastructureResource> MessagingInfrastructure(IqrSourceSnapshot s) => Infrastructure(s, InfrastructureCategory.Messaging);

    public static SourceEvidenceSlice<InfrastructureResource> ObservabilityInfrastructure(IqrSourceSnapshot s) => Infrastructure(s, InfrastructureCategory.Observability);

    public static SourceEvidenceSlice<InfrastructureResource> NetworkInfrastructure(IqrSourceSnapshot s) => Infrastructure(s, InfrastructureCategory.Networking, InfrastructureCategory.Dns);

    public static SourceEvidenceSlice<AccessAssignment> IdentityInfrastructure(IqrSourceSnapshot s) =>
        Slice(s, e => e.Infrastructure, e => e.Infrastructure.AccessAssignments, SourceEvidenceDomain.Infrastructure);

    /// <summary>Security-relevant settings as declared (public access, TLS, HTTPS-only, local auth, encryption, CORS, credentials in IaC) with their resource.</summary>
    public static SourceEvidenceSlice<(InfrastructureResource Resource, InfrastructureSetting Setting)> SecuritySettings(IqrSourceSnapshot s) =>
        Slice(s, e => e.Infrastructure, e => e.Infrastructure.Resources.SelectMany(r => r.Settings.Where(x => x.Area is "Security" or "Networking" or "Identity").Select(x => (r, x))), SourceEvidenceDomain.Infrastructure);

    // ── Configuration ───────────────────────────────────────────────────────────────────────────────────────────────────

    public static SourceEvidenceSlice<ConfigurationEntry> Configuration(IqrSourceSnapshot s, SourceEnvironmentKind? environment = null, params ConfigurationCategory[] categories) =>
        Slice(s, e => e.Configuration, e => e.Configuration.Entries.Where(c => (categories.Length == 0 || categories.Contains(c.Category)) && (environment is null || c.Environment.Kind == environment)), SourceEvidenceDomain.Configuration);

    public static SourceEvidenceSlice<ConfigurationEntry> IntegrationConfiguration(IqrSourceSnapshot s) =>
        Configuration(s, null, ConfigurationCategory.Messaging, ConfigurationCategory.Api, ConfigurationCategory.ExternalSystems, ConfigurationCategory.Storage, ConfigurationCategory.Database);

    public static SourceEvidenceSlice<ConfigurationEntry> SecurityConfiguration(IqrSourceSnapshot s) =>
        Slice(s, e => e.Configuration, e => e.Configuration.Entries.Where(c => c.Category is ConfigurationCategory.Authentication or ConfigurationCategory.Authorization or ConfigurationCategory.Security
            || c.Sensitivity != ConfigurationSensitivity.None), SourceEvidenceDomain.Configuration);

    // ── CI/CD ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static SourceEvidenceSlice<PipelineDefinition> Pipelines(IqrSourceSnapshot s) => Slice(s, e => e.CiCd, e => e.CiCd.Pipelines, SourceEvidenceDomain.CiCd);

    public static SourceEvidenceSlice<(PipelineDefinition Pipeline, PipelineStep Step)> PipelineSteps(IqrSourceSnapshot s, params PipelineStepKind[] kinds) =>
        Slice(s, e => e.CiCd, e => e.CiCd.Pipelines.SelectMany(p => p.Steps.Where(x => kinds.Length == 0 || kinds.Contains(x.Kind)).Select(x => (p, x))), SourceEvidenceDomain.CiCd);

    /// <summary>Dependency/security checks a pipeline intends to run (dependency scans, security scans, SBOM) — for Dependency Review context.</summary>
    public static SourceEvidenceSlice<(PipelineDefinition Pipeline, PipelineStep Step)> DependencyChecks(IqrSourceSnapshot s) =>
        PipelineSteps(s, PipelineStepKind.DependencyScan, PipelineStepKind.SecurityScan, PipelineStepKind.Sbom);

    // ── Contracts ───────────────────────────────────────────────────────────────────────────────────────────────────────

    public static SourceEvidenceSlice<SourceContract> Contracts(IqrSourceSnapshot s, params SourceContractType[] types) =>
        Slice(s, e => e.Contracts, e => e.Contracts.Contracts.Where(c => types.Length == 0 || types.Contains(c.Type)), SourceEvidenceDomain.Contracts);

    public static SourceEvidenceSlice<SourceContract> MessageContracts(IqrSourceSnapshot s) => Contracts(s, SourceContractType.MessageContract, SourceContractType.AsyncApi, SourceContractType.Protobuf);

    // ── Cross-domain ────────────────────────────────────────────────────────────────────────────────────────────────────

    public static SourceEvidenceSlice<SourceEvidenceLink> Links(IqrSourceSnapshot s, params SourceEvidenceLinkType[] types) =>
        Slice(s, e => e.CrossDomain, e => e.CrossDomain.Links.Where(l => types.Length == 0 || types.Contains(l.Type)), SourceEvidenceDomain.CrossDomain);

    public static SourceEvidenceSlice<SourceObservabilityLayer> ObservabilityLayers(IqrSourceSnapshot s) =>
        Slice(s, e => e.CrossDomain, e => e.CrossDomain.ObservabilityLayers, SourceEvidenceDomain.CrossDomain);
}
