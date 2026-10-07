# Current workspace: one authority for workspace, project and artifact presence

Every page that states whether a workspace, a project or an artifact exists reads one read model:
`ICurrentWorkspaceProjection` (`Services/CurrentWorkspaceProjection.cs`). Pages add their own domain status (analysis,
review, readiness, applicability) on top of it. None of them decides presence on its own.

```
Sample Project selection ─┐            (ingestion: sets the project identity)
Explorer import / paste ──┼─▶ WorkspaceArtifactRepository ─▶ IArtifactExplorerContext ─▶ ICurrentWorkspaceProjection
Resume / restore ─────────┘   (project slug, revisions,       (artifacts by role: Sample     (CurrentWorkspaceSnapshot)
                               selections, authority)          Project discovery + scoped          │
                                                               imported revisions)                 ├─▶ Dashboard
Workspace persistence ── save state only ───────────────────────────────────────────────────────▶  ├─▶ Recommended Workflow
                                                                                                    ├─▶ Sidebar applicability
                                                                                                    └─▶ (Explorers read the same context)
```

## Owners

| Question | Owner |
|---|---|
| Is a workspace loaded? Which project? Which artifact roles exist, how many artifacts, which is selected, its authority? | `ICurrentWorkspaceProjection` → `CurrentWorkspaceSnapshot` |
| Project identity (Sample Project slug) | `WorkspaceArtifactRepository.CurrentProject` |
| Imported artifacts, revisions, explorer selections, authority | `WorkspaceArtifactRepository.SddLifecycle` |
| A selected Sample Project's documents by role | `ISampleProjectArtifactDiscovery`, read through `IArtifactExplorerContext` |
| Saved / auto-saved / not saved, last saved, saved workspace id and name | backend workspace persistence (`GET api/workspace-persistence/current-state`) and the resume metadata of `IWorkspaceSessionRestoreService` |
| Review inputs for a run (what was reviewed) | `ReviewContext` (built per use) |
| Analysis results shown on the Dashboard | the session's report snapshots (`IDashboardSnapshotService`, review sessions) |

`CurrentWorkspaceProjection` stores no truth. It caches one snapshot and recomputes it when `IArtifactExplorerContext.Changed`
fires. That event relays every repository change: an artifact imported or selected, authority changed, a project selected or
switched, a workspace resumed or cleared, and a local data reset. Callers that change save state call `Invalidate()`.
There is no polling. Concurrent readers share one computation, and a snapshot computed across a change is never cached.

## Definitions

- **Workspace loaded**: a project is selected, or at least one artifact of a workflow role is in the workspace, or a saved
  workspace was resumed. An explicit empty workspace (a project without documents, or a resumed workspace without
  artifacts) is loaded with zero roles.
- **Project loaded**: the workspace has a project identity (a Sample Project slug). A manual workspace has none and shows
  *Project: Not assigned*. It is still a workspace.
- **Artifact role available**: at least one artifact of the role is in the current workspace scope. Roles are Constitution,
  Specification, Plan, Tasks and Data Model, and each is optional. `AvailableRoleCount` counts roles. `ArtifactCount` counts
  artifacts. Several Specifications are one available role.
- **Selection**: which artifact of a role reviews read. Set explicitly, or by single authority, or because it is the only
  artifact. `SelectionRequired` means several artifacts and no choice. Selection never changes availability.
- **Authority**: the selected artifact's repository authority (Approved, Baseline, …). It never changes availability.
- **Save state**: `NotSaved`, `AutoSaved`, `Saved`, `UnsavedChanges`. It never decides existence: an unsaved workspace is
  loaded. Backend save state and resume metadata are used only when they hold the same project slug as the session.
  Identity is compared by slug, never by display name.
- **Error**: the selected project's documents could not be read. The workspace exists, its role availability is
  *Unknown*, and pages say *Unable to load workspace*. They never say *No workspace*.
- **Loading**: no snapshot yet. Pages say *Loading workspace…* and do not compute recommendations.

## What the other layers are not

- **ReviewContext** describes the inputs of a review run. An empty ReviewContext does not mean there is no workspace.
- **Sample Projects** is an ingestion path. After selection, the repository and discovery are the truth. The Sample
  Projects page does not decide what other pages show.
- **Explorer selection** chooses which artifact of a role is shown. It is not presence.
- **`IWorkspaceArtifactStatusService`** reports only manual-workspace session copies (`Workspace.Set`). It is a diagnostic
  of that layer (System Settings), not presence.

## Consumers

- **Dashboard.** The *Current Workspace* panel, role badges, artifact cards, the *Governance* KPI (“N / 4” governance roles
  available) and the project phase all come from the snapshot. Artifact cards show availability (*Available*, *Missing*,
  *Availability unknown*), selection (*Selection required*) and analysis (*Analyzed*, *Not analyzed*) as separate badges.
  Analysis counts only for an available role. The quality aggregate averages assessed areas only, as before.
- **Recommended Workflow.** `WorkflowReadinessService` reads the snapshot, passes role availability to the backend step
  builder, and derives the next action. With no workspace it recommends *Load project artifacts*. A loaded workspace without
  artifacts gets *Add project artifacts*. A role with several artifacts and no choice gets *Choose the … to review*.
  Otherwise the next action is the first open review step. Release readiness is the approved share of required review
  steps, and “—” until a required step has a review decision. *Manual Review* says *None applicable* when no step requires
  review.
- **Sidebar applicability.** `ProjectApplicabilityState` takes `HasRequirements` and `HasDocumentation` from the snapshot.
  NavMenu refreshes on `Changed`.

## Known limits

- Requirements Traceability and Quality Review still read only the selected Sample Project's documents. In a manual
  workspace they say *No Sample Project selected*. That is true, but they do not read imported artifacts yet.
- Auto-save raises no completion event. The save state shown may lag until the next workspace change or explicit save.
  Existence never lags.
- A restored generic workspace whose project name is not a Sample Project slug shows the project as unavailable, and its
  role availability as *Unknown*.
