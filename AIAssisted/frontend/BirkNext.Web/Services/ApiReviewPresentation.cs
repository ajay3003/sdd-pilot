using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure projection of the API Quality Review's authoritative state into the page's presentation records. Inputs: the active
/// <see cref="FrontendAnalysisContext"/>, the resolved <see cref="ApiReviewTarget"/> list, <see cref="AuthenticatedReviewCapabilities"/>,
/// <see cref="ApiReviewRunEligibility"/> (the ONE Run rule), <see cref="ApiReviewHistory"/> and the <see cref="ApiReviewReport"/>.
/// No I/O and no review decision of its own: whether Run is enabled, which access mode a target got and what a target's status is
/// are read from those sources and only translated into tester-facing wording.
/// </summary>
public static class ApiReviewPresentation
{
    public const string TargetEnvironmentsHref = ApiReviewRunEligibility.TargetEnvironmentsHref;
    public const string ReadOnlyTitle = "Read-only API review";
    public const string ReadOnlySummary = "The automated review does not execute write operations. Only safe requests are sent: REST GET, HEAD and OPTIONS, and GraphQL queries. Mutations and other write operations are listed for manual review and never called.";
    public const string SecurityScopeNote = "Passive, read-only security review. An empty list means no configured issue was detected on the assessed targets; it does not establish that the API is secure.";
    public const string PerformanceScopeNote = "API response timing observed by the review's own requests (backend gateway to the API). Not end-user or production performance.";

    /// <summary>Concise manual-review areas shown on the landing view; the report's own items are shown verbatim after a run.</summary>
    public static readonly IReadOnlyList<string> ManualReviewAreas =
    [
        "Write operations and side effects",
        "Authorization between roles and tenants",
        "Business correctness of returned data",
    ];

    /// <summary>Concise limitation summary; the exact report limitations remain available in the technical disclosure.</summary>
    public static readonly IReadOnlyList<string> LimitationSummary =
    [
        "Write behaviour and side effects",
        "Role-based authorization correctness",
        "Business correctness of returned data",
        "JSON value semantics beyond the recorded structure",
        "GraphQL mutation behaviour (never executed)",
    ];

    // ── Access ────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static ApiReviewAccessAvailability Availability(FrontendAnalysisContext? context, AuthenticatedReviewCapabilities? capabilities)
    {
        if (context is null || capabilities is null) return ApiReviewAccessAvailability.Loading;
        if (capabilities.AuthenticatedApi) return ApiReviewAccessAvailability.Available;
        return context.ActiveProfile.Authentication.AuthenticatedTestingMethod switch
        {
            AuthenticatedTestingMethod.ManualOnly => ApiReviewAccessAvailability.ManualOnly,
            AuthenticatedTestingMethod.LocalHttpsProxy => capabilities.ContextStatus == AuthenticatedApiContextStatus.Expired ? ApiReviewAccessAvailability.Expired : ApiReviewAccessAvailability.NotConnected,
            _ => ApiReviewAccessAvailability.NotSupportedByMethod,
        };
    }

    public static string ApiAccessLabel(ApiReviewAccessAvailability availability, bool anyTargetRequiresAuth) => availability switch
    {
        ApiReviewAccessAvailability.Loading => "Resolving…",
        ApiReviewAccessAvailability.Available => "Authenticated available",
        _ when !anyTargetRequiresAuth => "Public only",
        _ => "Authenticated unavailable",
    };

