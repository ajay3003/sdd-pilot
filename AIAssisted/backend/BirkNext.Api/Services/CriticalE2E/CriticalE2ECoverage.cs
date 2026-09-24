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
        var runs = history.Where(r => r.FlowId == flow.Id).OrderByDescending(r => r.StartedAt).ToList();
        // With a build named, the flow reports its latest run ON that build — the one that is evidence — so a later run
        // on another build (or without one) neither erases it nor contradicts the release verdict. Otherwise, and when
        // the build has no run yet, it reports its latest run, which then matches no release.
        var onBuild = runs.FirstOrDefault(r => MatchesBuild(r.BuildId, buildId));
        var latest = onBuild ?? runs.FirstOrDefault();
        return new CriticalE2EFlowSummary
        {
            FlowId = flow.Id,
            Name = flow.Name,
            Module = flow.Module,
            Kind = flow.Kind,
            Mode = flow.Mode,
            StepCount = flow.Steps.Count,
            Enabled = flow.Enabled,
            RequiredForRelease = flow.RequiredForRelease,
            Configured = problem is null,
            ConfigurationProblem = problem,
            LastStatus = latest?.Status ?? CriticalE2EStatus.NotRun,
            LastRunAt = latest?.StartedAt,
            LastBuildId = latest?.BuildId,
            // A green run from a build nobody is shipping says nothing about the build they are.
            LastResultMatchesRelease = onBuild is not null,
        };
    }

    /// <summary>
    /// Release evidence is build-bound. A result matches only when a build is named and the result was recorded against
    /// it. With no build named nothing matches: runs still execute and keep their outcome, they are just not evidence.
    /// (This used to accept any recent result when no build was named, which let the API report Ready with no build.)
    /// </summary>
    private static bool MatchesBuild(string? resultBuild, string? targetBuild) =>
        !string.IsNullOrWhiteSpace(targetBuild) && string.Equals(resultBuild?.Trim(), targetBuild.Trim(), StringComparison.OrdinalIgnoreCase);


    public static List<CriticalE2EModuleCoverage> Modules(IReadOnlyList<CriticalE2EFlowDefinition> flows, IReadOnlyList<CriticalE2ERunResult> history,
        IReadOnlyList<string> knownModules, string? buildId)
    {
        var summaries = flows.Select(f => Summarize(f, history, buildId)).ToList();
        // Release coverage is about delivery modules. A module label that only diagnostic flows carry (a smoke module) is
        // not one, so it never becomes a coverage obligation, even when an earlier page load recorded it as known.
        bool DiagnosticOnly(string module)
        {
            var carriers = flows.Where(f => string.Equals(f.Module, module, StringComparison.OrdinalIgnoreCase)).ToList();
            return carriers.Count > 0 && carriers.All(f => f.Kind == CriticalE2EFlowKind.Diagnostic);
        }
        var modules = knownModules
            .Concat(flows.Where(f => f.Kind == CriticalE2EFlowKind.Critical).Select(f => f.Module))
            .Where(m => !string.IsNullOrWhiteSpace(m) && !DiagnosticOnly(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.CurrentCultureIgnoreCase);

        return modules.Select(module => new CriticalE2EModuleCoverage
        {
            Module = module,
            // A module is covered by having a required flow, not by having several. Counting flows would let one
            // well-tested module hide an untested one.
            Flows = summaries.Where(s => s.Kind == CriticalE2EFlowKind.Critical && string.Equals(s.Module, module, StringComparison.OrdinalIgnoreCase)).ToList(),
        }).ToList();
    }

    public static CriticalE2EReleaseStatus Release(IReadOnlyList<CriticalE2EFlowDefinition> flows, IReadOnlyList<CriticalE2ERunResult> history,
        IReadOnlyList<string> knownModules, string environmentId, string? buildId, string? releaseId)
    {
        // Configured coverage is structural: it needs no build and no run.
        var modules = Modules(flows, history, knownModules, buildId);
        var required = flows.Where(f => f.Enabled && f.RequiredForRelease && f.Kind == CriticalE2EFlowKind.Critical).Select(f => Summarize(f, history, buildId)).ToList();

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
                Summary = $"No build is selected, so release evidence is not evaluated. {required.Count} required flow(s) are configured.",
            };

        // A result only counts toward this release when it came from this build (the latest one on it, see Summarize).
        // Otherwise the flow is pending, which is "not known yet" — never "failed".
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
                CriticalE2EReleaseDisposition.NotConfigured => "No critical flow is currently marked as required for release.",
                CriticalE2EReleaseDisposition.Blocked when failed > 0 => $"{failed} required flow(s) failed against this build.",
                CriticalE2EReleaseDisposition.Blocked when unconfigured > 0 => $"{unconfigured} required flow(s) cannot run as configured.",
                CriticalE2EReleaseDisposition.Blocked => $"{blocked} required flow(s) could not run.",
                CriticalE2EReleaseDisposition.Incomplete => $"{pending} required flow(s) have not run against this build yet.",
                _ => $"All {passed} required flow(s) passed against this build.",
            },
        };
    }
}
