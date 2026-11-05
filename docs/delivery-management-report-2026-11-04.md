# Delivery Management — Overview, Reports and shared explorer: report (2026-11-04)

Branch `008-traceability-first`, not pushed. This report covers three queued briefs that overlap heavily, done as one piece of work:

1. Delivery Overview remake
2. Delivery Reports remake
3. Unified work-item explorer

Commits:

| Commit | What |
|---|---|
| d3bb0aea | Shared explorer with Current State / Comparison modes, page headers, comparison limitations |
| 3f1eba8c | Functional Quality Scope: requirement explorer, per-test relationships, shared review |
| 31b04783 | Snapshots search/sort, Report History table, capped attention previews |
| 20653297 | Attention headline semantics, from the real-data audit |

## A. Audit findings

**Duplication**
- Delivery Overview kept its own feature list.
- Delivery Reports → Stories used `DeliveryWorkspace` (Hierarchy / List / Change Board / Phases plus detail).
- Both pages already shared `DeliveryFeatureExplorer` (the feature drill-down).
- Both build work items from the same frozen report through `DeliveryWorkItems.Build` (canonical ids `f:` / `s:` / `t:` / `g:`). There is no second interpretation engine.

**Mixed dimensions**
- In the explorer, a story's or task's "attention" included comparison facts: a story changed this period, or a task reopened.
- So in a current-state view, "Needs review" would have meant "changed between snapshots".

**Counts (real M2LB frozen report, read-only SELECT)**
- The old headline "14 need attention" was 5 feature-attached attention items plus 9 scope changes. The scope changes are new features in a same-day comparison.
- Next to it were task-reference counts that overlap those items (6 unresolved, 5 removed-story) and 7 hygiene warnings (6 encoding, 1 ambiguous identity).
- The frozen `attention` metric is 12: every item, hygiene included.
- None of these were labelled with what they count.

**Snapshot context**
- The "New report" fields ("No baseline suggested") and the open frozen report's own baseline/current were shown without telling them apart.
- Coverage notes and comparison warnings were repeated as separate yellow banners.

**Functional Quality Scope**
- Coverage counts came from canonical `VERIFIES` relations, but the detail showed only one sentence.
- No individual test, no evidence, and no way to review a candidate.
- The detail opened below the full-length list.

**History and lists**
- Report History used tall cards.
- Snapshots had no search or sort.
- The explorer List silently cut off at 500 rows.

## B. Data verification (real data, read-only)

**Frozen M2LB report** (content v4, coverage Partial, SameDay, kind Change):
- 38 features, 138 stories, 6 scenarios.
- 2142 tasks: 2038 checked + 104 open = 2142 ✓.
- Task relationship split: 1146 explicit-story + 21 story-phase + 877 shared + 61 functional-group + 26 scenario-group = 2131. The remaining 11 = 6 unresolved + 5 removed-story references ✓.

**Attention, as now displayed**
- **5 feature attention items** on 5 features, each counted once.
- **9 scope changes to confirm**, shown as "not defects".
- Task reference counts are shown as overlapping.
- Hygiene (7) and comparison limitations (2 warnings + 1 coverage note) are shown as not counted.

**Real `VERIFIES` relations:** 20 Explicit and 1741 Candidate, all NotReviewed. Functional Quality Scope now shows these per item, explicit apart from candidates.

**Historical compatibility**
- Frozen reports are read, never recalculated.
- A report with unknown coverage (frozen before coverage was recorded) keeps its weekly title. Only same-day or partial comparisons are named "Delivery Change Report".

## C. UI changes

**Delivery Overview**
- Header: "Current delivery state, work-item progress, and quality findings."
- The checklist clarification is visible.
- Context line: "Current snapshot: X · N features".
- The paragraph explaining the filter implementation was removed.
- New **Features | Work items** switch. Work items is the shared explorer in current-state mode, with `?explore=items&item=` in the URL.

