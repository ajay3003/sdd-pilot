using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;

namespace BirkNext.Web.Services.ProjectEvidence;

/// <summary>How one capability of a package stands. Only capabilities the package actually contains get a status; absent ones are not a defect.</summary>
public enum PackageCapabilityStatus { Ready, Partial, Failed }

/// <summary>One capability a package provides, with its own extraction status and a short fact ("14 supported documents", "Terraform").</summary>
public sealed record PackageCapability(ProjectEvidenceCapability Capability, PackageCapabilityStatus Status, string Fact);

/// <summary>
/// One imported package as project evidence: what it provides (capabilities, each judged on its own), how it is classified (main application
/// candidate or supporting evidence, and why), and the canonical identities that reference it. Built only from existing deterministic evidence:
/// the import record, the artifact revisions of its import scope, its generated-documentation set and its stored Source Analysis snapshot.
/// The archive name is never used to classify.
/// </summary>
public sealed record ProjectEvidencePackage(
    string ImportId,
    string DisplayName,
    string ArchiveFileName,
    string ArchiveSha256,
    DateTimeOffset ImportedAt,
    Guid? SnapshotId,
    IReadOnlyList<PackageCapability> Capabilities,
    IReadOnlyDictionary<WorkspaceArtifactType, int> DocumentRoles,
    int DocumentCount,
    IReadOnlyList<string> LogicalSources,
    IReadOnlyList<string> InfrastructureFormats,
    IReadOnlyList<string> ConfigurationEnvironments,
    string? SourceAnalysisStatus)
{
    public bool Has(ProjectEvidenceCapability capability) => Capabilities.Any(c => c.Capability == capability);
    public PackageCapability? Capability(ProjectEvidenceCapability capability) => Capabilities.FirstOrDefault(c => c.Capability == capability);

    /// <summary>A main application/system candidate: it holds application source with a web/service host project (or, from an older backend
    /// that does not report project kinds, application source at all).</summary>
    public bool IsApplicationCandidate => Has(ProjectEvidenceCapability.ApplicationSource);

    /// <summary>Capability-based status: Ready when every capability the package contains was extracted; Partial only when extraction of a
    /// capability it contains is incomplete. Never Partial for lacking a capability it does not claim (no code in a docs package, or no docs in
    /// a source package).</summary>
    public PackageCapabilityStatus Status => Capabilities.Count == 0 ? PackageCapabilityStatus.Failed
        : Capabilities.Any(c => c.Status != PackageCapabilityStatus.Ready) ? PackageCapabilityStatus.Partial : PackageCapabilityStatus.Ready;

    /// <summary>"Application source", "Documentation source", "Source + documents", "Infrastructure source", …: the package's primary classification.</summary>
    public string Classification => ProjectEvidencePackages.ClassificationOf(this);

    /// <summary>Why the package is (or is not) a main project candidate, in one sentence built from evidence.</summary>
    public string ClassificationReason => ProjectEvidencePackages.ReasonOf(this);

    /// <summary>What the package does not contain, stated neutrally ("No application source", "No authored project documents").</summary>
    public IReadOnlyList<string> NotProvided => ProjectEvidencePackages.NotProvidedOf(this);

    public string DocumentRoleSummary => string.Join(" · ", DocumentRoles.Where(r => r.Value > 0).Select(r => ArtifactExplorerRoles.Label(r.Key)));
}

public static class ProjectEvidencePackages
{
    /// <summary>The authored roles a documentation package can contribute (supporting documents are context, not a role).</summary>
    public static readonly IReadOnlyList<WorkspaceArtifactType> DocumentRoleOrder =
    [
        WorkspaceArtifactType.Constitution, WorkspaceArtifactType.Specification, WorkspaceArtifactType.Plan,
        WorkspaceArtifactType.Tasks, WorkspaceArtifactType.DataModel, WorkspaceArtifactType.Research,
    ];

    /// <summary>Every recorded Project Import of the workspace as project evidence, newest first (order is display only, never a choice).</summary>
    public static IReadOnlyList<ProjectEvidencePackage> All(SddLifecycleState lifecycle, SourceSetupView? setup) =>
        lifecycle.ProjectImports.Select(record => Of(record, lifecycle, setup)).OrderByDescending(p => p.ImportedAt).ThenBy(p => p.ImportId, StringComparer.Ordinal).ToList();

