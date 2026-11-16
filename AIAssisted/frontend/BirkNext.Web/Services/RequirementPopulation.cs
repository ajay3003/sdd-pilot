using BirkNext.Sdd;
using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// R1 — the one authoritative requirement population for coverage numbers: the requirements of the project's <b>selected specification</b>
/// (the focused module's selected specification in a monorepo), read exactly as Test Coverage reads them
/// (<see cref="TestCoveragePresentation.WorkspaceEvidence"/>), with identifiers in the shared grammar (<see cref="RequirementReferenceParser"/>).
/// Three different measures are taken over this same population and are never presented as one another:
/// <list type="bullet">
/// <item><b>Specification coverage</b> — the specification itself states acceptance scenarios for the requirement (Specification Explorer).</item>
/// <item><b>Test coverage</b> — prepared or executed tests linked to the requirement (Test Coverage, Requirements Traceability).</item>
/// <item><b>Relationship-analysis coverage</b> — a reviewed scenario association from the last relationship analysis (canonical interpretation);
/// <see cref="Relationship"/> counts it over the population and reports what the analysis does not cover or covers outside the selection.</item>
/// </list>
/// </summary>
public static class RequirementPopulation
{
    /// <summary>The selected specification's requirement ids (normalised), or null when no specification is selected or it cannot be read.</summary>
    public static async Task<IReadOnlyList<string>?> SelectedSpecificationAsync(IServiceProvider services, CancellationToken ct = default)
    {
        if (services.GetService(typeof(IWorkspaceSessionService)) is not IWorkspaceSessionService workspace
            || services.GetService(typeof(Explorers.IArtifactExplorerContext)) is not Explorers.IArtifactExplorerContext artifacts) return null;
        try
        {
            var specification = await artifacts.GetStateAsync(WorkspaceArtifactType.Specification, ct);
            var context = (services.GetService(typeof(IReviewContextProvider)) as IReviewContextProvider)?.GetCurrent();
            var evidence = TestCoveragePresentation.WorkspaceEvidence(workspace, context, null, specification);
            return evidence.SpecificationAvailable ? Of(evidence.Requirements.Select(r => r.Id)) : null;
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    public static IReadOnlyList<string> Of(IEnumerable<string> ids) =>
        [.. ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(RequirementReferenceParser.Normalize).Distinct(StringComparer.Ordinal)];

    /// <summary>Relationship-analysis coverage over the population.</summary>
    /// <param name="Population">Requirements in the selected specification.</param>
    /// <param name="Reviewed">Of those, with reviewed scenario coverage in the last relationship analysis.</param>
    /// <param name="NeedsReview">Of those, with only suggested associations (Relationship Review decides).</param>
    /// <param name="NotAnalysed">Of those, absent from the last relationship analysis (re-run Analyze relationships).</param>
    /// <param name="OutsideSelection">Analysed requirements that are not in the selected specification (another module or an older text) — not counted.</param>
    public sealed record RelationshipCoverage(int Population, int Reviewed, int NeedsReview, int NotAnalysed, int OutsideSelection);

    public static RelationshipCoverage Relationship(RequirementCoverageView view, IReadOnlyList<string> population)
    {
        var inPopulation = population.ToHashSet(StringComparer.Ordinal);
        // One state per requirement id: the most advanced state wins when a requirement appears in several modules or features.
        var states = view.Rows.Where(r => !string.IsNullOrWhiteSpace(r.RequirementId))
            .GroupBy(r => RequirementReferenceParser.Normalize(r.RequirementId), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(r => r.State), StringComparer.Ordinal);
        return new(inPopulation.Count,
            inPopulation.Count(id => states.TryGetValue(id, out var s) && s == RequirementCoverageState.ReviewedScenarioCoverage),
            inPopulation.Count(id => states.TryGetValue(id, out var s) && s == RequirementCoverageState.NeedsRelationshipReview),
            inPopulation.Count(id => !states.ContainsKey(id)),
            states.Keys.Count(id => !inPopulation.Contains(id)));
    }
}
