using BirkNext.CriticalE2E;
using BirkNext.Governance;

namespace BirkNext.Api.Services.CriticalE2E;

/// <summary>
/// Reads the authoritative Critical E2E gate evaluation (<see cref="ICriticalE2EService.ReleaseGate"/>, the same code the Critical E2E page shows)
/// into a release readiness dimension. It does not evaluate the gate: it maps the gate's verdict, policy version and per-flow eligibility, and refuses
/// to read an evaluation of another build or environment as this readiness's evidence (Stale — never Passed).
///
/// State mapping (gate disposition → readiness):
///   Ready → Passed (Ready, Required) · Blocked → Blocked (Blocked, Required) · Incomplete / ReviewRequired / required flows without Business E2E
///   → Incomplete (Not assessed, Required) · NotEvaluated (no build) → Not assessed (Required) · no required flows → Not assessed (Optional)
///   · evaluated build/environment ≠ readiness build/environment → Stale (Not assessed, Required).
/// Pure: no clock, no storage.
/// </summary>
public static class CriticalE2EReleaseReadiness
{
    public const string Title = "Critical E2E";
    public const string Page = "critical-e2e-regression";

    /// <summary>The drill-down: the Critical E2E page for the same environment and build.</summary>
    public static string DrillDown(string environmentId, string? buildId) =>
        $"{Page}?environment={Uri.EscapeDataString(environmentId)}{(string.IsNullOrWhiteSpace(buildId) ? "" : $"&build={Uri.EscapeDataString(buildId.Trim())}")}";

    /// <summary>The gate request for a readiness: its environment (the canonical scope is the Target Environment profile id) and build. No profile id, so
    /// reading the gate never wakes the Browser Companion.</summary>
    public static CriticalE2EOverviewRequest Request(string environmentId, ReleaseEvaluationContext? context) => new()
    {
        EnvironmentId = environmentId,
        EnvironmentName = context?.EnvironmentName ?? "",
        EnvironmentType = context?.EnvironmentType,
        BuildId = Trim(context?.BuildId),
        BuildSource = Trim(context?.BuildId) is null ? null : "Manual",
    };

    /// <summary>Maps one gate evaluation for the readiness's environment and build.</summary>
    public static CriticalE2EReadinessEvaluation Evaluate(CriticalE2EOverview gate, string environmentId, string? buildId, DateTimeOffset evaluatedAt)
    {
        var release = gate.Release;
        buildId = Trim(buildId);
        var stale = !Same(release.EnvironmentId, environmentId)
            ? $"The Critical E2E evaluation is for environment {Show(release.EnvironmentId)}, not this readiness's environment {Show(environmentId)}."
            : !Same(release.BuildId, buildId)
                ? $"The Critical E2E evaluation is for build {Show(release.BuildId)}, not this readiness's build {Show(buildId)}."
                : null;
        var blocking = release.BlockingFlows.GroupBy(b => b.FlowId).ToDictionary(g => g.Key, g => g.First().Reason);
        var byId = gate.Flows.ToDictionary(f => f.FlowId, StringComparer.Ordinal);
        var flows = release.RequiredFlowIds.Where(byId.ContainsKey).Select(id => byId[id]).Select(f => new CriticalE2EReadinessFlow
        {
            FlowId = f.FlowId,
            Name = f.Name,
            Module = f.Module,
            TestType = f.TestType,
            Criticality = f.Kind,
            RequiredForRelease = f.RequiredForRelease,
            ReleaseReady = f.Readiness.ReleaseReady,
            Readiness = f.Readiness.ReleaseReady ? "Release-ready" : f.Readiness.Blockers.FirstOrDefault()?.Text ?? "Not release-ready",
            ExecutionResult = f.LastResultMatchesRelease ? f.LastStatus : CriticalE2EStatus.NotRun,
            RunId = f.LastResultMatchesRelease ? f.LastRunId : null,
            RunAt = f.LastResultMatchesRelease ? f.LastRunAt : null,
            Eligibility = f.Gate.Eligibility,
            EligibilityReason = f.Gate.Reason,
            BusinessE2EContribution = f.Gate.CountsTowardBusinessE2E,
            MandatoryReleaseBlocker = blocking.ContainsKey(f.FlowId),
            BlockerReason = blocking.GetValueOrDefault(f.FlowId),
        }).ToList();

        var state = stale is not null ? CriticalE2EReadinessState.Stale : release.Disposition switch
        {
            CriticalE2EReleaseDisposition.Ready => CriticalE2EReadinessState.Passed,
            CriticalE2EReleaseDisposition.Blocked => CriticalE2EReadinessState.Blocked,
            CriticalE2EReleaseDisposition.Incomplete or CriticalE2EReleaseDisposition.ReviewRequired => CriticalE2EReadinessState.Incomplete,
            // Policy 2: required flows exist and none can carry Business E2E coverage — not a pass.
            CriticalE2EReleaseDisposition.NotConfigured when release.RequiredFlowsTotal > 0 => CriticalE2EReadinessState.Incomplete,
            _ => CriticalE2EReadinessState.NotAssessed,
        };
        return new CriticalE2EReadinessEvaluation
        {
            State = state,
            PolicyVersion = release.PolicyVersion,
            Disposition = release.Disposition,
            EnvironmentId = release.EnvironmentId,
            BuildId = release.BuildId,
            BuildSource = release.BuildSource,
            ReadinessEnvironmentId = environmentId,
            ReadinessBuildId = buildId,
            EvaluatedAt = evaluatedAt,
            GateSummary = release.Summary,
            StaleReason = stale,
            RequiredFlows = release.RequiredFlowsTotal,
            BlockingFlows = flows.Count(f => f.MandatoryReleaseBlocker),
            BusinessE2EFlowsCounted = release.BusinessE2EFlowsCounted,
            Flows = flows,
        };
    }

