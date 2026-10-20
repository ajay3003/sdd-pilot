using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.IntegrationJourneys;

/// <summary>What a journey pack sees when it evaluates its own prerequisites. Everything comes from shared configuration: the backend-owned
/// trusted environment, the Integration Quality Review catalog and the workspace's Source Analysis snapshots (newest first).</summary>
public sealed record JourneyPackContext(
    string EnvironmentId,
    TrustedExecutionEnvironment? TrustedEnvironment,
    string TrustDetail,
    IntegrationCatalog Catalog,
    IReadOnlyList<IqrSourceSnapshot> SourceSnapshots,
    IReadOnlyList<IntegrationEvidenceBinding>? EvidenceBindings = null)
{
    /// <summary>The canonical Integration Quality Review source-evidence bindings of this environment (one per integration).</summary>
    public IReadOnlyList<IntegrationEvidenceBinding> Bindings => EvidenceBindings ?? [];

    /// <summary>The snapshot an integration is bound to, if its binding still resolves to a stored snapshot.</summary>
    public IqrSourceSnapshot? BoundSnapshot(string integrationId) =>
        Bindings.FirstOrDefault(b => b.IntegrationId == integrationId) is { } binding ? SourceSnapshots.FirstOrDefault(s => s.Id == binding.SnapshotId) : null;
}

/// <summary>
/// A project/domain journey pack (an extension). It declares journeys, scenarios, integration requirements, domain prerequisites and
/// architecture responsibility rules. It owns no configuration, transport, safety, persistence or reporting: those are shared. A pack can
/// add blocking prerequisites; it cannot remove the core ones (trusted non-production environment, integrations, executors, confirmation).
/// </summary>
public interface IIntegrationJourneyPack
{
    string PackId { get; }
    string PackVersion { get; }
    string DisplayName { get; }
    string Description { get; }
    IReadOnlyList<IntegrationJourneyDefinition> Journeys { get; }
    IReadOnlyList<ArchitectureResponsibilityRule> Rules { get; }
    IReadOnlyList<string> Limitations { get; }
    /// <summary>Read-only verification views the pack offers next to its journeys (e.g. verifying what happened to one message). Default: none.</summary>
    IReadOnlyList<TestPackView> VerificationViews => [];
    /// <summary>Source/runtime evidence the pack needs, owned by Source Analysis or Integration Quality Review. Default: none declared.</summary>
    IReadOnlyList<TestPackCapabilityRequirement> RequiredEvidenceCapabilities => [];
    /// <summary>Generic Test Data capabilities the pack resolves through the shared Test Data catalog. Default: none.</summary>
    IReadOnlyList<TestPackCapabilityRequirement> RequiredTestDataCapabilities => [];
    /// <summary>The logical sources the pack needs (repositories/applications), each resolved through IQR bindings. Default: none declared.</summary>
    IReadOnlyList<TestPackSourceRole> SourceRoles => [];
    /// <summary>Setup templates for the pack's integrations (safe, deterministic prefill for Add integration). Default: none.</summary>
    IReadOnlyList<IntegrationTemplateDefinition> IntegrationTemplates => [];
    /// <summary>Configuration values the pack needs, by semantic key and logical source (never by file name). Default: none.</summary>
    IReadOnlyList<BirkNext.SourceDomains.SourceConfigurationRequirement> RequiredConfiguration => [];
    /// <summary>What the pack's consumers have stored for its configuration requirements (requirement key → value), so guidance can show
    /// configured next to suggested. Default: nothing stored.</summary>
    Task<IReadOnlyDictionary<string, string?>> ConfiguredValuesAsync(JourneyPackContext context, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>());
    /// <summary>Stores safe source-derived values (requirement key → value) into the pack's consumers' EMPTY fields only, and returns the keys
    /// it stored. The caller has already checked that each value is bound, unambiguous and not credential material. Default: stores nothing.</summary>
    Task<IReadOnlyList<string>> ApplyConfigurationAsync(JourneyPackContext context, IReadOnlyDictionary<string, string> values, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
    /// <summary>Whether the pack's runs are kept in the shared journey history.</summary>
    bool SupportsHistory => true;
    /// <summary>Whether a configured IQR integration fulfils one of this pack's integration requirements.</summary>
    bool Matches(JourneyIntegrationRequirement requirement, IntegrationDefinition integration);
    Task<IReadOnlyList<JourneyPrerequisite>> PrerequisitesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context, CancellationToken ct);

