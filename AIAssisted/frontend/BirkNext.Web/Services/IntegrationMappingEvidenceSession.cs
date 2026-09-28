using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Mapping evidence checks run in this browser session, per Target Environment. Evidence only: nothing here is persisted, and nothing here
/// changes a mapping's provenance — a Suggested mapping with Strong evidence is still Suggested until a person confirms it.
/// Shared by Target Environment → Integrations (which runs the checks) and the Integration Quality Review pre-run (which summarizes them).
/// </summary>
public sealed class IntegrationMappingEvidenceSession
{
    private readonly Dictionary<string, Dictionary<string, IntegrationMappingEvidenceCheck>> _byEnvironment = new(StringComparer.Ordinal);

    public void Record(string environmentId, IntegrationMappingEvidenceCheck check)
    {
        if (!_byEnvironment.TryGetValue(environmentId, out var checks)) _byEnvironment[environmentId] = checks = new(StringComparer.Ordinal);
        checks[check.IntegrationId] = check;
    }

    public IReadOnlyDictionary<string, IntegrationMappingEvidenceCheck> For(string environmentId) =>
        _byEnvironment.TryGetValue(environmentId, out var checks) ? checks : new Dictionary<string, IntegrationMappingEvidenceCheck>();
}
