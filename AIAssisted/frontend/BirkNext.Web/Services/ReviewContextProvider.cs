using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Runtime owner of ReviewContext.
/// Responsible for building and maintaining the current semantic analysis state.
///
/// ReviewContext is derived from workspace artifacts via semantic model builders.
/// This is the ONLY place where ReviewContext is instantiated at runtime.
///
/// Pages NEVER build ReviewContext directly.
/// Pages NEVER cache ReviewContext.
/// Pages request current context via GetCurrent().
/// </summary>
public interface IReviewContextProvider
{
    /// <summary>
    /// Get the current ReviewContext (may be null if workspace is incomplete).
    /// </summary>
    ReviewContext? GetCurrent();

    /// <summary>
    /// Rebuild ReviewContext from current workspace artifacts.
    /// Call after artifacts have been loaded/modified.
    /// </summary>
    Task RebuildAsync();

    /// <summary>
    /// Fires when ReviewContext has been rebuilt and is ready for consumption.
    /// </summary>
    event EventHandler? ReviewContextChanged;
}

public sealed class ReviewContextProvider : IReviewContextProvider, IDisposable
{
    private readonly IWorkspaceArtifactRepository _artifacts;
    private readonly IWorkspaceUpdateCoordinator _updates;
    private readonly IConstitutionAnalysisService _constitutionService;
    private readonly IPlanAnalysisService _planService;
    private readonly IDataModelAnalysisService _dataModelService;
    private readonly ILogger<ReviewContextProvider> _logger;

    private ReviewContext? _current;
    private bool _isRebuilding;

    public event EventHandler? ReviewContextChanged;

    public ReviewContextProvider(
        IWorkspaceArtifactRepository artifacts,
        IWorkspaceUpdateCoordinator updates,
        IConstitutionAnalysisService constitutionService,
        IPlanAnalysisService planService,
        IDataModelAnalysisService dataModelService,
        ILogger<ReviewContextProvider> logger)
    {
        _artifacts = artifacts;
        _updates = updates;
        _constitutionService = constitutionService;
        _planService = planService;
        _dataModelService = dataModelService;
        _logger = logger;

        // Subscribe to workspace changes
        _updates.ArtifactsChanged += OnArtifactsChanged;

        _logger.LogInformation("ReviewContextProvider initialized");
    }

    public ReviewContext? GetCurrent() => _current;

