using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IStandardsComplianceService
{
    /// <summary>
    /// Results of loading each rule pack. Available after InitializeAsync completes.
    /// Entries with a non-null Error indicate packs that failed to load.
    /// </summary>
    IReadOnlyList<RulePackLoadResult> LoadedPacks { get; }

    /// <summary>
    /// All packs discovered from standards/index.json, in index order.
    /// Populated after InitializeAsync completes; empty if the index could not be loaded.
    /// Includes entries for packs that failed to load — check LoadedPacks for per-pack errors.
    /// </summary>
    IReadOnlyList<RulePackIndexEntry> DiscoveredPacks { get; }

    /// <summary>
    /// Loads rule packs from wwwroot/standards/index.json. Safe to call multiple
    /// times — subsequent calls are no-ops. Must be awaited before calling Assess.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Runs keyword-based standards checks against pre-extracted clean text
    /// produced by the shared Markdown Document Engine.
    /// </summary>
    StandardsComplianceReport Assess(
        string              combinedText,
        bool                hasConstitution,
        bool                hasSpec,
        bool                hasPlan,
        bool                hasTasks,
        IEnumerable<string> selectedStandards);
}
