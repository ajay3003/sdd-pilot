using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BirkNext.Interpretation;

// ── Canonical scoped views: Source × Functional scope ─────────────────────────────────────────────────────────────────────
// Two separate dimensions, never conflated:
//   Source           — where the evidence came from (a canonical Package / repository identity; a ZIP name is provenance only).
//   Functional scope — what business function or change it belongs to (a canonical Capability / change intent, a declared domain flow,
//                      or a saved Functional Quality Scope). Grouping features under one scope never merges their identities.
// Consumers add their own post-scope filters (Delivery: module, state, attention, search). Membership follows a strict relation whitelist
// (Feature —Implements→ Capability, established only); a dependency relation never makes a feature part of a functional scope.

/// <summary>The shared scope filter. Values are stable ids (<c>package:m2lb-language</c>, <c>capability:decision:adr-029</c>, <c>profile:x</c>,
/// <c>custom:{guid}</c>) — never display names.</summary>
public sealed record CanonicalScopeFilter(string? SourceId = null, string? FunctionalScopeId = null, string? Module = null)
{
    [JsonIgnore] public bool IsScoped => SourceId is not null || FunctionalScopeId is not null;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FunctionalScopeKind { Capability, ChangeIntent, DomainFlow, SavedScope }

/// <summary>Why one feature belongs to a functional scope: the relation path and its evidence ("Implements → ADR-029: Translate English").</summary>
public sealed record CanonicalScopeMember(string FeatureIdentity, string Resolution, string Rule, string Path, string Evidence);

/// <summary>One canonical functional scope over features: established members are in it; candidates wait for a person's decision and are never
/// counted as members.</summary>
public sealed record CanonicalFunctionalScope(string Id, string Name, FunctionalScopeKind Kind, string Detail,
    IReadOnlyList<CanonicalScopeMember> Members, IReadOnlyList<CanonicalScopeMember> Candidates);

public static class CanonicalScopeText
{
    public const string SourceHelp = "Source limits the view to evidence originating from a selected repository or imported source.";
    public const string FunctionalScopeHelp =
        "Functional scope groups related features, stories and scenarios by canonical capability or change intent. It does not merge their identities.";
    public const string Semantics =
        "Source = the imported source the feature was read from (its snapshot's repository/package), not a related contract in another source. " +
        "Functional scope = established Feature → Capability relations only (explicit, derived from a declaration, or confirmed by a person); " +
        "candidates are listed for review and never counted; dependencies never make a feature part of a scope.";

    public static string KindLabel(FunctionalScopeKind kind) => kind switch
    {
        FunctionalScopeKind.ChangeIntent => "Change intent",
        FunctionalScopeKind.DomainFlow => "Domain flow",
        FunctionalScopeKind.SavedScope => "Saved scope",
        _ => "Capability",
    };
}

/// <summary>
/// Decision records (ADR-nnn) a feature declares or cites. Declared = named in the feature's title or folder ("Translate Frontend to English
/// (ADR-029)", "007-translate-adr-029"): the author put the feature under that decision. Cited = mentioned in spec.md only: related, not
/// necessarily the same change. The id is normalised ("adr 29", "ADR-0029" → "ADR-029"); no decision number has a built-in meaning.
/// </summary>
public static partial class DecisionReferences
{
    public static IReadOnlyList<string> InText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in AdrRe().Matches(text))
            if (int.TryParse(m.Groups[1].Value, out var n) && n > 0) found.Add($"ADR-{n:000}");
        return [.. found];
    }

    public static IReadOnlyList<string> Declared(string title, string featureKey) =>
        [.. InText(title).Concat(InText(featureKey[(featureKey.LastIndexOf(':') + 1)..])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"(?<![\p{L}\d])ADR[\s_-]?0*(\d{1,4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex AdrRe();
}
