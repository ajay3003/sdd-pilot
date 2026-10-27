using BirkNext.ContractSchemaEvidence;
using BirkNext.Delivery;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using BirkNext.TestEvidence;

namespace BirkNext.Api.Services.IntegrationJourneys;

/// <summary>What the journey service resolved before composing: the bound sources and the Test Data resolution (null: no shared catalog).</summary>
public sealed record JourneyReadinessInputs(
    IntegrationJourneyDefinition Journey,
    IReadOnlyList<JourneyPrerequisite> Prerequisites,
    IReadOnlyList<JourneyStepStatus> StepStatus,
    IReadOnlyList<string> RunnableScenarios,
    IReadOnlyList<IqrSourceSnapshot> BoundSnapshots,
    IReadOnlyList<TestDataRequirement> TestDataRequirements,
    TestDataResolutionResult? TestDataResolution,
    string? TestDataProblem = null);

/// <summary>
/// Generic journey readiness: one state per dimension, each read from the shared evidence owner it names (IQR bindings, Contract &amp; Schema
/// Evidence, Infrastructure Source Evidence, Delivery Evidence, canonical Test Evidence, the Test Data catalog, Target Environment). Nothing is
/// parsed here and no pack is named: a pack only declares requirements. The dimensions are never collapsed — declared is not deployed,
/// discovered is not linked, available Test Data is not executable, executable is not verified — and an optional dimension never blocks.
/// </summary>
public static class JourneyReadinessComposer
{
    public static IReadOnlyList<JourneyReadinessDimension> Compose(JourneyReadinessInputs input) =>
    [
        Integration(input), SourceBindings(input), Contracts(input), Infrastructure(input), Environment(input),
        TestData(input), TestEvidence(input), Execution(input), Verification(input), Delivery(input),
    ];

    /// <summary>The assessment of the last run, apart from readiness: a completed execution with unverified steps is not "Verified".</summary>
    public static JourneyAssessmentState Assessment(JourneyRunState? last) => last switch
    {
        null => JourneyAssessmentState.NotRun,
        JourneyRunState.Completed => JourneyAssessmentState.Verified,
        JourneyRunState.Partial => JourneyAssessmentState.Partial,
        JourneyRunState.NotVerified => JourneyAssessmentState.ExecutedNotVerified,
        JourneyRunState.Failed => JourneyAssessmentState.Failed,
        JourneyRunState.Blocked => JourneyAssessmentState.Blocked,
        _ => JourneyAssessmentState.NotRun,
    };

    private static IEnumerable<JourneyPrerequisite> Of(JourneyReadinessInputs input, params JourneyPrerequisiteCategory[] categories) =>
        input.Prerequisites.Where(p => categories.Contains(p.Category));

    /// <summary>Prerequisite states folded without hiding the worst: any missing → Missing, any not ready → Partial/Blocked, all ready → Ready.</summary>
    private static JourneyDimensionState Fold(IReadOnlyList<JourneyPrerequisite> items, JourneyDimensionState notReady = JourneyDimensionState.Partial)
    {
        var mandatory = items.Where(i => i.Mandatory).ToList();
        if (items.Count == 0) return JourneyDimensionState.NotAssessed;
        if (mandatory.Count > 0 && mandatory.All(i => i.State is JourneyReadinessState.NotConfigured or JourneyReadinessState.NotAvailable)) return JourneyDimensionState.Missing;
        if (mandatory.All(i => i.State == JourneyReadinessState.Ready)) return items.All(i => i.State == JourneyReadinessState.Ready) ? JourneyDimensionState.Ready : JourneyDimensionState.Partial;
        return notReady;
    }

    private static IReadOnlyList<string> Lines(IEnumerable<JourneyPrerequisite> items) => [.. items.Select(i => $"{i.Label}: {i.State} — {i.Detail}")];

    private static JourneyReadinessDimension Integration(JourneyReadinessInputs input)
    {
        var items = Of(input, JourneyPrerequisiteCategory.Integration).ToList();
        var state = input.Journey.Integrations.Count == 0 ? JourneyDimensionState.NotAssessed
            : items.Any(i => i.State == JourneyReadinessState.NotConfigured) ? JourneyDimensionState.Missing : Fold(items);
        var text = state switch
        {
            JourneyDimensionState.Ready => "Configured and enabled for active runs.",
            JourneyDimensionState.Missing => "A required integration is not configured.",
            JourneyDimensionState.NotAssessed => "The journey declares no integration.",
            _ => "Configured — not enabled for active runs in this environment (configured ≠ runtime verified).",
        };
        return new(JourneyDimension.Integration, "Integration", state, text, "Target Environment → Integration Catalog", "integrations") { Items = Lines(items), Provenance = "Integration catalog + Target Environment" };
    }

