# NEXT53 — Unified Delivery Overview and Delivery Reports redesign (2026-11-06)

Branch `008-traceability-first`. Builds on NEXT52 (6e9580f0), which separated checklist progress from verified completion. Frontend only; no backend, API or data change. Nothing pushed.

**Status: partially completed.** The shared Delivery Intelligence layer, the Project Leader dashboard, the Scrum Master summary, the Change Board and the features-first navigation are done and verified in a real browser. Several brief items remain; they are listed under "Not done".

## Audit — gaps left after NEXT52

| # | Gap (brief reference) | Before |
|---|---|---|
| A1 | No shared calculation layer (G2) | Overview used `DeliveryDashboard`. Reports computed its headline cards inline in the page, with its own "attention" rule. |
| A2 | Task-centric default (G4, AC-05/07) | The Overview's main list was the work-item explorer: a Hierarchy/List/Board/Phases split with a permanent narrow detail panel. Tasks and phases were one click from the dashboard. |
| A3 | No feature cards or focused detail (G4/G6) | Features were explorer rows. The detail was a narrow side panel, or the drill-down page with long phase text. |
| A4 | Overview had no "Attention required" with navigation (G4) | Attention groups listed items with no link to act, and no entry for missing verification evidence. |
| A5 | Charts (G4) | A checklist donut and feature-status bars. No progress or blockers by module. |
| A6 | Reports had no Scrum Master dashboard (G5) | Three headline cards plus "All figures". Declared and verified completion were not shown apart. No Change Board. |
| A7 | Reports Stories defaulted to the task workspace (G5) | The work-item explorer was the default. |
| A8 | Navigation (G4, AC-18) | One long Overview page; Reports had no change-oriented view. |
| A9 | Overview load failure | A plain red paragraph with no Retry. |

## What changed

### Shared Delivery Intelligence (`Services/DeliveryIntelligence.cs`)

Both pages now take their figures from one calculation layer. Components only render; there are no formulas in the pages.

- **`DeliveryFeatureCard`** (built by `Cards(view, report)`) holds identity and the business summary. The summary is the first user story's own description, quoted from the specification and never generated.
  - It also carries the delivery status, the qualified checklist progress ("Checklist 12 / 14 (86%)", or "No task checklist", never 0/0), and verification evidence ("No implementation or QA verification linked" unless verified).
  - It also carries the requirement model, attention, the change badge and the priority.
  - Removed features are only included in comparison mode.
- **`Modules`, `ModuleProgress` and `BlockersByModule`**: checklist progress by module, and blockers by module. The blockers chart is shown only when something is blocked, so there is never a chart of zeros.
- **`AttentionRequired`** lists, in priority order, each item with where to act:
  - blocked features and open clarifications;
  - features with review items and removals to confirm;
  - scope changes and ambiguous scope candidates (not counted);
  - missing verification evidence and comparison limitations.
- **`Comparison(report)`** is the Scrum Master figure row:
  - features in scope (baseline → current), new, removed and changed;
  - **declared done (baseline → current)**, apart from **completed with verified evidence (Not assessed)**;
  - task checklist (baseline % → current %) and feature review items.
- **`ChangeBoard`** has six lanes: New · Changed · Removed · Completed with evidence · Checklist completed this period · Needs review.
  - Each lane carries its business impact.
  - "Completed with evidence" is *Not assessed* when nothing is verified; it is never filled from checkboxes.
  - "Checklist completed this period" only includes features whose checklist became complete in the period.
- **`NextActions(card, row)`** derives next steps from the feature's own evidence: resolve the blocker, answer clarifications, review traceability items, add a task list, or link verification evidence.
- **One definition of "verified"**: the Overview KPI and the Reports comparison KPI share `DeliveryDashboard.VerifiedInfo`. A test locks this.

### Shared components (`Components/`)

All use the existing design tokens and the global `css/delivery-intelligence.css`, which the dashboard styles moved into from the page-scoped CSS.

- `DeliveryKpiGrid`: the KPI row. A word value such as "Not assessed" is smaller than numbers, so it reads as a state.
- `DeliveryBarChart`: labelled, accessible bars. Values are printed, so the chart never relies on bar length or colour alone.
- `DeliveryAttentionRequired`: entries with count, reason and a "Show" link.
- `DeliveryFeatureList`: full-width cards. A card opens its detail in place, which keeps the list position. The list is **paged (25 per page, "Show more")**, and a deep-linked feature is always rendered.
- `DeliveryFeatureDetail`: the shared detail, in current-state mode (Overview) or comparison mode (Reports).
  - It shows the objective; delivery status, checklist, verification evidence and requirement model; up to five user stories (the rest collapsed); risks and attention; recent changes; and next actions.
  - **Technical details** (feature id, paths, phases, tasks and status evidence) are in a closed `<details>`, with "Open in the work-item explorer".
- `DeliveryChangeBoard`: the lanes, each showing six features with "Show all".

### Delivery Overview (Project Leader)

The Overview has four sections, kept in the URL as `?section=`: **Overview** (the default), **Features (n)**, **Risks & quality (n)**, and **Technical details** (advanced, right-aligned).

- **Overview**:
  - KPIs: features, completed with verified evidence, task checklist, open tasks, checklist in progress, need review, blocked.
  - Attention required, with links.
  - Feature status chart.
  - Task checklist by module chart.
  - Quality & evidence, and blockers by module when there are any.
  - Change this period, linked to the full report.
  - The trend note.
- **Features**: the shared cards, a sort (needs attention first, name, module, least checklist progress) and the open feature in `?f=`.
- **Risks & quality**: Attention required, Quality & evidence, review items by category, scope candidates (awaiting review, not counted), and removed features.
- **Technical details**: the existing work-item explorer (Hierarchy/List/Phases and tasks), unchanged. This is the only place tasks are listed.

