# ReviewContext Design & Implementation

## What is ReviewContext?

ReviewContext is a **derived, runtime-only semantic analysis state** that aggregates semantic models built from all workspace artifacts.

**Key Properties:**
- ✓ Built only when artifacts change
- ✓ Never cached in pages
- ✓ Always current (reflects latest artifacts)
- ✓ Built deterministically from artifacts
- ✓ Owned exclusively by ReviewContextProvider

---

## ReviewContext Structure

```csharp
public sealed class ReviewContext
{
    // ── Semantic Models (one per artifact type) ──────────────────────
    public ConstitutionSemanticModel Constitution { get; init; }
    public SpecificationSemanticModel Specification { get; init; }
    public PlanSemanticModel Plan { get; init; }
    public TaskSemanticModel Tasks { get; init; }
    public DataModelSemanticModel DataModel { get; init; }

    // ── Aggregated Coverage Metrics ──────────────────────────────────
    public ReviewCoverageSummary Coverage { get; init; }

    // ── Cross-Artifact Relationships ─────────────────────────────────
    public IReadOnlyDictionary<string, List<string>> SpecToTasks { get; init; }
    public IReadOnlyDictionary<string, List<string>> SpecToConstitution { get; init; }

    // ── Query API ────────────────────────────────────────────────────
    public IReadOnlyList<SemanticRequirement> GetRequirements()
    public SemanticRequirement? GetRequirement(string id)
    public IReadOnlyList<SemanticUserStory> GetUserStories()
    public IReadOnlyList<SemanticSuccessCriterion> GetSuccessCriteria()
    public IReadOnlyList<SemanticAcceptanceScenario> GetTests()
    public IReadOnlyList<TaskItem> GetTasks()
    public IReadOnlyList<SemanticDataEntity> GetDataEntities()
    
    public IEnumerable<SemanticRequirement> GetRequirementsWithTests()
    public IEnumerable<SemanticRequirement> GetRequirementsWithoutTests()
    
    public IReadOnlyList<SemanticAcceptanceScenario> GetTests(string requirementId)
    public IReadOnlyList<string> GetLinkedTasks(string requirementId)
    public bool HasTestCoverage(string requirementId)
    
    public int GetOrphanedTestCount()
    public int GetGapsCount()
}
```

---

## Semantic Models (Built Components)

Each semantic model is built from its corresponding artifact:

### ConstitutionSemanticModel
**Built From:** Constitution artifact (markdown)  
**Built By:** ConstitutionAnalysisService.BuildSemanticModel()  
**Contains:**
- Principles (governance rules)
- Standards (quality standards)
- Constraints (system constraints)

### SpecificationSemanticModel
**Built From:** Specification artifact (markdown)  
**Built By:** SpecExplorerService.BuildSemanticModel()  
**Contains:**
- Requirements (functional requirements)
- User Stories (user narratives)
- Success Criteria (acceptance criteria)
- Acceptance Scenarios (test scenarios)
- Clarifications (implementation notes)

### PlanSemanticModel
**Built From:** Plan artifact (markdown)  
**Built By:** PlanAnalysisService.BuildSemanticModel()  
**Contains:**
- Phases (project phases)
- Milestones (key milestones)
- Deliverables (phase deliverables)

### TaskSemanticModel
**Built From:** Tasks artifact (markdown)  
**Built By:** TaskExplorerService.BuildSemanticModel()  
**Contains:**
- Task Tree (hierarchical task structure)
- All Tasks (flat list of all tasks)

### DataModelSemanticModel
**Built From:** DataModel artifact (markdown)  
**Built By:** DataModelAnalysisService.BuildSemanticModel()  
**Contains:**
- Entities (data entities)
- Relationships (entity relationships)
- Attributes (entity attributes)

---

## ReviewContextProvider (The Owner)

### Location
`frontend/BirkNext.Web/Services/ReviewContextProvider.cs`

### Constructor & Dependencies
```csharp
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
}
```

### Public API

```csharp
// Get current ReviewContext
public ReviewContext? GetCurrent() 
    => _current;

// Rebuild ReviewContext from artifacts
public Task RebuildAsync()
{
    // Implementation details below
}

// Event fired when ReviewContext has been rebuilt
public event EventHandler? ReviewContextChanged;
```

### Rebuild Process