    public static ApiReviewTargetSummaryModel TargetSummary(FrontendAnalysisContext context, AuthenticatedReviewCapabilities? capabilities, IReadOnlyList<ApiReviewTarget> targets)
    {
        var availability = Availability(context, capabilities);
        var anyAuth = targets.Any(t => t.AuthRequired);
        var profile = context.ActiveProfile;
        var production = profile.EnvironmentType == FrontendEnvironmentType.Production;
        return new(
            string.IsNullOrWhiteSpace(profile.Name) ? "Unnamed environment" : profile.Name,
            FrontendQualityLandingPresentation.EnvironmentTypeLabel(profile.EnvironmentType),
            string.IsNullOrWhiteSpace(context.TargetUrl) ? "Not configured" : context.TargetUrl,
            FrontendQualityLandingPresentation.AuthenticationLabel(context.AuthenticationType, context.RequiresAuthentication),
            ApiAccessLabel(availability, anyAuth),
            availability,
            [
                new("Environment type", profile.EnvironmentType.ToString()),
                new("Environment ID", profile.Id),
                new("Authenticated testing method", AuthenticatedTestingMethodLabels.Option(profile.Authentication.AuthenticatedTestingMethod)),
                new("Review policy", production ? "Passive read-only review; unknown-route and invalid-query error probes are disabled in production" : "Read-only review with safe error probes (unknown route, invalid query)"),
                new("Request execution", "Safe requests only (REST GET/HEAD/OPTIONS, GraphQL queries). Authenticated requests are executed by the backend gateway; the review never receives a token."),
                new("Authenticated API context", capabilities is null ? "Resolving…" : ContextStatusLabel(capabilities.ContextStatus)),
            ]);
    }

    public static string ContextStatusLabel(AuthenticatedApiContextStatus status) => status switch
    {
        AuthenticatedApiContextStatus.Available => "Available (memory only)",
        AuthenticatedApiContextStatus.Expired => "Expired",
        AuthenticatedApiContextStatus.Stale => "Stale — environment changed",
        AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic => "Waiting for authenticated traffic",
        _ => "Not applicable",
    };

    public static ApiReviewAccessPanelModel AccessPanel(FrontendAnalysisContext? context, AuthenticatedReviewCapabilities? capabilities, IReadOnlyList<ApiReviewTarget> targets, IReadOnlyCollection<string> selected)
    {
        var availability = Availability(context, capabilities);
        var needed = targets.Any(t => selected.Contains(t.TargetId) && t.AuthRequired);
        var technical = new List<ApiReviewTechnicalField>();
        if (context is not null)
            technical.Add(new("Authenticated testing method", AuthenticatedTestingMethodLabels.Option(context.ActiveProfile.Authentication.AuthenticatedTestingMethod)));
        if (capabilities is not null)
        {
            technical.Add(new("Authenticated API context", ContextStatusLabel(capabilities.ContextStatus)));
            technical.Add(new("REST", capabilities.AuthenticatedRest ? "Authenticated REST traffic observed" : "No authenticated REST traffic observed"));
            technical.Add(new("GraphQL", capabilities.AuthenticatedGraphQlQuery ? "Authenticated GraphQL query traffic observed" : "No authenticated GraphQL traffic observed"));
            if (capabilities.ObservedHost is { Length: > 0 } host) technical.Add(new("Observed host", host));
            if (capabilities.Reason is { Length: > 0 } reason) technical.Add(new("Resolution", reason));
        }
        technical.Add(new("Execution", "Authenticated requests are executed by the backend gateway with the memory-only proxy credential (REST GET/HEAD/OPTIONS, GraphQL queries). The review itself never receives a token."));

        return availability switch
        {
            ApiReviewAccessAvailability.Loading => new(availability, needed, "Resolving whether authenticated API requests can be included…", [], null, TargetEnvironmentsHref, technical),
            ApiReviewAccessAvailability.Available => new(availability, needed,
                "Authenticated REST and GraphQL requests can be included. They are executed read-only through the existing secure gateway session.",
                [], "Manage authenticated session", TargetEnvironmentsHref, technical),
            ApiReviewAccessAvailability.ManualOnly => new(availability, needed,
                "This environment is manual-verification only. The review can inspect public API behaviour; authenticated APIs must be verified manually.",
                [], "Change authenticated testing method", TargetEnvironmentsHref, technical),
            ApiReviewAccessAvailability.NotSupportedByMethod => new(availability, needed,
                "The Managed Edge browser method provides no authenticated API execution. The review can inspect public API behaviour only.",
                ["Switch the Target Environment's authenticated testing method to the Local HTTPS proxy.", "Start the proxy, sign in to the target application and use it.", "Return here when the authenticated API context is available."],
                "Change authenticated testing method", TargetEnvironmentsHref, technical),
            _ => new(availability, needed,
                availability == ApiReviewAccessAvailability.Expired
                    ? "The authenticated session expired. The review can currently inspect public API behaviour only."
                    : "The review can currently inspect public API behaviour only.",
                [
                    "Start the Local HTTPS Proxy from the Target Environment page.",
                    "Sign in to the target application in the proxy-configured browser.",
                    "Use the application so authenticated API traffic is observed.",
                    "Return here when the authenticated API context is available.",
                ],
                "Manage authenticated session", TargetEnvironmentsHref, technical),
        };
    }

