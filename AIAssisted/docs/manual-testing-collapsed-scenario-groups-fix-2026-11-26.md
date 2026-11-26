# Finn og forstå — scenario groups collapsed by default (blocker fix) — 2026-11-26

## Root cause

`ManualTestingDiscover.razor`, `IsOpen(path, shownCount)` (scenario grouping, commit 4df809c6):

```csharp
_gOpen.TryGetValue(path, out var open) ? open : shownCount <= 200;
```

A group the tester had not toggled **opened by itself when 200 or fewer scenarios were shown**. A small scope (one module or specification, for example «Integrasjonstester» with a few scenarios) therefore came up expanded.

Expansion also lived only in the component, so it was lost when navigating away.

## Fix (changed component: `Pages/ManualTestingDiscover.razor`)

- **Collapsed by default:** `IsOpen` = «the tester opened it» (`_gOpen` holds `true`). There is no default that depends on count, recommendation, selection, search or position.
- **Kept per project:** the tester's open groups are saved in session storage (`birknext.manualTesting.openGroups.{project}`) on every toggle, «Vis alle grupper» and «Skjul alle». They are read once per project (`LoadOpenGroupsAsync`), so they survive re-renders, the drawer, and going to Planlegg og klargjør and back.
- **New context starts collapsed:** group paths contain the axis («/TestLevel:EndToEnd/ScenarioType:Functional»), so a new project (store cleared and re-read) or a new grouping axis starts collapsed unless that exact context was opened before.
- **Unchanged:**
  - «Vis alle grupper» opens every visible primary and secondary group; «Skjul alle» closes all.
  - Counts and «Velg alle» work on collapsed groups, because they use the group's filtered items rather than the rendered rows.
  - A collapsed group renders no rows, and an open one renders 50 at a time.
  - Selection, filters, canonical ids, membership and the turquoise rows are untouched.

## Regression tests (frontend, bUnit)

| Test | Covers |
|---|---|
| `DiscoverStoryStatusAndTypeTests.G01` | initial collapsed state with a small list; counts while collapsed; no rows rendered; manual expand of one group (others stay closed); the expansion saved for the project; «Vis alle grupper» (4 unique rows) and «Skjul alle» |
| `G04` | nested secondary groups start collapsed; a grouping-axis switch starts collapsed |
| `G05` | navigation away and back (saved open groups restored for the project); drawer open/close keeps the expansion |
| `G02`, `G03` | the saved grouping preference, axes, method grouping, flat «Ingen gruppering»; group selection and selection outside the filter **while collapsed** |

Fixtures that test other features (the drawer, filters, membership, work queues) now open with the tester's saved choice «Ingen gruppering». They test the flat list, not grouping.

**Results:**
- **Manual Testing, Discover, work-queue and browser test classes:** 616 / 616.
- **Project switch:** covered by the per-project storage key, re-read on project change (`LoadOpenGroupsAsync`). There is no dedicated bUnit test.
- **Browser:** see the addendum (isolated instance, disposable database).

## Addendum — browser verification (VERIFIED)

**Setup:** isolated instance `v1env12` (disposable database, ports 5097/5197), M2LB imported; script `scratchpad/v1/v1_groups.py`; screenshots `v1env12/shots/groups-*.png`.

**Results:**
- **Initial (whole project, Testnivå → Scenariotype):** 4 primary groups, all `aria-expanded=false`, 0 rows rendered.
- **Opening «Ende-til-ende-tester»:** only it opens. Its secondary group «Andre forslag» stays collapsed, and 0 rows are rendered until it is opened.
- **«Vis alle grupper»:** every group open, 284 rows rendered. **«Skjul alle»:** all collapsed, 0 rows.
- **Navigation:** open «Ende-til-ende-tester», go to Planlegg og klargjør, then browser Back: «Ende-til-ende-tester» is still open and the others are collapsed.
- **Switching the axis to Testmetode:** collapsed.
- **Console:** only 401s from requests made before the local session login.
