# BirkNext — Integration Suite Generator + Kode 6/7: gap register (2026-11-09)

Branch `008-traceability-first`. Initial HEAD `59299e42`. Source commits `d9426f10`, `0a909fef`, `2f4d73f2` (plus Reset Data `fca0ce04`).
Status words: IMPLEMENTED (code present) · TESTED (automated tests) · VERIFIED (isolated acceptance against the real archive / ledger) ·
PARTIAL · BLOCKED · NOT RUN.

## Verification summary

| Item | Result |
|---|---|
| Backend focused (`IntegrationSuiteTests`) | 14/14 PASS |
| Backend Contract Testing + suites | 100/100 PASS |
| Backend full regression (six memory-guarded partitions) | **6,330 passed, 19 skipped, 0 failed** (after the fix below) |
| Frontend focused (page, navigation, documentation, guide, reset panel) | 312/312 and 90/90 PASS |
| Frontend full regression | **8,530/8,530 PASS** |
| Release frontend build (`dotnet publish -c Release`) | PASS |
| Real M2LB acceptance + synthetic ledger + restart + browser | **68/68 PASS** (`birknext-auto-integration-kode67-acceptance.md`) |

## Gap register

| Id | Gap | Status | Owner / next step |
|---|---|---|---|
| G-01 | Real Altinn → Skolenærvær → Bufdata E2E execution | **BLOCKED** — MU, MM, Skole intake, Person/Tjeneste, period lock and Bufdata have no registered executor/observer in this installation; Altinn needs an authorised TT02 configuration and Target Environment; no per-message correlated observer exists downstream | adapters/observers (journey pack), environment access — not built here by design |
| G-02 | Kode 6/7 test persons | **BLOCKED** (manual preparation) — approved synthetic Kode 6 (`classification-level-3`) and Kode 7 (`classification-level-2`) children must exist in Test Data and be registered in the target Person service | test lead + Person owner |
| G-03 | Kode 6/7 domain rules | **NEEDS DECISION** — intake handling (store/withhold/reject, K67-01/02/04), aggregate Bufdata figures (K67-05), corrections (K67-10), reclassification (K67-11) | domain owner; answer the review items in the suite (stable keys, e.g. `security:bufdata-aggregate`) |
| G-04 | M01 runtime probe (K67-06/07) | **PARTIAL** — reused from Security Classification; needs a test-lead approved context, endpoint and tokens at run time | test lead |
| G-05 | Generated test data | **PARTIAL** — synthetic drafts are generated for non-identity fields and need approval in Test Data; identity fields are never generated; 8 journey inputs unresolved on M2LB | next task: *Automatic Contract Test Data Generation* (binding, schema validation, typed GraphQL variables) |
| G-06 | Negative proposals without contract-documented outcomes | **PARTIAL** — 64 of 560 M2LB tests need an expected outcome (no documented 4xx): *needs domain decision*, never inferred | contract owners / reviewers |
| G-07 | Source-backed contracts cannot assess every schema keyword | **PARTIAL** (by design) — such calls are *Not assessed*, never Passed; the suite outcome is at most *Executed* | import the document text into the Contract Registry for full assessment |
| G-08 | Topology mapping | **PARTIAL** — not resolved without a Target Environment (limitation shown on the suite) | select an environment when generating |
| G-09 | XML fixtures | **PARTIAL** — generated in Schema Studio from the exact snapshot (deep link), not inside the suite | next task |
| G-10 | Configuration values | **NEEDS REVIEW** — 26 configuration review items on M2LB (Discovered/Inferred values are never execution targets) | tester |
| G-11 | Running local stack (5000/5173) still on a build without the `AddIntegrationSuites` migration | **ACTION** — restart BirkNext so it applies the migration (Reset Data then works; until then the fixed build reports the mismatch) | user |

## Fixed in this work (were gaps or defects)

ATG-DEF-1…9 and UI-ATG-1/2 (see implementation and acceptance reports): contract continuation across snapshots, honest run summaries,
fingerprint stability, whole-body negative proposals, "already exists" not unsupported, review overlay, stable Kode 6/7 review keys, M01
requirements without an environment, unfinished calls not counted, 390 px layout.

## Regression

Sequential, memory-guarded, scratch tree = HEAD + this change; the running stack on 5000/5173 was not touched.

| Partition | Passed | Skipped | Failed |
|---|---|---|---|
| 1 Source Analysis, Integrations, Performance | 1,031 | 2 | 0 |
| 2 Target detection, Test Data, proxy, Altinn | 1,098 | 5 | 0 |
| 3 Journeys, Critical E2E, API Quality, authenticated review | 830 | 0 | 0 |
| 4 Interpretation, contract analysis, quality, engines, dependencies, Companion, Azure | 652 | 1 | 0 |
| 5 Topology, coverage, Security Classification, pipelines, … | 520 | 3 | 0 |
| 6 everything else | 2,199 | 8 | 0 |
| **Backend total** | **6,330** | **19** | **0** |
| Frontend (bUnit) | 8,530 | 0 | 0 |

The first run of partition 6 had 2 failures: `CompilerCompatibilityTests.NoPatternIsFollowedDirectlyByACollectionExpressionConditional`
(a real defect in `IntegrationSuiteComposer.cs:152` — the .NET 8 compiler used by CI cannot parse `x is T ? [] : [...]`; parenthesised) and
`ScenariosQueryTests` (its throwaway PostgreSQL container refused the connection; passed when re-run in isolation and in the full rerun —
transient, environmental). Partitions 1–5 ran on the build before that one-line parenthesisation (no behavioural change); partition 6 and
the focused suites ran after it. Skipped tests are pre-existing environment-gated skips. Release frontend publish: PASS.
