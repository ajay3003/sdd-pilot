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
}
