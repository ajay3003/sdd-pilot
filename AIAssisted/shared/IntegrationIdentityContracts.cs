using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// Canonical integration identity: one configured integration answers to several typed identities (logical journey role, technical producer and
// consumer, source resource, transport channel, contract identity, aliases). Every feature that asks "is this the integration I mean?" —
// journeys, IQR, Active Event providers, source evidence, setup profiles — resolves through these types instead of comparing display strings.
// Aliases keep their provenance: an expected (Test Pack) or imported alias is never a source-proven fact.

/// <summary>Where a logical ↔ technical alias comes from. Only <see cref="Confirmed"/> is a human decision; only <see cref="SourceDiscovered"/> is
/// evidence. <see cref="TestPackExpected"/> and <see cref="ImportedSetup"/> are expectations and never become source-proven by themselves.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationAliasProvenance { TestPackExpected, SourceDiscovered, ImportedSetup, ManuallyConfigured, Confirmed }

/// <summary>A logical role ("Skolenærværsrapport") and the technical identity that implements it ("M2LB.SkoleService.Api"), with provenance.</summary>
public sealed record IntegrationAlias(string Logical, string Technical, IntegrationAliasProvenance Provenance, string? Detail = null);

/// <summary>
/// A source database resource (CDC table) in typed form. "BirkM2LB.dbo.Utdanning", "dbo.Utdanning", "[dbo].[Utdanning]" and the CDC Event Hub name
/// "m2lb-cdc-dev.BirkM2LB.dbo.Utdanning" are representations of the same resource: schema and table must be equal, and the database too when both
/// sides state one. Nothing is guessed from a partial or similar name.
/// </summary>
public sealed record SourceResourceIdentity(string? Database, string Schema, string Table)
{
    public string Qualified => $"{Schema}.{Table}";
    public string Full => Database is null ? Qualified : $"{Database}.{Qualified}";

    /// <summary>"schema.table" or "database.schema.table" (brackets and quotes removed); null for anything else.</summary>
    public static SourceResourceIdentity? Parse(string? value)
    {
        var parts = Parts(value);
        return parts.Length switch
        {
            2 => new(null, parts[0], parts[1]),
            3 => new(parts[0], parts[1], parts[2]),
            _ => null,
        };
    }

    /// <summary>A Debezium/CDC topic or Event Hub name "{prefix}.{database}.{schema}.{table}": the last three parts. Null with fewer than four parts,
    /// so an ordinary hub name never reads as a table.</summary>
    public static SourceResourceIdentity? FromCdcTopic(string? value)
    {
        var parts = Parts(value);
        return parts.Length >= 4 ? new(parts[^3], parts[^2], parts[^1]) : null;
    }

    /// <summary>The integration's source resource: its configured source resource, else (CDC on Event Hub) the resource its hub name states.</summary>
    public static SourceResourceIdentity? Of(IntegrationDefinition integration) =>
        Parse(integration.SourceResource) ?? (integration.Kind == IntegrationKind.EventHub ? FromCdcTopic(integration.EndpointOrTopic) : null);

    public bool SameResource(SourceResourceIdentity other) =>
        string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase) && string.Equals(Table, other.Table, StringComparison.OrdinalIgnoreCase)
        && (Database is null || other.Database is null || string.Equals(Database, other.Database, StringComparison.OrdinalIgnoreCase));

    private static string[] Parts(string? value) => string.IsNullOrWhiteSpace(value) ? []
        : value.Trim().Split('.').Select(part => part.Trim().Trim('[', ']', '"', '`')).ToArray() is var parts && parts.All(part => part.Length > 0) ? parts : [];
}

