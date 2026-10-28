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

/// <summary>The candidate list's review-state filter. <see cref="Ready"/> is the default view; <see cref="All"/> lists every candidate.</summary>
public enum CandidateFilter { Ready, NeedsConfirmation, NeedsMapping, Conflicts, All }

/// <summary>One project-specific setup item, grouped by what kind of evidence or configuration it is.</summary>
public enum SetupGroup { Platforms, HistoricalRuntimeEvidence, Assumptions, ConfirmedMappings, Extensions }

/// <summary>Runtime evidence setup of an integration or environment: whether its runtime evidence SOURCES are configured — never observed health.
/// Unavailable: the integration's kind has no runtime evidence provider (e.g. HTTP/API), so there is nothing to configure.</summary>
public enum RuntimeEvidenceStatus { NotConfigured, Partial, Configured, Unavailable }

/// <summary>
/// Target Environment → Integration Catalog overview: configured integrations, suggestions from Source Analysis and runtime-evidence setup. It reads
/// the catalog and the source report; it never re-derives source evidence (Source Analysis owns that) and never confirms a mapping.
/// </summary>
public static class IntegrationsOverviewPresentation
{
    public static SuggestionStrength Strength(SourceIntegrationCandidate candidate) => candidate switch
    {
        // Contributing sources disagree (multi-source discovery): a decision, never resolved by picking one.
        { SourceDisagreements.Count: > 0 } => SuggestionStrength.Conflict,
        { ConsumerEvidence: ArchitectureEvidenceState.Conflict } => SuggestionStrength.Conflict,
        { ConsumerCandidate: null } or { ConsumerEvidence: ArchitectureEvidenceState.Unresolved } => SuggestionStrength.Unassigned,
        { ConsumerEvidence: ArchitectureEvidenceState.Confirmed or ArchitectureEvidenceState.StronglySupported } => SuggestionStrength.StronglySupported,
        _ => SuggestionStrength.Suggested,
    };

    /// <summary>
    /// The review state a person acts on. Same semantics as before, plainer words: StronglySupported (explicit wiring or several independent
    /// indicators) is ready to review first; Suggested (naming/convention only) needs confirmation; Unassigned (no consumer identified) needs
    /// mapping. None of them is a confirmed mapping.
    /// </summary>
    public static string Label(SuggestionStrength strength) => strength switch
    {
        SuggestionStrength.StronglySupported => "Ready to review",
        SuggestionStrength.Suggested => "Needs confirmation",
        SuggestionStrength.Conflict => "Conflicting evidence",
        _ => "Needs mapping",
    };

    /// <summary>The source-evidence wording stored with an added record's mapping provenance (unchanged by the review-state relabel).</summary>
    private static string EvidenceWording(SuggestionStrength strength) => strength switch
    {
        SuggestionStrength.StronglySupported => "strongly supported",
        SuggestionStrength.Suggested => "suggested",
        SuggestionStrength.Conflict => "conflict",
        _ => "unassigned",
    };

    public static SuggestionStrength? Strength(CandidateFilter filter) => filter switch
    {
        CandidateFilter.Ready => SuggestionStrength.StronglySupported,
        CandidateFilter.NeedsConfirmation => SuggestionStrength.Suggested,
        CandidateFilter.NeedsMapping => SuggestionStrength.Unassigned,
        CandidateFilter.Conflicts => SuggestionStrength.Conflict,
        _ => null,
    };

