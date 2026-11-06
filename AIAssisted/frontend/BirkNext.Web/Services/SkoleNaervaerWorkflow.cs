using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Skolenærvær Testing as guided steps: environment &amp; source evidence → integration configuration → Test Data → journeys → execution readiness →
/// execute &amp; verify → results &amp; history. Derived from what the page already loads (source guidance, integration coverage, the shared Test Data
/// readiness, journey views) — no second readiness engine. Available ≠ bound, discovered ≠ configured, saved ≠ approved, XSD valid ≠ business
/// valid, configured ≠ executable, executed ≠ verified; nothing runs because a step is visited.
/// </summary>
public static class SkoleNaervaerWorkflow
{
    public static IReadOnlyList<GuidedStep> Derive(string? environment, IReadOnlyList<TestPackSourceStatus> sources, string integrationCoverage, TestDataTesterStatus? testData,
        IReadOnlyList<IntegrationJourneyView> journeys, string executionConfiguration, Func<string, string> tabHref, string? profileId)
    {
        var required = sources.Where(s => s.Role.Requirement == TestPackSourceRequirement.Required).ToList();
        var bound = required.Count(s => s.State is TestPackSourceState.Bound or TestPackSourceState.ReferenceAvailable);
        var needsBinding = required.Count(s => s.State == TestPackSourceState.AvailableNotBound);
        var missing = required.Count - bound - needsBinding;
        var executable = journeys.Count(j => j.CanRun);
        var steps = new List<GuidedStep>
        {
            environment is null
                ? new("sources", "Environment and source evidence", GuidedStepState.Current, "No Target Environment", "Select the Target Environment to test against.")
                : required.Count == 0
                    ? new("sources", "Environment and source evidence", GuidedStepState.Waiting, "Not assessed", $"{environment}: source requirements are loading or not declared.")
                    : bound == required.Count
                        ? new("sources", "Environment and source evidence", GuidedStepState.Done, $"{bound} of {required.Count} required sources bound", $"{environment}: every required source is bound for this environment.")
                        : new("sources", "Environment and source evidence", GuidedStepState.Current, $"{bound} of {required.Count} required sources bound",
                            $"{environment}: {needsBinding} available but not bound (bind them; no re-import needed){(missing > 0 ? $" · {missing} missing or needing review" : "")}. Available is not bound.",
                            ActionLabel: "Open source binding", Href: "/integration-quality-review?view=setup-evidence"),
        };

        var complete = integrationCoverage.StartsWith("Complete", StringComparison.Ordinal);
        steps.Add(new("integrations", "Integration configuration", integrationCoverage == "Not assessed" ? GuidedStepState.Waiting : complete ? GuidedStepState.Done : GuidedStepState.Current,
            integrationCoverage.Split(':')[0], $"{integrationCoverage}. Discovered integration proposals are not configured integrations; identity conflicts are decided in Relationship Review.",
            ActionLabel: complete ? null : "Open Integration Catalog", Href: complete ? null : IntegrationReviewPresentation.IntegrationsHref(profileId)));

        steps.Add(testData is null
            ? new("test-data", "Test Data", GuidedStepState.Waiting, "Loading", "Test Data readiness comes from the Test Data owner (shown below).")
            : new("test-data", "Test Data", testData.Ready ? GuidedStepState.Done : GuidedStepState.Current, testData.Overall,
                $"{testData.XmlText} · fixture validation {testData.ContractValidation}. Saved is not approved; XSD valid is not business validated.{(testData.Reason is { } why ? $" {why}" : "")}",
                ActionLabel: testData.Ready ? null : "Open Test Data", Href: testData.Ready ? null : "/test-data"));

        steps.Add(new("journeys", "Select journeys", journeys.Count == 0 ? GuidedStepState.Waiting : GuidedStepState.Done,
            journeys.Count == 0 ? "Loading" : $"{journeys.Count} journeys", journeys.Count == 0 ? "Journeys are defined by the Skolenærvær Test Pack." :
            $"{string.Join(", ", journeys.Select(j => j.Journey.DisplayName))}: each has its own prerequisites and blockers (Journeys).",
            ActionLabel: "Open journeys", Href: tabHref("utdanningsdata")));

        steps.Add(new("execution", "Execution readiness", executionConfiguration == "Blocked" ? GuidedStepState.Blocked : executable > 0 ? GuidedStepState.Done : GuidedStepState.Waiting,
            executionConfiguration, executable > 0 ? "Executable journeys still need their own confirmation; safety rules are never relaxed to get a green status."
                : "Blocked by root cause: see what needs attention under Journeys (grouped by owner)."));

        steps.Add(new("verify", "Execute and verify", GuidedStepState.Waiting, "Explicit only",
            "A journey runs only after you confirm it on its tab; executed is not verified, and no live journey runs from this overview.", ActionLabel: "Open Verify", Href: tabHref("message-verification")));

        steps.Add(new("history", "Results and history", GuidedStepState.Waiting, "Recorded runs",
            "Historical runs keep their own evidence; a historical pass is not current readiness.", ActionLabel: "Open History", Href: tabHref("history")));

        var first = steps.FindIndex(s => s.State == GuidedStepState.Current);
        return [.. steps.Select((s, i) => i > first && first >= 0 && s.State == GuidedStepState.Current ? s with { State = GuidedStepState.Waiting } : s)];
    }
}
