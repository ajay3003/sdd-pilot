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
    IReadOnlyList<IqrSourceSnapshot> SourceSnapshots);

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
    /// <summary>Whether a configured IQR integration fulfils one of this pack's integration requirements.</summary>
    bool Matches(JourneyIntegrationRequirement requirement, IntegrationDefinition integration);
    Task<IReadOnlyList<JourneyPrerequisite>> PrerequisitesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context, CancellationToken ct);
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
    IReadOnlyList<JourneyStepResult> Previous);

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
