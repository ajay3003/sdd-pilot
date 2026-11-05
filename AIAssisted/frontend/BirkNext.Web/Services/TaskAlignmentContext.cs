using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// The review context Task Alignment reads: the selected Specification and Tasks, plus — when selected — the Constitution and Data Model, so a task
/// naming a Constitution rule id (e.g. "PS-08") or a Data Model structure is a design artifact link. Without them the design-link rule has nothing
/// to match and every such task fell through to "Needs review" (0 design artifact links).
/// </summary>
public static class TaskAlignmentContext
{
    public static ReviewContext Build(IArtifactParserService parser, string? constitutionText, string specText, string tasksText,
        string? dataModelText, IDataModelAnalysisService? dataModels)
    {
        var parsed = parser.Parse(string.IsNullOrWhiteSpace(constitutionText) ? null : constitutionText, specText, null, tasksText);
        return ReviewContextFactory.Create(
            parsed.Constitution is not null ? ConstitutionAnalysisService.BuildSemanticModel(parsed.Constitution) : new ConstitutionSemanticModel(),
            parsed.Spec is not null ? SpecExplorerService.BuildSemanticModel(parsed.Spec, specText) : new SpecificationSemanticModel(),
            parsed.Plan is not null ? PlanAnalysisService.BuildSemanticModel(parsed.Plan) : new PlanSemanticModel(),
            parsed.Tasks is not null ? TaskExplorerService.BuildSemanticModel(parsed.Tasks) : new TaskSemanticModel(),
            string.IsNullOrWhiteSpace(dataModelText) || dataModels is null ? new DataModelSemanticModel() : DataModelAnalysisService.BuildSemanticModel(dataModels.Parse(dataModelText)));
    }
}
