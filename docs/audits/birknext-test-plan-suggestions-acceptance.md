# Test Plan Suggestions — acceptance (task 20 v2)

**Baseline:** `581e6314`. **Commits:**
- `1d01dd6d`: feature checkpoint;
- the final commit, which carries this report, the acceptance fixes and MT-6.

**Install:**
- isolated Release build;
- API on :5496 against database `birknext_mb`, on the disposable PostgreSQL 16 cluster (port 55990);
- web on :5497;
- Edge driven by Playwright;
- harness `scratchpad/mb/tps-accept.cjs`, with the phases main, restart and responsive.

**Data:**
- the real `birknext_mb` workspace: seven real M2LB imports, m2lb analysis baseline selected, mu not imported;
- the real curated Skolenærvær guide: revision 1, *in review — nothing published yet*.

**Guard:** every request outside localhost was aborted, and none was attempted (TPS-28). There was no Azure DevOps, Altinn, TT02 or other external call.

The migration `20261112115955_AddTestPlanSuggestions` was applied as an upgrade at API start, including the trigger `test_plan_snapshots_no_update`.

## Real Skolenærvær acceptance (browser, final run)

| Id | Check | Result |
|---|---|---|
| TPS-01 | Manual Testing → Test preparation: Functional Test Scenarios, **Test Plan Suggestions**, Test Data | PASS |
| TPS-02 | Functional Test Scenarios still opens System Understanding → What to test (21 scenarios) | PASS |
| TPS-04/05 | Select a scenario → *Prepare in Test Plan Suggestions* opens it selected; "Nothing is approved by selecting" | PASS |
| TPS-06 | Context: guide revision 1 + its review state; per source role the baseline state as the API reports it (m2lb: analysis baseline selected; mu: not imported) | PASS |
| TPS-06b | The baseline warning appears only for BASELINE_NOT_SELECTED / UNAVAILABLE (none here) | PASS |
| TPS-07 | The candidate shows the guide's own objective and state; an explicit link is named ("Rapportmottak · E2E-01 / MU-01 · Valid report") | PASS |
| TPS-08/09/10 | 7 scenarios (Gyldig rapport, Barnet finnes ikke, Barnet dukker opp senere, Duplikat melding, Korrigert rapport, Periodelås, Bufdata-fil) → 7 drafts in 5 suites named by the guide's stages: Innsending i Altinn 1, Skoletjenesten tar imot 3, Person og holdte rapporter 1, Periodelås 1, Levering til Bufdata 1 | PASS |
| TPS-11 | A real Test Data asset (`skolenaervaer-child-default`, Approved) linked by permanent id and saved | PASS |
| TPS-12 | Testability is "Not ready: 3 open"; environment and permissions are "Not verified here" | PASS |
| TPS-13/13b/14 | Snapshot 1 saved; saving again → "Unchanged: identical to snapshot 1"; it opens read-only with its fingerprint | PASS |
| TPS-15a | Bufdata-fil (NeedsClarification in the guide) shows NEEDS CLARIFICATION | PASS |
| TPS-15b/c | Reviewer clarification of the expected result is stored apart from the generated one; *Accept for preparation* records a review event (local-user, time) | PASS |
| TPS-16 | Snapshot 2 saved | PASS |
| TPS-17/18 | Compare #1 → #2: 2 changed, 5 unchanged. Barnet finnes ikke: ExpectedResult, Risk and Review changes. Because one side rests on an unreviewed guide, it "needs review", **not** a potential rule change | PASS |
| TPS-19 | Regression recommendations include "Expected result update" for Barnet finnes ikke | PASS |
| TPS-20 | Export of snapshot 2: BirkNext JSON (`birknext.test-plan-proposal/v1`, snapshot id, source baseline, 5 suites, unresolved questions) + copyable text; boundary stated; no "ADO-ready" anywhere | PASS |
| TPS-21/22/23 | Dashboard and Recommended Workflow (Manual Testing view) show the same persisted facts (7 drafts, 1 accepted, 1 missing expected, 6 without Test Data, 7 blockers) and actions (review, clarify, Test Data, compare, export) | PASS |
| TPS-24 | Browser refresh: the revision and review are kept | PASS |
| TPS-26a/b/c | **After an API restart:** both snapshots exist with the same fingerprints (`96d51b719d36`, `ef314ba000c9`); snapshot 1 still holds its earlier content; the page lists the history | PASS |
| TPS-27 | Without an authorised session, reading and saving a snapshot → 401, 401 | PASS |
| TPS-40 | 7 screens (What to test, Discover, Review, Test Data, Snapshots, Compare, Export) at 1440/1280/1024/768/390: no horizontal overflow, no control under 24 px, no text under 12 px | PASS (5/5) |
| TPS-41 | One h1, `aria-current` area, every control labelled, areas operable by keyboard | PASS |
| TPS-28 | No page or console error; nothing left localhost | PASS (every phase) |

