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
    /// <summary>The inputs <see cref="_current"/> was built from (<see cref="InputSignature"/>); null when it was not built from the explorers.</summary>
    private string? _builtFrom;

    /// <summary>
    /// The last semantic model per workflow role with the exact text (and, for the plan, the pack's supporting plan documents) it was parsed
    /// from. A specification switch changes one input: the constitution, plan, tasks and data model are reused instead of being parsed again
    /// (the constitution alone took ~3.5 s of the browser's only thread). Models are built from their own text only and are never mutated by
    /// <see cref="ReviewContextFactory"/>, so reusing one is exact.
    /// </summary>
    private readonly Dictionary<WorkspaceArtifactType, List<(string? Text, string? Extra, object Model)>> _models = [];

    /// <summary>Models remembered per role, most recent first: switching back to a recently viewed specification does not parse it again.</summary>
    internal const int ModelsPerRole = 3;

    /// <summary>Models parsed / reused by the last rebuild (diagnostics and tests).</summary>
    internal (int Parsed, int Reused) LastRebuildWork { get; private set; }

    /// <summary>
    /// Everything a rebuild reads, as one exact string: the explorer scope, each workflow role as the explorers resolve it (status, module, pack,
    /// candidates, the selected document and its identity, the loaded content's length) and every revision of the workspace (identity, authority,
    /// selection, scope, loaded length — supporting documents feed the pack inventory) plus the generated-documentation and import records.
    /// Equal signatures mean an identical context; any document, selection, authority, scope or body-load change makes them differ.
    /// </summary>
    internal string InputSignature(IReadOnlyList<BirkNext.Web.Services.Explorers.ArtifactExplorerState> roles)
    {
        var b = new System.Text.StringBuilder();
        b.Append(_artifactContext?.CurrentScope).Append('\n');
        foreach (var s in roles)
        {
            b.Append(s.Role).Append('|').Append(s.Status).Append('|').Append(s.Module).Append('|').Append(s.Pack).Append('|')
                .Append(s.Selected?.Id).Append('|').Append(s.Selected?.DocumentIdentity).Append('|').Append(s.Content?.Length ?? -1).Append('|');
            foreach (var a in s.Artifacts) b.Append(a.Id).Append(':').Append(a.DocumentIdentity).Append(',');
            b.Append('\n');
        }
        var lifecycle = _artifacts.SddLifecycle;
        foreach (var r in lifecycle.Revisions)
            b.Append(r.RevisionId).Append('|').Append(r.Fingerprint).Append('|').Append(r.Authority).Append('|').Append(r.IsCurrentSelection).Append('|')
                .Append(r.WorkspaceScope).Append('|').Append(r.Content.Length).Append('|').Append(r.SupersededByRevisionId).Append('\n');
        b.Append(lifecycle.GeneratedDocumentContents.Count).Append('\n');
        foreach (var set in lifecycle.GeneratedDocumentationSets)
        {
            b.Append(set.ProjectImportId).Append('|');
            foreach (var a in set.Artifacts) b.Append(a.RelativePath).Append(':').Append(a.Fingerprint).Append(',');
            b.Append('\n');
        }
        foreach (var import in lifecycle.ProjectImports)
            b.Append(import.ImportId).Append('|').Append(import.SourceSnapshotId).Append('|').Append(import.ArchiveFileName).Append('\n');
        return b.ToString();
    }

    public event EventHandler? ReviewContextChanged;

    public ReviewContextProvider(
        IWorkspaceArtifactRepository artifacts,
        IWorkspaceUpdateCoordinator updates,
        IConstitutionAnalysisService constitutionService,
        IPlanAnalysisService planService,
        IDataModelAnalysisService dataModelService,
        ILogger<ReviewContextProvider> logger,
        BirkNext.Web.Services.Explorers.IArtifactExplorerContext? artifactContext = null,
        IWorkspaceDocumentHydration? documents = null)
    {
        _documents = documents;
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

    private readonly IWorkspaceDocumentHydration? _documents;

    private Task? _running;
    private bool _pending;

    /// <summary>
    /// Rebuilds the context. A request that arrives while a rebuild runs is never dropped: one more rebuild follows, so the context always ends
    /// on the latest selection (a quick module or feature switch used to keep the previous feature's requirements). Awaiting the returned task
    /// waits for that latest state.
    /// </summary>
    public Task RebuildAsync()
    {
        // Built from document bodies only: while they are deferred a rebuild would read nothing (and must not load them at start-up).
        // Loading the bodies rebuilds it.
        if (_documents is { IsDeferred: true }) return Task.CompletedTask;
        if (_running is { IsCompleted: false } running)
        {
            _pending = true;
            return running;
        }
        return _running = RunUntilCurrentAsync();
    }

    private async Task RunUntilCurrentAsync()
    {
        do
        {
            _pending = false;
            await RebuildOnceAsync();
        }
        while (_pending);
    }

    private async Task RebuildOnceAsync()
    {
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
            string? signature = null;
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
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var roles = await _artifactContext.GetStatesAsync([.. CurrentWorkspaceSnapshot.WorkflowRoles]);
                var rolesMs = clock.ElapsedMilliseconds;
                // Nothing the context is built from changed (an environment save, a navigation that re-announces the same workspace): the
                // current context stands. Rebuilding it reparsed every document for an identical result — seconds of the browser's only thread.
                signature = InputSignature(roles);
                var signatureMs = clock.ElapsedMilliseconds - rolesMs;
                if (_current is not null && string.Equals(signature, _builtFrom, StringComparison.Ordinal))
                {
                    _logger.LogInformation("ReviewContext inputs unchanged; rebuild skipped (roles {Roles} ms, signature {Signature} ms)", rolesMs, signatureMs);
                    return;
                }
                string? Content(WorkspaceArtifactType role) => roles.FirstOrDefault(state => state.Role == role) is { Status: BirkNext.Web.Services.Explorers.ExplorerArtifactStatus.Loaded } state ? state.Content : null;
                var (parsed, reused) = (0, 0);
                // Each changed model is parsed in its own browser task: between them the page can paint and respond (one ~14 s task before).
                async Task<T> ModelAsync<T>(WorkspaceArtifactType role, string? text, string? extra, Func<T> build) where T : class
                {
                    if (!_models.TryGetValue(role, out var memos)) _models[role] = memos = [];
                    var at = memos.FindIndex(m => m.Model is T && (ReferenceEquals(m.Text, text) || string.Equals(m.Text, text, StringComparison.Ordinal))
                        && string.Equals(m.Extra, extra, StringComparison.Ordinal));
                    if (at >= 0)
                    {
                        var hit = memos[at];
                        memos.RemoveAt(at);
                        memos.Insert(0, hit);
                        reused++;
                        return (T)hit.Model;
                    }
                    if (parsed++ > 0) await Task.Delay(1);
                    var model = build();
                    memos.Insert(0, (text, extra, model));
                    if (memos.Count > ModelsPerRole) memos.RemoveRange(ModelsPerRole, memos.Count - ModelsPerRole);
                    return model;
                }
                var constitutionText = Content(WorkspaceArtifactType.Constitution);
                constitution = await ModelAsync(WorkspaceArtifactType.Constitution, constitutionText, null, () => BuildConstitutionModel(constitutionText));
                var specificationText = Content(WorkspaceArtifactType.Specification);
                specification = await ModelAsync(WorkspaceArtifactType.Specification, specificationText, null, () => BuildSpecificationModel(specificationText));
                // Requirement IDs in the pack's own supporting plan documents (research, contracts …) are plan references too.
                var beforeInventory = clock.ElapsedMilliseconds;
                var inventory = await _artifactContext.GetPackInventoryAsync();
                var inventoryMs = clock.ElapsedMilliseconds - beforeInventory;
                var planText = Content(WorkspaceArtifactType.Plan);
                plan = await ModelAsync(WorkspaceArtifactType.Plan, planText, PlanInventoryKey(inventory), () => BuildPlanModel(planText, inventory));
                var tasksText = Content(WorkspaceArtifactType.Tasks);
                tasks = await ModelAsync(WorkspaceArtifactType.Tasks, tasksText, null, () => BuildTasksModel(tasksText));
                var dataModelText = Content(WorkspaceArtifactType.DataModel);
                dataModel = await ModelAsync(WorkspaceArtifactType.DataModel, dataModelText, null, () => BuildDataModelModel(dataModelText));
                LastRebuildWork = (parsed, reused);
                _logger.LogInformation("ReviewContext models: {Parsed} parsed, {Reused} reused (roles {Roles} ms, signature {Signature} ms, pack inventory {Inventory} ms, total {Total} ms)",
                    parsed, reused, rolesMs, signatureMs, inventoryMs, clock.ElapsedMilliseconds);
            }

            // Create ReviewContext from semantic models
            _current = ReviewContextFactory.Create(constitution, specification, plan, tasks, dataModel);
            _builtFrom = signature;

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
            _builtFrom = null;
            // Don't rethrow - gracefully handle errors
        }
        finally
        {
            _isRebuilding = false;
        }

    }

    /// <summary>Exactly what <see cref="PlanDocumentSet.AddPackScopedMentions"/> reads from the inventory: the pack's supporting plan documents.</summary>
    internal static string PlanInventoryKey(BirkNext.Web.Services.Explorers.ArtifactPackInventory? inventory)
    {
        if (inventory is null) return "";
        var b = new System.Text.StringBuilder();
        foreach (var d in inventory.PackScopedFamily(WorkspaceArtifactType.Plan).Where(d => !d.IsPrimary && !string.IsNullOrWhiteSpace(d.Text)))
            b.Append(d.FileName).Append('\u0001').Append(d.Text).Append('\u0002');
        return b.ToString();
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

    private PlanSemanticModel BuildPlanModel(string? text, BirkNext.Web.Services.Explorers.ArtifactPackInventory? inventory = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogInformation("Plan artifact is empty, using empty model");
                return new PlanSemanticModel();
            }

            var document = _planService.Parse(text);
            PlanDocumentSet.AddPackScopedMentions(document, inventory);
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
