using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IQualityReviewService
{
    /// <summary>
    /// All available review packs in display order (Core → Governance → Industry Standards).
    /// Populated after InitializeAsync completes.
    /// </summary>
    IReadOnlyList<QualityReviewPackDescriptor> AvailablePacks { get; }

    /// <summary>
    /// Loads dynamic packs (e.g. standards discovered from index.json).
    /// Safe to call multiple times — subsequent calls are no-ops.
    /// Must be awaited before calling RunAsync.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Parse all artifacts once, run every selected pack against the shared parsed output,
    /// and return a combined report.
    /// </summary>
    Task<QualityReviewReport> RunAsync(
        string? constitutionText,
        string? specText,
        string? planText,
        string? taskText,
        string? dataModelText,
        IEnumerable<string> selectedPackIds);

    /// <summary>
    /// The same run with its document scope: the Artifact Pack inventory (primary, supporting and generated documents with provenance) and the
    /// documentation gate policy. The report records exactly which documents each Review Pack read.
    /// </summary>
    Task<QualityReviewReport> RunAsync(QualityReviewRunInput input, IEnumerable<string> selectedPackIds) =>
        RunAsync(input.ConstitutionText, input.SpecText, input.PlanText, input.TaskText, input.DataModelText, selectedPackIds);
}
