namespace BirkNext.Api.Services.Review;



/// <summary>
/// Orchestrates building ReviewPageModels for all Review pages.
/// Handles error cases and missing prerequisites consistently.
/// </summary>
public class ReviewPageModelService(
    IDashboardPageModelBuilder dashboardBuilder,
    IConstitutionExplorerPageModelBuilder constitutionBuilder,
    IDataModelExplorerPageModelBuilder dataModelBuilder,
    IPlanExplorerPageModelBuilder planBuilder,
    ITaskExplorerPageModelBuilder taskBuilder,
    IWorkspaceArtifactStatusService artifactStatus)
{
    public async Task<ReviewPageModel> GetDashboardModelAsync()
    {
        try
        {
            return await dashboardBuilder.BuildAsync();
        }
        catch (Exception ex)
        {
            return CreateFailedModel(
                "Dashboard",
                "Executive overview of workspace and analyses",
                $"Failed to load dashboard: {ex.Message}");
        }
    }

    public async Task<ReviewPageModel> GetConstitutionExplorerModelAsync()
    {
        try
        {
            var artifact = await artifactStatus.GetArtifactAsync(WorkspaceArtifactKind.Constitution);

            if (artifact == null)
            {
                return CreateBlockedOrEmptyModel(
                    "Constitution Explorer",
                    "Navigate and analyze constitution.md files",
                    new[] { "Constitution" },
                    new[] { "Constitution" });
            }

            return await constitutionBuilder.BuildAsync();
        }
        catch (Exception ex)
        {
            return CreateFailedModel(
                "Constitution Explorer",
                "Navigate and analyze constitution.md files",
                $"Failed to load constitution: {ex.Message}");
        }
    }

    public async Task<ReviewPageModel> GetDataModelExplorerModelAsync()
    {
        try
        {
            var artifact = await artifactStatus.GetArtifactAsync(WorkspaceArtifactKind.DataModel);

            if (artifact == null)
            {
                return CreateBlockedOrEmptyModel(
                    "Data Model Explorer",
                    "Navigate and analyze data-model.md files",
                    new[] { "DataModel" },
                    new[] { "DataModel" });
            }

            return await dataModelBuilder.BuildAsync();
        }
        catch (Exception ex)
        {
            return CreateFailedModel(
                "Data Model Explorer",
                "Navigate and analyze data-model.md files",
                $"Failed to load data model: {ex.Message}");
        }
    }

    public async Task<ReviewPageModel> GetPlanExplorerModelAsync()
    {
        try
        {
            var artifact = await artifactStatus.GetArtifactAsync(WorkspaceArtifactKind.Plan);

            if (artifact == null)
            {
                return CreateBlockedOrEmptyModel(
                    "Plan Explorer",
                    "Navigate and analyze plan.md files",
                    new[] { "Plan" },
                    new[] { "Plan" });
            }

            return await planBuilder.BuildAsync();
        }
        catch (Exception ex)
        {
            return CreateFailedModel(
                "Plan Explorer",
                "Navigate and analyze plan.md files",
                $"Failed to load plan: {ex.Message}");
        }
    }

    public async Task<ReviewPageModel> GetTaskExplorerModelAsync()
    {
        try
        {
            var artifact = await artifactStatus.GetArtifactAsync(WorkspaceArtifactKind.Tasks);

            if (artifact == null)
            {
                return CreateBlockedOrEmptyModel(
                    "Task Explorer",
                    "Navigate and analyze tasks.md files",
                    new[] { "Tasks" },
                    new[] { "Tasks" });
            }

            return await taskBuilder.BuildAsync();
        }
        catch (Exception ex)
        {
            return CreateFailedModel(
                "Task Explorer",
                "Navigate and analyze tasks.md files",
                $"Failed to load tasks: {ex.Message}");
        }
    }

    private static ReviewPageModel CreateBlockedOrEmptyModel(
        string title,
        string description,
        string[] requiredInputs,
        string[] missingInputs)
    {
        return new ReviewPageModel
        {
            Title = title,
            Description = description,
            ReadinessStatus = ReviewStatus.Blocked,
            RequiredInputs = requiredInputs.ToList(),
            MissingInputs = missingInputs.ToList(),
            Summary = new ReviewSummary
            {
                StatusMessage = $"Missing required artifact: {string.Join(", ", missingInputs)}",
                TotalResults = 0,
                CanRun = false,
                HasAvailableActions = false
            }
        };
    }

    private static ReviewPageModel CreateFailedModel(
        string title,
        string description,
        string errorMessage)
    {
        return new ReviewPageModel
        {
            Title = title,
            Description = description,
            ReadinessStatus = ReviewStatus.Fail,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new ReviewSummary
            {
                StatusMessage = errorMessage,
                TotalResults = 0,
                CanRun = false,
                HasAvailableActions = false
            }
        };
    }
}
