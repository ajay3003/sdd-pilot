using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting;

public sealed record ActiveEventProjectEvidenceContext(string TargetEnvironmentId, IntegrationDefinition? Integration, IntegrationPlatform? Platform);

public sealed record ActiveEventScenarioPreparation
{
    public bool Compatible { get; init; }
    public string Detail { get; init; } = "";
    public ActiveEventSourceContractReference Contract { get; init; } = new();
    public IReadOnlyList<ActiveEventReadinessCheck> Checks { get; init; } = [];
    public IReadOnlyDictionary<string, string> SafeMetadata { get; init; } = new Dictionary<string, string>();
    /// <summary>Safe preview for the confirmation step (identity ranges, fields, operations) — never a payload.</summary>
    public IReadOnlyDictionary<string, string> SyntheticSummary { get; init; } = new Dictionary<string, string>();
}

public sealed record ActiveEventScenarioGeneration
{
    public IReadOnlyList<GeneratedActiveEvent> Events { get; init; } = [];
    public IReadOnlyDictionary<string, string> SafeMetadata { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>
/// Project/domain extension: decides applicability from integration/source evidence, validates its source contract, owns its synthetic
/// identities and creates bounded synthetic events. It cannot send, choose a destination, or relax a core safety check: its readiness checks
/// are added after the core's and can only block.
/// </summary>
public interface IActiveEventScenarioProvider
{
    string ExtensionId { get; }
    string ExtensionVersion { get; }
    string DisplayName { get; }
    string Description { get; }
    /// <summary>Source resources the provider generates events for (shown when no integration applies).</summary>
    IReadOnlyList<string> Resources { get; }
    /// <summary>Every scenario the provider declares, independent of configuration (display only; execution uses <see cref="GetScenarios"/>).</summary>
    IReadOnlyList<ActiveEventScenarioDescriptor> CatalogScenarios { get; }
    bool CanApply(ActiveEventProjectEvidenceContext context);
    IReadOnlyList<ActiveEventScenarioDescriptor> GetScenarios(ActiveEventProjectEvidenceContext context);
    Task<ActiveEventScenarioPreparation> PrepareAsync(string scenarioId, ActiveEventProjectEvidenceContext context, Guid? sourceSnapshotId, CancellationToken ct);
    Task<ActiveEventScenarioGeneration> GenerateAsync(string scenarioId, Guid runId, ActiveEventTrustedTarget target,
        ActiveEventSourceContractReference contract, CancellationToken ct);
}

public interface IActiveEventScenarioRegistry
{
    IReadOnlyList<IActiveEventScenarioProvider> Providers { get; }
    IReadOnlyList<IActiveEventScenarioProvider> Applicable(ActiveEventProjectEvidenceContext context);
    IActiveEventScenarioProvider? Find(string extensionId);
    IActiveEventScenarioProvider? FindScenario(string scenarioId, ActiveEventProjectEvidenceContext context);
    IReadOnlyList<ActiveEventScenarioDescriptor> Scenarios(ActiveEventProjectEvidenceContext context);
}

/// <summary>Duplicate extension ids or scenario ids (across all providers' declared catalogs) fail at construction, i.e. at first resolution.</summary>
public sealed class ActiveEventScenarioRegistry : IActiveEventScenarioRegistry
{
    private readonly IReadOnlyList<IActiveEventScenarioProvider> _providers;

    public ActiveEventScenarioRegistry(IEnumerable<IActiveEventScenarioProvider> providers)
    {
        _providers = providers.OrderBy(p => p.ExtensionId, StringComparer.Ordinal).ToArray();
        var duplicateExtension = _providers.GroupBy(p => p.ExtensionId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateExtension is not null) throw new InvalidOperationException($"Duplicate Active Event extension ID '{duplicateExtension.Key}'.");
        var duplicate = _providers.SelectMany(p => p.CatalogScenarios).GroupBy(item => item.ScenarioId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Duplicate Active Event scenario ID '{duplicate.Key}'. Scenario IDs must be unique across registered extensions.");
    }

    public IReadOnlyList<IActiveEventScenarioProvider> Providers => _providers;
    public IReadOnlyList<IActiveEventScenarioProvider> Applicable(ActiveEventProjectEvidenceContext context) => _providers.Where(p => p.CanApply(context)).ToArray();
    public IActiveEventScenarioProvider? Find(string extensionId) => _providers.FirstOrDefault(p => string.Equals(p.ExtensionId, extensionId, StringComparison.Ordinal));

    public IActiveEventScenarioProvider? FindScenario(string scenarioId, ActiveEventProjectEvidenceContext context) =>
        Applicable(context).SingleOrDefault(provider => provider.GetScenarios(context).Any(item => string.Equals(item.ScenarioId, scenarioId, StringComparison.Ordinal)));

    public IReadOnlyList<ActiveEventScenarioDescriptor> Scenarios(ActiveEventProjectEvidenceContext context)
    {
        var scenarios = Applicable(context).SelectMany(provider => provider.GetScenarios(context)).ToArray();
        var duplicate = scenarios.GroupBy(item => item.ScenarioId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Duplicate Active Event scenario ID '{duplicate.Key}'. Scenario IDs must be unique across registered extensions.");
        return scenarios;
    }
}

/// <summary>Technology extension: sends opaque generated bytes to a trusted target without interpreting their domain.</summary>
public interface IActiveEventTransportProvider
{
    string TransportType { get; }
    bool CanSend(ActiveEventTrustedTarget target, out string reason);
    Task<ActiveEventStageEvidence> SendAsync(ActiveEventTrustedTarget target, GeneratedActiveEvent activeEvent, TimeSpan timeout, CancellationToken ct);
}

public interface IActiveEventTransportRegistry
{
    IActiveEventTransportProvider? Resolve(string transportType);
}

public sealed class ActiveEventTransportRegistry(IEnumerable<IActiveEventTransportProvider> providers) : IActiveEventTransportRegistry
{
    private readonly IReadOnlyDictionary<string, IActiveEventTransportProvider> _providers = BuildProviders(providers);
    public IActiveEventTransportProvider? Resolve(string transportType) => _providers.GetValueOrDefault(transportType);

    private static IReadOnlyDictionary<string, IActiveEventTransportProvider> BuildProviders(IEnumerable<IActiveEventTransportProvider> providers)
    {
        var items = providers.ToArray();
        var duplicate = items.GroupBy(provider => provider.TransportType, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Duplicate Active Event transport provider '{duplicate.Key}'.");
        return items.ToDictionary(provider => provider.TransportType, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>What observation providers may read: the trusted target plus the configured integration/platform (monitoring references live there).</summary>
public sealed record ActiveEventObservationContext(ActiveEventTrustedTarget Target, IntegrationDefinition? Integration, IntegrationPlatform? Platform);

/// <summary>
/// Consumer activity around the run. When the evidence cannot be tied to the run's events it must say so (aggregate activity) and never
/// claim that a specific event was handled.
/// </summary>
public interface IActiveEventConsumerActivityProvider
{
    bool CanObserve(ActiveEventObservationContext context, out string reason);
    Task<ActiveEventStageEvidence> ObserveAsync(ActiveEventObservationContext context, DateTimeOffset sentFrom, TimeSpan window, CancellationToken ct);
}

/// <summary>Pre-send state captured by a continuity provider. <see cref="State"/> is opaque to the core.</summary>
public sealed record ActiveEventContinuityBaseline(bool Available, string Detail, DateTimeOffset CapturedAt, object? State = null);

/// <summary>
/// Consumer continuity: whether the consumer progressed past the run's events (e.g. checkpoints past the post-send positions). Continuity
/// needs the state before the first send, hence the baseline. Progression is never downstream success.
/// </summary>
public interface IActiveEventContinuityProvider
{
    bool CanObserve(ActiveEventObservationContext context, out string reason);
    Task<ActiveEventContinuityBaseline> CaptureBaselineAsync(ActiveEventObservationContext context, CancellationToken ct);
    Task<ActiveEventStageEvidence> ObserveAfterSendAsync(ActiveEventObservationContext context, ActiveEventContinuityBaseline baseline,
        IReadOnlyList<ActiveEventCorrelation> sent, TimeSpan window, CancellationToken ct);
}

/// <summary>Domain-specific expected-result check, read-only. Registered only when a trustworthy read contract exists for the domain.</summary>
public interface IActiveEventDownstreamVerifier
{
    bool CanVerify(ActiveEventTrustedTarget target, ActiveEventScenarioDescriptor scenario, out string reason);
    Task<ActiveEventDownstreamResult> VerifyAsync(ActiveEventTrustedTarget target, ActiveEventScenarioDescriptor scenario,
        ActiveEventCorrelation correlation, TimeSpan window, CancellationToken ct);
}
