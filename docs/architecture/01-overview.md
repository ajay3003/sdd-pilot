# BirkNext Architecture Overview

## Executive Summary

BirkNext is a Blazor WASM QA analysis tool that manages workspace artifacts (Constitution, Specification, Plan, Tasks, DataModel) and provides unified semantic analysis through a single ReviewContext.

**Core Principle:** ReviewContext is derived, runtime-only state built from workspace artifacts. Pages never cache or recreate it.

---

## System Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                      Frontend (Blazor WASM)                 │
│  ┌──────────────────────────────────────────────────────┐   │
│  │  UI Pages & Components                               │   │
│  │  ├─ Recommended Workflow                             │   │
│  │  ├─ Artifact Traceability (analysis tool)            │   │
│  │  ├─ Explorer Pages (Constitution, Plan, etc.)        │   │
│  │  └─ Quality Review Dashboard                         │   │
│  └──────────────────────────────────────────────────────┘   │
│           ↓ Subscribe/Inject                                 │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ Core Services (Singleton)                            │   │
│  │                                                      │   │
│  │  ▶ WorkspaceArtifactRepository                      │   │
│  │    └─ In-memory artifact storage                    │   │
│  │                                                      │   │
│  │  ▶ IWorkspaceUpdateCoordinator                      │   │
│  │    ├─ Batches mutations                             │   │
│  │    └─ Fires ArtifactsChanged (once per batch)       │   │
│  │                                                      │   │
│  │  ▶ ReviewContextProvider ★ (SOLE OWNER)            │   │
│  │    ├─ Builds ReviewContext from artifacts           │   │
│  │    ├─ Fires ReviewContextChanged after rebuild      │   │
│  │    └─ GetCurrent() API for pages                    │   │
│  │                                                      │   │
│  │  ▶ WorkflowReadinessService                         │   │
│  │    └─ Tracks workflow readiness state               │   │
│  │                                                      │   │
│  │  ▶ WorkspaceAutoSaveService                         │   │
│  │    └─ Auto-save with debounce timer                │   │
│  │                                                      │   │
│  │  ▶ WorkspaceSessionRestoreService                   │   │
│  │    └─ Restores saved workspaces + triggers rebuild  │   │
│  └──────────────────────────────────────────────────────┘   │
│           ↓ HTTP                                             │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ HTTP Client Services                                │   │
│  │ ├─ WorkspacePersistenceApiService                   │   │
│  │ ├─ RecommendedWorkflowApiService                    │   │
│  │ └─ Other API clients                                │   │
│  └──────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
                          ↓ HTTP REST
┌─────────────────────────────────────────────────────────────┐
│                    Backend (ASP.NET Core)                    │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ Controllers                                          │   │
│  │ ├─ WorkspacePersistenceController                   │   │
│  │ ├─ RecommendedWorkflowController                    │   │
│  │ └─ Other API endpoints                              │   │
│  └──────────────────────────────────────────────────────┘   │
│           ↓                                                   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ Services                                             │   │
│  │ ├─ WorkspacePersistenceService                      │   │
│  │ ├─ RecommendedWorkflowService                       │   │
│  │ └─ Other business logic                             │   │
│  └──────────────────────────────────────────────────────┘   │
│           ↓                                                   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ Entity Framework Core                                │   │
│  │ └─ AppDbContext                                      │   │
│  └──────────────────────────────────────────────────────┘   │
│           ↓                                                   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ Database (SQL Server)                                │   │
│  │ ├─ Workspaces table                                 │   │
│  │ ├─ WorkspaceArtifacts table                         │   │
│  │ └─ WorkflowApprovals table                          │   │
│  └──────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

---

## Key Concepts

### 1. WorkspaceArtifactRepository (Frontend In-Memory Store)

**Purpose:** Single in-memory store for workspace artifacts during a browser session.

**Artifacts:**
- Constitution (markdown)
- Specification (markdown)
- Plan (markdown)
- Tasks (markdown)
- DataModel (markdown)

**Key Methods:**
```csharp
Has(WorkspaceArtifactType type) → bool
Get(WorkspaceArtifactType type) → WorkspaceArtifact?
GetAllArtifacts() → IEnumerable<(WorkspaceArtifactType, WorkspaceArtifact)>
Set(WorkspaceArtifactType type, string text, string? fileName = null, ...) → void
Clear(WorkspaceArtifactType type) → void
```

**Lifetime:** Lives for the entire browser session. Persists across page navigation.

---

### 2. ReviewContext (Semantic Analysis State)

**Purpose:** Derived, runtime-only state representing unified semantic analysis across all artifacts.

**Ownership:** ReviewContextProvider is the SOLE owner.

**Properties:**
```csharp
ConstitutionSemanticModel Constitution { get; }
SpecificationSemanticModel Specification { get; }
PlanSemanticModel Plan { get; }
TaskSemanticModel Tasks { get; }
DataModelSemanticModel DataModel { get; }
ReviewCoverageSummary Coverage { get; }
```

**Key Query Methods:**
```csharp
GetRequirements() → IReadOnlyList<SemanticRequirement>
GetTasks() → IReadOnlyList<TaskItem>
GetDataEntities() → IReadOnlyList<SemanticDataEntity>
GetRequirementsWithTests() → IEnumerable<SemanticRequirement>
GetRequirementsWithoutTests() → IEnumerable<SemanticRequirement>
HasTestCoverage(string requirementId) → bool
```

**Creation:** Built only by ReviewContextProvider via ReviewContextFactory.Create()

**Important Rule:** Pages NEVER build ReviewContext. Pages NEVER cache ReviewContext. Pages request current context via `reviewContextProvider.GetCurrent()`.

