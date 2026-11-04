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

/// <summary>
/// Functional Quality Scope filter state: the one filter model the explorer reads, its active chips, and its round-trip through the URL (fq-* keys), so a
/// filter and the selected item survive leaving the page and coming back. Values are enum names and stable item keys — never display text.
/// </summary>
public static class ScopeFilterState
{
    public static readonly IReadOnlyList<string> QueryKeys = ["fq-q", "fq-type", "fq-quality", "fq-module", "fq-relation", "fq-coverage", "fq-testdata", "fq-testability", "fq-priority", "fq-item"];

    /// <summary>Relation, Coverage, Test Data, Testability and Priority: the advanced group (open when one of them is set).</summary>
    public static bool AdvancedActive(ScopeFilter f) => f.Relation is not null || f.Coverage is not null || f.TestData is not null || f.Testability is not null || f.Priority is not null;

    public static IReadOnlyDictionary<string, object?> ToQuery(ScopeFilter f, string? selectedItem) => new Dictionary<string, object?>
    {
        ["fq-q"] = string.IsNullOrWhiteSpace(f.Search) ? null : f.Search.Trim(),
        ["fq-type"] = f.Type?.ToString(), ["fq-quality"] = f.Category?.ToString(), ["fq-module"] = f.Module,
        ["fq-relation"] = f.Relation?.ToString(), ["fq-coverage"] = f.Coverage?.ToString(), ["fq-testdata"] = f.TestData?.ToString(),
        ["fq-testability"] = f.Testability?.ToString(), ["fq-priority"] = f.Priority, ["fq-item"] = selectedItem,
    };

    public static (ScopeFilter Filter, string? SelectedItem) FromQuery(IReadOnlyDictionary<string, string?> q)
    {
        string? S(string key) => q.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        static T? E<T>(string? v) where T : struct, Enum => Enum.TryParse<T>(v, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
        return (new ScopeFilter
        {
            Search = S("fq-q"), Type = E<ScopeArtifactType>(S("fq-type")), Category = E<QualityCategory>(S("fq-quality")), Module = S("fq-module"),
            Relation = E<ScopeRelation>(S("fq-relation")), Coverage = E<ScopeCoverage>(S("fq-coverage")), TestData = E<ScopeTestDataState>(S("fq-testdata")),
            Testability = E<TestabilityClassification>(S("fq-testability")), Priority = S("fq-priority"),
        }, S("fq-item"));
    }

    /// <summary>One removable chip per active filter: its label and the filter without it.</summary>
    public static IReadOnlyList<(string Label, ScopeFilter Without)> Chips(ScopeFilter f)
    {
        var chips = new List<(string, ScopeFilter)>();
        if (!string.IsNullOrWhiteSpace(f.Search)) chips.Add(($"Search: {f.Search.Trim()}", f with { Search = null }));
        if (f.Type is { } t) chips.Add(($"Type: {FunctionalQualityScopeText.TypeLabel(t)}", f with { Type = null }));
        if (f.Category is { } c) chips.Add(($"Quality: {FunctionalQualityClassifier.Label(c)}", f with { Category = null }));
        if (f.Module is { } m) chips.Add(($"Module: {m}", f with { Module = null }));
        if (f.Relation is { } r) chips.Add(($"Relation: {FunctionalQualityScopeText.RelationLabel(r)}", f with { Relation = null }));
        if (f.Coverage is { } cov) chips.Add(($"Coverage: {FunctionalQualityScopeText.CoverageLabel(cov)}", f with { Coverage = null }));
        if (f.TestData is { } d) chips.Add(($"Test Data: {FunctionalQualityScopeText.TestDataLabel(d)}", f with { TestData = null }));
        if (f.Testability is { } ta) chips.Add(($"Testability: {FunctionalQualityScopeText.TestabilityLabel(ta)}", f with { Testability = null }));
        if (f.Priority is { } p) chips.Add(($"Priority: {p}", f with { Priority = null }));
        return chips;
    }

    public static string KindLabel(ScopeTestLinkKind kind) => kind switch
    {
        ScopeTestLinkKind.Explicit => "Explicitly linked",
        ScopeTestLinkKind.Candidate => "Candidate — not confirmed",
        _ => "Rejected in review",
    };
}
