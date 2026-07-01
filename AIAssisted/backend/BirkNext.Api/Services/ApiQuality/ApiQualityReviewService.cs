using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace BirkNext.Api.Services.ApiQuality;

public sealed class ApiQualityReviewService : IApiQualityReviewService
{
    private readonly HttpClient _client;
    private readonly ILogger<ApiQualityReviewService> _logger;

    // Introspection query — minimal schema probe
    private const string IntrospectionQuery = """{"query":"{ __schema { queryType { name } mutationType { name } subscriptionType { name } } }"}""";

    public ApiQualityReviewService(HttpClient client, ILogger<ApiQualityReviewService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<ApiQualityReviewReport> AnalyzeAsync(ApiQualityReviewRequest request, CancellationToken ct = default)
    {
        var findings      = new List<ApiQualityFinding>();
        var limitations   = new List<string>();
        var recommendations = new List<string>();

        // ── Phase 1: Connectivity ─────────────────────────────────────────────────
        // Only probe API endpoints — the frontend URL is not an API target.

        var restResult     = await ProbeEndpointAsync(request.RestBaseUrl,     "REST API", ct);
        var healthResult   = await ProbeEndpointAsync(request.HealthEndpoint,  "Health",   ct);
        var swaggerResult  = await ProbeEndpointAsync(request.SwaggerUrl,      "Swagger",  ct);
        var graphQlResult  = await ProbeEndpointAsync(request.GraphQlEndpoint, "GraphQL",  ct);

        var connectivityFindings = BuildConnectivityFindings(
            restResult, healthResult, swaggerResult, graphQlResult);
        findings.AddRange(connectivityFindings);

        // ── Phase 2: Security ─────────────────────────────────────────────────────

        // Pick primary API endpoint for header analysis (health → rest)
        var primaryResult = healthResult ?? restResult;
        if (primaryResult is not null)
        {
            findings.AddRange(BuildSecurityFindings(primaryResult, restResult));
        }

        // ── Phase 3: OpenAPI ──────────────────────────────────────────────────────

        if (swaggerResult is { Reachable: true })
        {
            try
            {
                var openApiFindings = await AnalyzeOpenApiAsync(request.SwaggerUrl!, ct);
                findings.AddRange(openApiFindings);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenAPI analysis failed for {Url}", request.SwaggerUrl);
                limitations.Add($"OpenAPI analysis could not be completed: {ex.Message}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.SwaggerUrl))
        {
            limitations.Add("OpenAPI/Swagger URL was configured but not reachable.");
        }

        // ── Phase 4: GraphQL ──────────────────────────────────────────────────────

        if (graphQlResult is { Reachable: true })
        {
            try
            {
                var gqlFindings = await AnalyzeGraphQlAsync(request.GraphQlEndpoint!, ct);
                findings.AddRange(gqlFindings);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GraphQL analysis failed for {Url}", request.GraphQlEndpoint);
                limitations.Add($"GraphQL analysis could not be completed: {ex.Message}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.GraphQlEndpoint))
        {
            limitations.Add("GraphQL endpoint was configured but not reachable.");
        }

        // ── Phase 5: REST ─────────────────────────────────────────────────────────

        if (restResult is { Reachable: true } || (!string.IsNullOrWhiteSpace(request.RestBaseUrl) && restResult is not null))
        {
            findings.AddRange(BuildRestFindings(restResult!, request.RestBaseUrl!));
        }

        // ── Phase 6: Performance ──────────────────────────────────────────────────

        findings.AddRange(BuildPerformanceFindings(restResult, healthResult));

        // ── Phase 7: Readiness ────────────────────────────────────────────────────

        if (healthResult is not null || restResult is not null)
        {
            var readinessFindings = await BuildReadinessFindings(
                healthResult, restResult, request.HealthEndpoint, ct);
            findings.AddRange(readinessFindings);
        }

        // ── Scoring ───────────────────────────────────────────────────────────────

        var categoryScores = ComputeCategoryScores(findings,
            restResult, healthResult, swaggerResult, graphQlResult);

        int overallScore = ComputeOverallScore(categoryScores);
        bool isReady     = overallScore >= 70 && !findings.Any(f => f.Severity == ApiQualitySeverity.Critical);

        // ── Recommendations ───────────────────────────────────────────────────────

        recommendations.AddRange(BuildRecommendations(findings));

        return new ApiQualityReviewReport
        {
            EnvironmentName   = request.EnvironmentName,
            GeneratedAt       = DateTime.UtcNow,
            OverallScore      = overallScore,
            ConnectivityScore = Score(categoryScores, ApiQualityCategory.Connectivity),
            PerformanceScore  = Score(categoryScores, ApiQualityCategory.Performance),
            SecurityScore     = Score(categoryScores, ApiQualityCategory.Security),
            RestScore         = Score(categoryScores, ApiQualityCategory.Rest),
            GraphQlScore      = Score(categoryScores, ApiQualityCategory.GraphQL),
            OpenApiScore      = Score(categoryScores, ApiQualityCategory.OpenApi),
            ReadinessScore    = Score(categoryScores, ApiQualityCategory.Readiness),
            IsDeploymentReady = isReady,
            Findings          = findings,
            CategoryScores    = categoryScores,
            Recommendations   = recommendations,
            Limitations       = limitations,
            RestResult        = restResult,
            HealthResult      = healthResult,
            SwaggerResult     = swaggerResult,
            GraphQlResult     = graphQlResult,
        };
    }

    // ── Endpoint Probe ────────────────────────────────────────────────────────────

    private async Task<ApiQualityEndpointResult?> ProbeEndpointAsync(
        string? url, string label, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var sw = Stopwatch.StartNew();
        try
        {
            using var req     = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp    = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();

            var headers = resp.Headers
                .Concat(resp.Content.Headers)
                .Where(h => h.Value.Any())
                .ToDictionary(
                    h => h.Key,
                    h => h.Value.First(),
                    StringComparer.OrdinalIgnoreCase);

            var redirectedTo = resp.RequestMessage?.RequestUri?.ToString();
            if (string.Equals(redirectedTo, url, StringComparison.OrdinalIgnoreCase))
                redirectedTo = null;

            return new ApiQualityEndpointResult
            {
                Endpoint       = url,
                Reachable      = (int)resp.StatusCode < 500,
                StatusCode     = (int)resp.StatusCode,
                ResponseTimeMs = sw.ElapsedMilliseconds,
                IsHttps        = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
                ResponseHeaders = headers,
                RedirectedTo   = redirectedTo,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogDebug(ex, "Connectivity probe failed for {Label} {Url}", label, url);
            return new ApiQualityEndpointResult
            {
                Endpoint       = url,
                Reachable      = false,
                ResponseTimeMs = sw.ElapsedMilliseconds,
                IsHttps        = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
                Error          = Truncate(ex.Message, 200),
            };
        }
    }

    // ── Connectivity Findings ─────────────────────────────────────────────────────

    private static List<ApiQualityFinding> BuildConnectivityFindings(params ApiQualityEndpointResult?[] results)
    {
        var findings = new List<ApiQualityFinding>();

        foreach (var r in results.Where(r => r is not null).Cast<ApiQualityEndpointResult>())
        {
            if (!r.IsHttps)
            {
                findings.Add(Finding(
                    "conn-no-tls", $"{r.Endpoint} uses HTTP",
                    "The endpoint is not served over HTTPS. Unencrypted traffic exposes API calls and responses to interception.",
                    "Configure TLS and redirect HTTP traffic to HTTPS.",
                    ApiQualitySeverity.Critical, ApiQualityCategory.Connectivity, [r.Endpoint]));
            }

            if (!r.Reachable)
            {
                findings.Add(Finding(
                    $"conn-unreachable-{Slug(r.Endpoint)}", $"Endpoint unreachable: {r.Endpoint}",
                    r.Error is not null ? $"Connection failed: {r.Error}" : "The endpoint did not return a successful response.",
                    "Verify the URL is correct and the service is deployed and running.",
                    ApiQualitySeverity.High, ApiQualityCategory.Connectivity, [r.Endpoint]));
            }
            else if (r.StatusCode >= 500)
            {
                findings.Add(Finding(
                    $"conn-5xx-{Slug(r.Endpoint)}", $"Server error at {r.Endpoint}",
                    $"The endpoint returned HTTP {r.StatusCode}.",
                    "Investigate server-side errors before deployment.",
                    ApiQualitySeverity.High, ApiQualityCategory.Connectivity, [$"HTTP {r.StatusCode}"]));
            }

            if (r.RedirectedTo is not null)
            {
                findings.Add(Finding(
                    $"conn-redirect-{Slug(r.Endpoint)}", $"Redirect detected: {r.Endpoint}",
                    $"Request was redirected to {r.RedirectedTo}.",
                    "Ensure configured URLs point directly to the canonical endpoint to avoid unnecessary redirect latency.",
                    ApiQualitySeverity.Info, ApiQualityCategory.Connectivity, [r.Endpoint, $"→ {r.RedirectedTo}"]));
            }
        }

        return findings;
    }

    // ── Security Findings ─────────────────────────────────────────────────────────

    private static List<ApiQualityFinding> BuildSecurityFindings(
        ApiQualityEndpointResult primary, ApiQualityEndpointResult? secondary)
    {
        var findings = new List<ApiQualityFinding>();
        var headers  = primary.ResponseHeaders;

        // HSTS
        if (!headers.ContainsKey("Strict-Transport-Security"))
        {
            findings.Add(Finding(
                "sec-no-hsts", "HSTS header missing",
                "The Strict-Transport-Security header was not returned. Browsers will not automatically upgrade HTTP to HTTPS.",
                "Add 'Strict-Transport-Security: max-age=31536000; includeSubDomains' to all API responses.",
                ApiQualitySeverity.High, ApiQualityCategory.Security, []));
        }

        // X-Content-Type-Options
        if (!headers.ContainsKey("X-Content-Type-Options"))
        {
            findings.Add(Finding(
                "sec-no-xcto", "X-Content-Type-Options header missing",
                "Without this header, browsers may MIME-sniff responses, enabling content injection attacks.",
                "Add 'X-Content-Type-Options: nosniff' to all API responses.",
                ApiQualitySeverity.Medium, ApiQualityCategory.Security, []));
        }

        // Server header exposure
        if (headers.TryGetValue("Server", out var serverVal) && !string.IsNullOrWhiteSpace(serverVal))
        {
            findings.Add(Finding(
                "sec-server-exposed", $"Server header exposes technology: {serverVal}",
                "The Server response header reveals implementation details that aid fingerprinting attacks.",
                "Remove or replace the Server header with a non-descriptive value.",
                ApiQualitySeverity.Info, ApiQualityCategory.Security, [$"Server: {serverVal}"]));
        }

        // CORS wildcard
        if (headers.TryGetValue("Access-Control-Allow-Origin", out var corsVal) &&
            corsVal.Trim() == "*")
        {
            findings.Add(Finding(
                "sec-cors-wildcard", "CORS allows all origins (*)",
                "Access-Control-Allow-Origin: * allows any domain to call the API from a browser, bypassing origin-based restrictions.",
                "Restrict CORS to known trusted origins. Use an allowlist rather than a wildcard.",
                ApiQualitySeverity.High, ApiQualityCategory.Security, ["Access-Control-Allow-Origin: *"]));
        }

        // Rate limiting
        bool hasRateLimit = headers.Keys.Any(k =>
            k.StartsWith("X-RateLimit-", StringComparison.OrdinalIgnoreCase) ||
            k.StartsWith("RateLimit-", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(k, "Retry-After", StringComparison.OrdinalIgnoreCase));

        if (!hasRateLimit)
        {
            findings.Add(Finding(
                "sec-no-ratelimit", "No rate limiting headers detected",
                "No standard rate limiting headers (X-RateLimit-*, RateLimit-*, Retry-After) were returned. " +
                "Rate limiting may not be configured, or headers may not be propagated.",
                "Implement API rate limiting and expose limit/remaining/reset headers to clients.",
                ApiQualitySeverity.Medium, ApiQualityCategory.Security, []));
        }

        return findings;
    }

    // ── OpenAPI Analysis ──────────────────────────────────────────────────────────

    private async Task<List<ApiQualityFinding>> AnalyzeOpenApiAsync(string swaggerUrl, CancellationToken ct)
    {
        var findings = new List<ApiQualityFinding>();

        using var response = await _client.GetAsync(swaggerUrl, ct);
        if (!response.IsSuccessStatusCode)
            return findings;

        var json = await response.Content.ReadAsStringAsync(ct);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { findings.Add(Finding("oas-invalid-json", "Swagger/OpenAPI document is not valid JSON",
            "The Swagger URL returned content that could not be parsed as JSON.",
            "Ensure the endpoint returns a valid OpenAPI 3.x or Swagger 2.0 JSON document.",
            ApiQualitySeverity.High, ApiQualityCategory.OpenApi, [])); return findings; }

        using (doc)
        {
            var root = doc.RootElement;

            // Info object
            if (!root.TryGetProperty("info", out var info))
            {
                findings.Add(Finding("oas-no-info", "OpenAPI document missing 'info' object",
                    "The OpenAPI document does not contain an 'info' object.",
                    "Add a complete 'info' object with title and version.",
                    ApiQualitySeverity.Medium, ApiQualityCategory.OpenApi, []));
            }
            else
            {
                if (!info.TryGetProperty("title", out var titleEl) ||
                    string.IsNullOrWhiteSpace(titleEl.GetString()))
                {
                    findings.Add(Finding("oas-no-title", "OpenAPI document missing title",
                        "info.title is absent or empty.",
                        "Set a descriptive title in the OpenAPI info object.",
                        ApiQualitySeverity.Low, ApiQualityCategory.OpenApi, []));
                }

                if (!info.TryGetProperty("version", out var versionEl) ||
                    string.IsNullOrWhiteSpace(versionEl.GetString()))
                {
                    findings.Add(Finding("oas-no-version", "OpenAPI document missing version",
                        "info.version is absent or empty.",
                        "Set the API version in the OpenAPI info object.",
                        ApiQualitySeverity.Low, ApiQualityCategory.OpenApi, []));
                }
            }

            // Servers (OAS 3.x)
            if (root.TryGetProperty("openapi", out _))
            {
                if (!root.TryGetProperty("servers", out var servers) ||
                    servers.GetArrayLength() == 0)
                {
                    findings.Add(Finding("oas-no-servers", "OpenAPI 3.x document missing 'servers'",
                        "No servers array found. API clients will not know the base URL.",
                        "Add a 'servers' array with at least one server URL.",
                        ApiQualitySeverity.Medium, ApiQualityCategory.OpenApi, []));
                }
            }

            // Paths — check operationIds and responses
            if (root.TryGetProperty("paths", out var paths))
            {
                int missingOpId = 0, missingResponse = 0, totalOps = 0;
                var httpMethods = new[] { "get", "post", "put", "patch", "delete", "options", "head" };

                foreach (var pathItem in paths.EnumerateObject())
                {
                    foreach (var method in httpMethods)
                    {
                        if (!pathItem.Value.TryGetProperty(method, out var op)) continue;
                        totalOps++;

                        if (!op.TryGetProperty("operationId", out var opId) ||
                            string.IsNullOrWhiteSpace(opId.GetString()))
                            missingOpId++;

                        if (!op.TryGetProperty("responses", out var resp) ||
                            resp.EnumerateObject().Any() == false)
                            missingResponse++;
                    }
                }

                if (missingOpId > 0)
                {
                    findings.Add(Finding("oas-missing-operationids",
                        $"{missingOpId} of {totalOps} operations are missing operationId",
                        "Operations without operationId cannot be referenced by name in code generators or documentation tools.",
                        "Add a unique operationId to every path operation.",
                        ApiQualitySeverity.Low, ApiQualityCategory.OpenApi,
                        [$"{missingOpId}/{totalOps} operations"]));
                }

                if (missingResponse > 0)
                {
                    findings.Add(Finding("oas-missing-responses",
                        $"{missingResponse} of {totalOps} operations have no documented responses",
                        "Operations without response documentation reduce API usability for client developers.",
                        "Document at least the success (2xx) and error (4xx/5xx) responses for every operation.",
                        ApiQualitySeverity.Medium, ApiQualityCategory.OpenApi,
                        [$"{missingResponse}/{totalOps} operations"]));
                }
            }
            else
            {
                findings.Add(Finding("oas-no-paths", "OpenAPI document has no paths defined",
                    "The document does not define any API paths.",
                    "Ensure the OpenAPI document is complete and includes all API endpoints.",
                    ApiQualitySeverity.High, ApiQualityCategory.OpenApi, []));
            }

            // Security schemes
            bool hasSecurity = root.TryGetProperty("components", out var components) &&
                               components.TryGetProperty("securitySchemes", out var schemes) &&
                               schemes.EnumerateObject().Any();

            if (!hasSecurity)
            {
                // Also check Swagger 2.0 securityDefinitions
                hasSecurity = root.TryGetProperty("securityDefinitions", out var defs) &&
                              defs.EnumerateObject().Any();
            }

            if (!hasSecurity)
            {
                findings.Add(Finding("oas-no-security-schemes", "No security schemes defined in OpenAPI document",
                    "The document does not declare any authentication or authorization schemes.",
                    "Define security schemes (e.g. Bearer JWT, ApiKey, OAuth2) in the OpenAPI document.",
                    ApiQualitySeverity.Medium, ApiQualityCategory.OpenApi, []));
            }
        }

        return findings;
    }

    // ── GraphQL Analysis ──────────────────────────────────────────────────────────

    private async Task<List<ApiQualityFinding>> AnalyzeGraphQlAsync(string graphQlUrl, CancellationToken ct)
    {
        var findings = new List<ApiQualityFinding>();

        using var content  = new StringContent(IntrospectionQuery, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(graphQlUrl, content, ct);

        // Content-Type check
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Finding("gql-wrong-content-type",
                "GraphQL endpoint returned non-JSON content-type",
                $"GraphQL responses must be 'application/json'. Got: {contentType}",
                "Ensure the GraphQL endpoint returns application/json for all responses.",
                ApiQualitySeverity.Medium, ApiQualityCategory.GraphQL,
                [$"Content-Type: {contentType}"]));
        }

        if (!response.IsSuccessStatusCode)
        {
            findings.Add(Finding("gql-error-response",
                $"GraphQL introspection returned HTTP {(int)response.StatusCode}",
                "The endpoint returned a non-success status for an introspection query.",
                "Verify the GraphQL endpoint URL and ensure the server is running.",
                ApiQualitySeverity.High, ApiQualityCategory.GraphQL,
                [$"HTTP {(int)response.StatusCode}"]));
            return findings;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch
        {
            findings.Add(Finding("gql-invalid-json", "GraphQL response is not valid JSON",
                "The GraphQL endpoint returned a non-JSON body for an introspection query.",
                "Ensure the GraphQL server returns valid JSON responses.",
                ApiQualitySeverity.High, ApiQualityCategory.GraphQL, []));
            return findings;
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Introspection enabled check
            bool introspectionEnabled = root.TryGetProperty("data", out var data) &&
                                        data.TryGetProperty("__schema", out _);

            if (introspectionEnabled)
            {
                findings.Add(Finding("gql-introspection-enabled",
                    "GraphQL introspection is enabled",
                    "Introspection allows any client to enumerate the full API schema, types, and fields. " +
                    "In production environments this significantly aids attackers.",
                    "Disable introspection in production. Allow it only in development/staging environments.",
                    ApiQualitySeverity.High, ApiQualityCategory.GraphQL, [graphQlUrl]));
            }

            // Error format check
            if (root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            {
                findings.Add(Finding("gql-has-errors",
                    "GraphQL response contains errors",
                    "The introspection response included an errors array.",
                    "Investigate GraphQL server errors and ensure proper error handling.",
                    ApiQualitySeverity.Low, ApiQualityCategory.GraphQL, []));
            }
        }

        return findings;
    }

    // ── REST Findings ─────────────────────────────────────────────────────────────

    private static List<ApiQualityFinding> BuildRestFindings(
        ApiQualityEndpointResult restResult, string restBaseUrl)
    {
        var findings = new List<ApiQualityFinding>();
        var headers  = restResult.ResponseHeaders;

        // Compression
        bool compressed = headers.TryGetValue("Content-Encoding", out var enc) &&
                          !string.IsNullOrWhiteSpace(enc);
        if (!compressed)
        {
            findings.Add(Finding("rest-no-compression",
                "REST API response is not compressed",
                "No Content-Encoding header detected. Uncompressed responses increase bandwidth usage and latency.",
                "Enable gzip or brotli compression on all API responses.",
                ApiQualitySeverity.Medium, ApiQualityCategory.Rest, []));
        }

        // Versioning — check common version headers
        bool hasVersioning =
            headers.ContainsKey("api-version") ||
            headers.ContainsKey("x-api-version") ||
            restBaseUrl.Contains("/v1/", StringComparison.OrdinalIgnoreCase) ||
            restBaseUrl.Contains("/v2/", StringComparison.OrdinalIgnoreCase) ||
            restBaseUrl.Contains("/api/v", StringComparison.OrdinalIgnoreCase);

        if (!hasVersioning)
        {
            findings.Add(Finding("rest-no-versioning",
                "No API versioning detected",
                "Neither URL-based versioning (e.g. /api/v1/) nor header-based versioning (api-version, x-api-version) was detected.",
                "Implement API versioning to allow non-breaking evolution of your API contract.",
                ApiQualitySeverity.Low, ApiQualityCategory.Rest, []));
        }

        // Cache-Control
        bool hasCacheControl = headers.ContainsKey("Cache-Control");
        if (!hasCacheControl)
        {
            findings.Add(Finding("rest-no-cache-control",
                "REST API response has no Cache-Control header",
                "API responses without Cache-Control headers may be unexpectedly cached by proxies or clients.",
                "Add 'Cache-Control: no-store' or appropriate caching directives to API responses.",
                ApiQualitySeverity.Low, ApiQualityCategory.Rest, []));
        }

        return findings;
    }

    // ── Performance Findings ──────────────────────────────────────────────────────

    private static List<ApiQualityFinding> BuildPerformanceFindings(params ApiQualityEndpointResult?[] results)
    {
        var findings = new List<ApiQualityFinding>();

        foreach (var r in results.Where(r => r is { Reachable: true }).Cast<ApiQualityEndpointResult>())
        {
            if (r.ResponseTimeMs > 3000)
            {
                findings.Add(Finding(
                    $"perf-very-slow-{Slug(r.Endpoint)}",
                    $"Very slow response time: {r.ResponseTimeMs} ms ({r.Endpoint})",
                    $"The endpoint took {r.ResponseTimeMs} ms to respond. Responses above 3000 ms indicate a serious performance issue.",
                    "Investigate server-side processing time, database queries, and infrastructure capacity.",
                    ApiQualitySeverity.High, ApiQualityCategory.Performance, [$"{r.ResponseTimeMs} ms"]));
            }
            else if (r.ResponseTimeMs > 1000)
            {
                findings.Add(Finding(
                    $"perf-slow-{Slug(r.Endpoint)}",
                    $"Slow response time: {r.ResponseTimeMs} ms ({r.Endpoint})",
                    $"The endpoint took {r.ResponseTimeMs} ms to respond. Responses above 1000 ms will be perceived as slow by API consumers.",
                    "Profile the endpoint and optimise slow operations (queries, serialisation, I/O).",
                    ApiQualitySeverity.Medium, ApiQualityCategory.Performance, [$"{r.ResponseTimeMs} ms"]));
            }
        }

        return findings;
    }

    // ── Readiness Findings ────────────────────────────────────────────────────────

    private async Task<List<ApiQualityFinding>> BuildReadinessFindings(
        ApiQualityEndpointResult? healthResult,
        ApiQualityEndpointResult? restResult,
        string? healthEndpointUrl,
        CancellationToken ct)
    {
        var findings = new List<ApiQualityFinding>();

        if (healthResult is null && !string.IsNullOrWhiteSpace(healthEndpointUrl))
        {
            findings.Add(Finding("rdy-health-unreachable", "Health endpoint not reachable",
                "A health endpoint URL was configured but could not be reached.",
                "Ensure the health endpoint is deployed, accessible, and returns a success status.",
                ApiQualitySeverity.High, ApiQualityCategory.Readiness, [healthEndpointUrl]));
        }
        else if (healthResult is null && !string.IsNullOrWhiteSpace(restResult?.Endpoint))
        {
            findings.Add(Finding("rdy-no-health-endpoint", "No health endpoint configured",
                "The API does not have a dedicated health endpoint configured. " +
                "Health endpoints enable orchestrators and load balancers to verify service availability.",
                "Implement a /health endpoint that returns HTTP 200 and a structured JSON status body.",
                ApiQualitySeverity.Medium, ApiQualityCategory.Readiness, []));
        }

        if (healthResult is { Reachable: true })
        {
            if (healthResult.StatusCode != 200)
            {
                findings.Add(Finding("rdy-health-non200",
                    $"Health endpoint returned HTTP {healthResult.StatusCode}",
                    "A healthy service should return HTTP 200 from its health endpoint.",
                    "Fix the health endpoint to return 200 only when the service is healthy.",
                    ApiQualitySeverity.Medium, ApiQualityCategory.Readiness,
                    [$"HTTP {healthResult.StatusCode}"]));
            }
            else
            {
                // Try to fetch the health body for structure check
                try
                {
                    using var resp = await _client.GetAsync(healthEndpointUrl ?? healthResult.Endpoint, ct);
                    var body       = await resp.Content.ReadAsStringAsync(ct);

                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(body);
                            bool hasStatus = doc.RootElement.TryGetProperty("status", out _) ||
                                            doc.RootElement.TryGetProperty("Status", out _) ||
                                            doc.RootElement.TryGetProperty("health", out _);

                            if (!hasStatus)
                            {
                                findings.Add(Finding("rdy-health-no-status-field",
                                    "Health endpoint response does not include a 'status' field",
                                    "The health endpoint returns JSON but does not include a recognizable 'status' field. " +
                                    "Standard health check responses should include 'status: healthy/unhealthy'.",
                                    "Structure the health response as { \"status\": \"healthy\" } or use ASP.NET Core HealthChecks format.",
                                    ApiQualitySeverity.Low, ApiQualityCategory.Readiness, []));
                            }
                        }
                        catch
                        {
                            findings.Add(Finding("rdy-health-non-json",
                                "Health endpoint does not return structured JSON",
                                "The health endpoint returned a plain-text or non-JSON response. " +
                                "Structured JSON health responses enable automated monitoring tools.",
                                "Return a JSON body from the health endpoint, e.g. { \"status\": \"healthy\" }.",
                                ApiQualitySeverity.Low, ApiQualityCategory.Readiness, []));
                        }
                    }
                }
                catch { /* best-effort body check */ }
            }
        }

        return findings;
    }

    // ── Scoring ───────────────────────────────────────────────────────────────────

    private static List<ApiQualityCategoryScore> ComputeCategoryScores(
        List<ApiQualityFinding> findings,
        params ApiQualityEndpointResult?[] probeResults)
    {
        var scores   = new List<ApiQualityCategoryScore>();
        var assessed = new HashSet<ApiQualityCategory>(
            findings.Select(f => f.Category)
                    .Concat(probeResults.Where(r => r is not null)
                    .SelectMany(_ => new[] { ApiQualityCategory.Connectivity, ApiQualityCategory.Performance })));

        foreach (var cat in Enum.GetValues<ApiQualityCategory>())
        {
            var catFindings = findings.Where(f => f.Category == cat).ToList();
            bool wasAssessed = assessed.Contains(cat) || catFindings.Count > 0;
            if (!wasAssessed) continue;

            int penalty = catFindings.Sum(f => f.Severity switch
            {
                ApiQualitySeverity.Critical => 25,
                ApiQualitySeverity.High     => 15,
                ApiQualitySeverity.Medium   => 8,
                ApiQualitySeverity.Low      => 3,
                _                           => 0
            });

            scores.Add(new ApiQualityCategoryScore
            {
                Category     = cat,
                Score        = Math.Max(0, 100 - penalty),
                FindingCount = catFindings.Count,
                Assessed     = true
            });
        }

        return scores;
    }

    private static int ComputeOverallScore(List<ApiQualityCategoryScore> scores)
    {
        if (scores.Count == 0) return 0;
        return (int)scores.Average(s => s.Score);
    }

    private static int Score(List<ApiQualityCategoryScore> scores, ApiQualityCategory cat) =>
        scores.FirstOrDefault(s => s.Category == cat)?.Score ?? 0;

    // ── Recommendations ───────────────────────────────────────────────────────────

    private static List<string> BuildRecommendations(List<ApiQualityFinding> findings)
    {
        var recs = new List<string>();

        if (findings.Any(f => f.Severity == ApiQualitySeverity.Critical))
            recs.Add("Address all critical findings before deployment — they represent blocking security or availability issues.");
        if (findings.Any(f => f.Category == ApiQualityCategory.Security && f.Severity <= ApiQualitySeverity.High))
            recs.Add("Resolve high-severity security findings (HSTS, CORS, rate limiting) to meet baseline API security standards.");
        if (findings.Any(f => f.Id == "gql-introspection-enabled"))
            recs.Add("Disable GraphQL introspection in production environments to reduce schema exposure.");
        if (findings.Any(f => f.Id.StartsWith("perf-slow")))
            recs.Add("Profile slow endpoints and optimise database queries, serialisation, or upstream dependencies.");
        if (findings.Any(f => f.Category == ApiQualityCategory.OpenApi))
            recs.Add("Complete the OpenAPI documentation — add missing operationIds, response schemas, and security definitions.");
        if (findings.Any(f => f.Id == "rdy-no-health-endpoint"))
            recs.Add("Implement a /health endpoint returning structured JSON status for load balancer and orchestrator integration.");

        return recs;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static ApiQualityFinding Finding(
        string id, string title, string description, string recommendation,
        ApiQualitySeverity severity, ApiQualityCategory category, IEnumerable<string> evidence) =>
        new()
        {
            Id             = id,
            Title          = title,
            Description    = description,
            Recommendation = recommendation,
            Severity       = severity,
            Category       = category,
            Evidence       = evidence.ToList()
        };

    private static string Slug(string url)
    {
        try { return new Uri(url).Host.Replace('.', '-'); }
        catch { return url.Length > 20 ? url[..20].Replace('/', '-') : url.Replace('/', '-'); }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