    public Task RebuildAsync()
    {
        if (_isRebuilding)
        {
            _logger.LogWarning("ReviewContext rebuild already in progress, skipping");
            return Task.CompletedTask;
        }

        _isRebuilding = true;
        try
        {
            _logger.LogInformation("Rebuilding ReviewContext from workspace artifacts");

            // Build semantic models from artifacts
            var constitution = BuildConstitutionModel();
            var specification = BuildSpecificationModel();
            var plan = BuildPlanModel();
            var tasks = BuildTasksModel();
            var dataModel = BuildDataModelModel();

            // Create ReviewContext from semantic models
            _current = ReviewContextFactory.Create(constitution, specification, plan, tasks, dataModel);

            _logger.LogInformation(
                "ReviewContext rebuilt: {ReqCount} requirements, {TaskCount} tasks, {EntityCount} entities",
                _current.GetRequirements().Count,
                _current.GetTasks().Count,
                _current.GetDataEntities().Count);

            // Notify consumers
            ReviewContextChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rebuilding ReviewContext");
            _current = null;
            // Don't rethrow - gracefully handle errors
        }
        finally
        {
            _isRebuilding = false;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Build ConstitutionSemanticModel from artifact.
    /// Gracefully handles missing/malformed artifacts.
    /// </summary>
    private ConstitutionSemanticModel BuildConstitutionModel()
    {
        try
        {
            if (!_artifacts.Has(WorkspaceArtifactType.Constitution))
            {
                _logger.LogInformation("Constitution artifact not loaded, using empty model");
                return new ConstitutionSemanticModel();
            }

            var artifact = _artifacts.Get(WorkspaceArtifactType.Constitution);
            if (artifact == null || string.IsNullOrWhiteSpace(artifact.Text))
            {
                _logger.LogInformation("Constitution artifact is empty, using empty model");
                return new ConstitutionSemanticModel();
            }

            var document = _constitutionService.Parse(artifact.Text);
            var model = ConstitutionAnalysisService.BuildSemanticModel(document);
            _logger.LogInformation("Constitution model built: {RuleCount} rules", model.Rules.Count);
            return model;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error building Constitution model, using empty");
            return new ConstitutionSemanticModel();
        }
    }

    /// <summary>
    /// Build SpecificationSemanticModel from artifact.
    /// Gracefully handles missing/malformed artifacts.
    /// </summary>
    private SpecificationSemanticModel BuildSpecificationModel()
    {
        try
        {
            if (!_artifacts.Has(WorkspaceArtifactType.Specification))
            {
                _logger.LogInformation("Specification artifact not loaded, using empty model");
                return new SpecificationSemanticModel();
            }

            var artifact = _artifacts.Get(WorkspaceArtifactType.Specification);
            if (artifact == null || string.IsNullOrWhiteSpace(artifact.Text))
            {
                _logger.LogInformation("Specification artifact is empty, using empty model");
                return new SpecificationSemanticModel();
            }

            var specTree = SpecExplorerService.Parse(artifact.Text);
            var model = SpecExplorerService.BuildSemanticModel(specTree, artifact.Text);
            _logger.LogInformation("Specification model built: {ReqCount} requirements", model.Requirements.Count);
            return model;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error building Specification model, using empty");
            return new SpecificationSemanticModel();
        }
    }

    /// <summary>
    /// Build PlanSemanticModel from artifact.
    /// Gracefully handles missing/malformed artifacts.
    /// </summary>
    private PlanSemanticModel BuildPlanModel()
    {
        try
        {
            if (!_artifacts.Has(WorkspaceArtifactType.Plan))
            {
                _logger.LogInformation("Plan artifact not loaded, using empty model");
                return new PlanSemanticModel();
            }

            var artifact = _artifacts.Get(WorkspaceArtifactType.Plan);
            if (artifact == null || string.IsNullOrWhiteSpace(artifact.Text))
            {
                _logger.LogInformation("Plan artifact is empty, using empty model");
                return new PlanSemanticModel();
            }

            var document = _planService.Parse(artifact.Text);
            var model = PlanAnalysisService.BuildSemanticModel(document);
            _logger.LogInformation("Plan model built: {PhaseCount} phases", model.Phases.Count);
            return model;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error building Plan model, using empty");
            return new PlanSemanticModel();
        }
    }

    /// <summary>
    /// Build TaskSemanticModel from artifact.
    /// Gracefully handles missing/malformed artifacts.
    /// </summary>
    private TaskSemanticModel BuildTasksModel()
    {
        try
        {
            if (!_artifacts.Has(WorkspaceArtifactType.Tasks))
            {
                _logger.LogInformation("Tasks artifact not loaded, using empty model");
                return new TaskSemanticModel();
            }

            var artifact = _artifacts.Get(WorkspaceArtifactType.Tasks);
            if (artifact == null || string.IsNullOrWhiteSpace(artifact.Text))
            {
                _logger.LogInformation("Tasks artifact is empty, using empty model");
                return new TaskSemanticModel();
            }

            var taskTree = TaskExplorerService.Parse(artifact.Text);
            var model = TaskExplorerService.BuildSemanticModel(taskTree);
            _logger.LogInformation("Tasks model built: {TaskCount} tasks", model.AllTasks.Count);
            return model;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error building Tasks model, using empty");
            return new TaskSemanticModel();
        }
    }

    /// <summary>
    /// Build DataModelSemanticModel from artifact.
    /// Gracefully handles missing/malformed artifacts.
    /// </summary>
    private DataModelSemanticModel BuildDataModelModel()
    {
        try
        {
            if (!_artifacts.Has(WorkspaceArtifactType.DataModel))
            {
                _logger.LogInformation("DataModel artifact not loaded, using empty model");
                return new DataModelSemanticModel();
            }

            var artifact = _artifacts.Get(WorkspaceArtifactType.DataModel);
            if (artifact == null || string.IsNullOrWhiteSpace(artifact.Text))
            {
                _logger.LogInformation("DataModel artifact is empty, using empty model");
                return new DataModelSemanticModel();
            }

            var document = _dataModelService.Parse(artifact.Text);
            var model = DataModelAnalysisService.BuildSemanticModel(document);
            _logger.LogInformation("DataModel model built: {EntityCount} entities", model.Entities.Count);
            return model;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error building DataModel model, using empty");
            return new DataModelSemanticModel();
        }
    }

    /// <summary>
    /// Handle workspace update coordinator artifact change events.
    /// Rebuild ReviewContext exactly once per logical workspace update.
    /// </summary>
    private async void OnArtifactsChanged(object? sender, EventArgs e)
    {
        _logger.LogInformation("Artifacts changed event received");
        await RebuildAsync();
    }

    public void Dispose()
    {
        _updates.ArtifactsChanged -= OnArtifactsChanged;
        _logger.LogInformation("ReviewContextProvider disposed");
    }
}
