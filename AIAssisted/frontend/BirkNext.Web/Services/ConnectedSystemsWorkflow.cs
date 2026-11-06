using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Target Environment → Connected Systems as guided steps: environment → detect → review candidates → configure → validate. Derived only from the
/// pane's own state (configured systems from the server, candidates from this session's last Detect) — no second readiness model.
/// Detected ≠ registered; registered ≠ configured; configured ≠ reachable. Runtime is "verified" only when a system's own Runtime dimension says so.
/// </summary>
public static class ConnectedSystemsWorkflow
{
    public const string Detect = "detect", ReviewCandidates = "review-candidates", Add = "add", ReviewAttention = "review-attention";

    public static IReadOnlyList<GuidedStep> Derive(string environmentId, string? environmentName, ExternalIntegrationsView view, DateTimeOffset? detectedAt)
    {
        var systems = view.Integrations;
        var pending = view.Proposals.Count;
        var ambiguous = view.Proposals.Count(p => p.Ambiguous);
        var attention = systems.Where(NeedsAttention).ToList();
        var runtimeVerified = systems.Count(s => s.Summary.Runtime.State == SetupReadinessState.Verified);
        var runtimeObserved = systems.Count(s => s.Summary.Runtime.State == SetupReadinessState.Observed);
        var runtimeUnchecked = systems.Count - runtimeVerified - runtimeObserved;
        var steps = new List<GuidedStep>
        {
            new("environment", "Environment", string.IsNullOrWhiteSpace(environmentId) ? GuidedStepState.Blocked : GuidedStepState.Done,
                string.IsNullOrWhiteSpace(environmentId) ? "Not selected" : "Selected",
                string.IsNullOrWhiteSpace(environmentId) ? "Select a Target Environment first: connected systems are configured per environment."
                    : $"Systems below belong to {environmentName ?? "the active Target Environment"}. Another environment has its own systems."),
        };

        steps.Add(detectedAt is null
            ? new("detect", "Detect systems", systems.Count == 0 ? GuidedStepState.Current : GuidedStepState.Waiting, "Not run in this session",
                "Detect proposes systems that a provider recognises in the project's imported evidence. Nothing is saved until you accept a candidate.",
                ActionLabel: "Detect systems", ActionId: Detect)
            : new("detect", "Detect systems", GuidedStepState.Done, $"Detected {detectedAt.Value.ToLocalTime():d MMM HH:mm}",
                pending == 0
                    ? "No new system was recognised. Integration Catalog proposals are logical integrations: they do not register systems here, and accepting one there does not require a system here. Add a system manually when an integration needs one."
                    : $"{pending} candidate{(pending == 1 ? "" : "s")} proposed from imported evidence. Nothing was saved.",
                ActionLabel: "Detect again", ActionId: Detect));

        steps.Add(pending > 0
            ? new("candidates", "Review candidates", GuidedStepState.Current, $"{pending} awaiting a decision",
                ambiguous > 0
                    ? $"{pending - ambiguous} can be accepted; {ambiguous} match more than one system and need a person's decision (nothing is accepted for you)."
                    : "Accept each candidate explicitly, or ignore it for this session. Accepting registers the system; it does not verify it.",
                ActionLabel: "Go to candidates", ActionId: ReviewCandidates)
            : new("candidates", "Review candidates", detectedAt is null ? GuidedStepState.Waiting : GuidedStepState.Done,
                detectedAt is null ? "Waiting for detection" : "No pending candidates",
                detectedAt is null ? "Candidates appear after Detect." : "Every detected candidate has been accepted or ignored, or none was found."));

        steps.Add(systems.Count == 0
            ? new("configure", "Configure systems", pending > 0 || detectedAt is null ? GuidedStepState.Waiting : GuidedStepState.Current, "No system registered",
                "Register a system by accepting a candidate or adding it manually; then complete its configuration and authentication.",
                ActionLabel: "Add system manually", ActionId: Add)
            : attention.Count > 0
                ? new("configure", "Configure systems", GuidedStepState.Current, $"{attention.Count} of {systems.Count} need attention",
                    $"Needs attention: {string.Join(", ", attention.Take(3).Select(a => a.Integration.Name))}{(attention.Count > 3 ? "…" : "")} — a configuration, dependency, authentication, runtime or execution dimension is partial or blocked.",
                    Evidence: $"{systems.Count(s => s.Integration.Enabled)} of {systems.Count} enabled.",
                    ActionLabel: "Review first system", ActionId: ReviewAttention)
                : new("configure", "Configure systems", GuidedStepState.Done, $"{systems.Count} registered",
                    "Every registered system has its configuration, dependencies and authentication in place.",
                    Evidence: $"{systems.Count(s => s.Integration.Enabled)} of {systems.Count} enabled."));

        steps.Add(systems.Count == 0
            ? new("validate", "Validate", GuidedStepState.Waiting, "Not assessed", "Validation applies to registered systems.")
            : new("validate", "Validate", runtimeVerified == systems.Count ? GuidedStepState.Done : GuidedStepState.Waiting,
                runtimeVerified == systems.Count ? "Runtime verified" : runtimeVerified + runtimeObserved == 0 ? "Not verified" : "Partly verified",
                "Runtime connectivity is verified per system by explicit checks or recorded observations only — never by registration or a saved configuration.",
                Evidence: $"Runtime: {runtimeVerified} verified · {runtimeObserved} observed · {runtimeUnchecked} not verified or not assessed."));

        // Only one current step: the first one; later "current" steps wait for it.
        var first = steps.FindIndex(s => s.State == GuidedStepState.Current);
        return [.. steps.Select((s, i) => i > first && first >= 0 && s.State == GuidedStepState.Current ? s with { State = GuidedStepState.Waiting } : s)];
    }

    /// <summary>A registered system needs attention when any of its dimensions is Partial or Blocked (the cards' own rule).</summary>
    public static bool NeedsAttention(ExternalIntegrationView item) =>
        ExternalIntegrationPresentation.Dimensions(item.Summary).Any(d => ExternalIntegrationPresentation.Tone(d.State) is "warn" or "bad");
}
