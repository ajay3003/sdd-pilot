namespace BirkNext.Api.Services.WasmPerformance;

public enum GraphQLOperationType { Query = 0, Mutation = 1, Subscription = 2, Unknown = 3 }

public sealed class ApiAnalysisThresholds
{
    public double MaxGraphQLResponseKB { get; init; } = 500.0;
    public double MaxApiLatencyMs      { get; init; } = 2_000.0;
}

public sealed class GraphQLOperationSummary
{
    public required string         OperationName      { get; init; }
    public GraphQLOperationType    Type               { get; init; }
    public int                     Calls              { get; init; }
    public double                  AverageLatencyMs   { get; init; }
    public long                    LargestResponseBytes { get; init; }
    public long                    RequestPayloadBytes  { get; init; }
    public int                     ErrorCount         { get; init; }
    public bool                    IsCompressed       { get; init; }
    public string?                 Recommendation     { get; init; }
}

public sealed class RestEndpointSummary
{
    public required string Path   { get; init; }
    public required string Method { get; init; }
    public string?         Summary         { get; init; }
    public bool            HasAuthRequirement { get; init; }
}

public sealed class ApiAnalysisResult
{
    public bool     HasGraphQL                  { get; init; }
    public string?  GraphQLEndpoint             { get; init; }
    public bool     GraphQLIntrospectionEnabled { get; init; }
    public bool     GraphQLResponseCompressed   { get; init; }

    public bool     HasOpenApi        { get; init; }
    public string?  OpenApiUrl        { get; init; }
    public int      RestEndpointCount { get; init; }

    public List<GraphQLOperationSummary> GraphQLOperations { get; init; } = [];
    public List<RestEndpointSummary>     RestEndpoints     { get; init; } = [];

    public List<PerformanceFinding>        Findings        { get; init; } = [];
    public List<PerformanceRecommendation> Recommendations { get; init; } = [];

    public string? Error { get; init; }
}

// ── Internal probe input — pure data for unit-testable FindingGeneration ──────

internal sealed class ApiProbeInput
{
    public bool   GraphQLDetected      { get; init; }
    public bool   IntrospectionEnabled { get; init; }
    public bool   GraphQLCompressed    { get; init; }
    public double GraphQLLatencyMs     { get; init; }
    public long   GraphQLResponseBytes { get; init; }
    public bool   HasGraphQLErrors     { get; init; }
    public bool   HasOpenApi           { get; init; }
}
