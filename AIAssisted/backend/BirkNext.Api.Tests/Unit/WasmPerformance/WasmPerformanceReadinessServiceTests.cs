using BirkNext.Api.Services.WasmPerformance;
using FluentAssertions;

namespace BirkNext.Api.Tests.Unit.WasmPerformance;

public class WasmPerformanceReadinessServiceTests
{
    private static PerformanceFinding Finding(
        string id,
        PerformanceSeverity severity,
        PerformanceCategory category = PerformanceCategory.Startup,
        string title = "Test Finding") =>
        new() { Id = id, Severity = severity, Category = category, Title = title, Description = "", Recommendation = "" };

    private static PerformanceRecommendation Rec(int priority, string title) =>
        new() { Priority = priority, Title = title, Description = "", Category = PerformanceCategory.Startup };

    private static WasmAssetDiscoveryResult EmptyResult() =>
        new() { TargetUrl = "https://example.com" };

    private static WasmAssetDiscoveryResult BasicResult(
        StartupMetrics? startupMetrics = null,
        List<PerformanceFinding>? findings = null,
        List<PerformanceRecommendation>? recommendations = null,
        ApiAnalysisResult? apiAnalysis = null,
        CachingAnalysisResult? cachingAnalysis = null) =>
        new()
        {
            TargetUrl       = "https://example.com",
            Assets          = [new DiscoveredAsset { Url = "https://example.com/blazor.boot.json", StatusCode = 200 }],
            StartupMetrics  = startupMetrics ?? new StartupMetrics(),
            Findings        = findings        ?? [],
            Recommendations = recommendations ?? [],
            ApiAnalysis     = apiAnalysis,
            CachingAnalysis = cachingAnalysis
        };

    // ── CalculateCategoryScore ────────────────────────────────────────────────

    [Fact]
    public void CalculateCategoryScore_NoFindings_Returns100()
    {
        var score = WasmPerformanceReadinessService.CalculateCategoryScore([]);
        score.Should().Be(100);
    }

    [Fact]
    public void CalculateCategoryScore_OneLowFinding_Deducts4()
    {
        var findings = new[] { Finding("X-001", PerformanceSeverity.Low) };
        var score = WasmPerformanceReadinessService.CalculateCategoryScore(findings);
        score.Should().Be(96);
    }

    [Fact]
    public void CalculateCategoryScore_OneMediumFinding_Deducts12()
    {
        var findings = new[] { Finding("X-001", PerformanceSeverity.Medium) };
        var score = WasmPerformanceReadinessService.CalculateCategoryScore(findings);
        score.Should().Be(88);
    }

    [Fact]
    public void CalculateCategoryScore_OneHighFinding_Deducts25()
    {
        var findings = new[] { Finding("X-001", PerformanceSeverity.High) };
        var score = WasmPerformanceReadinessService.CalculateCategoryScore(findings);
        score.Should().Be(75);
    }

    [Fact]
    public void CalculateCategoryScore_OneCriticalFinding_Deducts35()
    {
        var findings = new[] { Finding("X-001", PerformanceSeverity.Critical) };
        var score = WasmPerformanceReadinessService.CalculateCategoryScore(findings);
        score.Should().Be(65);
    }

    [Fact]
    public void CalculateCategoryScore_InfoFinding_DeductsNothing()
    {
        var findings = new[] { Finding("X-001", PerformanceSeverity.Info) };
        var score = WasmPerformanceReadinessService.CalculateCategoryScore(findings);
        score.Should().Be(100);
    }

    [Fact]
    public void CalculateCategoryScore_ManyHighFindings_ClampsToZero()
    {
        var findings = Enumerable.Range(1, 10)
            .Select(i => Finding($"X-{i:00}", PerformanceSeverity.High))
            .ToList();
        var score = WasmPerformanceReadinessService.CalculateCategoryScore(findings);
        score.Should().Be(0);
    }

