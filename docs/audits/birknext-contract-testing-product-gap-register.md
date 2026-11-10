# BirkNext — Contract Testing complete product hardening: gap register (2026-11-10)

Branch `008-traceability-first`. Baseline `7d2e53c8`. Statuses: IMPLEMENTED · TESTED · VERIFIED (isolated acceptance) · PARTIAL · BLOCKED · NOT RUN.

## Verification summary

| Item | Status | Result |
|---|---|---|
| Focused backend (Contract Testing, Integration Suites, Execution Evidence) | TESTED | 156/156 PASS |
| Focused frontend (Contract Testing page, Integration Suites page) | TESTED | 32/32 PASS |
| Real M2LB (M2LB-language.zip + M2LB.zip) + synthetic ledger + restart + browser at 5 widths | VERIFIED | **48/48 PASS** (round 2; round 1 44/4, defects fixed) — `birknext-contract-testing-product-acceptance.md` |
| Backend full regression (six memory-guarded partitions, sequential) | TESTED | 1,031 + 1,098 + 830 + 652 + 520 + 2,211 = **6,342 passed, 19 skipped, 0 failed** |
| Frontend full regression | TESTED | **8,540 passed, 0 skipped, 0 failed** |
| Release frontend publish | TESTED | PASS (served for the browser acceptance) |

Test settings, timeouts and coverage thresholds unchanged. The pre-existing intermittent Altinn failure (CTD-G10) did not recur in this run; its
root cause is still not verified, so it stays open.

## Implemented

Source Analysis as the default import source · one row per source contract with registry state, module grouping, search and filter · bulk import
with tests and synthetic test data in the same step · continuation of an earlier import as the next version (shared with the Integration Suite
Generator) · fingerprint by document text (identical documents in another archive are not a new version) · compact layout with stats ·
operations with test-data state, data-changing marks, selection · tests with counts, blockers by area, filters, ready selection without
data-changing tests · chain readiness · change summary and breaking-only filter · coverage split into definition and execution with NOT ASSESSED ·
validation-depth pill · hand-off to the Integration Suite Generator · run message naming why calls were blocked.

## Gaps

| Id | Gap | Status | Next step |
|---|---|---|---|
| CTP-G1 | Continuation and de-duplication key on the source contract id (file path + type) across the newest 20 snapshots, not per project: two different projects with a file at the same path would be treated as one contract | PARTIAL | key on project identity + path once snapshots carry a stable project id |
| CTP-G2 | A renamed module or file (M2LB.zip `Autorisasjon/` vs M2LB-language.zip `AutorisasjonService/`) is a new contract; no rename detection | BY DESIGN | a tester can import it as the next version of the old contract with "Import only this one" + target, or a future rename suggestion by document similarity |
| CTP-G3 | A new contract version gets tests only for operations without a test; existing tests move through the reviewed update proposal | BY DESIGN | unchanged (tests stay pinned to their version; impact shown) |
| CTP-G4 | 186 operations need a reviewed value (record ids, complex patterns) and 7 need a value (identity-shaped fields) on M2LB-language | BY DESIGN (needs domain data) | testers confirm values in Test Data; a synthetic-entity provider could resolve ids later (CTD-G1/G2) |
| CTP-G5 | Endpoint and authentication are bound per contract and environment; no bulk binding across the 34 contracts | NOT IMPLEMENTED | a module-level binding (one base path + authentication for all contracts of a module) |
| CTP-G6 | No automatic chain proposal (e.g. create → read with the returned id) | NOT IMPLEMENTED | propose chains from POST responses whose fields match a later path parameter |
| CTP-G7 | Coverage is per contract version; no project-level coverage across contracts | NOT IMPLEMENTED | aggregate the per-contract metrics on the landing view |
| CTP-G8 | Runs from the browser were not exercised in the browser acceptance (no Target Environment in the scratch browser profile); runs were verified through the API against the loopback ledger | PARTIAL | browser run with a local synthetic Target Environment |
| CTP-G9 | Real M2LB endpoints were never called (m2lbdev, TT02, Altinn, MU, MM, Azure, Bufdata) | BLOCKED (by instruction) | only with explicit authorisation and a non-production environment |
| CTD-G10 | `AltinnTestingTests.ASubmission_EvidencesTheSixAltinnSteps…` intermittent failure (earlier run) | OPEN (pre-existing, root cause not verified) | unchanged by this work |

## Security findings

Nothing was sent during import, generation or test-data generation (ledger empty, CA-14). Data-changing calls are refused without the explicit
confirmation (CS-02) and the UI no longer pre-selects them. No identity-shaped value generated (0 of 682). Approved and generated Test Data sets were
not overwritten or re-versioned on re-analysis (CR-10), and no binding changed (CR-11). No external request from the browser (CB-Z1).