    // ── Readiness ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Readiness mirrors <see cref="ApiReviewRunEligibility"/>: Blocked iff Run is disabled; Limited when it runs without authenticated coverage it needs.</summary>
    public static ApiReviewReadinessModel Readiness(ApiReviewRunEligibility eligibility, FrontendAnalysisContext? context, IReadOnlyList<ApiReviewTarget> targets, IReadOnlyCollection<string> selected, AuthenticatedReviewCapabilities? capabilities)
    {
        var chosen = targets.Where(t => selected.Contains(t.TargetId)).ToList();
        if (context is null) return new(ApiReviewReadinessLevel.Loading, "Loading target environment", eligibility.Reason, chosen.Count, [], null, null);
        if (!eligibility.Enabled)
            return new(ApiReviewReadinessLevel.Blocked, "Review cannot start", eligibility.Reason, chosen.Count, [], eligibility.ActionText, eligibility.ActionHref ?? (eligibility.ActionText is null ? null : TargetEnvironmentsHref));

        var authenticated = capabilities?.AuthenticatedApi == true;
        var rest = chosen.Count(t => t.ApiType == ApiReviewTargetType.Rest);
        var gql = chosen.Count(t => t.ApiType == ApiReviewTargetType.GraphQl);
        var authTargets = chosen.Count(t => t.AuthRequired);
        var items = new List<ApiReviewReadinessItem>();
        if (rest > 0) items.Add(new($"{rest} REST {(rest == 1 ? "target" : "targets")} selected", ApiReviewReadinessItemState.Ok));
        if (gql > 0) items.Add(new($"{gql} GraphQL {(gql == 1 ? "target" : "targets")} selected", ApiReviewReadinessItemState.Ok));
        items.Add(new("Read-only review available", ApiReviewReadinessItemState.Ok));
        if (authTargets == 0)
            items.Add(new("No selected API requires authentication", ApiReviewReadinessItemState.Ok));
        else if (authenticated)
            items.Add(new($"Authenticated requests available for {authTargets} {(authTargets == 1 ? "target" : "targets")}", ApiReviewReadinessItemState.Ok));
        else
            items.Add(new($"Authenticated API context unavailable — {authTargets} {(authTargets == 1 ? "target" : "targets")} will be reported as authentication required", ApiReviewReadinessItemState.Warning));

        var limited = authTargets > 0 && !authenticated;
        return limited
            ? new(ApiReviewReadinessLevel.Limited, "Ready with limitations", $"{eligibility.Reason} The review can run now using public, read-only access; authenticated checks will be limited.", chosen.Count, items, "Manage authenticated session", TargetEnvironmentsHref)
            : new(ApiReviewReadinessLevel.Ready, "Ready to review", eligibility.Reason, chosen.Count, items, null, null);
    }

    // ── Targets ───────────────────────────────────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<ApiReviewTargetCardModel> TargetCards(IReadOnlyList<ApiReviewTarget> targets, ApiReviewTargetType type) =>
        targets.Where(t => t.ApiType == type).Select(TargetCard).ToList();

