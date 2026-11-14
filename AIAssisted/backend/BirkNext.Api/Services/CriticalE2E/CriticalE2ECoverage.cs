using BirkNext.CriticalE2E;

namespace BirkNext.Api.Services.CriticalE2E;

/// <summary>
/// Turns flow definitions and run history into the two answers the delivery requirement actually asks for, which are
/// not the same answer:
///
///   "Ende-til-ende-test for minst én kritisk brukerflyt per modul er på plass ved leveranse"
///
/// *Is it in place* is definition coverage — does every registered module have a required critical flow whose definition is
/// release-ready. *Does it pass* is release validation — did those flows pass against the build being shipped. A surface that
/// merges them lets a module with a configured-but-never-run flow read as covered, which is the failure mode this whole
/// capability exists to prevent.
///
/// Module authority: only registered delivery modules are coverage obligations. A module label typed on a flow is a manual
/// label — preserved and shown as "Unresolved module" — and never creates or satisfies a release obligation.
///
/// Pure functions over their inputs: no clock, no storage, no service calls, so the rules can be tested as rules.
/// </summary>
public static class CriticalE2ECoverage
{
    /// <summary>Problems of a flow's mapped central Test Data (empty = ready). Supplied by the service; pure rules default to none.</summary>
    public delegate IReadOnlyList<string> TestDataIssues(CriticalE2EFlowDefinition flow);

    private static readonly TestDataIssues NoIssues = _ => [];

    /// <summary>
    /// Is this flow runnable at all? A definition problem is reported here rather than discovered at run time, because
    /// "we never ran it" and "it could never have run" are different things to tell someone before a release.
    /// </summary>
    public static string? ConfigurationProblem(CriticalE2EFlowDefinition flow) => flow.ConfigurationProblem();

    /// <param name="policyVersion">The gate policy evaluating this build; null = chosen from the build's recorded evidence (<see cref="CriticalE2EGatePolicy.ForBuild"/>).</param>
    public static CriticalE2EFlowSummary Summarize(CriticalE2EFlowDefinition flow, IReadOnlyList<CriticalE2ERunResult> history, string? buildId,
        IReadOnlyCollection<string>? registeredModules = null, TestDataIssues? testData = null, string? policyVersion = null)
    {
        var problem = ConfigurationProblem(flow);
        var readiness = CriticalE2EReleaseRules.Evaluate(flow, registeredModules ?? [], (testData ?? NoIssues)(flow));
        var runs = history.Where(r => r.FlowId == flow.Id).OrderByDescending(r => r.StartedAt).ToList();
        // With a build named, the flow reports its latest run ON that build — the one that is evidence — so a later run
        // on another build (or without one) neither erases it nor contradicts the release verdict. Otherwise, and when
        // the build has no run yet, it reports its latest run, which then matches no release.
        var onBuild = runs.FirstOrDefault(r => MatchesBuild(r.BuildId, buildId));
        var latest = onBuild ?? runs.FirstOrDefault();
        // A run of an older definition is not evidence about the current one: it stays tied to the version it ran.
        var stale = latest is not null && (latest.FlowVersion != flow.Version
            // Same version number but different content (for example re-keyed by an import): the run is not evidence for this definition either.
            || latest.FlowFingerprint is { } executed && executed != CriticalE2EStore.Fingerprint(flow));
        var matchesRelease = onBuild is not null && !stale && onBuild.ReleaseEvidence;
        var gate = CriticalE2EGatePolicy.Evaluate(policyVersion ?? CriticalE2EGatePolicy.ForBuild(history, buildId), flow.Kind, flow.TestType, readiness,
            !string.IsNullOrWhiteSpace(buildId), matchesRelease ? onBuild!.Status : null);
        return new CriticalE2EFlowSummary
        {
            FlowId = flow.Id,
            Name = flow.Name,
            Module = flow.Module,
            Kind = flow.Kind,
            TestType = flow.TestType,
            Mode = flow.Mode,
            StepCount = flow.Steps.Count,
            Enabled = flow.Enabled,
            RequiredForRelease = flow.RequiredForRelease,
            Configured = problem is null,
            ConfigurationProblem = problem,
            LastStatus = latest?.Status ?? CriticalE2EStatus.NotRun,
            LastRunAt = latest?.StartedAt,
            LastBuildId = latest?.BuildId,
            LastRunId = latest?.RunId,
            // A green run from a build nobody is shipping says nothing about the build they are; nor does a run of an older
            // definition, or a run that was not release evidence when it ran (diagnostic, definition not release-ready).
            LastResultMatchesRelease = matchesRelease,
            Version = flow.Version,
            ActionSteps = flow.Steps.Count(s => !CriticalE2EAuthoring.IsAssertion(s.BrowserAction) && !(s.IsIntegrationStep && s.Expect is not null)),
            Assertions = CriticalE2EReleaseRules.Assertions(flow),
            Outcome = CriticalE2EReleaseRules.OutcomeText(flow),
            BoundaryStatus = flow.Boundary is { Explicit: true } ? "Explicit" : "Not explicit",
            BoundaryText = BoundaryText(flow),
            Readiness = readiness,
            Source = flow.Source,
            LastResultLabel = latest is null ? null : CriticalE2EReleaseRules.ResultLabel(latest),
            LastRunStale = stale,
            Gate = gate,
        };
    }

