using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Altinn provider: the canonical source-evidence binding its configuration reads — the Integration Quality Review binding of the configured
/// integration the journey pack matches to its report-intake requirement. Provider-specific (it names that requirement); generic review code
/// never calls it. A provider never selects a snapshot of its own.
/// </summary>
public static class AltinnSourceBinding
{
    public const string ReportIntakeRequirement = "report-intake";

    public sealed record Resolved(string? IntegrationId, string? IntegrationName, IntegrationEvidenceBinding? Binding);

    public static async Task<Resolved> ResolveAsync(IIntegrationCatalogApiService api, IIntegrationJourneysApiService? journeys, string environmentId, CancellationToken ct = default)
    {
        if (journeys is null || string.IsNullOrWhiteSpace(environmentId)) return new(null, null, null);
        var matched = ((await journeys.PacksAsync(environmentId, ct)).Value ?? []).SelectMany(pack => pack.Journeys).SelectMany(view => view.MatchedIntegrations)
            .FirstOrDefault(item => item.RequirementKey == ReportIntakeRequirement && item.IntegrationId is not null);
        if (matched?.IntegrationId is not { } integrationId) return new(null, null, null);
        var binding = ((await api.ListEvidenceBindingsAsync(environmentId, ct)) ?? []).FirstOrDefault(item => item.IntegrationId == integrationId);
        return new(integrationId, matched.DisplayName, binding);
    }
}
