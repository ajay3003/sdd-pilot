using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed record WcagAssessmentProfile(string ProfileId, string DisplayName, string LegalJurisdiction,
    string Sector, WcagVersion WcagVersion, IReadOnlyList<string> CriterionIds, bool IsLegalBaseline, string Description)
{
    /// <summary>
    /// Exact user-facing name, where the composed "{DisplayName} — WCAG {version}" form reads the wrong way round.
    /// A standards-named profile leads with the standard ("WCAG 2.2 AA — Extended review"); the legal baseline leads
    /// with the requirement it encodes.
    /// </summary>
    public string? ExplicitLabel { get; init; }

    public string VersionLabel => ProfileId == "legacy-unknown" ? "Unknown" : WcagVersion == WcagVersion.Wcag21 ? "2.1" : "2.2";

    public string Label => ExplicitLabel
        ?? (ProfileId == "legacy-unknown" ? "Saved assessment — Profile unknown" : $"{DisplayName} — WCAG {VersionLabel}");

    /// <summary>Criteria this profile puts in scope. The count is derived, never hard-coded in the UI.</summary>
    public int CriteriaInScope => CriterionIds.Count;

    /// <summary>
    /// One line under the name in the selector: how many criteria this profile puts in scope.
    ///
    /// Deliberately NOT "applicable success criteria". Applicability is a property of the target — which criteria a
    /// particular page actually engages — and nothing has been assessed when this line is read. The number is the size
    /// of the profile, which is all that is known before a review runs.
    /// </summary>
    public string ScopeSummary => ProfileId == "legacy-unknown"
        ? "Original profile and version are unknown."
        : $"{CriteriaInScope} criteria in this profile";

    /// <summary>The same fact stated for the profile that is currently chosen, where "this profile" would be ambiguous.</summary>
    public string SelectedScopeSummary => ProfileId == "legacy-unknown"
        ? "Original profile and version are unknown."
        : $"{CriteriaInScope} criteria in selected profile";
}

/// <summary>Membership only; all criterion metadata remains in WcagRegistry.</summary>
public static class WcagProfiles
{
    public const string NorwegianId = "no-public-wcag21-48-v1";
    public const string ExtendedId = "extended-wcag22-aa-v1";
    /// <summary>The whole WCAG 2.1 A + AA standard, not only the statutory public-sector subset.</summary>
    public const string Wcag21AaId = "wcag21-aa-full-v1";
    public const string LegalSource = "https://www.uutilsynet.no/wcag-standarden/wcag-standarden/86";
    // Uutilsynet public-sector list, verified 2026-09-17. Includes 1.2.5 and 4.1.1; excludes 1.2.3 and 1.2.4.
    public static WcagAssessmentProfile Norwegian { get; } = new(NorwegianId, "Norwegian public-sector requirements", "Norway",
        "Public sector", WcagVersion.Wcag21, Array.AsReadOnly(
        "1.1.1 1.2.1 1.2.2 1.2.5 1.3.1 1.3.2 1.3.3 1.3.4 1.3.5 1.4.1 1.4.2 1.4.3 1.4.4 1.4.5 1.4.10 1.4.11 1.4.12 1.4.13 2.1.1 2.1.2 2.1.4 2.2.1 2.2.2 2.3.1 2.4.1 2.4.2 2.4.3 2.4.4 2.4.5 2.4.6 2.4.7 2.5.1 2.5.2 2.5.3 2.5.4 3.1.1 3.1.2 3.2.1 3.2.2 3.2.3 3.2.4 3.3.1 3.3.2 3.3.3 3.3.4 4.1.1 4.1.2 4.1.3".Split(' ')),
        true, "Uutilsynet public-sector statutory subset. Applicability and complete processes require human assessment. " + LegalSource);

    /// <summary>
    /// Every WCAG 2.1 A + AA criterion, membership derived from the registry rather than listed again here. Broader
    /// than the statutory subset: it adds criteria Norway does not require and keeps 4.1.1, which 2.2 obsoletes.
    /// </summary>
    public static WcagAssessmentProfile Wcag21Aa => new(Wcag21AaId, "Full standard", "None", "Optional",
        WcagVersion.Wcag21,
        WcagRegistry.All.Where(d => d.Since <= WcagVersion.Wcag21).Select(d => d.CriterionId).ToArray(), false,
        "The complete WCAG 2.1 A + AA standard. Broader than the Norwegian public-sector subset and not itself a legal baseline.")
    { ExplicitLabel = "WCAG 2.1 AA — Full standard" };

    public static WcagAssessmentProfile Extended => new(ExtendedId, "Extended review", "None", "Optional",
        WcagVersion.Wcag22, WcagRegistry.All.Where(d => d.CriterionId != "4.1.1").Select(d => d.CriterionId).ToArray(), false,
        "Optional WCAG 2.2 A + AA review; not the Norwegian legal baseline.")
    { ExplicitLabel = "WCAG 2.2 AA — Extended review" };

    /// <summary>Legal baseline first: it is the default and the one most reviews should use.</summary>
    public static IReadOnlyList<WcagAssessmentProfile> Available => [Norwegian, Wcag21Aa, Extended];
    public static WcagAssessmentProfile Resolve(string id)
    {
        var known = Available.SingleOrDefault(p => p.ProfileId == id);
        if (known is not null) return known;
        if (id == "legacy-unknown") return new(id, "Saved assessment", "Unknown", "Unknown", WcagVersion.Wcag21, [], false, "Original profile and version are unknown. Select a profile to make a new assessment.");
        if (id is "legacy-21-A" or "legacy-21-AA" or "legacy-22-A" or "legacy-22-AA")
        {
            var version = id.Contains("21") ? WcagVersion.Wcag21 : WcagVersion.Wcag22;
            return new(id, $"General {(id.EndsWith("AA") ? "A + AA" : "level A")} assessment", "Unknown", "Unknown", version,
                WcagRegistry.All.Where(d => d.Since <= version && (id.EndsWith("AA") || d.Level == WcagLevel.A))
                    .Select(d => d.CriterionId).ToArray(), false, "Original version/level target; not a legal-profile assessment.");
        }
        throw new ArgumentException("Unknown WCAG assessment profile.");
    }
}