```csharp
public Task RebuildAsync()
{
    if (_isRebuilding)
        return Task.CompletedTask;

    _isRebuilding = true;
    try
    {
        // 1. Build semantic models from artifacts
        var constitution = BuildConstitutionModel();
        var specification = BuildSpecificationModel();
        var plan = BuildPlanModel();
        var tasks = BuildTasksModel();
        var dataModel = BuildDataModelModel();

        // 2. Create ReviewContext via factory
        _current = ReviewContextFactory.Create(
            constitution, specification, plan, tasks, dataModel);

        // 3. Fire event
        ReviewContextChanged?.Invoke(this, EventArgs.Empty);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error rebuilding ReviewContext");
        _current = null;
    }
    finally
    {
        _isRebuilding = false;
    }

    return Task.CompletedTask;
}
```

### Building Individual Models

Each semantic model builder method:
1. Checks if artifact exists and is non-empty
2. Parses artifact text via service
3. Builds semantic model from parsed document
4. Handles errors gracefully (logs warning, returns empty model)

```csharp
private ConstitutionSemanticModel BuildConstitutionModel()
{
    try
    {
        if (!_artifacts.Has(WorkspaceArtifactType.Constitution))
            return new ConstitutionSemanticModel();

        var artifact = _artifacts.Get(WorkspaceArtifactType.Constitution);
        if (artifact == null || string.IsNullOrWhiteSpace(artifact.Text))
            return new ConstitutionSemanticModel();

        // Parse and build
        var document = _constitutionService.Parse(artifact.Text);
        var model = ConstitutionAnalysisService.BuildSemanticModel(document);
        
        return model;
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Error building Constitution model");
        return new ConstitutionSemanticModel();
    }
}
```

### Event Subscription

```csharp
private async void OnArtifactsChanged(object? sender, EventArgs e)
{
    _logger.LogInformation("Artifacts changed event received");
    await RebuildAsync();
}

public void Dispose()
{
    _updates.ArtifactsChanged -= OnArtifactsChanged;
}
```

---

## ReviewContextFactory (The Builder)

### Location
`frontend/BirkNext.Web/Models/ReviewContext.cs`

### Purpose
Static factory that creates ReviewContext from semantic models.

**Never modifies semantic models.**
**Never parses markdown.**
**Never rebuilds on demand.**

### Method Signature

```csharp
public static ReviewContext Create(
    ConstitutionSemanticModel constitution,
    SpecificationSemanticModel specification,
    PlanSemanticModel plan,
    TaskSemanticModel tasks,
    DataModelSemanticModel dataModel)
{
    return new ReviewContext
    {
        Constitution = constitution,
        Specification = specification,
        Plan = plan,
        Tasks = tasks,
        DataModel = dataModel,
        Coverage = BuildCoverage(...),
        SpecToTasks = BuildSpecToTasksMap(...),
        SpecToConstitution = BuildSpecToConstitutionMap(...)
    };
}
```

---

## Lifecycle Integration

### Phase 1: Initial Build (After Load Sample Project)
```
LoadArtifacts batch
    ↓
coordinator.EndUpdate()
    ↓
ArtifactsChanged event
    ↓
ReviewContextProvider.RebuildAsync()
    ↓
ReviewContextChanged event
    ↓
Pages call provider.GetCurrent()
```

### Phase 2: Restore from Database (After Open Workspace)
```
RestoreWorkspaceAsync() loads artifacts
    ↓
WorkspaceSessionRestoreService.RestoreWorkspaceAsync()
    ↓
ReviewContextProvider.RebuildAsync() called directly
    ↓
ReviewContextChanged event
    ↓
Pages can access ReviewContext immediately
```

### Phase 3: Artifact Modification (User edits)
```
User modifies artifact
    ↓
repository.Set()
    ↓
(Auto-save handles persistence)
    ↓
No ReviewContext rebuild triggered at edit time
    ↓
ReviewContext is rebuilt when auto-save sends artifacts to backend?
    
OR

(If batch context available)
    ↓
coordinator.EndUpdate()
    ↓
ReviewContextProvider rebuilds
```

---

## Important Design Decisions

### Decision 1: ReviewContext is Derived State, Not Cached

**Why?**
- Pages should always see current state
- Eliminates cache invalidation complexity
- Prevents stale data bugs

**How?**
- Pages call `GetCurrent()` instead of caching
- ReviewContext is rebuilt atomically on each batch
- No partial updates or stale reads

### Decision 2: ReviewContextProvider is Sole Owner

**Why?**
- Single source of truth
- Consistent rebuild timing
- Easy to test and debug

**How?**
- ReviewContextFactory.Create() only in ReviewContextProvider
- Utility services build temporary contexts for analysis only
- Tests build temporary contexts for contracts

