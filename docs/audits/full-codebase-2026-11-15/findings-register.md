# Findings register

Audit baseline: 0f3ca7d. Counts: P0 0, P1 1, P2 3, P3 0.

| ID | Severity | Category / owner | Code evidence | Cause / consequence | Confidence / action |
|---|---|---|---|---|---|
| SEC-01 | P1 conditional | Security; API platform | LocalRequestGuard.cs 16–48; ManualTestRunsController.cs 21–44; TestPlanSuggestionsController.cs 26–98; ManualTestRunService.cs 203–210 | Authenticated caller-supplied project keys are not visibly bound to project membership. Shared Entra deployment may permit cross-project access. | High for reviewed paths, impact conditional. Bind principal to project or reject shared hosting; add two-principal negative tests. |
| ARCH-01 | P2 | Architecture; data platform | Program.cs 174–204; LocalDataReset.cs 215–240 | Multiple contexts/schemas and reset owners increase migration/reset/provenance coordination cost. No current corruption claimed. | High. Publish ownership matrix and enforce in CI. |
| PERF-01 | P2 | Performance; Manual Testing | ManualTestRunService.cs 74–85 | ProjectRuns loads and deserializes all rows; the separate summary endpoint caps at 500. History growth increases work and memory. | High static confidence; no benchmark. Add aggregation/paging; benchmark 10k/100k records. |
| TEST-01 | P2 | Assurance; QA engineering | API test project declares EF InMemory and Testcontainers PostgreSQL; multiple test project families. | Provider differences and current test reliability unverified because no suite ran. | High for audit limitation. Map invariants to providers and CI jobs. |

Suspected/not promoted: Markdown XSS pending sanitizer fixture review; dependency advisories NOT VERIFIED; cross-module stale evidence, runtime accessibility and scalability not dynamically tested. No secrets included.
