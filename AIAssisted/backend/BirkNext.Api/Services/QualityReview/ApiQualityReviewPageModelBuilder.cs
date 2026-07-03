using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services.QualityReview;

namespace BirkNext.Api.Services;

/// <summary>
/// Builds the structured page model for API Quality Review page.
/// Determines which API endpoints are configured in the active target environment.
/// </summary>
public sealed class ApiQualityReviewPageModelBuilder : IQualityReviewPageModelBuilder_ApiQuality
{
    private readonly ILogger<ApiQualityReviewPageModelBuilder> _logger;

    public ApiQualityReviewPageModelBuilder(ILogger<ApiQualityReviewPageModelBuilder> logger)
    {
        _logger = logger;
    }

    public async Task<QualityReviewPageModel> BuildPageModelAsync()
    {
        // In production, this would read from ITargetEnvironmentService or similar
        // For now, return a model structure showing what data is needed
        var endpoints = DetectConfiguredEndpoints();

        var canRun = endpoints.Values.Any(e => e);
        var missing = ExtractMissingEndpoints(endpoints);

        var packs = new List<QualityReviewPack>
        {
            new QualityReviewPack
            {
                Name = "API Endpoints",
                Category = "Configuration",
                Status = canRun ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
                Description = "API endpoints configuration check",
                RequiredInputs = ["REST Base URL", "GraphQL Endpoint", "OpenAPI/Swagger URL", "Health Endpoint"],
                MissingInputs = canRun ? [] : missing
            }
        };

        var checks = new List<QualityReviewCheck>
        {
            new QualityReviewCheck
            {
                Name = "REST API Connectivity",
                Category = "Connectivity",
                Status = endpoints["rest"] ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
                Description = "Checks REST API endpoint accessibility"
            },
            new QualityReviewCheck
            {
                Name = "GraphQL Connectivity",
                Category = "Connectivity",
                Status = endpoints["graphql"] ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
                Description = "Checks GraphQL endpoint accessibility"
            },
            new QualityReviewCheck
            {
                Name = "OpenAPI/Swagger",
                Category = "Documentation",
                Status = endpoints["openapi"] ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
                Description = "Checks for OpenAPI/Swagger documentation"
            },
            new QualityReviewCheck
            {
                Name = "Health Endpoint",
                Category = "Connectivity",
                Status = endpoints["health"] ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
                Description = "Checks health check endpoint"
            },
            new QualityReviewCheck
            {
                Name = "Performance Analysis",
                Category = "Performance",
                Status = canRun ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
                Description = "Analyzes API response times and throughput"
            },
            new QualityReviewCheck
            {
                Name = "Security Scan",
                Category = "Security",
                Status = canRun ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
                Description = "Checks for security vulnerabilities and issues"
            }
        };

        var model = new QualityReviewPageModel
        {
            Title = "API Quality Review",
            Description = "Comprehensive analysis of REST and GraphQL API quality, connectivity, and security.",
            Target = "Active Target Environment",
            ReadinessStatus = canRun ? QualityReviewStatus.Available : QualityReviewStatus.Blocked,
            ReviewPacks = packs,
            Checks = checks,
            Sections = new()
            {
                new QualityReviewSection
                {
                    Title = "Audit Target",
                    Description = "Target environment API endpoints",
                    Checks = checks.Take(4).ToList()
                },
                new QualityReviewSection
                {
                    Title = "What will be analyzed",
                    Description = "Dimensions of API quality to be evaluated",
                    Checks = checks.Skip(4).ToList()
                }
            },
            Summary = new QualityReviewSummary
            {
                TotalPacks = 1,
                AvailablePacks = canRun ? 1 : 0,
                BlockedPacks = canRun ? 0 : 1,
                CanRun = canRun,
                ReadinessMessage = canRun
                    ? "Ready to analyze API endpoints"
                    : "Configure at least one API endpoint to proceed"
            }
        };

        return await Task.FromResult(model);
    }

    private Dictionary<string, bool> DetectConfiguredEndpoints()
    {
        // In real implementation, read from target environment configuration
        // This is a placeholder showing the structure
        return new Dictionary<string, bool>
        {
            { "rest", false },      // REST Base URL configured
            { "graphql", false },   // GraphQL Endpoint configured
            { "openapi", false },   // OpenAPI/Swagger URL configured
            { "health", false }     // Health Endpoint configured
        };
    }

    private List<string> ExtractMissingEndpoints(Dictionary<string, bool> endpoints)
    {
        var missing = new List<string>();

        if (!endpoints.ContainsKey("rest") || !endpoints["rest"])
            missing.Add("REST Base URL");
        if (!endpoints.ContainsKey("graphql") || !endpoints["graphql"])
            missing.Add("GraphQL Endpoint");
        if (!endpoints.ContainsKey("openapi") || !endpoints["openapi"])
            missing.Add("OpenAPI/Swagger URL");
        if (!endpoints.ContainsKey("health") || !endpoints["health"])
            missing.Add("Health Endpoint");

        return missing;
    }
}