    [Fact]
    public void CalculateCategoryScore_MixedFindings_SumsDeductions()
    {
        // Critical(-35) + High(-25) + Medium(-12) + Low(-4) = -76 → score 24
        var findings = new[]
        {
            Finding("X-001", PerformanceSeverity.Critical),
            Finding("X-002", PerformanceSeverity.High),
            Finding("X-003", PerformanceSeverity.Medium),
            Finding("X-004", PerformanceSeverity.Low)
        };
        var score = WasmPerformanceReadinessService.CalculateCategoryScore(findings);
        score.Should().Be(24);
    }

    // ── DetermineState ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(100, ReadinessState.Ready)]
    [InlineData(80,  ReadinessState.Ready)]
    [InlineData(79,  ReadinessState.MostlyReady)]
    [InlineData(60,  ReadinessState.MostlyReady)]
    [InlineData(59,  ReadinessState.NeedsImprovement)]
    [InlineData(40,  ReadinessState.NeedsImprovement)]
    [InlineData(39,  ReadinessState.HighRisk)]
    [InlineData(0,   ReadinessState.HighRisk)]
    public void DetermineState_ScoreRanges_MapsToCorrectState(int score, ReadinessState expected)
    {
        WasmPerformanceReadinessService.DetermineState(score).Should().Be(expected);
    }

    // ── CalculateOverallScore ─────────────────────────────────────────────────

    [Fact]
    public void CalculateOverallScore_NoAssessedCategories_ReturnsZero()
    {
        var cats = new List<PerformanceCategorySummary>
        {
            new() { CategoryName = "A", WasAssessed = false, Score = 100, State = ReadinessState.NotAssessed }
        };
        WasmPerformanceReadinessService.CalculateOverallScore(cats).Should().Be(0);
    }

    [Fact]
    public void CalculateOverallScore_SingleAssessedCategory_ReturnsItsScore()
    {
        var cats = new List<PerformanceCategorySummary>
        {
            new() { CategoryName = "Startup", WasAssessed = true, Score = 75, State = ReadinessState.MostlyReady }
        };
        WasmPerformanceReadinessService.CalculateOverallScore(cats).Should().Be(75);
    }

    [Fact]
    public void CalculateOverallScore_SkipsNotAssessedCategories()
    {
        var cats = new List<PerformanceCategorySummary>
        {
            new() { CategoryName = "Startup",      WasAssessed = true,  Score = 80 },
            new() { CategoryName = "Architecture",  WasAssessed = false, Score = 100 },
            new() { CategoryName = "Caching",       WasAssessed = true,  Score = 60 }
        };
        // Average of 80 and 60 = 70
        WasmPerformanceReadinessService.CalculateOverallScore(cats).Should().Be(70);
    }

    [Fact]
    public void CalculateOverallScore_MultipleCategories_RoundsToNearestInt()
    {
        var cats = new List<PerformanceCategorySummary>
        {
            new() { CategoryName = "A", WasAssessed = true, Score = 75 },
            new() { CategoryName = "B", WasAssessed = true, Score = 63 },
            new() { CategoryName = "C", WasAssessed = true, Score = 88 }
        };
        // Average = (75 + 63 + 88) / 3 = 75.33 → rounds to 75
        WasmPerformanceReadinessService.CalculateOverallScore(cats).Should().Be(75);
    }

    // ── SelectTopRisks ────────────────────────────────────────────────────────

    [Fact]
    public void SelectTopRisks_ExcludesInfoFindings()
    {
        var findings = new[]
        {
            Finding("X-001", PerformanceSeverity.Info),
            Finding("X-002", PerformanceSeverity.Low),
            Finding("X-003", PerformanceSeverity.Medium)
        };
        var risks = WasmPerformanceReadinessService.SelectTopRisks(findings, 5).ToList();
        risks.Should().NotContain(f => f.Id == "X-001");
        risks.Should().HaveCount(2);
    }