---

### 3. IWorkspaceUpdateCoordinator (Event Synchronization)

**Purpose:** Coordinates workspace mutations and publishes single ArtifactsChanged event per logical batch.

**Key Methods:**
```csharp
void BeginUpdate()          // Start batch (depth++)
void EndUpdate()            // End batch (depth--)
void NotifyMutation()       // Signal that mutations occurred in this batch
event EventHandler? ArtifactsChanged  // Fires at EndUpdate if mutations occurred
```

**Guarantee:** ArtifactsChanged fires exactly once per logical batch, not once per artifact mutation.

**Use Pattern:**
```csharp
coordinator.BeginUpdate();
try 
{
    repository.Set(WorkspaceArtifactType.Constitution, text);
    repository.Set(WorkspaceArtifactType.Plan, text);
    coordinator.NotifyMutation();  // Once for both mutations
}
finally 
{
    coordinator.EndUpdate();  // Fires ArtifactsChanged here
}
```

---

### 4. ReviewContextProvider (The Sole ReviewContext Owner)

**Purpose:** Runtime owner of ReviewContext. Builds and maintains it as workspace artifacts change.

**Key Methods:**
```csharp
ReviewContext? GetCurrent()        // Get current ReviewContext (or null if workspace incomplete)
Task RebuildAsync()                // Rebuild ReviewContext from current artifacts
event EventHandler? ReviewContextChanged  // Fires after rebuild completes
```

**Lifecycle:**
1. Subscribes to WorkspaceUpdateCoordinator.ArtifactsChanged
2. When artifacts change, OnArtifactsChanged() fires
3. Calls RebuildAsync() to rebuild from current artifacts
4. Fires ReviewContextChanged when rebuild completes
5. Pages call GetCurrent() to access current ReviewContext

**Dependencies:**
- IWorkspaceArtifactRepository (read artifacts)
- IWorkspaceUpdateCoordinator (subscribe to changes)
- Analysis services: IConstitutionAnalysisService, IPlanAnalysisService, etc.

---

## Data Flow: Load Sample Project

```
SampleProjects.razor LoadArtifacts
  ↓
1. coordinator.BeginUpdate()
  ↓
2. repository.Set(Constitution, ...)
   repository.Set(Specification, ...)
   repository.Set(Plan, ...)
   repository.Set(Tasks, ...)
   repository.Set(DataModel, ...)
  ↓
3. coordinator.NotifyMutation()
  ↓
4. coordinator.EndUpdate()
  ↓
5. coordinator.ArtifactsChanged event fires
  ↓
6. ReviewContextProvider.OnArtifactsChanged()
  ↓
7. ReviewContextProvider.RebuildAsync()
   - Get artifacts from repository
   - Parse via analysis services
   - Build semantic models
   - ReviewContextFactory.Create()
   - Set _current = newReviewContext
  ↓
8. ReviewContextProvider.ReviewContextChanged fires
  ↓
9. Pages can now call reviewContextProvider.GetCurrent()
```

---

## Data Flow: Open Saved Workspace

```
WorkspaceManager SelectWorkspace
  ↓
1. Load SavedWorkspaceDto from backend
  ↓
2. WorkspaceSessionRestoreService.RestoreWorkspaceAsync(workspace)
  ↓
3. Clear all artifacts
  ↓
4. Restore artifacts: repository.Set() for each artifact
  ↓
5. ReviewContextProvider.RebuildAsync()
  ↓
6. ReviewContextProvider.ReviewContextChanged fires
  ↓
7. Recommended Workflow page subscribes, updates display
```

---

## Architectural Rules

### Rule 1: ReviewContext Ownership
- ReviewContextProvider is the ONLY runtime owner of ReviewContext
- No page creates ReviewContext
- No page caches ReviewContext
- Pages request via `provider.GetCurrent()`

### Rule 2: No Direct Markdown Parsing
- Pages do not parse markdown to rebuild semantic state
- Parsing happens only in:
  - ReviewContextProvider (for workspace context)
  - Isolated analysis tools (ArtifactTraceability, TaskToSpecAlignment)
  - Explorer pages (for single-artifact display)
  - Analysis utilities (utility services that take parsed docs as input)

### Rule 3: Single Event Pipeline
```
ArtifactsChanged (from WorkspaceUpdateCoordinator)
  ↓
ReviewContextProvider.RebuildAsync()
  ↓
ReviewContextChanged (available to subscribers)
```

### Rule 4: No Page Lifecycle Ownership
- Auto-save is workspace-level infrastructure (not page-owned)
- ReviewContext rebuilds happen at coordinator level (not page-triggered)
- Pages react to changes, not orchestrate them

### Rule 5: Service Layering
- Pages inject services, not other pages
- Services don't create ReviewContext (except ReviewContextProvider)
- Utility services accept ReviewContext as parameter or build temporary one for analysis

---

## Extension Points

See [07-extension-guide.md](07-extension-guide.md) for guidance on:
- Adding new artifact types
- Adding new analysis services
- Consuming ReviewContext in new pages
- Extending the workflow system

---

## Related Documents

- [02-runtime-flow.md](02-runtime-flow.md) - Detailed event flow diagrams
- [03-reviewcontext.md](03-reviewcontext.md) - ReviewContext design and implementation
- [04-workspace.md](04-workspace.md) - Workspace persistence strategy
- [05-workflow.md](05-workflow.md) - Workflow approval system
- [06-autosave.md](06-autosave.md) - Auto-save infrastructure
- [07-extension-guide.md](07-extension-guide.md) - How to extend the system
