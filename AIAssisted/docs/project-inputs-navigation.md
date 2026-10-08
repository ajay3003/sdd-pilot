# Project inputs and navigation: documents, source, target

BirkNext reviews three kinds of input. Each unlocks different reviews, and none is required for every project. The
sidebar and Recommended Workflow both teach this flow: inputs first, then the pages that consume them.

## The three inputs

| Input | Owner (state comes from) | States | Needed for |
|---|---|---|---|
| Project documents | Current workspace: `ICurrentWorkspaceProjection` (shared artifact repository: Sample Project discovery and imported revisions). See [current-workspace.md](current-workspace.md). | Not provided · Available · Selection required · Unavailable | Document Review, Document Quality Review, traceability |
| Source | Latest Source Analysis snapshot of the **workspace** (no Target Environment needed) (`api/technology-coverage`, held by `ProjectApplicabilityState.Coverage`) | Not added · Analyzed · Needs refresh · Unknown | Technology Coverage, Dependency, Pipeline and Environment Analysis |
| Target Environment | Active Target Environment profile (`ProjectApplicabilityState.Profile`) | Not configured · Partial (no application URL, or the seed's `example*.local` URL) · Configured · Unknown | Frontend, API, Integration and Performance reviews, Critical E2E |

`ProjectInputPresentation.Build` (`Services/ProjectInputs.cs`) derives the three inputs from these owners. It is a pure
function: there is no new store. Notes on each:

- Sample Project selection is one way to provide documents. A manual import is the other, and it counts the same.
- Source snapshots belong to the workspace, not to a Target Environment. Analyzing source needs no target; a source-only
  project is a snapshot with the target input *Not configured* (required only for runtime reviews).
- Unsupported source technologies are a tool limitation. The source is still provided.
- Authentication verification and runtime evidence are not target existence. Each review checks those for itself.
- Input completeness is never a score.

Change propagation reuses the owners' own signals; there is no polling and no extra store:

- **Documents.** The projection's `Changed` covers project selection, import, reset and resume.
- **Target Environment.** `IFrontendAnalysisSettingsService.Changed` is raised after the Target Environments are persisted
  (create, edit, delete, active switch, reset). `ProjectApplicabilityState` subscribes to it, and unsubscribes on dispose.
- **Source.** Source Analysis calls `ProjectApplicabilityState.RefreshAsync()` when a snapshot is analyzed or reselected.
- **Workflow.** `WorkflowReadinessService` relays applicability and workspace changes as `ReadinessChanged`, and an open
  Recommended Workflow re-reads immediately. The page unsubscribes on dispose.
- **Rapid changes.** Applicability refreshes are generation-guarded, so only the latest one publishes.
  `EnsureLoadedAsync` returns at once when already loaded, so a re-read during a publish cannot recurse.

## Recommended Workflow

The page shows the three inputs as cards, each with a requirement tag:
- Documents: *Recommended start*.
- Source: *Optional · for source-based reviews*.
- Target: *Required for runtime reviews*.

Each card also has a status (text and icon, not colour only), a detail line, facts (role chips, archive, environment and
host) and an action. The workspace card lists Workspace, Project, Documents, Source, Target, Save status, Last saved and
Release Readiness. *Manual Review* counts the required review steps that apply (not-applicable and optional steps are listed
under *What Manual Review counts* but not counted) and shows “—” when none applies. Release readiness stays “—” until a
review decision exists. No document role is required of every project: a step whose own artifact is absent is *N/A*.
Review decisions are bound to artifact revisions; see [current-workspace.md](current-workspace.md#review-decisions).

Next action, in priority order (`WorkflowReadinessService.NextAction`):

1. **Nothing provided.** *Give BirkNext project context*: choose a Sample Project, the primary and easiest start. Add Source
   Snapshot and Configure Target Environment are offered as alternatives.
2. **A loaded workspace with nothing else.** Add project artifacts.
3. **A document role with several artifacts and no choice.** Choose one.
4. **Source needs refresh.** Analyze again.
5. **An open document review step.** Done through the existing backend step builder, which receives role availability.
6. **The first applicable review, in sidebar order.** The lane is the sidebar section. If that review needs a browser or
   API target and the target is *Partial*, the recommendation is *Finish the Target Environment* instead.
7. **A partial target**, if nothing else is open.

A document-only project never waits for source or a target. A source-only or target-only project never waits for documents.

*Applicable reviews* lists every Applicable or Partially applicable review, grouped by sidebar section. Applicability is
not a result.

## Sidebar (`Services/NavigationCatalog.cs`)

Section IDs are stable, and order is deterministic:

| Section (id) | Rows |
|---|---|
| Getting Started (`getting-started`) | Recommended Workflow, User Guide, Dashboard (cross-cutting, so it sits here rather than under document review) |
| Project Inputs (`project-inputs`) | Sample Projects, Source Analysis, Target Environments |
| Document Review (`document-review`) | Specification, Constitution, Data Model, Plan, Task Explorer, Document Quality Review |
| Traceability (`traceability`) | Requirements Traceability, Implementation Review, Implementation Traceability, Spec Drift, Impact Analysis (+ legacy rows behind the legacy flag) |
| Source Review (`source-review`) | Technology Coverage, Dependency Review, Pipeline Review, Environment Analysis |
| Quality & Testing (`quality-testing`) | Frontend, API, Integration Quality Review, Performance Test Review, Critical E2E Regression |
| Extensions (`extensions`) | Security Classification (badge *Extension* when enabled, otherwise *N/A*) |
| AI Review (`ai-review`) | AI Change Review (off by default) |
| Admin (`admin`) | System Settings |

Rules:

- **Target Environments** is a deep link (`admin/system-settings?section=target-environments`). System Settings sections
  are URL state: choosing a section navigates to `?section=<id>`, and the page parses `section=` on in-place navigation.
  The existing page and state are reused, and Back/Forward restore the section.
- **Current row.** NavMenu computes the current row itself (`NavItem.IsCurrentLocation`), because NavLink compares whole URLs.
  A deep-link row is current only on its page with its query. A page row is current on its path, unless a deep link claims
  that location. On `?section=target-environments` only the shortcut is current; on any other section, System Settings is.
  The current row has `aria-current="page"`.
- **Routes are unchanged.** Badges are still the shared applicability evaluator's single status.
- **Visibility.** A section is shown when at least one of its rows is visible. The old hand-kept `ShowSection*`
  predicates were removed; they had drifted from the rows.
- **Collapse state** is session memory only, keyed by section ID. Old IDs simply no longer match anything.
- **Accessibility.** Each group is `role="group"`, with an accessible name such as “Project Inputs navigation group”.
- **Documentation Health.** It reads the catalog. `NormalizeRoute` ignores query strings, so a deep link validates against
  its page route. Registry categories follow the sections.

## Known limits

- The workflow does not track whether a source or runtime review has already been run. It recommends the first applicable
  one in sidebar order, and the *Applicable reviews* list shows them all.
