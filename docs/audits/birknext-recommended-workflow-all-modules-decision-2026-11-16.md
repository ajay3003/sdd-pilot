# Recommended Workflow and All modules: navigation decision (2026-11-16)

## Investigation (before any change)

The implementation, routes, module registry and recommendation engine were inspected, and the real UI was viewed in a browser at 1440×900.

**Recommended Workflow** (`/getting-started`, `RecommendedWorkflow.razor`)
- **Size:** about 7 screens long.
- **Content comes from stored state:**
  - `WorkflowReadinessService` (workspace snapshot, artifact revisions, review decisions, project evidence, applicability, Target Environments, runs);
  - `ReviewNavigator`;
  - the backend `/api/recommended-workflow` steps.
- **Static parts:** only the order of rules and the section texts.
- **Not built from the module registry:** its capability list combines backend steps, the 11 catalog rows that have a review id and five hand-written entries. It is grouped by evidence branch (Documentation / Traceability / Source / Runtime / Change / Quality / Extensions), not by catalog area. It omits every module without a review, such as Manual and Technical Testing and Test Data. That is why it and All modules looked inconsistent: they answer different questions with different lists.
- **Unique and indispensable:**
  - It is the **only UI that records review decisions** (Mark reviewed / Reviewed with gaps / Approve). Project Quality's "needs changes", stale and governance items link here.
  - The review-path chooser with the runtime setup checklist (`?path=…&execution=automated`). The synthetic "choose a path" recommendation routes here.
  - The project-input cards, the package card and the Workspace save menu.
- **Duplicated elsewhere:**
  - The "Available now" module list, plus the Source / Runtime / Change / Quality / Extensions inventories. These are module discovery, which is All modules' job.
  - The next action, which Project Quality (`/dashboard`) already shows from the same engine.

**All modules** (`/all-modules`) is the catalog built from `NavigationCatalog`: every module, searchable, grouped by area, marked with workspace reach and Feature Visibility. It was about 3.9 screens long, with no way to jump between areas.

**Inbound dependencies of `/getting-started`:**
- Project Quality (onboarding, "why and what comes next", governance links, the "choose a path" next action);
- `WorkspaceFocusPanel`, Project Import and the User Guide;
- a sidebar row in seven workspace views;
- 26 frontend test files and one Playwright feature.

## Decision

- **Not removed.** Recommended Workflow provides indispensable workflow (review decisions, the review path and project inputs). Removing it, or reducing it to a redirect, would lose functionality.
- **Not removed from the workspace sidebars either.** A page outside the selected view shows the "outside your workspace" notice and a temporary row on every visit. Avoiding that for a page the workspace itself links to would need a new "member without a row" concept in the view model. That would be a parallel navigation structure, which the task rules out.
- **One catalog.** All modules is the only module catalog. Recommended Workflow no longer presents itself as one. Its module inventories fold into one-line summaries with counts and point to All modules. Its decision workflow (next action, path, document and traceability review steps with their decision controls) stays open and first.
- **Next actions stay where they are.** Project Quality shows the next action from the shared engine, and the Manual / Technical / Integration Testing homes have their own domain next step. No new recommendation engine, registry or workflow state was added.

## Changes

| Area | Change |
|---|---|
| Recommended Workflow | The page description now states its purpose: record review decisions, choose a review path and set up inputs; every module is in All modules. "Available now" is a one-line disclosure ("N reviews can run with the current evidence"), with a link to All modules. Source / Runtime / Change / Quality / Extensions are disclosures with "x of y available · z not applicable". Source starts open because it holds the source-binding action. All content and test ids are kept. |
| Catalog row | It describes the page's purpose and adds search aliases "Review decisions" and "Review path". The route, label and workspace membership are unchanged. |
| Review step cards | The documentation and traceability review cards sit side by side, two per row on desktop, with tighter padding. Every control is kept: open, decision, notes. Page height at 1440×900 went from **7.05 to 4.62 screens**. |
| All modules | It has an area jump row: compact buttons with module counts that scroll to the area and focus its heading. |
| User Guide | Says where review decisions are recorded, and that All modules is where to find any module. |

## Retained / removed

- **Retained:**
  - every route, including `/getting-started` and its `?path=` deep links, so no bookmark breaks;
  - review decision recording;
  - the review path and runtime checklist;
  - project inputs, packages and the Workspace menu;
  - the backend API and the shared engine.
- **Removed:** nothing. Duplication was folded, not deleted, so nothing that a test, link or person relies on disappears.

## Remaining risks

- Review decisions are still recorded only on this page. Moving them next to each document (in the explorers) would remove the last reason to
  visit it for document reviews. That change is larger and should be its own task.
- The review-capability list stays a separate list. It is review-scoped by design, not module discovery, but it can still drift from catalog
  labels.

## Verification

**Unit tests:** frontend 9194/9194.

**Browser** (Release build, `rw-accept`):
- `rw-accept` result: 22/22.
- Paths covered:
  - Project Quality → Recommended Workflow inside the workspace, with no "outside" notice;
  - Back and Forward;
  - the folded sections with their counts;
  - the All modules link and jump;
  - search by the "review decisions" alias;
  - the `?path=runtime` deep link and its reload;
  - the Manual, Technical and Integration Testing homes;
  - project switching.

**Regression:**

| Check | Result |
|---|---|
| ui | 30/30 |
| nav | 41/41 |
| nav2 | 32/32 |
| tt | 76/76 |
| ttnav | 48/48 |
| open-now | 16/16 |

**Found by nav2 and fixed:** the area jump row first sat between the search box and the results, so Tab from search no longer reached the first module. The row now sits above the search box.

**Caveat:** the last verification build also contained in-progress changes to Integration Test Suites from the next task (other pages; nothing these checks touch). The final full regression of that task covers both commits on one revision.
