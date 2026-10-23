using BirkNext.Integrations;

namespace BirkNext.PerformanceTests;

/// <summary>
/// Performance Test Review as a consumer of the shared Guided Setup model (<see cref="JourneySetupView"/>): the steps are derived every time from the
/// definition and the backend readiness — nothing is stored, viewing saves nothing, and no Performance-specific wizard exists. Setup complete is
/// never "performance passed": it only says a run may be started after explicit confirmation.
/// </summary>
public static class PerformanceSetupModel
{
    public const string PackId = "performance-test-review";

    public static readonly IReadOnlyList<(JourneySetupStepKind Kind, string Title, string Owner)> StepOrder =
    [
        (JourneySetupStepKind.Target, "Target", "Target Environment"),
        (JourneySetupStepKind.Environment, "Environment", "Target Environment (production guard)"),
        (JourneySetupStepKind.Scenario, "Definition & scenario", "Performance Test Review → Scenario"),
        (JourneySetupStepKind.ExecutionEngine, "Execution engine", "System Settings → Performance Test Engines"),
        (JourneySetupStepKind.ContainerRuntime, "Container runtime", "Local execution configuration"),
        (JourneySetupStepKind.NetworkReachability, "Network reachability", "Performance Test Review → container network check"),
        (JourneySetupStepKind.CaTrust, "Corporate CA trust", "Local execution configuration"),
        (JourneySetupStepKind.Authentication, "Authentication", "Target Environment authentication"),
        (JourneySetupStepKind.Workload, "Workload", "Performance Test Review → Workload"),
        (JourneySetupStepKind.Thresholds, "Thresholds", "Performance Test Review → Thresholds"),
        (JourneySetupStepKind.ResourceObservation, "Resource observation", "Performance Test Review → Resources"),
        (JourneySetupStepKind.Baseline, "Baseline", "Performance Test Review → History (explicit promotion)"),
    ];

    /// <summary>Which setup step a readiness item belongs to.</summary>
    public static JourneySetupStepKind? KindOf(PerformanceReadinessItem item) => item.Key switch
    {
        "target" => JourneySetupStepKind.Target,
        "environment" => JourneySetupStepKind.Environment,
        "scenario" or "testdata" => JourneySetupStepKind.Scenario,
        "provider" when item.Category == PerformanceReadinessCategories.Definition => JourneySetupStepKind.Scenario,
        "provider" or "image" => JourneySetupStepKind.ExecutionEngine,
        "runtime" => JourneySetupStepKind.ContainerRuntime,
        "network" => JourneySetupStepKind.NetworkReachability,
        "ca-trust" or "tls" => JourneySetupStepKind.CaTrust,
        "authentication" => JourneySetupStepKind.Authentication,
        "workload" or "limits" => JourneySetupStepKind.Workload,
        "thresholds" => JourneySetupStepKind.Thresholds,
        "baseline" => JourneySetupStepKind.Baseline,
        _ when item.Key.StartsWith("resource", StringComparison.Ordinal) => JourneySetupStepKind.ResourceObservation,
        _ => null, // observability: informational only
    };

    private static readonly HashSet<JourneySetupStepKind> CoreBeforeReadiness =
        [JourneySetupStepKind.Target, JourneySetupStepKind.Environment, JourneySetupStepKind.ExecutionEngine, JourneySetupStepKind.ContainerRuntime,
         JourneySetupStepKind.NetworkReachability, JourneySetupStepKind.Workload];

    /// <param name="draftValid">A new draft has every required field (it will be saved automatically as v1 before readiness).</param>
    public static JourneySetupView Build(PerformanceTestDefinition definition, bool isNew, bool dirty, bool draftValid, PerformanceTestReadiness? readiness)
    {
        var evaluated = readiness is not null && !isNew && !dirty;
        var byKind = (evaluated ? readiness!.Items : []).Select(item => (Kind: KindOf(item), Item: item)).Where(x => x.Kind is not null)
            .GroupBy(x => x.Kind!.Value).ToDictionary(g => g.Key, g => g.Select(x => x.Item).ToList());
        var steps = new List<JourneySetupStep>();
        foreach (var (kind, title, owner) in StepOrder)
        {
            var items = byKind.GetValueOrDefault(kind) ?? [];
            if (kind == JourneySetupStepKind.Scenario)
            {
                steps.Add(ScenarioStep(definition, isNew, dirty, draftValid, readiness is not null, items, owner));
                continue;
            }
            if (!evaluated)
            {
                if (!CoreBeforeReadiness.Contains(kind)) continue; // optional steps appear once the backend has evaluated the definition
                steps.Add(new JourneySetupStep
                {
                    Kind = kind, Title = title, Owner = owner, Status = JourneySetupStepStatus.NeedsAction, Required = true,
                    Requirement = title, Blocker = isNew || dirty ? "Not evaluated yet — Check readiness evaluates it on the saved definition." : "Readiness could not be loaded.",
                    Facts = new(true, false, false, false),
                });
                continue;
            }
            if (items.Count == 0) continue; // a step this definition does not need is not shown
            steps.Add(Step(kind, title, owner, items));
        }
        var blockers = evaluated ? readiness!.Items.Count(i => i.Blocking) : 0;
        steps.Add(new JourneySetupStep
        {
            Kind = JourneySetupStepKind.ReadyToRun, Title = "Ready to run", Owner = "Performance Test Review → Run (explicit confirmation)", Required = true,
            Status = evaluated && readiness!.Ready ? JourneySetupStepStatus.Ready : JourneySetupStepStatus.NeedsAction,
            Requirement = "Every required readiness check passes; a run still needs an explicit Run action and sends real load.",
            Blocker = evaluated && readiness!.Ready ? "" : evaluated ? $"{blockers} readiness blocker{(blockers == 1 ? "" : "s")}." : "Check readiness first.",
            Facts = new(true, evaluated && readiness!.Ready, false, false),
        });
        var required = steps.Where(step => step.Required).ToList();
        var next = required.FirstOrDefault(step => step.Status is not (JourneySetupStepStatus.Ready or JourneySetupStepStatus.NotApplicable));
        return new JourneySetupView
        {
            PackId = PackId, JourneyId = $"performance-{(definition.Id.Length > 12 ? definition.Id[..12] : definition.Id)}", JourneyName = definition.Name, Steps = steps,
            RequiredSteps = required.Count, RequiredReady = required.Count(step => step.Status == JourneySetupStepStatus.Ready),
            NextStep = next?.Kind, NextAction = next is null ? "Setup complete · not runtime verified" : $"{next.Title}: {next.Blocker}",
            Execution = evaluated && readiness!.Ready ? "Ready to run after explicit confirmation (not a pass)" : "Blocked",
        };
    }