Filters:

- The frequent filters stay visible: search, module, work type, and on the list sections delivery status and attention.
- Source, functional scope and quality category are under one disclosure, which opens automatically while any of them is applied.
- Every filter stays in the URL. "Clear filters" keeps the current section.

A failed project load shows `LoadError` with **Retry**, never an empty dashboard.

### Delivery Reports (Scrum Master)

- Tabs: **Summary · Change board · Features & stories** · Snapshots · Report history · Functional Quality Scope.
- **Summary**:
  - The saved report header is unchanged (frozen identity, coverage, limitations, export).
  - It shows the shared **Baseline → current** KPI row and feature-change and task-checklist-change charts, with the trend note: "two points, not a trend"; a link to report history when there are earlier reports.
  - The existing This week, Delivery attention (hygiene apart) and Potential focus sections follow.
  - "Generate another report" is collapsed once a report is shown.
- **Change board** (new tab): the six lanes. Selecting a feature opens it in Features & stories with its detail in comparison mode.
- **Features & stories**: the shared cards are the default, in comparison mode with removed features included.
  - Filters: change (all, changed, new, removed, needs review), module and search, all in the URL.
  - **Work items** and **Feature drill-down** remain as advanced layouts.
  - A deep link with `?item=` still opens the work-item explorer.

## Tests

- New `DeliveryIntelligenceTests` (11):
  1. A complete checklist and a stated done are never verified.
  2. One definition of verified on both pages.
  3. The comparison keeps scope, change, declared and checklist apart.
  4. The Change Board lanes; the evidence lane is never filled from checkboxes.
  5. Attention entries and where they link.
  6. **A 1,600-feature backlog renders 25 cards**, "Show more" adds 25, and a deep-linked feature at position 900 is reachable.
  7. Feature detail: business facts first; tasks only under the closed Technical details.
  8. The Overview is a dashboard without a default task hierarchy or split panel; it has a module chart, attention links and a deep-linkable open card.
  9. The Reports Change Board opens the shared detail in comparison mode.
  10. The Reports summary uses the shared comparison figures; generating another report is collapsed.
  11. A failed load shows the error with Retry, never an empty dashboard.
- Updated to the new layout without changing their assertions about scope, filters or numbers: `DeliveryOverviewScopeTests` (features read from the Features section; checklist totals from the module chart note; candidates from Risks), `DeliveryManagementDrillDownTests`, `DeliveryOperationsTests` and `DeliveryReportingPageTests`.
- Full frontend suite: **8243 passed, 0 failed**. The retry test was added afterwards; the Delivery subset then passed 264/264.

## Browser verification (M2LB copy, Edge, localhost only) — 18/18

Script `next53-e2e.cjs`; screenshots `next53-overview.png`, `-features.png`, `-reports-summary.png`, `-change-board.png` and `-mobile.png`.

1. The Overview opens on the dashboard. KPIs: Features 24 · **Completed with verified evidence: Not assessed** · Task checklist 95% · Open tasks 58 · Checklist in progress 8 · Need review 3 · Blocked 0.
2. No task hierarchy or split panel by default.
3. Four sections: Overview · Features (24) · Risks & quality (3) · Technical details.
4. Module chart: AutorisasjonService 100% · Frontend 96% · HendelseAdapter 100% · …
5. Attention required: 3 features with review items · 24 missing verification evidence · 2 comparison limitations.
6. No horizontal overflow at 1440 px.
7. Features: 24 full-width cards.
8. A card is 1120 of 1176 px wide.
9. A card opens its detail in place (`&f=frontend%3A%3A006-…`) with Technical details closed.
10. The detail says "No implementation or QA verification linked".
11. The work-item explorer is still reachable under Technical details.
12. Reports Summary: scope 1 → 10, new 9, removed 0, changed 1, declared 0 → 0, **verified Not assessed**, checklist 100% → 93%, review items 1.
13. Change board: New 9 · Changed 1 · Removed 0 · Completed with evidence – · Checklist completed this period 2 · Needs review 1.
14. Reports Features & stories opens on cards; Work items is advanced.
15. No horizontal overflow at 390 px (mobile).

Also: read-only (no POST, PUT or DELETE), no external host, no page errors.

Visual fixes made after reviewing the screenshots: text-valued KPIs no longer tower over the numbers; Change Board typography and toggle style.

## Not done (remaining acceptance criteria)

- **G5 milestones / phases timeline**: no milestone timeline is shown, because no authoritative milestone evidence exists. Phases are listed only under Technical details.
- **G5 Functional Quality Scope restructuring** (AC defined / explicitly linked tests / candidate links / executed / passed / not assessed): the FQS tab is unchanged.
- **G5 history and immutable report navigation**: Report history is unchanged. There is no previous/next-report navigation inside a report and no comparison of two frozen reports.
- **G7 one shared FilterBar component**: the two pages share filter semantics and URL state, but each still renders its own filter markup.
- **G9 skeletons**: sections still use the shared inline loading indicator, not per-section skeletons. There is no export or report-generation loading change; that behaviour is as before.
- **G10 virtualization**: the cards are paged rather than virtualized. The work-item explorer keeps its existing virtualized list. No measurement was taken on 1,500–2,000 real work items; the test uses 1,600 synthetic features.
- **G12 role defaults** (Project Leader / Scrum Master / Test Lead): not implemented.
- **G13 tests** for renames and re-parenting, report immutability and export consistency: not added. Those behaviours are unchanged and covered by the existing backend tests.