    public static ProjectEvidencePackage Of(SddProjectImportRecord record, SddLifecycleState lifecycle, SourceSetupView? setup)
    {
        var scope = ProjectImportScope.For(record.ImportId);
        var documents = lifecycle.Revisions.Where(r => r.WorkspaceScope == scope && !string.IsNullOrWhiteSpace(r.Content)).ToList();
        var roles = DocumentRoleOrder.ToDictionary(role => role,
            role => documents.Where(r => r.Role == role.ToString()).Select(r => r.FileName).Distinct(StringComparer.Ordinal).Count());
        var documentCount = documents.Where(r => DocumentRoleOrder.Any(role => r.Role == role.ToString())).Select(r => (r.Role, r.FileName)).Distinct().Count();
        var mine = setup is null ? [] : ProjectImportHistory.SnapshotsOf(record, setup.Snapshots);
        var snapshot = mine.FirstOrDefault(s => s.SnapshotId == record.SourceSnapshotId) ?? mine.FirstOrDefault();
        var snapshotId = record.SourceSnapshotId ?? snapshot?.SnapshotId;
        var capabilities = new List<PackageCapability>();

        if (documentCount > 0)
            // Several candidates of a role is a selection decision, not incomplete extraction: the documents are Ready.
            capabilities.Add(new(ProjectEvidenceCapability.Documentation, PackageCapabilityStatus.Ready,
                $"{documentCount} supported document{(documentCount == 1 ? "" : "s")}"));

        var areas = snapshot?.Areas ?? [];
        string? Area(string name) => areas.FirstOrDefault(a => string.Equals(a.Area, name, StringComparison.Ordinal))?.Status;
        static PackageCapabilityStatus StatusOf(string? status) => status switch
        {
            "Complete" or "Ready" or "Detected" => PackageCapabilityStatus.Ready,
            "Failed" => PackageCapabilityStatus.Failed,
            _ => PackageCapabilityStatus.Partial,
        };
        static bool Present(string? status) => status is not null and not ("NotDetected" or "NotAnalyzed" or "Unsupported" or "NotApplicable");

        if (snapshotId is not null)
        {
            var code = Area("Application code");
            // Older backends report no areas: the recorded import state is the only evidence, and a snapshot means application source.
            var hasCode = snapshot is null ? record.SourceState is "Created" or "Reused" : areas.Count == 0 ? snapshot.Status is not "Failed" : Present(code);
            if (hasCode)
            {
                var library = snapshot is { ServiceProjectCount: 0, SharedProjectCount: > 0 };
                capabilities.Add(new(library ? ProjectEvidenceCapability.SupportingSource : ProjectEvidenceCapability.ApplicationSource,
                    areas.Count == 0 ? PackageCapabilityStatus.Ready : StatusOf(code),
                    library ? $"{snapshot!.SharedProjectCount} production project{(snapshot.SharedProjectCount == 1 ? "" : "s")} · no web/service host project"
                    : snapshot is { ServiceProjectCount: > 0 } ? $"{snapshot.ServiceProjectCount} application/service project{(snapshot.ServiceProjectCount == 1 ? "" : "s")}"
                    : "Source Analysis snapshot"));
            }
            if (Present(Area("Contracts")))
                capabilities.Add(new(ProjectEvidenceCapability.ContractSchema, StatusOf(Area("Contracts")), "Contracts found by Source Analysis"));
            if (Present(Area("Infrastructure as Code")) || snapshot?.InfrastructureFormats.Count > 0)
                capabilities.Add(new(ProjectEvidenceCapability.Infrastructure, StatusOf(Area("Infrastructure as Code") ?? "Complete"),
                    snapshot?.InfrastructureFormats.Count > 0 ? string.Join(", ", snapshot.InfrastructureFormats) : "Infrastructure as Code"));
        }

        if (lifecycle.GeneratedDocumentationSets.FirstOrDefault(s => s.ProjectImportId == record.ImportId) is { } generated && generated.Artifacts.Count > 0)
            capabilities.Add(new(ProjectEvidenceCapability.GeneratedDocumentation, PackageCapabilityStatus.Ready,
                $"{generated.Artifacts.Count} generated document{(generated.Artifacts.Count == 1 ? "" : "s")}"));

        var logical = ProjectImportHistory.LogicalSources(setup, snapshotId);
        return new ProjectEvidencePackage(record.ImportId, string.IsNullOrWhiteSpace(record.ProjectName) ? record.ArchiveFileName : record.ProjectName,
            record.ArchiveFileName, record.ArchiveSha256, record.ImportedAt, snapshotId, capabilities, roles, documentCount, logical,
            snapshot?.InfrastructureFormats ?? [], snapshot?.ConfigurationEnvironments ?? [], snapshot?.Status);
    }

