using System.IO.Compression;
using System.Net;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.DependencyReview;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.WasmPerformance;
using BirkNext.Api.Services.WasmSecurity;
using BirkNext.ApiReview;
using BirkNext.Applicability;
using BirkNext.Dependencies;
using BirkNext.Integrations;
using BirkNext.PipelineReview;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.SourceAnalysis;

/// <summary>
/// Every review-local score now goes through the shared ScoreSemantics: unsupported, not-applicable, unavailable and not-assessed checks never
/// lower quality and never become 0; nothing assessed is no score; coverage is reported separately; a provider failure is a coverage gap.
/// </summary>
public sealed class ReviewScoringMigrationTests
{
    // ── Shared matrix ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(new[] { CheckOutcome.Pass, CheckOutcome.Pass }, 100.0)]
    [InlineData(new[] { CheckOutcome.Fail, CheckOutcome.Fail }, 0.0)]
    [InlineData(new[] { CheckOutcome.Pass, CheckOutcome.Fail }, 50.0)]
    [InlineData(new[] { CheckOutcome.Pass, CheckOutcome.Warning }, 75.0)]
    [InlineData(new[] { CheckOutcome.NeedsReview }, 50.0)]
    [InlineData(new[] { CheckOutcome.Partial, CheckOutcome.Pass }, 75.0)]
    [InlineData(new[] { CheckOutcome.Pass, CheckOutcome.Unsupported, CheckOutcome.Unsupported }, 100.0)]
    [InlineData(new[] { CheckOutcome.Fail, CheckOutcome.Pass, CheckOutcome.Unsupported }, 50.0)]
    [InlineData(new[] { CheckOutcome.Pass, CheckOutcome.Unavailable, CheckOutcome.NotTested, CheckOutcome.NotAssessed }, 100.0)]
    [InlineData(new[] { CheckOutcome.Pass, CheckOutcome.Informational }, 100.0)]
    public void SharedMatrix_QualityAmongAssessed(CheckOutcome[] outcomes, double expected) =>
        ScoreSemantics.Compute(outcomes).QualityPercent.Should().Be(expected);

    [Theory]
    [InlineData(new[] { CheckOutcome.Unsupported })]
    [InlineData(new[] { CheckOutcome.NotApplicable })]
    [InlineData(new[] { CheckOutcome.NotAssessed, CheckOutcome.Unavailable })]
    [InlineData(new[] { CheckOutcome.Informational })]
    [InlineData(new CheckOutcome[0])]
    public void SharedMatrix_NothingJudged_HasNoScore(CheckOutcome[] outcomes) =>
        ScoreSemantics.Compute(outcomes).QualityPercent.Should().BeNull();

    [Fact]
    public void Coverage_IsSeparateFromQuality()
    {
        // 4 applicable checks, 2 executed (pass), 2 unavailable → 100 % quality, 50 % coverage — never 50 % quality.
        var q = ScoreSemantics.Compute([CheckOutcome.Pass, CheckOutcome.Pass, CheckOutcome.Unavailable, CheckOutcome.Unavailable]);
        q.QualityPercent.Should().Be(100);
        q.Coverage.AssessmentCoveragePercent.Should().Be(50);
        ScoreSemantics.Compute([CheckOutcome.Unsupported]).Coverage.ApplicableChecks.Should().Be(0);
    }

    [Fact]
    public void ProviderFailure_IsACoverageGap_NotAFailure()
    {
        var outcome = ScoreSemantics.FromExecution(ReviewExecutionState.FailedToExecute);
        outcome.Should().Be(CheckOutcome.Unavailable);
        ScoreSemantics.Compute([outcome]).QualityPercent.Should().BeNull();
        ScoreSemantics.Compute([CheckOutcome.Pass, outcome]).QualityPercent.Should().Be(100);
    }

    [Fact]
    public void Weighted_DenominatorHoldsOnlyAssessedWeight()
    {
        var unsupported = ScoreSemantics.ComputeWeighted([(CheckOutcome.Pass, 3), (CheckOutcome.Unsupported, 2)]);
        unsupported.QualityPercent.Should().Be(100, "never 60 %");
        unsupported.WeightedCoveragePercent.Should().Be(100, "an unsupported check is not applicable to coverage either");
        var notTested = ScoreSemantics.ComputeWeighted([(CheckOutcome.Pass, 3), (CheckOutcome.NotTested, 2)]);
        notTested.QualityPercent.Should().Be(100);
        notTested.WeightedCoveragePercent.Should().Be(60);
        ScoreSemantics.ComputeWeighted([(CheckOutcome.Fail, 1), (CheckOutcome.Pass, 3)]).QualityPercent.Should().Be(75);
    }

