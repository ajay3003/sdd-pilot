using BirkNext.Integrations;
using BirkNext.SourceArchitecture;

namespace BirkNext.Web.Services;

/// <summary>Disjoint consumer-mapping categories of a configured integration. Confirmed only through an explicit person's action.</summary>
public enum MappingCategory { Confirmed, Suggested, NeedsConfirmation, Unassigned }

/// <summary>Model-derived counts of the Source integrations section. Each dimension is counted on its own — never added together.</summary>
public sealed record SourceIntegrationsSummary(int BusinessIntegrations, IReadOnlyDictionary<MappingCategory, int> Mapping, IReadOnlyList<(string Label, int Count)> Review,
    int Conflicts, int SourceSuggestions, int Unconfigured, int TechnicalChannels);

/// <summary>The filter values of the section; empty string = All.</summary>
public sealed record SourceIntegrationFilter(string Search = "", string Configuration = "", string Review = "", string Mapping = "", string Pattern = "", string Transport = "", string SourceSystem = "");

/// <summary>
/// Pure presentation of Source integrations: configured integrations enriched by the latest source discovery. Generic — no source system,
/// capture technology or table name is special-cased. Detected values are labelled as detected; nothing here changes a mapping.
/// </summary>
public static class SourceIntegrationsPresentation
{
    public const string Title = "Source integrations";
    public const string Subtitle = "Source-to-consumer integrations detected or configured from source analysis.";

    public static MappingCategory Mapping(IntegrationDefinition definition) => definition.Consumer.MappingState switch
    {
        ConsumerMappingState.Confirmed => MappingCategory.Confirmed,
        ConsumerMappingState.Suggested => MappingCategory.Suggested,
        _ => string.IsNullOrWhiteSpace(definition.Consumer.DisplayName) ? MappingCategory.Unassigned : MappingCategory.NeedsConfirmation,
    };

    public static string Label(MappingCategory category) => category switch
    {
        MappingCategory.NeedsConfirmation => "Needs confirmation",
        _ => category.ToString(),
    };

    public static string Tone(MappingCategory category) => category switch
    {
        MappingCategory.Confirmed => "ready",
        MappingCategory.Suggested => "info",
        MappingCategory.NeedsConfirmation => "attention",
        _ => "muted",
    };

    /// <summary>One-line meaning of a mapping category (details/tooltips — never repeated in every row).</summary>
    public static string Meaning(MappingCategory category, string? consumer) => category switch
    {
        MappingCategory.Confirmed => "This consumer mapping was explicitly confirmed.",
        MappingCategory.Suggested => $"A suggested consumer mapping{(consumer is null ? "" : $" ({consumer})")}; it has not been confirmed.",
        MappingCategory.NeedsConfirmation => $"{consumer ?? "A consumer"} is recorded, but the mapping has not been confirmed.",
        _ => "No consumer mapping has been established.",
    };

    public static SourceIntegrationMatch? Match(SourceIntegrationsReport? report, IntegrationDefinition definition) =>
        report?.Matches.FirstOrDefault(m => m.IntegrationId == definition.Id);

    public static SourceIntegrationCandidate? Candidate(SourceIntegrationsReport? report, SourceIntegrationMatch? match) =>
        match?.CandidateId is { } id ? report?.Discovery?.Candidates.FirstOrDefault(c => c.Id == id) : null;

