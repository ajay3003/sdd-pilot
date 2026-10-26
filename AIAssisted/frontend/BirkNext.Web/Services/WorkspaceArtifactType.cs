namespace BirkNext.Web.Services;

public enum WorkspaceArtifactType
{
    Constitution,
    Specification,
    Plan,
    Tasks,
    DataModel,
    Research,
    /// <summary>Schema Studio profiles and saved schema test workspace metadata.</summary>
    SchemaStudio,
    /// <summary>Bounded Schema Studio saved schema package references and local XSD analyses.</summary>
    SchemaStudioPackages,
    /// <summary>Sanitized, bounded Schema Studio validation run history.</summary>
    SchemaStudioTestRuns,
    /// <summary>Bounded, source-bound Schema Studio Designer drafts.</summary>
    SchemaStudioDrafts,
    /// <summary>User-requested, bounded XML examples with schema and snapshot provenance.</summary>
    SchemaStudioGeneratedXml,
    /// <summary>
    /// Authored documentation that supports an Artifact Pack without being one of its roles (quickstart, contract, decision, checklist …):
    /// kept as revisions so reviews can read it with provenance. Never an explorer role and never authoritative by itself.
    /// </summary>
    SupportingDocument
}
