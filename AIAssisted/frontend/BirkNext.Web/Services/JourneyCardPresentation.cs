using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// One journey at a glance, from its <see cref="IntegrationJourneyView"/> only: purpose, defined scenarios, required integrations (configured or not),
/// prerequisites by kind (source evidence, test data, authentication/authorization), what verification means, eligibility, blockers, the latest
/// recorded run (historical, never current readiness) and the next action. Nothing is invented: requirements the pack does not declare are not
/// listed, and a scenario description is never an executable test.
/// </summary>
public static class JourneyCardPresentation
{
    public sealed record Item(string Label, string State, string Detail);

    public sealed record Card(
        string Purpose, IReadOnlyList<string> Scenarios, IReadOnlyList<Item> Integrations, IReadOnlyList<Item> SourceEvidence, IReadOnlyList<Item> TestData,
        IReadOnlyList<Item> Access, string Expected, string Eligibility, IReadOnlyList<Item> Blockers, string Verification, string? History, string NextAction);

    private static Item From(JourneyPrerequisite p) => new(p.Label, State(p.State), p.Detail);

    public static string State(JourneyReadinessState s) => s switch
    {
        JourneyReadinessState.Ready => "Ready",
        JourneyReadinessState.Partial => "Partial",
        JourneyReadinessState.NotConfigured => "Not configured",
        JourneyReadinessState.NotAvailable => "Not available",
        _ => "Not ready",
    };

    public static string Verification(JourneyAssessmentState s, JourneyMaturity m) => s switch
    {
        JourneyAssessmentState.Verified => "Verified by a recorded run",
        JourneyAssessmentState.ExecutedNotVerified => "Executed, not verified",
        JourneyAssessmentState.Partial => "Partly verified",
        JourneyAssessmentState.Failed => "A recorded run failed",
        JourneyAssessmentState.Blocked => "Blocked",
        _ => m.SourceVerified ? "Not run (source evidence only — not runtime verified)" : "Not run",
    };

    public static Card Build(IntegrationJourneyView view)
    {
        var j = view.Journey;
        var mandatory = view.Prerequisites.Where(p => p.Mandatory).ToList();
        var integrations = j.Integrations.Select(r =>
        {
            var match = view.MatchedIntegrations.FirstOrDefault(m => m.RequirementKey == r.Key);
            var state = match is { IntegrationId: not null } ? (match.Enabled ? "Configured" : "Configured, disabled") : "Not configured";
            return new Item($"{r.Label} ({r.Producer} → {r.Consumer})", state, match?.Detail is { Length: > 0 } detail ? detail : r.Purpose);
        }).ToList();
        var blockers = mandatory.Where(p => p.State != JourneyReadinessState.Ready).Select(From).ToList();
        var next = blockers.Count == 0
            ? view.CanRun ? "Open the run on this tab and confirm it explicitly; nothing runs by itself." : "Prerequisites are met; execution is not offered for this journey here (see its execution mode)."
            : mandatory.First(p => p.State != JourneyReadinessState.Ready) is var first && first.Category switch
            {
                JourneyPrerequisiteCategory.Integration => "Configure the integration in Target Environments → Integration Catalog.",
                JourneyPrerequisiteCategory.SourceEvidence => "Bind the source in Integration Quality Review → Setup & Evidence (available is not bound).",
                JourneyPrerequisiteCategory.SyntheticData => "Prepare and approve the Test Data (saved is not approved).",
                JourneyPrerequisiteCategory.Authentication or JourneyPrerequisiteCategory.Authorization => "Set up authentication for this environment (Target Environments → Authentication).",
                JourneyPrerequisiteCategory.Contract => "Configure the contract (Integration Catalog → contracts).",
                JourneyPrerequisiteCategory.TrustedEnvironment => "Enable active execution for this environment (an administrator decision).",
                _ => $"Resolve: {first.Label}.",
            } is var action ? action : "";
        var history = view.LastRun is { } run
            ? $"Last recorded run {run.StartedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC: {run.State} · {run.VerifiedSteps}/{run.TotalSteps} steps verified. Historical — not current readiness."
            : null;
        return new Card(
            string.IsNullOrWhiteSpace(j.Description) ? "No purpose is declared for this journey." : j.Description,
            [.. j.Scenarios.Select(s => s.DisplayName)],
            integrations,
            [.. view.Prerequisites.Where(p => p.Category == JourneyPrerequisiteCategory.SourceEvidence).Select(From)],
            [.. view.Prerequisites.Where(p => p.Category == JourneyPrerequisiteCategory.SyntheticData).Select(From)],
            [.. view.Prerequisites.Where(p => p.Category is JourneyPrerequisiteCategory.Authentication or JourneyPrerequisiteCategory.Authorization).Select(From)],
            j.Steps.Count == 0 ? "No verification steps are declared." : $"{j.Steps.Count} step{(j.Steps.Count == 1 ? "" : "s")} to verify{(string.IsNullOrWhiteSpace(j.EvidenceBasis) ? "" : $" · evidence basis: {j.EvidenceBasis}")}",
            view.CanRun ? "Executable after explicit confirmation" : blockers.Count > 0 ? $"Blocked — {blockers.Count} prerequisite{(blockers.Count == 1 ? "" : "s")} not met" : "Not executable here",
            blockers,
            Verification(view.Assessment, view.Maturity),
            history,
            next);
    }
}