    /// <summary>
    /// Validates and normalizes a run's typed inputs (declared by the scenario) before anything executes, and describes them safely for
    /// history. The default accepts only a scenario without inputs.
    /// </summary>
    JourneyInputPreparation PrepareInputs(IntegrationJourneyDefinition journey, JourneyScenarioDescriptor scenario, IReadOnlyDictionary<string, string> inputs) =>
        inputs.Count == 0 ? JourneyInputPreparation.None : JourneyInputPreparation.Invalid("This scenario takes no inputs.");

    /// <summary>
    /// Pack-specific dry-run checks for a run with validated inputs: configuration, payload construction, endpoint allowlist and plan —
    /// strictly without any network call, token or state change. The default adds nothing (generic readiness checks still apply).
    /// </summary>
    Task<JourneyPackValidation> ValidateRunAsync(IntegrationJourneyDefinition journey, JourneyScenarioDescriptor scenario, IReadOnlyDictionary<string, string> values,
        JourneyPackContext context, CancellationToken ct) => Task.FromResult(JourneyPackValidation.None);

    /// <summary>
    /// The Payload &amp; Mapping Flow of a dry run (validated values, memory only) or of a recorded run — strictly without any network call.
    /// Stages say what was generated, submitted, observed or only expected; a pack without one returns null.
    /// </summary>
    Task<PayloadFlowView?> PayloadFlowAsync(IntegrationJourneyDefinition journey, JourneyScenarioDescriptor scenario, PayloadFlowRequest request,
        JourneyPackContext context, CancellationToken ct) => Task.FromResult<PayloadFlowView?>(null);

    /// <summary>
    /// Pack-level evidence limitations derived from the computed journey views (e.g. which prerequisites keep a scenario blocked) and the
    /// pack's declared knowledge of the target systems. Read-only; the default has none.
    /// </summary>
    IReadOnlyList<JourneyEvidenceLimitation> EvidenceLimitations(IReadOnlyList<IntegrationJourneyView> journeys) => [];

    /// <summary>
    /// Lets a pack add its own derived, sanitized record to a finished run before it is stored (e.g. a message verification's correlations and
    /// assertions, computed from what its observers recorded). Never changes step evidence or the run state; the default returns the run.
    /// </summary>
    IntegrationJourneyRun CompleteRun(IntegrationJourneyDefinition journey, JourneyScenarioDescriptor scenario, IntegrationJourneyRun run) => run;
}

/// <summary>Either a dry run's validated values (never persisted) or a recorded run (its stored, redacted evidence only).</summary>
public sealed record PayloadFlowRequest(IReadOnlyDictionary<string, string>? DryRunValues, IntegrationJourneyRun? Run);

/// <summary>A pack's dry-run checks and its redacted preview.</summary>
public sealed record JourneyPackValidation(IReadOnlyList<JourneyValidationCheck> Checks, IReadOnlyList<JourneyEvidenceFact> Preview)
{
    public static JourneyPackValidation None { get; } = new([], []);
}

/// <summary>Normalized run inputs (memory only, for the executors) and the redacted summary history keeps instead of them.</summary>
public sealed record JourneyInputPreparation(IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Errors, IReadOnlyList<string> Summary)
{
    public static JourneyInputPreparation None { get; } = new(new Dictionary<string, string>(), [], []);
    public static JourneyInputPreparation Invalid(params string[] errors) => new(new Dictionary<string, string>(), errors, []);
    public bool Valid => Errors.Count == 0;
}

