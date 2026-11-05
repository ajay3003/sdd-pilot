using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectImport;

namespace BirkNext.Web.Services.ProjectEvidence;

/// <summary>
/// One vocabulary for source roles (Source Management) and evidence categories (Project Evidence). A role says what a source IS for an
/// application (several allowed per source); a category is the evidence it provides there. Capabilities are what its content shows — a role
/// never adds a capability, and an association is only possible where the content supports the role's category.
/// </summary>
public static class SourceRoleVocabulary
{
    /// <summary>The roles a person can choose (Not classified is a state, not a choice), in priority order.</summary>
    public static IReadOnlyList<PackageEvidenceRole> Choices { get; } =
        [.. Enum.GetValues<PackageEvidenceRole>().Where(r => r != PackageEvidenceRole.NotClassified)];

    /// <summary>The Project Evidence category of a role: Primary application source → Application source; Related application/system →
    /// Integration source; Supporting/shared → Supporting/shared source; and the evidence kinds by name.</summary>
    public static ProjectEvidenceCapability CategoryOf(PackageEvidenceRole role) => role switch
    {
        PackageEvidenceRole.PrimaryApplicationSource => ProjectEvidenceCapability.ApplicationSource,
        PackageEvidenceRole.RelatedApplicationSource => ProjectEvidenceCapability.IntegrationSource,
        PackageEvidenceRole.SupportingSource => ProjectEvidenceCapability.SupportingSource,
        PackageEvidenceRole.Documentation => ProjectEvidenceCapability.Documentation,
        PackageEvidenceRole.Infrastructure => ProjectEvidenceCapability.Infrastructure,
        PackageEvidenceRole.GeneratedDocumentation => ProjectEvidenceCapability.GeneratedDocumentation,
        PackageEvidenceRole.Contracts => ProjectEvidenceCapability.ContractSchema,
        _ => ProjectEvidenceCapability.Documentation,
    };

    /// <summary>The roles a source's content supports (the editor offers only these, plus a role a person already confirmed).</summary>
    public static IReadOnlyList<PackageEvidenceRole> Supported(ProjectEvidencePackage package) =>
        [.. Choices.Where(r => ProjectEvidencePackages.Satisfies(package, CategoryOf(r)))];

    public static string Label(PackageEvidenceRole role) => ApplicationContexts.RoleLabel(role);
}