    public static ApiReviewTargetCardModel TargetCard(ApiReviewTarget target)
    {
        var writes = target.Operations.Count(o => !o.IsSafe);
        return new(
            target,
            DisplayName(target),
            target.BasePath,
            SourceLabel(target.Source),
            target.AuthRequired ? "Authentication required" : "Public",
            ConfidenceLabel(target.Confidence),
            target.Operations.Count,
            writes,
            target.ContractSource is not null,
            target.ApiType == ApiReviewTargetType.GraphQl ? "Runtime schema (introspected at run)" : null,
            target.Operations.Select(o => new ApiReviewOperationRowModel(
                o.OperationType != GraphQlOperationType.None ? o.OperationType.ToString() : o.Method,
                o.OperationType != GraphQlOperationType.None ? o.OperationName ?? "(anonymous)" : o.Path,
                o.AuthObserved ? "Authentication required" : "Public",
                o.ObservedCount > 0 ? $"{SourceLabel(o.Source)} · {o.ObservedCount}× observed" : SourceLabel(o.Source),
                o.IsSafe)).ToList());
    }

    /// <summary>Service name without the repeated "GraphQL · host/path" prefix the resolver uses as a unique name.</summary>
    public static string DisplayName(ApiReviewTarget target)
    {
        if (target.ApiType != ApiReviewTargetType.GraphQl) return target.ServiceName;
        var last = target.BasePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var configured = target.ServiceName.EndsWith("(configured)", StringComparison.Ordinal) ? " (configured)" : "";
        if (last.Length >= 2 && last[^1].Equals("graphql", StringComparison.OrdinalIgnoreCase))
            return $"{Capitalize(last[^2])} GraphQL{configured}";
        return $"GraphQL · {target.Host}{configured}";
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public static string SourceLabel(ApiReviewTargetSource source) => source switch
    {
        ApiReviewTargetSource.DiscoveredTraffic => "Discovered traffic",
        ApiReviewTargetSource.Configured => "Configured",
        ApiReviewTargetSource.Contract => "Contract",
        _ => source.ToString(),
    };

    public static string ConfidenceLabel(ObservedEndpointConfidence confidence) => confidence switch
    {
        ObservedEndpointConfidence.Verified => "Verified",
        ObservedEndpointConfidence.Candidate => "Candidate",
        _ => "Rejected",
    };

    // ── Contracts ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public static ApiReviewContractPanelModel Contracts(IReadOnlyList<ApiReviewTarget> targets, IReadOnlyCollection<string> selected, ApiReviewHistory? history, ApiReviewReport? lastReport)
    {
        var chosen = targets.Where(t => selected.Contains(t.TargetId)).ToList();
        var rest = chosen.Where(t => t.ApiType == ApiReviewTargetType.Rest).ToList();
        var gql = chosen.Where(t => t.ApiType == ApiReviewTargetType.GraphQl).ToList();
        var openApi = rest.Where(t => t.ContractSource is not null).Select(t => t.ContractSource!).Distinct().ToList();
        var rows = new List<ApiReviewContractRow>();

        if (rest.Count == 0) rows.Add(new("REST", ApiReviewContractState.NotApplicable, "No REST target selected."));
        else if (openApi.Count > 0) rows.Add(new("REST", ApiReviewContractState.Available, openApi.Count == 1 ? "OpenAPI contract configured; live responses are validated against it." : $"{openApi.Count} OpenAPI contracts configured; live responses are validated against them."));
        else rows.Add(new("REST", ApiReviewContractState.NotConfigured, "No OpenAPI contract configured. Live responses can still be reviewed structurally."));

        if (gql.Count == 0) rows.Add(new("GraphQL", ApiReviewContractState.NotApplicable, "No GraphQL target selected."));
        else
        {
            var lastGql = lastReport?.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.GraphQl && t.Contract is not null).ToList() ?? [];
            if (lastGql.Count > 0 && lastGql.All(t => t.Contract!.IntrospectionEnabled == false))
                rows.Add(new("GraphQL", ApiReviewContractState.IntrospectionUnavailable, "Introspection was not available in the latest review; observed operations cannot be matched to a schema."));
            else if (lastGql.Any(t => t.Contract!.Available))
                rows.Add(new("GraphQL", ApiReviewContractState.RuntimeSchema, "Schema available. Retrieved through runtime introspection in the latest review."));
            else
                rows.Add(new("GraphQL", ApiReviewContractState.RuntimeSchema, "Schema is retrieved through runtime introspection when the review runs."));
        }

        var baselines = history is null ? 0 : chosen.Count(t => history.Baselines.ContainsKey(t.TargetId));
        var runs = history?.Runs.Count ?? 0;
        var historyLabel = baselines switch { 0 => "No previous baseline", 1 => "1 previous baseline available", _ => $"{baselines} previous baselines available" };
        var latest = lastReport is null ? "Not compared yet"
            : lastReport.Findings.Any(f => f.Type == ApiReviewFindingType.Drift) ? "Drift detected in the latest review"
            : runs >= 2 ? "No drift detected in the latest review"
            : "Not compared yet (the first review records the baseline)";

        var details = new List<string>
        {
            "REST: a published OpenAPI contract enables contract validation of live responses (documented operations, response shapes). Without one the review records the observed JSON structure (paths and types, never values) and compares it with previous runs.",
            "GraphQL: the schema is retrieved by runtime introspection with a query-only request when the review runs. Disabled introspection is recorded as a policy observation, not as a failure; observed operations are then marked for manual review.",
            "Contract history: structural baselines from previous runs (contract hash, root fields, response shapes) are sent with the next run to detect drift. Baselines contain no values.",
        };
        details.AddRange(openApi.Select(u => $"OpenAPI source: {u}"));
        return new(rows, baselines, historyLabel, latest, details);
    }

