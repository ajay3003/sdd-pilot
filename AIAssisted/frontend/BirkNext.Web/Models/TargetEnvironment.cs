namespace BirkNext.Web.Models;

/// <summary>
/// Connectivity-focused projection of a <see cref="FrontendAnalysisProfile"/>.
/// Exposes the URLs, API auth, and timeout settings that runtime reviews need
/// without exposing analysis-specific configuration (thresholds, feature toggles, etc.).
/// </summary>
public sealed class TargetEnvironment
{
    public string                  Id                     { get; init; } = "";
    public string                  Name                   { get; init; } = "";
    public string?                 Description            { get; init; }
    public FrontendEnvironmentType EnvironmentType        { get; init; }
    public bool                    IsActive               { get; init; }

    // Frontend
    public string? FrontendBaseUrl { get; init; }

    // REST API
    public string? RestBaseUrl    { get; init; }
    public string? HealthEndpoint { get; init; }
    public string? SwaggerUrl     { get; init; }

    // GraphQL
    public string? GraphQlEndpoint { get; init; }

    // API authentication (used by review tools to call REST/GraphQL APIs)
    public TargetApiCredentials ApiAuth { get; init; } = new();

    // Request settings
    public int RequestTimeoutSeconds { get; init; } = 30;
    public int RetryCount            { get; init; } = 3;

    public bool HasFrontendUrl  => !string.IsNullOrWhiteSpace(FrontendBaseUrl);
    public bool HasRestBaseUrl  => !string.IsNullOrWhiteSpace(RestBaseUrl);
    public bool HasGraphQlEndpoint => !string.IsNullOrWhiteSpace(GraphQlEndpoint);
}
