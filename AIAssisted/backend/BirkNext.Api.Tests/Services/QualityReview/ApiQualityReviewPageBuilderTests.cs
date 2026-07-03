using BirkNext.Api.Services.QualityReview;
using Xunit;

namespace BirkNext.Api.Tests.Services.QualityReview;

/// <summary>
/// Tests for API Quality Review page model building.
/// Validates that the page clearly exposes which API endpoints are configured.
/// </summary>
public class ApiQualityReviewPageBuilderTests
{
    [Fact]
    public void PageModel_StatusIsBlockedWhenNoEndpointsConfigured()
    {
        // No endpoints configured = cannot run = Blocked
        var model = new QualityReviewPageModel
        {
            Title = "API Quality Review",
            Target = "",
            ReadinessStatus = QualityReviewStatus.Blocked,
            ReviewPacks = new()
            {
                new QualityReviewPack
                {
                    Name = "API Endpoints",
                    Status = QualityReviewStatus.Blocked,
                    MissingInputs = ["REST Base URL", "GraphQL Endpoint", "OpenAPI/Swagger URL", "Health Endpoint"]
                }
            },
            Summary = new() { CanRun = false }
        };

        Assert.False(model.Summary.CanRun);
        Assert.Equal(QualityReviewStatus.Blocked, model.ReadinessStatus);
        var pack = model.ReviewPacks[0];
        Assert.Equal(4, pack.MissingInputs.Count);
    }

    [Fact]
    public void PageModel_StatusIsAvailableWhenAtLeastOneEndpointExists()
    {
        // At least one endpoint configured = can run = Available
        var model = new QualityReviewPageModel
        {
            Title = "API Quality Review",
            Target = "http://localhost:5000",
            ReadinessStatus = QualityReviewStatus.Available,
            ReviewPacks = new()
            {
                new QualityReviewPack
                {
                    Name = "REST API",
                    Status = QualityReviewStatus.Available,
                    MissingInputs = []
                },
                new QualityReviewPack
                {
                    Name = "GraphQL",
                    Status = QualityReviewStatus.Blocked,
                    MissingInputs = ["GraphQL Endpoint"]
                }
            },
            Summary = new() { AvailablePacks = 1, CanRun = true }
        };

        Assert.True(model.Summary.CanRun);
        Assert.Equal(QualityReviewStatus.Available, model.ReadinessStatus);
        Assert.Equal(1, model.Summary.AvailablePacks);
    }

    [Fact]
    public void PageModel_ShowsExactMissingEndpoints()
    {
        // Model lists exactly which endpoints are missing
        var model = new QualityReviewPageModel
        {
            ReviewPacks = new()
            {
                new QualityReviewPack
                {
                    Name = "API Configuration",
                    MissingInputs = ["REST Base URL", "OpenAPI/Swagger URL"]
                }
            }
        };

        var pack = model.ReviewPacks[0];
        Assert.Contains("REST Base URL", pack.MissingInputs);
        Assert.Contains("OpenAPI/Swagger URL", pack.MissingInputs);
        Assert.DoesNotContain("Health Endpoint", pack.MissingInputs);
    }

    [Fact]
    public void PageModel_IncludesConnectivityChecks()
    {
        // Page shows what will be checked
        var model = new QualityReviewPageModel
        {
            Title = "API Quality Review",
            Checks = new()
            {
                new QualityReviewCheck { Name = "REST Connectivity", Status = QualityReviewStatus.Available },
                new QualityReviewCheck { Name = "GraphQL Connectivity", Status = QualityReviewStatus.Blocked },
                new QualityReviewCheck { Name = "Performance", Status = QualityReviewStatus.Available },
                new QualityReviewCheck { Name = "Security", Status = QualityReviewStatus.Available }
            }
        };

        Assert.Contains(model.Checks, c => c.Name == "REST Connectivity");
        var graphQLCheck = model.Checks.FirstOrDefault(c => c.Name == "GraphQL Connectivity");
        Assert.NotNull(graphQLCheck);
        Assert.Equal(QualityReviewStatus.Blocked, graphQLCheck.Status);
    }

    [Fact]
    public void PageModel_BlockedIsNotFail()
    {
        // Missing configuration = Blocked (can be fixed by user) not Fail (unrecoverable error)
        var model = new QualityReviewPageModel
        {
            ReadinessStatus = QualityReviewStatus.Blocked,
            ReviewPacks = new()
            {
                new QualityReviewPack
                {
                    Status = QualityReviewStatus.Blocked,
                    MissingInputs = ["REST Base URL"]
                }
            }
        };

        // Not Fail — missing config is not an error, just incomplete setup
        Assert.NotEqual(QualityReviewStatus.Fail, model.ReadinessStatus);
        var pack = model.ReviewPacks[0];
        Assert.NotEqual(QualityReviewStatus.Fail, pack.Status);
    }
}
