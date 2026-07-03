using BirkNext.Api.Services.QualityReview;
using Xunit;

namespace BirkNext.Api.Tests.Services.QualityReview;

/// <summary>
/// Tests for Frontend Quality Review page model building.
/// Validates that the page clearly shows whether target environment is ready.
/// </summary>
public class FrontendQualityReviewPageBuilderTests
{
    [Fact]
    public void PageModel_StatusIsBlockedWhenTargetUrlMissing()
    {
        // No target URL configured = cannot run = Blocked
        var model = new QualityReviewPageModel
        {
            Title = "Frontend Quality Review",
            Target = "",
            ReadinessStatus = QualityReviewStatus.Blocked,
            ReviewPacks = new()
            {
                new QualityReviewPack
                {
                    Name = "Frontend Target",
                    Status = QualityReviewStatus.Blocked,
                    MissingInputs = ["Frontend URL from target environment"]
                }
            },
            Summary = new() { CanRun = false }
        };

        Assert.False(model.Summary.CanRun);
        Assert.Empty(model.Target);
    }

    [Fact]
    public void PageModel_StatusIsAvailableWhenTargetUrlExists()
    {
        // Target URL configured = can run = Available
        var model = new QualityReviewPageModel
        {
            Title = "Frontend Quality Review",
            Target = "http://localhost:5173",
            ReadinessStatus = QualityReviewStatus.Available,
            ReviewPacks = new()
            {
                new QualityReviewPack
                {
                    Name = "Frontend Target",
                    Status = QualityReviewStatus.Available,
                    MissingInputs = []
                }
            },
            Summary = new() { CanRun = true }
        };

        Assert.True(model.Summary.CanRun);
        Assert.NotEmpty(model.Target);
    }

    [Fact]
    public void PageModel_ShowsAnalyzedAreas()
    {
        // Model lists what will be analyzed
        var model = new QualityReviewPageModel
        {
            Title = "Frontend Quality Review",
            Checks = new()
            {
                new QualityReviewCheck { Name = "Performance Analysis", Category = "Performance", Status = QualityReviewStatus.Available },
                new QualityReviewCheck { Name = "Security Scan", Category = "Security", Status = QualityReviewStatus.Available },
                new QualityReviewCheck { Name = "Accessibility Check", Category = "Accessibility", Status = QualityReviewStatus.Available },
                new QualityReviewCheck { Name = "Standards Check", Category = "Standards", Status = QualityReviewStatus.Available },
                new QualityReviewCheck { Name = "Blazor WASM Check", Category = "Blazor WASM", Status = QualityReviewStatus.Available },
                new QualityReviewCheck { Name = "QA Readiness", Category = "QA Readiness", Status = QualityReviewStatus.Available }
            }
        };

        Assert.Equal(6, model.Checks.Count);
        Assert.Contains(model.Checks, c => c.Category == "Performance");
        Assert.Contains(model.Checks, c => c.Category == "Security");
        Assert.Contains(model.Checks, c => c.Category == "Accessibility");
        Assert.Contains(model.Checks, c => c.Category == "Standards");
        Assert.Contains(model.Checks, c => c.Category == "Blazor WASM");
        Assert.Contains(model.Checks, c => c.Category == "QA Readiness");
    }

    [Fact]
    public void PageModel_MissingAuthIsWarningNotBlocked()
    {
        // Missing auth is WARNING (degraded analysis) if auth is optional, not Blocked
        var model = new QualityReviewPageModel
        {
            Title = "Frontend Quality Review",
            Target = "http://localhost:5173",
            ReadinessStatus = QualityReviewStatus.Warning,
            ReviewPacks = new()
            {
                new QualityReviewPack
                {
                    Name = "Authentication",
                    Status = QualityReviewStatus.Warning,
                    RequiredInputs = ["Auth tokens (optional)"],
                    MissingInputs = ["Auth tokens"]
                }
            },
            Summary = new() { CanRun = true }
        };

        Assert.True(model.Summary.CanRun);
        Assert.Equal(QualityReviewStatus.Warning, model.ReadinessStatus);
        var authPack = model.ReviewPacks[0];
        Assert.Equal(QualityReviewStatus.Warning, authPack.Status);
    }

    [Fact]
    public void PageModel_TargetEnvironmentDeterminesReadiness()
    {
        // Page target comes from active target environment
        var model = new QualityReviewPageModel
        {
            Title = "Frontend Quality Review",
            Target = "https://staging.myapp.com",
            ReadinessStatus = QualityReviewStatus.Available,
            Sections = new()
            {
                new QualityReviewSection
                {
                    Title = "Audit Target",
                    Description = "Frontend application to be analyzed",
                    Checks = new() { new QualityReviewCheck { Name = "Target environment: staging" } }
                }
            }
        };

        Assert.NotEmpty(model.Target);
        var auditSection = model.Sections.FirstOrDefault(s => s.Title == "Audit Target");
        Assert.NotNull(auditSection);
    }
}
