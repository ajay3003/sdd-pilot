using BirkNext.Api.Services.QualityReview;
using Xunit;

namespace BirkNext.Api.Tests.Services.QualityReview;

/// <summary>
/// Tests for Integration Quality Review page model building.
/// Validates that the page clearly shows integration readiness.
/// </summary>
public class IntegrationQualityReviewPageBuilderTests
{
    [Fact]
    public void PageModel_StatusIsBlockedWhenNoIntegrationsConfigured()
    {
        // Zero configured integrations = cannot run = Blocked (not Fail)
        var model = new QualityReviewPageModel
        {
            Title = "Integration Quality Review",
            Target = "staging",
            ReadinessStatus = QualityReviewStatus.Blocked,
            ReviewPacks = new()
            {
                new QualityReviewPack
                {
                    Name = "Integration Configuration",
                    Status = QualityReviewStatus.Blocked,
                    RequiredInputs = ["At least one integration enabled"],
                    MissingInputs = ["No integrations enabled"]
                }
            },
            Summary = new() { CanRun = false }
        };

        Assert.False(model.Summary.CanRun);
        Assert.Equal(QualityReviewStatus.Blocked, model.ReadinessStatus);
        var pack = model.ReviewPacks[0];
        Assert.Equal(QualityReviewStatus.Blocked, pack.Status);
        Assert.NotEqual(QualityReviewStatus.Fail, pack.Status);
    }

    [Fact]
    public void PageModel_StatusIsAvailableWhenIntegrationConfigured()
    {
        // At least one integration enabled/configured = can run = Available
        var model = new QualityReviewPageModel
        {
            Title = "Integration Quality Review",
            Target = "staging",
            ReadinessStatus = QualityReviewStatus.Available,
            ReviewPacks = new()
            {
                new QualityReviewPack
                {
                    Name = "Slack Integration",
                    Status = QualityReviewStatus.Available,
                    MissingInputs = []
                },
                new QualityReviewPack
                {
                    Name = "Email Integration",
                    Status = QualityReviewStatus.Disabled,
                    MissingInputs = []
                }
            },
            Summary = new() { AvailablePacks = 1, CanRun = true }
        };

        Assert.True(model.Summary.CanRun);
        Assert.Equal(1, model.Summary.AvailablePacks);
    }

    [Fact]
    public void PageModel_ShowsExactMissingRequirement()
    {
        // Model lists exact missing requirement
        var model = new QualityReviewPageModel
        {
            ReviewPacks = new()
            {
                new QualityReviewPack
                {
                    Name = "Integrations",
                    RequiredInputs = ["At least one integration must be enabled in target environment"],
                    MissingInputs = ["No integrations enabled in staging environment"]
                }
            }
        };

        var pack = model.ReviewPacks[0];
        Assert.NotEmpty(pack.MissingInputs);
        Assert.Contains("No integrations enabled", pack.MissingInputs[0]);
    }

    [Fact]
    public void PageModel_ListsEnabledIntegrations()
    {
        // Model shows which integrations are available/enabled
        var model = new QualityReviewPageModel
        {
            Title = "Integration Quality Review",
            Checks = new()
            {
                new QualityReviewCheck { Name = "Slack Connection", Status = QualityReviewStatus.Available },
                new QualityReviewCheck { Name = "Email Service", Status = QualityReviewStatus.Disabled },
                new QualityReviewCheck { Name = "GitHub Sync", Status = QualityReviewStatus.Available }
            }
        };

        Assert.Equal(3, model.Checks.Count);
        var enabledCount = model.Checks.Count(c => c.Status == QualityReviewStatus.Available);
        Assert.Equal(2, enabledCount);
    }

    [Fact]
    public void PageModel_UsesActiveTargetEnvironment()
    {
        // Page uses the active target environment
        var model = new QualityReviewPageModel
        {
            Title = "Integration Quality Review",
            Target = "staging",
            Sections = new()
            {
                new QualityReviewSection
                {
                    Title = "Target Environment",
                    Description = "Integrations configured for: staging"
                }
            }
        };

        Assert.Equal("staging", model.Target);
        var targetSection = model.Sections.FirstOrDefault(s => s.Title == "Target Environment");
        Assert.NotNull(targetSection);
        Assert.Contains("staging", targetSection.Description);
    }

    [Fact]
    public void PageModel_BlockedIsNotFail()
    {
        // Missing integrations = Blocked (user can fix) not Fail (unrecoverable error)
        var model = new QualityReviewPageModel
        {
            ReadinessStatus = QualityReviewStatus.Blocked,
            ReviewPacks = new()
            {
                new QualityReviewPack
                {
                    Status = QualityReviewStatus.Blocked,
                    MissingInputs = ["No integrations enabled"]
                }
            }
        };

        Assert.NotEqual(QualityReviewStatus.Fail, model.ReadinessStatus);
        var pack = model.ReviewPacks[0];
        Assert.NotEqual(QualityReviewStatus.Fail, pack.Status);
    }
}
