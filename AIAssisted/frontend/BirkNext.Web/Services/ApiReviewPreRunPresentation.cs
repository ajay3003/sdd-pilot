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

    /// <summary>Whether the read-only runtime review of this target can run. Independent of its contract/schema evidence path.</summary>
    public string RuntimeLabel => AccessReady ? "Runtime review ready" : "Runtime review blocked — access unavailable";

    /// <summary>
    /// The evidence path for contract/schema checks, said separately from the runtime: a REST target without OpenAPI is "Contract
    /// limited", a GraphQL target reads its schema at runtime. Neither says one target is better than the other.
    /// </summary>
    public string EvidencePathLabel => Type == "GraphQL"
        ? State == ApiReviewTargetReviewState.Limited ? "Runtime schema · limited (unavailable previously)" : FallbackValue == "Configured" ? "Runtime schema · fallback configured" : "Runtime schema"
        : ContractValue == "Configured" ? "Contract configured" : "Contract limited";

    /// <summary>"Runtime ready · Contract limited", "Runtime ready · Runtime schema", or the access blocker.</summary>
    public string StatusLine => AccessReady ? $"Runtime ready · {EvidencePathLabel}" : "Blocked · access unavailable";
}

/// <summary>One known limitation of the planned read-only review, its area, and whether it blocks (it never does for optional checks).</summary>
public sealed record ApiReviewKnownLimitation(string Area, string Text);

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

    /// <summary>"2 selected · 1 REST · 1 GraphQL · 2 authenticated" — explicit target coverage, actual values.</summary>
    public static string TargetCoverage(IReadOnlyList<ApiReviewTargetReview> reviews)
    {
        var rest = reviews.Count(r => r.Type == "REST");
        var graphQl = reviews.Count(r => r.Type == "GraphQL");
        var authenticated = reviews.Count(r => r.Access == "Authenticated");
        return string.Join(" · ", new[]
        {
            $"{reviews.Count} selected",
            rest > 0 ? $"{rest} REST" : null,
            graphQl > 0 ? $"{graphQl} GraphQL" : null,
            authenticated > 0 ? $"{authenticated} authenticated" : null,
        }.Where(p => p is not null));
    }

    /// <summary>
    /// Every known limitation of the planned review, in one list: target evidence paths (no REST contract, runtime-only GraphQL schema) and
    /// optional active checks that are blocked. None of them blocks the read-only review; access blockers are reported as blockers instead.
    /// </summary>
    public static IReadOnlyList<ApiReviewKnownLimitation> KnownLimitations(IReadOnlyList<ApiReviewTargetReview> reviews, AuthorizationCheckSummary authorization)
    {
        var limitations = new List<ApiReviewKnownLimitation>();
        foreach (var review in reviews.Where(r => r.AccessReady))
        {
            if (review.Type == "REST" && review.ContractValue != "Configured")
                limitations.Add(new($"{review.Name} REST contract", "Contract validation unavailable — no OpenAPI contract is configured. Runtime security, error-handling and performance checks still run."));
            else if (review.Type == "GraphQL")
                limitations.Add(new(review.Name.EndsWith("GraphQL", StringComparison.OrdinalIgnoreCase) ? $"{review.Name} schema" : $"{review.Name} GraphQL schema",
                    review.State == ApiReviewTargetReviewState.Limited
                    ? review.Limitation ?? "Schema unavailable previously; schema-dependent checks need introspection or a fallback schema."
                    : "Will be obtained at runtime (introspection) rather than from a configured contract; schema availability is not contract validation."));
        }
        if (authorization.Available > 0 && authorization.Ready < authorization.Available)
            limitations.Add(new("Authorization checks", AuthorizationBlockerSummary(authorization)));
        return limitations;
    }

    /// <summary>
    /// Why authorization checks are not ready, grouped by the shared catalog's readiness with its exact reason:
    /// "2 checks blocked — approved authorization test identities are missing." Never just a count.
    /// </summary>
    public static string AuthorizationBlockerSummary(AuthorizationCheckSummary authorization)
    {
        var notReady = authorization.Items
            .Where(i => i.Readiness is not AuthorizationCheckReadiness.Ready and not AuthorizationCheckReadiness.NotApplicable)
            .GroupBy(i => i.Readiness)
            .Select(g => g.Select(i => i.Reason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)) is { } why
                ? $"{g.Count()} {AuthorizationCheckCatalog.ReadinessLabel(g.Key).ToLowerInvariant()} — {why.TrimEnd('.')}"
                : $"{g.Count()} {AuthorizationCheckCatalog.ReadinessLabel(g.Key).ToLowerInvariant()}")
            .ToList();
        return notReady.Count == 0 ? "All available checks are ready." : string.Join("; ", notReady) + ".";
    }

    /// <summary>The run decision's one summary sentence: what is ready, and how much needs attention — details live below.</summary>
    public static string ReadinessSummary(IReadOnlyList<ApiReviewTargetReview> reviews, IReadOnlyList<ApiReviewKnownLimitation> limitations)
    {
        var ready = reviews.Count(r => r.AccessReady);
        var targets = $"{ready} API target{(ready == 1 ? " is" : "s are")} ready for read-only review.";
        var targetLimits = limitations.Count(l => l.Area != "Authorization checks");
        var optional = limitations.Any(l => l.Area == "Authorization checks");
        var attention = new[]
        {
            targetLimits > 0 ? $"{targetLimits} known limitation{(targetLimits == 1 ? "" : "s")}" : null,
            optional ? "optional authorization checks" : null,
        }.Where(p => p is not null).ToList();
        return attention.Count == 0 ? targets
            : $"{targets} Needs attention: {string.Join(" and ", attention)} — none of them blocks the read-only review.";
    }

    /// <summary>"2 API targets selected · 2/2 access ready".</summary>
    public static string Counts(IReadOnlyList<ApiReviewTargetReview> reviews) =>
        $"{reviews.Count} API target{(reviews.Count == 1 ? "" : "s")} selected · {reviews.Count(r => r.AccessReady)}/{reviews.Count} access ready";

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
