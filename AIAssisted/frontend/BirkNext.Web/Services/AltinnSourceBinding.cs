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

    public sealed record Resolved(string? IntegrationId, string? IntegrationName, IntegrationEvidenceBinding? Binding)
    {
        /// <summary>The pack whose journey requires the report intake (its source guidance names this provider's fields).</summary>
        public string? PackId { get; init; }
    }

    public static async Task<Resolved> ResolveAsync(IIntegrationCatalogApiService api, IIntegrationJourneysApiService? journeys, string environmentId, CancellationToken ct = default)
    {
        if (journeys is null || string.IsNullOrWhiteSpace(environmentId)) return new(null, null, null);
        var entries = ((await journeys.PacksAsync(environmentId, ct)).Value ?? [])
            .SelectMany(pack => pack.Journeys.SelectMany(view => view.MatchedIntegrations).Select(item => (Pack: pack.PackId, Item: item)))
            .Where(entry => entry.Item.RequirementKey == ReportIntakeRequirement).ToList();
        var match = entries.FirstOrDefault(entry => entry.Item.IntegrationId is not null);
        // The pack that requires the report intake is known even before its integration is configured (its documented flow can be shown).
        if (match.Item?.IntegrationId is not { } integrationId)
            return new(null, null, null) { PackId = entries.Select(entry => entry.Pack).FirstOrDefault(pack => !string.IsNullOrEmpty(pack)) };
        var binding = ((await api.ListEvidenceBindingsAsync(environmentId, ct)) ?? []).FirstOrDefault(item => item.IntegrationId == integrationId);
        return new(integrationId, match.Item.DisplayName, binding) { PackId = string.IsNullOrEmpty(match.Pack) ? null : match.Pack };
    }
}
