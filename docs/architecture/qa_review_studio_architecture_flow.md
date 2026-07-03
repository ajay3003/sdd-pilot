# QA Review Studio – Architecture and Runtime Flow

## Purpose

QA Review Studio is an event-driven semantic workspace. Markdown artifacts are loaded into one workspace repository, workspace changes are batched through a coordinator, ReviewContext is rebuilt as derived runtime state, and pages/services consume the latest context read-only.

---

## High-Level Architecture

```mermaid
flowchart LR
    A[Workspace Artifacts\nconstitution.md\nspec.md\nplan.md\ntasks.md\ndata-model.md]

    B[WorkspaceArtifactRepository\nSingle source of truth\nfor loaded artifacts]

    C[IWorkspaceUpdateCoordinator\nBatches mutations\nBeginUpdate / NotifyMutation / EndUpdate]

    D[ArtifactsChanged Event]

    E[ReviewContextProvider\nSole runtime owner\nof ReviewContext]

    F[ReviewContext\nSemantic analysis state\nmodels, links, coverage, gaps]

    G[Consumers\nRecommended Workflow\nCompliance\nSpec Review\nTraceability\nImpact Analysis\nQA Auditor\nDelivery Readiness]

    H[Persistence Layer\nSavedWorkspace\nSavedWorkspaceArtifact\nApprovals / workflow state]

    I[AutoSaveService\nsubscribes to ArtifactsChanged\npersists artifacts + metadata]

    J[WorkflowReadinessService\nsubscribes to ArtifactsChanged\nrecomputes readiness]

    A --> B
    B --> C
    C --> D
    D --> E
    E --> F
    F --> G
    D --> I
    I --> H
    H --> B
    D --> J
    J --> G
```

---

## Core Ownership Rules

| Component | Owns | Does Not Own |
|---|---|---|
| `WorkspaceArtifactRepository` | Current in-memory workspace artifacts | Events, batching, ReviewContext, approvals |
| `IWorkspaceUpdateCoordinator` | Batched workspace mutation events | Artifact storage, persistence, approval state |
| `ReviewContextProvider` | Current runtime `ReviewContext` | Raw artifacts, workflow approvals, persistence |
| `ReviewContextFactory` | Creating `ReviewContext` from semantic models | Runtime lifecycle |
| `AutoSaveService` | Debounced persistence trigger | Artifact ownership, ReviewContext ownership |
| `WorkflowReadinessService` | Readiness calculation and workflow status | Artifact persistence, ReviewContext creation |
| Approval services/API | Reviewed/Approved/Needs Changes state | ReviewContext, artifact content |
| Consumers/pages | Read current state and display results | Rebuilding ReviewContext or reparsing markdown |

---

## Artifact Flow

```mermaid
flowchart TD
    A[User loads/imports/edits artifacts]
    B[Page/service calls WorkspaceArtifactRepository.Set]
    C[All artifact content stored in repository]
    D[Page/service calls IWorkspaceUpdateCoordinator.NotifyMutation]
    E[Coordinator publishes ArtifactsChanged once per logical update]

    A --> B --> C --> D --> E
```

For multi-artifact operations:

```csharp
Updates.BeginUpdate();
try
{
    Repository.Set(Constitution, content);
    Repository.Set(Specification, content);
    Repository.Set(Plan, content);
    Repository.Set(Tasks, content);
    Repository.Set(DataModel, content);

    Updates.NotifyMutation();
}
finally
{
    Updates.EndUpdate();
}
```

Expected result: **one logical workspace update = one `ArtifactsChanged` event**.

---

## ReviewContext Lifecycle

```mermaid
sequenceDiagram
    participant Repo as WorkspaceArtifactRepository
    participant Coord as IWorkspaceUpdateCoordinator
    participant Provider as ReviewContextProvider
    participant Factory as ReviewContextFactory
    participant Consumers as Pages/Services

    Repo->>Coord: NotifyMutation()
    Coord->>Provider: ArtifactsChanged
    Provider->>Repo: Read current artifacts
    Provider->>Provider: Build semantic models
    Provider->>Factory: Create ReviewContext
    Factory-->>Provider: ReviewContext
    Provider->>Consumers: ReviewContextChanged
    Consumers->>Provider: GetCurrent()
```

`ReviewContextProvider` is the **only runtime owner** of ReviewContext.

Consumers must use:

```csharp
var context = ReviewContextProvider.GetCurrent();
```

Consumers must not:

```csharp
ReviewContextFactory.Create(...);
BuildSemanticModel(...);
ParseMarkdown(...);
```

