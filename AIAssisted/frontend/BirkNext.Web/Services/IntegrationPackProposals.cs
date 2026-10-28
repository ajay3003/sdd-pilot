using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// A Test Pack integration template that imported evidence supports: the pack's integration requirement, its evidence source recognized in an
/// imported snapshot, and no configured integration fulfilling it yet. <see cref="Ready"/> when nothing needs a person's choice; otherwise
/// <see cref="Decision"/> names exactly what does. Adding goes through the normal Add integration form with the template preselected.
/// </summary>
public sealed record IntegrationPackProposal(IntegrationTemplateView Template, bool Ready, string? Decision, IReadOnlyList<string> Evidence);

/// <summary>
/// Test Pack proposals for the Integration Catalog. Deterministic and read-only: a template is proposed only when its evidence source role is
/// recognized in an imported snapshot (by content, never by archive name) and it is not already configured. A template whose evidence source is
/// missing is not proposed — the testing setup names that missing source instead. Nothing is created until the person adds it.
/// </summary>
public static class IntegrationPackProposals
{
    public static IReadOnlyList<IntegrationPackProposal> For(IReadOnlyList<IntegrationTemplateView> templates) =>
        [.. templates.Where(t => t.AlreadyConfigured.Count == 0 && t.EvidenceSource is { Candidates.Count: > 0 }).Select(Proposal)];

    private static IntegrationPackProposal Proposal(IntegrationTemplateView template)
    {
        var source = template.EvidenceSource!;
        var archives = source.Candidates.Select(c => c.ArchiveFileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ambiguous = source.Candidates.Select(c => c.Fingerprint).Distinct(StringComparer.Ordinal).Count() > 1 && !source.Candidates.Any(c => c.Bound);
        var decision = ambiguous
            ? $"Choose which {source.Role.Label} source is the evidence ({string.Join(", ", archives)})."
            : template.NeedsConfiguration.Count > 0
                ? $"Needs a value for this environment: {string.Join(", ", template.NeedsConfiguration.Select(IntegrationTemplatePresentation.FieldLabel))}."
                : null;
        List<string> evidence =
        [
            $"{template.PackName} Test Pack — integration requirement" + (template.Journeys.Count > 0 ? $" ({string.Join(", ", template.Journeys)})" : ""),
            $"{source.Role.Label} source: {string.Join(", ", archives)}",
        ];
        if (template.ReferenceSource is { State: TestPackSourceState.ReferenceAvailable } reference) evidence.Add($"{reference.Role.Label}: built-in Test Pack reference");
        return new(template, decision is null, decision, evidence);
    }
}
