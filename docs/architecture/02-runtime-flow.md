# Runtime Flow & Event Choreography

## Event-Driven Architecture

BirkNext uses event-driven architecture to keep ReviewContext in sync with workspace artifacts.

### Primary Event: ArtifactsChanged

**Source:** IWorkspaceUpdateCoordinator
**Subscribers:**
1. ReviewContextProvider (rebuilds ReviewContext)
2. WorkflowReadinessService (updates workflow state)
3. WorkspaceAutoSaveService (triggers auto-save)

**Guarantee:** Fires exactly once per logical batch, regardless of mutation count.

---

## Scenario 1: Load Sample Project (Fresh Workspace)

### Timeline

```
T=0:  User clicks "Load Sample Project"
      ↓
T=1:  SampleProjects.razor → LoadArtifacts()
      
      coordinator.BeginUpdate()           [Depth: 0→1]
      repository.Set(Constitution, ...)   [No event yet]
      repository.Set(Specification, ...) [No event yet]
      repository.Set(Plan, ...)          [No event yet]
      repository.Set(Tasks, ...)         [No event yet]
      repository.Set(DataModel, ...)     [No event yet]
      ↓
T=2:  coordinator.NotifyMutation()        [Marks batch as having mutations]
      ↓
T=3:  coordinator.EndUpdate()             [Depth: 1→0, triggers event]
      ↓
T=4:  [COORDINATOR] ArtifactsChanged event fires
      ↓
T=5:  [REVIEWCONTEXTPROVIDER] 
      OnArtifactsChanged() called
      RebuildAsync():
        - Get all artifacts from repository
        - Parse each via analysis services
        - Build 5 semantic models
        - ReviewContextFactory.Create()
        - Set _current = newReviewContext
      ↓
T=6:  [REVIEWCONTEXTPROVIDER]
      ReviewContextChanged event fires
      ↓
T=7:  [WORKFLOWREADINESSSERVICE]
      OnArtifactsChanged() called
      Updates workflow readiness state
      Fires ReadinessChanged
      ↓
T=8:  [WORKSPACEAUTOSAVESERVICE]
      OnArtifactsChanged() called
      Debounce timer resets (3 sec wait, 30 sec throttle)
      ↓
T=9:  [RECOMMENDED WORKFLOW]
      Subscribes to ReadinessChanged
      Updates workflow steps UI
      ↓
T=10: [Any page calling provider.GetCurrent()]
      Gets current ReviewContext with all semantic models
```

### Artifact Count Tracking

```
T=1:  User loads sample
      Repository artifact count: 0→1→2→3→4→5

T=3:  coordinate.EndUpdate()
      All 5 artifacts loaded ✓
      One ArtifactsChanged event ✓

T=5:  ReviewContextProvider rebuild
      Reads 5 artifacts from repository
      Builds 5 semantic models
      Creates one ReviewContext
      
T=6:  ReviewContextChanged fires
      Pages can access ReviewContext with all 5 artifacts

T=12-15: (After 3 sec wait + potential debounce)
         POST /api/workspace-persistence/auto-save
         Sends all 5 artifacts to backend
         Backend saves to database
```

**Key Guarantee:** No matter how many artifacts are set, ArtifactsChanged fires exactly once.

---

## Scenario 2: Open Saved Workspace

### Timeline

```
T=0:  User selects workspace in Workspace Manager
      ↓
T=1:  Load SavedWorkspaceDto from backend
      API: GET /api/workspace/{id}
      Returns: SavedWorkspaceDto with 5 artifacts
      ↓
T=2:  WorkspaceSessionRestoreService.RestoreWorkspaceAsync(workspace)
      
      Clear all existing artifacts
      repository.Clear(Constitution)
      repository.Clear(Specification)
      repository.Clear(Plan)
      repository.Clear(Tasks)
      repository.Clear(DataModel)
      ↓
T=3:  Restore artifacts from SavedWorkspaceDto
      for each artifact in workspace.Artifacts:
        repository.Set(type, content, fileName, ...)
        [No event yet - restoring is atomic]
      ↓
T=4:  ReviewContextProvider.RebuildAsync() called directly
      (Not waiting for coordinator event)
      
      Build semantic models from restored artifacts
      ReviewContextFactory.Create()
      _current = newReviewContext
      ↓
T=5:  ReviewContextProvider.ReviewContextChanged fires
      ↓
T=6:  RecommendedWorkflow page listens
      Gets current ReviewContext
      Renders workflow steps with current artifact state
      ↓
T=7:  User can approve/reject workflow steps
      WorkflowApprovalButtons work correctly
      (Because workspace ID is known)
```