    [Fact]
    public void SelectTopRisks_OrdersBySeverityAscending()
    {
        var findings = new[]
        {
            Finding("LOW-001",  PerformanceSeverity.Low),
            Finding("HIGH-001", PerformanceSeverity.High),
            Finding("MED-001",  PerformanceSeverity.Medium)
        };
        var risks = WasmPerformanceReadinessService.SelectTopRisks(findings, 5).ToList();
        risks[0].Id.Should().Be("HIGH-001");
        risks[1].Id.Should().Be("MED-001");
        risks[2].Id.Should().Be("LOW-001");
    }

    [Fact]
    public void SelectTopRisks_LimitsToRequestedCount()
    {
        var findings = Enumerable.Range(1, 10)
            .Select(i => Finding($"X-{i:00}", PerformanceSeverity.Medium))
            .ToList();
        var risks = WasmPerformanceReadinessService.SelectTopRisks(findings, 5).ToList();
        risks.Should().HaveCount(5);
    }

    // ── SelectTopRecommendations ──────────────────────────────────────────────

    [Fact]
    public void SelectTopRecommendations_OrdersByPriority()
    {
        var recs = new[]
        {
            Rec(3, "Third"),
            Rec(1, "First"),
            Rec(2, "Second")
        };
        var top = WasmPerformanceReadinessService.SelectTopRecommendations(recs, 5).ToList();
        top[0].Title.Should().Be("First");
        top[1].Title.Should().Be("Second");
        top[2].Title.Should().Be("Third");
    }

    [Fact]
    public void SelectTopRecommendations_DeduplicatesByTitle()
    {
        var recs = new[]
        {
            Rec(1, "Enable Brotli"),
            Rec(2, "Enable Brotli"),
            Rec(3, "Add caching")
        };
        var top = WasmPerformanceReadinessService.SelectTopRecommendations(recs, 5).ToList();
        top.Should().HaveCount(2);
        top.Should().ContainSingle(r => r.Title == "Enable Brotli");
    }

    [Fact]
    public void SelectTopRecommendations_LimitsToRequestedCount()
    {
        var recs = Enumerable.Range(1, 10)
            .Select(i => Rec(i, $"Rec {i}"))
            .ToList();
        var top = WasmPerformanceReadinessService.SelectTopRecommendations(recs, 5).ToList();
        top.Should().HaveCount(5);
    }

    // ── GenerateReport ────────────────────────────────────────────────────────

    [Fact]
    public void GenerateReport_NoData_HasDataFalse()
    {
        var svc    = new WasmPerformanceReadinessService();
        var report = svc.GenerateReport(EmptyResult());
        report.HasData.Should().BeFalse();
        report.Categories.Should().BeEmpty();
        report.TopRisks.Should().BeEmpty();
    }

    [Fact]
    public void GenerateReport_WithStartupData_HasDataTrue()
    {
        var svc    = new WasmPerformanceReadinessService();
        var report = svc.GenerateReport(BasicResult());
        report.HasData.Should().BeTrue();
        report.Categories.Should().HaveCount(7);
    }

    [Fact]
    public void GenerateReport_NoFindings_AllAssessedCategoriesScoreIs100()
    {
        var svc    = new WasmPerformanceReadinessService();
        var report = svc.GenerateReport(BasicResult(
            cachingAnalysis: new CachingAnalysisResult()));
        var assessedCats = report.Categories.Where(c => c.WasAssessed);
        assessedCats.Should().OnlyContain(c => c.Score == 100);
    }

    [Fact]
    public void GenerateReport_ArchitectureAlwaysNotAssessed()
    {
        var svc    = new WasmPerformanceReadinessService();
        var report = svc.GenerateReport(BasicResult());
        var arch   = report.Categories.First(c => c.CategoryName == "Architecture");
        arch.WasAssessed.Should().BeFalse();
        arch.State.Should().Be(ReadinessState.NotAssessed);
    }

