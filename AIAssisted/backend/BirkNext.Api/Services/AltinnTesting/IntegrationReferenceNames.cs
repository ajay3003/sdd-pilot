using BirkNext.Integrations;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// Human-readable names for integration references: a provider setting that points at a Target Environment integration shows that
/// integration's display name, and the raw id stays in <see cref="AltinnSettingField.Value"/> for advanced details. An id that matches
/// nothing keeps no display name (it is shown as it is, never guessed).
/// </summary>
public static class IntegrationReferenceNames
{
    public static string? Name(string? id, IReadOnlyList<IntegrationDefinition> integrations) =>
        id is null ? null : integrations.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))?.DisplayName is { Length: > 0 } name ? name : null;

    public static AltinnSettingField Apply(AltinnSettingField field, IReadOnlyList<IntegrationDefinition> integrations) => field with
    {
        DisplayValue = Name(field.Value, integrations),
        Candidates = [.. field.Candidates.Select(candidate => candidate with { DisplayValue = Name(candidate.Value, integrations) })],
        Proposal = field.Proposal is { } proposal ? proposal with { DisplayValue = Name(proposal.Value, integrations) } : null,
    };
}
