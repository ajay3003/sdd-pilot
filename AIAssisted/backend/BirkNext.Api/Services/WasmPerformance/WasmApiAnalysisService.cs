using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BirkNext.Api.Services.WasmPerformance;

public sealed class WasmApiAnalysisService : IWasmApiAnalysisService
{
    // GraphQL paths tried in parallel
    private static readonly string[] GraphQLPaths =
        ["/graphql", "/api/graphql", "/v1/graphql", "/_graphql", "/graph"];

    // OpenAPI/Swagger paths tried in order
    private static readonly string[] OpenApiPaths =
    [
        "/swagger/v1/swagger.json",
        "/swagger.json",
        "/openapi.json",
        "/openapi/v3/swagger.json"
    ];

    // Minimal detection query — not arbitrary; standard GraphQL health-check idiom
    private const string DetectionBody =
        """{"query":"query DetectGraphQL { __typename }","operationName":"DetectGraphQL"}""";

    // Schema presence check — confirms introspection is enabled, not a full schema dump
    private const string IntrospectionBody =
        """{"query":"{ __schema { queryType { name } mutationType { name } subscriptionType { name } } }","operationName":"IntrospectionCheck"}""";

    private static readonly ApiAnalysisThresholds Defaults = new();

    private const int GraphQLProbeSec = 8;
    private const int OpenApiProbeSec = 5;
    private const int MaxRestEndpoints = 100;

    private readonly HttpClient _client;
    private readonly ILogger<WasmApiAnalysisService> _logger;

    public WasmApiAnalysisService(
        HttpClient client,
        ILogger<WasmApiAnalysisService> logger)
    {
        _client = client;
        _logger = logger;
    }

    // ── Public interface ──────────────────────────────────────────────────────

    public async Task<ApiAnalysisResult> AnalyzeAsync(
        string targetUrl,
        ApiAnalysisThresholds? thresholds = null,
        CancellationToken ct = default)
    {
        var t = thresholds ?? Defaults;

        if (!Uri.TryCreate(targetUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var rootUri))
            return new ApiAnalysisResult { Error = "Invalid target URL for API analysis." };

        _logger.LogInformation("API analysis started for {Host}", rootUri.Host);

        // Run GraphQL probe and REST probe in parallel
        var graphqlTask = ProbeGraphQLAsync(rootUri, ct);
        var restTask    = ProbeRestAsync(rootUri, ct);

        await Task.WhenAll(graphqlTask, restTask);

        var graphql = graphqlTask.Result;
        var rest    = restTask.Result;

        var input = new ApiProbeInput
        {
            GraphQLDetected      = graphql.Detected,
            IntrospectionEnabled = graphql.IntrospectionEnabled,
            GraphQLCompressed    = graphql.Compressed,
            GraphQLLatencyMs     = graphql.LatencyMs,
            GraphQLResponseBytes = graphql.ResponseBytes,
            HasGraphQLErrors     = graphql.HasErrors,
            HasOpenApi           = rest.Detected
        };

        var findings = GenerateApiFindings(input, t);
        var recs     = GenerateApiRecommendations(graphql.Detected, findings);

        _logger.LogInformation(
            "API analysis complete for {Host}: graphQL={GraphQL}, openApi={OpenApi}, findings={Findings}",
            rootUri.Host, graphql.Detected, rest.Detected, findings.Count);

        return new ApiAnalysisResult
        {
            HasGraphQL                  = graphql.Detected,
            GraphQLEndpoint             = graphql.Endpoint,
            GraphQLIntrospectionEnabled = graphql.IntrospectionEnabled,
            GraphQLResponseCompressed   = graphql.Compressed,
            HasOpenApi                  = rest.Detected,
            OpenApiUrl                  = rest.OpenApiUrl,
            RestEndpointCount           = rest.Endpoints.Count,
            GraphQLOperations           = graphql.Operations,
            RestEndpoints               = rest.Endpoints,
            Findings                    = findings.ToList(),
            Recommendations             = recs.ToList()
        };
    }

    // ── Pure static methods — unit-testable ───────────────────────────────────

