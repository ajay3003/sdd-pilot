# MT-7 — acceptance

**Install:**
- isolated Release build: API :5496 on database `birknext_mb` (disposable PostgreSQL 16 cluster, port 55990), web :5497;
- Edge driven by Playwright;
- harness `scratchpad/mb/mt7-accept.cjs`, phases setup / main / recheck / branch / restart / unauth / responsive.

**Guard:** every request outside localhost was aborted, and none was attempted. There was no Azure DevOps, Altinn, TT02 or other external call.

## Real Skolenærvær data

- **Analysis baseline:** `M2LB (7).zip` (snapshot `46d8661d`, the latest `AnalysisBaseline` decision in `source_decisions`). Only this baseline was mixed with its own Delivery snapshot.
- **Delivery snapshot:** the same archive, read-only from Downloads, imported into the isolated database (41 feature packs; Skole: `SkoleService::001-skoleservice`, `SkoleAdapter::001-skoleadapter`).
- **Interpretation:** baseline snapshot + Delivery snapshot, 731 pack-defined requirements, 21 guide scenarios (guide revision 1, an unreviewed draft).

**Real finding:** the curated guide names no specification requirement id anywhere. So no explicit association exists, and with an unreviewed guide none would count anyway. Every proposed association comes from `CR-S-VIA-TEST`: a test that the guide names for the scenario has the requirement id written in its source (informal, low confidence).

| Scenario (brief) | Proposed associations (all *suggested*, none counted before review) |
|---|---|
| Unknown child — Barnet finnes ikke | FR-006, FR-007, FR-009, FR-046 (via `HeldReportTests`, `HeldAtLockTests`) |
| Duplicate report — Duplikat melding | FR-018 (via `ReportIntakeIdempotencyTests`) |
| Correction — Korrigert rapport | FR-006, FR-019, FR-022 (via `CorrectionAndChainTests`) |
| Period lock — Periodelås | FR-024, FR-026 (via `LockRerunTests`) |
| Bufdata delivery — Bufdata-fil | FR-009, FR-037 (via `PeriodLockEndToEndTests`) |

Requirement ids known only from a test reference (`SC-003`, `SC-007` …) have no specification feature pack. They are not proposed and not counted.

### Reviewer decisions (made in the Relationship Review UI, judged against the real spec text of M2LB (7) `SkoleService/specs/001-skoleservice/spec.md`)

| Association | Spec text (start) | Decision |
|---|---|---|
| Duplikat melding → FR-018 | "Re-delivery of a message with an id already processed MUST have no effect…" | Confirmed |
| Korrigert rapport → FR-022 | "The current version MUST be decided by send time, not arrival order…" | Confirmed |
| Barnet finnes ikke → FR-006 | "A report whose child identifier cannot be resolved…" | Confirmed |
| Barnet finnes ikke → FR-046 | "The system MUST expose separate liveness and readiness health signals" | **Rejected** (informal mention in the test file only) |

### Results (browser and API, final build)

