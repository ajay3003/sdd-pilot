# BirkNext — Unified Test Execution & Evidence Explorer: gap register (2026-11-10)

Audit: `birknext-unified-execution-evidence-audit.md`. Implementation: `../hardening/birknext-unified-execution-evidence-implementation.md`.
Acceptance: `birknext-unified-execution-evidence-acceptance.md` (41/41 + 5/5 + 10/10 PASS, 1 NOT RUN). Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN · NOT SUPPORTED.

## Audit gaps

| ID | Gap | Status | Evidence |
|---|---|---|---|
| UE-G1 | Results page showed Execution Evidence runs only | PASS | six read-only providers (unit 7/7); in the browser: contract tests, chains, composite E2E (UE-C1/C2) |
| UE-G2 | No run kind | PASS | contract tests / chain / suite / composite distinguished through suite links and `ChainRef`; subflow never a full pass (UE-C2) |
| UE-G3 | No server-side paging of the history | PASS | bounded page (≤ 100), total, skip/take on the server (unit; UE-17/18) |
| UE-G4 | Storage metrics dominated; table allocation shown as evidence | PASS | measured JSON on the overview, allocation only under Storage & Retention with an explanation (UE-23/28) |
| UE-G5 | No project or environment scope | PASS | scope bar; project from recorded references only; isolation (UE-20/21, unit) |
| UE-G6 | (checked) Passed without assessment | not a gap in the store | the explorer repeats the guard and normalises the other owners without upgrading (unit) |
| UE-G7 | No overview, attention or per-kind results | PASS | UE-22/24, results by kind with denominators |
| UE-G8 | No cross-stage timeline | PASS | recorded events only; unobserved stages listed (UE-07, UE-C3) |
| UE-G9 | No run comparison | PASS | NewFailure / ResolvedFailure / Regressed / latency; refuses other environments (UE-13/14, unit) |
| UE-G10 | No tester report | PASS | sanitized Markdown, bounded, no bodies (UE-27, unit) |
| UE-G11 | Static assessments not separated | PASS | `StaticAssessment` / `DataValidation` never counted as executions (unit) |
| UE-G12 | Technical empty state | PASS | UE-02 |

## Defects found by acceptance and fixed

| ID | Defect | Fix | Regression test |
|---|---|---|---|
| UE-D1 | A composite stage whose calls were skipped (planned, never transmitted) disappeared from the timeline instead of showing "not observed" | stages without a transmitted attempt are listed as "Not observed by this run" | UE-C3 (browser, real composite runs) |

## Remaining gaps

| ID | Gap | Status | Note |
|---|---|---|---|
| UE-R1 | Journey, browser E2E, performance and Parquet results not exercised in the browser | NOT RUN | No real run of those kinds exists in the isolated stack, and none is fabricated. Covered by unit tests over owner-shaped records. |
| UE-R2 | History scan is bounded (newest 1000 per owner) | PARTIAL by design | Truncation is reported; narrowing the time range reaches older runs. Making it exhaustive would need run-kind and project columns on `execution_runs`. |
| UE-R3 | Project attribution for runs without contract references | PARTIAL | Shown as "not recorded" and never guessed. Recording a project on every run needs the owners to store it. |
| UE-R4 | No per-project ACL | NOT SUPPORTED | Single local workspace. The explorer adds no permission and bypasses none: GETs only; deletion and retention keep their write policy. |
| UE-R5 | Comparison only for REST/GraphQL runs | PARTIAL by design | Performance keeps its own baselines; other owners have no comparable unit. |
| UE-R6 | Messaging and Blob observations not listed as runs | NOT SUPPORTED by design | They are imported observations, not executions; they stay on the run's call evidence (correlation by execution id). |
