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

- **Dashboard.** The *Current Workspace* panel, role badges, *Project Artifacts* cards and the project phase come from the
  snapshot. Artifact cards show availability (*Available*, *Missing*, *Availability unknown*), selection (*Selection
  required*), the review decision on the selected revision (from the workflow, see below) and analysis (*Analyzed*, *Not
  analyzed*) as separate badges. *Governance* is not an availability count: it is “—” / *Not assessed* until a review
  decision exists on a current artifact revision, then the approved document reviews of those that apply (“1 / 5”), with
  *needs changes* or *review stale* named. The quality aggregate averages assessed areas only, as before.
- **Recommended Workflow.** `WorkflowReadinessService` reads the snapshot, passes role availability and each role's selected
  revision (`ArtifactRevisions`) to the backend step builder, and derives the next action. With no document artifact it
  recommends *Load project artifacts*. A role with several artifacts and no choice gets *Choose the … to review*. Otherwise
  the next action is the first applicable review step that is open, reviewed but unapproved, needs changes or is stale.
  Release readiness is the approved share of the required review steps that apply, and “—” until one has a decision.

## Review decisions

A review decision (*Mark Reviewed*, *Approve*, *Needs Changes*) is about exact artifact revisions, not a role:

- The client sends, with every step build and decision, the selected artifact of each available role: its id
  (`sample:<path>` or `workspace:<file>`) and the fingerprint of its content (`ArtifactFingerprint`, SHA-256).
- The backend (`WorkflowArtifactBinding`) hashes the revisions a step reads (its required roles, plus optional ones that are
  present) and stores the decision in `workspace_review_progress` under that hash. A decision that names no revision is
  refused. One row per step and binding: a decision on an earlier revision stays as history.
- A step's decision is current only for the same hash. The same artifacts with other content → *Review stale* (the earlier
  decision is shown with its date); another artifact of the role → *Ready to review* (it never inherits the decision).
  Decisions recorded before revision binding name no revision and are always stale.
- States: *Ready to review* (available, not reviewed), *In review*, *Reviewed* (inspected, no approval implied), *Approved*,
  *Needs changes*, *Review stale*, *Blocked* (no artifact chosen, or an earlier step to approve first) and *N/A* (a role the
  step requires is absent: not counted, not recommended). `ArtifactReviewPresentation` is the one reading; the Dashboard
  and Recommended Workflow both use it.
- Approval is required to complete a step; *Reviewed* alone does not. *Needs changes* and *Review stale* keep the step
  recommended. Opening an explorer records nothing.
- *Manual Review* counts the required review steps that apply (approved / applicable), lists every step with whether it
  counts (*What Manual Review counts*), and names optional steps (the Data Model review) separately; they never block release.
- Decisions belong to the saved workspace (project): they survive reload, never cross to another project, and are removed
  with the workspace by Reset Local Data.
- **Sidebar applicability.** `ProjectApplicabilityState` takes `HasRequirements` and `HasDocumentation` from the snapshot.
  NavMenu refreshes on `Changed`.

## Requirements Traceability and Document Quality Review

Sample Projects are one source of artifacts, not the definition of a workspace. Requirements Traceability and Quality
Review resolve their inputs from the current workspace through the same artifact repository and role resolver used by the
Explorers. They work with either discovered Sample Project documents or imported artifacts, including a valid workspace
whose project is *Not assigned*. Research is discoverable as a role but is not consumed by the current traceability or
Document Quality Review packs.

Artifact availability, selection, and analysis/review state are separate. Multiple artifacts without a chosen or uniquely
authoritative artifact put that role in *Selection required*. Availability never means that traceability was recomputed
or a Document Quality Review was run. Opening either page only resolves inputs and displays existing results; analysis and review
remain explicit user actions. Traceability keeps its existing report state and is not automatically recomputed when
artifacts become available.

Document Quality Review selects each document pack independently. The roles in the table describe its minimum eligibility and the additional
optional context the engine receives. Missing unrelated roles do not disable a pack.

| Review Pack | Artifact roles consumed |
| --- | --- |
| QA Auditor | Eligible with Specification, Plan or Tasks; the engine also accepts Constitution as context. |
| Constitution Compliance | Constitution; Specification, Plan and Tasks are optional coverage context. |
| Data Model Quality | Data Model. |
| WCAG 2.2 — Documentation, OWASP Security Documentation Coverage, GDPR Documentation Coverage, ISO 25010 — Documented Quality Characteristics | Any of Constitution, Specification, Plan or Tasks enables the pack; all available text from those roles is evaluated together. Data Model is not passed to these packs. Results measure document coverage only, not conformance, source implementation, legal compliance or runtime behavior. |
| QA Readiness | Eligible with Specification or Tasks; Constitution and Plan are optional context. |
| Delivery Readiness | Eligible with Plan or Tasks; Constitution and Specification are optional context. |

## Known limits

- Auto-save raises no completion event. The save state shown may lag until the next workspace change or explicit save.
  Existence never lags.
- A restored generic workspace whose project name is not a Sample Project slug shows the project as unavailable, and its
  role availability as *Unknown*.
