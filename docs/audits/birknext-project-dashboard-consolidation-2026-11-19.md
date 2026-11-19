# Project Dashboard consolidation and Recommended Workflow retirement — 2026-11-19

Branch `008-traceability-first`. Local only; not pushed.

## Decisions

- **Rename.** "Project Quality" is renamed to **Project Dashboard** in every user-facing place:
  - sidebar, search and All Modules;
  - page heading, and the browser title "BirkNext — Project Dashboard";
  - Help Center and User Guide.

  The route (`/dashboard`) and internal ids are unchanged. "Project Quality", "Recommended Workflow", "Getting Started", "Review decisions"
  and "Release Readiness" stay as search aliases of the Dashboard.
- **Recommended Workflow retired** as a page. `/getting-started` redirects to `/dashboard` with its query string kept; the history entry is
  replaced, so Back does not loop.
- **Delivery Overview kept** as a separate management view: its data (delivery snapshots, scopes, milestones) and audience differ.

## What Recommended Workflow owned, and where it went

| Capability | Unique? | Now |
| --- | --- | --- |
| Review decisions per step (Mark reviewed, Reviewed with gaps, Approve), recorded against the exact artifact revisions | Yes: no other page called the decision API | `ReviewDecisionPanel` on Project Dashboard: the same `IRecommendedWorkflowApiService` calls, the same revisions, the same explicit "Record review decision" disclosure, refused decisions shown |
| Workspace Save, Save As, Manage Workspaces, Clear | Yes | `WorkspaceActionsMenu` in the Dashboard's workspace header |
| Project inputs (documents, source, target) | No: the Dashboard has evidence cards and readiness | Dashboard (unchanged) |
| Recommended next action | No: both pages already used `DashboardPresentation.NextStep` | Dashboard (unchanged) |
| Review-path chooser | Presentation of the same readiness paths; the Dashboard's next step and "Your next steps" cover it | Dashboard |

No new store, readiness calculator or recommendation engine was added. Both components read the Dashboard's existing `WorkflowReadiness`.
They resolve their services when an action runs, so a host without the decision or persistence API still renders the Dashboard.

## Tests

- `ReviewDecisionSemanticsTests` was ported from the page to the panel, with the same fixtures and assertions:
  - states in words, never "Ready" as completion;
  - an explicit disclosure that names the exact revision;
  - Approve sends the revisions shown, and opening a review records nothing;
  - a refused decision is shown;
  - the count of required, optional and not-counted gates.
- `CrossPageWorkspaceContractTests` now covers Project Dashboard and the sidebar: switching projects without a reload, reset, manual import,
  review decisions and the workspace menu on the Dashboard.
- Two page-specific test classes (`RecommendedWorkflowReactivityTests`, `RecommendedWorkflowStepOneTests`) and two `ViewBehaviorTests` that
  rendered the retired page were removed. Their behaviour now lives on the Dashboard and is covered there.
- Navigation expectations were updated for the removed entry.

Results: see the commit message.
