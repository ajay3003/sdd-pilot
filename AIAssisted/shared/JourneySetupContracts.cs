using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// Guided Setup: a stateless, reconstructable view of what a journey still needs, derived every time from the authoritative journey readiness
// (prerequisites computed by the journey service from Target Environment, IQR, Test Data, Contract Evidence, runtime providers and the Test
// Pack). It owns no configuration and stores no wizard state: progress is recomputed from the owners whenever it is read. Viewing it saves nothing.

/// <summary>The shared setup step categories, in the order a journey is prepared.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JourneySetupStepKind { Integration, SourceEvidence, Contract, Environment, Authentication, Authorization, TestData, ExecutionProvider, RuntimeVerification, ReadyToRun }

/// <summary>One status vocabulary for every step.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JourneySetupStepStatus { Ready, NeedsAction, Blocked, Partial, Optional, NotApplicable }

/// <summary>Expected / configured / source verified / runtime verified stay separate facts of a step — never collapsed into "Verified".</summary>
public sealed record JourneySetupFacts(bool Expected, bool Configured, bool SourceVerified, bool RuntimeVerified);

public sealed record JourneySetupStep
{
    public JourneySetupStepKind Kind { get; init; }
    public string Title { get; init; } = "";
    public JourneySetupStepStatus Status { get; init; }
    /// <summary>Required for the journey (a mandatory prerequisite of this kind exists); optional steps never block progress.</summary>
    public bool Required { get; init; }
    /// <summary>What is required, in the tester's terms.</summary>
    public string Requirement { get; init; } = "";
    /// <summary>The exact blocker (first unresolved check), or empty when ready.</summary>
    public string Blocker { get; init; } = "";
    public string Owner { get; init; } = "";
    public string Provenance { get; init; } = "";
    public JourneySetupFacts Facts { get; init; } = new(false, false, false, false);
    /// <summary>The underlying prerequisite keys (open first), for diagnosis.</summary>
    public IReadOnlyList<string> Checks { get; init; } = [];
    public int OpenChecks { get; init; }
    public bool ManualOnly { get; init; }
}

public sealed record JourneySetupView
{
    public string PackId { get; init; } = "";
    public string JourneyId { get; init; } = "";
    public string JourneyName { get; init; } = "";
    public IReadOnlyList<JourneySetupStep> Steps { get; init; } = [];
    public int RequiredSteps { get; init; }
    public int RequiredReady { get; init; }
    /// <summary>The first required step that is not ready: where "Continue setup" resumes.</summary>
    public JourneySetupStepKind? NextStep { get; init; }
    public string NextAction { get; init; } = "";
    /// <summary>Setup complete = every required configuration prerequisite present. Never review passed, runtime verified or production ready.</summary>
    public bool SetupComplete => RequiredSteps > 0 && RequiredReady == RequiredSteps;
    /// <summary>Execution readiness is a separate answer (trusted environment, enrolment, confirmation).</summary>
    public string Execution { get; init; } = "";
}

/// <summary>Builds a journey's setup steps from its readiness view (pure; the same input always gives the same steps).</summary>
public static class JourneySetupModel
{
    public static readonly IReadOnlyList<(JourneySetupStepKind Kind, string Title, string Owner)> StepOrder =
    [
        (JourneySetupStepKind.Integration, "Integration", "Target Environment → Integrations"),
        (JourneySetupStepKind.SourceEvidence, "Source evidence", "Integration Quality Review → Setup & Evidence"),
        (JourneySetupStepKind.Contract, "Contract", "Contract & Schema Evidence"),
        (JourneySetupStepKind.Environment, "Environment", "Test pack run configuration"),
        (JourneySetupStepKind.Authentication, "Authentication", "Local execution config"),
        (JourneySetupStepKind.Authorization, "Authorization", "Local execution config"),
        (JourneySetupStepKind.TestData, "Test Data", "Test Data"),
        (JourneySetupStepKind.ExecutionProvider, "Execution provider", "Active Event Tests / journey runner"),
        (JourneySetupStepKind.RuntimeVerification, "Runtime verification", "Runtime evidence providers"),
        (JourneySetupStepKind.ReadyToRun, "Ready to run", "Target Environment (trusted execution)"),
    ];

    /// <summary>Which step a prerequisite belongs to. Run-configuration values (Altinn environment, app, base URL) are the Environment step.</summary>
    public static JourneySetupStepKind KindOf(JourneyPrerequisite item) => item.Key switch
    {
        "altinn-environment" or "altinn-app" or "public-metadata" => JourneySetupStepKind.Environment,
        _ when item.Key.StartsWith("integration:", StringComparison.Ordinal) && item.Category == JourneyPrerequisiteCategory.Integration && item.State != JourneyReadinessState.NotConfigured
            && !item.Key.EndsWith(":mapping", StringComparison.Ordinal) && item.Detail.Contains("not enabled for active runs", StringComparison.Ordinal) => JourneySetupStepKind.ReadyToRun,
        _ => item.Category switch
        {
            JourneyPrerequisiteCategory.Integration => JourneySetupStepKind.Integration,
            JourneyPrerequisiteCategory.SourceEvidence => JourneySetupStepKind.SourceEvidence,
            JourneyPrerequisiteCategory.Contract => JourneySetupStepKind.Contract,
            JourneyPrerequisiteCategory.Authentication => JourneySetupStepKind.Authentication,
            JourneyPrerequisiteCategory.Authorization => JourneySetupStepKind.Authorization,
            JourneyPrerequisiteCategory.SyntheticData => JourneySetupStepKind.TestData,
            JourneyPrerequisiteCategory.Executor or JourneyPrerequisiteCategory.Trigger => JourneySetupStepKind.ExecutionProvider,
            JourneyPrerequisiteCategory.Observation or JourneyPrerequisiteCategory.DownstreamVerification => JourneySetupStepKind.RuntimeVerification,
            JourneyPrerequisiteCategory.TrustedEnvironment => JourneySetupStepKind.ReadyToRun,
            _ => JourneySetupStepKind.ExecutionProvider,
        },
    };