| Id | Check | Result |
|---|---|---|
| MT7-S1–S6 | Scope from Relationship Review; Delivery import; interpretation; coverage known; 0 reviewed before any decision; every association suggested | PASS |
| MT7-B01 | Manual Testing view → Test Plan Suggestions | PASS |
| MT7-B04/05 | Case shows the guide's business rule ids (labelled "Business rules (guide)") and its source (guide, scenario id, revision, fingerprint) | PASS |
| MT7-B06 | Before review: FR-018 shown as "Suggested — not verified, not counted", with a link to Relationship Review | PASS |
| MT7-B08 | Traceability: 0 reviewed, 13 needing relationship review, "Of 731 requirements"; same counts as the API | PASS |
| MT7-B10 | 3 confirmed and 1 rejected through the Relationship Review UI (type filter "Scenario coverage") | PASS |
| MT7-B12/12b | Test Plan Suggestions: FR-018 "Confirmed in Relationship Review · local-user, 2026-11-12"; FR-046 under "Rejected"; FR-006 verified | PASS |
| MT7-B13 | After accepting the Duplikat melding case: FR-018 and FR-006 = Reviewed scenario coverage; FR-022 = association verified, scenario not reviewed; FR-046 = needs relationship review (rejected for one scenario, still suggested for another) | PASS on the re-check. The first run FAILED because of a harness lookup defect: `endsWith('/FR-018')` matched another feature pack's FR-018. The product values were already correct. |
| MT7-B08b | Traceability shows the decision and the tester review (2 reviewed, 1 linked/not reviewed, 10 need review) | PASS |
| MT7-B14/16 | Dashboard "2 requirements with reviewed scenario coverage (of 731)"; Recommended Workflow "10 requirements needing association review" + action → `correlation-review?tab=review` | PASS |
| MT7-B19 | Browser refresh keeps the facts | PASS |
| MT7-B18 | Alternative generation `M2LB (5).zip` (Norwegian `Skole::001-skoletjenesten`, 461 requirements) interpreted in the same scope: 0 reviewed, no association to the `001-skoleservice` requirements. Duplikat melding → `skole::001-skoletjenesten/FR-018` appears as a new *suggestion* (another identity; the earlier decision does not carry over) | PASS |
| MT7-B18b | Back on the baseline: the decisions apply again (2 reviewed), none lost | PASS |
| MT7-B21/21b/21c | After an API restart: coverage, the decision history (#1–#4, reviewer and time) and the page | PASS. One console error on the first page load ("Failed to fetch"): the harness started while the restarted API was still starting; the page then loaded correctly |
| MT7-B22 | Without a session (fresh request context): coverage and decide → 401, 401 | PASS |
| MT7-R1440…R390 | No horizontal overflow and no control under 24 px at 1440/1280/1024/768/390 (Test Plan Suggestions links, Traceability panel, Workflow, Relationship Review) | PASS, after fixing two defects in Relationship Review: overflow at ≤ 1024 px from unbreakable fingerprints, and selects under 24 px |
| MT7-T1440…T390 | No text under 12 px | **FAIL**: the shared `MetaBadge` type badge ("Scenario coverage") in Relationship Review renders below 12 px. Pre-existing shared component; gap MT7-G5 |

Screenshots: `scratchpad/mb/out/mt7-*.png`.

## Independent synthetic acceptance (automated, expected outcomes stated from the rule)

`RequirementCoverageTests` (backend, 16) and `ReviewedRequirementCoverageTests` plus 2 `TestPlanSuggestionsPageTests` (frontend, 8):

| Fixture | Test |
|---|---|
| A explicit reference | `A_explicit_reference_in_a_published_guide…` |
| B verified reviewed / C suggested | `B_C_a_draft_guide_reference_is_only_suggested_until_confirmed…` |
| D similar titles / G scenario without requirement / H requirement without scenario | `D_G_H_…` |
| E/F one-to-many and many-to-one | `E_F_…` |
| I rejected / J superseded / Q history | `I_J_Q_…` |
| K changed scenario revision | `K_…` |
| L changed requirement text | `L_…` |
| M changed baseline (requirement missing) | `M_…` |
| N alternative branches (same id in two packs) / O missing identity | `N_O_…` |
| P concurrent review | `P_successive_decisions…`: sequential decisions on the in-memory provider. **NOT RUN:** truly concurrent writes on PostgreSQL (protected by the unique `(scope, sequence)` index of the shared registry) |
| R unauthorised reviewer | browser MT7-B22 (401). No separate role exists for Relationship Review decisions (gap MT7-G3) |
| S Dashboard / T Workflow consistency | `S_T_dashboard_and_workflow_facts_equal_the_coverage_counts…` + browser MT7-B14/16 |
| informal / ambiguous test links; test-only ids | `Via_test_…` (2 tests) |
| guide reader passes only what the guide states | `The_guide_reader_passes_only_what_the_guide_states` |
| Unknown never zero | `Without_an_interpretation_or_without_guide_scenarios…`, frontend unknown/no-project tests |
| accepting a Test Case approves no association | `MT7_a_case_shows_verified_and_suggested…` |
