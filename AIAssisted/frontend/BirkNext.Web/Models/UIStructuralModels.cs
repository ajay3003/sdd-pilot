namespace BirkNext.Web.Models;

public enum PageMigrationStatus
{
    FullyConsistent,
    PartiallyMigrated,
    LegacyContaminated,
    NotMigrated
}

public enum UIPageSection
{
    PageHeader,
    KpiMetrics,
    ContentCards,
    Recommendations,
    InputFilterPanels,
    EmptyStates,
    FooterAuxiliary
}

public enum UISectionStatus
{
    Consistent,
    PartiallyMigrated,
    Legacy,
    Hybrid
}

public enum UIStructuralSeverity { Critical, High, Medium, Low }

public sealed class UIHybridPatternDetection
{
    public string PageName     { get; init; } = string.Empty;
    public string SectionName  { get; init; } = string.Empty;
    public string PatternType  { get; init; } = string.Empty;
    public string Description  { get; init; } = string.Empty;
    public string? LegacyElement { get; init; }
    public string? SharedElement { get; init; }
}

public sealed class UISectionFinding
{
    public UIPageSection Section { get; init; }
    public UISectionStatus Status { get; init; }
    public IReadOnlyList<UIHybridPatternDetection> Patterns { get; init; } = [];

    public string SectionDisplayName => Section switch
    {
        UIPageSection.PageHeader       => "Page Header",
        UIPageSection.KpiMetrics       => "KPI / Metrics",
        UIPageSection.ContentCards     => "Content Cards",
        UIPageSection.Recommendations  => "Recommendations",
        UIPageSection.InputFilterPanels => "Input / Filter Panels",
        UIPageSection.EmptyStates      => "Empty States",
        UIPageSection.FooterAuxiliary  => "Footer / Auxiliary",
        _                              => Section.ToString()
    };

    public UIStructuralSeverity Severity => Status switch
    {
        UISectionStatus.Hybrid            => UIStructuralSeverity.Critical,
        UISectionStatus.Legacy            => UIStructuralSeverity.High,
        UISectionStatus.PartiallyMigrated => UIStructuralSeverity.Medium,
        _                                 => UIStructuralSeverity.Low
    };

    public string IssueCardSeverity => Status switch
    {
        UISectionStatus.Hybrid            => "critical",
        UISectionStatus.Legacy            => "error",
        UISectionStatus.PartiallyMigrated => "warning",
        _                                 => "info"
    };
}

public sealed class UIStructuralFinding
{
    public string PageName  { get; init; } = string.Empty;
    public string PageRoute { get; init; } = string.Empty;
    public IReadOnlyList<UISectionFinding> Sections { get; init; } = [];

    public PageMigrationStatus DerivedStatus
    {
        get
        {
            if (Sections.Count == 0) return PageMigrationStatus.NotMigrated;
            if (Sections.Any(s => s.Status == UISectionStatus.Hybrid))
                return PageMigrationStatus.LegacyContaminated;
            if (Sections.All(s => s.Status == UISectionStatus.Consistent))
                return PageMigrationStatus.FullyConsistent;
            var legacyCount = Sections.Count(s => s.Status == UISectionStatus.Legacy);
            return legacyCount > Sections.Count / 2
                ? PageMigrationStatus.NotMigrated
                : PageMigrationStatus.PartiallyMigrated;
        }
    }
}

public sealed class UIStructuralIntegrityReport
{
    public IReadOnlyList<UIStructuralFinding> PageResults { get; init; } = [];

    private IReadOnlyList<UISectionFinding> AllSections =>
        PageResults.SelectMany(p => p.Sections).ToList();

    public int FullyConsistentSectionCount   => AllSections.Count(s => s.Status == UISectionStatus.Consistent);
    public int HybridSectionCount            => AllSections.Count(s => s.Status == UISectionStatus.Hybrid);
    public int PartiallyMigratedSectionCount => AllSections.Count(s => s.Status == UISectionStatus.PartiallyMigrated);
    public int LegacySectionCount            => AllSections.Count(s => s.Status == UISectionStatus.Legacy);

    public int FullyConsistentPageCount    => PageResults.Count(p => p.DerivedStatus == PageMigrationStatus.FullyConsistent);
    public int LegacyContaminatedPageCount => PageResults.Count(p => p.DerivedStatus == PageMigrationStatus.LegacyContaminated);

    public IReadOnlyList<UIHybridPatternDetection> TopHybridPatterns =>
        PageResults
            .SelectMany(p => p.Sections)
            .Where(s => s.Status == UISectionStatus.Hybrid)
            .SelectMany(s => s.Patterns)
            .Take(5)
            .ToList();

    public int StructuralRiskScore
    {
        get
        {
            var raw = AllSections.Sum(s => s.Status switch
            {
                UISectionStatus.Hybrid            => 10,
                UISectionStatus.Legacy            => 5,
                UISectionStatus.PartiallyMigrated => 2,
                _                                 => 0
            });
            return Math.Min(100, raw);
        }
    }
}
