BirkNext Workflow - Practical Implementation Plan

Date: 2026-07-03
Scope: 6-Phase Stabilization (Minimum Viable Architecture)
Principle: Fix broken, remove duplicate, integrate existing
Total Duration: 12-18 days (estimated)

---
PHASE 1: PERSISTENCE LAYER FIXES

Objective: Ensure SaveCurrentAsync, LoadAsync, and RestoreWorkspaceAsync work correctly.

Files to Modify:
- BirkNext.Api/Services/WorkspacePersistenceService.cs (3 methods)
- BirkNext.Web/Services/WorkspaceSessionRestoreService.cs (1 method)

Problem Statement:
- SaveCurrentAsync: Does NOT persist artifact content to SavedWorkspaceArtifact table
- LoadAsync: May not include artifact collection
- RestoreWorkspaceAsync: Clears repository before populating, loses artifacts if DTO empty

Changes Required:

Fix 1: SaveCurrentAsync must persist artifacts

Current behavior:
  SaveCurrentAsync(workspaceId, metadata)
    → Updates SavedWorkspaces row only
    → SavedWorkspaceArtifact rows unchanged

Required behavior:
  SaveCurrentAsync(workspaceId, metadata, currentArtifacts)
    → Update SavedWorkspaces row
    → For each artifact in currentArtifacts:
      → Create/update SavedWorkspaceArtifact row

Implementation:
1. Add method: SaveArtifactsAsync(workspaceId, artifacts)
2. SaveCurrentAsync calls SaveArtifactsAsync at end
3. SaveArtifactsAsync creates/updates artifact rows in database
4. Ensure idempotent (safe to call multiple times)

Fix 2: LoadAsync must include artifacts

Change from:
  _db.SavedWorkspaces.FirstOrDefault(...)

To:
  _db.SavedWorkspaces
    .Include(w => w.Artifacts)
    .FirstOrDefault(...)

Fix 3: RestoreWorkspaceAsync handles empty list gracefully

Current behavior:
  Clear all artifacts
  foreach (artifact in workspace.Artifacts)  ← May be empty
    Set(artifact)
  → Result: Repository empty

Required behavior:
  Only clear artifacts that will be restored
  foreach (artifact in workspace.Artifacts)
    Set(artifact)
  if (artifacts.Count == 0)
    Log warning: "No artifacts in loaded workspace"
  → Result: Graceful handling