    private static JourneyReadinessDimension SourceBindings(JourneyReadinessInputs input)
    {
        var items = Of(input, JourneyPrerequisiteCategory.SourceEvidence).ToList();
        var state = items.Count == 0 && input.BoundSnapshots.Count > 0 ? JourneyDimensionState.Ready : Fold(items);
        var text = state switch
        {
            JourneyDimensionState.Ready => $"Bound in Integration Quality Review: {string.Join(", ", input.BoundSnapshots.Select(s => s.Archive.FileName).Distinct())}.",
            JourneyDimensionState.Missing => "No matching source evidence is bound or imported.",
            JourneyDimensionState.NotAssessed => "No source evidence is required or bound for this journey.",
            _ => "Source evidence is available but not (fully) bound — available ≠ bound.",
        };
        return new(JourneyDimension.SourceBindings, "Source bindings", state, text, "Integration Quality Review → Setup & Evidence", "iqr-evidence")
            { Items = Lines(items), Provenance = "IQR source-evidence bindings" };
    }

    private static JourneyReadinessDimension Contracts(JourneyReadinessInputs input)
    {
        var items = Of(input, JourneyPrerequisiteCategory.Contract).ToList();
        var facts = input.BoundSnapshots.Where(s => s.EvidenceDomains?.Contracts is { Contracts.Count: > 0 })
            .SelectMany(s => ContractEvidenceQueries.GetContracts(new ContractEvidenceSource(s.Id, s.Archive.FileName, s.AnalyzedAt, s.EvidenceDomains!.Contracts))).ToList();
        var lines = new List<string>(Lines(items));
        if (facts.Count > 0)
            lines.Add($"Contracts in bound source evidence: {facts.Count} ({string.Join(", ", facts.GroupBy(f => f.Contract.Type).Select(g => $"{g.Count()} {g.Key}"))})");
        lines.Add("Relationship to this journey: confirmed only by review — a similar name never confirms it.");
        lines.Add("Runtime validation: Not assessed (a contract in source is not a validated payload).");
        var state = items.Count > 0 ? Fold(items) : facts.Count > 0 ? JourneyDimensionState.ReviewRequired : JourneyDimensionState.NotAssessed;
        var text = state switch
        {
            JourneyDimensionState.Ready => "Contract prerequisites are available. Runtime validation is separate.",
            JourneyDimensionState.ReviewRequired => $"{facts.Count} contract(s) in bound source evidence; the producer/consumer relationship needs review.",
            JourneyDimensionState.Missing => "A required contract is missing.",
            JourneyDimensionState.NotAssessed => "No contract evidence is bound for this journey.",
            _ => "Contract evidence is partial.",
        };
        return new(JourneyDimension.Contracts, "Contracts", state, text, "Integration Quality Review / Contract & Schema Evidence", "source-contracts")
            { Items = lines, Provenance = "Contract & Schema Evidence (shared)" };
    }

    /// <summary>Generic resource-type tokens per integration kind (provider adapters name types this way); a kind without one is not assessed.</summary>
    private static string? ResourceToken(IntegrationKind kind) => kind switch
    {
        IntegrationKind.EventHub => "eventhub",
        IntegrationKind.ServiceBus => "servicebus",
        _ => null,
    };

