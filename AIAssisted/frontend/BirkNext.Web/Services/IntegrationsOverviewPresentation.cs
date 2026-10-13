using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.Web.Services;

/// <summary>How strongly the analyzed source supports a candidate's consumer. Never a mapping state: nothing here is confirmed.</summary>
public enum SuggestionStrength { StronglySupported, Suggested, Unassigned, Conflict }

/// <summary>Source suggestions at a glance. <see cref="Available"/> is false when no source snapshot has been analyzed.</summary>
public sealed record SuggestionSummary(bool Available, int Total, int StronglySupported, int Suggested, int Unassigned, int Conflicts);

/// <summary>
/// What Terraform in Source Analysis says about a platform's configured environment values. Without Terraform a seeded value stays a
/// <see cref="SeededAssumption"/> (never "verified"); a difference is <see cref="NeedsReview"/>, never an automatic overwrite.
/// </summary>
public enum TerraformSupport { NotCompared, SeededAssumption, Supported, NeedsReview }

/// <summary>Runtime evidence configuration of an integration or environment: configuration only, never observed health.</summary>
public enum RuntimeEvidenceStatus { NotConfigured, Partial, Configured }

/// <summary>
/// Target Environment → Integrations overview: configured integrations, suggestions from Source Analysis and runtime-evidence setup. It reads
/// the catalog and the source report; it never re-derives source evidence (Source Analysis owns that) and never confirms a mapping.
/// </summary>
public static class IntegrationsOverviewPresentation
{
    public static SuggestionStrength Strength(SourceIntegrationCandidate candidate) => candidate switch
    {
        { ConsumerEvidence: ArchitectureEvidenceState.Conflict } => SuggestionStrength.Conflict,
        { ConsumerCandidate: null } or { ConsumerEvidence: ArchitectureEvidenceState.Unresolved } => SuggestionStrength.Unassigned,
        { ConsumerEvidence: ArchitectureEvidenceState.Confirmed or ArchitectureEvidenceState.StronglySupported } => SuggestionStrength.StronglySupported,
        _ => SuggestionStrength.Suggested,
    };

    public static string Label(SuggestionStrength strength) => strength switch
    {
        SuggestionStrength.StronglySupported => "Strongly supported",
        SuggestionStrength.Suggested => "Suggested",
        SuggestionStrength.Conflict => "Conflict",
        _ => "Unassigned",
    };

    public static string Tone(SuggestionStrength strength) => strength switch
    {
        SuggestionStrength.StronglySupported => "info",
        SuggestionStrength.Conflict => "attention",
        _ => "muted",
    };

    /// <summary>Candidates from the latest source discovery that no configured integration covers (business channels only).</summary>
    public static IReadOnlyList<SourceIntegrationCandidate> Suggestions(SourceIntegrationsReport? report) => SourceIntegrationsPresentation.Unconfigured(report);

    public static SuggestionSummary Summary(SourceIntegrationsReport? report)
    {
        if (report?.Discovery is null) return new(false, 0, 0, 0, 0, 0);
        var suggestions = Suggestions(report).Select(Strength).ToList();
        return new(true, suggestions.Count, suggestions.Count(s => s == SuggestionStrength.StronglySupported), suggestions.Count(s => s == SuggestionStrength.Suggested),
            suggestions.Count(s => s == SuggestionStrength.Unassigned), suggestions.Count(s => s == SuggestionStrength.Conflict));
    }

    /// <summary>The default review list: strongly supported mappings and conflicts that need action, strongest first. "Show all" lists everything.</summary>
    public static IReadOnlyList<SourceIntegrationCandidate> Prioritized(SourceIntegrationsReport? report, bool showAll) =>
        Suggestions(report)
            .Where(c => showAll || Strength(c) is SuggestionStrength.StronglySupported or SuggestionStrength.Conflict)
            .OrderBy(c => Strength(c) switch { SuggestionStrength.Conflict => 0, SuggestionStrength.StronglySupported => 1, SuggestionStrength.Suggested => 2, _ => 3 })
            .ThenBy(c => c.SourceEntity ?? c.ChannelName, StringComparer.OrdinalIgnoreCase).ToList();

