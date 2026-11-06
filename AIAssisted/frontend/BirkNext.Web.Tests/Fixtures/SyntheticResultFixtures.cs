using System.Text.Json;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.ManualReview;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Microsoft.JSInterop;

namespace BirkNext.Web.Tests.Fixtures;

/// <summary>
/// Deterministic, explicitly SYNTHETIC post-run results for localhost-only browser checks of the Frontend Quality Review and API Quality
/// Review result workspaces. Everything is built from the real model types and written through the real browser stores
/// (<see cref="FrontendAnalysisSettingsService"/>, <see cref="FrontendQualityReviewHistory"/>, <see cref="ApiReviewHistoryService"/>) into a
/// captured local storage, so the browser reads exactly what those stores would have written. The fixture is seeded into a fresh,
/// disposable browser context only: it never touches the backend database or a person's browser profile.
///
/// Safety: one synthetic Target Environment whose target is the loopback discard port (http://127.0.0.1:9 — nothing listens, no DNS, no
/// external host); no credential, token or personal data; every title is marked [synthetic]. These are stored review results of a target
/// that does not exist — they claim no runtime verification of any real system.
/// </summary>
public static class SyntheticResultFixtures
{
    public const string ProfileId = "synthetic-fixture-0001";
    public const string ProfileName = "SYNTHETIC FIXTURE (localhost only)";
    public const string TargetUrl = "http://127.0.0.1:9/synthetic-fixture/";
    public const string Host = "127.0.0.1";
    private static readonly DateTime T0 = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Local storage keys and values to seed, plus the manual decisions the page would list (served by the browser test, not stored).</summary>
    public sealed record Fixture(IReadOnlyDictionary<string, string> LocalStorage, IReadOnlyList<ManualReviewDecisionView> ApiManualDecisions);

    public static async Task<Fixture> BuildAsync()
    {
        var js = new CapturingStorage();

        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(js);
        foreach (var seed in settings.Settings.Profiles.Select(p => p.Id).ToList()) settings.DeleteProfile(seed);
        var profile = settings.CreateProfile(ProfileName, FrontendEnvironmentType.Development);
        profile.Id = ProfileId;
        profile.TargetUrl = TargetUrl;
        profile.Description = "Synthetic fixture for localhost-only browser checks. Not a real environment.";
        settings.SelectActiveProfile(ProfileId);
        await settings.SaveAsync(js);

        // Oldest first: the history store keeps the newest first.
        var fqr = new FrontendQualityReviewHistory(js);
        foreach (var report in FrontendRuns()) await fqr.AddAsync(ProfileId, report);
        // The store mints a random id per run; the fixture fixes them (newest first) so it is byte-for-byte repeatable.
        var key = "birknext:fqr-history:" + ProfileId;
        var runs = System.Text.Json.Nodes.JsonNode.Parse(js.Items[key])!.AsArray();
        for (var i = 0; i < runs.Count; i++) runs[i]!["id"] = $"synthetic-run-{runs.Count - i}";
        js.Items[key] = runs.ToJsonString();

        var aqr = new ApiReviewHistoryService();
        await aqr.LoadAsync(js);
        await aqr.RecordAsync(js, ProfileId, ApiReport(T0.AddDays(-1), blockedGraphQl: true));
        await aqr.RecordAsync(js, ProfileId, ApiReport(T0, blockedGraphQl: false));

        return new Fixture(js.Items, ApiManualDecisions());
    }

