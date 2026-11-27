# BirkNext V11 — release readiness (2026-11-27)

**Recommendation: CONDITIONAL GO, pending the final runs.** It becomes GO only if the PENDING runs below pass. A FAIL there changes the decision. V11 is **not** claimed fully verified.

## Criteria
| Area | Criterion | Status | Evidence |
|---|---|---|---|
| Correctness | No unresolved P0/P1 in core Manual Testing journeys | PASS (V10) | V10 browser: dashboard 24/24, regression 62/62, fixes 29/29 (`afade54e`) |
| Correctness | Counts reconcile across pages | PASS | Data Quality on M2LB: 0 FAIL after the twin-scenario fix (20/20 browser checks) |
| Correctness | Scenario and test-case identities correct | PASS | V10 identity rules; Data Quality identity dry run (no ambiguous relations in M2LB) |
| Correctness | Existing user data preserved | PASS | Every run isolated; DATA-unchanged checks; no migration |
| Usability | Critical journeys without workarounds | PASS (scripted) / NOT VERIFIED (real testers) | browser journeys; acceptance guide not yet run with testers |
| Usability | No misleading statuses | PASS | blocked ≠ verified (V12); approval ≠ readiness (V10); translation errors now say what happened (V11) |
| Traceability | Source identity preserved; nothing invented | PASS | V10 / Data Quality |
| Performance | Baseline documented | PENDING | `birknext-v11-performance-baseline-2026-11-27.md` |
| Performance | Search covers the whole population | PASS | V10 P1 (synthetic 12,000) |
| Reliability | Frontend suite passes | PENDING (full run after the row-focus fix) | earlier 9,921 / 9,921; targeted 461 / 461 after the fix |
| Reliability | Backend partitioned suite passes | PENDING | `v11-backend-partitioned.log` |
| Reliability | Full-suite memory limitation reported | PARTIAL | per-host cause proven and fixed; whole-suite peaks PENDING |
| Reliability | Isolated browser journeys pass | PENDING (V11 build) | v1env40: V12 checks (incl. focus fix), dashboard, regression, fixes |
| Export safety | Export reports included/omitted; no remote writes | PASS | V10 FUN-018; no Azure DevOps writes in any run |

## Changes in this verification pass
**Translation error handling (user decision):** four distinct cases instead of one «No translation service is available right now» (503):
| Case | Status | Code | Message starts |
|---|---|---|---|
| Not configured | 503 | `translation-not-configured` | «Ingen oversettelsestjeneste er satt opp.» |
| Temporarily unavailable (HTTP 429/5xx, no connection, offline helper stopped) | 503 | `translation-unavailable` | «Oversettelsestjenesten er midlertidig utilgjengelig. Prøv igjen om litt.» |
| Timeout | 504 | `translation-timeout` | «Oversettelsen tok for lang tid og ble stoppet.» |
| Failed | 502 | `translation-failed` | «Oversettelsen mislyktes.» |

- The original cause is kept as «Detalj: …»; nothing is changed in any case.
- The page's «Prøv igjen» works for every case.
- **Tests:**
  - `V11_each_translation_problem_says_what_happened…` (4 cases);
  - `V11_HTTP_and_transport_errors_are_classified…`;
  - three assertions updated (intended wording change);
  - results PENDING.

## Confirmed defects fixed in V11
1. A singleton captured a typed HttpClient, so every API host was retained (and the handler never rotated in production).
2. Disposed test hosts were retained through HotChocolate's static hot-reload list (test-side pruner).
3. Focus could not land on a scenario row in a real browser (V12 regression found by the browser check).
4. The translation errors were misleading (above).

## Open issues (not V11)
- **BUG-005:** PARTIALLY VERIFIED until the CI-built launcher passes on an authorized tester PC.
- **Persistent requirement-identity migration:** not done (only the dry run is approved).
- **Test-method filter:** not in V11.
- **V12 UX (one assignment, per the user):** shared filtering and grouping, direct add into a chosen test suite, consistent terminology, the Testplaner hierarchy, application-wide dialog and status consistency.
- **The three readiness views:** analyse before any merge (they measure different things).

## Not verified
- Manual-tester acceptance: real testers have not run the exercise.
- Whole-suite backend memory after the fix: PENDING.
- Performance figures: PENDING.

## Risks
- **Data Quality:** a new System Settings section with record-level detail. Its access requires the system-configuration permission (tested).
- **The test-side HotChocolate pruner** depends on library internals (`_actions`, `_disposed`). If HotChocolate changes them, the pruner does nothing (no failure) and the memory growth would return. A newer HotChocolate may remove the need for it.
- **The «≤ 25 results open their groups» rule:** a change in Finn og forstå behaviour that testers will notice (intended).