**Final totals:** main 29/29 functional checks (its 4 responsive checks were superseded by the responsive phase); restart 5/5; responsive 6/6.

## Defects found by the acceptance and fixed (not hidden)

| Run | Finding | Root cause | Fix |
|---|---|---|---|
| 1 (22/33) | A reviewer's expected-result edit and risk never reached the server (TPS-15b/18/19) | **Race:** a review request was still in flight (the workspace read took 8.7 s on the real data) when the reviewer edited another case. The late response replaced the working copy, silently dropping the unsaved edit, and the next save sent the server's copy | Edit controls are locked while a request is in flight, and review decisions start only from a saved state. Regression test `An_unsaved_edit_blocks_review_decisions…` |
| 1 | Slow reads (workspace 8.7 s, draft 4.3 s) | Every draft read, review and snapshot reloaded every Source Analysis snapshot only to fingerprint guide scenarios | `SystemGuideService.ShownAsync` (guide content only) for change detection and the guide list; light `summary` endpoint for the focus panel |
| 1 | Export at 768 px overflowed | The download button carried the long file name, with no wrapping | "Download JSON" + a wrapping file-name note |
| 2–3 | Targets under 24 px (checkbox, card link, proposal chips); then chips overflowed at 390 px | Small hit areas; non-wrapping chip text | Larger targets; chips wrap |
| 1 | TPS-11/13/13b/15c | **Harness timing**, not product: messages were read 1.5 s after a click | Harness waits for the message (no product change) |

The readability metric was refined after run 2. Inline text links (including the shared HelpLink) are judged by font size (≥ 12 px), not by a 24 px touch-target height. Buttons, inputs, selects and checkbox labels must still be 24 px or more.

## Synthetic and independent acceptance (automated tests)

| Area | Covered by |
|---|---|
| Scenario from System Understanding, multiple selection, contextual navigation | `TestPlanSuggestionsPageTests.Scenarios_selected…`, `SystemUnderstandingPageTests.What_to_test_offers…` |
| Scenario named by Critical E2E / Skolenærvær (explicit id), duplicate across modules (ambiguous), no title matching | `TestPlanSuggestionEngineTests.Executable_links_come_only…` |
| Without expected result, without Test Data, without observation method; reviewed vs unclear requirements | `…A_scenario_the_guide_marks_unclear…`, `…A_documented_scenario_is_not_testable…` |
| Cross-project (cross-system) selection never combined or compared | `…A_rule_version_only_difference…_different_systems_are_not_compared` |
| Unselected baseline | `TestPlanSuggestionsPageTests.An_unselected_baseline…` |
| Unauthorized | Browser TPS-27; a read-only user in `A_read_only_user…` |
| Scenario revision change (flag → explicit update → back under review) | `TestPlanSuggestionServiceTests.A_guide_change_is_flagged…` |
| Human review, suite grouping, export | Service and engine tests above |
| Snapshot: first, second, identical, changed review, changed expected result, changed rule version, historical retrieval, immutability (database), concurrent save (4 parallel → 1 row), restart | `TestPlanSuggestionServiceTests` (PostgreSQL) and browser TPS-13–26 |
| Comparison: added, removed, modified, unchanged, ambiguous match, changed expected result, Test Data, requirements, suite membership, rule-version-only, incompatible system, no false behaviour claim | `TestPlanSuggestionEngineTests` |

**NOT RUN:**
- an interrupted snapshot save in the browser (covered only by the unique-constraint design and the concurrent-save test);
- an Entra-authenticated user without the write permission (the local session has it; the policy is the same as System Understanding's).

## Regression (full, on the final tree before the last test-only fix)

| Phase | Passed | Skipped | Failed | Total |
|---|---|---|---|---|
| Backend 1 / 2 / 3 / 4 / 5 | 1 049 / 1 099 / 840 / 652 / 521 | 2 / 5 / 0 / 1 / 3 | 0 | 1 051 / 1 104 / 840 / 653 / 524 |
| Backend 6a / 6b / 6c | 139 / 982 / 1 311 | 0 / 0 / 8 | 0 / **1** / 0 | 139 / 983 / 1 319 |
| **Backend total** | **6 593** | **19** | **1** | **6 613** |
| **Frontend** | **8 710** | 0 | 0 | 8 710 |

The Release build of API and web was published for acceptance.

**The one failure** is `WorkspaceBootstrapPayloadTests.Auto_save_and_save_from_a_manifest_keep_the_stored_bodies`, an outdated test from task 19C (`437c455f`), not from task 20:
- The test read the raw stored row and expected inline document bodies.
- Since 19C, bodies live in the content-addressed store, and every reader (load, bootstrap, documents) restores them with `WithDocumentBodiesAsync`.
- The test now reads the same way. It also asserts that the row holds no inline body, so it is stricter, not weaker.
- **This fix was not rerun before the commit**, at the user's request not to wait for test results. The next regression must confirm it.
