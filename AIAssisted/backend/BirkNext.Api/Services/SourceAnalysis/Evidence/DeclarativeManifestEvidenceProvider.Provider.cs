using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

internal sealed partial class DeclarativeManifestEvidenceProvider
{
    public string Key => "declarative-manifests";
    public string Name => "Declarative manifest evidence provider";
    public int Version => 3;

    public IReadOnlyList<DomainCapability> Capabilities { get; } =
    [
        new(SourceEvidenceDomain.Infrastructure, "Bicep", DomainSupport.Partial, "Resource, parameter, module and output declarations by pattern; expressions and loops are not evaluated."),
        new(SourceEvidenceDomain.Infrastructure, "ARM template", DomainSupport.Partial, "Top-level resources and parameters; template expressions and nested deployments are not evaluated."),
        new(SourceEvidenceDomain.Infrastructure, "Kubernetes manifest", DomainSupport.Partial, "Kinds, names, namespaces and selected security/network settings via a YAML subset reader; Kustomize overlays are not applied."),
        new(SourceEvidenceDomain.Infrastructure, "Helm chart", DomainSupport.Partial, "Chart metadata and values files; templates are not rendered."),
        new(SourceEvidenceDomain.Infrastructure, "Pulumi / AWS CDK / CloudFormation", DomainSupport.Unsupported, "Detected only; not analyzed."),
    ];

    public bool Claims(EvidenceFile file) => file.Role == SourceFileRole.InfrastructureAsCode && file.Technology is "Bicep" or "ARM template" or "Kubernetes manifest" or "Helm chart" or "Helm template" or "Pulumi";

    public InfrastructureProviderRun Contribute(SourceEvidenceContext context, IReadOnlyList<EvidenceFile> files, InfrastructureEvidenceBuilder b, CancellationToken ct)
    {
        Bicep(b, files.Where(f => f.Technology == "Bicep").ToList(), ct);
        Arm(b, files.Where(f => f.Technology == "ARM template").ToList(), ct);
        Kubernetes(b, files.Where(f => f.Technology == "Kubernetes manifest").ToList(), ct);
        Helm(b, files.Where(f => f.Technology is "Helm chart" or "Helm template").ToList());
        var unsupported = files.Where(f => f.Technology == "Pulumi").ToList();
        if (unsupported.Count > 0)
        {
            b.Technologies.Add("Pulumi (unsupported)");
            b.Diagnostics.Add(new("Unsupported format", $"{unsupported.Count} Pulumi file(s) detected; Pulumi programs are not analyzed.", SourceEvidenceRedaction.Safe(unsupported[0].Path)));
        }
        var analyzed = files.Count - unsupported.Count;
        return new(Key, Name, Version, files.Count == 0 ? SourceDomainStatus.NotDetected : analyzed == 0 ? SourceDomainStatus.Unsupported : SourceDomainStatus.Partial, files.Count,
            files.Count == 0 ? "No Bicep, ARM, Kubernetes or Helm files in this source." : string.Join(", ", files.GroupBy(f => f.Technology).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}")) + " (pattern-based).");
    }
}