    // ── Results ───────────────────────────────────────────────────────────────────────────────────────────────────────

    public static ApiReviewSummaryModel Summary(ApiReviewReport report)
    {
        var c = report.Coverage;
        var accessUsed = c.AuthenticatedExecuted > 0 && c.PublicExecuted > 0 ? "Mixed"
            : c.AuthenticatedExecuted > 0 ? "Authenticated"
            : c.PublicExecuted > 0 ? "Public only"
            : "None executed";
        var accessDetail = string.Join(" · ", new[]
        {
            c.AuthenticatedPlanned > 0 ? $"{c.AuthenticatedExecuted} of {c.AuthenticatedPlanned} authentication-required {(c.AuthenticatedPlanned == 1 ? "target" : "targets")} reviewed with authenticated requests" : null,
            c.PublicPlanned > 0 ? $"{c.PublicExecuted} of {c.PublicPlanned} public {(c.PublicPlanned == 1 ? "target" : "targets")} reviewed" : null,
            c.TargetsBlocked > 0 ? $"{c.TargetsBlocked} not executed" : null,
        }.Where(s => s is not null));

        var restContract = report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.Rest).Select(t => t.Contract).ToList();
        var gqlContract = report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.GraphQl).Select(t => t.Contract).ToList();
        var coverage = new List<ApiReviewCoverageRowModel>
        {
            new("REST", $"{c.RestOperationsReviewed} / {c.RestOperationsTotal} operations reviewed", null),
            new("GraphQL", $"{c.GraphQlOperationsObserved} observed operations", c.GraphQlOperationsObserved > 0 ? $"{c.GraphQlOperationsMatched} / {c.GraphQlOperationsObserved} matched to the runtime schema" : null),
            new("Contracts", ContractCoverageLabel(restContract, gqlContract), c.ContractChecks > 0 ? $"{c.ContractChecks} contract checks executed" : null),
            new("Security", $"{c.SecurityChecks} passive, read-only checks executed", null),
            new("Access", accessUsed, accessDetail),
        };
        if (c.UnsafeOperationsNotExecuted > 0)
            coverage.Add(new("Write operations", $"{c.UnsafeOperationsNotExecuted} listed, none executed", "Write operations are never executed by the automated review."));

        return new(
            report.Environment.Name,
            report.Environment.EnvironmentType,
            report.GeneratedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            report.RestServices,
            report.GraphQlServices,
            accessUsed,
            accessDetail,
            c.RestOperationsReviewed + report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.GraphQl).Sum(t => t.Operations.Count(o => o.Executed)),
            coverage,
            Enum.GetValues<ApiReviewSeverity>().Select(s => new ApiReviewSeverityCount(s, report.Findings.Count(f => f.Severity == s))).ToList(),
            c.TargetsBlocked);
    }

    private static string ContractCoverageLabel(IReadOnlyList<ApiReviewContractSummary?> rest, IReadOnlyList<ApiReviewContractSummary?> gql)
    {
        var parts = new List<string>();
        if (rest.Count > 0) parts.Add(rest.Any(x => x is { Available: true }) ? "REST contract validated" : "No REST contract configured");
        if (gql.Count > 0)
            parts.Add(gql.Any(x => x is { Available: true }) ? "GraphQL runtime schema available"
                : gql.Any(x => x is { IntrospectionEnabled: false }) ? "GraphQL introspection unavailable" : "GraphQL schema not retrieved");
        return parts.Count == 0 ? "No contract evidence" : string.Join(" · ", parts);
    }

    /// <summary>Top findings for the Overview tab: severity first, then type, capped.</summary>
    public static IReadOnlyList<ApiReviewFinding> KeyFindings(ApiReviewReport report, int max = 5) =>
        report.Findings.OrderBy(f => f.Severity).ThenBy(f => f.Type).ThenBy(f => f.Title).Take(max).ToList();

    public static string ServiceNameOf(ApiReviewReport report, string targetId) =>
        report.Targets.FirstOrDefault(t => t.Target.TargetId == targetId) is { } t ? DisplayName(t.Target) : "";

    // ── Review scope ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Counted from the selected targets themselves, so the summary cannot drift from the list it summarises.</summary>
    public static ApiReviewScopeSummary Scope(IReadOnlyList<ApiReviewTarget> targets, IReadOnlyCollection<string> selected)
    {
        var chosen = targets.Where(t => selected.Contains(t.TargetId)).ToList();
        return new ApiReviewScopeSummary(
            Selected: chosen.Count,
            Rest: chosen.Count(t => t.ApiType == ApiReviewTargetType.Rest),
            GraphQl: chosen.Count(t => t.ApiType == ApiReviewTargetType.GraphQl),
            AuthRequired: chosen.Count(t => t.AuthRequired),
            Operations: chosen.Sum(t => TargetCard(t).OperationCount));
    }

    // ── What will be reviewed ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The review domains, in review-scope words. Two rules this exists to keep:
    ///
    /// <list type="bullet">
    /// <item>A missing contract limits the CONTRACTS domain. REST is still reviewed structurally against live responses,
    /// so "No contract configured" never removes REST from the review.</item>
    /// <item>Refused introspection limits GraphQL's contract evidence. Observed operations are still reviewed, so it never
    /// removes GraphQL from the review either.</item>
    /// </list>
    ///
    /// Access state ("Unavailable", "Not connected") is never a domain state; it appears at most as a limitation.
    /// </summary>
    public static IReadOnlyList<ApiReviewDomainCard> Domains(
        ApiReviewScopeSummary scope,
        ApiReviewContractPanelModel contracts,
        ApiReviewAccessAvailability availability)
    {
        var nothingSelected = scope.Selected == 0;
        var authLimited = scope.AuthRequired > 0 && availability != ApiReviewAccessAvailability.Available;
        var authLimitation = authLimited
            ? $"{scope.AuthRequired} selected target{(scope.AuthRequired == 1 ? "" : "s")} require authenticated access, which is unavailable; those endpoints are reported as authentication required."
            : null;

        ApiReviewDomainState Scoped(bool present, bool limited) =>
            nothingSelected || !present ? ApiReviewDomainState.NotIncluded
            : limited ? ApiReviewDomainState.Limited
            : ApiReviewDomainState.Included;

        var restContract = contracts.Rows.FirstOrDefault(r => r.Label == "REST")?.State;
        var gqlContract = contracts.Rows.FirstOrDefault(r => r.Label == "GraphQL")?.State;
        var contractLimits = new[]
        {
            restContract == ApiReviewContractState.NotConfigured ? "No REST contract is configured, so REST is reviewed against live responses rather than a published contract." : null,
            gqlContract == ApiReviewContractState.IntrospectionUnavailable ? "GraphQL introspection is unavailable, so schema comparison is limited to observed operations." : null,
        }.Where(l => l is not null).ToList();

        return
        [
            new("security", "Security", "Passive, read-only security review of responses, headers and exposure.",
                Scoped(true, authLimited), authLimitation),

            new("contracts", "Contracts", "Published contracts and schemas, compared against previous baselines.",
                nothingSelected ? ApiReviewDomainState.NotIncluded
                    : contractLimits.Count > 0 ? ApiReviewDomainState.Limited
                    : ApiReviewDomainState.Included,
                contractLimits.Count > 0 ? string.Join(" ", contractLimits) : null),

            new("errors", "Error handling", "How the API answers safe, read-only requests it cannot satisfy.",
                Scoped(true, false),
                nothingSelected ? null : "Robustness is judged from safe requests only; no write or destructive request is ever sent."),

            new("performance", "Performance", "Response timing observed by the review's own read-only requests.",
                Scoped(true, false),
                nothingSelected ? null : "Timing is measured from the backend gateway, not from an end user."),

            new("rest", "REST", "Routes, status handling and structure of the selected REST APIs.",
                Scoped(scope.Rest > 0, restContract == ApiReviewContractState.NotConfigured),
                scope.Rest > 0 && restContract == ApiReviewContractState.NotConfigured
                    ? "Reviewed structurally; no published contract is available to compare against."
                    : null),

            new("graphql", "GraphQL", "Operations, schema evidence and error behaviour of the selected GraphQL APIs.",
                Scoped(scope.GraphQl > 0, gqlContract == ApiReviewContractState.IntrospectionUnavailable),
                scope.GraphQl > 0 && gqlContract == ApiReviewContractState.IntrospectionUnavailable
                    ? "Observed operations are reviewed; the schema itself could not be retrieved."
                    : null),
        ];
    }

    // ── Result ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How the completed review ended, derived only from what the report records. Execution outranks quality: a review
    /// where nothing could be reached says so rather than reporting an encouraging absence of findings.
    /// </summary>
    public static ApiReviewResultView Result(ApiReviewReport report)
    {
        var summary = Summary(report);
        var statuses = report.Targets.Select(ApiReviewStatusLabels.Of).ToList();
        var assessed = statuses.Count(s => s is ApiReviewTargetPresentationStatus.Assessed or ApiReviewTargetPresentationStatus.PartiallyAssessed);
        var blocked = statuses.Count - assessed;

        var state =
            report.Targets.Count == 0 || assessed == 0 ? ApiReviewResultState.FailedToRun
            : blocked > 0 ? ApiReviewResultState.PartialCoverage
            : statuses.Any(s => s == ApiReviewTargetPresentationStatus.PartiallyAssessed) ? ApiReviewResultState.CompletedWithLimitations
            : report.ManualReviewItems.Count > 0 ? ApiReviewResultState.CompletedWithManualReview
            : ApiReviewResultState.Completed;

        var findings = report.Findings.Count;
        var services = $"{assessed} of {report.Targets.Count} selected service{(report.Targets.Count == 1 ? "" : "s")}";
        var text = state switch
        {
            ApiReviewResultState.FailedToRun =>
                "No selected API target could be reviewed, so nothing can be concluded about them.",
            ApiReviewResultState.PartialCoverage =>
                $"{findings} finding{(findings == 1 ? "" : "s")} across {services}. {blocked} could not be reached and {(blocked == 1 ? "is" : "are")} reported as such, never as a pass.",
            ApiReviewResultState.CompletedWithLimitations =>
                $"{findings} finding{(findings == 1 ? "" : "s")} across {services}; some were reviewed under reduced access.",
            ApiReviewResultState.CompletedWithManualReview =>
                $"{findings} finding{(findings == 1 ? "" : "s")} across {services}. Parts of this API can only be reviewed by a person.",
            _ => $"{findings} finding{(findings == 1 ? "" : "s")} across {services}.",
        };

        return new ApiReviewResultView(state, text, findings, report.ManualReviewItems.Count, assessed, blocked, summary);
    }
}