    /// <summary>
    /// The candidate review list: one review state (or all), optionally searched, ordered ready-first then needs confirmation, needs mapping and
    /// conflicts, alphabetical by relationship within a state — never the backend's incidental order.
    /// </summary>
    public static IReadOnlyList<SourceIntegrationCandidate> Candidates(SourceIntegrationsReport? report, CandidateFilter filter, string? search = null) =>
        Suggestions(report)
            .Where(c => Strength(filter) is not { } wanted || Strength(c) == wanted)
            .Where(c => string.IsNullOrWhiteSpace(search) || SearchRank(c, search) is not null)
            .OrderBy(c => string.IsNullOrWhiteSpace(search) ? 0 : SearchRank(c, search) ?? 9)
            .ThenBy(c => Strength(c) switch { SuggestionStrength.StronglySupported => 0, SuggestionStrength.Suggested => 1, SuggestionStrength.Unassigned => 2, _ => 3 })
            .ThenBy(Relationship, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// How well a candidate matches a search, best first: 0 exact word ("MU" ↔ "Meldingsutveksleren (MU)"), 1 word prefix, 2 substring of the whole
    /// text (3+ characters only). A short query never matches inside a word, so "MU" does not find "Kommune". Null: no match.
    /// </summary>
    public static int? SearchRank(SourceIntegrationCandidate c, string search)
    {
        var query = BirkNext.Integrations.IntegrationIdentityResolver.Fold(search);
        if (query.Length == 0) return 0;
        var text = SearchText(c);
        var words = BirkNext.Integrations.IntegrationIdentityResolver.Words(text);
        if (words.Contains(query)) return 0;
        if (query.Length >= 2 && words.Any(word => word.StartsWith(query, StringComparison.Ordinal))) return 1;
        return query.Length >= 3 && BirkNext.Integrations.IntegrationIdentityResolver.Fold(text).Contains(query, StringComparison.Ordinal) ? 2 : null;
    }

    private static string SearchText(SourceIntegrationCandidate c) =>
        string.Join(" ", new[] { Relationship(c), c.ChannelName, c.ProducerComponent, c.SourceSystem, SuggestionKind(c), c.NamespaceName }.OfType<string>());

    /// <summary>Who produces or calls, in words: the source entity, else the producing component, else the source system, else the channel.</summary>
    public static string Producer(SourceIntegrationCandidate c) =>
        c.SourceEntity ?? c.ProducerComponent ?? c.SourceSystem ?? SourceIntegrationsPresentation.ChannelShort(c.ChannelName);

    /// <summary>"Producer → consumer": the human-readable relationship a candidate row leads with.</summary>
    public static string Relationship(SourceIntegrationCandidate c) => $"{Producer(c)} → {c.ConsumerCandidate ?? "consumer not identified"}";

    /// <summary>Candidates sharing a producer, so several technically different relationships of one component read as one expandable group.</summary>
    public static IReadOnlyList<IGrouping<string, SourceIntegrationCandidate>> GroupByProducer(IReadOnlyList<SourceIntegrationCandidate> candidates) =>
        candidates.GroupBy(Producer, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The overview's runtime-evidence answer, in words that never sound like a failed execution or a missing configuration object.</summary>
    public static string OverviewRuntimeLabel(IntegrationCatalog catalog, RuntimeEvidenceStatus status) => catalog.Integrations.Count == 0
        ? "None — no configured integrations"
        : status switch
        {
            RuntimeEvidenceStatus.Configured => "Sources configured · read during review",
            RuntimeEvidenceStatus.Partial => "Partly set up",
            RuntimeEvidenceStatus.Unavailable => "Not applicable to these integrations",
            _ => "Setup required",
        };

    /// <summary>Which project-specific setup group an item belongs to.</summary>
    public static SetupGroup Group(IntegrationTemplateItem item) => item switch
    {
        { Provenance: IntegrationTemplateProvenance.ConfirmedMapping } => SetupGroup.ConfirmedMappings,
        _ when item.Kind.Contains("Extension", StringComparison.OrdinalIgnoreCase) => SetupGroup.Extensions,
        _ when item.Kind.Contains("Platform", StringComparison.OrdinalIgnoreCase) => SetupGroup.Platforms,
        { Provenance: IntegrationTemplateProvenance.HistoricalAudit } => SetupGroup.HistoricalRuntimeEvidence,
        _ => SetupGroup.Assumptions,
    };

    public static string GroupLabel(SetupGroup group) => group switch
    {
        SetupGroup.Platforms => "Platforms",
        SetupGroup.HistoricalRuntimeEvidence => "Historical runtime evidence",
        SetupGroup.Assumptions => "Assumptions",
        SetupGroup.ConfirmedMappings => "Confirmed integration mappings",
        _ => "Extensions",
    };

    /// <summary>Pre-apply counts in words ("3 platform settings"), one per non-empty group.</summary>
    public static string GroupCount(SetupGroup group, int count) => group switch
    {
        SetupGroup.Platforms => $"{count} platform setting{(count == 1 ? "" : "s")}",
        SetupGroup.HistoricalRuntimeEvidence => $"{count} historical runtime evidence record{(count == 1 ? "" : "s")}",
        SetupGroup.Assumptions => $"{count} assumption{(count == 1 ? "" : "s")}",
        SetupGroup.ConfirmedMappings => $"{count} confirmed integration mapping{(count == 1 ? "" : "s")}",
        _ => $"{count} extension{(count == 1 ? "" : "s")}",
    };

    /// <summary>What a setup item's state means, so an assumption never carries the confidence of a confirmed mapping or a runtime observation.</summary>
    public static string ProvenanceHelp(IntegrationTemplateItem item) => item.Provenance switch
    {
        IntegrationTemplateProvenance.ConfirmedMapping => "Reviewed mapping carried by this setup.",
        IntegrationTemplateProvenance.ConfiguredAssumption => Group(item) == SetupGroup.Extensions
            ? "Project-specific extension setup, not general integration discovery. Assumed — not verified at runtime."
            : "Assumed value — not verified at runtime.",
        _ => "Previously verified; not re-verified for the current review.",
    };

    /// <summary>The Terraform line of Source evidence. "0 of 0" is never a metric: no comparable value reads as exactly that.</summary>
    public static string TerraformSummary(int matching, int compared, int reviews) => compared == 0
        ? "Terraform evidence available. No comparable environment values were found."
        : $"{matching} of {compared} compared value{(compared == 1 ? "" : "s")} match source{(reviews > 0 ? $" · {reviews} need{(reviews == 1 ? "s" : "")} review" : "")}.";



    public static string Tone(SuggestionStrength strength) => strength switch
    {
        SuggestionStrength.StronglySupported => "info",
        SuggestionStrength.Conflict => "attention",
        _ => "muted",
    };

    /// <summary>Candidates from the latest source discovery that no configured integration covers (business channels only).</summary>
    public static IReadOnlyList<SourceIntegrationCandidate> Suggestions(SourceIntegrationsReport? report) => SourceIntegrationsPresentation.Unconfigured(report);

    /// <summary>Source candidates plus Test Pack proposals backed by recognized source evidence: ready ones are ready to review, the rest need a
    /// decision (counted as needing confirmation). Available when either exists.</summary>
    public static SuggestionSummary Summary(SourceIntegrationsReport? report, IReadOnlyList<IntegrationPackProposal> packs)
    {
        var source = Summary(report);
        if (packs.Count == 0) return source;
        var ready = packs.Count(p => p.Ready);
        return source with { Available = true, Total = source.Total + packs.Count, StronglySupported = source.StronglySupported + ready, Suggested = source.Suggested + packs.Count - ready };
    }

    /// <summary>"M2LB.Common.zip — Current project source; Tiltak 2.zip — MU source (Skolenærvær Testing)": why BirkNext discovered the candidate.</summary>
    public static IReadOnlyList<string> Provenance(SourceIntegrationCandidate candidate, string? fallbackArchive) =>
        candidate.Sources.Count > 0
            ? [.. candidate.Sources.Select(s => $"{s.ArchiveName} — {s.Reason}{(s.CorroboratingOnly ? " (corroborates)" : "")}")]
            : fallbackArchive is { Length: > 0 } archive ? [archive] : [];

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
                    MappingSource = $"Source Analysis suggestion ({EvidenceWording(Strength(candidate))})",
                }
                : new IntegrationConsumer { MappingState = ConsumerMappingState.NeedsConfirmation },
            Origin = IntegrationRecordOrigin.SourceSuggestion,
        };
    }

    /// <summary>What a suggestion is ("Messaging · Service Bus queue"), so a bare channel name never reads as a recommended business integration.</summary>
    public static string SuggestionKind(SourceIntegrationCandidate candidate) => string.Join(" · ", new[]
    {
        candidate.Pattern == IntegrationPattern.Unknown ? null : SourceIntegrationLabels.Pattern(candidate.Pattern),
        candidate.DestinationType ?? (candidate.Transport == IntegrationTransport.Unknown ? null : SourceIntegrationLabels.Transport(candidate.Transport)),
    }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>Where a suggestion was detected: the snapshot's archive and the source project its evidence sits in (most evidence first).
    /// Provenance only; it never says the suggestion is the same integration as a configured one.</summary>
    public static string DetectedIn(SourceIntegrationCandidate candidate, string? archive)
    {
        var project = candidate.Evidence.Select(e => e.File.Replace('\\', '/')).Where(file => file.Contains('/'))
            .GroupBy(file => file[..file.IndexOf('/')], StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key).FirstOrDefault();
        return string.Join(" · ", new[] { archive is { Length: > 0 } ? $"Detected in {archive}" : "Detected in Source Analysis", project }.OfType<string>());
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

    /// <summary>Kinds whose runtime evidence comes from a configured platform source (namespace metadata, checkpoints, telemetry, SCIM endpoint).</summary>
    public static bool HasRuntimeProvider(IntegrationKind kind) => kind is IntegrationKind.EventHub or IntegrationKind.ServiceBus or IntegrationKind.IdentityProvisioning;

    /// <summary>One integration's runtime evidence setup: Unavailable when its kind has no runtime provider, else its platform's sources.</summary>
    public static RuntimeEvidenceStatus Runtime(IntegrationDefinition definition, IntegrationPlatform? platform) =>
        HasRuntimeProvider(definition.Kind) ? Runtime(platform) : RuntimeEvidenceStatus.Unavailable;

    /// <summary>The environment's runtime-evidence setup: Not configured while nothing is configured or no platform has any source; Unavailable
    /// when no configured integration has a kind with a runtime evidence provider.</summary>
    public static RuntimeEvidenceStatus Runtime(IntegrationCatalog catalog)
    {
        if (catalog.Integrations.Count > 0 && !catalog.Integrations.Any(i => HasRuntimeProvider(i.Kind)) && !catalog.Platforms.Any(p => p.Kind == IntegrationKind.IdentityProvisioning))
            return RuntimeEvidenceStatus.Unavailable;
        var used = catalog.Platforms.Where(p => catalog.Integrations.Any(i => i.PlatformId == p.Id) || p.Kind == IntegrationKind.IdentityProvisioning).Select(Runtime).ToList();
        if (catalog.Integrations.Count == 0 || used.Count == 0 || used.All(s => s == RuntimeEvidenceStatus.NotConfigured)) return RuntimeEvidenceStatus.NotConfigured;
        return used.All(s => s == RuntimeEvidenceStatus.Configured) ? RuntimeEvidenceStatus.Configured : RuntimeEvidenceStatus.Partial;
    }

    public static string Label(RuntimeEvidenceStatus status) => status switch
    {
        RuntimeEvidenceStatus.Configured => "Configured",
        RuntimeEvidenceStatus.Partial => "Partial",
        RuntimeEvidenceStatus.Unavailable => "Unavailable",
        _ => "Not configured",
    };

    /// <summary>What a runtime evidence status means (evidence setup, never health).</summary>
    public static string Meaning(RuntimeEvidenceStatus status, IntegrationKind? kind = null) => status switch
    {
        RuntimeEvidenceStatus.Configured => "Runtime evidence sources are configured. Configured is not observed: a review run reads them.",
        RuntimeEvidenceStatus.Partial => "Some runtime evidence sources are configured; the checks that need the others are not assessed.",
        RuntimeEvidenceStatus.Unavailable => kind is { } k
            ? $"No runtime evidence provider exists for {IntegrationConfigurationRules.KindLabel(k)} integrations; nothing is observed at runtime, and there is nothing to configure."
            : "No configured integration has a runtime evidence provider; nothing is observed at runtime.",
        _ => "The platform's runtime evidence sources are not configured, so runtime evidence cannot be read. This is not a health result.",
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
        IntegrationRecordOrigin.Template => "Template",
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

    public static string ProvenanceTone(IntegrationTemplateProvenance provenance) => provenance switch
    {
        IntegrationTemplateProvenance.ConfirmedMapping => "info",
        _ => "muted",
    };
}