Implementation:
1. Don't use Clear(all); only clear what's being replaced
2. Add null check for artifact list
3. Log if artifacts expected but not loaded
4. Continue execution (don't crash)

Expected Behavior Change:
- Save workspace → Artifacts persisted to database
- Load workspace → Artifacts restored from database
- No data loss during save/open cycle

Risk Level: MEDIUM
- Touches critical save/load paths
- Data-affecting operation
- Requires careful testing

Rollback Strategy:
1. Remove SaveArtifactsAsync call from SaveCurrentAsync
2. Revert LoadAsync Include() change
3. Restore original Clear() logic in RestoreWorkspaceAsync

Regression Tests:
- Save workspace with 1 artifact → Load workspace → Artifact present ✓
- Save workspace with 5 artifacts → Load workspace → All 5 present ✓
- Save workspace with 0 artifacts → Load workspace → No crash ✓
- Open previously saved workspace → Artifacts restored ✓
- Open workspace → Approve step → Load different workspace → Approvals correct ✓

Expected Duration: 2-3 days

---
PHASE 2: ELIMINATE ARTIFACT OWNERSHIP DUPLICATE

Objective: Remove WorkspaceArtifactStatusService cache; make WorkspaceArtifactRepository the single owner.

Files to Modify:
- BirkNext.Web/Services/WorkspaceArtifactStatusService.cs (simplify/delete)
- BirkNext.Web/Services/WorkflowReadinessService.cs (change source of artifact status)
- BirkNext.Web/Services/WorkspaceArtifactRepository.cs (add artifact count property)

Problem Statement:
- Artifact count cached in WorkspaceArtifactStatusService
- Artifact count also computable from WorkspaceArtifactRepository
- Cache can become stale; duplicate source of truth
- StatusChanged event creates implicit coupling

Changes Required:

Change 1: WorkspaceArtifactRepository owns artifact count

Current:
  _artifacts: Dictionary<WorkspaceArtifactType, WorkspaceArtifact>

Add:
  public int ArtifactCount => _artifacts.Count;

  public IReadOnlyList<WorkspaceArtifactType> LoadedArtifactTypes
    => _artifacts.Keys.ToList();

Change 2: WorkspaceArtifactStatusService becomes passthrough (simplify)

Current:
  GetStatus()
    → _cachedStatus (returns cached object)
    → StatusChanged event fires

New:
  GetStatus()
    → Query repository directly
    → Return fresh WorkspaceArtifactStatus
    → NO caching
    → NO event

Change 3: WorkflowReadinessService reads from repository directly

Current:
  _status = _artifactStatusService.GetStatus()

New:
  int artifactCount = _repository.ArtifactCount;
  hasConstitution = _repository.Has(Constitution);
  ... (for each artifact type)

Expected Behavior Change:
- No cache staleness (repository is source of truth)
- Artifact counts always accurate
- No duplicate ownership
- Same behavior, cleaner state

Risk Level: LOW
- Only removes duplicate state
- Repository query is O(1)
- No logic changes
- Easy rollback

Rollback Strategy:
1. Restore WorkspaceArtifactStatusService caching
2. Revert WorkflowReadinessService to use StatusService
3. Restore StatusChanged event

Regression Tests:
- Load artifacts → Count equals loaded count ✓
- Save workspace → Count persists ✓
- Open workspace → Count correct ✓
- Dashboard, WorkspaceManager, RecommendedWorkflow show same count ✓
- Performance unchanged (queries fast) ✓

Expected Duration: 1-2 days

---
PHASE 3: APPROVAL STATE OWNERSHIP

Objective: Fix approval flow so approval state is owned in ONE place and updates immediately.

Files to Modify:
- BirkNext.Api/Services/RecommendedWorkflowService.cs (approval methods)
- BirkNext.Web/Services/WorkflowReadinessService.cs (reads approval state)
- BirkNext.Web/Pages/RecommendedWorkflow.razor (triggers approval refresh)

Problem Statement:
- Approval state queried from database repeatedly
- No in-memory cache of approvals
- No clear approval state holder on frontend
- Readiness computed without current approval state
- Approvals don't update readiness immediately

Changes Required:

Option A: Lightweight ApprovalService (Recommended)

New file: BirkNext.Web/Services/ApprovalService.cs

public class ApprovalService
{
    private Dictionary<string, WorkspaceReviewProgress> _approvalCache = new();

    public void LoadApprovalsForWorkspace(Guid workspaceId, List<WorkspaceReviewProgress> approvals)
    {
        _approvalCache = approvals.ToDictionary(a => a.StepKey);
    }

    public bool IsApproved(string stepKey) => _approvalCache.ContainsKey(stepKey) && ...

    public void InvalidateCache() => _approvalCache.Clear();
}

Option B: Improve existing flow (If ApprovalService too heavy)

Instead of new service, just:
1. WorkspaceSessionRestoreService loads approvals when restoring workspace
2. Store in simple Dict<string, ApprovalState>
3. WorkflowReadinessService reads from dict
4. RecommendedWorkflow.razor calls InvalidateCache() after approval

Implementation (Option A - Lightweight Service):

1. Create ApprovalService:
  - Load approvals on workspace restore
  - Cache in memory
  - Provide IsApproved(stepKey) method
  - Provide InvalidateCache() for clearing
2. Modify WorkspaceSessionRestoreService.RestoreWorkspaceAsync():
// Load approvals from database
var approvals = await _db.WorkspaceReviewProgress
  .Where(p => p.WorkspaceId == workspace.Id)
  .ToListAsync();

// Pass to approval service
_approvalService.LoadApprovalsForWorkspace(workspace.Id, approvals);
3. Modify WorkflowReadinessService.GetReadinessAsync():
// Read approval state from service, not API
var approvalsDict = _approvalService.GetApprovals();
var approvedCount = approvalsDict.Values.Count(a => a.ApprovalState == Approved);
4. Modify RecommendedWorkflow.razor.ApproveStepAsync():
// After approval completes
await WorkflowApi.ApproveStepAsync(...);
_approvalService.InvalidateCache();
await RefreshReadinessAsync();

Expected Behavior Change:
- Approval immediately updates approval cache
- Readiness recomputes with new approval state
- UI updates immediately (no 1+ second delay)
- No repeated database queries during session

Risk Level: LOW (with Option A: lightweight service)
- New service is simple (3-4 methods)
- Easy to test
- Clear responsibility
- Easy rollback

Rollback Strategy (Option A):
1. Delete ApprovalService
2. Revert RestoreWorkspaceAsync to not load approvals
3. Revert WorkflowReadinessService to query API
4. Remove InvalidateCache() call from component

Regression Tests:
- Approve step → Readiness updates immediately ✓
- Readiness percentage changes on approval ✓
- Step status updates on approval ✓
- Load different workspace → Different approvals show ✓
- Approval persists to database ✓

Expected Duration: 2-3 days

---
PHASE 4: FIX WORKFLOW STATE AND READINESS

Objective: Ensure workflow steps lock/unlock correctly and readiness recomputes correctly.

Files to Modify:
- BirkNext.Api/Services/RecommendedWorkflowService.cs (step status computation)
- BirkNext.Web/Services/WorkflowReadinessService.cs (readiness computation)
- BirkNext.Web/Pages/RecommendedWorkflow.razor (refresh logic)

Problem Statement:
- Workflow steps not updating when approvals change
- Readiness cache not invalidating properly
- Step dependencies not always respected
- No clear readiness recomputation trigger

Changes Required:

Fix 1: Ensure BuildWorkflowStepsAsync always gets current approval state

Backend: RecommendedWorkflowService.BuildWorkflowStepsAsync()

Current:
  public async Task<List<WorkflowStepViewModel>> BuildWorkflowStepsAsync(
    Guid workspaceId,
    bool hasConstitution, ...)
  {
    // Load approvals
    var progressRecords = await _db.WorkspaceReviewProgress
      .Where(p => p.WorkspaceId == workspaceId)
      .ToListAsync();  ← Queries database
  }

Problem: workspaceId could be Guid.Empty if CurrentWorkspace not set

Required:
  Ensure workspaceId is NEVER Guid.Empty
  Add assertion/null check

Fix 2: Ensure WorkflowReadinessService recomputes from fresh state

Frontend: WorkflowReadinessService.GetReadinessAsync()

Current:
  private WorkflowReadiness _readiness;  ← Cached

  GetReadinessAsync()
  {
    if (_readiness != null)
      return _readiness;  ← Stale cache returned
  }

Required:
  Always recompute from current artifacts + approvals
  Cache only if necessary for performance
  Invalidate cache explicitly when approvals change

Fix 3: Clear readiness cache on approval change

RecommendedWorkflow.razor.ApproveStepAsync()

Current:
  await WorkflowApi.ApproveStepAsync(...);
  // No refresh

Required:
  await WorkflowApi.ApproveStepAsync(...);
  await WorkflowReadiness.InvalidateCache();  ← New method
  await RefreshReadinessAsync();

Expected Behavior Change:
- Approve step → Workflow steps immediately reflect new status
- Approve step → Readiness immediately updates
- No cache staleness
- All approvals correctly tracked

Risk Level: MEDIUM
- Touches readiness computation (critical path)
- Must ensure no infinite loops
- Cache invalidation is tricky

Rollback Strategy:
1. Restore original readiness caching logic
2. Remove InvalidateCache() calls
3. Revert BuildWorkflowStepsAsync to original logic

Regression Tests:
- Approve step → Dependent steps unlock ✓
- Approve step → Readiness increases ✓
- Load workspace → Existing approvals respected ✓
- Workflow steps show correct status ✓
- Approval workflow: Load → Approve → Verify works ✓

Expected Duration: 2-3 days

---
PHASE 5: REVIEWCONTEXT INTEGRATION

Objective: Wire ReviewContext into the initialization flow; make it available (but optional).

Files to Modify:
- BirkNext.Web/Services/WorkspaceSessionRestoreService.cs (build ReviewContext on restore)
- BirkNext.Web/Services/ReviewContextFactory.cs (no changes; already exists)
- Analysis service consumers (already expect ReviewContext; no changes)

Problem Statement:
- ReviewContextRebuildNeeded event fires but ReviewContext is null
- Semantic analysis layer disconnected from workflow
- ReviewContext never built; analysis pages don't use it
- Potential semantic information available but unused

Changes Required:

Change 1: Build ReviewContext during workspace restore

WorkspaceSessionRestoreService.RestoreWorkspaceAsync()

Add after artifacts are loaded:

TRY
{
    // Build semantic models
    var constitution = ConstitutionAnalysisService.BuildSemanticModel(
        _repository.Get(Constitution)?.Text ?? "");
    var specification = SpecExplorerService.BuildSemanticModel(
        _repository.Get(Specification)?.Text ?? "", "");
    var plan = PlanAnalysisService.BuildSemanticModel(
        _repository.Get(Plan)?.Text ?? "");
    var tasks = TaskExplorerService.BuildSemanticModel(
        _repository.Get(Tasks)?.Text ?? "");
    var dataModel = new DataModelSemanticModel();

    // Assemble ReviewContext
    _reviewContext = ReviewContextFactory.Create(
        constitution,
        specification,
        plan,
        tasks,
        dataModel);
}
CATCH (Exception ex)
{
    _logger.LogWarning(ex, "Failed to build ReviewContext");
    _reviewContext = null;  // Graceful degradation
}

Change 2: Make ReviewContext available to consumers

WorkspaceSessionRestoreService
{
    public ReviewContext? GetReviewContext() => _reviewContext;
}

Analysis pages and services consume via:
    var context = await _restoreService.GetReviewContext();
    if (context != null)
    {
        // Use semantic analysis
    }

Expected Behavior Change:
- ReviewContext built on workspace restore
- Available to analysis pages
- Workflow layer can optionally use for enhanced metrics
- Graceful handling if parsing fails

Risk Level: LOW
- Additive change (only builds, doesn't modify state)
- Parsing failures handled gracefully
- No behavior change if ReviewContext null
- Easy rollback

Rollback Strategy:
1. Remove ReviewContext building code
2. Remove GetReviewContext() method
3. Analysis pages revert to building ReviewContext on demand

Regression Tests:
- Restore workspace → ReviewContext built ✓
- ReviewContext accessible to analysis pages ✓
- Malformed artifacts handled gracefully ✓
- Workflow continues if ReviewContext null ✓
- Analysis pages use ReviewContext correctly ✓

Expected Duration: 1-2 days

---
PHASE 6: CLEANUP AND VALIDATION

Objective: Remove obsolete code, verify ownership rules, test end-to-end.

Files to Modify:
- Remove unused services/methods
- Add code comments documenting ownership
- Add comprehensive integration tests

Changes Required:

Change 1: Mark obsolete code

WorkspaceSessionService (legacy)
  [Obsolete("Use WorkspaceArtifactRepository instead")]

WorkspaceArtifactStatusService (if cache removed)
  [Obsolete("Query WorkspaceArtifactRepository.ArtifactCount instead")]

ReviewContextRebuildNeeded event (if not used)
  [Obsolete("ReviewContext built automatically on restore")]

Change 2: Add ownership documentation

// WorkspaceArtifactRepository
/// <summary>
/// OWNS: Loaded artifacts in current session.
/// SINGLE SOURCE OF TRUTH for artifact state at runtime.
/// Artifact count computed from _artifacts.Count.
/// </summary>

// WorkspaceSessionService
/// <summary>
/// OWNS: Current workspace ID, name, project name.
/// Set during RestoreWorkspaceAsync.
/// Read-only after initialization.
/// </summary>

// ApprovalService (if created)
/// <summary>
/// OWNS: Current approval state for workspace.
/// Cached from WorkspaceReviewProgress on restore.
/// Invalidated when approvals change.
/// </summary>

Change 3: Comprehensive integration tests

Test scenarios:
  1. Load sample → Save → Open → Verify all artifacts restored
  2. Open → Approve → Verify readiness updates immediately
  3. Open → Upload spec → Verify workflow unlocks
  4. Save → Load different workspace → Verify approvals correct
  5. Readiness computed correctly (all paths)
  6. Dashboard, Workflow, Manager show same counts
  7. ReviewContext built; available to pages
  8. Graceful handling of malformed artifacts

Expected Behavior Change:
- No functional changes (all behavior from Phases 1-5 preserved)
- Code is cleaner and documented
- Ownership clear to future maintainers
- Integration tests prevent regressions

Risk Level: NONE
- Code cleanup only; no behavior changes
- Tests only verify existing behavior
- Safe rollback (just delete tests)

Rollback Strategy:
- Delete new tests
- Remove obsolete attributes
- Remove new documentation

Regression Tests:
- All existing tests pass ✓
- All new integration tests pass ✓
- No new warnings/errors ✓
- Documentation accurate ✓

Expected Duration: 2-3 days

---
IMPLEMENTATION SUMMARY

┌───────┬───────────────────────┬─────────────────────────────────────────────────────────┬────────┬──────────┬────────────┐
│ Phase │       Objective       │                      Files Changed                      │  Risk  │ Duration │ Shippable? │
├───────┼───────────────────────┼─────────────────────────────────────────────────────────┼────────┼──────────┼────────────┤
│ 1     │ Fix persistence       │ WorkspacePersistenceService, RestoreService             │ MEDIUM │ 2-3d     │ ✓ Yes      │
├───────┼───────────────────────┼─────────────────────────────────────────────────────────┼────────┼──────────┼────────────┤
│ 2     │ Remove artifact cache │ StatusService, ReadinessService, Repository             │ LOW    │ 1-2d     │ ✓ Yes      │
├───────┼───────────────────────┼─────────────────────────────────────────────────────────┼────────┼──────────┼────────────┤
│ 3     │ Approval ownership    │ ApprovalService (new), RestoreService, ReadinessService │ LOW    │ 2-3d     │ ✓ Yes      │
├───────┼───────────────────────┼─────────────────────────────────────────────────────────┼────────┼──────────┼────────────┤
│ 4     │ Workflow/Readiness    │ RecommendedWorkflowService, ReadinessService, Component │ MEDIUM │ 2-3d     │ ✓ Yes      │
├───────┼───────────────────────┼─────────────────────────────────────────────────────────┼────────┼──────────┼────────────┤
│ 5     │ ReviewContext wire-in │ RestoreService                                          │ LOW    │ 1-2d     │ ✓ Yes      │
├───────┼───────────────────────┼─────────────────────────────────────────────────────────┼────────┼──────────┼────────────┤
│ 6     │ Cleanup/Tests         │ All, plus new test suite                                │ NONE   │ 2-3d     │ ✓ Yes      │
└───────┴───────────────────────┴─────────────────────────────────────────────────────────┴────────┴──────────┴────────────┘

Total Duration: 12-18 days
Total Risk: LOW-MEDIUM (mitigated by small phases and rollback plans)
Code Churn: Minimal (fix broken, remove duplicate, wire existing)
New Services: 1 (ApprovalService - lightweight)

---
CRITICAL SUCCESS FACTORS

1. Phase 1 must succeed first - Unblocks all other phases
2. Each phase independently shippable - Don't combine phases
3. Rollback tested for each phase - Before committing to next phase
4. Regression tests comprehensive - Catch regressions early
5. ReviewContext remains optional - Workflow works with or without it
6. No God objects introduced - Keep services focused

---
EXPECTED FINAL STATE

After all 6 phases:

✓ Persistence: Save/open works correctly, no data loss
✓ Artifact Ownership: Single source of truth (Repository)
✓ Approval Ownership: Single source of truth (ApprovalService)
✓ Workflow State: Updates immediately when artifacts/approvals change
✓ Readiness: Computed correctly, updates immediately
✓ ReviewContext: Wired in, available to analysis pages
✓ Code Quality: Clean, documented, testable

Stable, maintainable, minimal risk implementation.