    /// <summary>Checks only a person can do outside BirkNext (secrets in the backend secret store, authorization attestations, trust decisions).</summary>
    public static bool ManualOnly(JourneyPrerequisite item) => item.Key is "client-secret" or "app-policy" or "trusted-environment"
        || item.Detail.Contains("secret", StringComparison.OrdinalIgnoreCase) && item.Category == JourneyPrerequisiteCategory.Authentication;

    public static JourneySetupView Build(string packId, IntegrationJourneyView view)
    {
        var byKind = view.Prerequisites.GroupBy(KindOf).ToDictionary(group => group.Key, group => group.ToList());
        var steps = new List<JourneySetupStep>();
        foreach (var (kind, title, owner) in StepOrder)
        {
            if (kind == JourneySetupStepKind.ReadyToRun) continue;
            var items = byKind.GetValueOrDefault(kind) ?? [];
            if (items.Count == 0) continue; // a step the journey does not need is not shown
            steps.Add(Step(kind, title, owner, items, view));
        }
        // Ready to run: only the blockers that remain once every other step is considered (trust, enrolment) — summarized, never a pass.
        var runItems = byKind.GetValueOrDefault(JourneySetupStepKind.ReadyToRun) ?? [];
        var ready = Step(JourneySetupStepKind.ReadyToRun, "Ready to run", "Target Environment (trusted execution)", runItems, view) with
        {
            Requirement = "Every required step ready, the environment trusted for active execution and the integration enrolled; confirmation is still asked before anything runs.",
        };
        steps.Add(runItems.Count == 0 && view.CanRun ? ready with { Status = JourneySetupStepStatus.Ready, Blocker = "" } : ready);

        var required = steps.Where(step => step.Required).ToList();
        var next = required.FirstOrDefault(step => step.Status is not (JourneySetupStepStatus.Ready or JourneySetupStepStatus.NotApplicable));
        return new JourneySetupView
        {
            PackId = packId, JourneyId = view.Journey.JourneyId, JourneyName = view.Journey.DisplayName, Steps = steps,
            RequiredSteps = required.Count, RequiredReady = required.Count(step => step.Status == JourneySetupStepStatus.Ready),
            NextStep = next?.Kind, NextAction = next is null ? "Setup complete · not runtime verified" : $"{next.Title}: {next.Blocker}",
            Execution = view.CanRun ? "Executable after confirmation (not a pass)" : "Blocked",
        };
    }

    private static JourneySetupStep Step(JourneySetupStepKind kind, string title, string owner, List<JourneyPrerequisite> items, IntegrationJourneyView view)
    {
        var open = items.Where(item => item.State != JourneyReadinessState.Ready).ToList();
        var mandatory = items.Where(item => item.Mandatory).ToList();
        var openMandatory = open.Where(item => item.Mandatory).ToList();
        var status = items.Count == 0 ? JourneySetupStepStatus.NotApplicable
            : open.Count == 0 ? JourneySetupStepStatus.Ready
            : mandatory.Count == 0 ? JourneySetupStepStatus.Optional
            // A capability that does not exist yet (no verifier, no read contract) blocks; something a tester can configure needs action.
            : openMandatory.Any(item => item.State == JourneyReadinessState.NotAvailable) && openMandatory.All(item => item.State is JourneyReadinessState.NotAvailable) ? JourneySetupStepStatus.Blocked
            : openMandatory.Count < mandatory.Count || open.Any(item => item.State == JourneyReadinessState.Partial) ? JourneySetupStepStatus.Partial
            : JourneySetupStepStatus.NeedsAction;
        var first = openMandatory.FirstOrDefault() ?? open.FirstOrDefault();
        var configured = kind == JourneySetupStepKind.Integration ? view.MatchedIntegrations.Any(m => m.IntegrationId is not null) : items.Any(item => item.State == JourneyReadinessState.Ready);
        return new JourneySetupStep
        {
            Kind = kind, Title = title, Owner = owner, Status = status, Required = mandatory.Count > 0,
            Requirement = string.Join("; ", items.Select(item => item.Label).Distinct()),
            Blocker = first is null ? "" : $"{first.Label} — {first.Detail}",
            Provenance = string.Join(", ", items.Select(item => item.Source).OfType<string>().Distinct()),
            Facts = new(Expected: true, Configured: configured,
                SourceVerified: kind == JourneySetupStepKind.SourceEvidence && open.Count == 0,
                RuntimeVerified: false),
            Checks = [.. open.Select(item => item.Key).Concat(items.Where(item => item.State == JourneyReadinessState.Ready).Select(item => item.Key))],
            OpenChecks = open.Count, ManualOnly = open.Count > 0 && open.All(ManualOnly),
        };
    }
}
