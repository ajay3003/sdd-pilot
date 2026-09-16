using System.Text.RegularExpressions;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.ApiReview;

namespace BirkNext.Api.Services.ApiQuality;

public sealed record GraphQlSchemaReviewResult(
    string Hash, List<string> RootQueryFields, List<string> MutationFields, List<string> SubscriptionFields, List<string> DeprecatedFields,
    int TypeCount, List<ApiReviewCheck> Checks, List<ApiReviewFinding> Findings, List<ApiReviewGraphQlOperationMatch> OperationMatches);

/// <summary>
/// Pure review of a normalized GraphQL schema (from introspection) plus the observed operations from Endpoint Discovery: mutations and
/// subscriptions are listed (never executed), deprecated fields, descriptions, list-field nullability/pagination patterns, enum design,
/// naming consistency, and whether each observed operation name plausibly maps to a root field. Subjective design remarks stay Low/Info.
/// </summary>
public static class GraphQlSchemaReview
{
    private static readonly string[] PaginationArgs = ["first", "after", "last", "before", "skip", "take", "limit", "offset", "page", "pagesize", "cursor"];
    private static readonly Regex OperationPrefix = new("^(get|query|fetch|load|list|search|find|read)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static GraphQlSchemaReviewResult Review(GraphQlNormalizedContract schema, IReadOnlyList<ApiReviewOperation> observed, string endpoint, string targetId)
    {
        var checks = new List<ApiReviewCheck>();
        var findings = new List<ApiReviewFinding>();
        var userTypes = schema.Types.Where(t => !t.Name.StartsWith("__", StringComparison.Ordinal)).ToList();
        var queryType = userTypes.FirstOrDefault(t => string.Equals(t.Name, "Query", StringComparison.OrdinalIgnoreCase)) ?? userTypes.FirstOrDefault(t => t.Kind == "OBJECT" && t.Fields.Count > 0 && schema.Operations.Any(o => o.Kind == "query" && o.RootType == t.Name));
        var mutationType = userTypes.FirstOrDefault(t => string.Equals(t.Name, "Mutation", StringComparison.OrdinalIgnoreCase));
        var subscriptionType = userTypes.FirstOrDefault(t => string.Equals(t.Name, "Subscription", StringComparison.OrdinalIgnoreCase));
        var rootQuery = queryType?.Fields.Select(f => f.Name).ToList() ?? schema.Operations.Where(o => o.Kind == "query").Select(o => o.RootField).Distinct().ToList();
        var mutations = mutationType?.Fields.Select(f => f.Name).ToList() ?? schema.Operations.Where(o => o.Kind == "mutation").Select(o => o.RootField).Distinct().ToList();
        var subscriptions = subscriptionType?.Fields.Select(f => f.Name).ToList() ?? schema.Operations.Where(o => o.Kind == "subscription").Select(o => o.RootField).Distinct().ToList();

        var deprecated = userTypes.SelectMany(t => t.Fields.Where(f => f.IsDeprecated).Select(f => $"{t.Name}.{f.Name}"))
            .Concat(userTypes.Where(t => t.EnumValues is not null).SelectMany(t => t.EnumValues!.Where(v => v.IsDeprecated).Select(v => $"{t.Name}.{v.Name}"))).ToList();

        checks.Add(OpenApiDocumentReview.Check("gql-mutations", ApiReviewFindingType.GraphQl, "Mutations inspected, not executed",
            mutations.Count == 0 ? ApiReviewCheckResult.NotApplicable : ApiReviewCheckResult.ManualReview,
            mutations.Count == 0 ? "Schema exposes no mutations." : $"{mutations.Count} mutation(s) exist; write behaviour requires manual review — never executed by the automated review.", mutations.Take(20).ToList()));
        checks.Add(OpenApiDocumentReview.Check("gql-subscriptions", ApiReviewFindingType.GraphQl, "Subscriptions", subscriptions.Count == 0 ? ApiReviewCheckResult.NotApplicable : ApiReviewCheckResult.Pass,
            subscriptions.Count == 0 ? "None." : $"{subscriptions.Count} subscription(s) declared.", subscriptions.Take(20).ToList()));
        checks.Add(OpenApiDocumentReview.Check("gql-deprecated", ApiReviewFindingType.GraphQl, "Deprecated fields", deprecated.Count == 0 ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning,
            $"{deprecated.Count} deprecated field(s)/enum value(s).", deprecated.Take(20).ToList()));
        if (deprecated.Count > 0)
            findings.Add(OpenApiDocumentReview.Finding(targetId, "gql-deprecated-fields", ApiReviewSeverity.Low, ApiReviewFindingType.GraphQl, endpoint, "Deprecated fields",
                $"{deprecated.Count} deprecated field(s) still exposed", "Deprecated fields remain callable; clients still using them (see observed operations) should migrate before removal.",
                "Plan the removal, and check observed operations for deprecated field usage.", deprecated.Take(15).ToList(), ApiReviewCheckResult.Warning));

        var describable = userTypes.Where(t => t.Kind is "OBJECT" or "INPUT_OBJECT" or "ENUM" or "INTERFACE").ToList();
        var undescribedTypes = describable.Count(t => string.IsNullOrWhiteSpace(t.Description));
        var fields = describable.SelectMany(t => t.Fields).ToList();
        var undescribedFields = fields.Count(f => string.IsNullOrWhiteSpace(f.Description));
        checks.Add(OpenApiDocumentReview.Check("gql-descriptions", ApiReviewFindingType.Documentation, "Schema descriptions",
            describable.Count == 0 ? ApiReviewCheckResult.NotApplicable : undescribedTypes == 0 && undescribedFields == 0 ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning,
            $"{describable.Count - undescribedTypes}/{describable.Count} types and {fields.Count - undescribedFields}/{fields.Count} fields described."));
        if (describable.Count > 0 && (undescribedTypes > describable.Count / 2 || undescribedFields > Math.Max(1, fields.Count / 2)))
            findings.Add(OpenApiDocumentReview.Finding(targetId, "gql-missing-descriptions", ApiReviewSeverity.Info, ApiReviewFindingType.Documentation, endpoint, "Descriptions",
                "Most schema types/fields have no description", $"{undescribedTypes} of {describable.Count} types and {undescribedFields} of {fields.Count} fields are undescribed.",
                "Add descriptions to public types and fields; informational unless project policy requires them.", [], ApiReviewCheckResult.Warning));

        // Root list fields without pagination arguments → potential unbounded collections (only when the field returns a list of objects).
        var unbounded = new List<string>();
        var paginated = new List<string>();
        foreach (var field in queryType?.Fields ?? [])
        {
            var (typeName, _, listDepth) = field.Type?.Unwrap() ?? ("", false, 0);
            var returnsConnection = typeName.EndsWith("Connection", StringComparison.Ordinal) || typeName.EndsWith("Page", StringComparison.Ordinal) || typeName.EndsWith("Result", StringComparison.Ordinal) && userTypes.Any(t => t.Name == typeName && t.Fields.Any(f => f.Name is "items" or "nodes" or "edges" or "pageInfo" or "totalCount"));
            var hasPaginationArgs = field.Arguments.Any(a => PaginationArgs.Contains(a.Name.ToLowerInvariant()));
            if (listDepth > 0 || returnsConnection)
            {
                if (hasPaginationArgs || returnsConnection && field.Arguments.Count > 0) paginated.Add(field.Name);
                else if (listDepth > 0 && userTypes.Any(t => t.Name == typeName && t.Kind == "OBJECT")) unbounded.Add(field.Name);
            }
        }
        checks.Add(OpenApiDocumentReview.Check("gql-pagination", ApiReviewFindingType.GraphQl, "Pagination on collection fields",
            paginated.Count + unbounded.Count == 0 ? ApiReviewCheckResult.NotApplicable : unbounded.Count == 0 ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning,
            $"{paginated.Count} paginated, {unbounded.Count} unbounded list field(s) on the query root.", unbounded.Take(20).ToList()));
        if (unbounded.Count > 0)
            findings.Add(OpenApiDocumentReview.Finding(targetId, "gql-unbounded-collections", ApiReviewSeverity.Medium, ApiReviewFindingType.GraphQl, endpoint, "Unbounded collections",
                $"{unbounded.Count} root list field(s) without pagination arguments", "List fields returning object types without first/after, skip/take or limit/offset arguments can return unbounded result sets.",
                "Add pagination (Connection pattern or skip/take) to collection fields.", unbounded.Take(15).ToList(), ApiReviewCheckResult.Warning));

        // Nullability: non-null list of nullable items is a common design smell only when widespread; report informational.
        var nullableListItems = fields.Count(f => f.Type?.Unwrap() is { ListDepth: > 0 } && f.Type.Kind == "NON_NULL" && f.Type.OfType?.OfType?.Kind != "NON_NULL");
        checks.Add(OpenApiDocumentReview.Check("gql-nullability", ApiReviewFindingType.GraphQl, "List item nullability", fields.Count == 0 ? ApiReviewCheckResult.NotApplicable : ApiReviewCheckResult.Pass,
            $"{nullableListItems} non-null list field(s) with nullable items (informational)."));

        // Naming consistency: field names should be camelCase; enum values UPPER_SNAKE.
        var badFieldNames = fields.Select(f => f.Name).Where(n => n.Length > 0 && (char.IsUpper(n[0]) || n.Contains('_'))).Distinct().ToList();
        var enumTypes = userTypes.Where(t => t.Kind == "ENUM" && t.EnumValues is not null).ToList();
        var badEnumValues = enumTypes.SelectMany(t => t.EnumValues!.Select(v => $"{t.Name}.{v.Name}")).Where(v => v.Split('.')[1].Any(char.IsLower)).ToList();
        checks.Add(OpenApiDocumentReview.Check("gql-naming", ApiReviewFindingType.GraphQl, "Naming consistency",
            badFieldNames.Count == 0 && badEnumValues.Count == 0 ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning,
            $"{badFieldNames.Count} non-camelCase field name(s), {badEnumValues.Count} non-UPPER_SNAKE enum value(s).", badFieldNames.Concat(badEnumValues).Take(20).ToList()));
        if (badFieldNames.Count + badEnumValues.Count > 0)
            findings.Add(OpenApiDocumentReview.Finding(targetId, "gql-naming-inconsistent", ApiReviewSeverity.Info, ApiReviewFindingType.GraphQl, endpoint, "Naming",
                "Inconsistent GraphQL naming conventions", "Field names should be camelCase and enum values UPPER_SNAKE_CASE per common GraphQL conventions.",
                "Align names with the schema's conventions; consider deprecating rather than renaming in place.", badFieldNames.Concat(badEnumValues).Take(15).ToList(), ApiReviewCheckResult.Warning));

        var scalars = userTypes.Where(t => t.Kind == "SCALAR").Select(t => t.Name).ToList();
        checks.Add(OpenApiDocumentReview.Check("gql-scalars", ApiReviewFindingType.GraphQl, "Scalar usage", ApiReviewCheckResult.Pass, $"{scalars.Count} scalar type(s): {string.Join(", ", scalars.Take(12))}."));
        var inputTypes = userTypes.Count(t => t.Kind == "INPUT_OBJECT");
        checks.Add(OpenApiDocumentReview.Check("gql-input-types", ApiReviewFindingType.GraphQl, "Input types", ApiReviewCheckResult.Pass, $"{inputTypes} input type(s); {enumTypes.Count} enum(s)."));

        // Observed operations vs schema: client operation names are free text, so the mapping is a normalized heuristic; misses are manual review.
        var matches = new List<ApiReviewGraphQlOperationMatch>();
        foreach (var op in observed.Where(o => o.OperationType != GraphQlOperationType.None))
        {
            var candidates = op.OperationType switch { GraphQlOperationType.Mutation => mutations, GraphQlOperationType.Subscription => subscriptions, _ => rootQuery };
            var match = MatchRootField(op.OperationName, candidates);
            var display = op.Display;
            if (string.IsNullOrWhiteSpace(op.OperationName))
                matches.Add(new ApiReviewGraphQlOperationMatch(display, null, ApiReviewCheckResult.ManualReview, "Anonymous operation: the root field cannot be identified from the operation name."));
            else if (match is not null)
                matches.Add(new ApiReviewGraphQlOperationMatch(display, match, ApiReviewCheckResult.Pass, $"Maps to root field '{match}'{(deprecated.Any(d => d.EndsWith("." + match, StringComparison.Ordinal)) ? " (deprecated)" : "")}."));
            else
                matches.Add(new ApiReviewGraphQlOperationMatch(display, null, ApiReviewCheckResult.ManualReview, "No root field matches the operation name; the query body is not persisted, so the selection set cannot be verified automatically."));
        }
        var unmatched = matches.Where(m => m.Result != ApiReviewCheckResult.Pass).ToList();
        checks.Add(OpenApiDocumentReview.Check("gql-observed-operations", ApiReviewFindingType.Contract, "Observed operations map to schema",
            matches.Count == 0 ? ApiReviewCheckResult.NotApplicable : unmatched.Count == 0 ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.ManualReview,
            $"{matches.Count - unmatched.Count} of {matches.Count} observed operation(s) mapped to a root field.", unmatched.Select(m => m.Operation).Take(20).ToList()));

        var hash = JsonBodyInspector.Hash(userTypes.Select(t => $"{t.Kind} {t.Name}: {string.Join(",", t.Fields.Select(f => f.Name + ":" + (f.Type?.Unwrap().TypeName ?? "") + (f.IsDeprecated ? "!" : "")))}"));
        return new GraphQlSchemaReviewResult(hash, rootQuery, mutations, subscriptions, deprecated, userTypes.Count, checks, findings, matches);
    }

    /// <summary>GetChildren → children; ChildrenQuery → children; GetPlacement → placement(s).</summary>
    public static string? MatchRootField(string? operationName, IReadOnlyList<string> rootFields)
    {
        if (string.IsNullOrWhiteSpace(operationName) || rootFields.Count == 0) return null;
        var normalized = Normalize(OperationPrefix.Replace(operationName, ""));
        var stripped = Regex.Replace(normalized, "(query|operation|request)$", "");
        foreach (var candidate in new[] { normalized, stripped })
        {
            var exact = rootFields.FirstOrDefault(f => Normalize(f) == candidate);
            if (exact is not null) return exact;
            var plural = rootFields.FirstOrDefault(f => Normalize(f) == candidate + "s" || Normalize(f) + "s" == candidate || Normalize(f) == candidate + "es");
            if (plural is not null) return plural;
        }
        return rootFields.FirstOrDefault(f => Normalize(f).Length >= 4 && (normalized.Contains(Normalize(f), StringComparison.Ordinal) || Normalize(f).Contains(normalized, StringComparison.Ordinal)));
    }

    private static string Normalize(string value) => value.Replace("_", "").ToLowerInvariant();
}
