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
        };
    }
}
