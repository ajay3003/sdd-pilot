Redefined QA Review Studio Workflow Architecture

Core principle:
One runtime source per concern.
Database persists state.
Repository owns loaded artifacts.
ReviewContext owns semantic analysis.
WorkflowSession owns workflow/session state.
UI owns no business state.

==================================================
1. Layers
==================================================

Persistence Layer
- SavedWorkspace
- SavedWorkspaceArtifact
- WorkspaceReviewProgress
- WorkspacePersistenceService
- ApprovalPersistenceService

Artifact Runtime Layer
- WorkspaceArtifactRepository
- Loaded artifacts
- Artifact metadata
- Artifact count
- Artifact hash

Semantic Analysis Layer
- ReviewContext
- ReviewContextFactory
- Semantic model builders
- Traceability
- Coverage
- Compliance
- QA analysis

Workflow Runtime Layer
- WorkspaceSession
- WorkflowState
- ApprovalState
- ReadinessState
- RecommendedWorkflowService
- WorkflowReadinessService

UI Layer
- RecommendedWorkflow.razor
- WorkspaceManager.razor
- Dashboard
- Analysis pages

==================================================
2. Canonical ownership
==================================================

Workspace identity:
Owner = WorkspaceSession

Persisted workspace:
Owner = WorkspacePersistenceService

Loaded artifacts:
Owner = WorkspaceArtifactRepository

Artifact count:
Owner = WorkspaceArtifactRepository
Computed fresh. Never cached elsewhere.

Semantic models:
Owner = ReviewContext

Traceability / coverage / compliance:
Owner = ReviewContext and analysis services

Approval state:
Owner = WorkspaceReviewProgress persistence
Runtime projection = ApprovalState

Workflow step state:
Owner = WorkflowState

Readiness:
Owner = WorkflowReadinessService
Computed from ArtifactRepository + ApprovalState + WorkflowState

Dashboard metrics:
Projection only.
No ownership.

UI components:
Read only.
No duplicated state.

==================================================
3. Important correction
==================================================

Database is NOT runtime source of truth.

Database:
- saves
- loads
- duplicates
- deletes

After restore, runtime decisions must use:

WorkspaceArtifactRepository
ApprovalState
WorkflowState
ReviewContext

NOT direct database queries.

==================================================
4. Service boundaries
==================================================

WorkspacePersistenceService
- Save workspace
- Load workspace
- Duplicate workspace
- Delete workspace
- Persist artifacts
- Must not compute readiness
- Must not know workflow rules

WorkspaceSessionService
- Own current workspace id/name/project
- Own dirty/saved status
- Coordinates restore
- Does not parse artifacts
- Does not compute readiness

WorkspaceArtifactRepository
- Own loaded artifacts
- Set/Get/Clear artifacts
- Compute artifact count/hash
- No persistence logic

ReviewContextBuilder / DeliveryReadinessService
- Build ReviewContext from loaded artifacts
- No workspace approval logic
- No save/open logic

RecommendedWorkflowService
- Defines workflow steps and prerequisites
- Computes step availability from artifact presence + approvals
- Does not own artifacts
- Does not read database directly during runtime

WorkflowReadinessService
- Computes readiness from:
  - artifact status
  - approval state
  - workflow state
- No cached artifact counts
- No component-local readiness state

ApprovalService
- Approve
- Mark reviewed
- Needs changes
- Persist approval state
- Refresh runtime ApprovalState

==================================================
5. Runtime flow
==================================================

Load Sample Project
→ ArtifactRepository.Set(...)
→ Recompute artifact count
→ Build/refresh ReviewContext
→ Recompute WorkflowState
→ Recompute Readiness
→ UI refresh

Save Workspace
→ WorkspaceSession metadata
→ ArtifactRepository contents
→ WorkspacePersistenceService persists workspace + artifacts
→ ApprovalService persists approvals
→ Mark session saved

Open Workspace
→ WorkspacePersistenceService loads workspace + artifacts + approvals
→ WorkspaceSession sets active workspace
→ ArtifactRepository populated
→ ApprovalState loaded
→ ReviewContext rebuilt
→ WorkflowState recomputed
→ Readiness recomputed
→ UI refresh

Approve Step
→ ApprovalService persists approval
→ ApprovalState refreshed
→ WorkflowState recomputed
→ Readiness recomputed
→ UI refresh

Duplicate Workspace
→ WorkspacePersistenceService copies:
   - metadata
   - artifacts
   - approval state
→ New workspace id
→ Does not modify active workspace unless opened

==================================================
6. Migration phases
==================================================

Phase 1:
Fix artifact persistence.
Save/Open must preserve artifacts.

Phase 2:
Fix artifact restore.
Repository must match saved workspace exactly.

Phase 3:
Introduce lightweight WorkspaceSession.
Only workspace id/name/project/save status.

Phase 4:
Remove duplicated artifact counts.
All counts from ArtifactRepository.

Phase 5:
Fix approval actions.
Approve/Review/NeedsChanges must update persisted approval state and runtime ApprovalState.

Phase 6:
Fix workflow step availability.
Steps use ArtifactRepository + ApprovalState only.

Phase 7:
Fix readiness.
Readiness computed fresh from runtime state.

Phase 8:
Rebuild ReviewContext after artifact changes/open workspace.

Phase 9:
Wire analysis pages to ReviewContext only.

Phase 10:
Remove old caches, fallback paths, and duplicate state services.

==================================================
7. Success criteria
==================================================

- Workspace Manager, Current Workspace, Dashboard and Workflow show same artifact count.
- Save/Open restores all artifacts.
- Duplicate preserves artifacts and approvals.
- Approve immediately changes manual review count.
- Readiness changes immediately after approval.
- No step remains locked when prerequisites are met.
- ReviewContext is not a God object.
- UI contains no business state.
- Database is only persistence, not runtime decision source.