using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Integrations;
using BirkNext.IntegrationSuites;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.IntegrationSuites;

/// <summary>
/// The integration side of requirements traceability, added to the one prepared-case trace view that Test Coverage and Requirements Traceability
/// share: confirmed requirement ↔ journey links of the project, the project's suites prepared for those journeys and the outcome each suite's
/// recorded runs give. Read-only; a prepared suite is never an execution.
/// </summary>
public static class IntegrationRequirementTrace
{
    public static async Task<PreparedCaseTraceView> WithIntegrationAsync(PreparedCaseTraceView view, string projectKey, JourneyRequirementLinkStore? links,
        DeclaredIntegrationJourneyStore? journeys, IntegrationSuiteService? suites, CancellationToken ct)
    {
        if (links is null || journeys is null || string.IsNullOrWhiteSpace(projectKey)) return view;
        var confirmed = (await links.ListAsync(projectKey, null, ct)).Where(l => l.Status == JourneyRequirementStatus.Confirmed).ToList();
        if (confirmed.Count == 0) return view;
        var declared = (await journeys.ForProjectAsync(projectKey, ct)).ToDictionary(j => j.JourneyId, StringComparer.Ordinal);
        var summaries = suites is null ? [] : (await suites.ListAsync(ct)).Where(s => s.WorkspaceProjects.Count == 0 || s.WorkspaceProjects.Contains(projectKey.Trim(), StringComparer.Ordinal)).ToList();
        var outcomes = new Dictionary<Guid, SuiteExecutionOutcome>();
        var traces = new List<RequirementIntegrationTrace>();
        foreach (var l in confirmed)
        {
            if (!declared.TryGetValue(l.JourneyId, out var journey)) continue; // a retired journey no longer counts
            var prepared = new List<IntegrationSuiteTrace>();
            foreach (var s in summaries.Where(s => s.PreparedForJourneys.Any(j => j.JourneyId == journey.Id)))
            {
                if (!outcomes.TryGetValue(s.Id, out var outcome))
                    outcomes[s.Id] = outcome = suites is not null && await suites.GetAsync(s.Id, ct) is { } full ? full.Outcome : SuiteExecutionOutcome.NotExecuted;
                prepared.Add(new(s.Id, s.ScopeName, s.Revision, Label(outcome), outcome != SuiteExecutionOutcome.NotExecuted, $"integration-suites?area=generate&suite={s.Id:D}"));
            }
            traces.Add(new(l.RequirementId, l.SpecificationPath, l.Title, journey.Id, journey.DisplayName, l.JourneyRevision, journey.Revision, prepared));
        }
        return view with { Integration = [.. traces.OrderBy(t => t.RequirementId, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.JourneyName, StringComparer.CurrentCultureIgnoreCase)] };
    }

    public static string Label(SuiteExecutionOutcome o) => o switch
    {
        SuiteExecutionOutcome.Verified => "Verified",
        SuiteExecutionOutcome.PartiallyVerified => "Partially verified",
        SuiteExecutionOutcome.Executed => "Executed, not verified",
        SuiteExecutionOutcome.Failed => "Failed",
        _ => PreparedCaseTraceView.NotExecuted,
    };
}
