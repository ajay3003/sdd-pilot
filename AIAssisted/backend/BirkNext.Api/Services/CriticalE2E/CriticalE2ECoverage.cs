using BirkNext.CriticalE2E;

namespace BirkNext.Api.Services.CriticalE2E;

/// <summary>
/// Turns flow definitions and run history into the two answers the delivery requirement actually asks for, which are
/// not the same answer:
///
///   "Ende-til-ende-test for minst én kritisk brukerflyt per modul er på plass ved leveranse"
///
/// *Is it in place* is definition coverage — does every module have a required critical flow configured. *Does it pass*
/// is release validation — did those flows pass against the build being shipped. A surface that merges them lets a
/// module with a configured-but-never-run flow read as covered, which is the failure mode this whole capability exists
/// to prevent.
///
/// Pure functions over their inputs: no clock, no storage, no service calls, so the rules can be tested as rules.
/// </summary>
public static class CriticalE2ECoverage
{
    /// <summary>
    /// Is this flow runnable at all? A definition problem is reported here rather than discovered at run time, because
    /// "we never ran it" and "it could never have run" are different things to tell someone before a release.
    /// </summary>
    public static string? ConfigurationProblem(CriticalE2EFlowDefinition flow) => flow.ConfigurationProblem();

    public static CriticalE2EFlowSummary Summarize(CriticalE2EFlowDefinition flow, IReadOnlyList<CriticalE2ERunResult> history, string? buildId)
    {
        var problem = ConfigurationProblem(flow);
        var latest = history.Where(r => r.FlowId == flow.Id).OrderByDescending(r => r.StartedAt).FirstOrDefault();
        return new CriticalE2EFlowSummary
        {
            FlowId = flow.Id,
            Name = flow.Name,
            Module = flow.Module,
            Mode = flow.Mode,
            Enabled = flow.Enabled,
            RequiredForRelease = flow.RequiredForRelease,
            Configured = problem is null,
            ConfigurationProblem = problem,
            LastStatus = latest?.Status ?? CriticalE2EStatus.NotRun,
            LastRunAt = latest?.StartedAt,
            LastBuildId = latest?.BuildId,
            // A green run from a build nobody is shipping says nothing about the build they are.
            LastResultMatchesRelease = latest is not null && MatchesBuild(latest.BuildId, buildId),
        };
    }

    /// <summary>
    /// When no build is being validated, any recent result counts — the user is looking at the capability, not gating a
    /// release. When a build IS named, only results from that build count.
    /// </summary>
    private static bool MatchesBuild(string? resultBuild, string? targetBuild) =>
        string.IsNullOrWhiteSpace(targetBuild) || string.Equals(resultBuild, targetBuild, StringComparison.OrdinalIgnoreCase);

    public static List<CriticalE2EModuleCoverage> Modules(IReadOnlyList<CriticalE2EFlowDefinition> flows, IReadOnlyList<CriticalE2ERunResult> history,
        IReadOnlyList<string> knownModules, string? buildId)
    {
        var summaries = flows.Select(f => Summarize(f, history, buildId)).ToList();
        var modules = knownModules
            .Concat(flows.Select(f => f.Module))
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.CurrentCultureIgnoreCase);

        return modules.Select(module => new CriticalE2EModuleCoverage
        {
            Module = module,
            // A module is covered by having a required flow, not by having several. Counting flows would let one
            // well-tested module hide an untested one.
            Flows = summaries.Where(s => string.Equals(s.Module, module, StringComparison.OrdinalIgnoreCase)).ToList(),
        }).ToList();
    }

    public static CriticalE2EReleaseStatus Release(IReadOnlyList<CriticalE2EFlowDefinition> flows, IReadOnlyList<CriticalE2ERunResult> history,
        IReadOnlyList<string> knownModules, string environmentId, string? buildId, string? releaseId)
    {
        var modules = Modules(flows, history, knownModules, buildId);
        var required = flows.Where(f => f.Enabled && f.RequiredForRelease).Select(f => Summarize(f, history, buildId)).ToList();

        // A result only counts toward this release when it came from this build. Otherwise the flow is pending, which is
        // "not known yet" — never "failed".
        var current = required.Select(r => r.LastResultMatchesRelease ? r.LastStatus : CriticalE2EStatus.NotRun).ToList();
        var passed = current.Count(s => s == CriticalE2EStatus.Passed);
        var failed = current.Count(s => s == CriticalE2EStatus.Failed);
        var blocked = current.Count(s => s is CriticalE2EStatus.Blocked or CriticalE2EStatus.Cancelled);
        var pending = current.Count(s => s is CriticalE2EStatus.NotRun or CriticalE2EStatus.Running);
        var unconfigured = required.Count(r => !r.Configured);

        var disposition =
            required.Count == 0 ? CriticalE2EReleaseDisposition.NotConfigured
            : failed > 0 || blocked > 0 || unconfigured > 0 ? CriticalE2EReleaseDisposition.Blocked
            : pending > 0 ? CriticalE2EReleaseDisposition.Incomplete
            : CriticalE2EReleaseDisposition.Ready;

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
            RequiredFlowsBlocked = blocked + unconfigured,
            Summary = disposition switch
            {
                CriticalE2EReleaseDisposition.NotConfigured => "No critical flows are marked as required for release yet.",
                CriticalE2EReleaseDisposition.Blocked when failed > 0 => $"{failed} required flow(s) failed against this build.",
                CriticalE2EReleaseDisposition.Blocked when unconfigured > 0 => $"{unconfigured} required flow(s) cannot run as configured.",
                CriticalE2EReleaseDisposition.Blocked => $"{blocked} required flow(s) could not run.",
                CriticalE2EReleaseDisposition.Incomplete => $"{pending} required flow(s) have not run against this build yet.",
                _ => $"All {passed} required flow(s) passed against this build.",
            },
        };
    }
}
