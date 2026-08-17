namespace BirkNext.Api.Models;

/// <summary>
/// Step type categorization for workflow.
/// </summary>
public enum WorkflowStepType
{
    ArtifactLoad,      // Loading initial artifacts
    Explorer,          // Artifact explorer (Constitution, Plan, Tasks, DataModel)
    Analysis,          // Analysis step (SpecReview, Traceability, Implementation Review)
    Validation,        // Developer/internal validation step
    Dashboard,         // Dashboard view (informational only)
    Documentation      // Documentation review
}

/// <summary>
/// Static workflow step definition.
/// Defines structure and requirements, not persisted per workspace.
/// </summary>
public class WorkflowStepDefinition
{
    public string StepKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Route { get; set; } = "";
    public int SortOrder { get; set; }
    public WorkflowStepType StepType { get; set; }

    // Artifact requirements
    public List<string> RequiredArtifacts { get; set; } = new();
    public List<string> OptionalArtifacts { get; set; } = new();

    // Dependency on previous steps
    public List<string> RequiredPreviousApprovals { get; set; } = new();

    // Review/Approval requirements
    public bool RequiresManualReview { get; set; } = true;
    public bool RequiresApproval { get; set; } = true;
    public bool IsOptional { get; set; } = false;
    public bool IsDeveloperOnly { get; set; } = false;

    // Display properties
    public string ActionLabel { get; set; } = "";
    public string Color { get; set; } = "#2563eb";  // Default blue
}

/// <summary>
/// Workflow definition registry.
/// Contains all static step definitions.
/// </summary>
public static class WorkflowDefinitions
{
    public static readonly List<WorkflowStepDefinition> AllSteps = new()
    {
        new()
        {
            StepKey = "LoadSampleProject",
            Title = "Load Sample Project",
            Description = "Load a sample project or import artifacts to get started",
            Route = "sample-projects",
            ActionLabel = "Open Sample Projects",
            StepType = WorkflowStepType.ArtifactLoad,
            SortOrder = 1,
            RequiresManualReview = false,
            RequiresApproval = false,
            Color = "#15803d"
        },
        new()
        {
            StepKey = "ConstitutionExplorer",
            Title = "Constitution Explorer",
            Description = "Review governance rules and quality standards",
            Route = "constitution-explorer",
            ActionLabel = "Open Constitution Explorer",
            StepType = WorkflowStepType.Explorer,
            SortOrder = 2,
            RequiredArtifacts = new() { "Constitution" },
            RequiresManualReview = true,
            RequiresApproval = true,
            Color = "#1e40af"
        },
        new()
        {
            StepKey = "SpecificationExplorer",
            Title = "Specification Explorer",
            Description = "Explore and validate specification structure and requirements",
            Route = "specification-explorer",
            ActionLabel = "Open Specification Explorer",
            StepType = WorkflowStepType.Explorer,
            SortOrder = 3,
            RequiredArtifacts = new() { "Specification" },
            RequiresManualReview = true,
            RequiresApproval = true,
            Color = "#0f766e"
        },
        new()
        {
            StepKey = "PlanExplorer",
            Title = "Plan Explorer",
            Description = "Inspect implementation plan and architecture decisions",
            Route = "plan-explorer",
            ActionLabel = "Open Plan Explorer",
            StepType = WorkflowStepType.Explorer,
            SortOrder = 4,
            RequiredArtifacts = new() { "Plan" },
            RequiresManualReview = true,
            RequiresApproval = true,
            Color = "#6d28d9"
        },
        new()
        {
            StepKey = "TaskExplorer",
            Title = "Task Explorer",
            Description = "Review task coverage and delivery risk",
            Route = "task-explorer",
            ActionLabel = "Open Task Explorer",
            StepType = WorkflowStepType.Explorer,
            SortOrder = 5,
            RequiredArtifacts = new() { "Tasks" },
            RequiresManualReview = true,
            RequiresApproval = true,
            Color = "#b45309"
        },
        new()
        {
            StepKey = "DataModelExplorer",
            Title = "Data Model Explorer",
            Description = "Review entities, relationships, and constraints",
            Route = "data-model-explorer",
            ActionLabel = "Open Data Model Explorer",
            StepType = WorkflowStepType.Explorer,
            SortOrder = 6,
            OptionalArtifacts = new() { "DataModel" },
            RequiresManualReview = true,
            RequiresApproval = true,
            IsOptional = true,
            Color = "#065f46"
        },
        new()
        {
            StepKey = "ArtifactTraceability",
            Title = "Artifact Traceability",
            Description = "Analyze end-to-end coverage across artifacts",
            Route = "artifact-traceability",
            ActionLabel = "Run Artifact Traceability",
            StepType = WorkflowStepType.Analysis,
            SortOrder = 7,
            RequiredArtifacts = new() { "Constitution", "Specification", "Plan", "Tasks" },
            RequiresManualReview = true,
            RequiresApproval = true,
            Color = "#2563eb"
        },
        new()
        {
            StepKey = "ImplementationReview",
            Title = "Implementation Review",
            Description = "Validate tasks against spec for alignment gaps",
            Route = "task-alignment",
            ActionLabel = "Run Implementation Review",
            StepType = WorkflowStepType.Analysis,
            SortOrder = 8,
            RequiredArtifacts = new() { "Specification", "Tasks" },
            RequiredPreviousApprovals = new() { "ArtifactTraceability" },
            RequiresManualReview = true,
            RequiresApproval = true,
            Color = "#c2410c"
        },
        new()
        {
            StepKey = "ReviewContextValidation",
            Title = "ReviewContext Validation",
            Description = "Validate ReviewContext for consistency",
            Route = "review-context-validation",
            ActionLabel = "View Validation",
            StepType = WorkflowStepType.Validation,
            SortOrder = 9,
            RequiresManualReview = false,
            RequiresApproval = false,
            IsDeveloperOnly = true,
            Color = "#6366f1"
        },
        new()
        {
            StepKey = "Dashboard",
            Title = "Dashboard",
            Description = "View comprehensive analysis dashboard",
            Route = "dashboard",
            ActionLabel = "View Dashboard",
            StepType = WorkflowStepType.Dashboard,
            SortOrder = 10,
            RequiresManualReview = false,
            RequiresApproval = false,
            IsOptional = true,
            Color = "#8b5cf6"
        }
    };

    public static WorkflowStepDefinition? GetDefinition(string stepKey)
    {
        return AllSteps.FirstOrDefault(s => s.StepKey == stepKey);
    }

    public static List<WorkflowStepDefinition> GetDefinitions(IEnumerable<string> stepKeys)
    {
        return AllSteps.Where(s => stepKeys.Contains(s.StepKey)).ToList();
    }
}
