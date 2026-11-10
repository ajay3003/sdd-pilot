# BirkNext — Intelligent suggested E2E test generation: gap register (2026-11-10)

Audit: `birknext-suggested-e2e-tests-audit.md`. Implementation: `../hardening/birknext-suggested-e2e-tests-implementation.md`.
Acceptance: `birknext-suggested-e2e-tests-acceptance.md` (31/31 + 4/4 PASS with the real Edge Companion). Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN · NOT SUPPORTED.

## Audit gaps

| ID | Gap | Status | Evidence |
|---|---|---|---|
| SG-G1 | No UI facts from source | PASS | `SourceUiExtractor`; facts only, never markup (unit; SG-02/03) |
| SG-G2 | No suggestion engine | PASS | 5 kinds, expected results from evidence only, none invented (unit; SG-03/05/06/07) |
| SG-G3 | No locator verification | PASS | Companion `VerifySelectors` in real Edge; VERIFIED_UNIQUE / AMBIGUOUS / NOT_FOUND / NOT_VISIBLE / UNSUPPORTED_FRAME / STALE (unit, extension tests; SG-09/14/25) |
| SG-G4 | No human review of mappings | PASS | accept / alternative / reject / unresolved with history; ambiguous refused (unit, bUnit; SG-11) |
| SG-G5 | Duplicates | PASS | ALREADY / PARTIALLY covered / OVERLAP / NEW (unit; SG-13) |
| SG-G6 | Test Data | PARTIAL | Synthetic values from constraints; identifiers, credentials, patterns and files → NEEDS_TEST_DATA (SG-05). Binding to an existing catalog dataset is a manual step in the flow editor after approval. |
| SG-G7 | Approval and execution | PASS | Diagnostic flow via the existing store; runs only through the existing runner on an explicit Run (SG-12, SG-16–19) |
| SG-G8 | Regression impact | PASS | revalidation proposed; approved flow untouched (SG-23/24) |
| SG-G9 | Password values readable by `ReadValue` / `AssertValue` | PASS (fixed) | extension tests; SG-20 |

## Defects found by acceptance and fixed

| ID | Defect | Fix |
|---|---|---|
| SG-D1 | After approval the panel's refresh replaced the "Approved as…" message, so the tester did not see that approval happened | the message is set after the refresh |

## Remaining gaps

| ID | Gap | Status | Owner | Note |
|---|---|---|---|---|
| SG-R1 | BirkNext suggestions on the real M2LB snapshots | BLOCKED | User (environment) | The local Podman/WSL VM hosting PostgreSQL stopped during the import (`wsl.exe`: "The file cannot be accessed by the system"). Independent offline inventory done: 8 routes, 0 test ids, 0 forms. |
| SG-R2 | Requirement-text expected results (acceptance criteria in spec.md) | NOT SUPPORTED | Platform | requirement ids are recorded; free text never becomes an expected result without a reviewed mapping |
| SG-R3 | .tsx / .jsx / .vue UI | NOT SUPPORTED | Platform | Source Analysis does not read JS/TS |
| SG-R4 | Frames and shadow DOM | NOT SUPPORTED | Platform | reported per selector; would need `allFrames` and a permission review |
| SG-R5 | Running build = imported snapshot | NOT ASSESSED | Operator | verification records the observed origin, route and time; it does not prove the build |
| SG-R6 | Free DOM inventory (every control on a page) | NOT SUPPORTED by design | — | only named selectors are checked (≤ 16), so no page content is enumerated |
| SG-R7 | Accessible-name algorithm | PARTIAL | Platform | aria-label → aria-labelledby → text; label-for and title not used for role names |
| SG-R8 | M2LB has no test ids | OPEN (team) | M2LB team | role + name locators break on copy and language changes; ask for `data-testid` on critical journeys (as the 2026-09 spike recommended) |