    /// <summary>"Person" and "BIRK · BirkM2LB.dbo.Person" from the configured resource; never assumes a table or schema shape.</summary>
    public static (string Entity, string? Qualifier) Source(IntegrationDefinition definition, SourceIntegrationCandidate? candidate)
    {
        var resource = definition.SourceResource ?? (candidate?.SourceSchema is { } schema && candidate.SourceEntity is { } table ? $"{candidate.SourceDatabase}.{schema}.{table}".TrimStart('.') : null);
        var entity = resource?.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? candidate?.SourceEntity ?? definition.DisplayName;
        var system = definition.SourceSystem ?? candidate?.SourceSystem;
        var qualifier = string.Join(" · ", new[] { system, resource }.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase));
        return (entity, qualifier.Length == 0 ? null : qualifier);
    }

    public static IntegrationTransport Transport(IntegrationDefinition definition, SourceIntegrationCandidate? candidate) =>
        candidate?.Transport ?? SourceIntegrationLabels.TransportOf(definition.Kind);

    /// <summary>Long channel names shown by their last two segments ("…dbo.Person"); the full name is in the title and the details.</summary>
    public static string ChannelShort(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return "Not configured";
        var parts = channel.Split('.');
        return parts.Length > 3 ? "…" + string.Join('.', parts[^2..]) : channel;
    }

    /// <summary>Pattern and capture/producer: detected from source when a candidate matches, otherwise the configured producer text.</summary>
    public static (string Pattern, string Capture, bool Detected) ProducerCapture(IntegrationDefinition definition, SourceIntegrationCandidate? candidate)
    {
        if (candidate is not null)
            return (SourceIntegrationLabels.PatternShort(candidate.Pattern),
                candidate.CaptureTechnology ?? candidate.ProducerComponent ?? candidate.CallerTechnology ?? "Producer not in source", true);
        return ("Not detected", string.IsNullOrWhiteSpace(definition.Producer) ? "Not configured" : definition.Producer!, false);
    }

    /// <summary>The short source note next to the consumer: suggestion, conflict, outdated or changed evidence — or nothing.</summary>
    public static (string Label, string Tone, string Detail)? SourceNote(SourceIntegrationMatch? match)
    {
        if (match is null) return null;
        if (match.State == SourceMatchState.Conflict) return ("Mapping conflict · Needs review", "attention", match.Detail);
        if (match.State == SourceMatchState.SourceSuggestion) return ($"Source suggests {match.SourceConsumer}", "info", match.Detail);
        if (match.StaleEvidence) return ("Source evidence outdated", "attention", "The mapping was confirmed against an older source snapshot. Review recommended; the confirmation is kept.");
        if (match.SourceChanges.Count > 0) return ("Source evidence changed", "attention", string.Join("; ", match.SourceChanges));
        return null;
    }

    public static bool Matches(IntegrationRow row, SourceIntegrationFilter filter, IntegrationsPaneRowSource source)
    {
        if (filter.Configuration.Length > 0 && row.Configuration.ToString() != filter.Configuration) return false;
        if (filter.Review.Length > 0 && row.ReviewReadiness != filter.Review) return false;
        if (filter.Mapping.Length > 0 && source.Mapping.ToString() != filter.Mapping) return false;
        if (filter.Pattern.Length > 0 && SourceIntegrationLabels.PatternGroup(source.Candidate?.Pattern) != filter.Pattern) return false;
        if (filter.Transport.Length > 0 && source.Transport.ToString() != filter.Transport) return false;
        if (filter.SourceSystem.Length > 0 && !string.Equals(SourceSystem(row.Definition, source.Candidate), filter.SourceSystem, StringComparison.OrdinalIgnoreCase)) return false;
        return filter.Search.Length == 0
            || row.Definition.DisplayName.Contains(filter.Search, StringComparison.OrdinalIgnoreCase)
            || (row.Topic ?? "").Contains(filter.Search, StringComparison.OrdinalIgnoreCase)
            || row.Consumer.Contains(filter.Search, StringComparison.OrdinalIgnoreCase)
            || source.Entity.Contains(filter.Search, StringComparison.OrdinalIgnoreCase);
    }

    public static string SourceSystem(IntegrationDefinition definition, SourceIntegrationCandidate? candidate) =>
        definition.SourceSystem ?? candidate?.SourceSystem ?? "Not specified";

    /// <summary>Business integrations of the catalog (identity provisioning is its own flow, never counted here).</summary>
    public static IReadOnlyList<IntegrationDefinition> Business(IntegrationCatalog catalog) =>
        catalog.Integrations.Where(i => i.Kind != IntegrationKind.IdentityProvisioning).ToList();

    public static SourceIntegrationsSummary Summary(IntegrationCatalog catalog, SourceIntegrationsReport? report)
    {
        var business = Business(catalog);
        var mapping = Enum.GetValues<MappingCategory>().ToDictionary(c => c, c => business.Count(i => Mapping(i) == c));
        var review = business.Select(i => IntegrationsPanePresentation.Row(catalog, i).ReviewReadiness).GroupBy(r => r)
            .OrderBy(g => Array.IndexOf(IntegrationsPanePresentation.ReadinessLabels, g.Key)).Select(g => (g.Key, g.Count())).ToList();
        var matches = report?.Matches ?? [];
        return new(business.Count, mapping, review, matches.Count(m => m.State == SourceMatchState.Conflict), matches.Count(m => m.State == SourceMatchState.SourceSuggestion),
            report?.UnconfiguredCandidateIds.Count ?? 0, TechnicalChannels(catalog, report).Count);
    }

    /// <summary>Pattern filter values present in the data (CDC / Messaging / API / Other) — no empty options.</summary>
    public static IReadOnlyList<string> PatternGroups(IEnumerable<IntegrationsPaneRowSource> rows) =>
        rows.Select(r => SourceIntegrationLabels.PatternGroup(r.Candidate?.Pattern)).Distinct().OrderBy(g => g switch { "CDC" => 0, "Messaging" => 1, "API" => 2, _ => 3 }).ToList();

    public static IReadOnlyList<IntegrationTransport> Transports(IEnumerable<IntegrationsPaneRowSource> rows) => rows.Select(r => r.Transport).Distinct().Order().ToList();

    public static IReadOnlyList<string> SourceSystems(IEnumerable<IntegrationsPaneRowSource> rows) =>
        rows.Select(r => r.SourceSystem).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Platform/support channels: configured technical topics plus the ones the source discovery excluded. Never business counts.</summary>
    public static IReadOnlyList<TechnicalIntegrationChannel> TechnicalChannels(IntegrationCatalog catalog, SourceIntegrationsReport? report)
    {
        var configured = catalog.Platforms.SelectMany(p => p.TechnicalTopics.Select(t => new TechnicalIntegrationChannel(t.Name,
            p.Kind == IntegrationKind.ServiceBus ? IntegrationTransport.ServiceBus : SourceIntegrationLabels.TransportOf(p.Kind), t.Purpose, "Configured", "Platform support channel — excluded from business integration counts.")));
        var discovered = report?.Discovery?.TechnicalChannels ?? [];
        return configured.Concat(discovered).DistinctBy(t => (t.Transport, t.Name.ToLowerInvariant())).ToList();
    }

    /// <summary>Business candidates in source that no configured integration covers (shown apart; never added automatically).</summary>
    public static IReadOnlyList<SourceIntegrationCandidate> Unconfigured(SourceIntegrationsReport? report) =>
        report?.Discovery is { } discovery ? discovery.Candidates.Where(c => report.UnconfiguredCandidateIds.Contains(c.Id)).ToList() : [];

    /// <summary>The source analysis line: which snapshot the section reflects, or Not analyzed.</summary>
    public static (string Label, string Tone, string Detail) SourceAnalysis(SourceIntegrationsReport? report) => report?.Discovery switch
    {
        null => ("Not analyzed", "muted", "No source snapshot has been analyzed for this environment. Analyze source in Source Analysis to discover integrations."),
        var d => (d.Status == SourceDiscoveryStatus.Complete ? "Analyzed" : "Partial", d.Status == SourceDiscoveryStatus.Complete ? "info" : "attention",
            $"{d.ArchiveName ?? "Source snapshot"} · analyzed {d.AnalyzedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC{(d.SourceFingerprint.Length >= 12 ? $" · sha256 {d.SourceFingerprint[..12]}" : "")}"),
    };

    /// <summary>The compact result shown after "Discover from source".</summary>
    public static IReadOnlyList<string> DiscoveryReport(IntegrationCatalog catalog, SourceIntegrationsReport report)
    {
        if (report.Discovery is not { } discovery) return ["No source snapshot has been analyzed for this environment; nothing was discovered.", report.Boundary];
        var business = discovery.Candidates.Where(c => c.IsBusinessIntegration).ToList();
        var summary = Summary(catalog, report);
        return
        [
            $"{business.Count} business integration candidate(s) detected in source ({string.Join(", ", business.GroupBy(c => SourceIntegrationLabels.Pattern(c.Pattern)).Select(g => $"{g.Count()} {g.Key}"))}).",
            $"Consumer mappings (configured): {summary.Mapping[MappingCategory.Confirmed]} confirmed · {summary.Mapping[MappingCategory.Suggested]} suggested · {summary.Mapping[MappingCategory.NeedsConfirmation]} need confirmation · {summary.Mapping[MappingCategory.Unassigned]} unassigned.",
            $"Source suggestions: {summary.SourceSuggestions} · Conflicts: {summary.Conflicts} · Detected but not configured: {summary.Unconfigured} · Technical channels excluded: {summary.TechnicalChannels}.",
            report.Boundary,
        ];
    }

    public static string Evidence(ArchitectureEvidenceState state) => SourceIntegrationLabels.Evidence(state);

    public static string EvidenceTone(ArchitectureEvidenceState state) => state switch
    {
        ArchitectureEvidenceState.Confirmed or ArchitectureEvidenceState.StronglySupported => "info",
        ArchitectureEvidenceState.Conflict => "attention",
        _ => "muted",
    };
}

/// <summary>The source-derived side of one table row (kept apart from the configuration-derived <see cref="IntegrationRow"/>).</summary>
public sealed record IntegrationsPaneRowSource(MappingCategory Mapping, SourceIntegrationMatch? Match, SourceIntegrationCandidate? Candidate, string Entity, string? Qualifier,
    IntegrationTransport Transport, string Pattern, string Capture, bool CaptureDetected, string SourceSystem)
{
    public static IntegrationsPaneRowSource For(IntegrationDefinition definition, SourceIntegrationsReport? report)
    {
        var match = SourceIntegrationsPresentation.Match(report, definition);
        var candidate = SourceIntegrationsPresentation.Candidate(report, match);
        var (entity, qualifier) = SourceIntegrationsPresentation.Source(definition, candidate);
        var (pattern, capture, detected) = SourceIntegrationsPresentation.ProducerCapture(definition, candidate);
        return new(SourceIntegrationsPresentation.Mapping(definition), match, candidate, entity, qualifier, SourceIntegrationsPresentation.Transport(definition, candidate),
            pattern, capture, detected, SourceIntegrationsPresentation.SourceSystem(definition, candidate));
    }
}