except in tests, isolated offline utilities, or intentionally standalone analysis tools.

---

## Auto-Save Flow

```mermaid
sequenceDiagram
    participant Coord as IWorkspaceUpdateCoordinator
    participant AutoSave as WorkspaceAutoSaveService
    participant Repo as WorkspaceArtifactRepository
    participant Api as WorkspacePersistenceApiService
    participant Backend as WorkspacePersistenceController
    participant Db as Database

    Coord->>AutoSave: ArtifactsChanged
    AutoSave->>AutoSave: Start/reset debounce timer
    AutoSave->>Repo: GetAllArtifacts()
    Repo-->>AutoSave: Current artifacts
    AutoSave->>Api: AutoSaveAsync(artifacts + metadata)
    Api->>Backend: POST /api/workspace-persistence/auto-save
    Backend->>Db: Save workspace + artifacts
    Db-->>Backend: SavedWorkspaceId
    Backend-->>Api: 200 OK
```

Important rule: AutoSave must include artifacts in the request body, using the same pattern as Save As.

---

## Restore Flow

```mermaid
sequenceDiagram
    participant UI as Workspace Manager
    participant Api as WorkspacePersistenceApiService
    participant Backend as WorkspacePersistenceController
    participant Db as Database
    participant Restore as WorkspaceSessionRestoreService
    participant Repo as WorkspaceArtifactRepository
    participant Provider as ReviewContextProvider

    UI->>Api: Load workspace
    Api->>Backend: GET /workspace-persistence/load/{id}
    Backend->>Db: Load workspace with artifacts
    Db-->>Backend: Workspace DTO + artifacts
    Backend-->>Api: Workspace DTO
    Api-->>Restore: RestoreWorkspaceAsync(dto)
    Restore->>Repo: Populate artifacts
    Restore->>Restore: Restore metadata / approvals / workflow state
    Restore->>Provider: RebuildAsync()
    Provider->>Repo: Read restored artifacts
    Provider->>Provider: Build ReviewContext
```

ReviewContext must be rebuilt **after** artifacts are restored, never before.

---

## Approval and Readiness Flow

```mermaid
flowchart TD
    A[User clicks Mark Reviewed / Approve / Needs Changes]
    B[RecommendedWorkflow API call]
    C[Backend updates WorkspaceReviewProgress]
    D[Frontend refreshes workflow readiness]
    E[WorkflowReadinessService recomputes]
    F[Recommended Workflow UI updates]

    A --> B --> C --> D --> E --> F
```

Approval state is separate from ReviewContext.

ReviewContext answers: **What does the artifact content mean?**

Approval state answers: **What did the user approve in this workspace?**

---

## Runtime Event Flow Summary

```mermaid
flowchart LR
    A[Artifact changed] --> B[NotifyMutation]
    B --> C[EndUpdate]
    C --> D[ArtifactsChanged]
    D --> E[AutoSaveService]
    D --> F[WorkflowReadinessService]
    D --> G[ReviewContextProvider]
    G --> H[ReviewContextChanged]
    H --> I[Consumers refresh]
    E --> J[Workspace persisted]
    F --> K[Readiness updated]
```

---

## Current Completed Architecture

- Workspace persistence works.
- AutoSave persists workspace with artifacts.
- Workspace Manager shows correct artifact count.
- Approve / Mark Reviewed / Needs Changes work.
- Workflow readiness updates.
- ReviewContext contract tests pass.
- ReviewContext lifecycle integration is complete.
- ReviewContextProvider is the sole runtime owner.
- Consumers are migrated or intentionally left as isolated analysis utilities.

---

## Architectural Guarantees

1. Markdown artifacts are the source input.
2. `WorkspaceArtifactRepository` is the single runtime artifact store.
3. `IWorkspaceUpdateCoordinator` emits one event per logical workspace update.
4. `ReviewContextProvider` owns runtime ReviewContext.
5. Consumers read ReviewContext; they do not build it.
6. AutoSave and approvals are event-driven and decoupled.
7. Persistence and restore preserve artifacts and approval state.
8. ReviewContext is derived state and can always be rebuilt from artifacts.

---

## Remaining Stabilization Items

- Remove temporary diagnostic logs.
- Verify no duplicate artifact/status caches remain.
- Fix remaining `spec.md` and `data-model.md` HTTP 400 issues if still present.
- Add final architecture audit documentation.
- Add end-to-end regression tests for:
  - Load sample project
  - Auto-save
  - Open workspace
  - Approve / review
  - Readiness update
  - ReviewContext rebuild

