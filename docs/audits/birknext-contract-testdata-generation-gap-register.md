# BirkNext — Automatic contract test data generation: gap register (2026-11-10)

Branch `008-traceability-first`. Baseline `d2746018`. Source commits `0ed74d1f` + the acceptance-fix commit that carries these reports.
Statuses: IMPLEMENTED · TESTED · VERIFIED (isolated acceptance) · PARTIAL · BLOCKED · NOT RUN.

## Verification summary

| Item | Status | Result |
|---|---|---|
| Focused backend (Contract Testing, Test Data, suites) | TESTED | 276/276 PASS |
| `ContractTestDataTests` + `IntegrationSuiteTests` | TESTED | 22/22 → 23 cases incl. identity-shaped pattern PASS |
| Focused frontend (Contract Testing page, suite page) | TESTED | 23/23 PASS |
| Real M2LB + synthetic ledger + restart + browser | VERIFIED | **27/27 PASS** (`birknext-contract-testdata-generation-acceptance.md`) |
| Backend partitions 1–5 | TESTED | 1,031 + 1,097(+1 flake, see below) + 830 + 652 + 520 passed; 0 caused by this change |
| Backend partition 6 | TESTED (re-run 2026-11-10) | **2,208 passed, 8 skipped, 0 failed** (memory guard: start 5,315 MB free, minimum 2,287 MB, no abort). The first attempt had been stopped for low memory; the re-run used the same filter, settings and timeouts |
| Frontend full regression | TESTED (re-run 2026-11-10, after partition 6) | **8,531 passed, 0 skipped, 0 failed** |
| Release frontend publish | TESTED | PASS (used for the browser acceptance) |

Partition 2 had one failure: `AltinnTestingTests.ASubmission_EvidencesTheSixAltinnSteps…` asserts no 11-digit run in stored evidence; it
passed 5/5 when re-run in isolation — a pre-existing data-dependent flake (random ids/timestamps), not touched by this change.

## Gap register

| Id | Gap | Status | Next step |
|---|---|---|---|
| CTD-G1 | Record identifiers (path parameters, `…Id`, GraphQL `ID`) need a value that exists in the target | BY DESIGN (needs review) | tester confirms a value in the generated set (new version) → reused and pinned; a synthetic-entity provider could resolve them later |
| CTD-G2 | Identities and Kode 6/7 persons | BLOCKED (manual preparation) | approved synthetic identities in Test Data (`protected-identity`, `classification-level-2/3`), registered in the target Person service |
| CTD-G3 | Complex regex patterns, unknown formats, `not`/`if-then-else`, `patternProperties`, custom GraphQL scalars | PARTIAL | named per field; a configured generator provider per format/scalar would close them |
| CTD-G4 | Snapshots analysed before this change have no retained document text → structural validation only (data stays "needs review") | PARTIAL | re-import the project (Project Import) to get full validation |
| CTD-G5 | XML/XSD fixtures are generated in Schema Studio (browser) and validated server-side at approval; not part of the Contract Testing generator | PARTIAL | unchanged by design (Schema Studio owns XML); contract tests are REST/GraphQL |
| CTD-G6 | Additional positive variants (boundary values, every enum member, optional omitted) | NOT IMPLEMENTED | one minimal-valid set per operation today; negatives come from `ContractNegativeRules` |
| CTD-G7 | Tests on a newer contract version get data only after the reviewed update proposal moves them | BY DESIGN | the update proposal keeps the binding; readiness shows missing values |
| CTD-G8 | Contract Testing UI is still the form-heavy layout | NEXT TASK | *Contract Testing complete product hardening* (queued) |
| CTD-G9 | Backend partition 6 and the full frontend regression | CLOSED | re-run sequentially on 2026-11-10: 2,208/8 skipped/0 failed and 8,531/8,531 |
| CTD-G10 | `AltinnTestingTests.ASubmission_EvidencesTheSixAltinnSteps…` failed once (11-digit run in stored evidence), passed 5/5 in isolation | OPEN (pre-existing flake, root cause not verified) | find which random id/timestamp produces an 11-digit run; not caused by this change |

## Security findings

No real personal data generated (0 identity-shaped values of 741 on M2LB; 11-digit values refused at the source); no credentials generated
(rule aligned with Test Data's); examples never copied; generation offline (TS-04, ledger empty); approval automatic only for contract-derived,
validated data; no execution without explicit authorisation (TS-05 is an authorised run against loopback only). Retained contract documents
stay in the local database (not logged, not exported).