    public static string? BoundaryText(CriticalE2EFlowDefinition flow) => flow.Boundary is { Explicit: true } b
        ? $"{b.Start} → {b.End}{(b.CoversDownstream ? " · downstream verified by this flow" : b.Downstream.Length > 0 ? $" · downstream: {b.Downstream}" : " · downstream not verified by this flow")}"
        : string.IsNullOrWhiteSpace(flow.AutomationBoundary) ? null : $"Legacy note: {flow.AutomationBoundary}";

    /// <summary>
    /// Release evidence is build-bound. A result matches only when a build is named and the result was recorded against
    /// it. With no build named nothing matches: runs still execute and keep their outcome, they are just not evidence.
    /// (This used to accept any recent result when no build was named, which let the API report Ready with no build.)
    /// </summary>
    private static bool MatchesBuild(string? resultBuild, string? targetBuild) =>
        !string.IsNullOrWhiteSpace(targetBuild) && string.Equals(resultBuild?.Trim(), targetBuild.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Flow module labels that are not registered modules: preserved, shown, never counted.</summary>
    public static List<string> UnresolvedModules(IReadOnlyList<CriticalE2EFlowDefinition> flows, IReadOnlyCollection<string> registeredModules) =>
        [.. flows.Select(f => f.Module.Trim()).Where(m => m.Length > 0 && !registeredModules.Contains(m, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.CurrentCultureIgnoreCase)];

    /// <param name="registeredModules">Authoritative delivery modules. Nothing else becomes a coverage row.</param>
    public static List<CriticalE2EModuleCoverage> Modules(IReadOnlyList<CriticalE2EFlowDefinition> flows, IReadOnlyList<CriticalE2ERunResult> history,
        IReadOnlyList<string> registeredModules, string? buildId, TestDataIssues? testData = null)
    {
        var registered = registeredModules.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var policy = CriticalE2EGatePolicy.ForBuild(history, buildId);
        var summaries = flows.Select(f => Summarize(f, history, buildId, registered, testData, policy)).ToList();

        return registered.OrderBy(m => m, StringComparer.CurrentCultureIgnoreCase).Select(module =>
        {
            // A module is covered by having a release-ready required flow, not by having several. Counting flows would let
            // one well-tested module hide an untested one.
            var carried = summaries.Where(s => s.Kind == CriticalE2EFlowKind.Critical && string.Equals(s.Module.Trim(), module, StringComparison.OrdinalIgnoreCase)).ToList();
            return new CriticalE2EModuleCoverage { Module = module, Flows = carried, Status = ModuleStatus(module, carried, buildId) };
        }).ToList();
    }

    /// <summary>"M01 · required flow configured · assertion missing", "M01 · required flow ready · no build selected" …</summary>
    public static string ModuleStatus(string module, IReadOnlyList<CriticalE2EFlowSummary> flows, string? buildId)
    {
        var required = flows.Where(f => f.RequiredForRelease && f.Enabled).ToList();
        if (flows.Count == 0) return $"{module} · no critical flow";
        if (required.Count == 0) return $"{module} · {flows.Count} critical flow(s) · none required for release";
        // Policy 2: only flows that can carry Business E2E coverage satisfy the module. (Legacy policy: every critical flow can.)
        var business = required.Where(f => f.Gate.CountsTowardBusinessE2E).ToList();
        if (business.Count == 0)
            return required.Any(f => f.Gate.Eligibility == CriticalE2EGateEligibilityKind.LegacyReview)
                ? $"{module} · required flow needs review · legacy classification requires review"
                : $"{module} · required flow(s) not Business E2E · no Business E2E coverage";
        required = business;
        var ready = required.Where(f => f.Readiness.ReleaseReady).ToList();
        if (ready.Count == 0)
            return $"{module} · required flow configured · {Blocker(required[0].Readiness)}";
        if (string.IsNullOrWhiteSpace(buildId)) return $"{module} · required flow ready · no build selected";
        return ready.Any(f => f.LastResultMatchesRelease && f.LastStatus == CriticalE2EStatus.Passed)
            ? $"{module} · required flow passed on build {buildId.Trim()}"
            : $"{module} · required flow ready · not yet passed on build {buildId.Trim()}";
    }

    private static string Blocker(CriticalE2EFlowReadiness readiness) => readiness.Blockers.FirstOrDefault()?.Key switch
    {
        "outcome" => readiness.LegacyAssertionMarker ? "legacy assertion marker" : "assertion missing",
        "enabled" => "flow disabled",
        "runnable" => "not runnable",
        "selector" => "fragile selector not reviewed",
        "boundary" => "automation boundary not explicit",
        "testdata" => "Test Data not ready",
        _ => "not release-ready",
    };

    /// <summary>
    /// Why a required flow blocks the gate on the named build, or null when it does not. The same conditions <see cref="Release"/> counts as
    /// failed / blocked / unconfigured / not release-ready — whatever the flow's test type: under policy 2 a failed required UI smoke flow still blocks,
    /// it just never contributes Business E2E coverage.
    /// </summary>
    public static string? BlockingReason(CriticalE2EFlowSummary required)
    {
        var status = required.LastResultMatchesRelease ? required.LastStatus : CriticalE2EStatus.NotRun;
        if (status == CriticalE2EStatus.Failed) return "Failed required flow on this build";
        if (status is CriticalE2EStatus.Blocked or CriticalE2EStatus.Cancelled or CriticalE2EStatus.Inconclusive)
            return $"Required flow {status.ToString().ToLowerInvariant()} on this build";
        if (!required.Configured) return $"Required flow cannot run as configured: {required.ConfigurationProblem}";
        if (!required.Readiness.ReleaseReady) return $"Required flow not release-ready: {required.Readiness.Blockers.FirstOrDefault()?.Text ?? "see blockers"}";
        return null;
    }

    public static CriticalE2EReleaseStatus Release(IReadOnlyList<CriticalE2EFlowDefinition> flows, IReadOnlyList<CriticalE2ERunResult> history,
        IReadOnlyList<string> registeredModules, string environmentId, string? buildId, string? releaseId, TestDataIssues? testData = null, string? buildSource = null)
    {
        // Configured coverage is structural: it needs no build and no run.
        var modules = Modules(flows, history, registeredModules, buildId, testData);
        var registered = registeredModules.Select(m => m.Trim()).ToList();
        // One policy per evaluated build, chosen from the evidence recorded on it: a build whose runs predate versioned policies keeps the
        // legacy verdict it had; a new build (or one with new evidence) is evaluated by the current policy.
        var policy = CriticalE2EGatePolicy.ForBuild(history, buildId);
        var legacyPolicy = CriticalE2EGatePolicy.IsLegacy(policy);
        var required = flows.Where(f => f.Enabled && f.RequiredForRelease && f.Kind == CriticalE2EFlowKind.Critical)
            .Select(f => Summarize(f, history, buildId, registered, testData, policy)).ToList();
        var notReady = required.Count(r => !r.Readiness.ReleaseReady);
        var legacyReview = legacyPolicy ? 0 : required.Count(r => r.Gate.Eligibility == CriticalE2EGateEligibilityKind.LegacyReview);
        var businessRequired = required.Count(r => r.Gate.CountsTowardBusinessE2E);
        var businessCounted = required.Count(r => r.Gate.Eligibility == CriticalE2EGateEligibilityKind.Counts);

        // Release evidence needs a named build. Without one the scope is still reported, but no result is evidence, so
        // the verdict is "not evaluated" — not Ready (nothing is established) and not Failed (nothing is wrong).
        if (required.Count > 0 && string.IsNullOrWhiteSpace(buildId))
            return new CriticalE2EReleaseStatus
            {
                BuildId = null,
                ReleaseId = releaseId,
                EnvironmentId = environmentId,
                Disposition = CriticalE2EReleaseDisposition.NotEvaluated,
                ModulesCovered = modules.Count(m => m.Covered),
                ModulesTotal = modules.Count,
                RequiredFlowsTotal = required.Count,
                RequiredFlowsNotReady = notReady,
                PolicyVersion = policy,
                RequiredFlowsLegacyReview = legacyReview,
                RequiredFlowIds = [.. required.Select(r => r.FlowId)],
                Summary = $"No build is selected, so release evidence is not evaluated. {required.Count} required flow(s) are configured"
                    + (notReady > 0 ? $", {notReady} not release-ready." : ".")
                    + (legacyReview > 0 ? $" {legacyReview} need legacy classification review before they can count toward Business E2E." : ""),
            };

        // A result only counts toward this release when it came from this build, ran the current definition version and
        // was release evidence when it ran (see Summarize). Otherwise the flow is pending — "not known yet", never "failed".
        var current = required.Select(r => r.LastResultMatchesRelease ? r.LastStatus : CriticalE2EStatus.NotRun).ToList();
        var passed = current.Count(s => s == CriticalE2EStatus.Passed);
        var failed = current.Count(s => s == CriticalE2EStatus.Failed);
        var blocked = current.Count(s => s is CriticalE2EStatus.Blocked or CriticalE2EStatus.Cancelled or CriticalE2EStatus.Inconclusive);
        var pending = current.Count(s => s is CriticalE2EStatus.NotRun or CriticalE2EStatus.Running);
        var unconfigured = required.Count(r => !r.Configured);
        // A required flow whose definition is not release-ready (manual module, no concrete outcome …) blocks the gate.
        var definitionBlocked = required.Count(r => r.Configured && !r.Readiness.ReleaseReady);

        // Legacy policy: exactly the verdict as it was. Policy 2: a failure is still a failure whatever the test type, an unclassified
        // required flow needs review (never Ready), and Ready additionally needs at least one required flow that counts as Business E2E.
        var disposition = legacyPolicy
            ? (required.Count == 0 ? CriticalE2EReleaseDisposition.NotConfigured
                : failed > 0 || blocked > 0 || unconfigured > 0 || definitionBlocked > 0 ? CriticalE2EReleaseDisposition.Blocked
                : pending > 0 ? CriticalE2EReleaseDisposition.Incomplete
                : CriticalE2EReleaseDisposition.Ready)
            : (required.Count == 0 ? CriticalE2EReleaseDisposition.NotConfigured
                : failed > 0 || blocked > 0 || unconfigured > 0 || definitionBlocked > 0 ? CriticalE2EReleaseDisposition.Blocked
                : legacyReview > 0 ? CriticalE2EReleaseDisposition.ReviewRequired
                : pending > 0 ? CriticalE2EReleaseDisposition.Incomplete
                : businessRequired == 0 ? CriticalE2EReleaseDisposition.NotConfigured
                : CriticalE2EReleaseDisposition.Ready);

        return new CriticalE2EReleaseStatus
        {
            BuildId = buildId,
            ReleaseId = releaseId,
            EnvironmentId = environmentId,
            Disposition = disposition,
            ModulesCovered = modules.Count(m => m.Covered),
            ModulesTotal = modules.Count,
            RequiredFlowsPassed = passed,
            RequiredFlowsTotal = required.Count,
            RequiredFlowsPending = pending,
            RequiredFlowsFailed = failed,
            RequiredFlowsBlocked = blocked + unconfigured + definitionBlocked,
            RequiredFlowsNotReady = notReady,
            BuildSource = string.IsNullOrWhiteSpace(buildId) ? null : buildSource ?? "Manual",
            PolicyVersion = policy,
            RequiredFlowsLegacyReview = legacyReview,
            BusinessE2EFlowsCounted = businessCounted,
            RequiredFlowIds = [.. required.Select(r => r.FlowId)],
            BlockingFlows = [.. required.Select(r => (r.FlowId, Reason: BlockingReason(r))).Where(b => b.Reason is not null).Select(b => new CriticalE2EBlockingFlow(b.FlowId, b.Reason!))],
            Summary = disposition switch
            {
                CriticalE2EReleaseDisposition.NotConfigured when required.Count > 0 =>
                    $"{required.Count} required flow(s) passed, but none is a Business E2E flow, so none counts toward Business E2E coverage.",
                CriticalE2EReleaseDisposition.NotConfigured => "No critical flow is currently marked as required for release.",
                CriticalE2EReleaseDisposition.Blocked when failed > 0 => $"{failed} required flow(s) failed against this build.",
                CriticalE2EReleaseDisposition.Blocked when unconfigured > 0 => $"{unconfigured} required flow(s) cannot run as configured.",
                CriticalE2EReleaseDisposition.Blocked when definitionBlocked > 0 => $"{definitionBlocked} required flow(s) are configured but not release-ready.",
                CriticalE2EReleaseDisposition.Blocked => $"{blocked} required flow(s) could not run or were inconclusive.",
                CriticalE2EReleaseDisposition.ReviewRequired => $"{legacyReview} required flow(s) need review: legacy classification requires review before they can count toward Business E2E.",
                CriticalE2EReleaseDisposition.Incomplete => $"{pending} required flow(s) have not run against this build yet.",
                _ when !legacyPolicy && businessCounted < passed => $"All {passed} required flow(s) passed against this build · {businessCounted} count toward Business E2E.",
                _ => $"All {passed} required flow(s) passed against this build.",
            },
        };
    }
}