    /// <summary>The readiness dimension for one mapped evaluation.</summary>
    public static ReadinessDimension Dimension(CriticalE2EReadinessEvaluation e)
    {
        var configured = e.RequiredFlows > 0;
        var applicability = configured || e.State != CriticalE2EReadinessState.NotAssessed ? DimensionApplicability.Required : DimensionApplicability.Optional;
        var decision = e.State switch
        {
            CriticalE2EReadinessState.Passed => GateDecision.Ready,
            CriticalE2EReadinessState.Blocked => GateDecision.Blocked,
            _ => GateDecision.NotAssessed,
        };
        var build = e.ReadinessBuildId is null ? "no build selected" : $"build {e.ReadinessBuildId}";
        var summary = e.State switch
        {
            CriticalE2EReadinessState.Stale => $"Stale: {e.StaleReason} It is not evidence for this release.",
            CriticalE2EReadinessState.NotAssessed when !configured => "Not assessed: no critical flow is marked as required for release in this environment.",
            CriticalE2EReadinessState.NotAssessed => $"Not assessed: {e.GateSummary}",
            CriticalE2EReadinessState.Passed => $"Passed on {build} ({e.PolicyVersion}): {e.GateSummary}",
            CriticalE2EReadinessState.Blocked => $"Blocked on {build} ({e.PolicyVersion}): {e.GateSummary}",
            _ when e.Disposition == CriticalE2EReleaseDisposition.ReviewRequired => $"Review required on {build} ({e.PolicyVersion}): {e.GateSummary}",
            _ => $"Incomplete on {build} ({e.PolicyVersion}): {e.GateSummary}",
        };
        var next = e.State switch
        {
            CriticalE2EReadinessState.Passed => null,
            CriticalE2EReadinessState.Stale => "Assess the release again for the same build and environment.",
            CriticalE2EReadinessState.NotAssessed when !configured => "Mark the module's critical Business E2E flow required for release in Critical E2E Regression.",
            CriticalE2EReadinessState.NotAssessed => "Enter the build being released so its Critical E2E release evidence is evaluated.",
            CriticalE2EReadinessState.Blocked => "Fix the blocking required flows and run them again against this build in Critical E2E Regression.",
            _ when e.Disposition == CriticalE2EReleaseDisposition.ReviewRequired => "Classify the test type of the legacy required flows in the Critical E2E flow editor.",
            _ when e.Disposition == CriticalE2EReleaseDisposition.NotConfigured => "Add a required critical Business E2E flow; the required flows do not count toward Business E2E.",
            _ => "Run the required flows against this build in Critical E2E Regression.",
        };
        var evidence = e.Flows.Select(f => $"{f.Name} — {f.TestType} · {CriticalE2EGatePolicy.ShortLabel(f.Eligibility)} · result {f.ExecutionResult}"
            + (f.MandatoryReleaseBlocker ? $" · blocks release: {f.BlockerReason}" : "")).ToList();
        return new ReadinessDimension(ReadinessDimensionKey.CriticalE2E, Title, applicability, decision, summary, evidence, next,
            DrillDown(e.ReadinessEnvironmentId, e.ReadinessBuildId)) { CriticalE2E = e };
    }

    /// <summary>When this host has no Critical E2E capability: nothing is read, nothing is invented.</summary>
    public static ReadinessDimension Unavailable() => new(ReadinessDimensionKey.CriticalE2E, Title, DimensionApplicability.Optional, GateDecision.NotAssessed,
        "Not read into release readiness: the Critical E2E gate is not available in this host. Open Critical E2E Regression for its own readiness and results.", [], null, Page);

    private static bool Same(string? a, string? b) => string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Show(string? value) => Trim(value) ?? "(none)";
}