/// <summary>
/// What a requirement (a journey's integration, a setup profile entry, a provider's resource) says about the integration it needs. Each list holds
/// alternative names of one dimension (logical name, technical implementation, known aliases). The contract identity (event/message type) is kept
/// apart from the transport channel: a topic is never expected to carry the contract's name unless a channel says so.
/// </summary>
public sealed record IntegrationRequirementIdentity
{
    public IntegrationKind Kind { get; init; }
    public string LogicalName { get; init; } = "";
    public IReadOnlyList<string> ProducerNames { get; init; } = [];
    public IReadOnlyList<string> ConsumerNames { get; init; } = [];
    /// <summary>Expected transport channels (queue/topic/hub/endpoint), exact names.</summary>
    public IReadOnlyList<string> Channels { get; init; } = [];
    /// <summary>Contract/event identities (e.g. "SkolenaervaersperiodeLukket"): matched against the integration's contract references and names.</summary>
    public IReadOnlyList<string> ContractIdentities { get; init; } = [];
    public IReadOnlyList<SourceResourceIdentity> SourceResources { get; init; } = [];
    public IReadOnlyList<IntegrationAlias> Aliases { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationMatchState { NotMatched, MappingIncomplete, Matched }

/// <summary>How an integration matches a requirement: the dimensions that matched (with the name that matched) and the declared dimensions that did
/// not. <see cref="IntegrationMatchState.MappingIncomplete"/> means the integration IS the one required (an anchor matched) but a declared
/// dimension is not configured or does not match — "Integration found · mapping incomplete", never "Missing integration".</summary>
public sealed record IntegrationIdentityMatch(IntegrationMatchState State, IReadOnlyList<string> Matched, IReadOnlyList<string> Unresolved)
{
    public static readonly IntegrationIdentityMatch None = new(IntegrationMatchState.NotMatched, [], []);
    public bool Found => State != IntegrationMatchState.NotMatched;
}

/// <summary>The one resolver every feature uses to correlate a requirement with a configured integration.</summary>
public static class IntegrationIdentityResolver
{
    /// <summary>
    /// Anchors establish identity: the source resource (typed), an exact channel, a contract identity, or producer AND consumer together. Without
    /// an anchor there is no match. Every declared dimension that does not match is listed as unresolved.
    /// </summary>
    public static IntegrationIdentityMatch Match(IntegrationRequirementIdentity requirement, IntegrationDefinition integration)
    {
        if (integration.Kind != requirement.Kind) return IntegrationIdentityMatch.None;
        var matched = new List<string>();
        var unresolved = new List<string>();
        var anchored = false;

        if (requirement.SourceResources.Count > 0)
        {
            var resource = SourceResourceIdentity.Of(integration);
            if (resource is not null && requirement.SourceResources.Any(resource.SameResource)) { matched.Add($"Source resource {resource.Full}"); anchored = true; }
            else unresolved.Add(resource is null ? "Source resource not configured" : $"Source resource {resource.Full} is not {string.Join(" / ", requirement.SourceResources.Select(r => r.Full))}");
        }
        if (requirement.Channels.Count > 0)
        {
            var channel = requirement.Channels.FirstOrDefault(name => SameName(name, integration.EndpointOrTopic));
            if (channel is not null) { matched.Add($"Channel {channel}"); anchored = true; }
            else unresolved.Add(string.IsNullOrWhiteSpace(integration.EndpointOrTopic) ? "Channel not configured" : $"Channel {integration.EndpointOrTopic} is not {string.Join(" / ", requirement.Channels)}");
        }
        if (requirement.ContractIdentities.Count > 0
            && requirement.ContractIdentities.FirstOrDefault(name => AnyName(name, integration.ProducerContractReference, integration.ConsumerContractReference, integration.EndpointOrTopic, integration.DisplayName)) is { } contract)
        {
            matched.Add($"Contract {contract}");
            anchored = true;
        }
        var producer = Names(requirement.ProducerNames, requirement.Aliases).FirstOrDefault(name => AnyName(name, integration.Producer, integration.SourceSystem));
        var consumer = Names(requirement.ConsumerNames, requirement.Aliases).FirstOrDefault(name =>
            AnyName(name, integration.Consumer.DisplayName, integration.Consumer.LogicalName, integration.DestinationSystem));
        if (requirement.ProducerNames.Count > 0)
        {
            if (producer is not null) matched.Add($"Producer {producer}");
            else unresolved.Add(string.IsNullOrWhiteSpace(integration.Producer ?? integration.SourceSystem) ? "Producer not configured" : $"Producer {integration.Producer ?? integration.SourceSystem} is not {requirement.ProducerNames[0]}");
        }
        if (requirement.ConsumerNames.Count > 0)
        {
            if (consumer is not null) matched.Add($"Consumer {consumer}");
            else unresolved.Add(string.IsNullOrWhiteSpace(integration.Consumer.DisplayName ?? integration.DestinationSystem) ? $"Consumer {requirement.ConsumerNames[0]} not configured"
                : $"Consumer {integration.Consumer.DisplayName ?? integration.DestinationSystem} is not {requirement.ConsumerNames[0]}");
        }
        anchored |= producer is not null && consumer is not null;
        // A channel or resource anchor plus the producer is enough to be the integration; the consumer may still be unmapped.
        anchored |= producer is not null && requirement.Channels.Count == 0 && requirement.SourceResources.Count == 0 && requirement.ConsumerNames.Count == 0;
        if (!anchored) return IntegrationIdentityMatch.None;
        return new(unresolved.Count == 0 ? IntegrationMatchState.Matched : IntegrationMatchState.MappingIncomplete, matched, unresolved);
    }

    /// <summary>The names a dimension answers to: its own names plus the technical side of every alias whose logical side is one of them.</summary>
    public static IEnumerable<string> Names(IReadOnlyList<string> names, IReadOnlyList<IntegrationAlias> aliases) =>
        names.Concat(aliases.Where(alias => names.Any(name => SameName(name, alias.Logical))).Select(alias => alias.Technical)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The same name: equal after folding case, diacritics (æ/ø/å) and separators — "M2LB.SkoleService.Api" ≡ "m2lb skoleservice api".</summary>
    public static bool SameName(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && Fold(left) is { Length: > 0 } a && a == Fold(right);

    /// <summary>A name matches a value when the whole value is the name, or (one-word names such as "Bufdata", "MU") when the name is one of the
    /// value's words — "Meldingsutveksleren (MU)" contains MU; "Kommune" does not contain MU.</summary>
    public static bool AnyName(string name, params string?[] values) => values.Any(value =>
        SameName(name, value) || (Words(name).Count == 1 && value is not null && Words(value).Contains(Fold(name))));

    /// <summary>The folded words of a name: dot/dash/space segments and their camel-case parts ("M2LB.SkoleAdapter" → m2lb, skoleadapter, skole, adapter).</summary>
    public static HashSet<string> Words(string value)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in value.Split(['.', '-', '_', '/', '\\', ' ', ':', '(', ')', ',', '@', '→', '–'], StringSplitOptions.RemoveEmptyEntries))
        {
            words.Add(Fold(segment));
            var current = new StringBuilder();
            for (var i = 0; i < segment.Length; i++)
            {
                var c = segment[i];
                if (i > 0 && char.IsUpper(c) && (char.IsLower(segment[i - 1]) || (i + 1 < segment.Length && char.IsLower(segment[i + 1]))) && current.Length > 0)
                {
                    words.Add(Fold(current.ToString()));
                    current.Clear();
                }
                current.Append(c);
            }
            if (current.Length > 0) words.Add(Fold(current.ToString()));
        }
        words.Remove("");
        return words;
    }

    public static string Fold(string value)
    {
        var folded = value.Trim().ToLowerInvariant().Replace("æ", "ae", StringComparison.Ordinal).Replace("ø", "o", StringComparison.Ordinal).Replace("å", "a", StringComparison.Ordinal);
        return new string(folded.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).ToArray());
    }
}

/// <summary>
/// What a bound source snapshot says about ONE integration: snapshot bound, integration-specific evidence resolved (and through what), and how much
/// of the repository is context only. Bound ≠ resolved: a bound snapshot whose content does not correlate with the integration supports nothing.
/// </summary>
public sealed record IntegrationSourceEvidenceScope(string IntegrationId)
{
    public Guid? SnapshotId { get; init; }
    public string? ArchiveName { get; init; }
    public bool SnapshotBound { get; init; }
    public bool EvidenceResolved { get; init; }
    public IReadOnlyList<string> CorrelatedOn { get; init; } = [];
    public IReadOnlyList<string> CorrelatedProjects { get; init; } = [];
    public int IntegrationRules { get; init; }
    public int IntegrationTests { get; init; }
    public int IntegrationPathHops { get; init; }
    /// <summary>Repository-wide counts: context only, never the integration's evidence.</summary>
    public int RepositoryRules { get; init; }
    public int RepositoryTests { get; init; }
    public int RepositoryPathHops { get; init; }
    public string Summary { get; init; } = "";
}
