using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.ApiReview;

namespace BirkNext.Api.Services.ApiQuality;

/// <summary>
/// Pure contract validation and drift detection over structural evidence only (JSON paths + types). Live response shapes are compared
/// with the OpenAPI response schema of the matching operation, and with the shape recorded in a previous run (baseline). No values.
/// </summary>
public static class ContractValidation
{
    /// <summary>Compares a live 2xx response shape against the operation's documented success schema.</summary>
    public static List<ApiReviewFinding> ValidateAgainstSchema(NormalizedOperation operation, string statusCode, IReadOnlyList<JsonShapeEntry> shape, string targetId, string endpoint)
    {
        var findings = new List<ApiReviewFinding>();
        if (!operation.ResponseSchemas.TryGetValue(statusCode, out var schema) && !operation.ResponseSchemas.TryGetValue("default", out schema))
        {
            if (operation.ResponseSchemas.Count > 0)
                findings.Add(OpenApiDocumentReview.Finding(targetId, "contract-undocumented-status", ApiReviewSeverity.Medium, ApiReviewFindingType.Contract, endpoint, "Documented response codes",
                    $"Live response HTTP {statusCode} is not documented", $"The contract documents {string.Join(", ", operation.ResponseSchemas.Keys)} for this operation.",
                    "Document the observed response code or fix the endpoint.", [$"Observed: HTTP {statusCode}", $"Documented: {string.Join(", ", operation.ResponseSchemas.Keys)}"], drift: ApiReviewDriftClassification.PotentiallyBreaking));
            return findings;
        }
        var byPath = shape.ToDictionary(s => s.Path, s => s, StringComparer.Ordinal);
        var root = schema.Type == "array" ? "$[*]" : "$";
        if (schema.Type == "array" && !byPath.ContainsKey("$") ) return findings;
        if (schema.Type == "array" && byPath.TryGetValue("$", out var rootEntry) && rootEntry.Type != "array")
        {
            findings.Add(TypeMismatch(targetId, endpoint, "$", "array", rootEntry.Type));
            return findings;
        }
        if (schema.Type != "array" && byPath.TryGetValue("$", out var objRoot) && objRoot.Type != "object" && schema.Properties.Count > 0)
        {
            findings.Add(TypeMismatch(targetId, endpoint, "$", "object", objRoot.Type));
            return findings;
        }
        ValidateObject(schema, root, byPath, findings, targetId, endpoint, 0);
        return findings;
    }