**Delivery Reports**
- Header: "Understand what changed between delivery snapshots." plus a secondary line, a tooltip and the coverage clarification.
- Context line: "Baseline: X → Current: Y · Same-day comparison".
- Saved report title "Delivery Change Report", with the week as secondary text.
- "Saved report comparison" and "New report configuration" are labelled apart.
- One compact **Comparison limitations** block replaces the repeated warning banners.
- Attention lists are capped at 10 per category, with "+N more" linking to Stories.
- **Snapshots:** search, role filter, sort (effective / imported / name), count, Latest badge, "Not comparable" when no features were read.
- **Report History:** one table row per report, with search.

**Shared explorer (`DeliveryWorkspace` with `DeliveryExplorerOptions`)**
- List columns: ID / Type / Title / Parent / Module / Phase / Progress / (Period change) / Review, with fixed widths and truncated titles.
- Paging (100 + Show more).
- Result count, filter chips, Clear all.

## D. Requirement ↔ test correlation

- `ScopeItemDetail.Tests` lists each canonical Test → VERIFIES relation, classified exactly like the coverage counts:
  - the feature-pack id comes first;
  - an id several features share is never explicit;
  - **Explicit**, **Candidate**, or **Rejected** (stays visible as rejected and never becomes coverage again).
- Each test shows its identity, source location (from the canonical occurrence), mapping rule, resolution, evidence and review state.
- Execution is always "Execution evidence not linked — a discovered test is not an executed test". No result is invented.
- With no interpretation at all, the panel says **Not assessed**, not "untested".
- **Confirm / Reject / Defer** write to the shared canonical decision registry (`POST api/canonical/{scope}/decisions`, the same one Correlation Review uses), then the item is re-read. Without that service the view is read-only.

## E. Functional verification (bUnit)

- **Current State vs Comparison mode:**
  - current state has no removed items, no Change Board and no change badges;
  - "Needs review" means a genuine review need only;
  - both modes use the same canonical ids and checklist state.
- **Cross-page link** between the same item on both pages, in both directions.
- **Overview:** switching to Work items keeps the filters in the URL; the selection is the canonical id.
- **FQS acceptance journey:**
  1. Filter Quality.
  2. The count changes ("Showing 1 of 5").
  3. Select an item; it is highlighted.
  4. Expand the full description.
  5. Acceptance criteria are shown.
  6. Explicit, candidate and rejected tests are listed apart.
  7. Select a test: identity, location, relationship, execution and evidence are shown.
  8. Back to the requirement, then back to the list.
  9. The filter is still active, and filter and item are in the URL.
- Returning to the page restores filter and selection from the URL without re-analysis.
- A filter that hides the selected item clears the selection.
- Advanced filters open when one of them is active.
- Reviewing a candidate goes through the shared registry and re-reads the item.

**Not verified live:** none of this was exercised in a browser against the real M2LB data. The real database was only read with SELECT.

## F. Tests

| Suite | Result |
|---|---|
| Frontend `BirkNext.Web.Tests` | 8045 passed, 0 failed |
| Backend `BirkNext.Api.Tests` (after 3f1eba8c) | 5878 passed, 13 skipped, 0 failed |

New tests:
- `DeliveryWorkspaceTests`: 5 mode tests.
- `DeliveryManagementDrillDownTests`: 2 Overview page tests.
- `FunctionalQualityScopeUiTests`: 5 journey, URL, reconciliation and review tests.
- `FunctionalQualityScopeTests` (backend): per-test detail.
- `DeliverySnapshotListTests`.
- Assertions extended in `DeliveryReportingPageTests`: headers, saved vs new, history table, snapshot search, attention parts.

## G. Remaining gaps (as of 2026-11-04 — resolved in section H)

1. **Overview's feature list is kept.** It is the Features view with backlog status, which the shared explorer does not model. It was not merged into the explorer, so both pages use the explorer, but Overview's feature cards remain a separate list.
2. **No "Integration" quality category.** The acceptance scenario asks for one, but the classifier has none (Functional, Security, Privacy, Performance, Reliability, DataQuality, Compliance, Availability, Technical, Unclassified). The journey was verified with an existing category. No category was added, because that would change the classification rules.
3. **The FQS coverage index is not filtered by canonical scope.** Neither are the new per-test lists, which classify the same way. Relations from every interpreted scope are read, which matters if several Target Environments are interpreted. This is pre-existing; it is now visible through the per-test `Scope` field.
4. **Explorer state outside the URL.** Explorer filters in Delivery Reports → Stories are component state. Selection is in the URL (`item=`), but search, module and type are not, so they reset when leaving the page.
5. **Test execution evidence is not linked.** No TRX results are joined to `VERIFIES` relations. Execution is stated as unavailable.
6. **Schema / XSD evidence per requirement is not linked.** Test Data shows the assessment's asset ids only.
7. **Not done:** list virtualisation (paging is used), keyboard arrow navigation in the list, and saving scroll position across page navigation.
8. **Not done:** a live browser E2E run on real M2LB.

