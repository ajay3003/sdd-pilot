namespace BirkNext.Web.Services.Explorers;

/// <summary>Role wording shared by the document explorers. File names here are examples for help text, never lookups.</summary>
public static class ArtifactExplorerRoles
{
    public static string Label(WorkspaceArtifactType role) => role switch
    {
        WorkspaceArtifactType.Tasks => "Task",
        WorkspaceArtifactType.DataModel => "Data Model",
        _ => role.ToString(),
    };

    public static string Icon(WorkspaceArtifactType role) => role switch
    {
        WorkspaceArtifactType.Constitution => "⚖️",
        WorkspaceArtifactType.Specification => "📋",
        WorkspaceArtifactType.Plan => "🗺️",
        WorkspaceArtifactType.Tasks => "✅",
        WorkspaceArtifactType.DataModel => "🗄️",
        _ => "📄",
    };

    /// <summary>What the explorer reviews, for the page lead.</summary>
    public static string Purpose(WorkspaceArtifactType role) => role switch
    {
        WorkspaceArtifactType.Constitution => "Review principles, standards, constraints and governance rules from Constitution artifacts.",
        WorkspaceArtifactType.Specification => "Review requirements, scenarios, acceptance criteria and validation from Specification artifacts.",
        WorkspaceArtifactType.Plan => "Review architecture decisions, risks and constraints, complexity and constitution compliance from Plan artifacts.",
        WorkspaceArtifactType.Tasks => "Review phases, user stories, task groups and task traceability from Task artifacts.",
        WorkspaceArtifactType.DataModel => "Review entities, tables, relationships, constraints, indexes and traceability from Data Model artifacts.",
        _ => $"Review {Label(role)} artifacts.",
    };

    /// <summary>A common file name for the role (help text only).</summary>
    public static string CommonFileName(WorkspaceArtifactType role) => role switch
    {
        WorkspaceArtifactType.Constitution => "constitution.md",
        WorkspaceArtifactType.Specification => "spec.md",
        WorkspaceArtifactType.Plan => "plan.md",
        WorkspaceArtifactType.Tasks => "tasks.md",
        WorkspaceArtifactType.DataModel => "data-model.md",
        WorkspaceArtifactType.Research => "research.md",
        _ => "document.md",
    };

    /// <summary>A non-canonical file name for the role, used as the import placeholder.</summary>
    public static string ExampleCustomFileName(WorkspaceArtifactType role) => role switch
    {
        WorkspaceArtifactType.Constitution => "governance-rules.md",
        WorkspaceArtifactType.Specification => "requirements.md",
        WorkspaceArtifactType.Plan => "implementation-roadmap.md",
        WorkspaceArtifactType.Tasks => "delivery-backlog.md",
        WorkspaceArtifactType.DataModel => "domain-schema.md",
        _ => "notes.md",
    };
}