    [Fact]
    public void GenerateReport_GraphQLNotPresent_GraphQLNotAssessed()
    {
        var svc     = new WasmPerformanceReadinessService();
        var apiRes  = new ApiAnalysisResult { HasGraphQL = false };
        var report  = svc.GenerateReport(BasicResult(apiAnalysis: apiRes));
        var gqlCat  = report.Categories.First(c => c.CategoryName == "GraphQL");
        gqlCat.WasAssessed.Should().BeFalse();
        gqlCat.State.Should().Be(ReadinessState.NotAssessed);
    }

    [Fact]
    public void GenerateReport_GraphQLPresent_GraphQLAssessed()
    {
        var svc     = new WasmPerformanceReadinessService();
        var apiRes  = new ApiAnalysisResult { HasGraphQL = true };
        var report  = svc.GenerateReport(BasicResult(apiAnalysis: apiRes));
        var gqlCat  = report.Categories.First(c => c.CategoryName == "GraphQL");
        gqlCat.WasAssessed.Should().BeTrue();
    }

    [Fact]
    public void GenerateReport_StartupHighFinding_ReducesStartupScore()
    {
        var svc = new WasmPerformanceReadinessService();
        var findings = new List<PerformanceFinding>
        {
            Finding("STA-001", PerformanceSeverity.High, PerformanceCategory.Startup)
        };
        var report = svc.GenerateReport(BasicResult(findings: findings));
        var startup = report.Categories.First(c => c.CategoryName == "Startup");
        startup.Score.Should().Be(75); // 100 - 25
        startup.State.Should().Be(ReadinessState.MostlyReady);
    }

    [Fact]
    public void GenerateReport_ApiFindings_PartitionedByPrefix()
    {
        var svc = new WasmPerformanceReadinessService();
        var apiFindings = new List<PerformanceFinding>
        {
            Finding("API-G001", PerformanceSeverity.Medium, PerformanceCategory.ApiCalls),
            Finding("API-R001", PerformanceSeverity.Low,    PerformanceCategory.ApiCalls)
        };
        var apiRes = new ApiAnalysisResult { HasGraphQL = true, Findings = apiFindings };
        var report = svc.GenerateReport(BasicResult(apiAnalysis: apiRes));

        var gqlCat  = report.Categories.First(c => c.CategoryName == "GraphQL");
        var apiCat  = report.Categories.First(c => c.CategoryName == "API");

        gqlCat.FindingsCount.Should().Be(1);
        gqlCat.Score.Should().Be(88); // 100 - 12

        apiCat.FindingsCount.Should().Be(1);
        apiCat.Score.Should().Be(96); // 100 - 4
    }

    [Fact]
    public void GenerateReport_TopRisks_ExcludesInfoExcludesArchitecture()
    {
        var svc = new WasmPerformanceReadinessService();
        var findings = new List<PerformanceFinding>
        {
            Finding("STA-001", PerformanceSeverity.High,   PerformanceCategory.Startup, "High finding"),
            Finding("STA-002", PerformanceSeverity.Info,   PerformanceCategory.Startup, "Info finding"),
            Finding("STA-003", PerformanceSeverity.Medium, PerformanceCategory.Startup, "Medium finding")
        };
        var report = svc.GenerateReport(BasicResult(findings: findings));
        report.TopRisks.Should().HaveCount(2);
        report.TopRisks.Should().NotContain(f => f.Severity == PerformanceSeverity.Info);
        report.TopRisks[0].Severity.Should().Be(PerformanceSeverity.High);
    }

    [Fact]
    public void GenerateReport_Health_SummaryMatchesCategoryScores()
    {
        var svc     = new WasmPerformanceReadinessService();
        var report  = svc.GenerateReport(BasicResult(
            cachingAnalysis: new CachingAnalysisResult()));

        report.Health.OverallScore.Should().Be(report.OverallScore);
        report.Health.StartupScore.Should().Be(report.Categories.First(c => c.CategoryName == "Startup").Score);
        report.Health.CachingScore.Should().Be(report.Categories.First(c => c.CategoryName == "Caching").Score);
    }
}