## H. Section G gaps — resolved (2026-11-05)

All eight gaps from section G were worked on. Real-browser verification ran against a throwaway copy of the M2LB data (database `birknext_gaps`, API :5099, published Release web build :5199, Microsoft Edge via Playwright, localhost only). The real `birknext` database was found **empty** on 2026-11-05 (no projects, snapshots or canonical entities — a local reset outside this work; it was only ever read), so the copy was taken from the earlier throwaway `birknext_ci2`, which holds the same M2LB import.

| Gap | Implementation status | Tests | Browser verified | Evidence |
|---|---|---|---|---|
| 1. Filter persistence | Implemented | `DeliveryExplorerStateTests` (3), `DeliveryWorkspaceTests` (URL + session restore, Back/Forward, stale filters) | Yes | wi-* keys in the URL; leaving Stories for Overview and returning restores view, filters, selection and scroll (3000 px → 3000 px); Back/Forward move between selections |
| 2. Environment scope | Implemented | backend: 3 environment tests; UI: selector, aggregate label, "choose an environment" | Yes (one environment exists in the data) | Selector lists only `ci-live` (the only environment with evidence); context line names it and says "source relationships, not executions"; `fq-env` in the URL |
| 3. TRX correlation | Implemented | `RequirementTestExecutionsTests` (11), UI run-table test | Yes — unavailable state | The M2LB workspace has **0 imported TRX results**, so every linked test correctly shows "Execution evidence unavailable"; pass/fail/run metadata verified with controlled fixtures only |
| 4. Integration category | Implemented | 10 classifier cases from real M2LB texts, legacy-rule-set test, frozen-report test | Yes | 23 Skolenærvær items classify as Integration (MU intake, receipts, delivery to SkoleService); frozen reports keep their rules (Overview of an older report shows no Integration) |
| 5. Schema/XSD evidence | Implemented | backend schema-link test; UI linked/candidate/validation/link test | Yes | MU FR-007 / US1: 0 linked · 7 candidate (5 XSD, 1 OpenAPI, 1 test XSD), all "Validation not performed"; the XSD link opens the exact stored file in Schema Studio |
| 6. Shared Feature Explorer | Implemented (snapshot-only fallback kept) | Workspace Features-view tests; Overview page tests migrated | Yes | Overview opens on the shared explorer's Features view (24 features, backlog statuses Not assessed / In progress / Task checklist complete / Planned); feature details → feature explorer → Back |
| 7. List usability | Implemented | keyboard grid, stable sort, virtualization tests | Yes | Full M2LB report: 2385 work items, ~50 rows in the DOM, fixed 40 px rows; arrows + Enter open an item; focus ring visible |
| 8. Browser E2E | Done | `scratchpad/gaps-e2e.cjs` (50 checks) | 50/50 passed | Screenshots in `scratchpad/gaps-shots/`; no external host contacted; no browser errors besides the one 500 injected to test error handling |

### Root causes