    private static JourneySetupStep ScenarioStep(PerformanceTestDefinition d, bool isNew, bool dirty, bool draftValid, bool evaluated, List<PerformanceReadinessItem> items, string owner)
    {
        var open = items.Where(i => i.Blocking).ToList();
        var (status, blocker) = isNew
            ? draftValid ? (JourneySetupStepStatus.NeedsAction, "Draft — saved automatically as v1 when you check readiness.")
                : (JourneySetupStepStatus.NeedsAction, "Draft — complete the required fields (name, at least one step with a relative path).")
            : dirty ? (JourneySetupStepStatus.NeedsAction, $"Unsaved changes to v{d.Version} — save them as a new version before readiness.")
            : open.Count > 0 ? (JourneySetupStepStatus.NeedsAction, $"{open[0].Label} — {open[0].Detail}")
            : evaluated ? (JourneySetupStepStatus.Ready, "") : (JourneySetupStepStatus.NeedsAction, "Readiness could not be loaded.");
        return new JourneySetupStep
        {
            Kind = JourneySetupStepKind.Scenario, Title = "Definition & scenario", Owner = owner, Status = status, Required = true,
            Requirement = $"A saved definition version with at least one read-only request step ({d.Scenario.Steps.Count} configured).", Blocker = blocker,
            Provenance = isNew ? "Prefilled from the Target Environment (not saved)" : $"Saved definition v{d.Version}",
            Facts = new(true, !isNew && !dirty, false, false), Checks = [.. items.Select(i => i.Key)], OpenChecks = open.Count,
        };
    }

    private static readonly HashSet<PerformanceReadinessState> Hard = [PerformanceReadinessState.UnsafeEnvironment, PerformanceReadinessState.Blocked, PerformanceReadinessState.InvalidScenario];

    private static JourneySetupStep Step(JourneySetupStepKind kind, string title, string owner, List<PerformanceReadinessItem> items)
    {
        var open = items.Where(i => i.Blocking).ToList();
        var required = items.Any(i => i.Blocking || i.State != PerformanceReadinessState.Optional);
        var status = open.Count > 0 ? open.Any(i => Hard.Contains(i.State)) ? JourneySetupStepStatus.Blocked : JourneySetupStepStatus.NeedsAction
            : !required ? JourneySetupStepStatus.Optional : JourneySetupStepStatus.Ready;
        var first = open.FirstOrDefault();
        var display = items.FirstOrDefault(i => i.Status is not null) ?? items[0];
        return new JourneySetupStep
        {
            Kind = kind, Title = title, Owner = first?.Owner ?? owner, Status = status, Required = required,
            Requirement = string.Join("; ", items.Select(i => i.Label).Distinct()),
            Blocker = first is null ? status == JourneySetupStepStatus.Optional ? display.Detail : "" : $"{(first.Headline.Length > 0 ? first.Headline : first.Label)} — {first.Detail}",
            Provenance = string.Join(", ", items.Select(i => i.ConfigurationKey).OfType<string>().Distinct()),
            // Runtime verified only where a check actually ran from the load generator (the container network check).
            Facts = new(true, status == JourneySetupStepStatus.Ready, false, kind == JourneySetupStepKind.NetworkReachability && status == JourneySetupStepStatus.Ready),
            Checks = [.. open.Select(i => i.Key).Concat(items.Where(i => !i.Blocking).Select(i => i.Key))], OpenChecks = open.Count,
            ManualOnly = open.Count > 0 && open.All(i => i.Owner == "Local execution configuration"),
        };
    }
}
