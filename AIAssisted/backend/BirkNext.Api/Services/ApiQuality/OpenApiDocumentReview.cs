using System.Text.Json;
using BirkNext.ApiReview;

namespace BirkNext.Api.Services.ApiQuality;

/// <summary>One operation of an OpenAPI document as needed for the review (documentation, security, pagination, responses).</summary>
public sealed record OpenApiOperationInfo(string Method, string Path, string? OperationId, bool HasSummaryOrDescription, List<string> ResponseCodes,
    bool HasErrorResponse, bool HasSecurity, List<string> QueryParameters, List<string> PaginationParameters);

public sealed record OpenApiReviewResult(
    bool Valid, string? Version, string? Title, string Hash, int PathCount, List<OpenApiOperationInfo> Operations, List<string> SecuritySchemes,
    bool GlobalSecurity, List<ApiReviewCheck> Checks, List<ApiReviewFinding> Findings, string? Error);

/// <summary>
/// Pure review of an OpenAPI 3.x / Swagger 2.0 JSON document: validity, version, duplicate operations, operationIds, descriptions,
/// undocumented/missing error responses, security schemes and operation security, pagination conventions. Documentation gaps are
/// Low/Info; structural contract gaps are Medium. Never blocks a release on descriptions alone.
/// </summary>
public static class OpenApiDocumentReview
{
    private static readonly string[] HttpMethods = ["get", "post", "put", "patch", "delete", "options", "head"];
    private static readonly string[] PaginationParams = ["page", "pagesize", "page_size", "pageindex", "limit", "offset", "skip", "take", "cursor", "after", "before", "first", "last", "continuationtoken", "top"];