- **Filters lost (gap 1).** The explorer kept search, module, type, change, review, view and sort as component fields. Only the selection (`item=`) was in the URL, and every tab link was built from scratch, so returning to Stories reset everything.
- **Mixed environments (gap 2).** `CoverageIndexAsync` and `TestLinksAsync` read `VERIFIES` relations across **every** canonical scope. Each canonical scope is one Target Environment, so with two interpreted environments one environment's tests counted as another's coverage.
- **No execution link (gap 3).** TRX results live in the workspace SDD lifecycle (`TestExecutions`, `TestRuns`), keyed by the source test's stable identity once the import correlated them. Nothing joined them to the canonical `Test → VERIFIES` relations shown in Functional Quality Scope.
- **No Integration category (gap 4).** It was missing from the taxonomy. The real "retries … exponential backoff" story also exposed that the Reliability rule matched "retry" but not "retries".
- **No schema evidence (gap 5).** The canonical store holds contracts (Contract ← ContractVersion ← ContractOccurrence, Service → References → ContractVersion), but no requirement → contract relation exists in the real data. Nothing projected contracts onto requirements.
- **Two feature lists (gap 6).** The Overview rendered its own backlog list because the shared explorer had no notion of backlog status, priority or "why included".
- **List (gap 7).** The List rendered pages of 100 rows with "Show more", had no keyboard model and used unstable `Reverse()` sorting. Its columns were narrow next to the details panel.
- **Found only in the browser:**
  - The Functional Quality Scope detail kept its old fixed-drawer CSS (`position: fixed; top: 0`). Inside the master-detail layout from 3f1eba8c, the pane was 0 px tall and the page's sticky header hid the top of the drawer.
  - The explorer details pane also started behind the ~290 px sticky Delivery Reports header.
  - A restored scroll position was overridden by bringing the restored selection into view.
  - Rows showed through the master pane's top padding above the sticky table header.

### Architecture decisions

- **One state model for the explorer.** `DeliveryExplorerState` maps the filter to and from compact URL keys. `wi-view`, `wi-q`, `wi-module`, `wi-type`, `wi-change`, `wi-review`, `wi-sort` and `wi-desc` are each written only when not the default. `ExplorerViewStateStore` is a per-tab session memory keyed by mode + project + report (Functional Quality Scope: `fqs|project`). The URL always wins; a plain link restores the remembered state and writes it back to the address. Values that don't fit the report or mode (an unknown module, a change filter in current-state mode) are dropped with a visible note.
- **Environment = canonical scope.** No new identity model. `ScopeQuery.Environment` takes an environment id, `*` (all, aggregated and labelled as not evidence from one environment), or null (the only environment with evidence, if exactly one).
  - With several environments and none chosen, coverage is Not assessed and the person is asked to choose. The UI defaults to the active Target Environment only when it has evidence.
  - Canonical test relations are labelled "source relationship … discovery, not execution". Only a run is execution evidence.
- **Execution correlation is a frontend projection** (`RequirementTestExecutions`) over the existing imported results. No second store, no backend copy.
  - **Attribution needs identity:** the TRX import correlated the result (`Confirmed`) to the same stable test identity.
  - **Name-only matches** are listed as "not attributed", with their reported outcome shown only as evidence.
  - **Environment:** a run counts for the selected environment only when it records that environment (id or name). A run without one is "Environment not recorded" and never counts.
  - **Runs:** every run is listed. The current result is the newest run in the environment whose result is still current, a rule shown on screen. Theory rows fold per run; any failure fails the run, and "skipped" is kept apart from "not executed".
- **Integration (`functional-quality/3`).** Categories stay exclusive and are applied in precedence order.
  - **Precedence:** technical, then privacy, security, performance, reliability, then Integration, then data quality, compliance, availability.
  - **Integration needs intent, not one keyword:** explicit integration wording (never "integration test"), or an exchange channel *and* a system-to-system exchange.
  - **A new delivery-failure Reliability rule** ("retries", "backoff", "never silently discard") keeps failure handling as Reliability.
  - **Ordinals unchanged:** `Integration` is appended to the enum, so stored ordinals never shift. Lists use the display order.
  - **Frozen reports:** new reports record `QualityRuleSet`. A report without one is viewed with `functional-quality/2`, which has no Integration rules.
- **Schema evidence** is projected from the same environment's canonical contract evidence, in three kinds:
  - *Confirmed / explicit:* a canonical relation exists between the item and the contract.
  - *Explicit:* the item's text names the contract (its specific name, namespace or file; generic names like `schema.graphql` never count).
  - *Candidate:* module ownership only.
  - Validation is always "not performed": no validation store is linked, and an existing schema is never a validation. XSD and JSON schemas open the exact stored file in Schema Studio; other contracts open the snapshot's contract evidence in Source Analysis.
