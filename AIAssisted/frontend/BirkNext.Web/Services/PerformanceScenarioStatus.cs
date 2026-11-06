using BirkNext.PerformanceTests;
using R = BirkNext.PerformanceTests.PerformanceScenarioRuntime;

namespace BirkNext.Web.Services;

/// <summary>One scenario-wide status: how many enabled steps are defined, configured (runtime values), authenticated and executable — and why not.</summary>
public sealed record PerformanceScenarioStatus(int Steps, int Defined, int Configured, int ValuesResolved, int ValuesTotal, int AuthRequired, int AuthMissing,
    bool AuthEvidenceKnown, int Executable, IReadOnlyList<string> Issues)
{
    public bool AllExecutable => Steps > 0 && Executable == Steps;
}

/// <summary>
/// The scenario status every Performance Test Review tab shows (Overview, Scenario, Readiness, Run), computed once from the same shared runtime rules
/// the steps, the server readiness and the run start use — so no tab can disagree. Unknown endpoint evidence keeps authentication "not fully known".
/// </summary>
public static class PerformanceScenarioStatusModel
{
    public static PerformanceScenarioStatus Of(PerformanceTestDefinition d, IReadOnlySet<string>? knownAliases, IReadOnlySet<string>? observedAuthentication)
    {
        var enabled = d.Scenario.Steps.Select((s, i) => (s, i)).Where(x => !x.s.Disabled && !PerformanceDiscoveryDrafts.IsDraft(x.s)).ToList();
        var dims = enabled.Select(x => (x.s, x.i, dim: R.Dimensions(d, x.i, knownAliases, observedAuthentication))).ToList();
        var auth = R.AuthenticationRequirements(d, observedAuthentication);
        var (resolved, total, _) = R.ValuesSummary(d.Scenario, knownAliases);
        var missingAuth = auth.Where(a => a.State is StepAuthenticationState.Missing or StepAuthenticationState.OverrideOutdated).ToList();
        var issues = new List<string>();
        issues.AddRange(missingAuth.Select(a => $"Step {a.Index + 1} · {a.Step}: {R.AuthenticationRequirementProblem(a)}"));
        issues.AddRange(dims.Where(x => x.dim.RuntimeValues.State is StepDimensionState.NeedsConfiguration or StepDimensionState.Blocked)
            .Select(x => $"Step {x.i + 1} · {x.s.Name}: {x.dim.RuntimeValues.Text}"));
        issues.AddRange(dims.Where(x => x.dim.Definition.State is StepDimensionState.NeedsConfiguration or StepDimensionState.Blocked)
            .Select(x => $"Step {x.i + 1} · {x.s.Name}: {x.dim.Definition.Text}"));
        return new(enabled.Count,
            dims.Count(x => x.dim.Definition.State == StepDimensionState.Ready),
            dims.Count(x => x.dim.RuntimeValues.State is StepDimensionState.Ready or StepDimensionState.NotApplicable),
            resolved, total,
            auth.Count(a => a.Requirement is not null), missingAuth.Count, observedAuthentication is not null,
            // Executable needs every dimension: defined, runtime values resolved (or none needed), an executable credential, and no missing authentication.
            dims.Count(x => x.dim.Definition.State == StepDimensionState.Ready && x.dim.RuntimeValues.State is StepDimensionState.Ready or StepDimensionState.NotApplicable
                && x.dim.Execution.State is StepDimensionState.Ready or StepDimensionState.NotApplicable && missingAuth.All(a => a.Index != x.i)),
            issues);
    }
}