**Important:** Restore calls `RebuildAsync()` directly, not via coordinator event. This ensures ReviewContext is available immediately after restore.

---

## Scenario 3: Modify Artifact (Auto-Save)

### Timeline

```
T=0:  User edits Constitution in ConstitutionExplorer
      ↓
T=1:  OnPanelTextChanged(newText)
      BuildSemanticModel(newText)
      repository.Set(WorkspaceArtifactType.Constitution, newText)
      ↓
T=2:  repository.Set() fires internal event
      WorkspaceArtifactRepository.ReviewContextRebuildNeeded
      (Legacy event, not actively used)
      ↓
T=3:  IWorkspaceUpdateCoordinator.ArtifactsChanged
      (Not fired yet - no batch context)
      
T=4:  Wait 3 seconds (debounce timer)
      ↓
T=5:  WorkspaceAutoSaveService timer fires
      Gets all artifacts from repository
      Builds HTTP POST request
      
      POST /api/workspace-persistence/auto-save
      Body: {
        "generatedName": "Auto_Saved_Workspace_...",
        "artifacts": [ /* 5 artifacts */ ]
      }
      ↓
T=6:  Backend: WorkspacePersistenceController.AutoSave()
      Service.AutoSaveAsync(name, artifacts)
      Saves workspace + artifacts to database
      
T=7:  Response: 200 OK with SavedWorkspace
      Frontend: Auto-save complete
      
T=8:  ReviewContext is already up-to-date
      (Built when artifact was modified, not waiting for auto-save)
```

**Key Point:** ReviewContext updates happen at artifact modification time. Auto-save happens asynchronously and doesn't block ReviewContext availability.

---

## Scenario 4: Approve Workflow Step

### Timeline

```
T=0:  User clicks "Approve" button in RecommendedWorkflow
      ↓
T=1:  RecommendedWorkflow.razor
      Gets current workspace ID
      (From WorkflowReadinessService state)
      ↓
T=2:  Call: workflowApi.ApproveStepAsync(workspaceId, stepKey)
      
      POST /api/recommended-workflow/approve-step
      Body: { workspaceId, stepKey }
      ↓
T=3:  Backend: RecommendedWorkflowController.ApproveStepAsync()
      Service.ApproveStepAsync(workspaceId, stepKey)
      
      Load Workspace from database
      Update WorkflowApproval state
      Save to database
      ↓
T=4:  Response: 200 OK with updated WorkflowReadiness
      Frontend: Button state updates
      
T=5:  Critical: WorkspaceId must be known
      (Comes from auto-saved workspace or explicitly set)
      If WorkspaceId = Guid.Empty → Approval fails
```

**Depends On:**
- WorkspacePersistenceService.GetCurrentStateAsync() returns valid WorkspaceId
- Auto-save has run at least once (so workspace exists in database)
- Workspace artifacts have been loaded (so ReviewContext is available)

---

## Event Subscription Map

### Who Subscribes to ArtifactsChanged?

```
IWorkspaceUpdateCoordinator.ArtifactsChanged
├─ ReviewContextProvider
│  ├─ OnArtifactsChanged()
│  └─ RebuildAsync()
│     └─ ReviewContextChanged fires
│
├─ WorkflowReadinessService
│  ├─ OnArtifactsChanged()
│  └─ Fires ReadinessChanged
│
└─ WorkspaceAutoSaveService
   ├─ OnArtifactsChanged()
   └─ Resets debounce timer
```

### Who Subscribes to ReviewContextChanged?

```
ReviewContextProvider.ReviewContextChanged
└─ (Currently: No runtime subscribers)
   (Pages call GetCurrent() instead of subscribing)
```

### Who Subscribes to ReadinessChanged?

```
WorkflowReadinessService.ReadinessChanged
├─ RecommendedWorkflow.razor
│  ├─ Refreshes workflow steps
│  └─ Updates button states
│
└─ Quality Review page
   └─ Updates workflow section
```

