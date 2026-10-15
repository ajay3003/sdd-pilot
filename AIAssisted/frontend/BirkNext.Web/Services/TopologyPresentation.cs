using BirkNext.IntegrationTopology;

namespace BirkNext.Web.Services;

/// <summary>Labels for the canonical topology. Presentation only: every status is the backend's own.</summary>
public static class TopologyPresentation
{
    public static string Status(TopologyStatus status) => status switch
    {
        TopologyStatus.Expected => "Expected",
        TopologyStatus.Configured => "Configured",
        TopologyStatus.Discovered => "Discovered",
        TopologyStatus.Observed => "Observed",
        TopologyStatus.NeedsReview => "Needs review",
        TopologyStatus.Conflict => "Conflict",
        _ => "Not evaluated",
    };

    public static string Tone(TopologyStatus status) => status switch
    {
        TopologyStatus.Observed or TopologyStatus.Discovered => "ready",
        TopologyStatus.Configured => "neutral",
        TopologyStatus.Conflict or TopologyStatus.NeedsReview => "attention",
        _ => "muted",
    };

    public static string Type(TopologyInterfaceType type) => type switch
    {
        TopologyInterfaceType.Rest => "REST",
        TopologyInterfaceType.GraphQl => "GraphQL",
        TopologyInterfaceType.ExternalApplication => "External application",
        _ => type.ToString(),
    };

    public static string Source(TopologySource source) => source switch
    {
        TopologySource.TargetEnvironment => "Target Environment",
        TopologySource.OpenApi => "OpenAPI",
        TopologySource.GraphQlSchema => "GraphQL schema",
        TopologySource.SourceAnalysis => "Source Analysis",
        TopologySource.IntegrationCatalog => "Integration catalog",
        TopologySource.ExternalApplicationMetadata => "Application metadata",
        TopologySource.BrowserObservation => "Browser observation",
        TopologySource.RuntimeEvidence => "Runtime evidence",
        _ => "Domain profile",
    };

    public static string Sources(IEnumerable<TopologyProvenance> provenance) => string.Join(", ", provenance.Select(item => Source(item.Source)).Distinct());

    public static string Direction(TopologyDirection direction) => direction switch
    {
        TopologyDirection.Provides => "provides",
        TopologyDirection.Calls => "calls",
        TopologyDirection.Produces => "produces",
        TopologyDirection.Consumes => "consumes",
        _ => "",
    };
}
