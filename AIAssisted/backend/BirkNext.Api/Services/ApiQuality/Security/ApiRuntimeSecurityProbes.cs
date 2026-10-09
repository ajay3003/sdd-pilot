using System.Net.Http.Headers;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.RuntimeSecurity;

namespace BirkNext.Api.Services.ApiQuality.Security;

/// <summary>
/// Anonymous, read-only runtime probes of API Quality Review: API documentation exposure (a few same-origin GETs) and CORS preflights
/// for the frontend origin, configured allowed origins and one synthetic foreign origin. No credential is ever attached, redirects are
/// not followed (the review client disables them) and response bodies are inspected transiently: only the classification is kept.
/// </summary>
public static class ApiRuntimeSecurityProbes
{
    private const int MaxDocumentBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public static async Task<ApiDocumentationExposureResult> DocumentationExposureAsync(HttpClient client, IOpenApiExtractor openApi, string targetId, string origin,
        string? contractSource, ApiRuntimeSecurityPolicy? expectations, CancellationToken ct)
    {
        var expectation = expectations?.ApiDocumentationExposure ?? ApiDocumentationExposureExpectation.NotSpecified;
        var probes = new List<ApiDocumentationProbe>();
        foreach (var (path, source) in ApiDocumentationExposureRules.Candidates(origin, expectations?.ApiDocumentationPaths ?? [], contractSource))
        {
            ct.ThrowIfCancellationRequested();
            probes.Add(await ProbeAsync(client, openApi, origin + path, path, source, ct));
            // Stop early once documentation is clearly public: further candidates add requests, not evidence.
            if (probes[^1].State == ApiDocumentationProbeState.ReachablePublic) break;
        }
        var observed = ApiDocumentationExposureRules.Aggregate(probes);
        var (assessment, reason) = ApiDocumentationExposureRules.Assess(observed, expectation);
        return new ApiDocumentationExposureResult { TargetId = targetId, Origin = origin, Probes = probes, Observed = observed, Expectation = expectation, Assessment = assessment, Reason = reason };
    }

    private static async Task<ApiDocumentationProbe> ProbeAsync(HttpClient client, IOpenApiExtractor openApi, string url, string path, string source, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)response.StatusCode;
            string? text = null;
            if (status is >= 200 and < 300)
            {
                var body = await ResponseBodyReader.ReadAsync(response, cts.Token, maxInspected: MaxDocumentBytes);
                text = body.Text;
            }
            var (state, kind, note) = ApiDocumentationExposureRules.Classify(status, response.Content.Headers.ContentType?.MediaType, text?.Length > 4096 ? text[..4096] : text);
            string? validation = null;
            if (kind == "OpenAPI document" && text is not null)
            {
                if (text.TrimStart().StartsWith('{'))
                {
                    var extraction = openApi.Extract(text);
                    validation = extraction.Success && extraction.Contract is { } contract
                        ? $"Valid OpenAPI 3.x document ({contract.Operations.Count} operation(s)); exposure evidence only, not the review's contract authority."
                        : $"Malformed or unsupported OpenAPI document ({extraction.ErrorMessage ?? "not parseable"}).";
                }
                else validation = "YAML document: recognised as OpenAPI, not validated (the shared OpenAPI parser reads JSON).";
            }
            return new ApiDocumentationProbe { Path = path, Source = source, StatusCode = status, State = state, DocumentKind = kind, DocumentValidation = validation, Note = note };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new ApiDocumentationProbe { Path = path, Source = source, State = ApiDocumentationProbeState.NotVerified, Note = $"No response ({ex.GetType().Name})." };
        }
    }

    /// <summary>One anonymous preflight. Returns the status and the CORS-relevant response headers (names lower-case); null on failure.</summary>
    public static async Task<(int? Status, Dictionary<string, string> Headers)> PreflightAsync(HttpClient client, string url, string origin, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var preflight = new HttpRequestMessage(HttpMethod.Options, url);
            preflight.Headers.TryAddWithoutValidation("Origin", origin);
            preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", CorsProbeRules.RequestedMethod);
            preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", CorsProbeRules.RequestedHeaders);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            using var response = await client.SendAsync(preflight, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            foreach (var name in new[] { "access-control-allow-origin", "access-control-allow-credentials", "access-control-allow-methods", "access-control-allow-headers", "vary" })
            {
                if (response.Headers.TryGetValues(name, out var values)) headers[name] = string.Join(", ", values);
                else if (response.Content.Headers.TryGetValues(name, out var contentValues)) headers[name] = string.Join(", ", contentValues);
            }
            return ((int)response.StatusCode, headers);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, headers);
        }
    }
}
