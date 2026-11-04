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

## G. Remaining gaps

1. **Overview's feature list is kept.** It is the Features view with backlog status, which the shared explorer does not model. It was not merged into the explorer, so both pages use the explorer, but Overview's feature cards remain a separate list.
2. **No "Integration" quality category.** The acceptance scenario asks for one, but the classifier has none (Functional, Security, Privacy, Performance, Reliability, DataQuality, Compliance, Availability, Technical, Unclassified). The journey was verified with an existing category. No category was added, because that would change the classification rules.
3. **The FQS coverage index is not filtered by canonical scope.** Neither are the new per-test lists, which classify the same way. Relations from every interpreted scope are read, which matters if several Target Environments are interpreted. This is pre-existing; it is now visible through the per-test `Scope` field.
4. **Explorer state outside the URL.** Explorer filters in Delivery Reports → Stories are component state. Selection is in the URL (`item=`), but search, module and type are not, so they reset when leaving the page.
5. **Test execution evidence is not linked.** No TRX results are joined to `VERIFIES` relations. Execution is stated as unavailable.
6. **Schema / XSD evidence per requirement is not linked.** Test Data shows the assessment's asset ids only.
7. **Not done:** list virtualisation (paging is used), keyboard arrow navigation in the list, and saving scroll position across page navigation.
8. **Not done:** a live browser E2E run on real M2LB.
