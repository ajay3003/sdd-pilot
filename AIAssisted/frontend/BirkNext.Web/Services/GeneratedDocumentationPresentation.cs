using System.Globalization;
using BirkNext.GeneratedDocumentation;

namespace BirkNext.Web.Services;

/// <summary>Filters of the Generated Documentation Health module list. Every filter is a fact of the result, never a colour.</summary>
public enum GeneratedDocumentationFilter { All, Fresh, PotentiallyStale, UnknownFreshness, MissingExpectedDocs, SourceDiscrepancies, CrossArtifactDrift }

/// <summary>Pure presentation of a Generated Documentation Health run: filter semantics, labels and tones. Status is always shown as text;
/// amber ("Needs review") for staleness and drift, red only for a deterministic structural failure.</summary>
public static class GeneratedDocumentationPresentation
{
    public static readonly IReadOnlyList<GeneratedDocumentationFilter> Filters = Enum.GetValues<GeneratedDocumentationFilter>();

    public static string Label(GeneratedDocumentationFilter filter) => filter switch
    {
        GeneratedDocumentationFilter.All => "All",
        GeneratedDocumentationFilter.Fresh => "Fresh",
        GeneratedDocumentationFilter.PotentiallyStale => "Potentially stale",
        GeneratedDocumentationFilter.UnknownFreshness => "Unknown freshness",
        GeneratedDocumentationFilter.MissingExpectedDocs => "Missing expected docs",
        GeneratedDocumentationFilter.SourceDiscrepancies => "Source discrepancies",
        _ => "Cross-artifact drift",
    };

    public static bool Matches(GeneratedDocumentationHealthResult module, GeneratedDocumentationFilter filter) => filter switch
    {
        GeneratedDocumentationFilter.All => true,
        GeneratedDocumentationFilter.Fresh => module.Freshness == GeneratedDocumentationFreshnessStatus.Current,
        GeneratedDocumentationFilter.PotentiallyStale => module.Freshness == GeneratedDocumentationFreshnessStatus.Stale,
        GeneratedDocumentationFilter.UnknownFreshness => module.Freshness is GeneratedDocumentationFreshnessStatus.Unknown or GeneratedDocumentationFreshnessStatus.NotEnoughEvidence,
        GeneratedDocumentationFilter.MissingExpectedDocs => module.MissingExpectedDocs.Count > 0,
        GeneratedDocumentationFilter.SourceDiscrepancies => module.SourceDiscrepancies.Count > 0,
        _ => module.CrossArtifactDrift.Count > 0,
    };

    public static int Count(GeneratedDocumentationDiagnosticRun run, GeneratedDocumentationFilter filter) => run.Modules.Count(m => Matches(m, filter));

    public static string Tone(GeneratedDocumentationHealthStatus status) => status switch
    {
        GeneratedDocumentationHealthStatus.Pass => "complete",
        GeneratedDocumentationHealthStatus.Warning => "partial",
        GeneratedDocumentationHealthStatus.Fail => "attention",
        _ => "muted",
    };

    public static string Tone(GeneratedDocumentationFreshnessStatus status) => status switch
    {
        GeneratedDocumentationFreshnessStatus.Current => "complete",
        GeneratedDocumentationFreshnessStatus.Stale => "partial",
        _ => "muted",
    };

    public static string Tone(GeneratedComparisonState state) => state switch
    {
        GeneratedComparisonState.Equivalent => "complete",
        GeneratedComparisonState.PotentiallyDrifted => "partial",
        _ => "muted",
    };

    public static string Date(DatedEvidence? evidence) => evidence is null ? "Not established"
        : $"{evidence.At.ToString(evidence.Precision == "Day" ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} · {evidence.Basis}";

    public static string StateTitle(GeneratedDocumentationRunState state) => state switch
    {
        GeneratedDocumentationRunState.NoSnapshot => "No source snapshot",
        GeneratedDocumentationRunState.NotAvailableForSnapshot => "Not available for this snapshot",
        GeneratedDocumentationRunState.NoGeneratedDocumentation => "No generated documentation",
        _ => "Generated documentation evaluated",
    };

    /// <summary>Drift candidates of one module, grouped by family (documentation drift first, then authored drift types).</summary>
    public static IReadOnlyList<CrossArtifactDriftCandidate> DriftFor(GeneratedDocumentationDiagnosticRun run, GeneratedDocumentationHealthResult module) =>
        run.Drift.Where(d => module.SourceDiscrepancies.Contains(d.Id) || module.CrossArtifactDrift.Contains(d.Id))
            .OrderBy(d => d.DriftType).ThenBy(d => d.Family, StringComparer.Ordinal).ThenBy(d => d.StructuredKey, StringComparer.Ordinal).ToList();

    public static string Side(CrossArtifactDriftCandidate drift, bool left) => drift.DriftType switch
    {
        CrossArtifactDriftType.DocumentationDrift => left ? "Generated documentation" : "Source / configuration / contract evidence",
        CrossArtifactDriftType.SpecificationDrift => left ? "Specification" : "Generated documentation",
        CrossArtifactDriftType.ConstitutionDrift => left ? "Constitution rule" : "Generated documentation",
        CrossArtifactDriftType.PlanDrift => left ? "Plan" : "Generated documentation",
        _ => left ? "Task (marked complete)" : "Generated documentation / source evidence",
    };

    public static string ShortId(string id) => id.Length > 12 ? id[..12] : id;
}