### Decision 3: Rebuild is Synchronous

**Why?**
- Fast (no I/O, just object construction)
- Deterministic
- No race conditions

**How?**
- Analysis services are stateless
- Parsing and building are pure functions
- No async operations needed

### Decision 4: No Direct Markdown Parsing in Pages

**Why?**
- Centralizes parsing logic
- Prevents duplicate semantic model building
- Easier to maintain and update parsers

**How?**
- ReviewContextProvider parses on rebuild
- Explorer pages parse only their artifact for display
- Analysis tools parse their own artifacts (isolated)

---

## Testing ReviewContext

### Contract Tests
Location: `BirkNext.Web.Tests/Integration/ReviewContextContractTests.cs`

Verify ReviewContext behavior:
- Builds from complete workspace ✓
- Handles missing artifacts ✓
- Handles malformed artifacts ✓
- Rebuilds correctly ✓
- Is deterministic ✓
- Cross-artifact links work ✓
- Query API works ✓
- No exceptions on edge cases ✓

### Lifecycle Integration Tests
Location: `BirkNext.Web.Tests/Integration/ReviewContextLifecycleIntegrationTest.cs`

Verify provider behavior:
- Single batch → single rebuild ✓
- Nested batches → single rebuild ✓
- Sequential updates → multiple rebuilds ✓
- No mutations → no rebuild ✓
- RestoreWorkspaceAsync triggers rebuild ✓
- Partial artifacts handled gracefully ✓
- Errors handled gracefully ✓

### Unit Tests
Each service has unit tests for its BuildSemanticModel() method.

---

## Performance Characteristics

### Build Time
- **Typical:** <100ms (depends on artifact size)
- **Fast:** Parsing + object construction only
- **No I/O:** All data from memory

### Memory Usage
- **Per Context:** ~1-5MB (depends on artifact count/size)
- **Single Instance:** ReviewContextProvider keeps only current
- **No Leaks:** Garbage collector cleans old contexts

### Scalability
- **Large Specs:** Builds correctly (may take 100-500ms)
- **Concurrent Loads:** Single-threaded JavaScript, not a concern
- **Session Lifetime:** Workspace lives for entire browser session

---

## Common Patterns

### Pattern 1: Consume ReviewContext in a Page

```razor
@page "/my-page"
@inject IReviewContextProvider reviewContextProvider

@code {
    private ReviewContext? _context;

    protected override void OnInitialized()
    {
        _context = reviewContextProvider.GetCurrent();
        if (_context == null)
        {
            ErrorMessage = "No workspace loaded";
            return;
        }

        var requirements = _context.GetRequirementsWithoutTests();
        _requirementsWithoutTests = requirements.Count();
    }
}
```

### Pattern 2: React to ReviewContext Changes

```csharp
@inject IReviewContextProvider reviewContextProvider

@code {
    private bool _isReady;

    protected override void OnInitialized()
    {
        reviewContextProvider.ReviewContextChanged += OnReviewContextChanged;
    }

    private void OnReviewContextChanged(object? sender, EventArgs e)
    {
        _isReady = reviewContextProvider.GetCurrent() != null;
        StateHasChanged();
    }

    void IDisposable.Dispose()
    {
        reviewContextProvider.ReviewContextChanged -= OnReviewContextChanged;
    }
}
```

### Pattern 3: Build Temporary Context (Analysis Tool)

```csharp
// For isolated analysis, build temporary ReviewContext
var tempContext = ReviewContextFactory.Create(
    ConstitutionAnalysisService.BuildSemanticModel(constitutionDoc),
    SpecExplorerService.BuildSemanticModel(specTree, specText),
    PlanAnalysisService.BuildSemanticModel(planDoc),
    TaskExplorerService.BuildSemanticModel(taskTree),
    new DataModelSemanticModel());

// Use for analysis
var report = TraceabilityService.Analyze(constitution, spec, plan, tasks, tempContext);
```

---

## Debugging Checklist

- [ ] Is ReviewContextProvider registered in DI? (Program.cs)
- [ ] Is IReviewContextProvider injected? (not ReviewContextProvider)
- [ ] Is GetCurrent() called, not Create()?
- [ ] Is artifact loaded? (check repository.Has())
- [ ] Is ReviewContextChanged event firing? (add Debug.WriteLine)
- [ ] Are semantic models non-empty? (check model.Requirements.Count)
- [ ] Is ReviewContext null? (means workspace incomplete)

---

See [04-workspace.md](04-workspace.md) for persistence strategy.