    internal static string ClassificationOf(ProjectEvidencePackage p)
    {
        var app = p.Has(ProjectEvidenceCapability.ApplicationSource);
        var docs = p.Has(ProjectEvidenceCapability.Documentation);
        if (app && docs) return "Source + documents";
        if (app) return "Application source";
        if (p.Has(ProjectEvidenceCapability.SupportingSource)) return docs ? "Supporting source + documents" : "Supporting/shared source";
        if (docs) return "Documentation source";
        if (p.Has(ProjectEvidenceCapability.Infrastructure)) return "Infrastructure source";
        if (p.Has(ProjectEvidenceCapability.ContractSchema)) return "Contract/schema source";
        if (p.Has(ProjectEvidenceCapability.GeneratedDocumentation)) return "Generated documentation";
        return "Unknown";
    }

    internal static string ReasonOf(ProjectEvidencePackage p)
    {
        if (p.Capability(ProjectEvidenceCapability.ApplicationSource) is { } app)
            return $"Application candidate: Source Analysis found application source ({app.Fact}).";
        if (p.Capability(ProjectEvidenceCapability.SupportingSource) is { } shared)
            return $"Supporting evidence: production code without a web/service host project ({shared.Fact}).";
        if (p.Has(ProjectEvidenceCapability.Documentation))
            return $"Supporting evidence: authored project documents ({p.DocumentRoleSummary}), no application source.";
        if (p.Has(ProjectEvidenceCapability.Infrastructure))
            return "Supporting evidence: infrastructure as code only, no application source.";
        if (p.Has(ProjectEvidenceCapability.ContractSchema))
            return "Supporting evidence: contracts/schemas only, no application source.";
        return "No supported documents or source evidence were extracted from this package.";
    }

    internal static IReadOnlyList<string> NotProvidedOf(ProjectEvidencePackage p)
    {
        var lacking = new List<string>();
        if (!p.Has(ProjectEvidenceCapability.ApplicationSource) && !p.Has(ProjectEvidenceCapability.SupportingSource)) lacking.Add("No application source");
        if (!p.Has(ProjectEvidenceCapability.Documentation)) lacking.Add("No authored project documents");
        return lacking;
    }

    /// <summary>Whether a package can satisfy a role of this capability: only by actually providing it (an infrastructure-only package never
    /// satisfies an application or documentation role). Application source also accepts supporting/shared source only for the supporting role.</summary>
    public static bool Satisfies(ProjectEvidencePackage package, ProjectEvidenceCapability capability) => capability switch
    {
        ProjectEvidenceCapability.ApplicationSource => package.Has(ProjectEvidenceCapability.ApplicationSource),
        ProjectEvidenceCapability.SupportingSource => package.Has(ProjectEvidenceCapability.SupportingSource) || package.Has(ProjectEvidenceCapability.ApplicationSource),
        ProjectEvidenceCapability.IntegrationSource => package.Has(ProjectEvidenceCapability.ApplicationSource) || package.Has(ProjectEvidenceCapability.SupportingSource),
        _ => package.Has(capability),
    };

    /// <summary>Why a package matches a role: the evidence facts of that capability.</summary>
    public static string WhyMatches(ProjectEvidencePackage package, ProjectEvidenceCapability capability) => capability switch
    {
        ProjectEvidenceCapability.Documentation => $"{package.Capability(capability)?.Fact} · {package.DocumentRoleSummary}",
        ProjectEvidenceCapability.SupportingSource or ProjectEvidenceCapability.IntegrationSource =>
            (package.Capability(ProjectEvidenceCapability.SupportingSource) ?? package.Capability(ProjectEvidenceCapability.ApplicationSource))?.Fact ?? "",
        _ => package.Capability(capability)?.Fact ?? "",
    };

    public static string CapabilityLabel(ProjectEvidenceCapability capability) => capability switch
    {
        ProjectEvidenceCapability.ApplicationSource => "Application source",
        ProjectEvidenceCapability.Documentation => "Documentation",
        ProjectEvidenceCapability.ContractSchema => "Contracts/schema",
        ProjectEvidenceCapability.Infrastructure => "Infrastructure",
        ProjectEvidenceCapability.SupportingSource => "Supporting/shared source",
        ProjectEvidenceCapability.GeneratedDocumentation => "Generated docs",
        ProjectEvidenceCapability.IntegrationSource => "Integration source",
        _ => capability.ToString(),
    };
}
