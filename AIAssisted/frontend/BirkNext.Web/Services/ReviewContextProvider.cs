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
    private readonly BirkNext.Web.Services.Explorers.IArtifactExplorerContext? _artifactContext;

    private ReviewContext? _current;
    private bool _isRebuilding;

    public event EventHandler? ReviewContextChanged;

    public ReviewContextProvider(
        IWorkspaceArtifactRepository artifacts,
        IWorkspaceUpdateCoordinator updates,
        IConstitutionAnalysisService constitutionService,
        IPlanAnalysisService planService,
        IDataModelAnalysisService dataModelService,
        ILogger<ReviewContextProvider> logger,
        BirkNext.Web.Services.Explorers.IArtifactExplorerContext? artifactContext = null)
    {
        _artifacts = artifacts;
        _updates = updates;
        _constitutionService = constitutionService;
        _planService = planService;
        _dataModelService = dataModelService;
        _logger = logger;
        _artifactContext = artifactContext;

        // Subscribe to workspace changes
        _updates.ArtifactsChanged += OnArtifactsChanged;
        if (_artifactContext is not null) _artifactContext.Changed += OnArtifactContextChanged;

        _logger.LogInformation("ReviewContextProvider initialized");
    }

    public ReviewContext? GetCurrent() => _current;

    public async Task RebuildAsync()
    {
        if (_isRebuilding)
        {
            _logger.LogWarning("ReviewContext rebuild already in progress, skipping");
            return;
        }

        _isRebuilding = true;
        try
        {
            _logger.LogInformation("Rebuilding ReviewContext from workspace artifacts");

            // Build every semantic input from the same selected role resolution used by the Explorers and review pages.
            // Ambiguous roles resolve to empty until the user selects an artifact; no first/latest artifact is guessed.
            ConstitutionSemanticModel constitution;
            SpecificationSemanticModel specification;
            PlanSemanticModel plan;
            TaskSemanticModel tasks;
            DataModelSemanticModel dataModel;
            if (_artifactContext is null)
            {
                constitution = BuildConstitutionModel();
                specification = BuildSpecificationModel();
                plan = BuildPlanModel();
                tasks = BuildTasksModel();
                dataModel = BuildDataModelModel();
            }
            else
            {
                var roles = await Task.WhenAll(CurrentWorkspaceSnapshot.WorkflowRoles.Select(role => _artifactContext.GetStateAsync(role)));
                string? Content(WorkspaceArtifactType role) => roles.FirstOrDefault(state => state.Role == role) is { Status: BirkNext.Web.Services.Explorers.ExplorerArtifactStatus.Loaded } state ? state.Content : null;
                constitution = BuildConstitutionModel(Content(WorkspaceArtifactType.Constitution));
                specification = BuildSpecificationModel(Content(WorkspaceArtifactType.Specification));
                plan = BuildPlanModel(Content(WorkspaceArtifactType.Plan));
                tasks = BuildTasksModel(Content(WorkspaceArtifactType.Tasks));
                dataModel = BuildDataModelModel(Content(WorkspaceArtifactType.DataModel));
            }

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

    }

    /// <summary>
    /// Build ConstitutionSemanticModel from artifact.
    /// Gracefully handles missing/malformed artifacts.
    /// </summary>
    private ConstitutionSemanticModel BuildConstitutionModel()
        => BuildConstitutionModel(_artifacts.Get(WorkspaceArtifactType.Constitution)?.Text);

    private ConstitutionSemanticModel BuildConstitutionModel(string? text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogInformation("Constitution artifact is empty, using empty model");
                return new ConstitutionSemanticModel();
            }

            var document = _constitutionService.Parse(text);
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
        => BuildSpecificationModel(_artifacts.Get(WorkspaceArtifactType.Specification)?.Text);

    private SpecificationSemanticModel BuildSpecificationModel(string? text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogInformation("Specification artifact is empty, using empty model");
                return new SpecificationSemanticModel();
            }

            var specTree = SpecExplorerService.Parse(text);
            var model = SpecExplorerService.BuildSemanticModel(specTree, text);
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
        => BuildPlanModel(_artifacts.Get(WorkspaceArtifactType.Plan)?.Text);

    private PlanSemanticModel BuildPlanModel(string? text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogInformation("Plan artifact is empty, using empty model");
                return new PlanSemanticModel();
            }

            var document = _planService.Parse(text);
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
        => BuildTasksModel(_artifacts.Get(WorkspaceArtifactType.Tasks)?.Text);

    private TaskSemanticModel BuildTasksModel(string? text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogInformation("Tasks artifact is empty, using empty model");
                return new TaskSemanticModel();
            }

            var taskTree = TaskExplorerService.Parse(text);
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
        => BuildDataModelModel(_artifacts.Get(WorkspaceArtifactType.DataModel)?.Text);

    private DataModelSemanticModel BuildDataModelModel(string? text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogInformation("DataModel artifact is empty, using empty model");
                return new DataModelSemanticModel();
            }

            var document = _dataModelService.Parse(text);
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

    private async void OnArtifactContextChanged(object? sender, EventArgs e) => await RebuildAsync();

    public void Dispose()
    {
        _updates.ArtifactsChanged -= OnArtifactsChanged;
        if (_artifactContext is not null) _artifactContext.Changed -= OnArtifactContextChanged;
        _logger.LogInformation("ReviewContextProvider disposed");
    }
}
