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
    string? FallbackValue,
    /// <summary>REST: how the contract was resolved from existing evidence (configured, Source Analysis, ambiguous, none).</summary>
    ApiReviewContractBinding? Binding = null)
{
    public string StateLabel => State switch
    {
        ApiReviewTargetReviewState.Ready => "Ready",
        ApiReviewTargetReviewState.Limited => "Limited",
        _ => "Blocked",
    };

    /// <summary>Whether the read-only runtime review of this target can run. Independent of its contract/schema evidence path.</summary>
    public string RuntimeLabel => AccessReady ? "Runtime review available" : "Runtime review blocked — access unavailable";

    /// <summary>
    /// The evidence path for contract/schema checks, said separately from the runtime: a REST target without OpenAPI is "Contract
    /// limited", a GraphQL target reads its schema at runtime. Neither says one target is better than the other.
    /// </summary>
    public string EvidencePathLabel => Type == "GraphQL"
        ? FallbackValue == "Configured" ? "Published schema configured" : "Published schema not available · runtime schema via introspection"
        : Binding?.Label ?? (ContractValue == "Configured" ? "Contract configured" : "Contract not available");

    /// <summary>"Runtime review available · Contract not available" — never a schema claim before the schema is observed.</summary>
    public string StatusLine => AccessReady ? $"Runtime review available · {EvidencePathLabel}" : "Blocked · access unavailable";
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
        ApiReviewReport? lastReport = null,
        IReadOnlyDictionary<string, ApiReviewContractBinding>? bindings = null)
    {
        artifacts ??= new Dictionary<string, GraphQlSchemaArtifact>();
        return targets.Where(t => selected.Contains(t.TargetId)).Select(t => Review(t, availability, artifacts.ContainsKey(t.TargetId),
            ApiReviewPresentation.SchemaUnavailablePreviously(t, lastReport), bindings?.GetValueOrDefault(t.TargetId))).ToList();
    }

    public static ApiReviewTargetReview Review(ApiReviewTarget target, ApiReviewAccessAvailability availability, bool hasFallback, bool schemaUnavailablePreviously,
        ApiReviewContractBinding? binding = null)
    {
        binding ??= target.ApiType == ApiReviewTargetType.Rest
            ? new(target.ContractSource is null ? ApiReviewContractBindingState.NotAvailable : ApiReviewContractBindingState.Configured, target.ContractSource)
            : null;
        var graphQl = target.ApiType == ApiReviewTargetType.GraphQl;
        var accessReady = !target.AuthRequired || availability == ApiReviewAccessAvailability.Available;
        var name = ApiReviewPresentation.DisplayName(target);
        var operations = graphQl
            ? target.Operations.Count(o => o.OperationType != GraphQlOperationType.None) is var observed ? $"{observed} observed operation{(observed == 1 ? "" : "s")}" : ""
            : $"{target.Operations.Count} operation{(target.Operations.Count == 1 ? "" : "s")}";
        var access = target.AuthRequired ? "Authenticated" : "Public";

        if (!accessReady)
            return new(target, name, graphQl ? "GraphQL" : "REST", access, false, operations,
                graphQl ? "Runtime schema" : "REST contract", graphQl ? "Not observed — access unavailable" : binding!.Label, null,
                ApiReviewTargetReviewState.Blocked, [], ["All checks — authenticated access is unavailable for this API."],
                $"Authenticated access unavailable for {name}.", ApiReviewTargetAction.FixAccess, graphQl ? (hasFallback ? "Configured" : "Not available") : null, binding);

        if (graphQl)
        {
            // Runtime introspection is never a published contract; a target without a published schema still runs (compatibility is then
            // not assessed). A previous rejection is noted, not a limitation of the read-only review.
            var limited = false;
            return new(target, name, "GraphQL", access, true, operations,
                "Runtime schema", schemaUnavailablePreviously ? "Unavailable previously — introspection attempted again during review" : "Will attempt introspection during review",
                schemaUnavailablePreviously
                    ? hasFallback
                        ? "Introspection was unavailable previously; it is attempted again, and the configured fallback schema is used if it is still unavailable."
                        : "Introspection was unavailable previously; it is attempted again during the review."
                    : "The GraphQL schema will be retrieved through introspection during the review.",
                limited ? ApiReviewTargetReviewState.Limited : ApiReviewTargetReviewState.Ready,
                ["Observed operations", "Runtime schema checks (if introspection succeeds)", "Security", "Error handling", "Response timing"],
                hasFallback ? [] : ["Contract compatibility (no published schema — not assessed)"],
                null,
                ApiReviewTargetAction.ConfigureFallback, hasFallback ? "Configured" : "Not available");
        }

        // The contract is resolved from existing evidence before anyone is asked to configure it; a missing contract is a coverage note, not a
        // limitation of the read-only review. Configuration is offered only to resolve ambiguity or to override.
        var contract = binding!.State == ApiReviewContractBindingState.Configured;
        var needsChoice = binding.State is ApiReviewContractBindingState.MultipleCandidates or ApiReviewContractBindingState.MultipleSnapshots;
        return new(target, name, "REST", access, true, operations,
            "REST contract", binding.Label, binding.Meaning,
            ApiReviewTargetReviewState.Ready,
            contract
                ? ["Contract compatibility validation", "Structural response review", "Security", "Error handling", "Response timing"]
                : ["Structural response review", "Security", "Error handling", "Response timing"],
            contract ? [] : ["Contract compatibility (not assessed)"],
            null,
            needsChoice ? ApiReviewTargetAction.ConfigureContract : ApiReviewTargetAction.None, null, binding);
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
        // Coverage notes, not limitations of the read-only review: contract/schema evidence that is not available, and optional active checks.
        foreach (var review in reviews.Where(r => r.AccessReady))
        {
            if (review.Type == "REST" && review.Binding is { State: not ApiReviewContractBindingState.Configured } binding)
                limitations.Add(new($"{review.Name} REST contract", $"{binding.Label}. {binding.Meaning}"));
            else if (review.Type == "GraphQL" && review.FallbackValue != "Configured")
                limitations.Add(new(review.Name.EndsWith("GraphQL", StringComparison.OrdinalIgnoreCase) ? $"{review.Name} schema" : $"{review.Name} GraphQL schema",
                    "Published schema: Not available. The runtime schema is observed via introspection during the review (not a published contract); contract compatibility is not assessed."));
        }
        if (authorization.Available > 0 && authorization.Ready < authorization.Available)
            limitations.Add(new("Authorization checks", "Optional · not ready — non-blocking. " + AuthorizationBlockerSummary(authorization)));
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
        var coverageNotes = limitations.Count(l => l.Area != "Authorization checks");
        var optional = limitations.Any(l => l.Area == "Authorization checks");
        // Neutral: contract/schema evidence that is not available and optional active checks never need "attention" before a read-only review.
        return targets
            + (coverageNotes > 0 ? $" Contract coverage: partial ({coverageNotes} coverage note{(coverageNotes == 1 ? "" : "s")})." : "")
            + (optional ? " Optional active checks unavailable — non-blocking." : "");
    }

    /// <summary>"2 API targets selected · 2/2 access ready".</summary>
    public static string Counts(IReadOnlyList<ApiReviewTargetReview> reviews) =>
        $"{reviews.Count} API target{(reviews.Count == 1 ? "" : "s")} selected · {reviews.Count(r => r.AccessReady)}/{reviews.Count} access available";

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
