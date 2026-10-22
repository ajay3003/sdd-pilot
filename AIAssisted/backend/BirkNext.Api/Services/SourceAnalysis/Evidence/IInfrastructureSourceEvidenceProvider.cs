using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// One sibling provider of the Infrastructure Source Evidence family (Terraform, declarative manifests, infrastructure scripts; Bicep/Helm/
/// Kubernetes-specific providers can join later). A provider claims the files it understands, reads them statically ONCE per immutable
/// snapshot and writes normalized facts with provenance into the shared <see cref="InfrastructureEvidenceBuilder"/>. Providers never execute
/// anything, never contact a backend, registry or cloud, and never read files another provider claimed.
/// </summary>
internal interface IInfrastructureSourceEvidenceProvider
{
    string Key { get; }
    string Name { get; }
    int Version { get; }
    IReadOnlyList<DomainCapability> Capabilities { get; }
    bool Claims(EvidenceFile file);
    InfrastructureProviderRun Contribute(SourceEvidenceContext context, IReadOnlyList<EvidenceFile> files, InfrastructureEvidenceBuilder builder, CancellationToken ct);
}