    [Fact]
    public void CategoryAverages_SkipUnassessedCategories()
    {
        ScoreSemantics.AverageAssessed([80, null, 60, null]).Should().Be(70);
        ScoreSemantics.AverageAssessed([null, null]).Should().BeNull("no assessed category is no score, not 0");
        ScoreSemantics.AverageAssessed([0]).Should().Be(0, "0 is a real score when an assessed category genuinely scored 0");
    }

    // ── API Quality Review ────────────────────────────────────────────────────────────────────────────────────────────

    private static ApiReviewCheck Check(ApiReviewFindingType area, ApiReviewCheckResult result) => new() { CheckId = $"{area}-{result}", Area = area, Result = result };

    [Fact]
    public void Aqr_BlockedAndNotTested_AreCoverage_NotQuality()
    {
        var report = new ApiReviewReport
        {
            Targets =
            [
                new ApiReviewTargetResult { Checks = [Check(ApiReviewFindingType.Rest, ApiReviewCheckResult.Pass), Check(ApiReviewFindingType.Security, ApiReviewCheckResult.Blocked),
                    Check(ApiReviewFindingType.Security, ApiReviewCheckResult.NotTested), Check(ApiReviewFindingType.GraphQl, ApiReviewCheckResult.NotApplicable)] },
            ],
        };
        report.Quality.QualityPercent.Should().Be(100);
        report.Quality.Coverage.AssessmentCoveragePercent.Should().BeApproximately(33.3, 0.1);
        report.QualityByArea[ApiReviewFindingType.Security].QualityPercent.Should().BeNull("a security area where nothing ran has no score, never 0");
        report.QualityByArea[ApiReviewFindingType.GraphQl].QualityPercent.Should().BeNull();
        report.QualityByArea.Should().NotContainKey(ApiReviewFindingType.Performance, "an area without checks has no entry at all");
        new ApiReviewReport().Quality.QualityPercent.Should().BeNull();
        ApiReviewScoring.Outcome(ApiReviewCheckResult.ManualReview).Should().Be(CheckOutcome.NeedsReview);
    }

    [Fact]
    public void AqrLegacy_UnassessedCategories_AreNull_NotZero()
    {
        var none = ApiQualityReviewService.ComputeCategoryScores([]);
        none.Should().BeEmpty();
        ApiQualityReviewService.ComputeOverallScore(none).Should().BeNull("nothing assessed is no score");
        foreach (var category in Enum.GetValues<ApiQualityCategory>())
            ApiQualityReviewService.Score(none, category).Should().BeNull($"{category} was not assessed");

        var security = ApiQualityReviewService.ComputeCategoryScores([new ApiQualityFinding { Category = ApiQualityCategory.Security, Severity = ApiQualitySeverity.High }]);
        ApiQualityReviewService.Score(security, ApiQualityCategory.Security).Should().Be(85);
        ApiQualityReviewService.Score(security, ApiQualityCategory.GraphQL).Should().BeNull("no GraphQL was assessed");
        ApiQualityReviewService.ComputeOverallScore(security).Should().Be(85, "unassessed categories are not averaged in as 0");
        new ApiQualityReviewReport().ScoringModelVersion.Should().Be(2);
    }

    // ── WASM performance readiness / WASM security (dashboard inputs) ──────────────────────────────────────────────────

    [Fact]
    public void WasmPerformance_UnassessedCategory_HasNoScore_AndNothingAssessedIsNoData()
    {
        var notAssessed = WasmPerformanceReadinessService.BuildCategory("GraphQL", BirkNext.Api.Services.WasmPerformance.PerformanceCategory.ApiCalls, [], wasAssessed: false);
        notAssessed.Score.Should().BeNull("previously a default 100");
        notAssessed.State.Should().Be(ReadinessState.NotAssessed);
        WasmPerformanceReadinessService.CalculateOverallScore([notAssessed]).Should().BeNull("previously 0 with HasData = true, which the dashboard averaged in");
        var assessed = WasmPerformanceReadinessService.BuildCategory("Startup", BirkNext.Api.Services.WasmPerformance.PerformanceCategory.Startup, [], wasAssessed: true);
        WasmPerformanceReadinessService.CalculateOverallScore([notAssessed, assessed]).Should().Be(100);
    }

