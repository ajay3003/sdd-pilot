using System.Globalization;
using BirkNext.GeneratedDocumentation;

namespace BirkNext.Web.Services;

/// <summary>Filters of the Generated Documentation Health module list. Every filter is a fact of the result, never a colour. Member names are kept
/// for compatibility; the labels carry the semantics (SourceDiscrepancies = source consistency candidates, CrossArtifactDrift = cross-artifact consistency).</summary>
public enum GeneratedDocumentationFilter { All, Fresh, PotentiallyStale, UnknownFreshness, MissingExpectedDocs, SourceDiscrepancies, CrossArtifactDrift, UnableToCompare }

/// <summary>Pure presentation of a Generated Documentation Health run: filter semantics, labels and tones. Status is always shown as text;
/// amber only for a required document missing, trustworthy stale evidence or a comparable inconsistency; red only for a deterministic structural
/// failure; unknown freshness, unresolved correlation, unable to compare, optional and applicability unknown stay neutral.</summary>
public static class GeneratedDocumentationPresentation
{
    public static readonly IReadOnlyList<GeneratedDocumentationFilter> Filters = Enum.GetValues<GeneratedDocumentationFilter>();

    public static string Label(GeneratedDocumentationFilter filter) => filter switch
    {
        GeneratedDocumentationFilter.All => "All",
        GeneratedDocumentationFilter.Fresh => "Fresh",
        GeneratedDocumentationFilter.PotentiallyStale => "Potentially stale",
        GeneratedDocumentationFilter.UnknownFreshness => "Freshness unknown",
        GeneratedDocumentationFilter.MissingExpectedDocs => "Required docs missing",
        GeneratedDocumentationFilter.SourceDiscrepancies => "Source consistency candidates",
        GeneratedDocumentationFilter.CrossArtifactDrift => "Cross-artifact consistency",
        _ => "Unable to compare",
    };

    public static bool Matches(GeneratedDocumentationHealthResult module, GeneratedDocumentationFilter filter) => filter switch
    {
        GeneratedDocumentationFilter.All => true,
        GeneratedDocumentationFilter.Fresh => module.Freshness == GeneratedDocumentationFreshnessStatus.Current,
        GeneratedDocumentationFilter.PotentiallyStale => module.Freshness == GeneratedDocumentationFreshnessStatus.Stale,
        GeneratedDocumentationFilter.UnknownFreshness => module.Freshness is GeneratedDocumentationFreshnessStatus.Unknown or GeneratedDocumentationFreshnessStatus.NotEnoughEvidence,
        GeneratedDocumentationFilter.MissingExpectedDocs => module.MissingExpectedDocs.Count > 0,
        GeneratedDocumentationFilter.SourceDiscrepancies => module.SourceDiscrepancies.Count > 0,
        GeneratedDocumentationFilter.CrossArtifactDrift => module.CrossArtifactDrift.Count > 0,
        _ => module.Comparisons.Any(c => c.State == GeneratedComparisonState.UnableToCompare),
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
        GeneratedComparisonState.Equivalent or GeneratedComparisonState.Consistent => "complete",
        GeneratedComparisonState.PotentialInconsistency => "partial",
        _ => "muted",
    };

    public static string Tone(GeneratedConsistencyCategory category) => category == GeneratedConsistencyCategory.PotentialInconsistency ? "partial" : "muted";

    public static string Tone(GeneratedDocumentationReviewState state) => state == GeneratedDocumentationReviewState.NoReviewSuggested ? "muted" : "partial";

    public static string Tone(ExpectedGeneratedDocument document) => document.State switch
    {
        ExpectedDocumentState.RequiredMissing => "partial",
        ExpectedDocumentState.ExpectedByProfile when !document.Present => "partial",
        ExpectedDocumentState.RequiredPresent or ExpectedDocumentState.ExpectedByProfile => "complete",
        _ => "muted",
    };

    /// <summary>"No confirmed required documents missing" / "1 required document missing", plus neutral unresolved applicability.</summary>
    public static string RequiredCoverage(GeneratedDocumentationHealthResult module) =>
        (module.MissingExpectedDocs.Count == 0 ? "No confirmed required documents missing"
            : $"{module.MissingExpectedDocs.Count} required document{(module.MissingExpectedDocs.Count == 1 ? "" : "s")} missing")
        + (module.ApplicabilityUnresolvedDocs.Count > 0 ? $" · {module.ApplicabilityUnresolvedDocs.Count} applicability not established" : "");

    /// <summary>Review candidates first; unresolved correlations are a neutral addition, never a candidate count.</summary>
    public static string Consistency(int candidates, int unresolved) =>
        (candidates > 0 ? $"{candidates} review candidate{(candidates == 1 ? "" : "s")}" : "No comparable differences")
        + (unresolved > 0 ? $" · {unresolved} unresolved" : "");

    /// <summary>Unresolved correlations (and source-only observations) of one module, source or authored/cross-artifact side.</summary>
    public static IReadOnlyList<CrossArtifactDriftCandidate> UnresolvedFor(GeneratedDocumentationDiagnosticRun run, GeneratedDocumentationHealthResult module, bool? source = null) =>
        run.Drift.Where(d => module.UnresolvedCorrelations.Contains(d.Id) && (source is null || (d.DriftType == CrossArtifactDriftType.DocumentationDrift) == source))
            .OrderBy(d => d.DriftType).ThenBy(d => d.Family, StringComparer.Ordinal).ThenBy(d => d.StructuredKey, StringComparer.Ordinal).ToList();

    public static string ExplorerLink(GeneratedDocumentationHealthResult module) =>
        $"{NavigationCatalog.GeneratedDocumentationExplorerRoute}?module={Uri.EscapeDataString(module.RootPath.Trim('/').Length == 0 ? "(repository)" : module.RootPath.Trim('/'))}";

    public static string Date(DatedEvidence? evidence) => evidence is null ? "Not established"
        : $"{evidence.At.ToString(evidence.Precision == "Day" ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} · {evidence.Basis}";

    public static string StateTitle(GeneratedDocumentationRunState state) => state switch
    {
        GeneratedDocumentationRunState.NoSnapshot => "No source snapshot",
        GeneratedDocumentationRunState.NotAvailableForSnapshot => "Not available for this snapshot",
        GeneratedDocumentationRunState.NoGeneratedDocumentation => "No generated documentation",
        _ => "Generated documentation evaluated",
    };

    /// <summary>Review candidates (comparable conflicting claims) of one module, grouped by family (source consistency first, then authored types).</summary>
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
