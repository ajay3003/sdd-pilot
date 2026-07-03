BirkNext Workflow Architecture - Deep Analysis

Date: 2026-07-03
Analysis Type: Current vs. Target Architecture Gap Analysis
Scope: Complete runtime state ownership, dependencies, and migration risk assessment
Status: Architecture Analysis Only (No Implementation)

---
PART 1: CURRENT ARCHITECTURE DIAGRAM

┌─────────────────────────────────────────────────────────────────────┐
│ UI LAYER (RecommendedWorkflow.razor, WorkspaceManager.razor)        │
├─────────────────────────────────────────────────────────────────────┤
│ Injects:                                                            │
│  • IWorkflowReadinessService                                        │
│  • IWorkspacePersistenceApiService                                  │
│  • IWorkspaceSessionRestoreService                                  │
│  • IWorkspaceAutoSaveService                                        │
│  • IRecommendedWorkflowApiService                                   │
│  • ILogger                                                          │
│                                                                     │
│ Local State:                                                        │
│  • _readiness (WorkflowReadiness) ← CACHED DUPLICATE               │
│  • _isSaving                                                        │
│  • _approvingStepId                                                 │
│  • _workspaces (in WorkspaceManager)                                │
│  • _selectedWorkspaceId                                             │
│                                                                     │
│ PROBLEM: Component owns transient state; no clear session object   │
└──────────────────┬──────────────────────────────────────────────────┘
                   │
          (unclear dependencies)
                   │
┌──────────────────▼──────────────────────────────────────────────────┐
│ WORKFLOW RUNTIME LAYER                                             │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│ ┌─ Frontend (Scoped/Singleton)  ──────────────────────────────┐   │
│ │                                                               │   │
│ │ WorkflowReadinessService                                     │   │
│ │  Injects:                                                    │   │
│ │   • IWorkspaceArtifactRepository (singleton)                │   │
│ │   • IWorkspaceSessionRestoreService (singleton)            │   │
│ │   • IWorkspaceArtifactStatusService (singleton)  ← CACHES  │   │
│ │   • IRecommendedWorkflowApiService (HTTP)                  │   │
│ │                                                               │   │
│ │  Outputs:                                                    │   │
│ │   • _readiness (cached locally)                            │   │
│ │   • StatusChanged event                                     │   │
│ │                                                               │   │
│ │  PROBLEM: Composition of multiple sources; no unified      │   │
│ │  session object                                              │   │
│ │                                                               │   │
│ ├────────────────────────────────────────────────────────────┤   │
│ │                                                               │   │
│ │ WorkspaceSessionRestoreService                              │   │
│ │  Owns:                                                       │   │
│ │   • CurrentWorkspaceId (memory)                            │   │
│ │   • CurrentWorkspaceName (memory)                          │   │
│ │   • _currentMetadata (memory)                              │   │
│ │   • ReviewContextRebuildNeeded event                       │   │
│ │                                                               │   │
│ │  PROBLEM: Scattered metadata; not part of unified object    │   │
│ │                                                               │   │
│ ├────────────────────────────────────────────────────────────┤   │
│ │                                                               │   │
│ │ WorkspaceArtifactRepository                                 │   │
│ │  Owns:                                                       │   │
│ │   • _artifacts { Constitution, Spec, Plan, Tasks, Data }   │   │
│ │   • ProjectName                                             │   │
│ │                                                               │   │
│ │  PROBLEM: No clear artifact count tracking; count computed  │   │
│ │  separately in StatusService                                │   │
│ │                                                               │   │
│ ├────────────────────────────────────────────────────────────┤   │
│ │                                                               │   │
│ │ WorkspaceArtifactStatusService                              │   │
│ │  Owns:                                                       │   │
│ │   • _cachedStatus (HasConstitution, ArtifactCount, etc.)   │   │
│ │   • StatusChanged event                                     │   │
│ │                                                               │   │
│ │  PROBLEM: DUPLICATE of repository state; cache can stale   │   │
│ │                                                               │   │
│ └────────────────────────────────────────────────────────────┘   │
│                                                                    │
│ ┌─ Backend (Scoped)  ──────────────────────────────────────────┐  │
│ │                                                               │  │
│ │ RecommendedWorkflowService                                   │  │
│ │  Owns:                                                        │  │
│ │   • WorkflowStepDefinitions (static)                        │  │
│ │   • ApprovalDependencies (static)                           │  │
│ │   • Step status computation logic                           │  │
│ │                                                               │  │
│ │  Reads:                                                       │  │
│ │   • WorkspaceReviewProgress (DB query each time)  ← REPEATED│  │
│ │   • Artifact flags (hasConstitution, etc.) from frontend   │  │
│ │                                                               │  │
│ │  PROBLEM: No approval service layer; queries DB directly    │  │
│ │  PROBLEM: Step status not cached; recomputed every call     │  │
│ │                                                               │  │
│ └────────────────────────────────────────────────────────────┘   │
│                                                                    │
│ OVERALL PROBLEM:                                                  │
│  • No unified WorkflowSessionContext                              │
│  • No ApprovalService to manage approval state                    │
│  • Artifact count duplicated in StatusService                     │
│  • No unified approval state holder                               │
│  • Readiness cached in both service AND component                 │
│  • Circular event dependencies (StatusChanged → ReadinessChanged)│
│                                                                    │
└──────────────────┬──────────────────────────────────────────────────┘
                   │
         (mixed upward/downward)
                   │
┌──────────────────▼──────────────────────────────────────────────────┐
│ SEMANTIC ANALYSIS LAYER                                            │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│ ReviewContext                                                     │
│  Owns: Constitution, Spec, Plan, Tasks, DataModel, links         │
│  Built by: ReviewContextFactory                                   │
│  Consumed by: QAReadiness, Compliance, Traceability              │
│                                                                    │
│ PROBLEM: Never built for workflow; event fires but ReviewContext  │
│ is null                                                            │
│                                                                    │
│ DeliveryReadinessService                                          │
│  Owns: Gate logic, delivery assessment                            │
│  Consumes: ReviewContext from builders                            │
│  Used by: QualityReview pages                                     │
│                                                                    │
│ PROBLEM: Not connected to workflow; isolated from main flow       │
│                                                                    │
└──────────────────┬──────────────────────────────────────────────────┘
                   │
          (downward only, OK)
                   │
┌──────────────────▼──────────────────────────────────────────────────┐
│ ARTIFACT LAYER                                                     │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│ Semantic Model Builders (Stateless)                               │
│  • ConstitutionAnalysisService                                    │
│  • SpecExplorerService                                            │
│  • PlanAnalysisService                                            │
│  • TaskExplorerService                                            │
│  • DataModelSemanticModel                                         │
│                                                                    │
│ ReviewContextFactory (Static)                                     │
│  • Assembles models → ReviewContext                               │
│                                                                    │
└──────────────────┬──────────────────────────────────────────────────┘
                   │
        (downward only, OK)
                   │