    private static void ValidateObject(NormalizedSchema schema, string path, Dictionary<string, JsonShapeEntry> byPath, List<ApiReviewFinding> findings, string targetId, string endpoint, int depth)
    {
        if (depth > 6) return;
        var objectObserved = byPath.ContainsKey(path) || byPath.Keys.Any(k => k.StartsWith(path + ".", StringComparison.Ordinal));
        if (!objectObserved) return;
        foreach (var property in schema.Properties)
        {
            var propertyPath = $"{path}.{property.Name}";
            var required = property.Required || schema.Required.Contains(property.Name, StringComparer.Ordinal);
            if (!byPath.TryGetValue(propertyPath, out var observed))
            {
                if (required)
                    findings.Add(OpenApiDocumentReview.Finding(targetId, "contract-missing-required", ApiReviewSeverity.High, ApiReviewFindingType.Contract, endpoint, "Required properties",
                        $"Missing required property {propertyPath}", "The live response does not contain a property the contract marks as required.",
                        "Return the property or update the contract; consumers relying on it will break.", [$"Path: {propertyPath}", $"Expected: {property.Type}{(property.Nullable ? " (nullable)" : "")}", "Observed: absent"], drift: ApiReviewDriftClassification.Breaking));
                continue;
            }
            var observedTypes = observed.Type.Split('|', StringSplitOptions.RemoveEmptyEntries).Where(t => t != "null").ToList();
            var expected = MapType(property.Type, property.Format);
            if (observedTypes.Count > 0 && !observedTypes.All(t => Compatible(expected, t)))
                findings.Add(TypeMismatch(targetId, endpoint, propertyPath, expected, observed.Type));
            if (observed.Nullable && !property.Nullable && required)
                findings.Add(OpenApiDocumentReview.Finding(targetId, "contract-null-not-allowed", ApiReviewSeverity.Medium, ApiReviewFindingType.Contract, endpoint, "Nullability",
                    $"Null observed for non-nullable {propertyPath}", "The live response contains null where the contract does not allow it.",
                    "Mark the property nullable in the contract or never return null.", [$"Path: {propertyPath}", $"Expected: {property.Type} (non-nullable)", "Observed: null"], drift: ApiReviewDriftClassification.PotentiallyBreaking));
            if (property.Type == "array" && property.ArrayItemType is { } itemType && byPath.TryGetValue(propertyPath + "[*]", out var item))
            {
                var itemTypes = item.Type.Split('|', StringSplitOptions.RemoveEmptyEntries).Where(t => t != "null").ToList();
                var expectedItem = MapType(itemType, null);
                if (itemTypes.Count > 0 && expectedItem is not ("object" or "unknown") && !itemTypes.All(t => Compatible(expectedItem, t)))
                    findings.Add(TypeMismatch(targetId, endpoint, propertyPath + "[*]", expectedItem, item.Type));
            }
        }
        // Undocumented properties present in the live response (added fields) — informational unless additionalProperties is false.
        var documented = schema.Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var prefix = path + ".";
        var extra = byPath.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !k[prefix.Length..].Contains('.') && !k[prefix.Length..].Contains('['))
            .Select(k => k[prefix.Length..]).Where(name => !documented.Contains(name)).ToList();
        if (extra.Count > 0)
            findings.Add(OpenApiDocumentReview.Finding(targetId, "contract-undocumented-property", schema.AllowsAdditionalProperties == false ? ApiReviewSeverity.Medium : ApiReviewSeverity.Info, ApiReviewFindingType.Contract, endpoint, "Undocumented properties",
                $"{extra.Count} undocumented propert{(extra.Count == 1 ? "y" : "ies")} at {path}", schema.AllowsAdditionalProperties == false ? "The contract forbids additional properties, yet the live response contains some." : "The live response contains properties the contract does not document (non-breaking addition).",
                "Document the properties in the contract.", extra.Take(15).Select(e => $"Path: {path}.{e}").ToList(), ApiReviewCheckResult.Warning, ApiReviewDriftClassification.NonBreaking));
    }

    private static ApiReviewFinding TypeMismatch(string targetId, string endpoint, string path, string expected, string observed) =>
        OpenApiDocumentReview.Finding(targetId, "contract-type-mismatch", ApiReviewSeverity.High, ApiReviewFindingType.Contract, endpoint, "Property types",
            $"Unexpected type at {path}", "The live response type differs from the contract.", "Align the response with the contract or version the API.",
            [$"Path: {path}", $"Expected: {expected}", $"Observed: {observed}"], drift: ApiReviewDriftClassification.Breaking);

    public static string MapType(string? type, string? format) => (type ?? "").ToLowerInvariant() switch
    {
        "integer" or "int32" or "int64" or "int" or "long" => "integer",
        "number" or "float" or "double" or "decimal" => "number",
        "boolean" or "bool" => "boolean",
        "array" => "array",
        "object" => "object",
        "string" => "string",
        "" => "unknown",
        _ => "object",
    };

    /// <summary>integer is acceptable where number is documented; every observed type is acceptable for an unknown/any schema.</summary>
    public static bool Compatible(string expected, string observed) => expected == observed || expected == "unknown" ||
        (expected == "number" && observed == "integer") || (expected == "object" && observed == "object");

    /// <summary>Drift between a previously recorded shape and the current one for the same operation.</summary>
    public static List<ApiReviewFinding> Drift(string operationKey, IReadOnlyList<JsonShapeEntry> previous, IReadOnlyList<JsonShapeEntry> current, string targetId, string endpoint)
    {
        var findings = new List<ApiReviewFinding>();
        var before = previous.ToDictionary(p => p.Path, p => p, StringComparer.Ordinal);
        var after = current.ToDictionary(p => p.Path, p => p, StringComparer.Ordinal);
        var removed = before.Keys.Where(k => !after.ContainsKey(k) && !before.Keys.Any(o => o != k && k.StartsWith(o + ".", StringComparison.Ordinal) && !after.ContainsKey(o))).ToList();
        var added = after.Keys.Where(k => !before.ContainsKey(k) && !after.Keys.Any(o => o != k && k.StartsWith(o + ".", StringComparison.Ordinal) && !before.ContainsKey(o))).ToList();
        var typeChanged = before.Keys.Where(k => after.TryGetValue(k, out var a) && Types(a.Type).Count > 0 && Types(before[k].Type).Count > 0 && !Types(a.Type).SetEquals(Types(before[k].Type))).ToList();
        var nullabilityChanged = before.Keys.Where(k => after.TryGetValue(k, out var a) && a.Nullable && !before[k].Nullable).ToList();
        if (removed.Count > 0)
            findings.Add(OpenApiDocumentReview.Finding(targetId, "drift-removed-property", ApiReviewSeverity.High, ApiReviewFindingType.Drift, endpoint, $"Drift · {operationKey}",
                $"{removed.Count} propert{(removed.Count == 1 ? "y" : "ies")} no longer returned", "Properties present in the previous review's response shape are absent now.",
                "Confirm the removal is intentional and versioned; consumers may break.", removed.Take(15).Select(r => $"Removed: {r}").ToList(), drift: ApiReviewDriftClassification.Breaking));
        if (typeChanged.Count > 0)
            findings.Add(OpenApiDocumentReview.Finding(targetId, "drift-type-change", ApiReviewSeverity.High, ApiReviewFindingType.Drift, endpoint, $"Drift · {operationKey}",
                $"{typeChanged.Count} propert{(typeChanged.Count == 1 ? "y" : "ies")} changed type", "The observed JSON type of these paths differs from the previous review.",
                "Treat as a breaking change unless the previous value was a one-off.", typeChanged.Take(15).Select(k => $"{k}: {before[k].Type} → {after[k].Type}").ToList(), drift: ApiReviewDriftClassification.Breaking));
        if (nullabilityChanged.Count > 0)
            findings.Add(OpenApiDocumentReview.Finding(targetId, "drift-nullability-change", ApiReviewSeverity.Medium, ApiReviewFindingType.Drift, endpoint, $"Drift · {operationKey}",
                $"{nullabilityChanged.Count} propert{(nullabilityChanged.Count == 1 ? "y" : "ies")} now nullable", "Null values were observed where the previous review saw none.",
                "Verify consumers handle null; document nullability.", nullabilityChanged.Take(15).Select(k => $"Now nullable: {k}").ToList(), ApiReviewCheckResult.Warning, ApiReviewDriftClassification.PotentiallyBreaking));
        if (added.Count > 0)
            findings.Add(OpenApiDocumentReview.Finding(targetId, "drift-added-property", ApiReviewSeverity.Info, ApiReviewFindingType.Drift, endpoint, $"Drift · {operationKey}",
                $"{added.Count} new propert{(added.Count == 1 ? "y" : "ies")} returned", "New properties appeared since the previous review (non-breaking addition).",
                "Document the additions in the contract.", added.Take(15).Select(a => $"Added: {a}").ToList(), ApiReviewCheckResult.Warning, ApiReviewDriftClassification.NonBreaking));
        return findings;
    }

    private static HashSet<string> Types(string joined) => joined.Split('|', StringSplitOptions.RemoveEmptyEntries).Where(t => t != "null").ToHashSet(StringComparer.Ordinal);

    /// <summary>GraphQL schema drift versus the previous run: removed root fields (breaking), newly deprecated fields (potentially breaking), schema hash change.</summary>
    public static List<ApiReviewFinding> GraphQlDrift(ApiReviewBaseline baseline, GraphQlSchemaReviewResult current, string targetId, string endpoint)
    {
        var findings = new List<ApiReviewFinding>();
        var removed = baseline.GraphQlRootFields.Where(f => !current.RootQueryFields.Contains(f, StringComparer.Ordinal)).ToList();
        if (removed.Count > 0)
            findings.Add(OpenApiDocumentReview.Finding(targetId, "drift-gql-root-field-removed", ApiReviewSeverity.High, ApiReviewFindingType.Drift, endpoint, "GraphQL schema drift",
                $"{removed.Count} root query field(s) removed since the previous review", "Observed client operations may still target these fields.",
                "Restore or version the schema; migrate clients.", removed.Take(15).Select(r => $"Removed root field: {r}").ToList(), drift: ApiReviewDriftClassification.Breaking));
        var newlyDeprecated = current.DeprecatedFields.Where(f => !baseline.GraphQlDeprecatedFields.Contains(f, StringComparer.Ordinal)).ToList();
        if (newlyDeprecated.Count > 0)
            findings.Add(OpenApiDocumentReview.Finding(targetId, "drift-gql-newly-deprecated", ApiReviewSeverity.Medium, ApiReviewFindingType.Drift, endpoint, "GraphQL schema drift",
                $"{newlyDeprecated.Count} field(s) newly deprecated", "Fields deprecated since the previous review announce a future breaking removal.",
                "Migrate observed operations away from these fields.", newlyDeprecated.Take(15).ToList(), ApiReviewCheckResult.Warning, ApiReviewDriftClassification.PotentiallyBreaking));
        var added = current.RootQueryFields.Where(f => !baseline.GraphQlRootFields.Contains(f, StringComparer.Ordinal)).ToList();
        if (added.Count > 0 && baseline.GraphQlRootFields.Count > 0)
            findings.Add(OpenApiDocumentReview.Finding(targetId, "drift-gql-root-field-added", ApiReviewSeverity.Info, ApiReviewFindingType.Drift, endpoint, "GraphQL schema drift",
                $"{added.Count} root query field(s) added", "Non-breaking schema additions since the previous review.", "Document the new fields.", added.Take(15).ToList(), ApiReviewCheckResult.Warning, ApiReviewDriftClassification.NonBreaking));
        else if (removed.Count == 0 && newlyDeprecated.Count == 0 && baseline.GraphQlSchemaHash is { } h && h != current.Hash)
            findings.Add(OpenApiDocumentReview.Finding(targetId, "drift-gql-schema-changed", ApiReviewSeverity.Info, ApiReviewFindingType.Drift, endpoint, "GraphQL schema drift",
                "GraphQL schema changed since the previous review (no root field removed)", "Type or field level changes were detected by schema hash.", "Review the schema diff in source control.",
                [$"Previous hash: {h}", $"Current hash: {current.Hash}"], ApiReviewCheckResult.Warning, ApiReviewDriftClassification.Informational));
        return findings;
    }
}
