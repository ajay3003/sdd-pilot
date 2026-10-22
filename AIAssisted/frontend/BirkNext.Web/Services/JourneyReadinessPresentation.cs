using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Generic presentation of journey readiness dimensions (any test pack): state labels/tones, the owning feature's link for each dimension
/// (the test pack never edits shared configuration — it links to the owner), and attention counted per category instead of one number.
/// </summary>
public static class JourneyReadinessPresentation
{
    /// <summary>The matrix columns of the Overview, in order.</summary>
    public static readonly IReadOnlyList<JourneyDimension> MatrixColumns =
        [JourneyDimension.Integration, JourneyDimension.Contracts, JourneyDimension.TestData, JourneyDimension.Execution, JourneyDimension.Verification];

    public static string StateLabel(JourneyDimensionState state) => state switch
    {
        JourneyDimensionState.Ready => "Ready",
        JourneyDimensionState.Partial => "Partial",
        JourneyDimensionState.ReviewRequired => "Review required",
        JourneyDimensionState.Missing => "Missing",
        JourneyDimensionState.Blocked => "Blocked",
        _ => "Not assessed",
    };

    /// <summary>Tone class suffix (matches the page's pill tones).</summary>
    public static string Tone(JourneyDimensionState state) => state switch
    {
        JourneyDimensionState.Ready => "ready",
        JourneyDimensionState.Partial or JourneyDimensionState.ReviewRequired => "partial",
        JourneyDimensionState.Missing or JourneyDimensionState.Blocked => "attention",
        _ => "muted",
    };

    public static string AssessmentLabel(JourneyAssessmentState state) => state switch
    {
        JourneyAssessmentState.Verified => "Verified",
        JourneyAssessmentState.Partial => "Partially verified",
        JourneyAssessmentState.ExecutedNotVerified => "Executed · not verified",
        JourneyAssessmentState.Failed => "Failed",
        JourneyAssessmentState.Blocked => "Blocked",
        _ => "Not run",
    };

    /// <summary>The owning feature's page for a dimension's fix; null when the owner is this page (verification).</summary>
    public static string? OwnerHref(string ownerKey, string? profileId) => ownerKey switch
    {
        "integrations" => IntegrationReviewPresentation.IntegrationsHref(profileId),
        "execution-settings" => TestPackPresentation.ExecutionSettingsHref,
        "iqr-evidence" => SourceGuidancePresentation.ActionHref("iqr-evidence"),
        "source-contracts" => "/source-analysis?area=quality-evidence&view=contracts",
        "source-infrastructure" => "/source-analysis?area=quality-evidence&view=infrastructure",
        "test-data" => "/test-data",
        "test-coverage" => "/test-coverage-review",
        "pipeline-review" => "/pipeline-review",
        _ => null,
    };

    public static string OwnerAction(string ownerKey) => ownerKey switch
    {
        "integrations" => "Configure integration",
        "execution-settings" => "Manage active execution",
        "iqr-evidence" => "Bind source evidence",
        "source-contracts" => "Open contract evidence",
        "source-infrastructure" => "Open infrastructure evidence",
        "test-data" => "Open Test Data",
        "test-coverage" => "Open Test Coverage",
        "pipeline-review" => "Open Pipeline Review",
        _ => "View details",
    };

    /// <summary>A dimension that needs attention: not ready, not optional, and assessed (Not assessed is a limitation, not an action).</summary>
    public static bool NeedsAttention(JourneyReadinessDimension d) =>
        !d.Optional && d.State is JourneyDimensionState.Missing or JourneyDimensionState.Blocked or JourneyDimensionState.Partial or JourneyDimensionState.ReviewRequired;

    public sealed record AttentionCategory(JourneyDimension Dimension, string Label, int Count, string Owner, string OwnerKey, IReadOnlyList<string> Journeys);

    /// <summary>"6 things need attention: 2 integration, 1 Test Data …" — counted per dimension across journeys, each with its owner.</summary>
    public static IReadOnlyList<AttentionCategory> Attention(IEnumerable<IntegrationJourneyView> journeys) =>
        [.. journeys.SelectMany(j => j.Dimensions.Where(NeedsAttention).Select(d => (Journey: j.Journey.DisplayName, Dimension: d)))
            .GroupBy(x => x.Dimension.Dimension).OrderBy(g => g.Key)
            .Select(g => new AttentionCategory(g.Key, g.First().Dimension.Label, g.Count(), g.First().Dimension.Owner, g.First().Dimension.OwnerKey, [.. g.Select(x => x.Journey)]))];

    public static string AttentionSummary(IReadOnlyList<AttentionCategory> categories) => categories.Count == 0
        ? "Nothing needs attention in the assessed dimensions."
        : $"{categories.Sum(c => c.Count)} thing{(categories.Sum(c => c.Count) == 1 ? "" : "s")} need attention: {string.Join(", ", categories.Select(c => $"{c.Count} {c.Label.ToLowerInvariant()}"))}";

    public static JourneyReadinessDimension? Get(IntegrationJourneyView view, JourneyDimension dimension) => view.Dimensions.FirstOrDefault(d => d.Dimension == dimension);
}