┌──────────────────▼──────────────────────────────────────────────────┐
│ PERSISTENCE LAYER                                                  │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│ Backend:                                                           │
│  • WorkspacePersistenceService                                    │
│    ├─ SaveCurrentAsync (BROKEN: doesn't persist artifacts)       │
│    ├─ SaveAsAsync (OK: creates artifacts)                        │
│    ├─ LoadAsync (OK: loads with artifacts)                       │
│    └─ PROBLEM: Inconsistent save behavior                        │
│                                                                    │
│  • RecommendedWorkflowService (approval operations)              │
│    ├─ ApproveStepAsync (persists to WorkspaceReviewProgress)    │
│    └─ PROBLEM: No dedicated approval service                     │
│                                                                    │
│  • Database tables:                                                │
│    ├─ SavedWorkspaces                                             │
│    ├─ SavedWorkspaceArtifacts                                     │
│    └─ WorkspaceReviewProgress                                     │
│                                                                    │
│ Frontend:                                                          │
│  • WorkspacePersistenceApiService (HTTP bridge)                   │
│                                                                    │
│ PROBLEM: Persistence is dirty; not truly "persistence only"       │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘

---
PART 2: TARGET ARCHITECTURE DIAGRAM

┌─────────────────────────────────────────────────────────────────────┐
│ UI LAYER (RecommendedWorkflow.razor, WorkspaceManager.razor)        │
├─────────────────────────────────────────────────────────────────────┤
│ Pure projection - NO BUSINESS LOGIC STATE                           │
│                                                                     │
│ Injects:                                                            │
│  • IWorkflowSessionContext (unified facade)                         │
│  • IWorkspacePersistenceApiService (for save/load operations)      │
│  • ILogger                                                          │
│                                                                     │
│ Local state: ONLY UI state (collapsed panels, sort order, etc.)    │
│                                                                     │
│ Does NOT own:                                                       │
│  • Readiness                                                        │
│  • Artifact state                                                   │
│  • Approval state                                                   │
│  • Workflow state                                                   │
│                                                                     │
│ Pattern: Query session, render, handle user actions                 │
│                                                                     │
└──────────────────┬──────────────────────────────────────────────────┘
                   │
          (pure injection)
                   │
┌──────────────────▼──────────────────────────────────────────────────┐
│ WORKFLOW RUNTIME LAYER                                             │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│ ┌─ Coordinator: WorkflowSessionContext (Facade)  ────────────┐   │
│ │  Unified object combining:                                  │   │
│ │   • WorkspaceSessionService (current workspace ID)         │   │
│ │   • WorkspaceArtifactRepository (loaded artifacts)         │   │
│ │   • ApprovalService (approval state from DB)               │   │
│ │   • WorkflowStateService (computed workflow state)         │   │
│ │   • ReviewContext (optional semantic analysis)             │   │
│ │   • WorkflowReadinessService (computed readiness)          │   │
│ │                                                              │   │
│ │  SINGLE point of access for all session information         │   │
│ │  Components inject THIS, not individual services            │   │
│ └──────────────────────────────────────────────────────────┘   │
│                                                                    │
│ ┌─ Component: WorkspaceSessionService  ──────────────────────┐   │
│ │  OWNS: CurrentWorkspaceId (single source of truth)         │   │
│ │  OWNS: CurrentWorkspaceName                                │   │
│ │  OWNS: ProjectName                                          │   │
│ │  READONLY (no business logic, just state holder)            │   │
│ └──────────────────────────────────────────────────────────┘   │
│                                                                    │
│ ┌─ Component: WorkspaceArtifactRepository  ─────────────────┐   │
│ │  OWNS: Loaded artifacts { Constitution, Spec, etc. }      │   │
│ │  OWNS: Artifact count (computed from artifact keys)        │   │
│ │  SINGLE point of truth for artifact state                  │   │
│ │  No caching elsewhere                                       │   │
│ └──────────────────────────────────────────────────────────┘   │
│                                                                    │
│ ┌─ Component: ApprovalService  ──────────────────────────────┐   │
│ │  OWNS: Current approval state (from WorkspaceReviewProgress)   │
│ │  Persists: Approval changes to database                    │   │
│ │  SINGLE point of truth for approval state                  │   │
│ └──────────────────────────────────────────────────────────┘   │
│                                                                    │
│ ┌─ Component: WorkflowStateService  ─────────────────────────┐  │
│ │  OWNS: Computed workflow step status                       │   │
│ │  Reads: Artifacts + Approvals (from other components)      │   │
│ │  Computes: Which steps locked/ready/completed              │   │
│ │  NOT persisted (always recomputed from inputs)              │   │
│ └──────────────────────────────────────────────────────────┘   │
│                                                                    │
│ ┌─ Component: WorkflowReadinessService  ──────────────────────┐  │
│ │  OWNS: Computed readiness percentage                        │   │
│ │  Reads: Artifacts + Approvals + WorkflowState (from session)   │
│ │  Computes: 0-100% overall readiness                         │   │
│ │  NOT persisted (always recomputed from inputs)              │   │
│ │  Optional: Consumes ReviewContext for enhanced metrics      │   │
│ └──────────────────────────────────────────────────────────┘   │
│                                                                    │
│ DESIGN PRINCIPLES:                                                │
│  ✓ Every state has ONE owner                                      │
│  ✓ Components composed into single facade                         │
│  ✓ Unidirectional flow (components read each other's outputs)    │
│  ✓ No caching at multiple levels                                 │
│  ✓ Computed state never persisted                                │
│                                                                    │
└──────────────────┬──────────────────────────────────────────────────┘
                   │
       (clean downward only)
                   │
┌──────────────────▼──────────────────────────────────────────────────┐
│ SEMANTIC ANALYSIS LAYER                                            │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│ ReviewContext (Pure Semantic Analysis)                            │
│  OWNS ONLY:                                                        │
│   • Constitution semantic model                                   │
│   • Specification semantic model                                  │
│   • Plan semantic model                                           │
│   • Task semantic model                                           │
│   • DataModel semantic model                                      │
│   • Cross-artifact links (Spec→Tasks, etc.)                      │
│   • Coverage metrics                                               │
│                                                                    │
│  Does NOT own:                                                     │
│   • Workspace ID                                                   │
│   • Approval state                                                │
│   • Workflow steps                                                │
│   • Readiness                                                      │
│   • Any runtime workflow state                                    │
│                                                                    │
│  Built by: ReviewContextFactory (called by WorkflowSessionContext)│
│  Used by: Analysis pages, optional workflow enhancements         │
│                                                                    │
│ DeliveryReadinessService (Connected Optional Feature)            │
│  Owns: Delivery gate logic                                         │
│  Consumes: ReviewContext from factory                            │
│                                                                    │
│ DESIGN PRINCIPLE:                                                  │
│  ✓ ReviewContext remains PURE semantic analysis                   │
│  ✓ No pollution with runtime/approval/workflow concerns           │
│  ✓ Available as OPTIONAL enhancement to workflow layer            │
│                                                                    │
└──────────────────┬──────────────────────────────────────────────────┘
                   │
        (downward only, OK)
                   │
┌──────────────────▼──────────────────────────────────────────────────┐
│ ARTIFACT LAYER                                                     │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│ Semantic Model Builders (Stateless, called on-demand)            │
│  • ConstitutionAnalysisService.BuildSemanticModel()              │
│  • SpecExplorerService.BuildSemanticModel()                      │
│  • PlanAnalysisService.BuildSemanticModel()                      │
│  • TaskExplorerService.BuildSemanticModel()                      │
│  • DataModelSemanticModel()                                       │
│                                                                    │
│ ReviewContextFactory (Static builder)                             │
│  • Create(models) → ReviewContext                                │
│                                                                    │
│ Called only by: WorkflowSessionContext initialization              │
│                                                                    │
└──────────────────┬──────────────────────────────────────────────────┘
                   │
        (downward only, OK)
                   │
┌──────────────────▼──────────────────────────────────────────────────┐
│ PERSISTENCE LAYER                                                  │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│ WorkspacePersistenceService (Clean CRUD)                         │
│  Save(workspace, artifacts, approvals) → void                    │
│  Load(workspaceId) → SavedWorkspaceDto                           │
│  List() → SavedWorkspaceDtos                                      │
│  Delete(workspaceId) → void                                       │
│  Rename(workspaceId, name) → void                                │
│  Duplicate(workspaceId) → new SavedWorkspaceDto                  │
│                                                                    │
│  NEVER computes:                                                   │
│   • Readiness                                                     │
│   • Artifact count                                                │
│   • Workflow steps                                                │
│   • Approval state (just persists)                               │
│                                                                    │
│ ApprovalPersistenceService (Approval CRUD)                       │
│  Approve(workspaceId, stepKey, data) → void                      │
│  Reject(workspaceId, stepKey, comment) → void                    │
│  Review(workspaceId, stepKey) → void                             │
│  LoadForWorkspace(workspaceId) → List<ReviewProgress>            │
│                                                                    │
│  NEVER computes:                                                   │
│   • Step status                                                    │
│   • Readiness                                                      │
│                                                                    │
│ Database Tables:                                                   │
│  • SavedWorkspaces                                                 │
│  • SavedWorkspaceArtifacts                                         │
│  • WorkspaceReviewProgress                                         │
│                                                                    │
│ DESIGN PRINCIPLE:                                                  │
│  ✓ Persistence is truly PERSISTENCE ONLY                          │
│  ✓ No business logic                                              │
│  ✓ Clean CRUD interface                                           │
│  ✓ No repeated queries in runtime layer                           │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘

---
PART 3: STATE OWNERSHIP COMPARISON MATRIX

┌────────────────────┬─────────────────────────────────────────┬─────────────────────────┬────────────────────────────────────┬────────────────────────────┐
│       State        │            Current Owner(s)             │    Current Problems     │            Target Owner            │            Gap             │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Workspace ID       │ WorkspaceSessionRestoreService          │ Scattered metadata; not │ WorkspaceSessionService (via       │ Extract metadata, compose  │
│                    │ (singleton)                             │  part of unified object │ context facade)                    │ into context               │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Workspace Name     │ WorkspaceSessionRestoreService          │ Same as above           │ WorkspaceSessionService (via       │ Same                       │
│                    │                                         │                         │ context facade)                    │                            │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Project Name       │ WorkspaceArtifactRepository +           │ Duplicated in two       │ WorkspaceSessionService (via       │ Consolidate                │
│                    │ RestoreService                          │ places                  │ context facade)                    │                            │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Loaded Artifacts   │ WorkspaceArtifactRepository (singleton) │ ✓ Correct location,     │ WorkspaceArtifactRepository (via   │ No change; just expose via │
│                    │                                         │ but...                  │ context facade)                    │  context                   │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Artifact Count     │ WorkspaceArtifactStatusService (CACHE)  │ DUPLICATED; cache can   │ WorkspaceArtifactRepository        │ Remove StatusService cache │
│                    │ + Repository                            │ stale                   │ (computed property)                │  entirely                  │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Artifact Presence  │ WorkspaceArtifactRepository             │ ✓ Correct               │ WorkspaceArtifactRepository        │ No change                  │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Artifact Hash      │ WorkspaceSessionRestoreService          │ Correct but scattered   │ WorkspaceSessionService            │ Consolidate into service   │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Approval State     │ RecommendedWorkflowService (queries DB  │ No service layer;       │ ApprovalService (caches from DB)   │ Create ApprovalService     │
│                    │ each time)                              │ inefficient             │                                    │                            │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Approval by Step   │ WorkspaceReviewProgress table +         │ Inefficient; queries DB │ ApprovalService (owns cache)       │ Consolidate into service   │
│                    │ transient queries                       │  repeatedly             │                                    │                            │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Workflow Steps     │ RecommendedWorkflowService (backend,    │ Recomputed every call;  │ WorkflowStateService (frontend,    │ Move to frontend; cache    │
│                    │ computed)                               │ no cache                │ cached)                            │ computed state             │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Step Status        │ RecommendedWorkflowService              │ Computed from artifacts │ WorkflowStateService (computed     │ Consolidate; optimize      │
│                    │ (Locked/Ready/Completed)                │  + approvals each time  │ once, cached)                      │                            │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Approval           │ RecommendedWorkflowService (static      │ ✓ Correct location      │ RecommendedWorkflowService (or     │ No change or move to       │
│ Dependencies       │ dict)                                   │                         │ WorkflowStateService)              │ WorkflowStateService       │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Readiness %        │ WorkflowReadinessService (cached) +     │ DUPLICATED in 2 places; │ WorkflowReadinessService (via      │ Remove component-level     │
│                    │ RecommendedWorkflow.razor               │  both can stale         │ context facade)                    │ caching                    │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ ArtifactReadiness  │ WorkflowReadinessService                │ ✓ Correct               │ WorkflowReadinessService           │ No change                  │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ ReviewReadiness    │ WorkflowReadinessService                │ ✓ Correct               │ WorkflowReadinessService           │ No change                  │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ ApprovalReadiness  │ WorkflowReadinessService                │ ✓ Correct               │ WorkflowReadinessService           │ No change                  │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│                    │                                         │ Never built for         │ ReviewContextFactory (built in     │ Wire into workflow         │
│ ReviewContext      │ ReviewContextFactory (built on demand)  │ workflow; event fires   │ context init)                      │ initialization             │
│                    │                                         │ but unused              │                                    │                            │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Semantic Models    │ Individual builders (stateless)         │ ✓ Correct               │ Individual builders                │ No change                  │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Coverage Metrics   │ ReviewContext (aggregated)              │ ✓ Correct               │ ReviewContext                      │ No change                  │
├────────────────────┼─────────────────────────────────────────┼─────────────────────────┼────────────────────────────────────┼────────────────────────────┤
│ Dashboard Metrics  │ Computed ad-hoc from readiness          │ Scattered logic         │ WorkflowReadinessService (provides │ Consolidate; provide       │
│                    │                                         │                         │  summary)                          │ dashboard view             │
└────────────────────┴─────────────────────────────────────────┴─────────────────────────┴────────────────────────────────────┴────────────────────────────┘

---
PART 4: RUNTIME STATE FLOW COMPARISON

Current Flow: Load Sample Project

User uploads Constitution.md
  ↓
RecommendedWorkflow.razor.OnSampleUploadedAsync()
  ↓
WorkspaceArtifactRepository.Set(Constitution, text)
  │
  ├─ [STATE CHANGE 1] Repository has Constitution
  │
  ├→ WorkspaceArtifactStatusService._cachedStatus invalidated
  │   └─ [STATE CHANGE 2] Cache cleared (duplicate state mutated)
  │
  └→ Fires: StatusChanged event
     ↓
WorkflowReadinessService.OnReadinessChanged()
  ├→ GetReadinessAsync()
  │   ├─ Queries: _workspace.Has(Constitution)
  │   ├─ Queries: _workspaceRestore.GetCurrentWorkspaceMetadataAsync()
  │   ├─ Queries: WorkspaceReviewProgress from API ← INEFFICIENT
  │   ├─ TRY: Build ReviewContext (but review context is null usually)
  │   └─ Recomputes: artifact/review/approval readiness
  │
  └─ [STATE CHANGE 3] _readiness cached in service
     └─ [STATE CHANGE 4] RecommendedWorkflow._readiness set in component

PROBLEMS:
  • 4 state changes for 1 user action
  • Duplicate caching (StatusService + WorkflowReadiness)
  • ReviewContext never built (event fires but unused)
  • No unified session object
  • ReviewContextRebuildNeeded event fires but ignored

Target Flow: Load Sample Project

User uploads Constitution.md
  ↓
RecommendedWorkflow.razor calls:
  WorkflowSessionContext.LoadArtifactAsync(Constitution, text)
  ↓
WorkspaceArtifactRepository.Set(Constitution, text)
  │
  ├─ [STATE CHANGE 1] Repository has Constitution
  │
  └─ WorkflowSessionContext automatically:
      ├─ Rebuilds ReviewContext (if artifacts parseable)
      │   [STATE CHANGE 2] Optional ReviewContext updated
      │
      ├─ Recomputes: WorkflowStateService.ComputeSteps()
      │   [STATE CHANGE 3] Workflow state (what's locked/ready)
      │
      └─ Recomputes: WorkflowReadinessService.ComputeReadiness()
          [STATE CHANGE 4] Readiness 0-100%

UI queries: WorkflowSessionContext
  ├─ Gets: Artifacts, ReviewContext, WorkflowState, Readiness
  └─ Renders

BENEFITS:
  • 4 state changes still (unavoidable), but coordinated
  • NO duplicate caching
  • ReviewContext built and available
  • Single unified session object
  • Clear ownership and flow

---
PART 5: OWNERSHIP VIOLATIONS IN CURRENT IMPLEMENTATION

Critical Violations

┌───────────────────────────────┬───────────────────────────────────────────────────────────────────────────────┬────────────────────────────────┬──────────┐
│           Violation           │                                   Location                                    │             Impact             │ Severity │
├───────────────────────────────┼───────────────────────────────────────────────────────────────────────────────┼────────────────────────────────┼──────────┤
│ Artifact count duplicated     │ WorkspaceArtifactStatusService._cachedStatus                                  │ Multiple sources of truth;     │ CRITICAL │
│                               │                                                                               │ cache staleness                │          │
├───────────────────────────────┼───────────────────────────────────────────────────────────────────────────────┼────────────────────────────────┼──────────┤
│ Readiness cached at multiple  │ WorkflowReadinessService + RecommendedWorkflow.razor                          │ Stale UI; approval changes not │ CRITICAL │
│ levels                        │                                                                               │  visible immediately           │          │
├───────────────────────────────┼───────────────────────────────────────────────────────────────────────────────┼────────────────────────────────┼──────────┤
│ No approval service           │ Approvals scattered in RecommendedWorkflowService + component                 │ No clean approval state        │ CRITICAL │
│                               │                                                                               │ holder; queries DB repeatedly  │          │
├───────────────────────────────┼───────────────────────────────────────────────────────────────────────────────┼────────────────────────────────┼──────────┤
│                               │ Multiple injections: IWorkspaceArtifactRepository,                            │ Unclear ownership; components  │          │
│ No unified session object     │ IWorkspaceSessionRestoreService, IWorkspaceArtifactStatusService, etc.        │ don't know which service owns  │ CRITICAL │
│                               │                                                                               │ what                           │          │
├───────────────────────────────┼───────────────────────────────────────────────────────────────────────────────┼────────────────────────────────┼──────────┤
│                               │                                                                               │ Semantic analysis layer        │          │
│ ReviewContext never built     │ ReviewContextRebuildNeeded event fires but ReviewContext stays null           │ completely disconnected from   │ CRITICAL │
│                               │                                                                               │ workflow                       │          │
├───────────────────────────────┼───────────────────────────────────────────────────────────────────────────────┼────────────────────────────────┼──────────┤
│ SaveCurrentAsync doesn't      │ WorkspacePersistenceService.SaveCurrentAsync()                                │ Data loss: save→load loses     │ CRITICAL │
│ persist artifacts             │                                                                               │ artifacts                      │          │
├───────────────────────────────┼───────────────────────────────────────────────────────────────────────────────┼────────────────────────────────┼──────────┤
│ RestoreWorkspaceAsync clears  │ WorkspaceSessionRestoreService.RestoreWorkspaceAsync()                        │ Data loss: open workspace      │ CRITICAL │
│ before populating             │                                                                               │ loses all artifacts            │          │
├───────────────────────────────┼───────────────────────────────────────────────────────────────────────────────┼────────────────────────────────┼──────────┤
│ Backend duplicates frontend   │ RecommendedWorkflowService computes steps already computed by frontend        │ Inconsistent state between     │ HIGH     │
│ work                          │                                                                               │ backend and UI                 │          │
├───────────────────────────────┼───────────────────────────────────────────────────────────────────────────────┼────────────────────────────────┼──────────┤
│ No clear readiness ownership  │ Split between WorkflowReadinessService and RecommendedWorkflowService         │ Calculation logic in two       │ HIGH     │
│                               │                                                                               │ places; can diverge            │          │
├───────────────────────────────┼───────────────────────────────────────────────────────────────────────────────┼────────────────────────────────┼──────────┤
│ Artifact metadata scattered   │ WorkspaceSessionRestoreService owns ID/Name, WorkspaceArtifactRepository owns │ Metadata fragmented; no single │ HIGH     │
│                               │  ProjectName                                                                  │  workspace object              │          │
└───────────────────────────────┴───────────────────────────────────────────────────────────────────────────────┴────────────────────────────────┴──────────┘

---
PART 6: DUPLICATE STATE LOCATIONS

┌───────────┬─────────────────────────────────────────────────┬────────────────────────────────────────────────────────────────┬─────────────┬──────────────┐
│   State   │                   Location 1                    │                           Location 2                           │ Location 3  │   Problem    │
├───────────┼─────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────┼─────────────┼──────────────┤
│           │                                                 │                                                                │ (computed   │ Triple       │
│ Artifact  │ WorkspaceArtifactRepository.Has().Count()       │ WorkspaceArtifactStatusService._cachedStatus.ArtifactCount     │ on demand   │ source;      │
│ Count     │                                                 │                                                                │ in          │ cache can    │
│           │                                                 │                                                                │ GetStatus)  │ diverge      │
├───────────┼─────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────┼─────────────┼──────────────┤
│ Artifact  │ WorkspaceArtifactRepository._artifacts.Keys     │ WorkspaceArtifactStatusService._cachedStatus.{HasConstitution, │             │ Cache        │
│ Presence  │                                                 │  HasSpec, ...}                                                 │             │ duplication  │
├───────────┼─────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────┼─────────────┼──────────────┤
│ Workspace │ WorkspaceSessionRestoreService._currentMetadata │ (scattered across components)                                  │             │ No unified   │
│  Metadata │                                                 │                                                                │             │ object       │
├───────────┼─────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────┼─────────────┼──────────────┤
│ Readiness │ WorkflowReadinessService._readiness             │ RecommendedWorkflow.razor._readiness                           │             │ Both cached; │
│           │                                                 │                                                                │             │  both stale  │
├───────────┼─────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────┼─────────────┼──────────────┤
│           │                                                 │                                                                │             │ No in-memory │
│ Approval  │ WorkspaceReviewProgress (DB)                    │ (queries each time in ReadinessService)                        │             │  cache;      │
│ State     │                                                 │                                                                │             │ repeated DB  │
│           │                                                 │                                                                │             │ queries      │
├───────────┼─────────────────────────────────────────────────┼────────────────────────────────────────────────────────────────┼─────────────┼──────────────┤
│ Workflow  │ RecommendedWorkflowService (backend, computed)  │ (only on backend; frontend recalculates)                       │             │ Split        │
│ Steps     │                                                 │                                                                │             │ computation  │
└───────────┴─────────────────────────────────────────────────┴────────────────────────────────────────────────────────────────┴─────────────┴──────────────┘

---
PART 7: CIRCULAR DEPENDENCIES

Current Circular Patterns

RecommendedWorkflow.razor
  ↓ injects
WorkflowReadinessService
  ├─ injects: WorkspaceArtifactRepository
  ├─ injects: WorkspaceSessionRestoreService
  ├─ injects: WorkspaceArtifactStatusService
  └─ injects: RecommendedWorkflowApiService (backend)
      ↓
  RecommendedWorkflowService (backend)
      ├─ Queries: WorkspaceReviewProgress (approval state)
      ├─ Computes: Workflow steps
      └─ Returns: Via HTTP
      ↓
  WorkflowReadinessService (frontend, upper in flow)

PATTERN: Frontend service calls backend service calls database,
returns to frontend service, which caches and fires events to
components that initiated the request.

Event Chain:
  StatusChanged event (from StatusService)
    ↓
  WorkflowReadinessService.OnReadinessChanged()
    ↓
  Recomputes readiness
    ↓
  But StatusService status was already cached during GetReadinessAsync()

This creates implicit circular event flow, not a code-level cycle,
but a runtime state synchronization problem.

Circular Dependency Issues

┌───────────────────┬──────────────────────────────────────────────────────────────────────────────────────────┬───────────────────────────────────────┐
│       Cycle       │                                           Path                                           │                Problem                │
├───────────────────┼──────────────────────────────────────────────────────────────────────────────────────────┼───────────────────────────────────────┤
│ Event-driven sync │ StatusChanged → ReadinessChanged → StatusInvalidated                                     │ Unclear ordering; can miss updates    │
├───────────────────┼──────────────────────────────────────────────────────────────────────────────────────────┼───────────────────────────────────────┤
│ Approval flow     │ Component → API → Backend → DB → Frontend service → Component                            │ Long path; no unified approval holder │
├───────────────────┼──────────────────────────────────────────────────────────────────────────────────────────┼───────────────────────────────────────┤
│ Readiness         │ Component owns readiness cache, service owns readiness cache, backend computes readiness │ Triple ownership; consensus unclear   │
└───────────────────┴──────────────────────────────────────────────────────────────────────────────────────────┴───────────────────────────────────────┘

---
PART 8: SERVICES THAT SHOULD DISAPPEAR

Services to Eliminate

┌─────────────────────────────────┬─────────────────────────────────────────┬───────────────────────────────────────────────────────┬───────────────────────┐
│             Service             │                   Why                   │                          How                          │         Risk          │
├─────────────────────────────────┼─────────────────────────────────────────┼───────────────────────────────────────────────────────┼───────────────────────┤
│ WorkspaceArtifactStatusService  │ Duplicate of repository; only caches    │ Delete; move count computation to                     │ Low (no logic; pure   │
│                                 │ artifact count                          │ WorkspaceArtifactRepository (read-only property)      │ cache)                │
├─────────────────────────────────┼─────────────────────────────────────────┼───────────────────────────────────────────────────────┼───────────────────────┤
│ WorkspaceSessionService         │ Marked as superseded by                 │ Already not registered; mark obsolete, delete after   │ Low (already unused)  │
│ (legacy)                        │ WorkspaceArtifactRepository             │ Phase 2                                               │                       │
├─────────────────────────────────┼─────────────────────────────────────────┼───────────────────────────────────────────────────────┼───────────────────────┤
│ IWorkspaceSessionService        │ Redundant with                          │ Consolidate into single interface                     │ Low (bridge pattern;  │
│ interface                       │ IWorkspaceArtifactRepository            │                                                       │ easy to update)       │
└─────────────────────────────────┴─────────────────────────────────────────┴───────────────────────────────────────────────────────┴───────────────────────┘

Services to Refactor (Not Eliminate)

┌────────────────────────────────┬───────────────────────────────┬────────────────────────────────────────────────────────┬─────────────────────────────────┐
│            Service             │         Current Role          │                      Target Role                       │             Change              │
├────────────────────────────────┼───────────────────────────────┼────────────────────────────────────────────────────────┼─────────────────────────────────┤
│ WorkflowReadinessService       │ Computes readiness; caches    │ Computes readiness; used via context facade; result    │ Major refactoring (owns         │
│                                │ result; fires events          │ not cached locally                                     │ computation, not caching)       │
├────────────────────────────────┼───────────────────────────────┼────────────────────────────────────────────────────────┼─────────────────────────────────┤
│ RecommendedWorkflowService     │ Computes workflow steps       │ Defines workflow rules only; step computation moved to │ Major refactoring (move         │
│                                │ (backend)                     │  frontend WorkflowStateService                         │ computation to frontend)        │
├────────────────────────────────┼───────────────────────────────┼────────────────────────────────────────────────────────┼─────────────────────────────────┤
│ WorkspaceSessionRestoreService │ Owns scattered metadata; no   │ Part of WorkflowSessionContext facade                  │ Consolidation (component of     │
│                                │ unified object                │                                                        │ larger facade)                  │
├────────────────────────────────┼───────────────────────────────┼────────────────────────────────────────────────────────┼─────────────────────────────────┤
│ WorkspacePersistenceService    │ Buggy save (doesn't persist   │ Clean CRUD only; fix SaveCurrentAsync to persist       │ Fix + move to true persistence  │
│                                │ artifacts)                    │                                                        │ layer                           │
└────────────────────────────────┴───────────────────────────────┴────────────────────────────────────────────────────────┴─────────────────────────────────┘

---
PART 9: SERVICES THAT SHOULD BECOME COORDINATORS ONLY

Services to Convert to Coordinators

┌───────────────────────────┬────────────────────────────────┬───────────────────────┬─────────────────────────────────────────────────────────────────────┐
│          Service          │            Current             │        Target         │                                Scope                                │
├───────────────────────────┼────────────────────────────────┼───────────────────────┼─────────────────────────────────────────────────────────────────────┤
│ WorkflowSessionContext    │                                │                       │ Composes: SessionService, ArtifactRepository, ApprovalService,      │
│ (NEW)                     │ (doesn't exist)                │ Facade/Coordinator    │ WorkflowStateService, ReviewContext, ReadinessService. Single       │
│                           │                                │                       │ injection point for UI.                                             │
├───────────────────────────┼────────────────────────────────┼───────────────────────┼─────────────────────────────────────────────────────────────────────┤
│ WorkflowStateService      │ Computation split across       │ Coordinator +         │ Owns: Step status logic. Reads: Artifacts + Approvals. Outputs:     │
│ (NEW)                     │ backend                        │ Computation           │ WorkflowSteps.                                                      │
├───────────────────────────┼────────────────────────────────┼───────────────────────┼─────────────────────────────────────────────────────────────────────┤
│ ApprovalService (NEW)     │ Scattered in                   │ Coordinator +         │ Owns: Approval state holder. Reads: WorkspaceReviewProgress from    │
│                           │ RecommendedWorkflowService     │ Persistence           │ DB. Updates: Persists changes.                                      │
├───────────────────────────┼────────────────────────────────┼───────────────────────┼─────────────────────────────────────────────────────────────────────┤
│ DeliveryReadinessService  │ Gate evaluation for delivery   │ Optional enhancement  │ Remains independent; available to workflow context if ReviewContext │
│                           │ readiness                      │ provider              │  built successfully.                                                │
└───────────────────────────┴────────────────────────────────┴───────────────────────┴─────────────────────────────────────────────────────────────────────┘

---
PART 10: RECOMMENDED MIGRATION PHASES (LOWEST RISK ORDER)

Phase Ordering Rationale

The phases are ordered to:
1. Fix bugs first (SaveCurrentAsync, RestoreWorkspaceAsync) - these are data-losing bugs
2. Build foundation services (ApprovalService, WorkflowStateService) - these are new, non-breaking
3. Create facade (WorkflowSessionContext) - brings everything together
4. Remove duplicates (StatusService cache) - only after facade is in place
5. Verify (end-to-end integration tests) - ensure all ownership rules followed

---
Phase 1: Fix Data Persistence Bugs

RISK LEVEL: MEDIUM (touches save/load paths, critical data operation)

Goal: Ensure artifacts persist correctly and restore correctly.

Services Modified:
- WorkspacePersistenceService.SaveCurrentAsync() → Must call SaveArtifactsAsync
- WorkspacePersistenceService.SaveArtifactsAsync() → New method
- WorkspaceSessionRestoreService.RestoreWorkspaceAsync() → Handle empty artifact list gracefully

What Changes:
1. SaveCurrentAsync persists artifact content (currently broken)
2. RestoreWorkspaceAsync doesn't clear artifacts unnecessarily
3. LoadAsync properly includes artifact collection

What Doesn't Change:
- Architecture (same layer structure)
- Interfaces
- UI behavior
- Dependencies

Verification:
- Save workspace → load workspace → verify artifacts present
- Open workspace → artifacts restored from database
- No data loss

Rollback: Revert SaveArtifactsAsync call; restore old Clear() logic

Duration: 1-2 days

---
Phase 2: Create ApprovalService (New)

RISK LEVEL: LOW (new service, non-breaking)

Goal: Extract approval state management into dedicated service.

New Service:
- ApprovalService: Owns WorkspaceReviewProgress state

What Changes:
1. Create ApprovalService
2. Register in DI
3. Load approval state on workspace restore
4. ApprovalService caches state in memory

What Doesn't Change:
- WorkflowReadinessService logic
- RecommendedWorkflowService persistence
- UI behavior
- Database schema

How RecommendedWorkflowService Changes:
- Calls ApprovalService.UpdateApprovalAsync() instead of direct persistence
- ApprovalService persists to DB + invalidates cache

Verification:
- Approvals persisted to database
- Workflow state reflects approvals
- Readiness updates on approval

Rollback: Delete ApprovalService; revert RecommendedWorkflowService to direct DB persistence

Duration: 2-3 days

---
Phase 3: Create WorkflowStateService (New)

RISK LEVEL: LOW (new service, computation only)

Goal: Centralize workflow step status computation (move from backend to frontend, cache it).

New Service:
- WorkflowStateService: Computes workflow steps

What Changes:
1. Create WorkflowStateService (frontend)
2. Implement step status computation logic (move from RecommendedWorkflowService)
3. Cache computed steps in memory
4. Invalidate cache when artifacts or approvals change

What Doesn't Change:
- RecommendedWorkflowService definitions (artifact dependencies, approval dependencies)
- UI behavior
- Database operations
- Persistence

How Services Interact:
- WorkflowStateService reads: WorkspaceArtifactRepository, ApprovalService
- WorkflowStateService outputs: WorkflowSteps[]
- UI reads: WorkflowStateService (via context, not yet)

Verification:
- Step status computed correctly
- Cache invalidated on artifact/approval changes
- Steps match backend expectations

Rollback: Delete WorkflowStateService; keep using RecommendedWorkflowService for step computation

Duration: 2-3 days

---
Phase 4: Create WorkflowSessionContext (New Facade)

RISK LEVEL: MEDIUM (introduces new injection point; must coordinate old and new services)

Goal: Create unified session object composing all workflow state.

New Service:
- WorkflowSessionContext: Facade/Coordinator

What Changes:
1. Create WorkflowSessionContext class
2. Compose: WorkspaceSessionService, WorkspaceArtifactRepository, ApprovalService, WorkflowStateService, ReviewContext, WorkflowReadinessService
3. Provide single GetCurrentSessionAsync() method
4. UI injects WorkflowSessionContext instead of individual services

What Doesn't Change:
- Individual services (they keep working)
- Database operations
- Readiness computation logic
- ReviewContext building

How Composition Works:
WorkflowSessionContext
  ├─ WorkspaceSessionService (reads CurrentWorkspaceId)
  ├─ WorkspaceArtifactRepository (reads Artifacts)
  ├─ ApprovalService (reads ApprovalState)
  ├─ WorkflowStateService (reads computed Steps)
  ├─ ReviewContext (optional, if built)
  └─ WorkflowReadinessService (reads computed Readiness)

Verification:
- Context created successfully
- All components initialized
- Old injection paths still work (both old and new coexist)
- UI can inject either new context or old services

Rollback: Keep old services; delete context facade

Duration: 2-3 days

---
Phase 5: Migrate UI to Use WorkflowSessionContext

RISK LEVEL: MEDIUM (changes UI dependencies; must verify all readiness logic)

Goal: Unify UI access to session state.

What Changes:
1. RecommendedWorkflow.razor: Change injections
  - From: IWorkflowReadinessService, IWorkspaceArtifactRepository, etc.
  - To: IWorkflowSessionContext
2. WorkspaceManager.razor: Similar changes
3. All state access goes through context

What Doesn't Change:
- Readiness computation logic
- Event handling
- Database operations
- Artifact loading/saving

Verification:
- UI renders correctly
- Readiness displays correctly
- Artifact counts match
- Workflow steps show correct status
- Old injections no longer used

Rollback: Revert to old injections; delete context facade

Duration: 2-3 days

---
Phase 6: Remove Duplicate Artifact Count Cache

RISK LEVEL: LOW (only removing duplicate; computation logic stays same)

Goal: Eliminate WorkspaceArtifactStatusService duplicate caching.

What Changes:
1. WorkspaceArtifactStatusService: No longer caches artifact count
2. GetStatus() returns count computed from repository directly (O(1) operation)
3. Remove _cachedStatus field
4. Remove StatusChanged event (no longer needed for invalidation)

What Doesn't Change:
- Artifact count logic (same computation)
- Repository behavior
- Readiness logic (just reads from service instead of cache)
- Database operations

Verification:
- Artifact counts still correct
- No cache staleness issues
- Performance unchanged (repository query is O(1))
- No race conditions (no cache to invalidate)

Rollback: Restore _cachedStatus caching and StatusChanged event

Duration: 1 day

---
Phase 7: Build ReviewContext in Session Initialization

RISK LEVEL: MEDIUM (adds parsing; could slow restore if artifacts are malformed)

Goal: Ensure ReviewContext is built and available when session loads.

What Changes:
1. WorkflowSessionContext initialization calls ReviewContextFactory
2. TRY { Build ReviewContext } CATCH { set to null, log warning }
3. Graceful degradation if parsing fails

What Doesn't Change:
- Workflow computation (works with or without ReviewContext)
- Artifact storage
- Approval state
- Persistence

Verification:
- ReviewContext built successfully for valid artifacts
- Graceful handling of malformed artifacts
- Workflow continues without ReviewContext
- Readiness available (with or without semantic analysis)

Rollback: Remove ReviewContext building; leave null

Duration: 1-2 days

---
Phase 8: Deprecate and Remove Old Patterns

RISK LEVEL: LOW (removing unused code, not changing behavior)

Goal: Clean up legacy code patterns.

What Changes:
1. Mark WorkspaceSessionService (legacy) as Obsolete
2. Mark WorkspaceArtifactStatusService for deletion
3. Remove direct WorkflowReadinessService injections from components
4. Remove old event subscriptions (StatusChanged, ReviewContextRebuildNeeded)

What Doesn't Change:
- Readiness computation
- Artifact behavior
- Workflow logic
- Persistence

Verification:
- No compilation warnings
- All old code paths removed
- All tests pass
- UI behaves identically

Rollback: Restore old code; remove Obsolete attributes

Duration: 1-2 days

---
Phase 9: Comprehensive Integration Testing

RISK LEVEL: LOW (testing only; no code changes to production)

Goal: Verify entire architecture adheres to blueprint.

What Changes:
- Add integration tests covering all state transitions
- Test ownership rules
- Test circular dependency absence

What Doesn't Change:
- Production code
- Architecture
- Behavior

Verification:
- All integration tests pass
- State ownership verified
- No circular dependencies
- No cache staleness issues

Rollback: Delete tests

Duration: 3-5 days

---
Phase 10: Documentation & Code Cleanup

RISK LEVEL: NONE (documentation only)

Goal: Document final architecture.

What Changes:
- Update ARCHITECTURE.md with new design
- Add code comments explaining ownership
- Document migration decisions

What Doesn't Change:
- Code behavior
- Tests
- Architecture

Rollback: N/A (docs only)

Duration: 1-2 days

---
PART 11: PHASE EXECUTION SUMMARY TABLE

┌──────────────────────────────────┬──────────┬────────┬────────────┬───────────┬───────────┬───────────────────────────────────────┐
│              Phase               │ Duration │  Risk  │ Complexity │ Breaking? │ Rollback? │           Order Dependency            │
├──────────────────────────────────┼──────────┼────────┼────────────┼───────────┼───────────┼───────────────────────────────────────┤
│ 1: Fix persistence bugs          │ 1-2d     │ MEDIUM │ High       │ No        │ Yes       │ Must be first (unblocks other phases) │
├──────────────────────────────────┼──────────┼────────┼────────────┼───────────┼───────────┼───────────────────────────────────────┤
│ 2: Create ApprovalService        │ 2-3d     │ LOW    │ Medium     │ No        │ Yes       │ After Phase 1                         │
├──────────────────────────────────┼──────────┼────────┼────────────┼───────────┼───────────┼───────────────────────────────────────┤
│ 3: Create WorkflowStateService   │ 2-3d     │ LOW    │ Medium     │ No        │ Yes       │ After Phase 2 (needs approvals)       │
├──────────────────────────────────┼──────────┼────────┼────────────┼───────────┼───────────┼───────────────────────────────────────┤
│ 4: Create WorkflowSessionContext │ 2-3d     │ MEDIUM │ High       │ No        │ Yes       │ After Phase 3 (needs all components)  │
├──────────────────────────────────┼──────────┼────────┼────────────┼───────────┼───────────┼───────────────────────────────────────┤
│ 5: Migrate UI to context         │ 2-3d     │ MEDIUM │ Medium     │ No        │ Yes       │ After Phase 4                         │
├──────────────────────────────────┼──────────┼────────┼────────────┼───────────┼───────────┼───────────────────────────────────────┤
│ 6: Remove artifact cache         │ 1d       │ LOW    │ Low        │ No        │ Yes       │ After Phase 5 (remove when safe)      │
├──────────────────────────────────┼──────────┼────────┼────────────┼───────────┼───────────┼───────────────────────────────────────┤
│ 7: Build ReviewContext           │ 1-2d     │ MEDIUM │ Medium     │ No        │ Yes       │ Any time after Phase 4 (optional)     │
├──────────────────────────────────┼──────────┼────────┼────────────┼───────────┼───────────┼───────────────────────────────────────┤
│ 8: Deprecate old patterns        │ 1-2d     │ LOW    │ Low        │ No        │ Yes       │ After Phase 7 (last cleanup)          │
├──────────────────────────────────┼──────────┼────────┼────────────┼───────────┼───────────┼───────────────────────────────────────┤
│ 9: Integration testing           │ 3-5d     │ LOW    │ Medium     │ No        │ N/A       │ After Phase 8                         │
├──────────────────────────────────┼──────────┼────────┼────────────┼───────────┼───────────┼───────────────────────────────────────┤
│ 10: Documentation                │ 1-2d     │ NONE   │ Low        │ No        │ N/A       │ Final (can overlap with testing)      │
└──────────────────────────────────┴──────────┴────────┴────────────┴───────────┴───────────┴───────────────────────────────────────┘

Total Duration: 16-28 days (assuming 1 developer)
Parallelizable: Phases 2-3 (both new services, non-blocking)
Critical Path: 1 → 4 → 5 → 6 → 8

---
PART 12: SUMMARY: CURRENT STATE vs TARGET STATE

Current State (Broken)

Multiple sources of truth
  ├─ Artifact count in StatusService + Repository
  ├─ Readiness in ReadinessService + Component
  ├─ Approval state scattered (DB + no service)
  ├─ Workspace metadata scattered (RestoreService + Component + Repository)
  └─ ReviewContext: Never built (event fires, no consumer)

Unclear ownership
  ├─ UI doesn't know which service owns what
  ├─ Components cache business state
  ├─ Backend computes what frontend also computes
  └─ Approval flow: No dedicated service

Data loss bugs
  ├─ SaveCurrentAsync: Artifacts not persisted
  └─ RestoreWorkspaceAsync: Artifacts cleared, not restored

Inefficiencies
  ├─ Workflow steps computed every call (no cache)
  ├─ Approvals queried repeatedly (no cache)
  └─ Artifact status cached unnecessarily (but duplicated)

Event-driven chaos
  ├─ StatusChanged → ReadinessChanged → ??? (unclear flow)
  ├─ ReviewContextRebuildNeeded event fires but ignored
  └─ No clear synchronization semantics

RESULT: Cascading failures where save→load loses data,
        approvals don't update readiness, artifact counts diverge

Target State (Clean)

Single source of truth
  ├─ Artifact count: WorkspaceArtifactRepository only
  ├─ Readiness: WorkflowReadinessService only
  ├─ Approval state: ApprovalService only
  ├─ Workspace metadata: WorkspaceSessionService only
  ├─ Workflow steps: WorkflowStateService only
  └─ ReviewContext: ReviewContextFactory → available to all

Clear ownership
  ├─ UI injects: WorkflowSessionContext (single facade)
  ├─ Context composes: SessionService, Repository, ApprovalService, StateService, ReviewContext, ReadinessService
  ├─ Each component owns exactly one state domain
  └─ Unidirectional data flow (no cycles)

Data integrity
  ├─ SaveCurrentAsync: Persists artifacts correctly
  └─ RestoreWorkspaceAsync: Restores artifacts correctly

Efficient caching
  ├─ Workflow steps cached in WorkflowStateService (computed once)
  ├─ Approvals cached in ApprovalService (loaded once)
  ├─ Artifact status computed on demand (no cache; O(1) query)
  └─ ReviewContext built once on session init

Explicit coordination
  ├─ WorkflowSessionContext triggers all computations
  ├─ Artifacts → ReviewContext → WorkflowState → Readiness (deterministic order)
  └─ Clear invalidation rules (when each cache invalidates)

RESULT: Save→Load works correctly, approvals update immediately,
        artifact counts consistent, semantic analysis available

---
CONCLUSION: ARCHITECTURE ANALYSIS COMPLETE

Current Implementation: Violates 10+ ownership rules; has 6+ duplicate state locations; no clear unidirectional flow

Target Implementation: Single owner per state; unidirectional dependencies; unified session facade; ReviewContext pure semantic analysis

Migration Risk: MEDIUM (10 phases, 16-28 days, careful rollback at each phase)

Technical Debt: Saved by eliminating duplicate caching, scattered metadata, and unclear ownership

Recommended Next Step: Executive review of Phase 1 (fix persistence bugs) to unblock all subsequent work
