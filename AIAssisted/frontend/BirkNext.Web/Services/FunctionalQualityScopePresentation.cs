using BirkNext.DeliveryReporting;

namespace BirkNext.Web.Services;

/// <summary>Filters and grouping for the Functional Quality Scope tab. Pure, so the panel stays declarative and the rules are testable.</summary>
public sealed record ScopeFilter
{
    public ScopeArtifactType? Type { get; init; }
    public QualityCategory? Category { get; init; }
    public string? Module { get; init; }
    public ScopeRelation? Relation { get; init; }
    public ScopeCoverage? Coverage { get; init; }
    public ScopeTestDataState? TestData { get; init; }
    public TestabilityClassification? Testability { get; init; }
    public string? Priority { get; init; }
    public string? Search { get; init; }
}

public sealed record ScopeFeatureGroup(string Module, string FeatureKey, string FeatureTitle, ScopeRelation Relation, IReadOnlyList<ScopeItem> Items);

public static class FunctionalQualityScopePresentation
{
    public const string TabId = "scope";
    public const string SkolenaervaerScope = "profile:skolenaervaer";

    /// <summary>Deep link into the scope tab (e.g. from Skolenærvær Testing). The page falls back to the first reporting project.</summary>
    public static string Href(string scopeKey, Guid? projectId = null) =>
        $"{DeliveryReportingPresentation.Route}?{(projectId is { } p ? $"project={p}&" : "")}tab={TabId}&scope={Uri.EscapeDataString(scopeKey)}";

    public static IReadOnlyList<ScopeItem> Apply(IEnumerable<ScopeItem> items, ScopeFilter f) => [.. items.Where(i =>
        (f.Type is null || i.Type == f.Type)
        && (f.Category is null || i.Category == f.Category)
        && (string.IsNullOrEmpty(f.Module) || string.Equals(i.Module, f.Module, StringComparison.OrdinalIgnoreCase))
        && (f.Relation is null || i.Relation == f.Relation)
        && (f.Coverage is null || i.Coverage == f.Coverage)
        && (f.TestData is null || i.TestData == f.TestData)
        && (f.Testability is null || i.Testability == f.Testability)
        && (string.IsNullOrEmpty(f.Priority) || string.Equals(i.Priority, f.Priority, StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(f.Search) || Matches(i, f.Search.Trim())))];

    private static bool Matches(ScopeItem i, string q) =>
        i.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || i.DisplayId.Contains(q, StringComparison.OrdinalIgnoreCase)
        || i.FeatureKey.Contains(q, StringComparison.OrdinalIgnoreCase) || i.FeatureTitle.Contains(q, StringComparison.OrdinalIgnoreCase);

    /// <summary>Groups by feature (module/feature) in resolution order; an unavailable item forms its own group under its module.</summary>
    public static IReadOnlyList<ScopeFeatureGroup> Group(IReadOnlyList<ScopeItem> items) =>
        [.. items.GroupBy(i => (i.Module, i.FeatureKey)).Select(g => new ScopeFeatureGroup(g.Key.Module, g.Key.FeatureKey,
            g.First().FeatureTitle is { Length: > 0 } t ? t : g.First().Unavailable ? "Unavailable items" : g.Key.FeatureKey, g.Min(i => i.Relation), [.. g]))];

    public static SemanticStatus CoverageStatus(ScopeCoverage c) => c switch
    {
        ScopeCoverage.AutomatedCoverage => SemanticStatus.Available,
        ScopeCoverage.CandidateCoverageOnly => SemanticStatus.NeedsReview,
        ScopeCoverage.NoKnownCoverage => SemanticStatus.Missing,
        _ => SemanticStatus.NotAssessed,
    };

    public static SemanticStatus TestDataStatus(ScopeTestDataState s) => s switch
    {
        ScopeTestDataState.Available => SemanticStatus.Available,
        ScopeTestDataState.MissingRequired => SemanticStatus.Missing,
        ScopeTestDataState.NotRequired => SemanticStatus.Ready,
        _ => SemanticStatus.Unknown,
    };

    public static string KindHeading(string kind) => kind switch
    {
        "Domain flow" => "Domain flows",
        "Module" => "Modules",
        _ => "Custom scopes",
    };

    /// <summary>The verification types a test designer can choose (stored by name on the assessment).</summary>
    public static readonly IReadOnlyList<string> VerificationTypes = ["Manual", "Automated UI", "Automated API", "Integration", "Contract", "Exploratory"];

    public static string SourceRef(ScopeItem i) => i.SourcePath is { Length: > 0 } p ? i.SourceLine is { } l ? $"{p}:{l}" : p : "";
}
