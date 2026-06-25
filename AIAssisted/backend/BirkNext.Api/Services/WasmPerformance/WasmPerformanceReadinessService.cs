namespace BirkNext.Api.Services.WasmPerformance;

public sealed class WasmPerformanceReadinessService : IWasmPerformanceReadinessService
{
    public PerformanceReadinessReport GenerateReport(WasmAssetDiscoveryResult result)
    {
        bool hasData = result.Assets.Count > 0 || result.StartupMetrics is not null;
        if (!hasData)
            return new PerformanceReadinessReport { HasData = false };

        // Partition startup findings
        var startupFindings = result.Findings
            .Where(f => f.Category == PerformanceCategory.Startup)
            .ToList();

        // Asset-specific startup findings (individual asset sizes)
        var assetFindings = startupFindings
            .Where(f => f.Id == "STA-003" || f.Id == "STA-004" || f.Id == "STA-008")
            .ToList();

        // API phase findings split by REST vs GraphQL prefix
        var apiFindingsAll  = result.ApiAnalysis?.Findings ?? [];
        var restFindings    = apiFindingsAll
            .Where(f => f.Id.StartsWith("API-R", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var graphqlFindings = apiFindingsAll
            .Where(f => f.Id.StartsWith("API-G", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Caching phase findings split by category
        var cachingPhaseFx  = result.CachingAnalysis?.Findings ?? [];
        var cachingFindings = cachingPhaseFx
            .Where(f => f.Category == PerformanceCategory.Caching)
            .ToList();
        var compressionFindings = cachingPhaseFx
            .Where(f => f.Category == PerformanceCategory.Compression)
            .ToList();

        // Architecture findings (Phase 6 — not yet implemented, always empty)
        var archFindings = result.Findings
            .Where(f => f.Category == PerformanceCategory.BlazorRuntime
                     || f.Category == PerformanceCategory.Configuration)
            .ToList();

        bool startupAssessed  = result.StartupMetrics is not null;
        bool apiAssessed      = result.ApiAnalysis is not null;
        bool graphqlAssessed  = result.ApiAnalysis?.HasGraphQL == true;
        bool cachingAssessed  = result.CachingAnalysis is not null;
        bool archAssessed     = false; // Phase 6 not implemented

        var categories = new List<PerformanceCategorySummary>
        {
            BuildCategory("Startup",      PerformanceCategory.Startup,       startupFindings,      startupAssessed),
            BuildCategory("API",          PerformanceCategory.ApiCalls,       restFindings,         apiAssessed),
            BuildCategory("GraphQL",      PerformanceCategory.ApiCalls,       graphqlFindings,      graphqlAssessed),
            BuildCategory("Caching",      PerformanceCategory.Caching,        cachingFindings,      cachingAssessed),
            BuildCategory("Compression",  PerformanceCategory.Compression,    compressionFindings,  cachingAssessed),
            BuildCategory("Architecture", PerformanceCategory.BlazorRuntime,  archFindings,         archAssessed),
            BuildCategory("Assets",       PerformanceCategory.Assets,         assetFindings,        startupAssessed)
        };

        // All findings across all assessed phases
        var allFindings = startupFindings
            .Concat(apiFindingsAll)
            .Concat(cachingPhaseFx)
            .ToList();

        // All recommendations across all phases, merged and priority-ordered
        var allRecs = result.Recommendations
            .Concat(result.ApiAnalysis?.Recommendations ?? [])
            .Concat(result.CachingAnalysis?.Recommendations ?? [])
            .OrderBy(r => r.Priority)
            .ToList();

        var overallScore = CalculateOverallScore(categories);
        var overallState = DetermineState(overallScore);

        int CatScore(string name) => categories.First(c => c.CategoryName == name).Score;

        var health = new PerformanceReadinessHealth
        {
            OverallScore      = overallScore,
            StartupScore      = CatScore("Startup"),
            ApiScore          = CatScore("API"),
            GraphQlScore      = CatScore("GraphQL"),
            CachingScore      = CatScore("Caching"),
            CompressionScore  = CatScore("Compression"),
            ArchitectureScore = CatScore("Architecture"),
            CriticalFindings  = allFindings.Count(f => f.Severity == PerformanceSeverity.Critical),
            HighFindings      = allFindings.Count(f => f.Severity == PerformanceSeverity.High),
            MediumFindings    = allFindings.Count(f => f.Severity == PerformanceSeverity.Medium),
            LowFindings       = allFindings.Count(f => f.Severity == PerformanceSeverity.Low)
        };

        return new PerformanceReadinessReport
        {
            OverallScore       = overallScore,
            OverallState       = overallState,
            Categories         = categories,
            TopRisks           = SelectTopRisks(allFindings, 5).ToList(),
            TopRecommendations = SelectTopRecommendations(allRecs, 5).ToList(),
            Health             = health,
            HasData            = true
        };
    }

    // ── Pure computation helpers (internal static for testability) ──────────────

    internal static PerformanceCategorySummary BuildCategory(
        string name,
        PerformanceCategory category,
        IReadOnlyList<PerformanceFinding> findings,
        bool wasAssessed)
    {
        var score = wasAssessed ? CalculateCategoryScore(findings) : 100;
        return new PerformanceCategorySummary
        {
            CategoryName  = name,
            Category      = category,
            Score         = score,
            State         = wasAssessed ? DetermineState(score) : ReadinessState.NotAssessed,
            FindingsCount = findings.Count,
            CriticalCount = findings.Count(f => f.Severity == PerformanceSeverity.Critical),
            HighCount     = findings.Count(f => f.Severity == PerformanceSeverity.High),
            MediumCount   = findings.Count(f => f.Severity == PerformanceSeverity.Medium),
            LowCount      = findings.Count(f => f.Severity == PerformanceSeverity.Low),
            WasAssessed   = wasAssessed
        };
    }

    internal static int CalculateCategoryScore(IReadOnlyList<PerformanceFinding> findings)
    {
        if (findings.Count == 0) return 100;
        var deduction = findings.Sum(f => f.Severity switch
        {
            PerformanceSeverity.Critical => 35,
            PerformanceSeverity.High     => 25,
            PerformanceSeverity.Medium   => 12,
            PerformanceSeverity.Low      => 4,
            _                            => 0
        });
        return Math.Max(0, 100 - deduction);
    }

    internal static ReadinessState DetermineState(int score) => score switch
    {
        >= 80 => ReadinessState.Ready,
        >= 60 => ReadinessState.MostlyReady,
        >= 40 => ReadinessState.NeedsImprovement,
        _     => ReadinessState.HighRisk
    };

    internal static int CalculateOverallScore(IReadOnlyList<PerformanceCategorySummary> categories)
    {
        var assessed = categories.Where(c => c.WasAssessed).ToList();
        if (assessed.Count == 0) return 0;
        return (int)Math.Round(assessed.Average(c => c.Score));
    }

    internal static IEnumerable<PerformanceFinding> SelectTopRisks(
        IReadOnlyList<PerformanceFinding> allFindings, int count)
    {
        return allFindings
            .Where(f => f.Severity != PerformanceSeverity.Info)
            .OrderBy(f => (int)f.Severity)
            .Take(count);
    }

    internal static IEnumerable<PerformanceRecommendation> SelectTopRecommendations(
        IReadOnlyList<PerformanceRecommendation> allRecs, int count)
    {
        return allRecs
            .OrderBy(r => r.Priority)
            .DistinctBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .Take(count);
    }
}
