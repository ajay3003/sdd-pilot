using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

public sealed class SampleProjectArtifactText
{
    public string FileName { get; init; } = "";
    public string Content { get; init; } = "";
}

public sealed class DetectedTargetEnvironmentHints
{
    public string ProjectSlug { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string? FrontendUrl { get; set; }
    public string? RestBaseUrl { get; set; }
    public string? HealthEndpoint { get; set; }
    public string? SwaggerUrl { get; set; }
    public string? GraphQlEndpoint { get; set; }
    public FrontendEnvironmentType? EnvironmentType { get; set; }
    public TargetApiAuthType AuthType { get; set; } = TargetApiAuthType.None;
    public List<IntegrationTargetHint> Integrations { get; set; } = [];
    public List<string> Evidence { get; set; } = [];

    [JsonIgnore]
    public bool HasAny =>
        !string.IsNullOrWhiteSpace(FrontendUrl) ||
        !string.IsNullOrWhiteSpace(RestBaseUrl) ||
        !string.IsNullOrWhiteSpace(HealthEndpoint) ||
        !string.IsNullOrWhiteSpace(SwaggerUrl) ||
        !string.IsNullOrWhiteSpace(GraphQlEndpoint) ||
        EnvironmentType is not null ||
        AuthType != TargetApiAuthType.None ||
        Integrations.Count > 0;
}

public sealed class IntegrationTargetHint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ProviderType { get; set; } = "";
    public string? Endpoint { get; set; }
    public string? Namespace { get; set; }
    public string? Resource { get; set; }
    public string? Topic { get; set; }
    public string? Queue { get; set; }
    public string? ConsumerGroup { get; set; }
    public string? Subscription { get; set; }
    public TargetApiAuthType AuthType { get; set; } = TargetApiAuthType.None;
    public string? EnvironmentHint { get; set; }
    public string? Source { get; set; }
}

public sealed class IntegrationTargetRegistry
{
    public List<IntegrationTargetHint> Entries { get; set; } = [];
}