/// <summary>Input to a step executor/observer. <see cref="Environment"/> is null for readiness probes (nothing is executed then).</summary>
public sealed record JourneyStepContext(
    Guid RunId,
    string EnvironmentId,
    TrustedExecutionEnvironment? Environment,
    IntegrationJourneyDefinition Journey,
    JourneyScenarioDescriptor? Scenario,
    JourneyStepDefinition Step,
    IntegrationDefinition? Integration,
    IReadOnlyList<JourneyStepResult> Previous,
    IReadOnlyDictionary<string, string>? Inputs = null,
    string? Initiator = null)
{
    /// <summary>The run's normalized inputs (never persisted).</summary>
    public IReadOnlyDictionary<string, string> InputValues => Inputs ?? new Dictionary<string, string>();
}

/// <summary>Performs an active step (e.g. a submission through the shared gateway). Must be read-only or explicitly state-changing by scenario.</summary>
public interface IJourneyStepExecutor
{
    bool CanExecute(JourneyStepContext context, out string reason);
    Task<JourneyStepResult> ExecuteAsync(JourneyStepContext context, CancellationToken ct);
}

/// <summary>Observes or verifies a step without causing it (telemetry, broker metadata, a read-only domain query).</summary>
public interface IJourneyStepObserver
{
    bool CanObserve(JourneyStepContext context, out string reason);
    Task<JourneyStepResult> ObserveAsync(JourneyStepContext context, CancellationToken ct);
}

public interface IIntegrationJourneyPackRegistry
{
    IReadOnlyList<IIntegrationJourneyPack> Packs { get; }
    IIntegrationJourneyPack? Find(string packId);
}

/// <summary>Compiled pack registration. Duplicate pack, journey or scenario ids fail when the registry is built.</summary>
public sealed class IntegrationJourneyPackRegistry : IIntegrationJourneyPackRegistry
{
    public IntegrationJourneyPackRegistry(IEnumerable<IIntegrationJourneyPack> packs)
    {
        Packs = packs.OrderBy(pack => pack.PackId, StringComparer.Ordinal).ToArray();
        if (Packs.GroupBy(pack => pack.PackId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } duplicatePack)
            throw new InvalidOperationException($"Duplicate integration journey pack '{duplicatePack.Key}'.");
        foreach (var pack in Packs)
        {
            if (pack.Journeys.GroupBy(journey => journey.JourneyId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } duplicateJourney)
                throw new InvalidOperationException($"Duplicate journey '{duplicateJourney.Key}' in pack '{pack.PackId}'.");
            if (pack.Journeys.Any(journey => !string.Equals(journey.PackId, pack.PackId, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Pack '{pack.PackId}' declares a journey owned by another pack.");
            foreach (var journey in pack.Journeys)
            {
                if (journey.Scenarios.GroupBy(scenario => scenario.ScenarioId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } duplicateScenario)
                    throw new InvalidOperationException($"Duplicate scenario '{duplicateScenario.Key}' in journey '{journey.JourneyId}'.");
                if (journey.Steps.GroupBy(step => step.StepId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } duplicateStep)
                    throw new InvalidOperationException($"Duplicate step '{duplicateStep.Key}' in journey '{journey.JourneyId}'.");
                if (journey.Steps.FirstOrDefault(step => step.IntegrationKey is { } key && journey.Integrations.All(item => item.Key != key)) is { } orphan)
                    throw new InvalidOperationException($"Step '{orphan.StepId}' references an integration requirement the journey does not declare.");
            }
            if (pack.Rules.GroupBy(rule => rule.RuleId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } duplicateRule)
                throw new InvalidOperationException($"Duplicate architecture rule '{duplicateRule.Key}' in pack '{pack.PackId}'.");
        }
    }

    public IReadOnlyList<IIntegrationJourneyPack> Packs { get; }
    public IIntegrationJourneyPack? Find(string packId) => Packs.FirstOrDefault(pack => string.Equals(pack.PackId, packId, StringComparison.Ordinal));
}
