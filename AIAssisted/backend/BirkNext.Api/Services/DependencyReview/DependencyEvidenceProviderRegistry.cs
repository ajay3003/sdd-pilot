using BirkNext.Dependencies;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.DependencyReview;

/// <summary>
/// One member of the dependency evidence provider family. Every provider says which evidence layer it fills (declared, resolved, SBOM, deployed,
/// policy, registry, security, license, internal relationship), which review modes use it and what it cannot prove. Online providers are
/// explicit; source extraction is offline.
/// </summary>
public interface IDependencyEvidenceProvider
{
    DependencyEvidenceProviderDescriptor Descriptor { get; }
}

/// <summary>Source snapshot provider: dependency evidence extracted ONCE when Source Analysis analyzes an archive and stored on the immutable
/// snapshot (declarations, central package management, project references, produced packages, Renovate files). Consumers read it; nothing re-parses.</summary>
public sealed class SourceSnapshotDependencyProvider : IDependencyEvidenceProvider
{
    public DependencyEvidenceProviderDescriptor Descriptor => DependencyEvidenceProviders.Get("source-snapshot");
    public (SourceDependencyEvidence? Evidence, string? Error) Extract(string repository, byte[] bytes, string fileName) => SourceDependencyEvidenceExtractor.Extract(repository, bytes, fileName);
}

/// <summary>SBOM / lock-file provider: validates and normalizes CycloneDX JSON/XML, SPDX JSON and NuGet lock files into an immutable inventory.</summary>
public sealed class SbomDependencyProvider : IDependencyEvidenceProvider
{
    public DependencyEvidenceProviderDescriptor Descriptor => DependencyEvidenceProviders.Get("sbom");
    public (SbomValidation Validation, DependencyInventorySnapshot? Inventory) Import(InventorySources.ImportInput input) => InventorySources.Import(input);
}

/// <summary>Existing inventory provider: stored inventories (identity, origin, capture time, stage, provenance) and those derived from stored source reviews.</summary>
public sealed class ExistingInventoryProvider : IDependencyEvidenceProvider
{
    public DependencyEvidenceProviderDescriptor Descriptor => DependencyEvidenceProviders.Get("existing-inventory");
}

/// <summary>Renovate policy provider: the offline evaluator over a snapshot's redacted Renovate files. Policy only — never execution.</summary>
public sealed class RenovatePolicyProvider : IDependencyEvidenceProvider
{
    public DependencyEvidenceProviderDescriptor Descriptor => DependencyEvidenceProviders.Get("renovate-policy");
}

/// <summary>License metadata provider: SBOM license fields, else registry license metadata — descriptive unless a license policy is configured.</summary>
public sealed class LicenseMetadataProvider : IDependencyEvidenceProvider
{
    public DependencyEvidenceProviderDescriptor Descriptor => DependencyEvidenceProviders.Get("license-metadata");
}

/// <summary>Internal package relationship provider: producer (PackageId + version) → consuming projects across stored snapshots, never merged.</summary>
public sealed class InternalPackageRelationshipProvider : IDependencyEvidenceProvider
{
    public DependencyEvidenceProviderDescriptor Descriptor => DependencyEvidenceProviders.Get("internal-packages");

    public IReadOnlyList<InternalPackageMapping> Mappings(IEnumerable<IqrSourceSnapshot> snapshots) =>
        DependencyEvidenceQueries.InternalPackageMappings([.. snapshots.Where(s => s.DependencyEvidence is not null).OrderBy(s => s.AnalyzedAt)
            .Select(s => new DependencySourceEvidence(s.Id, s.Archive.FileName, s.AnalyzedAt, s.DependencyEvidence!, s.Archive.Sha256))]);
}

/// <summary>A provider's availability in this installation (e.g. registry configured, advisory source registered).</summary>
public sealed record DependencyEvidenceProviderStatus(DependencyEvidenceProviderDescriptor Descriptor, bool Available, string Detail);

/// <summary>The provider family as registered in this installation — what Dependency Review, Technology Coverage and documentation describe.</summary>
public sealed class DependencyEvidenceProviderRegistry(IEnumerable<IPackageRegistryProvider> registries, IAdvisoryProvider advisories, IDependencyAutomationSource automation,
    IDeployedDependencySource deployed)
{
    public IReadOnlyList<DependencyEvidenceProviderStatus> Status()
    {
        var registry = registries.FirstOrDefault();
        return
        [
            new(new SourceSnapshotDependencyProvider().Descriptor, true, "Offline: read from stored Source Analysis snapshots."),
            new(new ExistingInventoryProvider().Descriptor, true, "Offline: stored inventories."),
            new(new SbomDependencyProvider().Descriptor, true, "Offline: validated on import."),
            new(deployed.Descriptor, true, "Contacts only the target the person selects, when they start a capture."),
            new(new RenovatePolicyProvider().Descriptor, true, "Offline evaluator; Renovate is never executed."),
            new(registry?.Descriptor ?? DependencyEvidenceProviders.Get("registry-metadata"), registry is not null,
                registry is null ? "No package registry provider is registered." : $"{registry.Registry}: read-only lookups during a review run; private package patterns are never sent."),
            new(advisories.Descriptor, true, $"{advisories.Name}: read-only lookups during a review run; unavailable lookups are reported as coverage gaps."),
            new(new LicenseMetadataProvider().Descriptor, true, "From SBOM license fields or registry metadata."),
            new(new InternalPackageRelationshipProvider().Descriptor, true, "Offline: produced packages and consumers across stored snapshots."),
            new(automation.Descriptor, true, "Renovate execution evidence (runs/PRs) needs Azure DevOps access; without it execution is Not assessed."),
        ];
    }
}
