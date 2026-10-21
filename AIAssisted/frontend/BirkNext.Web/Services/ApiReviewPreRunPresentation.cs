using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Whether one selected API target can be reviewed fully, with limitations, or not at all.</summary>
public enum ApiReviewTargetReviewState { Ready, Limited, Blocked }

/// <summary>Which corrective action a target card offers (rendered by the page as a link or as an in-page action).</summary>
public enum ApiReviewTargetAction { None, ConfigureContract, ConfigureFallback, FixAccess }

/// <summary>One selected API target as the pre-run states it: what it is, how it is reached, what will and will not run, and why.</summary>
public sealed record ApiReviewTargetReview(
    ApiReviewTarget Target,
    string Name,
    string Type,
    string Access,
    bool AccessReady,
    string Operations,
    string ContractLabel,
    string ContractValue,
    string? ContractNote,
    ApiReviewTargetReviewState State,
    IReadOnlyList<string> WillRun,
    IReadOnlyList<string> WillNotRun,
    string? Limitation,
    ApiReviewTargetAction Action,
    string? FallbackValue)
{
    public string StateLabel => State switch
    {
        ApiReviewTargetReviewState.Ready => "Ready",
        ApiReviewTargetReviewState.Limited => "Limited",
        _ => "Blocked",
    };
}

/// <summary>
/// The readiness-first pre-run of API Quality Review: one run decision, a compact target/scope/access line, and per-target review states.
/// Missing optional setup (a REST OpenAPI contract, a GraphQL fallback schema, authorization checks) is a limitation of what runs — never a
/// blocker of the read-only review. Only access a target cannot get blocks it.
/// </summary>
public static class ApiReviewPreRunPresentation
{
    public static IReadOnlyList<ApiReviewTargetReview> TargetReviews(
        IReadOnlyList<ApiReviewTarget> targets,
        IReadOnlyCollection<string> selected,
        ApiReviewAccessAvailability availability,
        IReadOnlyDictionary<string, GraphQlSchemaArtifact>? artifacts = null,
        ApiReviewReport? lastReport = null)
    {
        artifacts ??= new Dictionary<string, GraphQlSchemaArtifact>();
        return targets.Where(t => selected.Contains(t.TargetId)).Select(t => Review(t, availability, artifacts.ContainsKey(t.TargetId),
            ApiReviewPresentation.SchemaUnavailablePreviously(t, lastReport))).ToList();
    }

    public static ApiReviewTargetReview Review(ApiReviewTarget target, ApiReviewAccessAvailability availability, bool hasFallback, bool schemaUnavailablePreviously)
    {
        var graphQl = target.ApiType == ApiReviewTargetType.GraphQl;
        var accessReady = !target.AuthRequired || availability == ApiReviewAccessAvailability.Available;
        var name = ApiReviewPresentation.DisplayName(target);
        var operations = graphQl
            ? target.Operations.Count(o => o.OperationType != GraphQlOperationType.None) is var observed ? $"{observed} observed operation{(observed == 1 ? "" : "s")}" : ""
            : $"{target.Operations.Count} operation{(target.Operations.Count == 1 ? "" : "s")}";
        var access = target.AuthRequired ? "Authenticated" : "Public";

        if (!accessReady)
            return new(target, name, graphQl ? "GraphQL" : "REST", access, false, operations,
                graphQl ? "Schema" : "OpenAPI", graphQl ? "Runtime introspection" : target.ContractSource is null ? "Not configured" : "Configured", null,
                ApiReviewTargetReviewState.Blocked, [], ["All checks — authenticated access is unavailable for this API."],
                $"Authenticated access unavailable for {name}.", ApiReviewTargetAction.FixAccess, graphQl ? (hasFallback ? "Configured" : "Not configured") : null);

        if (graphQl)
        {
            var limited = schemaUnavailablePreviously && !hasFallback;
            return new(target, name, "GraphQL", access, true, operations,
                "Schema", "Runtime introspection",
                schemaUnavailablePreviously
                    ? hasFallback
                        ? "Introspection was unavailable previously; it is attempted again, and the configured fallback schema is used if it is still unavailable."
                        : "Introspection was unavailable previously; it is attempted again during the review."
                    : "The GraphQL schema will be retrieved through introspection during the review.",
                limited ? ApiReviewTargetReviewState.Limited : ApiReviewTargetReviewState.Ready,
                ["Observed operations", "Schema checks", "Security", "Error handling", "Performance"],
                limited ? ["Schema-dependent checks, if introspection is still unavailable (no fallback schema configured)"] : [],
                limited ? $"GraphQL schema was unavailable previously for {name}; schema-dependent checks need introspection or a fallback schema." : null,
                ApiReviewTargetAction.ConfigureFallback, hasFallback ? "Configured" : "Not configured");
        }

        var contract = target.ContractSource is not null;
        return new(target, name, "REST", access, true, operations,
            "OpenAPI", contract ? "Configured" : "Not configured",
            contract ? "Live responses are validated against the configured contract."
                : "Responses can still be reviewed structurally, but contract compatibility cannot be validated.",
            contract ? ApiReviewTargetReviewState.Ready : ApiReviewTargetReviewState.Limited,
            contract
                ? ["Contract compatibility validation", "Structural response review", "Security", "Error handling", "Performance"]
                : ["Structural response review", "Security", "Error handling", "Performance"],
            contract ? [] : ["Contract compatibility validation"],
            contract ? null : $"REST contract validation unavailable for {name}.",
            contract ? ApiReviewTargetAction.None : ApiReviewTargetAction.ConfigureContract, null);
    }