    [Fact]
    public async Task WasmSecurity_UnreachableTarget_HasNoScore_NotAHundred()
    {
        var service = new BlazorWasmSecurityReviewService(new HttpClient(new Unreachable()), NullLogger<BlazorWasmSecurityReviewService>.Instance);
        var report = await service.ScanAsync(new WasmScanRequest { TargetUrl = "https://unreachable.example.test/" }, CancellationToken.None);
        report.Health.Score.Should().BeNull("no check ran");
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("unreachable");
    }

    // ── Integration Quality Review ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Iqr_ConfigurationReadinessIsNotQuality_AndObservationsAreNotPasses()
    {
        IntegrationCheck C(IntegrationCheckStatus status, IntegrationEvidenceSource source) => new() { CheckId = $"{status}-{source}", Status = status, Provenance = source };
        var result = new IntegrationReviewResult
        {
            Systems =
            [
                new IntegrationSystemResult
                {
                    SystemName = "Payments Kafka", Kind = IntegrationKind.Other,
                    PlatformChecks = [C(IntegrationCheckStatus.Fail, IntegrationEvidenceSource.Configuration), C(IntegrationCheckStatus.Observed, IntegrationEvidenceSource.AzureMetadata),
                        C(IntegrationCheckStatus.Unavailable, IntegrationEvidenceSource.ApplicationInsights), C(IntegrationCheckStatus.Pass, IntegrationEvidenceSource.ContractArtifact)],
                },
            ],
        };
        result.Quality.QualityPercent.Should().Be(100, "the configuration gap is readiness; Observed is informational; Unavailable is coverage");
        result.ConfigurationReadiness.QualityPercent.Should().Be(0);
        result.RuntimeSupport.Single().Level.Should().Be(BirkNext.Technology.SupportLevel.Unsupported);
        IntegrationReviewScoring.Outcome(IntegrationCheckStatus.Observed).Should().Be(CheckOutcome.Informational, "Observed ≠ Pass");
        IntegrationReviewScoring.Outcome(IntegrationCheckStatus.NotConfigured).Should().Be(CheckOutcome.NotAssessed);
    }

    // ── Dependency Review ─────────────────────────────────────────────────────────────────────────────────────────────