    internal static bool IsGraphQLResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            var doc  = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   (root.TryGetProperty("data", out _) || root.TryGetProperty("errors", out _));
        }
        catch { return false; }
    }

    internal static bool HasGraphQLErrors(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            var doc  = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("errors", out var errors)) return false;
            return errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0;
        }
        catch { return false; }
    }

    internal static bool IsIntrospectionEnabled(string body)
    {
        if (!IsGraphQLResponse(body)) return false;
        try
        {
            var doc  = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data)) return false;
            return data.ValueKind == JsonValueKind.Object &&
                   data.TryGetProperty("__schema", out _);
        }
        catch { return false; }
    }

    internal static IReadOnlyList<RestEndpointSummary> ParseOpenApiEndpoints(string json)
    {
        var results     = new List<RestEndpointSummary>();
        var httpMethods = new[] { "get", "post", "put", "patch", "delete", "head", "options" };

        try
        {
            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("paths", out var paths))
                return results;

            foreach (var pathEntry in paths.EnumerateObject())
            {
                if (results.Count >= MaxRestEndpoints) break;
                var pathStr = pathEntry.Name;

                foreach (var method in httpMethods)
                {
                    if (!pathEntry.Value.TryGetProperty(method, out var op)) continue;

                    var summary = "";
                    if (op.TryGetProperty("summary", out var s) &&
                        s.ValueKind == JsonValueKind.String)
                        summary = s.GetString() ?? "";

                    if (string.IsNullOrEmpty(summary) &&
                        op.TryGetProperty("operationId", out var oid) &&
                        oid.ValueKind == JsonValueKind.String)
                        summary = oid.GetString() ?? "";

                    var hasAuth = false;
                    if (op.TryGetProperty("security", out var sec) &&
                        sec.ValueKind == JsonValueKind.Array)
                        hasAuth = sec.GetArrayLength() > 0;

                    results.Add(new RestEndpointSummary
                    {
                        Path               = pathStr,
                        Method             = method.ToUpperInvariant(),
                        Summary            = string.IsNullOrEmpty(summary) ? null : summary,
                        HasAuthRequirement = hasAuth
                    });
                }
            }
        }
        catch { /* ignore parse errors */ }

        return results;
    }

    internal static IReadOnlyList<PerformanceFinding> GenerateApiFindings(
        ApiProbeInput input, ApiAnalysisThresholds t)
    {
        var findings = new List<PerformanceFinding>();

        if (input.GraphQLDetected)
        {
            // API-G001: Introspection enabled in production
            if (input.IntrospectionEnabled)
            {
                findings.Add(new PerformanceFinding
                {
                    Id          = "API-G001",
                    Title       = "GraphQL introspection is enabled",
                    Severity    = PerformanceSeverity.Medium,
                    Category    = PerformanceCategory.ApiCalls,
                    Description = "GraphQL introspection allows any client to query the full API schema, exposing " +
                                  "field names, types, and relationships. In production, this is a security concern " +
                                  "and adds unnecessary overhead.",
                    Recommendation = "Disable introspection in production by configuring your GraphQL server " +
                                     "(e.g. HotChocolate: options.EnableSchemaIntrospection = false for non-development environments).",
                    Evidence    = ["Introspection query returned __schema data"]
                });
            }

            // API-G002: Uncompressed GraphQL responses
            if (!input.GraphQLCompressed)
            {
                findings.Add(new PerformanceFinding
                {
                    Id          = "API-G002",
                    Title       = "GraphQL responses are not compressed",
                    Severity    = PerformanceSeverity.Medium,
                    Category    = PerformanceCategory.ApiCalls,
                    Description = "GraphQL JSON responses were not served with Brotli or Gzip compression. " +
                                  "GraphQL payloads contain repetitive structure that compresses extremely well — " +
                                  "typically 80–90% size reduction.",
                    Recommendation = "Enable response compression middleware on the GraphQL server. " +
                                     "For ASP.NET Core: add UseResponseCompression() and configure Brotli/Gzip " +
                                     "for application/json and application/graphql content types.",
                    Evidence    = ["GraphQL response contained no Content-Encoding header"]
                });
            }

            // API-G003: Large GraphQL response
            var maxBytes = (long)(t.MaxGraphQLResponseKB * 1024);
            if (input.GraphQLResponseBytes > maxBytes)
            {
                findings.Add(new PerformanceFinding
                {
                    Id          = "API-G003",
                    Title       = "Large GraphQL response payload",
                    Severity    = PerformanceSeverity.Medium,
                    Category    = PerformanceCategory.ApiCalls,
                    Description = $"The GraphQL endpoint returned {FormatBytes(input.GraphQLResponseBytes)}, " +
                                  $"exceeding the {t.MaxGraphQLResponseKB} KB threshold. " +
                                  "Large responses indicate over-fetching or missing pagination.",
                    Recommendation = "Reduce field selection in GraphQL queries to request only the data needed. " +
                                     "Implement cursor-based pagination for list queries. " +
                                     "Consider splitting large operations into smaller, targeted queries.",
                    Evidence    = [$"Response size: {FormatBytes(input.GraphQLResponseBytes)}"]
                });
            }

            // API-G004: Slow GraphQL latency
            if (input.GraphQLLatencyMs > t.MaxApiLatencyMs)
            {
                findings.Add(new PerformanceFinding
                {
                    Id          = "API-G004",
                    Title       = "Slow GraphQL endpoint response time",
                    Severity    = PerformanceSeverity.Low,
                    Category    = PerformanceCategory.ApiCalls,
                    Description = $"The GraphQL endpoint responded in {input.GraphQLLatencyMs:F0} ms, " +
                                  $"exceeding the {t.MaxApiLatencyMs:F0} ms threshold. " +
                                  "Slow API responses increase time-to-interactive for data-driven components.",
                    Recommendation = "Profile resolver performance and use DataLoader to batch database queries. " +
                                     "Check for N+1 query problems. Consider adding response caching for reference data.",
                    Evidence    = [$"Detection query latency: {input.GraphQLLatencyMs:F0} ms", $"Threshold: {t.MaxApiLatencyMs:F0} ms"]
                });
            }

            // API-G005: GraphQL errors returned with HTTP 200
            if (input.HasGraphQLErrors)
            {
                findings.Add(new PerformanceFinding
                {
                    Id          = "API-G005",
                    Title       = "GraphQL returned errors in HTTP 200 response",
                    Severity    = PerformanceSeverity.Info,
                    Category    = PerformanceCategory.ApiCalls,
                    Description = "The GraphQL endpoint returned an errors array inside an HTTP 200 response. " +
                                  "This is standard GraphQL behaviour for partial or blocked results, " +
                                  "but may indicate the scan query was unauthorised or filtered.",
                    Recommendation = "Verify that unauthenticated access to GraphQL is intentional. " +
                                     "If the endpoint requires authentication, ensure client code handles errors gracefully " +
                                     "rather than treating all HTTP 200 responses as success.",
                    Evidence    = ["Detection query received errors array in response body"]
                });
            }
        }

        // API-R001: No REST API documentation
        if (!input.HasOpenApi)
        {
            findings.Add(new PerformanceFinding
            {
                Id          = "API-R001",
                Title       = "No REST API documentation found",
                Severity    = PerformanceSeverity.Info,
                Category    = PerformanceCategory.ApiCalls,
                Description = "No OpenAPI (Swagger) documentation was found at common paths. " +
                              "Without API documentation, clients and QA tooling cannot enumerate or validate endpoints.",
                Recommendation = "Add OpenAPI documentation using Swashbuckle.AspNetCore. " +
                                 "Expose the spec at /swagger/v1/swagger.json and " +
                                 "enable SwaggerUI in development environments.",
                Evidence    = ["Probed: /swagger/v1/swagger.json, /swagger.json, /openapi.json, /openapi/v3/swagger.json — all returned non-200 or non-JSON"]
            });
        }

        return findings;
    }

    internal static IReadOnlyList<PerformanceRecommendation> GenerateApiRecommendations(
        bool graphqlDetected, IReadOnlyList<PerformanceFinding> findings)
    {
        var ids  = findings.Select(f => f.Id).ToHashSet();
        var recs = new List<PerformanceRecommendation>();
        int p    = 1;

        if (graphqlDetected)
        {
            // Always recommend operationName — best practice regardless of detection result
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Always include operationName in GraphQL requests",
                Description = "Ensure every GraphQL operation sent by the client has a unique operationName. " +
                              "This enables server-side logging, APM tracing, query-level caching, and " +
                              "easier debugging in production.",
                Category    = PerformanceCategory.ApiCalls
            });
        }

        if (ids.Contains("API-G001"))
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Disable GraphQL introspection in production",
                Description = "Configure the GraphQL server to reject introspection queries outside development environments. " +
                              "HotChocolate: options.EnableSchemaIntrospection = env.IsDevelopment(). " +
                              "Apollo: introspection: process.env.NODE_ENV !== 'production'.",
                Category    = PerformanceCategory.ApiCalls
            });
        }

        if (ids.Contains("API-G002"))
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Enable Brotli compression for GraphQL responses",
                Description = "Add response compression middleware and target application/json content types. " +
                              "Brotli compression typically reduces GraphQL payload size by 80–90%, " +
                              "substantially reducing startup time for data-heavy Blazor apps.",
                Category    = PerformanceCategory.ApiCalls
            });
        }

        if (ids.Contains("API-G003"))
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Reduce GraphQL response payload size",
                Description = "Select only the fields required by each component. " +
                              "Implement cursor-based pagination for list queries. " +
                              "Consider splitting startup queries from background data loads.",
                Category    = PerformanceCategory.ApiCalls
            });
        }

        if (graphqlDetected)
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Cache reference-data GraphQL queries",
                Description = "Use server-side response caching (HotChocolate @cacheControl directive) for " +
                              "queries that return rarely-changing data such as configuration, lookup tables, or " +
                              "taxonomy trees. This reduces database load and improves startup time for returning users.",
                Category    = PerformanceCategory.ApiCalls
            });
        }

        if (!ids.Contains("API-R001") /* has OpenAPI */)
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p,
                Title       = "Document REST endpoints and their caching strategy",
                Description = "Review discovered REST endpoints and annotate them with response caching headers. " +
                              "Add Cache-Control headers for GET endpoints returning stable data. " +
                              "Consider ETag support for conditional requests.",
                Category    = PerformanceCategory.ApiCalls
            });
        }

        return recs;
    }

    // ── HTTP probe internals ───────────────────────────────────────────────────

    private async Task<GraphQLProbeState> ProbeGraphQLAsync(Uri rootUri, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        var tasks = GraphQLPaths.Select(async path =>
        {
            try
            {
                var uri = new Uri(rootUri, path.TrimStart('/'));
                return await TryGraphQLEndpointAsync(uri, cts.Token);
            }
            catch { return new GraphQLProbeState(); }
        });

        var results = await Task.WhenAll(tasks);
        return results.FirstOrDefault(r => r.Detected) ?? new GraphQLProbeState();
    }

    private async Task<GraphQLProbeState> TryGraphQLEndpointAsync(Uri uri, CancellationToken ct)
    {
        var state = new GraphQLProbeState();
        var sw    = Stopwatch.StartNew();

        string body;
        HttpResponseMessage response;

        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(TimeSpan.FromSeconds(GraphQLProbeSec));

            var content  = new StringContent(DetectionBody, Encoding.UTF8, "application/json");
            response     = await _client.PostAsync(uri, content, reqCts.Token);
            sw.Stop();
            body         = await response.Content.ReadAsStringAsync(ct);
        }
        catch { return state; }

        if (!IsGraphQLResponse(body)) return state;

        var payloadBytes = Encoding.UTF8.GetByteCount(DetectionBody);

        state.Detected       = true;
        state.Endpoint       = uri.ToString().TrimEnd('/');
        state.LatencyMs      = sw.Elapsed.TotalMilliseconds;
        state.ResponseBytes  = Encoding.UTF8.GetByteCount(body);
        state.Compressed     = !string.IsNullOrEmpty(
            response.Content.Headers.ContentEncoding.FirstOrDefault());
        state.HasErrors      = HasGraphQLErrors(body);

        state.Operations.Add(new GraphQLOperationSummary
        {
            OperationName        = "DetectGraphQL",
            Type                 = GraphQLOperationType.Query,
            Calls                = 1,
            AverageLatencyMs     = Math.Round(state.LatencyMs, 1),
            LargestResponseBytes = state.ResponseBytes,
            RequestPayloadBytes  = payloadBytes,
            ErrorCount           = state.HasErrors ? 1 : 0,
            IsCompressed         = state.Compressed,
            Recommendation       = state.Compressed ? null : "Enable Brotli compression for GraphQL endpoint."
        });

        // Introspection check
        try
        {
            using var introCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            introCts.CancelAfter(TimeSpan.FromSeconds(GraphQLProbeSec));

            var introSw      = Stopwatch.StartNew();
            var introContent = new StringContent(IntrospectionBody, Encoding.UTF8, "application/json");
            var introResp    = await _client.PostAsync(uri, introContent, introCts.Token);
            introSw.Stop();

            var introBody       = await introResp.Content.ReadAsStringAsync(ct);
            var introEnabled    = IsIntrospectionEnabled(introBody);
            state.IntrospectionEnabled = introEnabled;

            state.Operations.Add(new GraphQLOperationSummary
            {
                OperationName        = "IntrospectionCheck",
                Type                 = GraphQLOperationType.Query,
                Calls                = 1,
                AverageLatencyMs     = Math.Round(introSw.Elapsed.TotalMilliseconds, 1),
                LargestResponseBytes = Encoding.UTF8.GetByteCount(introBody),
                RequestPayloadBytes  = Encoding.UTF8.GetByteCount(IntrospectionBody),
                ErrorCount           = HasGraphQLErrors(introBody) ? 1 : 0,
                IsCompressed         = !string.IsNullOrEmpty(
                    introResp.Content.Headers.ContentEncoding.FirstOrDefault()),
                Recommendation       = introEnabled
                    ? "Disable introspection in production environments."
                    : null
            });
        }
        catch { /* introspection probe is best-effort */ }

        return state;
    }

    private async Task<RestProbeState> ProbeRestAsync(Uri rootUri, CancellationToken ct)
    {
        foreach (var path in OpenApiPaths)
        {
            try
            {
                var uri = new Uri(rootUri, path.TrimStart('/'));

                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(TimeSpan.FromSeconds(OpenApiProbeSec));

                var response = await _client.GetAsync(uri, reqCts.Token);
                if (!response.IsSuccessStatusCode) continue;

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) continue;

                var body      = await response.Content.ReadAsStringAsync(ct);
                var endpoints = ParseOpenApiEndpoints(body);
                if (endpoints.Count == 0) continue;

                return new RestProbeState
                {
                    Detected   = true,
                    OpenApiUrl  = uri.ToString().TrimEnd('/'),
                    Endpoints   = endpoints.ToList()
                };
            }
            catch { continue; }
        }

        return new RestProbeState();
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)        return "0 B";
        if (bytes < 1_024)     return $"{bytes} B";
        if (bytes < 1_048_576) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1_048_576.0:F2} MB";
    }

    // ── Private probe state ────────────────────────────────────────────────────

    private sealed class GraphQLProbeState
    {
        public bool   Detected              { get; set; }
        public string? Endpoint             { get; set; }
        public bool   IntrospectionEnabled  { get; set; }
        public bool   Compressed            { get; set; }
        public double LatencyMs             { get; set; }
        public long   ResponseBytes         { get; set; }
        public bool   HasErrors             { get; set; }
        public List<GraphQLOperationSummary> Operations { get; } = [];
    }

    private sealed class RestProbeState
    {
        public bool    Detected  { get; init; }
        public string? OpenApiUrl { get; init; }
        public List<RestEndpointSummary> Endpoints { get; init; } = [];
    }
}