    /// <summary>The fixture as one JSON document for the browser test (local storage entries + manual decisions in the API's wire format).</summary>
    public static async Task<string> ToJsonAsync()
    {
        var fixture = await BuildAsync();
        return JsonSerializer.Serialize(new
        {
            synthetic = true,
            profileId = ProfileId,
            targetUrl = TargetUrl,
            localStorage = fixture.LocalStorage,
            apiManualDecisions = fixture.ApiManualDecisions,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }

    // ── Frontend Quality Review: four runs, oldest first ─────────────────────────────────────────────────────────────────

    internal static IReadOnlyList<FrontendQualityReviewReport> FrontendRuns() =>
    [
        // Preflight: the target did not answer, nothing was reviewed (not executed is not passed).
        FrontendReport(T0.AddDays(-3), requiredCompleted: false, findings: [], preflight: PreflightStatus.Unreachable),
        // No automated block detected — still never a pass; manual assessment remains.
        FrontendReport(T0.AddDays(-2), requiredCompleted: true, findings: [Finding("perf-js", "Large application JavaScript payload", FrontendQualityCategory.Performance, FrontendQualitySeverity.Low)]),
        // Blocked: a required engine did not complete (coverage blocker) and the CSP issue policy blocks.
        FrontendReport(T0.AddDays(-1), requiredCompleted: false, findings: RichFindings(), policyBlocksCsp: true),
        // Latest: review required, partial coverage (authenticated DOM not assessed), WCAG evidence, derived and informational items.
        FrontendReport(T0, requiredCompleted: true, findings: RichFindings(), wcag: true, manualItems: true),
    ];

    private static FrontendQualityFinding Finding(string id, string title, FrontendQualityCategory category, FrontendQualitySeverity severity,
        FrontendQualityEngineId engine = FrontendQualityEngineId.StaticSecurity, string? rule = null, string? page = null,
        FrontendQualityFindingOrigin origin = FrontendQualityFindingOrigin.Source) => new()
        {
            Id = id, Title = $"[synthetic] {title}{(page is null ? "" : $" — {page}")}", Category = category, Severity = severity,
            Description = $"Synthetic fixture finding: {title}.", Recommendation = $"Resolve {title}.",
            Evidence = page is null ? [] : [$"Page: {page}"], SourceSystem = engine.ToString(), EngineId = engine, SourceRuleId = rule,
            Status = CheckExecutionStatus.Failed, Origin = origin,
        };

    private static List<FrontendQualityFinding> RichFindings()
    {
        var findings = new List<FrontendQualityFinding>();
        findings.AddRange(new[] { "/", "/synthetic/a", "/synthetic/b" }.Select((route, i) => Finding($"contrast-{i}", "Contrast (Minimum)",
            FrontendQualityCategory.Accessibility, FrontendQualitySeverity.High, FrontendQualityEngineId.BrowserQuality, "a11y-contrast", route)));
        findings.Add(Finding("sec-csp", "Missing security header: Content-Security-Policy", FrontendQualityCategory.Security, FrontendQualitySeverity.High,
            rule: "HDR-MISSING-CONTENT-SECURITY-POLICY"));
        findings.Add(Finding("std-csp", "Content-Security-Policy header missing", FrontendQualityCategory.Standards, FrontendQualitySeverity.Critical, rule: "std-csp-missing"));
        findings.Add(Finding("perf-api", "Repeated API call", FrontendQualityCategory.Performance, FrontendQualitySeverity.Medium, FrontendQualityEngineId.PerformanceQuality, "pq-repeated-rest", "/"));
        findings.Add(Finding("std-lang", "Document language not declared", FrontendQualityCategory.Standards, FrontendQualitySeverity.Info));
        findings.Add(Finding("rdy-1", "Missing security headers raise release risk", FrontendQualityCategory.Readiness, FrontendQualitySeverity.Low,
            origin: FrontendQualityFindingOrigin.Derived));
        return findings;
    }

    private static FrontendQualitySurfacePlan Plan(FrontendQualityReviewSurface surface, FrontendQualityPlanDecision decision, bool primary, string reason = "") =>
        new(surface, decision, "Public HTTP", decision == FrontendQualityPlanDecision.Run ? "Backend HTTP" : null, reason, primary);

    private static FrontendQualitySurfaceResult Surface(FrontendQualityReviewSurface surface, bool completed, bool primary, string reason = "") =>
        new(surface, completed ? "Completed" : "Not assessed", completed, completed ? "Backend HTTP" : null, reason, primary);

    private static FrontendQualityReviewReport FrontendReport(DateTime at, bool requiredCompleted, List<FrontendQualityFinding> findings,
        bool policyBlocksCsp = false, bool wcag = false, bool manualItems = false, PreflightStatus? preflight = null)
    {
        const string noAuthDom = "Authenticated browser DOM unavailable: no signed-in browser context for this synthetic run.";
        FrontendQualityEnginePlan Engine(FrontendQualityEngineId id, string name, FrontendQualityEngineRequirement policy, params FrontendQualitySurfacePlan[] surfaces) => new()
        {
            EngineId = id, DisplayName = name, Enabled = true, Policy = policy, Scope = FrontendReviewAccessScope.PublicAndAuthenticated, Prerequisite = "Public HTTP",
            Surfaces = surfaces, Applicable = true,
            Decision = surfaces.Any(s => s.Decision == FrontendQualityPlanDecision.Run) ? FrontendQualityPlanDecision.Run : FrontendQualityPlanDecision.NotAssessed,
        };
        var plan = new FrontendQualityExecutionPlan
        {
            Scope = FrontendReviewAccessScope.PublicAndAuthenticated,
            Engines =
            [
                Engine(FrontendQualityEngineId.StaticSecurity, "HTTP Security", FrontendQualityEngineRequirement.Required, Plan(FrontendQualityReviewSurface.Public, FrontendQualityPlanDecision.Run, true)),
                Engine(FrontendQualityEngineId.PassivePerformance, "HTTP Performance", FrontendQualityEngineRequirement.Required, Plan(FrontendQualityReviewSurface.Public, FrontendQualityPlanDecision.Run, true)),
                Engine(FrontendQualityEngineId.BrowserQuality, "Browser Quality", FrontendQualityEngineRequirement.Optional,
                    Plan(FrontendQualityReviewSurface.Public, FrontendQualityPlanDecision.Run, true),
                    Plan(FrontendQualityReviewSurface.Authenticated, FrontendQualityPlanDecision.NotAssessed, false, noAuthDom)),
                Engine(FrontendQualityEngineId.Lighthouse, "Lighthouse", FrontendQualityEngineRequirement.Optional,
                    Plan(FrontendQualityReviewSurface.Public, FrontendQualityPlanDecision.NotAssessed, true, "Lighthouse is not installed on this synthetic host.")),
            ],
        };
        FrontendQualityEngineOutcome Outcome(FrontendQualityEngineId id, string name, FrontendQualityEngineRequirement policy, FrontendQualityEngineExecutionState state, params FrontendQualitySurfaceResult[] surfaces) => new()
        {
            EngineId = id, DisplayName = name, Enabled = true, Requirement = policy, ExecutionState = state, Surfaces = [.. surfaces],
        };
        var outcomes = new List<FrontendQualityEngineOutcome>
        {
            Outcome(FrontendQualityEngineId.StaticSecurity, "HTTP Security", FrontendQualityEngineRequirement.Required,
                requiredCompleted ? FrontendQualityEngineExecutionState.Assessed : FrontendQualityEngineExecutionState.Unavailable,
                Surface(FrontendQualityReviewSurface.Public, requiredCompleted, true, requiredCompleted ? "" : "Synthetic target did not answer over public HTTP.")),
            Outcome(FrontendQualityEngineId.PassivePerformance, "HTTP Performance", FrontendQualityEngineRequirement.Required,
                preflight is null ? FrontendQualityEngineExecutionState.Assessed : FrontendQualityEngineExecutionState.Unavailable,
                Surface(FrontendQualityReviewSurface.Public, preflight is null, true, preflight is null ? "" : "Synthetic target did not answer.")),
            Outcome(FrontendQualityEngineId.BrowserQuality, "Browser Quality", FrontendQualityEngineRequirement.Optional,
                preflight is null ? FrontendQualityEngineExecutionState.Assessed : FrontendQualityEngineExecutionState.Unavailable,
                Surface(FrontendQualityReviewSurface.Public, preflight is null, true), Surface(FrontendQualityReviewSurface.Authenticated, false, false, noAuthDom)),
            Outcome(FrontendQualityEngineId.Lighthouse, "Lighthouse", FrontendQualityEngineRequirement.Optional, FrontendQualityEngineExecutionState.Unavailable,
                Surface(FrontendQualityReviewSurface.Public, false, true, "Lighthouse is not installed on this synthetic host.")),
        };
        var issues = FrontendQualityLogicalIssueGrouper.Group(findings);
        var blockers = new List<FrontendQualityReleaseBlocker>();
        if (!requiredCompleted)
            blockers.Add(new(FrontendQualityBlockerType.Coverage, "HTTP Security (Public)", "Synthetic target did not answer over public HTTP.", "Required engines must complete."));
        if (policyBlocksCsp && issues.FirstOrDefault(i => i.Category == FrontendQualityCategory.Security) is { } csp)
            blockers.Add(new(FrontendQualityBlockerType.IssuePolicy, csp.CanonicalTitle, "Synthetic policy: a missing CSP blocks release.", FrontendQualityDecisionSupportService.IssuePolicyRule));
        return new FrontendQualityReviewReport
        {
            TargetUrl = TargetUrl, GeneratedAt = at, CompletedAt = at.AddMinutes(2),
            TargetEnvironment = new FrontendReviewTargetIdentity(ProfileId, ProfileName, "Development", TargetUrl, at),
            ExecutionPlan = plan, EngineOutcomes = outcomes, Coverage = FrontendQualityCoverage.Evaluate(outcomes, plan),
            ReleaseBlockers = blockers,
            ReleaseDisposition = blockers.Count > 0 ? FrontendQualityReleaseDisposition.Blocked
                : findings.Count > 1 ? FrontendQualityReleaseDisposition.ReviewRequired : FrontendQualityReleaseDisposition.NoAutomatedBlockDetected,
            Findings = findings, LogicalIssues = issues,
            ManualReviewItems = manualItems
                ? [new() { Title = "[synthetic] Keyboard operation of the main menu", Reason = "Automated checks cannot judge keyboard operability.", Source = "Accessibility" }]
                : [],
            Wcag = wcag
                ? new WcagAssessment
                {
                    Profile = WcagProfiles.Norwegian, PagesWithEvidence = 3,
                    Results = [new WcagCriterionResult { Definition = WcagRegistry.All.Single(d => d.CriterionId == "1.4.3"), Page = "/", Status = WcagStatus.Fail, LastTested = new DateTimeOffset(at) }],
                }
                : null,
            PreflightStatus = preflight,
            PreflightMessage = preflight is null ? null : "Synthetic target did not answer (loopback discard port).",
            Limitations = ["Synthetic fixture: no real system was reviewed."],
        };
    }

    // ── API Quality Review: two runs (the latest is the current result for the synthetic environment) ─────────────────

    private static ApiReviewTarget Target(string id, ApiReviewTargetType type, string path, string name) => new()
    {
        TargetId = id, ApiType = type, Scheme = "http", Host = Host, Port = 9, BasePath = path, ServiceName = name, AuthRequired = true,
        Source = ApiReviewTargetSource.Configured, Confidence = ObservedEndpointConfidence.Verified, Selected = true,
    };

    private static ApiReviewFinding ApiFinding(string id, string rule, string target, ApiReviewSeverity severity, ApiReviewFindingType type, string title,
        ApiReviewCheckResult result = ApiReviewCheckResult.Warning) => new()
        {
            Id = id, RuleId = rule, TargetId = target, Severity = severity, Type = type, Endpoint = $"http://{Host}:9/synthetic-fixture", Check = title,
            Title = $"[synthetic] {title}", Description = $"Synthetic fixture finding: {title}.", Recommendation = $"Fix: {title}", Result = result,
            Evidence = [$"Synthetic evidence for {id}"],
        };

    internal static ApiReviewReport ApiReport(DateTime at, bool blockedGraphQl)
    {
        var rest = new ApiReviewTargetResult
        {
            Target = Target("syn-rest", ApiReviewTargetType.Rest, "/synthetic-fixture/api", "Synthetic REST API"),
            AccessMode = ApiReviewAccessMode.AuthenticatedHttp, Status = ApiReviewTargetStatus.PartiallyCompleted, FindingCount = 4,
            Operations =
            [
                new() { Display = "GET /synthetic-fixture/api/items", Method = "GET", Path = "/synthetic-fixture/api/items", Executed = true, StatusCode = 200, ElapsedMs = 142,
                    Checks = [new() { CheckId = "rest-latency", Area = ApiReviewFindingType.Performance, Result = ApiReviewCheckResult.Pass, Detail = "142 ms (warning > 800 ms)" }] },
                new() { Display = "GET /synthetic-fixture/api/items/{id}", Method = "GET", Path = "/synthetic-fixture/api/items/1", Executed = true, StatusCode = 500, ElapsedMs = 1210,
                    Checks = [new() { CheckId = "rest-latency", Area = ApiReviewFindingType.Performance, Result = ApiReviewCheckResult.Warning, Detail = "1210 ms (warning > 800 ms)" }] },
                new() { Display = "POST /synthetic-fixture/api/items", Method = "POST", Path = "/synthetic-fixture/api/items", Executed = false, Result = ApiReviewCheckResult.NotTested },
            ],
            Checks =
            [
                new() { CheckId = "sec-hsts", Area = ApiReviewFindingType.Security, Result = ApiReviewCheckResult.Warning, Title = "HSTS" },
                new() { CheckId = "errors-unknown-route", Area = ApiReviewFindingType.Errors, Result = ApiReviewCheckResult.Pass, Title = "Unknown route returns 404" },
                new() { CheckId = "errors-500", Area = ApiReviewFindingType.Errors, Result = ApiReviewCheckResult.Fail, Title = "Server error on a read operation" },
                new() { CheckId = "errors-method", Area = ApiReviewFindingType.Errors, Result = ApiReviewCheckResult.NotTested, Title = "Unsupported method", Detail = "Not tested: write methods are never sent." },
                new() { CheckId = "drift-shape", Area = ApiReviewFindingType.Drift, Result = ApiReviewCheckResult.Pass, Title = "Response shape drift" },
            ],
            Contract = new() { Kind = "OpenAPI", Available = false, Note = "No OpenAPI document is configured for the synthetic service." },
            AuthenticationEnforcement = new() { Status = ApiAuthenticationEnforcementStatus.Verified, Probe = "GET /synthetic-fixture/api/items", StatusCode = 401, Reason = "Refused without a credential." },
        };
        var gql = new ApiReviewTargetResult
        {
            Target = Target("syn-gql", ApiReviewTargetType.GraphQl, "/synthetic-fixture/graphql", "Synthetic GraphQL API"),
            AccessMode = blockedGraphQl ? ApiReviewAccessMode.Unavailable : ApiReviewAccessMode.AuthenticatedHttp,
            Status = blockedGraphQl ? ApiReviewTargetStatus.Blocked : ApiReviewTargetStatus.Completed,
            AccessReason = blockedGraphQl ? "Authentication required: no attended session for this synthetic run." : null,
            FindingCount = blockedGraphQl ? null : 1,
            Operations = blockedGraphQl ? [] :
            [
                new() { Display = "query { __typename }", Method = "POST", Path = "/synthetic-fixture/graphql", Executed = true, StatusCode = 200, ElapsedMs = 95, OperationType = GraphQlOperationType.Query },
            ],
            Contract = new() { Kind = "GraphQL schema", Available = false, IntrospectionEnabled = false, Note = "Introspection disabled on the synthetic service." },
            Checks = blockedGraphQl ? [] :
            [
                new() { CheckId = "gql-perf", Area = ApiReviewFindingType.Performance, Result = ApiReviewCheckResult.NotTested, Title = "GraphQL business response timing",
                    Detail = "Not tested: not enough representative GraphQL business responses." },
            ],
            AuthenticationEnforcement = blockedGraphQl ? null : new() { Status = ApiAuthenticationEnforcementStatus.NotVerified, Probe = "query { __typename }", Reason = "Not verified in this synthetic run." },
        };
        return new ApiReviewReport
        {
            Environment = new() { EnvironmentId = ProfileId, Name = ProfileName, EnvironmentType = "Development", TargetUrl = TargetUrl },
            GeneratedAt = new DateTimeOffset(at),
            Targets = [rest, gql],
            Findings =
            [
                ApiFinding("syn-hsts-1", "sec-no-hsts", "syn-rest", ApiReviewSeverity.Low, ApiReviewFindingType.Security, "HSTS header missing on API responses"),
                ApiFinding("syn-500", "err-server-error", "syn-rest", ApiReviewSeverity.High, ApiReviewFindingType.Errors, "Read operation returned HTTP 500", ApiReviewCheckResult.Fail),
                ApiFinding("syn-pd", "err-no-problem-details", "syn-rest", ApiReviewSeverity.Low, ApiReviewFindingType.Errors, "Error responses do not use ProblemDetails"),
                ApiFinding("syn-slow", "perf-slow", "syn-rest", ApiReviewSeverity.Medium, ApiReviewFindingType.Performance, "Single request slower than the warning threshold"),
                .. blockedGraphQl ? Array.Empty<ApiReviewFinding>() : [ApiFinding("syn-hsts-2", "sec-no-hsts", "syn-gql", ApiReviewSeverity.Low, ApiReviewFindingType.Security, "HSTS header missing on API responses")],
            ],
            Coverage = new()
            {
                RestOperationsTotal = 3, RestOperationsReviewed = 2, AuthenticatedPlanned = 2, AuthenticatedExecuted = blockedGraphQl ? 1 : 2,
                TargetsCompleted = blockedGraphQl ? 0 : 1, TargetsBlocked = blockedGraphQl ? 1 : 0, UnsafeOperationsNotExecuted = 1, SecurityChecks = 2,
            },
            ManualReviewItems = ["Write operations: behaviour and side effects.", "Access control between roles/tenants.", "Business correctness of returned data."],
        };
    }

    /// <summary>A current decision and a superseded one for the first obligation (audit history), and one for the second; none for the rest.</summary>
    internal static IReadOnlyList<ManualReviewDecisionView> ApiManualDecisions()
    {
        var areas = ApiReviewPresentation.ManualReviewAreas;
        ManualReviewDecisionView D(int n, string area, ManualReviewDecisionValue value, string note, bool current, int hours) => new()
        {
            Id = new Guid($"00000000-0000-0000-0000-{n:000000000000}"), Module = ManualReviewModules.ApiQuality, ScopeKey = ProfileId, SubjectKey = area,
            Reviewer = "Synthetic reviewer", Decision = value, Note = note, EvidenceReferences = ["synthetic-evidence-ref"],
            AppliesTo = "Synthetic REST API, Synthetic GraphQL API", DecidedAt = new DateTimeOffset(T0).AddHours(hours), Current = current,
        };
        return
        [
            D(1, areas[0], ManualReviewDecisionValue.Acceptable, "[synthetic] Reviewed the write operations against the specification.", true, 3),
            D(2, areas[0], ManualReviewDecisionValue.NeedsEvidence, "[synthetic] Earlier decision, superseded.", false, 1),
            D(3, areas[1], ManualReviewDecisionValue.IssueFound, "[synthetic] A role can read another tenant's list.", true, 2),
        ];
    }

    /// <summary>A stand-in for the browser's birkNextStorage interop: getItem/setItem/removeItem over a dictionary.</summary>
    internal sealed class CapturingStorage(IReadOnlyDictionary<string, string>? seed = null) : IJSRuntime
    {
        public SortedDictionary<string, string> Items { get; } = seed is null ? new(StringComparer.Ordinal) : new(seed.ToDictionary(), StringComparer.Ordinal);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            var key = args is { Length: > 0 } ? args[0]?.ToString() ?? "" : "";
            switch (identifier)
            {
                case "birkNextStorage.setItem": Items[key] = args![1]?.ToString() ?? ""; return ValueTask.FromResult(default(TValue)!);
                case "birkNextStorage.removeItem": Items.Remove(key); return ValueTask.FromResult(default(TValue)!);
                case "birkNextStorage.getItem": return ValueTask.FromResult((TValue)(object?)(Items.TryGetValue(key, out var value) ? value : null)!);
                default: return ValueTask.FromResult(default(TValue)!);
            }
        }
    }
}