    private static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files) { using var w = new StreamWriter(zip.CreateEntry(path).Open()); w.Write(content); }
        return buffer.ToArray();
    }

    [Fact]
    public void Dependency_MavenWithoutReader_IsNotAssessed_NeverMissing()
    {
        // Maven + npm only: nothing BirkNext reads natively.
        var bytes = Zip(("Svc/pom.xml", "<project><artifactId>svc</artifactId></project>"), ("Svc/src/main/java/App.java", "class App {}"),
            ("Svc/web/package.json", "{\"dependencies\":{\"react\":\"18.3.1\"}}"));
        var (evidence, error) = SourceDependencyEvidenceExtractor.Extract("Svc", bytes, "Svc.zip");
        error.Should().BeNull();
        evidence!.UnsupportedManifests.Should().Contain(m => m.StartsWith("Maven / Gradle: ") && m.EndsWith("pom.xml")).And.Contain(m => m.StartsWith("npm: "));
        var review = DependencyReviewBuilder.Build(SourceDependencyEvidenceExtractor.Input("Svc", "sha", evidence), null);
        review.UnsupportedManifests.Should().NotBeEmpty();
        var coverage = DependencyReviewService.Categories([review]).Single(c => c.Name == "Coverage");
        coverage.State.Should().Be(ReviewCategoryState.NotAssessed, "a Maven project BirkNext cannot read is a coverage gap, not missing coverage");
        coverage.Detail.Should().Contain("Maven / Gradle").And.Contain("tool limitation").And.Contain("SBOM");
        review.Findings.Should().NotContain(f => f.Title.Contains("Maven", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dependency_PartlyReadableRepository_NamesWhatWasNotReviewed()
    {
        // PaymentHub: the Dockerfile image is read (and has no Renovate coverage — a genuine observation); Maven/npm are named as not reviewed.
        var (evidence, _) = SourceDependencyEvidenceExtractor.Extract("PaymentHub", TechnologyIndependenceTests.PaymentHub(), "PaymentHub.zip");
        var review = DependencyReviewBuilder.Build(SourceDependencyEvidenceExtractor.Input("PaymentHub", "sha", evidence!), null);
        review.Dependencies.Should().OnlyContain(d => d.Manager == "dockerfile");
        DependencyReviewService.Categories([review]).Single(c => c.Name == "Coverage").Detail.Should().Contain("Not reviewed: Maven / Gradle, npm manifests").And.Contain("tool limitation");
    }

    [Fact]
    public void Dependency_NuGetRepository_StillAssessesCoverage()
    {
        var bytes = Zip(("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Serilog\" Version=\"3.1.1\" /></ItemGroup></Project>"));
        var (evidence, _) = SourceDependencyEvidenceExtractor.Extract("App", bytes, "App.zip");
        evidence!.UnsupportedManifests.Should().BeEmpty();
        var review = DependencyReviewBuilder.Build(SourceDependencyEvidenceExtractor.Input("App", "sha", evidence), null);
        DependencyReviewService.Categories([review]).Single(c => c.Name == "Coverage").State.Should().Be(ReviewCategoryState.Missing, "a real NuGet repository without Renovate genuinely lacks coverage");
    }

    // ── Pipeline Review ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pipeline_ParserLimitations_AreAssessmentGaps_NotDefects()
    {
        new PipelineReviewFinding { Category = PipelineFindingCategory.UnresolvedFlow }.IsAssessmentGap.Should().BeTrue();
        new PipelineReviewFinding { Category = PipelineFindingCategory.TemplateResolutionGap }.IsAssessmentGap.Should().BeTrue();
        new PipelineReviewFinding { Category = PipelineFindingCategory.TestGatingGap }.IsAssessmentGap.Should().BeFalse("a detected missing test gate is a quality finding");
    }

    // ── Generic fixtures through Source Analysis: no false zeros ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("PaymentHub")]
    [InlineData("LegacyClaims")]
    [InlineData("DataLakeIngestion")]
    [InlineData("UnknownTech")]
    public async Task Fixtures_GetNoFalseQualityPenaltyFromToolGaps(string fixture)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var bytes = fixture switch
        {
            "PaymentHub" => TechnologyIndependenceTests.PaymentHub(),
            "LegacyClaims" => TechnologyIndependenceTests.LegacyClaims(),
            "DataLakeIngestion" => TechnologyIndependenceTests.DataLakeIngestion(),
            _ => TechnologyIndependenceTests.UnknownTech(),
        };
        var (snapshot, error) = await new IqrSourceStore(db).AnalyzeAsync("env", IqrSourceStore.SourceAnalysisOwner, fixture + ".zip", bytes);
        error.Should().BeNull();

        // Dependency Review over the snapshot's evidence: no "Missing" category merely because the ecosystem is unsupported.
        var review = DependencyReviewBuilder.Build(SourceDependencyEvidenceExtractor.Input(fixture, snapshot!.Archive.Sha256, snapshot.DependencyEvidence!), null);
        var coverage = DependencyReviewService.Categories([review]).Single(c => c.Name == "Coverage");
        if (review.Dependencies.Count == 0) coverage.State.Should().Be(ReviewCategoryState.NotAssessed);

        // Applicability: no review is "failed"; reviews that cannot run are excluded from every quality aggregate.
        var input = new BirkNext.Technology.ProjectApplicabilityInput
        {
            HasSourceSnapshot = true, Technologies = snapshot.TechnologyCoverage!.Technologies, Capabilities = snapshot.TechnologyCoverage.Capabilities,
        };
        var applicability = BirkNext.Technology.ApplicabilityEvaluator.EvaluateAll(input);
        foreach (var review2 in new[] { "frontend-quality-review", "api-quality-review", "azure-environment" })
            ScoreSemantics.ExcludedFromQuality(applicability[review2].Status).Should().BeTrue($"{fixture}: {review2} has no target or provider and must not contribute a score");
        if (fixture == "DataLakeIngestion")
        {
            applicability["frontend-quality-review"].Status.Should().Be(ApplicabilityStatus.NotApplicable);
            applicability["api-quality-review"].Status.Should().Be(ApplicabilityStatus.NotApplicable);
        }
    }
}