    /// <summary>"2 API targets selected · 2/2 access ready".</summary>
    public static string Counts(IReadOnlyList<ApiReviewTargetReview> reviews) =>
        $"{reviews.Count} API target{(reviews.Count == 1 ? "" : "s")} selected · {reviews.Count(r => r.AccessReady)}/{reviews.Count} access ready";

    /// <summary>The first limitation that changes what runs, named exactly (blocked targets are blockers, not limitations).</summary>
    public static string? MainLimitation(IReadOnlyList<ApiReviewTargetReview> reviews)
    {
        var limited = reviews.Where(r => r.State == ApiReviewTargetReviewState.Limited).ToList();
        var rest = limited.Where(r => r.Type == "REST").Select(r => r.Name).ToList();
        if (rest.Count > 0) return $"REST contract validation unavailable for {Join(rest)}.";
        return limited.FirstOrDefault()?.Limitation;
    }

    /// <summary>What the review still does despite the main limitation — said once, under it.</summary>
    public static string? MainLimitationDetail(IReadOnlyList<ApiReviewTargetReview> reviews) =>
        reviews.Any(r => r.State == ApiReviewTargetReviewState.Limited && r.Type == "REST")
            ? "The review can still perform structural response, security, error-handling and performance checks."
            : reviews.Any(r => r.State == ApiReviewTargetReviewState.Limited)
                ? "Observed operations, security, error handling and performance are still reviewed."
                : null;

    /// <summary>"Will be inspected at runtime" for GraphQL targets; null without one.</summary>
    public static string? GraphQlSchema(IReadOnlyList<ApiReviewTargetReview> reviews)
    {
        var gql = reviews.Where(r => r.Type == "GraphQL" && r.AccessReady).ToList();
        if (gql.Count == 0) return null;
        return gql.All(r => r.FallbackValue == "Configured") ? "Will be inspected at runtime; fallback schema configured"
            : gql.Any(r => r.State == ApiReviewTargetReviewState.Limited) ? "Will be inspected at runtime (unavailable previously)"
            : "Will be inspected at runtime";
    }

    /// <summary>Exact blockers: one line per target that cannot be reached.</summary>
    public static IReadOnlyList<string> Blockers(IReadOnlyList<ApiReviewTargetReview> reviews) =>
        reviews.Where(r => r.State == ApiReviewTargetReviewState.Blocked).Select(r => $"Authenticated access unavailable for {r.Name}").ToList();

    /// <summary>Compact coverage line: "Security · Included".</summary>
    public static string CoverageState(ApiReviewDomainState state) => ApiReviewDomainStates.Label(state);

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
    };
}