- **One explorer for the Overview.** `DeliveryExplorerOptions` gained `Backlog`, `Why` and `FeatureKeys` (the page's own filters), plus a Features view (current-state mode only).
  - The feature detail shows backlog status, requirement model and why included, and opens the feature drill-down.
  - The old Overview list remains only for projects that have snapshots but no report, because the explorer needs a frozen report's hierarchy.
- **List.** Virtualized (`Virtualize`, fixed 40 px rows, overscan 15) inside the scrolling master pane, with a sticky header and stable sort (column → canonical order → id).
  - The table is a keyboard grid: `role=grid`, `aria-activedescendant`, arrows / Home / End / Page keys move the active row, and Enter / Space selects it.
  - A remembered scroll position is applied until the virtualized list is tall enough.
  - Sticky panes sit below the page's sticky header via `--page-sticky-offset` (measured with a ResizeObserver on `[data-sticky-offset]`).

### Test commands and results

| Command | Result |
|---|---|
| `dotnet test backend/BirkNext.Api.Tests` (full) | 5894 passed, 13 skipped, 0 failed |
| `dotnet test frontend/BirkNext.Web.Tests` (full) | 8077 passed, 0 failed |
| `node scratchpad/gaps-e2e.cjs` (Edge, Release build, M2LB copy) | 50/50 checks passed |

### Migration

None. `WeeklyDeliveryReport.QualityRuleSet` is a new optional field in the frozen report JSON: absent in existing reports, which is exactly the "legacy rules" case. No table or column changed, and no frozen report was rewritten.

### Files changed

- **Shared:** `shared/FunctionalQualityScope.cs` (Integration, rule sets, environment context, schema links), `shared/DeliveryReportingContracts.cs` (`QualityRuleSet`).
- **Backend:** `FunctionalQualityScopeService.cs` (environment context, scoped coverage and tests, schema links), `FunctionalQualityScopeController.cs` (`environment` query), `DeliveryScopeService.cs` (frozen rule set), `WeeklyReportBuilder.cs`.
- **Frontend:**
  - `Components/DeliveryWorkspace.razor` (+ `.css`): state, keyboard, virtualization, Features view, scroll.
  - `Components/FunctionalQualityScopePanel.razor` (+ `.css`): environment, executions, schemas, restore, in-pane detail.
  - Pages: `Pages/DeliveryOverview.razor`, `Pages/DeliveryReporting.razor`.
  - Services: `Services/DeliveryWorkItems.cs` (options, `DeliveryExplorerState`, `ExplorerViewStateStore`), `Services/RequirementTestExecutions.cs` (new), `Services/FunctionalQualityScopePresentation.cs`, `Services/FunctionalQualityScopeApiService.cs`.
  - Wiring and assets: `Program.cs`, `wwwroot/js/explorerState.js` (new), `wwwroot/index.html`.
- **Tests:** `FunctionalQualityScopeTests.cs`, `DeliveryScopeTests.cs`, `DeliveryExplorerStateTests.cs` (new), `RequirementTestExecutionsTests.cs` (new), `DeliveryWorkspaceTests.cs`, `FunctionalQualityScopeUiTests.cs`, Overview page tests migrated to the Features view.

### Remaining gaps

1. **No live TRX evidence or environment isolation.** The M2LB data has no imported TRX results and only one interpreted environment. Pass/fail/skipped display, multi-run history and mixed-environment prevention are verified with controlled fixtures (bUnit, backend in-memory), not on real data.
2. **No server-validated schema state.** No requirement in the real data names a specific schema file, and no requirement → contract canonical relation exists, so all real associations are candidates (correct, but no Explicit example on real data). A Confirm action for schema associations needs a canonical relation to review, which the interpreter does not yet produce.
3. **Snapshot-only Overview fallback.** A project with snapshots but no generated report still uses the old Overview list: the explorer needs a report's hierarchy.
4. **Membership is not environment-scoped.** Functional Quality Scope *membership* via canonical capability relations still reads all scopes. Only test and schema evidence follow the selected environment.
5. **Window scroll isn't restored.** Scroll restoration covers the explorer's own scroll container (List / Features / Hierarchy), not the page's window scroll.