---

## Critical Invariants

### Invariant 1: Single Rebuild Per Batch

**Rule:** No matter how many artifacts are modified in a batch, ReviewContext rebuilds exactly once.

**Enforced By:**
- BeginUpdate/EndUpdate nesting counter
- NotifyMutation() boolean flag
- ArtifactsChanged fires only at depth 0 with mutations

**Verification:**
```csharp
// Batch with 5 mutations
coordinator.BeginUpdate();
repository.Set(Constitution, ...);  // No event
repository.Set(Specification, ...); // No event
repository.Set(Plan, ...);          // No event
repository.Set(Tasks, ...);         // No event
repository.Set(DataModel, ...);     // No event
coordinator.NotifyMutation();       // Mark batch as mutated
coordinator.EndUpdate();            // Fire ONE ArtifactsChanged
// Result: ReviewContextProvider rebuilds ONCE
```

### Invariant 2: Deterministic ReviewContext

**Rule:** Same artifacts → same ReviewContext every time.

**Enforced By:**
- ReviewContextFactory.Create() is deterministic
- Semantic model builders are deterministic
- No randomization or time-dependent logic

### Invariant 3: No Orphan Events

**Rule:** No page or service has its own rebuild pipeline.

**Enforced By:**
- Architecture review completed
- All direct ReviewContextFactory.Create() calls in ReviewContextProvider only
- Utility services build temporary ReviewContext for analysis only

### Invariant 4: Workspace ID Availability

**Rule:** ApproveStepAsync() must have a valid workspace ID.

**Depends On:**
- WorkspacePersistenceService.GetCurrentStateAsync() persists workspace ID across requests
- Auto-save has run (so workspace exists in backend)
- Scoped vs Singleton service lifetime management

---

## Timing Considerations

### Auto-Save Debounce
```
Artifact changed → 3 second wait → POST /api/auto-save
                 → Another change → 3 second wait resets
                 → Max 30 second throttle
```

### ReviewContext Rebuild
```
Artifacts changed → IMMEDIATE rebuild (synchronous)
                 → ReviewContextChanged fires
                 → Pages can call GetCurrent()
```

### Workflow Readiness Update
```
Artifacts changed → IMMEDIATE state update
                 → ReadinessChanged fires
                 → Pages update UI
```

---

## Debugging Tips

### To Verify ReviewContext is Rebuilt

```csharp
// In any page
@inject IReviewContextProvider provider

protected override void OnInitialized()
{
    var context = provider.GetCurrent();
    if (context == null)
        Console.WriteLine("ERROR: ReviewContext is null - workspace incomplete");
    else
        Console.WriteLine($"✓ ReviewContext built: {context.GetRequirements().Count} requirements");
}
```

### To Trace Event Timing

```csharp
// In WorkspaceUpdateCoordinator
public void EndUpdate()
{
    _updateBatchDepth--;
    if (_updateBatchDepth == 0 && _batchHasMutations)
    {
        Debug.WriteLine("COORD: ArtifactsChanged event firing");
        ArtifactsChanged?.Invoke(this, EventArgs.Empty);
        _batchHasMutations = false;
    }
}

// In ReviewContextProvider
private async void OnArtifactsChanged(object? sender, EventArgs e)
{
    Debug.WriteLine("RCTX-PROVIDER: Rebuild started");
    await RebuildAsync();
    Debug.WriteLine("RCTX-PROVIDER: Rebuild completed, ReviewContextChanged fired");
}
```

### To Monitor Auto-Save

```csharp
// In WorkspaceAutoSaveService
private void OnArtifactsChanged(object? sender, EventArgs e)
{
    Debug.WriteLine($"AUTO-SAVE: Artifacts changed, debounce timer reset");
    // Timer starts 3-second countdown...
}

private async Task PerformAutoSaveAsync()
{
    Debug.WriteLine($"AUTO-SAVE: POST request to backend");
    var result = await _persistenceApi.AutoSaveAsync(name, artifacts);
    Debug.WriteLine($"AUTO-SAVE: Response status {result?.Status}");
}
```

---

See [03-reviewcontext.md](03-reviewcontext.md) for implementation details of ReviewContextProvider.