    public static IntegrationKind Kind(IntegrationTransport transport) => transport switch
    {
        IntegrationTransport.EventHub or IntegrationTransport.Kafka => IntegrationKind.EventHub,
        IntegrationTransport.ServiceBus or IntegrationTransport.ServiceBusQueue or IntegrationTransport.ServiceBusTopic => IntegrationKind.ServiceBus,
        IntegrationTransport.Http or IntegrationTransport.GraphQl => IntegrationKind.HttpApi,
        IntegrationTransport.File or IntegrationTransport.Blob => IntegrationKind.File,
        _ => IntegrationKind.Other,
    };

    /// <summary>
    /// A configured integration built from a source suggestion, for a person to add. Origin = source suggestion; the consumer stays a
    /// suggestion (or unassigned) — adding is not confirming, and the backend refuses to store it as confirmed.
    /// </summary>
    public static IntegrationDefinition FromCandidate(IntegrationCatalog catalog, SourceIntegrationCandidate candidate, Guid? snapshotId)
    {
        var kind = Kind(candidate.Transport);
        var platforms = catalog.Platforms.Where(p => p.Kind == kind).ToList();
        return new IntegrationDefinition
        {
            DisplayName = $"{candidate.SourceEntity ?? SourceIntegrationsPresentation.ChannelShort(candidate.ChannelName)} ({SourceIntegrationLabels.Transport(candidate.Transport)})",
            Kind = kind, Enabled = true, PlatformId = platforms.Count == 1 ? platforms[0].Id : null,
            EndpointOrTopic = candidate.ChannelName, SourceSystem = candidate.SourceSystem,
            SourceResource = candidate.SourceDatabase is { } db && candidate.SourceEntity is { } entity ? $"{db}.{candidate.SourceSchema ?? "dbo"}.{entity}" : null,
            Producer = candidate.CaptureTechnology ?? candidate.ProducerComponent ?? candidate.CallerTechnology,
            Consumer = candidate.ConsumerCandidate is { } consumer
                ? new IntegrationConsumer
                {
                    DisplayName = consumer, MappingState = ConsumerMappingState.Suggested, MappingSourceSnapshotId = snapshotId,
                    MappingSource = $"Source Analysis suggestion ({Label(Strength(candidate)).ToLowerInvariant()})",
                }
                : new IntegrationConsumer { MappingState = ConsumerMappingState.NeedsConfirmation },
            Origin = IntegrationRecordOrigin.SourceSuggestion,
        };
    }

    /// <summary>Whether the catalog already configures this candidate's channel (the backend refuses the duplicate too).</summary>
    public static bool AlreadyConfigured(IntegrationCatalog catalog, SourceIntegrationCandidate candidate) =>
        candidate.ChannelName is { Length: > 0 } channel
        && catalog.Integrations.Any(i => i.Kind == Kind(candidate.Transport) && string.Equals(i.EndpointOrTopic?.Trim(), channel.Trim(), StringComparison.OrdinalIgnoreCase));

    public static RuntimeEvidenceStatus Runtime(IntegrationPlatform? platform)
    {
        if (platform is null) return RuntimeEvidenceStatus.NotConfigured;
        switch (platform.Kind)
        {
            case IntegrationKind.EventHub:
                var (configured, total) = EventHubRuntimeSources.Count(platform);
                return configured == 0 ? RuntimeEvidenceStatus.NotConfigured : configured == total ? RuntimeEvidenceStatus.Configured : RuntimeEvidenceStatus.Partial;
            case IntegrationKind.ServiceBus:
                return string.IsNullOrWhiteSpace(platform.RuntimeEvidence?.SubscriptionId) ? RuntimeEvidenceStatus.NotConfigured : RuntimeEvidenceStatus.Configured;
            case IntegrationKind.IdentityProvisioning:
                return string.IsNullOrWhiteSpace(platform.ScimProvisioning?.BaseUrl) ? RuntimeEvidenceStatus.NotConfigured : RuntimeEvidenceStatus.Configured;
            default:
                return RuntimeEvidenceStatus.NotConfigured;
        }
    }

