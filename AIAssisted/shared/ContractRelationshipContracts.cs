using System.Text.Json.Serialization;
using BirkNext.SourceDomains;

namespace BirkNext.ContractSchemaEvidence;

// Contract relationship review: a producer artifact compared with a consumer artifact, both identified exactly (immutable snapshot id + exact
// source path, or a stored contract artifact). A matching name or file name only correlates identity; it never confirms compatibility, and a
// relationship is confirmed only by a person (ContractRelationshipState.RelationshipVerified), never by this comparison.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractRelationshipResultState { NotAssessable, ExactStructuralMatch, Compatible, CompatibleWithDifferences, Incompatible }

/// <summary>An exactly identified contract artifact: the immutable snapshot it lives in and its exact path there.</summary>
public sealed record ContractArtifactRef(Guid SnapshotId, string ArchiveName, string Path, string Name, string? Version)
{
    /// <summary>"{snapshotId}:{path}" — the exact identity deep links carry.</summary>
    public string Key => $"{SnapshotId}:{ContractArtifactPaths.Normalize(Path)}";
}

public sealed record ContractRelationshipDifference(string Path, string Kind, string Detail, bool Breaking);

public sealed record ContractRelationshipReview
{
    public string IntegrationId { get; init; } = "";
    public ContractRelationshipResultState State { get; init; }
    public ContractArtifactRef? Producer { get; init; }
    public ContractArtifactRef? Consumer { get; init; }
    /// <summary>What the configured references name (a logical contract id, or a path) — shown beside what was resolved.</summary>
    public string? ProducerReference { get; init; }
    public string? ConsumerReference { get; init; }
    public IReadOnlyList<ContractRelationshipDifference> Differences { get; init; } = [];
    /// <summary>"Matching source schema found · relationship not yet confirmed", "Producer artifact not available offline" …</summary>
    public string Summary { get; init; } = "";
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

public static class ContractArtifactPaths
{
    /// <summary>Forward slashes, no leading "./" or "/" — the only normalization applied to an exact path (case is compared insensitively).</summary>
    public static string Normalize(string? path) => (path ?? "").Trim().Replace('\\', '/').TrimStart('.', '/');

    public static bool SamePath(string? left, string? right) =>
        Normalize(left).Length > 0 && string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>A reference that names a file ("…/x.xsd", "x.xsd") rather than a logical contract id ("bufdir_…_altinn").</summary>
    public static bool IsPath(string? reference) => reference is not null && (reference.Contains('/') || reference.Contains('\\')
        || reference.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase) || reference.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

    /// <summary>A contract answers to a logical id when its declared name or its file stem is exactly that id (never a prefix or similarity).</summary>
    public static bool NamedExactly(SourceContract contract, string id) =>
        string.Equals(contract.Name, id.Trim(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(System.IO.Path.GetFileNameWithoutExtension(Normalize(contract.File)), id.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A path reference matches a contract file exactly, or by its file name when the reference is a bare file name.</summary>
    public static bool PathMatches(SourceContract contract, string reference) =>
        SamePath(contract.File, reference)
        || (!Normalize(reference).Contains('/') && string.Equals(System.IO.Path.GetFileName(Normalize(contract.File)), Normalize(reference), StringComparison.OrdinalIgnoreCase));
}

/// <summary>The one structural comparison of a producer and a consumer contract (field facts from Contract &amp; Schema Evidence).</summary>
public static class ContractRelationshipComparer
{
    public static (ContractRelationshipResultState State, IReadOnlyList<ContractRelationshipDifference> Differences) Compare(ContractFact producer, ContractFact consumer)
    {
        var produced = ContractEvidenceQueries.GetFields(producer).GroupBy(f => Key(f.Path)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var consumed = ContractEvidenceQueries.GetFields(consumer).GroupBy(f => Key(f.Path)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (produced.Count == 0 || consumed.Count == 0 || producer.Parse == ContractParseStatus.Failed || consumer.Parse == ContractParseStatus.Failed)
            return (ContractRelationshipResultState.NotAssessable, []);
        var differences = new List<ContractRelationshipDifference>();
        foreach (var (path, need) in consumed)
        {
            if (!produced.TryGetValue(path, out var has))
            {
                differences.Add(new(need.Path, need.Required ? "Required field not produced" : "Optional field not produced",
                    need.Required ? "The consumer requires it; the producer does not declare it." : "The consumer accepts it; the producer does not declare it.", need.Required));
                continue;
            }
            if (!string.Equals(Local(has.Type), Local(need.Type), StringComparison.OrdinalIgnoreCase))
                differences.Add(new(need.Path, "Type differs", $"Producer {has.Type ?? "untyped"} · consumer {need.Type ?? "untyped"}.", true));
            if (need.Required && !has.Required)
                differences.Add(new(need.Path, "Optional in producer, required by consumer", "A producer may omit it; the consumer rejects that.", true));
            if (has.Repeating && !need.Repeating)
                differences.Add(new(need.Path, "Repeating in producer only", "The producer may send several; the consumer accepts one.", true));
            if (need.AllowedValues.Count > 0)
            {
                var extra = (has.AllowedValues.Count == 0 ? ["any value"] : has.AllowedValues.Except(need.AllowedValues, StringComparer.Ordinal)).ToList();
                if (extra.Count > 0) differences.Add(new(need.Path, "Allowed values differ", $"Producer may send {string.Join(", ", extra.Take(6))}, which the consumer does not allow.", true));
            }
            if (!string.Equals(has.Pattern, need.Pattern, StringComparison.Ordinal) && need.Pattern is not null)
                differences.Add(new(need.Path, "Pattern differs", "The consumer declares a pattern the producer does not declare identically.", false));
            if (!string.Equals(has.MaxLength, need.MaxLength, StringComparison.Ordinal) && need.MaxLength is not null)
                differences.Add(new(need.Path, "Length differs", $"Producer max length {has.MaxLength ?? "none"} · consumer {need.MaxLength}.", false));
        }
        foreach (var (path, extra) in produced.Where(p => !consumed.ContainsKey(p.Key)))
            differences.Add(new(extra.Path, "Field not consumed", "The producer declares it; the consumer does not.", extra.Required));
        var state = differences.Count == 0 ? ContractRelationshipResultState.ExactStructuralMatch
            : differences.Any(d => d.Breaking) ? ContractRelationshipResultState.Incompatible
            : ContractRelationshipResultState.CompatibleWithDifferences;
        return (state, differences);
    }

    public static string Label(ContractRelationshipResultState state) => state switch
    {
        ContractRelationshipResultState.ExactStructuralMatch => "Exact structural match",
        ContractRelationshipResultState.Compatible => "Compatible",
        ContractRelationshipResultState.CompatibleWithDifferences => "Compatible with reviewed differences",
        ContractRelationshipResultState.Incompatible => "Incompatible",
        _ => "Insufficient evidence · not assessable",
    };

    private static string Key(string path) => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Local)).ToLowerInvariant();
    private static string Local(string? qualified) => qualified is null ? "" : qualified.Contains(':') ? qualified[(qualified.LastIndexOf(':') + 1)..] : qualified;
}
