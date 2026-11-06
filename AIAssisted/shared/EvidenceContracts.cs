using System.Text.Json.Serialization;

namespace BirkNext.Evidence;

// Domain-neutral evidence contracts shared by modules that keep their own readiness rules (Test Data, Performance, Release Readiness, Delivery…).
// They describe WHERE evidence comes from and WHETHER it is still the evidence a result was based on — never what a module concludes from it.

/// <summary>
/// Whether evidence a result was based on is still the owner's current evidence. Only <see cref="Current"/> may support a Ready/Passed claim:
/// changed evidence is Stale, evidence its owner no longer holds is Missing, and evidence without a comparable identity is Unknown — never Current.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceFreshness { Current, Stale, Missing, Unknown }

/// <summary>A reference to evidence owned by another store: owner, identity and fingerprint at the time it was used — never a copy of the payload.</summary>
public sealed record EvidenceReference(string Owner, string Id, string? Fingerprint, DateTimeOffset? RecordedAt = null);

/// <summary>Who evaluated a result, with which rule version, when, and over which input fingerprint (for reports that must say how they were made).</summary>
public sealed record EvaluationProvenance(string Authority, string RuleVersion, DateTimeOffset EvaluatedAt, string? InputFingerprint = null);

public static class EvidenceFreshnessRules
{
    /// <summary>
    /// The freshness of evidence recorded as <paramref name="recorded"/> against the owner's <paramref name="current"/> identity (fingerprint or
    /// version). Missing when the owner no longer holds it; Unknown when either side has no identity; Current only on an exact (ordinal) match.
    /// </summary>
    public static EvidenceFreshness Of(string? recorded, string? current, bool ownerHolds = true) =>
        !ownerHolds ? EvidenceFreshness.Missing
        : recorded is null || current is null ? EvidenceFreshness.Unknown
        : string.Equals(recorded, current, StringComparison.Ordinal) ? EvidenceFreshness.Current
        : EvidenceFreshness.Stale;

    /// <summary>Only current evidence can support a positive conclusion; Stale, Missing and Unknown never can.</summary>
    public static bool CanSupportPositive(EvidenceFreshness freshness) => freshness == EvidenceFreshness.Current;
}