    /// <summary>The environment's runtime-evidence setup: Not configured while nothing is configured or no platform has any source.</summary>
    public static RuntimeEvidenceStatus Runtime(IntegrationCatalog catalog)
    {
        var used = catalog.Platforms.Where(p => catalog.Integrations.Any(i => i.PlatformId == p.Id) || p.Kind == IntegrationKind.IdentityProvisioning).Select(Runtime).ToList();
        if (catalog.Integrations.Count == 0 || used.Count == 0 || used.All(s => s == RuntimeEvidenceStatus.NotConfigured)) return RuntimeEvidenceStatus.NotConfigured;
        return used.All(s => s == RuntimeEvidenceStatus.Configured) ? RuntimeEvidenceStatus.Configured : RuntimeEvidenceStatus.Partial;
    }

    public static string Label(RuntimeEvidenceStatus status) => status switch
    {
        RuntimeEvidenceStatus.Configured => "Configured",
        RuntimeEvidenceStatus.Partial => "Partial",
        _ => "Not configured",
    };

    public static string Tone(RuntimeEvidenceStatus status) => status switch
    {
        RuntimeEvidenceStatus.Configured => "ready",
        RuntimeEvidenceStatus.Partial => "info",
        _ => "muted",
    };

    /// <summary>Where a configured record came from. A seeded value is a template value, never discovered evidence.</summary>
    public static string Origin(IntegrationDefinition definition) => definition.Origin switch
    {
        IntegrationRecordOrigin.Seed => definition.UserModified ? "Seed · modified" : "Seed",
        IntegrationRecordOrigin.ImportedFromBrowserProfile => "Imported",
        IntegrationRecordOrigin.SourceSuggestion => "Source suggestion",
        _ => "Manual",
    };

    /// <summary>"Development · m2lbdev.bufetat.no" — the environment's type and host, from its profile.</summary>
    public static string Environment(FrontendAnalysisProfile profile) =>
        Uri.TryCreate(profile.TargetUrl, UriKind.Absolute, out var uri) ? $"{profile.EnvironmentType} · {uri.Host}" : profile.EnvironmentType.ToString();

    /// <summary>A configured value that disagrees with Terraform, or where Terraform declares several candidates: a person decides.</summary>
    public static bool NeedsReview(SourceComparisonState state) => state is SourceComparisonState.Differs or SourceComparisonState.MultipleSourceCandidates;

    public static IReadOnlyList<ConfiguredSourceComparison> Reviews(IntegrationTerraformComparison? terraform) =>
        terraform is { TerraformAvailable: true } ? terraform.Values.Where(v => NeedsReview(v.Comparison.State)).ToList() : [];

    public static TerraformSupport Support(IntegrationTerraformComparison? terraform, IntegrationPlatform platform)
    {
        var seeded = platform.Origin == IntegrationRecordOrigin.Seed;
        if (terraform is not { TerraformAvailable: true }) return seeded ? TerraformSupport.SeededAssumption : TerraformSupport.NotCompared;
        var values = terraform.Values.Where(v => v.SubjectId == platform.Id).ToList();
        if (values.Any(v => NeedsReview(v.Comparison.State))) return TerraformSupport.NeedsReview;
        if (values.Any(v => v.Comparison.State == SourceComparisonState.Matches)) return TerraformSupport.Supported;
        return seeded ? TerraformSupport.SeededAssumption : TerraformSupport.NotCompared;
    }

    public static string Label(TerraformSupport support) => support switch
    {
        TerraformSupport.SeededAssumption => "Seeded assumption",
        TerraformSupport.Supported => "Supported by Terraform",
        TerraformSupport.NeedsReview => "Terraform differs · Needs review",
        _ => "Not compared with Terraform",
    };

    public static string Tone(TerraformSupport support) => support switch
    {
        TerraformSupport.NeedsReview => "attention",
        TerraformSupport.Supported => "info",
        _ => "muted",
    };

    /// <summary>Configured integrations whose consumer differs from the source's candidate (the configured mapping is kept until a person decides).</summary>
    public static int SourceConflicts(SourceIntegrationsReport? report) => report?.Matches.Count(m => m.State == SourceMatchState.Conflict) ?? 0;

    public static string Provenance(IntegrationTemplateProvenance provenance) => provenance switch
    {
        IntegrationTemplateProvenance.ConfirmedMapping => "Confirmed mapping",
        IntegrationTemplateProvenance.ConfiguredAssumption => "Assumption",
        _ => "Historical audit",
    };
}
