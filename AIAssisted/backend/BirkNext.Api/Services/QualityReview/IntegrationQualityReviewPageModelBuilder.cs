using BirkNext.Api.Services.QualityReview;
using BirkNext.Api.Services;

namespace BirkNext.Api.Services;

/// <summary>
/// Builds the structured page model for Integration Quality Review page.
/// Determines readiness based on configured integrations in the target environment.
/// </summary>
public sealed class IntegrationQualityReviewPageModelBuilder : IQualityReviewPageModelBuilder_IntegrationQuality
{
    private readonly ILogger<IntegrationQualityReviewPageModelBuilder> _logger;

    public IntegrationQualityReviewPageModelBuilder(ILogger<IntegrationQualityReviewPageModelBuilder> logger)
    {
        _logger = logger;
    }

    public async Task<QualityReviewPageModel> BuildPageModelAsync()
    {
        // In production, this would read from integration configuration service
        var integrations = DetectConfiguredIntegrations();
        var enabledCount = integrations.Count(i => i.Value);
        var canRun = enabledCount > 0;

        var checks = integrations.Select(i => new QualityReviewCheck
        {
            Name = $"{i.Key} Integration",
            Category = "Integration",
            Status = i.Value ? QualityReviewStatus.Available : QualityReviewStatus.Disabled,
            Description = $"{i.Key} integration status"
        }).ToList();

        var missing = canRun
            ? new List<string>()
            : new List<string> { "No integrations enabled in target environment" };

        var packs = new List<QualityReviewPack>
        {
            new QualityReviewPack
            {
                Name = "Integration Configuration",
                Category = "Configuration",
                Status = canRun ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
                Description = "Integrations configured in target environment",
                RequiredInputs = ["At least one integration enabled"],
                MissingInputs = missing
            }
        };

        var model = new QualityReviewPageModel
        {
            Title = "Integration Quality Review",
            Description = "Analyzes third-party integrations and their readiness for deployment.",
            Target = "staging",
            ReadinessStatus = canRun ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
            ReviewPacks = packs,
            Checks = checks,
            Sections = new()
            {
                new QualityReviewSection
                {
                    Title = "Target Environment",
                    Description = $"Integrations configured for: staging",
                    Checks = new()
                    {
                        new QualityReviewCheck
                        {
                            Name = "Environment",
                            Category = "Configuration",
                            Status = QualityReviewStatus.Available,
                            Description = "staging"
                        }
                    }
                },
                new QualityReviewSection
                {
                    Title = "Configured Integrations",
                    Description = $"{enabledCount} integration(s) enabled and ready",
                    Checks = checks.Where(c => c.Status == QualityReviewStatus.Available).ToList()
                },
                new QualityReviewSection
                {
                    Title = "Disabled Integrations",
                    Description = "Integrations not active in this environment",
                    Checks = checks.Where(c => c.Status == QualityReviewStatus.Disabled).ToList()
                }
            },
            Summary = new QualityReviewSummary
            {
                TotalPacks = 1,
                AvailablePacks = canRun ? 1 : 0,
                BlockedPacks = canRun ? 0 : 1,
                CanRun = canRun,
                ReadinessMessage = canRun
                    ? $"Ready to analyze {enabledCount} integration(s)"
                    : "Enable at least one integration in target environment to proceed"
            }
        };

        return await Task.FromResult(model);
    }

    private Dictionary<string, bool> DetectConfiguredIntegrations()
    {
        // In production, read from integration configuration service
        // This is a placeholder showing all possible integrations
        return new Dictionary<string, bool>
        {
            { "Slack", false },
            { "Email", false },
            { "GitHub", false },
            { "Azure DevOps", false },
            { "Jira", false }
        };
    }
}
