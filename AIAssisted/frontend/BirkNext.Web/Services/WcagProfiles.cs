using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed record WcagAssessmentProfile(string ProfileId, string DisplayName, string LegalJurisdiction,
    string Sector, WcagVersion WcagVersion, IReadOnlyList<string> CriterionIds, bool IsLegalBaseline, string Description)
{
    public string VersionLabel => ProfileId == "legacy-unknown" ? "Unknown" : WcagVersion == WcagVersion.Wcag21 ? "2.1" : "2.2";
    public string Label => ProfileId == "legacy-unknown" ? "Saved assessment — Profile unknown" : $"{DisplayName} — WCAG {VersionLabel}";
}

/// <summary>Membership only; all criterion metadata remains in WcagRegistry.</summary>
public static class WcagProfiles
{
    public const string NorwegianId = "no-public-wcag21-48-v1";
    public const string ExtendedId = "extended-wcag22-aa-v1";
    public const string LegalSource = "https://www.uutilsynet.no/wcag-standarden/wcag-standarden/86";
    // Uutilsynet public-sector list, verified 2026-09-17. Includes 1.2.5 and 4.1.1; excludes 1.2.3 and 1.2.4.
    public static WcagAssessmentProfile Norwegian { get; } = new(NorwegianId, "Norwegian legal baseline", "Norway",
        "Public sector", WcagVersion.Wcag21, Array.AsReadOnly(
        "1.1.1 1.2.1 1.2.2 1.2.5 1.3.1 1.3.2 1.3.3 1.3.4 1.3.5 1.4.1 1.4.2 1.4.3 1.4.4 1.4.5 1.4.10 1.4.11 1.4.12 1.4.13 2.1.1 2.1.2 2.1.4 2.2.1 2.2.2 2.3.1 2.4.1 2.4.2 2.4.3 2.4.4 2.4.5 2.4.6 2.4.7 2.5.1 2.5.2 2.5.3 2.5.4 3.1.1 3.1.2 3.2.1 3.2.2 3.2.3 3.2.4 3.3.1 3.3.2 3.3.3 3.3.4 4.1.1 4.1.2 4.1.3".Split(' ')),
        true, "Uutilsynet public-sector statutory subset. Applicability and complete processes require human assessment. " + LegalSource);
    public static WcagAssessmentProfile Extended => new(ExtendedId, "Extended assessment", "None", "Optional",
        WcagVersion.Wcag22, WcagRegistry.All.Where(d => d.CriterionId != "4.1.1").Select(d => d.CriterionId).ToArray(), false,
        "Optional WCAG 2.2 A + AA review; not the Norwegian legal baseline.");
    public static IReadOnlyList<WcagAssessmentProfile> Available => [Norwegian, Extended];
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
