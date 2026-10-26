using BirkNext.Web.Services.Explorers;

namespace BirkNext.Web.Services;

/// <summary>
/// What each Review Pack reads from an Artifact Pack's inventory. Declared once, used for the run (which documents a pack consumes) and for
/// provenance ("What did this review read?"). Generated documentation is read only by a pack that asks for it, and then as context only.
/// </summary>
public static class QualityReviewInputContracts
{
    private const string QaAuditor = "qa-auditor";
    private const string Constitution = "constitution-compliance";
    private const string QaReadiness = "qa-readiness";
    private const string Delivery = "delivery-readiness";
    private const string DataModel = "data-model-quality";

    /// <summary>The input contract in tester words.</summary>
    public static string Describe(string packId) => packId switch
    {
        QaAuditor => "Primary documents; the Plan is read with its supporting plan documents (research, contracts, decisions) for decisions and requirement references.",
        Constitution => "The Constitution against every authored document of the Artifact Pack — primary and supporting; each reference keeps the document it was found in.",
        QaReadiness => "Primary documents with the Plan's supporting documents (testing and architecture statements across the Plan document set).",
        Delivery => "Primary documents only. Documentation readiness: advisory unless a project policy makes it a release gate.",
        DataModel => "The authored Data Model. Generated data-model documentation is read as context only and never scored.",
        _ => "Every authored document of the Artifact Pack — primary, supporting and applicable shared documentation (documentation coverage only).",
    };

    /// <summary>Whether a Review Pack reads <paramref name="document"/> under its contract.</summary>
    public static bool Reads(string packId, PackDocument document)
    {
        if (document.Class == PackDocumentClass.Generated)
            return packId == DataModel && document.Family == WorkspaceArtifactType.DataModel;
        if (!document.IsAuthored || string.IsNullOrEmpty(document.Text) && !document.IsPrimary) return false;
        if (document.IsPrimary)
            return packId switch
            {
                DataModel => document.Role == WorkspaceArtifactType.DataModel,
                Constitution or QaAuditor or QaReadiness or Delivery => document.Role != WorkspaceArtifactType.DataModel,
                _ => true,
            };
        return packId switch
        {
            QaAuditor or QaReadiness => document.Family == WorkspaceArtifactType.Plan,
            Delivery or DataModel => false,
            _ => true, // Constitution Compliance and every documentation-coverage (standards) pack read all supporting authored documents
        };
    }

    /// <summary>"Primary artifacts", "Supporting authored documentation", "Shared documentation", "Generated documentation".</summary>
    public static string Category(PackDocument document) => document.Class switch
    {
        PackDocumentClass.PrimaryPackLocal => "Primary artifacts",
        PackDocumentClass.SharedPrimary => "Primary artifacts",
        PackDocumentClass.SupportingPackLocal => "Supporting authored documentation",
        PackDocumentClass.SupportingShared => "Shared documentation",
        PackDocumentClass.Generated => "Generated documentation (derived)",
        _ => "Other",
    };
}