    public static OpenApiReviewResult Review(string json, string source, string targetId)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            var finding = Finding(targetId, "oas-invalid-json", ApiReviewSeverity.High, ApiReviewFindingType.Contract, source, "Contract document",
                "OpenAPI document is not valid JSON", $"The contract URL returned content that could not be parsed as JSON ({ex.GetType().Name}).",
                "Serve a valid OpenAPI 3.x or Swagger 2.0 JSON document.", ["Source: " + source]);
            return new OpenApiReviewResult(false, null, null, JsonBodyInspector.Hash(json), 0, [], [], false,
                [Check("oas-valid", ApiReviewFindingType.Contract, "OpenAPI document valid", ApiReviewCheckResult.Fail, "Not valid JSON.")], [finding], "Invalid JSON");
        }
        using (doc)
        {
            var root = doc.RootElement;
            var checks = new List<ApiReviewCheck>();
            var findings = new List<ApiReviewFinding>();
            var version = root.TryGetProperty("openapi", out var v) ? v.GetString() : root.TryGetProperty("swagger", out var s) ? "swagger " + s.GetString() : null;
            var title = root.TryGetProperty("info", out var info) && info.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (version is null)
            {
                findings.Add(Finding(targetId, "oas-no-version-field", ApiReviewSeverity.Medium, ApiReviewFindingType.Contract, source, "Document validity",
                    "Document declares neither 'openapi' nor 'swagger'", "The document cannot be identified as an OpenAPI/Swagger contract.", "Add the 'openapi' version field (e.g. \"3.0.3\").", []));
                checks.Add(Check("oas-valid", ApiReviewFindingType.Contract, "OpenAPI document valid", ApiReviewCheckResult.Fail, "No openapi/swagger version field."));
            }
            else checks.Add(Check("oas-valid", ApiReviewFindingType.Contract, "OpenAPI document valid", ApiReviewCheckResult.Pass, $"Version {version}."));

            if (info.ValueKind == JsonValueKind.Undefined || string.IsNullOrWhiteSpace(title) || !(info.TryGetProperty("version", out var iv) && !string.IsNullOrWhiteSpace(iv.GetString())))
                findings.Add(Finding(targetId, "oas-info-incomplete", ApiReviewSeverity.Low, ApiReviewFindingType.Documentation, source, "Info object",
                    "OpenAPI info object incomplete", "info.title and/or info.version are missing.", "Provide title and version in the info object.", []));

            var operations = new List<OpenApiOperationInfo>();
            var duplicates = new List<string>();
            var seenOperationIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var pathCount = 0;
            if (root.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
            {
                var normalizedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pathItem in paths.EnumerateObject())
                {
                    pathCount++;
                    var normalized = NormalizeTemplate(pathItem.Name);
                    if (normalizedPaths.TryGetValue(normalized, out var other)) duplicates.Add($"{pathItem.Name} ≈ {other}");
                    else normalizedPaths[normalized] = pathItem.Name;
                    var pathParameters = pathItem.Value.TryGetProperty("parameters", out var pp) ? QueryParams(pp) : [];
                    foreach (var method in HttpMethods)
                    {
                        if (!pathItem.Value.TryGetProperty(method, out var op) || op.ValueKind != JsonValueKind.Object) continue;
                        var operationId = op.TryGetProperty("operationId", out var oid) ? oid.GetString() : null;
                        if (operationId is { Length: > 0 })
                        {
                            if (seenOperationIds.TryGetValue(operationId, out var first)) duplicates.Add($"operationId '{operationId}' on {first} and {method.ToUpperInvariant()} {pathItem.Name}");
                            else seenOperationIds[operationId] = $"{method.ToUpperInvariant()} {pathItem.Name}";
                        }
                        var described = (op.TryGetProperty("summary", out var sum) && !string.IsNullOrWhiteSpace(sum.GetString())) || (op.TryGetProperty("description", out var d) && !string.IsNullOrWhiteSpace(d.GetString()));
                        var responses = op.TryGetProperty("responses", out var r) && r.ValueKind == JsonValueKind.Object ? r.EnumerateObject().Select(p => p.Name).ToList() : [];
                        var hasError = responses.Any(c => c.StartsWith('4') || c.StartsWith('5') || c.Equals("default", StringComparison.OrdinalIgnoreCase));
                        var hasSecurity = op.TryGetProperty("security", out var sec) && sec.ValueKind == JsonValueKind.Array && sec.GetArrayLength() > 0;
                        var query = pathParameters.Concat(op.TryGetProperty("parameters", out var opp) ? QueryParams(opp) : []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        var pagination = query.Where(q => PaginationParams.Contains(q.ToLowerInvariant())).ToList();
                        operations.Add(new OpenApiOperationInfo(method.ToUpperInvariant(), pathItem.Name, operationId, described, responses, hasError, hasSecurity, query, pagination));
                    }
                }
            }
            else
            {
                findings.Add(Finding(targetId, "oas-no-paths", ApiReviewSeverity.Medium, ApiReviewFindingType.Contract, source, "Paths",
                    "OpenAPI document has no paths", "The document defines no API paths, so no operation can be validated against it.", "Publish the complete contract.", []));
            }

            var securitySchemes = new List<string>();
            if (root.TryGetProperty("components", out var components) && components.TryGetProperty("securitySchemes", out var schemes) && schemes.ValueKind == JsonValueKind.Object)
                securitySchemes.AddRange(schemes.EnumerateObject().Select(p => p.Name));
            if (root.TryGetProperty("securityDefinitions", out var defs) && defs.ValueKind == JsonValueKind.Object)
                securitySchemes.AddRange(defs.EnumerateObject().Select(p => p.Name));
            var globalSecurity = root.TryGetProperty("security", out var gs) && gs.ValueKind == JsonValueKind.Array && gs.GetArrayLength() > 0;

            if (duplicates.Count > 0)
                findings.Add(Finding(targetId, "oas-duplicate-operations", ApiReviewSeverity.Medium, ApiReviewFindingType.Contract, source, "Duplicate paths/operations",
                    $"{duplicates.Count} duplicate path template(s) or operationId(s)", "Duplicate templates or operationIds make the contract ambiguous for clients and generators.",
                    "Merge or rename the duplicated operations.", duplicates.Take(10).ToList()));
            checks.Add(Check("oas-duplicates", ApiReviewFindingType.Contract, "No duplicate operations", duplicates.Count == 0 ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning, $"{duplicates.Count} duplicate(s)."));

            var missingOperationId = operations.Where(o => string.IsNullOrWhiteSpace(o.OperationId)).ToList();
            if (missingOperationId.Count > 0)
                findings.Add(Finding(targetId, "oas-missing-operationid", ApiReviewSeverity.Low, ApiReviewFindingType.Documentation, source, "operationId",
                    $"{missingOperationId.Count} of {operations.Count} operations have no operationId", "Operations without operationId cannot be referenced by generators or documentation tools.",
                    "Add a unique operationId to every operation.", missingOperationId.Take(10).Select(o => $"{o.Method} {o.Path}").ToList()));
            var undescribed = operations.Where(o => !o.HasSummaryOrDescription).ToList();
            if (undescribed.Count > 0)
                findings.Add(Finding(targetId, "oas-missing-descriptions", ApiReviewSeverity.Info, ApiReviewFindingType.Documentation, source, "Summaries/descriptions",
                    $"{undescribed.Count} of {operations.Count} operations have no summary or description", "Undocumented operations reduce API usability; informational unless project policy requires descriptions.",
                    "Add a summary or description to each operation.", undescribed.Take(10).Select(o => $"{o.Method} {o.Path}").ToList()));
            var noResponses = operations.Where(o => o.ResponseCodes.Count == 0).ToList();
            if (noResponses.Count > 0)
                findings.Add(Finding(targetId, "oas-undocumented-responses", ApiReviewSeverity.Medium, ApiReviewFindingType.Contract, source, "Responses",
                    $"{noResponses.Count} operation(s) document no responses", "Clients cannot know the success shape or status of these operations.",
                    "Document at least the success response with its schema.", noResponses.Take(10).Select(o => $"{o.Method} {o.Path}").ToList()));
            var noErrors = operations.Where(o => o.ResponseCodes.Count > 0 && !o.HasErrorResponse).ToList();
            if (noErrors.Count > 0)
                findings.Add(Finding(targetId, "oas-missing-error-responses", ApiReviewSeverity.Low, ApiReviewFindingType.Contract, source, "Error responses",
                    $"{noErrors.Count} operation(s) document no 4xx/5xx or default response", "Error contracts (e.g. ProblemDetails) are part of the API surface.",
                    "Document error responses (400/401/404/500 or default) with a shared problem schema.", noErrors.Take(10).Select(o => $"{o.Method} {o.Path}").ToList()));
            if (securitySchemes.Count == 0)
                findings.Add(Finding(targetId, "oas-no-security-schemes", ApiReviewSeverity.Medium, ApiReviewFindingType.Security, source, "Security schemes",
                    "No security schemes declared", "The contract does not declare how clients authenticate.", "Declare the security scheme (e.g. bearer JWT) and reference it globally or per operation.", []));
            else if (!globalSecurity)
            {
                var unsecured = operations.Where(o => !o.HasSecurity).ToList();
                if (unsecured.Count > 0)
                    findings.Add(Finding(targetId, "oas-operations-without-security", ApiReviewSeverity.Low, ApiReviewFindingType.Security, source, "Operation security",
                        $"{unsecured.Count} operation(s) declare no security requirement", "Security schemes exist but these operations do not reference one; if they are intentionally public, document it.",
                        "Reference a security requirement on every protected operation or set a global security requirement.", unsecured.Take(10).Select(o => $"{o.Method} {o.Path}").ToList()));
            }
            checks.Add(Check("oas-security-declared", ApiReviewFindingType.Security, "Security schemes declared", securitySchemes.Count > 0 ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning, string.Join(", ", securitySchemes)));

            var listLike = operations.Where(o => o.Method == "GET" && !o.Path.TrimEnd('/').EndsWith('}') && o.ResponseCodes.Contains("200")).ToList();
            var unpaged = listLike.Where(o => o.PaginationParameters.Count == 0 && o.QueryParameters.Count > 0).ToList();
            checks.Add(Check("oas-pagination", ApiReviewFindingType.Contract, "Pagination declared on collection operations",
                listLike.Count == 0 ? ApiReviewCheckResult.NotApplicable : unpaged.Count == 0 ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning,
                $"{listLike.Count - unpaged.Count} of {listLike.Count} collection GET operations declare pagination parameters."));
            checks.Add(Check("oas-documentation", ApiReviewFindingType.Documentation, "Operations documented",
                operations.Count == 0 ? ApiReviewCheckResult.NotApplicable : undescribed.Count == 0 && missingOperationId.Count == 0 ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning,
                $"{operations.Count - undescribed.Count}/{operations.Count} described; {operations.Count - missingOperationId.Count}/{operations.Count} with operationId."));
            checks.Add(Check("oas-error-responses", ApiReviewFindingType.Contract, "Error responses documented",
                operations.Count == 0 ? ApiReviewCheckResult.NotApplicable : noErrors.Count == 0 && noResponses.Count == 0 ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning,
                $"{operations.Count - noErrors.Count - noResponses.Count}/{operations.Count} operations document error responses."));

            var hash = JsonBodyInspector.Hash(operations.Select(o => $"{o.Method} {o.Path} {string.Join(",", o.ResponseCodes)} {string.Join(",", o.QueryParameters)}"));
            return new OpenApiReviewResult(version is not null, version, title, hash, pathCount, operations, securitySchemes, globalSecurity, checks, findings, null);
        }
    }

    private static List<string> QueryParams(JsonElement parameters)
    {
        var list = new List<string>();
        if (parameters.ValueKind != JsonValueKind.Array) return list;
        foreach (var p in parameters.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            var inQuery = p.TryGetProperty("in", out var i) && string.Equals(i.GetString(), "query", StringComparison.OrdinalIgnoreCase);
            if (inQuery && p.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) list.Add(name);
        }
        return list;
    }

    /// <summary>"/children/{id}" and "/children/{childId}" are the same route.</summary>
    public static string NormalizeTemplate(string path) => System.Text.RegularExpressions.Regex.Replace(path.TrimEnd('/'), @"\{[^}]+\}", "{}").ToLowerInvariant();

    /// <summary>Matches a concrete path ("/api/children/42") to a template ("/api/children/{id}").</summary>
    public static bool TemplateMatches(string template, string concretePath)
    {
        var t = template.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var c = concretePath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length != c.Length) return false;
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i].StartsWith('{') && t[i].EndsWith('}')) continue;
            if (!string.Equals(t[i], c[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    internal static ApiReviewCheck Check(string id, ApiReviewFindingType area, string title, ApiReviewCheckResult result, string detail, List<string>? evidence = null) =>
        new() { CheckId = id, Area = area, Title = title, Result = result, Detail = detail, Evidence = evidence ?? [] };

    internal static ApiReviewFinding Finding(string targetId, string id, ApiReviewSeverity severity, ApiReviewFindingType type, string endpoint, string check, string title,
        string description, string recommendation, List<string> evidence, ApiReviewCheckResult result = ApiReviewCheckResult.Fail, ApiReviewDriftClassification? drift = null) =>
        new() { Id = $"{id}-{Math.Abs(HashCode.Combine(targetId, endpoint, title)) % 100000}", RuleId = id, TargetId = targetId, Severity = severity, Type = type, Endpoint = endpoint, Check = check, Title = title,
            Description = description, Recommendation = recommendation, Evidence = evidence, Result = result, Drift = drift };
}
