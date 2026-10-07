# Project inputs and navigation: documents, source, target

BirkNext reviews three kinds of input. Each unlocks different reviews, and none is required for every project. The
sidebar and Recommended Workflow both teach this flow: inputs first, then the pages that consume them.

## The three inputs

| Input | Owner (state comes from) | States | Needed for |
|---|---|---|---|
| Project documents | Current workspace: `ICurrentWorkspaceProjection` (shared artifact repository: Sample Project discovery and imported revisions). See [current-workspace.md](current-workspace.md). | Not provided · Available · Selection required · Unavailable | Document review, traceability, Quality Review |
| Source | Latest Source Analysis snapshot of the **active Target Environment** (`api/technology-coverage`, held by `ProjectApplicabilityState.Coverage`) | Not added · Analyzed · Needs refresh · Unknown | Technology Coverage, Dependency, Pipeline and Environment Analysis |
| Target Environment | Active Target Environment profile (`ProjectApplicabilityState.Profile`) | Not configured · Partial (no application URL, or the seed's `example*.local` URL) · Configured · Unknown | Frontend, API, Integration and Performance reviews, Critical E2E |

`ProjectInputPresentation.Build` (`Services/ProjectInputs.cs`) derives the three inputs from these owners. It is a pure
function: there is no new store. Notes on each:

- Sample Project selection is one way to provide documents. A manual import is the other, and it counts the same.
- Source snapshots are stored per Target Environment. Analyzing source therefore needs an active environment, though not an
  application URL. A source-only project is an environment without a URL plus a snapshot, so its target shows *Partial*.
- Unsupported source technologies are a tool limitation. The source is still provided.
- Authentication verification and runtime evidence are not target existence. Each review checks those for itself.
- Input completeness is never a score.

Change propagation reuses existing events. The projection's `Changed` covers project selection, import, reset and resume.
`ProjectApplicabilityState.Changed` fires after navigation (NavMenu refreshes on location change), after a workspace change,
after reset and after integration templates are applied. A Target Environment saved, or a source analyzed, shows up on the
next navigation. There is no polling.

## Recommended Workflow

The page shows the three inputs as cards, each with a requirement tag:
- Documents: *Recommended start*.
- Source: *Optional · for source-based reviews*.
- Target: *Required for runtime reviews*.

Each card also has a status (text and icon, not colour only), a detail line, facts (role chips, archive, environment and
host) and an action. The workspace card lists Workspace, Project, Documents, Source, Target, Save status, Last saved and
Release Readiness. *Manual Review* says *Not required* when there are no document review steps. Release readiness stays
“—” until a review decision exists.

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
| Document Review (`document-review`) | Specification, Constitution, Data Model, Plan, Task Explorer |
| Traceability (`traceability`) | Requirements Traceability, Implementation Review, Implementation Traceability, Spec Drift, Impact Analysis (+ legacy rows behind the legacy flag) |
| Source Review (`source-review`) | Technology Coverage, Dependency Review, Pipeline Review, Environment Analysis |
| Quality & Testing (`quality-testing`) | Quality Review, Frontend, API, Integration Quality Review, Performance Test Review, Critical E2E Regression |
| Extensions (`extensions`) | Security Classification (badge *Extension* when enabled, otherwise *N/A*) |
| AI Review (`ai-review`) | AI Change Review (off by default) |
| Admin (`admin`) | System Settings |

Rules:

- **Target Environments** is a deep link (`admin/system-settings?section=target-environments`). System Settings handles
  `section=` on in-place navigation, so the existing page and state are reused. The row never claims the System Settings
  page path, so that page stays under Admin.
- **Routes are unchanged.** Badges are still the shared applicability evaluator's single status.
- **Visibility.** A section is shown when at least one of its rows is visible. The old hand-kept `ShowSection*`
  predicates were removed; they had drifted from the rows.
- **Collapse state** is session memory only, keyed by section ID. Old IDs simply no longer match anything.
- **Accessibility.** Each group is `role="group"`, with an accessible name such as “Project Inputs navigation group”.
- **Documentation Health.** It reads the catalog. `NormalizeRoute` ignores query strings, so a deep link validates against
  its page route. Registry categories follow the sections.

## Known limits

- Without an active Target Environment there is nowhere to store a source snapshot. This is the current Source Analysis
  model, and it was not changed here.
- The workflow does not track whether a source or runtime review has already been run. It recommends the first applicable
  one in sidebar order, and the *Applicable reviews* list shows them all.
- After a Target Environment is saved or a source is analyzed, the workflow updates on the next navigation, which is when
  applicability refreshes. It does not update while you stay on the settings page.
- The Target Environments row stays highlighted while you switch to another System Settings section inside the page,
  because that switch does not change the URL.
