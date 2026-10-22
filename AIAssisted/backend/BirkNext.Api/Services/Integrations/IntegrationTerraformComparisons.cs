using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations;

/// <summary>
/// Target Environment → Integrations' view of Terraform: the environment's CONFIGURED values (seeded or typed) compared with what the latest
/// Source Analysis snapshot with infrastructure evidence DECLARES, through the same comparison Integration Quality Review uses. Read-only —
/// nothing is written and neither side wins: a difference is "needs review", never an automatic overwrite. Declared ≠ deployed.
/// </summary>
public static class IntegrationTerraformComparisons
{
    public static IntegrationTerraformComparison Compare(IntegrationCatalog catalog, IReadOnlyList<IqrSourceSnapshot> snapshots, string? environmentName, bool sourceAnalysisEnabled)
    {
        if (!sourceAnalysisEnabled) return new() { SourceAnalysisEnabled = false };
        // The same snapshot IqrInfrastructureComparison compares against: the newest one that declares infrastructure.
        var terraform = snapshots.FirstOrDefault(s => s.EvidenceDomains?.Infrastructure.Resources.Count > 0);
        if (terraform is null) return new();
        return new()
        {
            TerraformAvailable = true, SnapshotId = terraform.Id, ArchiveName = terraform.Archive.FileName, AnalyzedAt = terraform.AnalyzedAt,
            Values = IqrInfrastructureComparison.Compare(catalog, snapshots, environmentName),
            SupportingValidation = SupportingValidation(terraform.EvidenceDomains!.Infrastructure),
        };
    }

    /// <summary>Checks in the source that inspect messaging, identity or access declarations — context for review, never mapping evidence.</summary>
    internal static List<string> SupportingValidation(BirkNext.SourceDomains.InfrastructureEvidence infrastructure)
    {
        if (!infrastructure.AutomationAnalyzed) return [];
        var types = BirkNext.SourceDomains.InfrastructureEvidenceQueries.Resources(infrastructure, BirkNext.SourceDomains.InfrastructureCategory.Messaging,
            BirkNext.SourceDomains.InfrastructureCategory.Identity, BirkNext.SourceDomains.InfrastructureCategory.AccessControl).Select(r => r.ResourceType).ToHashSet(StringComparer.Ordinal);
        return infrastructure.Checks.Where(c => c.Subjects.Any(types.Contains)).Select(c =>
        {
            var invocations = c.Implementations.SelectMany(id => BirkNext.SourceDomains.InfrastructureEvidenceQueries.Invocations(infrastructure, id)).ToList();
            var where = invocations.Any(i => i.Source == "Pipeline") ? "referenced by a pipeline" : invocations.Count > 0 ? "pre-commit hook" : "not referenced by a pipeline";
            return $"{c.Name} · {c.Scope} · {where} · execution not assessed";
        }).ToList();
    }
}