    private static JourneyReadinessDimension Infrastructure(JourneyReadinessInputs input)
    {
        var tokens = input.Journey.Integrations.Select(i => ResourceToken(i.Kind)).OfType<string>().Distinct().ToList();
        var resources = input.BoundSnapshots.SelectMany(s => s.EvidenceDomains?.Infrastructure?.Resources ?? []).Where(r => r.Kind == "resource").ToList();
        var hasInfrastructure = input.BoundSnapshots.Any(s => s.EvidenceDomains?.Infrastructure is { Resources.Count: > 0 });
        if (tokens.Count == 0)
            return new(JourneyDimension.Infrastructure, "Infrastructure", JourneyDimensionState.NotAssessed, "No declared messaging resource applies to this journey's integrations.",
                "Source Analysis → Infrastructure", "source-infrastructure", Optional: true) { Provenance = "Infrastructure Source Evidence (shared)" };
        var matching = resources.Where(r => tokens.Any(t => r.ResourceType.Replace("_", "").Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        var state = !hasInfrastructure ? JourneyDimensionState.NotAssessed : matching.Count > 0 ? JourneyDimensionState.Partial : JourneyDimensionState.Missing;
        var text = state switch
        {
            JourneyDimensionState.Partial => $"Declared in source: {matching.Count} matching resource(s). Declared ≠ deployed ≠ runtime observed.",
            JourneyDimensionState.Missing => "Bound infrastructure evidence declares no matching resource.",
            _ => "No infrastructure evidence is bound (optional).",
        };
        return new(JourneyDimension.Infrastructure, "Infrastructure", state, text, "Source Analysis → Infrastructure", "source-infrastructure", Optional: true)
        {
            Items = [.. matching.Take(8).Select(r => $"{r.CategoryDetail} {r.DeclaredName ?? r.LogicalName}: declared in {r.Format} · mapped to application: not confirmed · runtime observed: no")],
            Provenance = "Infrastructure Source Evidence (shared)",
        };
    }

    private static JourneyReadinessDimension Environment(JourneyReadinessInputs input)
    {
        var items = Of(input, JourneyPrerequisiteCategory.TrustedEnvironment, JourneyPrerequisiteCategory.Authentication, JourneyPrerequisiteCategory.Authorization).ToList();
        var state = Fold(items, JourneyDimensionState.Blocked);
        var text = state == JourneyDimensionState.Ready ? "Trusted non-production environment with its configuration in place."
            : "Environment configuration or execution permission is incomplete — owned by Target Environment, not by this test pack.";
        return new(JourneyDimension.EnvironmentConfiguration, "Environment configuration", state, text, "Target Environment", "execution-settings")
            { Items = Lines(items), Provenance = "Target Environment" };
    }

    private static JourneyReadinessDimension TestData(JourneyReadinessInputs input)
    {
        var items = Of(input, JourneyPrerequisiteCategory.SyntheticData).ToList();
        var lines = new List<string>(Lines(items));
        var resolution = input.TestDataResolution;
        if (resolution is not null)
            foreach (var role in resolution.Roles)
                lines.Add($"{role.Role}: {(role.State is TestDataResolutionState.Resolved or TestDataResolutionState.Selected ? $"approved ({role.AssetAlias})" : role.State == TestDataResolutionState.Ambiguous ? $"{role.Candidates.Count} approved candidates — the tester chooses" : "missing")}{(role.Optional ? " · optional" : "")}");
        if (input.TestDataProblem is { } problem) lines.Add(problem);
        var required = resolution?.Roles.Where(r => !r.Optional).ToList() ?? [];
        var state = input.TestDataRequirements.Count == 0 && items.Count == 0 ? JourneyDimensionState.NotAssessed
            : resolution is null && items.Count == 0 ? JourneyDimensionState.NotAssessed
            : required.Any(r => r.State == TestDataResolutionState.Unresolved) || items.Any(i => i.Mandatory && i.State is JourneyReadinessState.NotConfigured or JourneyReadinessState.NotAvailable) ? JourneyDimensionState.Missing
            : required.Any(r => r.State == TestDataResolutionState.Ambiguous) || items.Any(i => i.State != JourneyReadinessState.Ready)
                || (resolution?.Roles.Any(r => r.Optional && r.State == TestDataResolutionState.Unresolved) ?? false) ? JourneyDimensionState.Partial
            : JourneyDimensionState.Ready;
        var text = state switch
        {
            JourneyDimensionState.Ready => "Approved Test Data is available for every role. Available ≠ executable: execution has its own gate.",
            JourneyDimensionState.Missing => "Approved Test Data is missing for a required role — a journey prerequisite, not a product defect.",
            JourneyDimensionState.Partial => "Some Test Data is missing, optional or needs a choice.",
            _ => input.TestDataProblem ?? "No Test Data requirement is declared or the shared catalog is unavailable.",
        };
        return new(JourneyDimension.TestData, "Test Data", state, text, "Test Data", "test-data") { Items = lines, Provenance = "Test Data catalog (shared)" };
    }

    private static JourneyReadinessDimension TestEvidence(JourneyReadinessInputs input)
    {
        var inventories = input.BoundSnapshots.Select(s => s.TestInventory).OfType<SourceTestInventory>().ToList();
        if (inventories.Count == 0)
            return new(JourneyDimension.TestEvidence, "Test evidence", JourneyDimensionState.NotAssessed, "No test inventory in the bound source evidence.", "Test Coverage", "test-coverage", Optional: true)
                { Provenance = "Test Evidence (shared)" };
        var tests = inventories.SelectMany(TestEvidenceQueries.FromInventory).ToList();
        // Explicit/strong requirement links to the journey or one of its scenarios (the traceability relationship); a name match never links.
        var linked = new[] { input.Journey.JourneyId }.Concat(input.Journey.Scenarios.Select(s => s.ScenarioId))
            .SelectMany(id => TestEvidenceQueries.ForRequirement(tests, id)).DistinctBy(t => t.Id).ToList();
        var journeyLayer = tests.Where(t => t.Layer is TestLayer.Integration or TestLayer.Contract or TestLayer.EndToEnd).ToList();
        var state = linked.Count > 0 ? JourneyDimensionState.Ready : journeyLayer.Count > 0 ? JourneyDimensionState.ReviewRequired : JourneyDimensionState.Missing;
        var text = state switch
        {
            JourneyDimensionState.Ready => $"{linked.Count} test(s) explicitly linked to this journey (discovered in source; not executed here).",
            JourneyDimensionState.ReviewRequired => $"{journeyLayer.Count} integration/contract/E2E test(s) in bound sources; none is explicitly linked to this journey.",
            _ => "Bound sources contain no integration, contract or E2E tests.",
        };
        return new(JourneyDimension.TestEvidence, "Test evidence", state, text, "Test Coverage", "test-coverage", Optional: true)
            { Items = [.. linked.Concat(journeyLayer).DistinctBy(t => t.Id).Take(8).Select(t => $"{t.ClassName}.{t.MethodName} ({t.Layer}, {t.State})")], Provenance = "Test Evidence (shared)" };
    }

    private static JourneyReadinessDimension Execution(JourneyReadinessInputs input)
    {
        var items = Of(input, JourneyPrerequisiteCategory.Executor, JourneyPrerequisiteCategory.Trigger).ToList();
        var delegated = input.Journey.ExecutionMode == JourneyExecutionMode.DelegatedToActiveEvent;
        var state = input.RunnableScenarios.Count > 0 ? JourneyDimensionState.Ready
            : delegated ? (Fold(items, JourneyDimensionState.Blocked) is JourneyDimensionState.Ready ? JourneyDimensionState.Ready : JourneyDimensionState.Blocked)
            : JourneyDimensionState.Blocked;
        var text = state == JourneyDimensionState.Ready
            ? delegated ? "Executed by the delegated engine; allowed ≠ started." : $"{input.RunnableScenarios.Count} scenario(s) can run after confirmation — allowed ≠ started."
            : "No scenario can run yet: an execution prerequisite is not met (see the other dimensions).";
        return new(JourneyDimension.Execution, "Execution", state, text, "Target Environment / execution safety", "execution-settings") { Items = Lines(items), Provenance = "Journey runner + Target Environment" };
    }

    private static JourneyReadinessDimension Verification(JourneyReadinessInputs input)
    {
        var mandatory = input.Journey.Steps.Where(s => s.Mandatory).Select(s => s.StepId).ToHashSet(StringComparer.Ordinal);
        var verifiable = input.StepStatus.Count(s => mandatory.Contains(s.StepId) && s.Verifiable);
        var items = Of(input, JourneyPrerequisiteCategory.Observation, JourneyPrerequisiteCategory.DownstreamVerification).ToList();
        var state = mandatory.Count == 0 ? JourneyDimensionState.NotAssessed
            : verifiable == mandatory.Count ? JourneyDimensionState.Ready : verifiable > 0 ? JourneyDimensionState.Partial : JourneyDimensionState.NotAssessed;
        var text = state switch
        {
            JourneyDimensionState.Ready => "Every mandatory step has a registered observer.",
            JourneyDimensionState.Partial => $"{verifiable} of {mandatory.Count} mandatory steps are observable; the rest stay Not assessed (a run reaches Partial at most).",
            _ => "No runtime verification provider observes this journey's steps here.",
        };
        return new(JourneyDimension.Verification, "Verification", state, text, "Runtime evidence / Verify", "verify") { Items = Lines(items), Provenance = "Runtime verification providers" };
    }

    private static JourneyReadinessDimension Delivery(JourneyReadinessInputs input)
    {
        var pipelines = input.BoundSnapshots.Select(s => s.EvidenceDomains?.CiCd).OfType<PipelineEvidence>().Where(p => p.Pipelines.Count > 0).ToList();
        if (pipelines.Count == 0)
            return new(JourneyDimension.Delivery, "Delivery", JourneyDimensionState.NotAssessed, "No pipeline evidence in the bound sources (optional).", "Pipeline Review", "pipeline-review", Optional: true)
                { Provenance = "Pipeline & Delivery Evidence (shared)" };
        var steps = pipelines.SelectMany(p => p.Pipelines.SelectMany(d => d.Steps)).ToList();
        var tests = steps.Count(s => s.Kind is PipelineStepKind.IntegrationTest or PipelineStepKind.E2ETest or PipelineStepKind.Test);
        var contract = pipelines.Sum(p => DeliveryEvidenceQueries.ContractValidationSteps(p).Count);
        var deploys = steps.Count(s => s.Kind is PipelineStepKind.ApplicationDeploy or PipelineStepKind.InfrastructureDeploy);
        return new(JourneyDimension.Delivery, "Delivery", JourneyDimensionState.Partial,
            $"Configured in pipelines: {deploys} deployment step(s), {tests} integration/E2E test step(s), {contract} contract validation step(s). Configured ≠ executed ≠ passed.",
            "Pipeline Review", "pipeline-review", Optional: true) { Provenance = "Pipeline & Delivery Evidence (shared)" };
    }
}
