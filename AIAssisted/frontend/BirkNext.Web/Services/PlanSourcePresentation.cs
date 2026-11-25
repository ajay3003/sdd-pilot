using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// Plain Norwegian wording for the source basis of a discovery and the source state of saved cases (2026-11-23). Only what the backend reports is
/// shown: a state the server did not compute is never inferred from timestamps here.
/// </summary>
public static class PlanSourcePresentation
{
    public sealed record Label(string Text, string Tone);

    public static string BasisTitle(DiscoverySourceBasis basis) => basis.Kind switch
    {
        DiscoveryBasisKind.Historical => "Historisk innsyn — endrer ikke gjeldende testplaner",
        DiscoveryBasisKind.DocumentsOnly => "Bare dokumenter — ingen kildeversjon er knyttet til prosjektet",
        _ when !basis.Complete => "Kilde mangler — grunnlaget er ufullstendig",
        _ => "Gjeldende kildegrunnlag",
    };

    public static string BasisSources(DiscoverySourceBasis basis) => basis.Members.Count == 0 ? "Ingen kildeversjon."
        : string.Join(" · ", basis.Members.Select(m => m.SnapshotId is null ? $"{m.DisplayName}: ikke valgt"
            : $"{m.DisplayName}: {m.ArchiveName}{(m.AnalyzedAt is { } at ? $" ({at.ToLocalTime():yyyy-MM-dd HH:mm})" : "")}{(m.StateCode == "INSPECTED" ? " — eldre versjon" : "")}{(m.NewerArchiveName is { } newer && m.SnapshotId is not null ? $" · ny versjon finnes: {newer}" : "")}"));

    public static string SpecificationOrigin(DiscoverySourceBasis basis) => basis.SpecificationOrigin switch
    {
        BirkNext.TestPlanning.SpecificationOrigin.ImportedSnapshot => "Spesifikasjon: den importerte versjonen som hører til kildeversjonen.",
        BirkNext.TestPlanning.SpecificationOrigin.WorkingDraft => "Spesifikasjon: arbeidsutkast i nettleseren (ikke den importerte versjonen).",
        BirkNext.TestPlanning.SpecificationOrigin.BrowserUnverified => "Spesifikasjon: nettleserens kopi — kan ikke kontrolleres mot kildeversjonen (eldre import).",
        _ => "Ingen spesifikasjon i grunnlaget.",
    };

    /// <summary>The case's source state as a short label ("Utgått scenario", "Kildegrunnlag endret", "Kilde mangler"); null when current or unknown.</summary>
    public static Label? CaseState(TestCaseDraft c) => c.SourceState switch
    {
        CaseSourceState.Removed => new("Utgått scenario", "bad"),
        CaseSourceState.Changed => new("Kildegrunnlag endret", "warn"),
        CaseSourceState.SourceUnavailable => new("Kilde mangler", "muted"),
        CaseSourceState.NotCompared => new("Ikke sammenlignet", "muted"),
        _ => null,
    };
}
