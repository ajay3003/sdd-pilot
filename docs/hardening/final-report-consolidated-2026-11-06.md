# Consolidated final report — 008-traceability-first (2026-11-06)

All work is committed **locally** on `008-traceability-first`. Nothing was pushed or deployed.

**Never touched:**
- live TT02, m2lbdev, Azure DevOps or any external service (every browser check aborted non-localhost requests and recorded none);
- `appsettings.Local.json` (a pre-existing line-ending-only change from 2026-11-02 is left uncommitted);
- `testdata/` (untracked user data, left as is).

**Not done:** no live k6, ZAP or Lighthouse run, and no real authenticated or graded-access test.

Each item below has its own detailed report in `docs/hardening/` or `docs/security/`. This report summarises them and records the final regression.

## 1. Work delivered (commits in order)

| # | Item | Commit(s) | Report |
|---|---|---|---|
| — | Tester package: frontend served as Development | 10b0d7e3 | (commit message) |
| 1 | Architecture hardening: database consistency, shared contracts, feature adoption | f5088dfa, e9271e50, 5731cf56, 532b103f | final-report-architecture-consistency-2026-11-06.md |
| 2 | Tester package: container runtime auto-detection and plain-language preflight | 01b5af57 | (commit message; 18/18 preflight tests) |
| 3 | Git Sources: prerequisites are setup, not failures; disabled actions say why | 3a878947 | FINDINGS in commit message |
| 4 | Snapshots UX | e536b241 | commit message |
| 5 | Relationship Review: (re)analysis explained | e70619f1 | commit message |
| 6 | Test Data Overview fields + XML fixture workflow | 094d9783, 6a10acf5 | commit messages |
| 7 | Performance reachability automation | 0d6b3866 | final-report-performance-reachability-2026-11-06.md |
| — | Localhost security (launch-code session, loopback-only, CSRF guard) | cbe8772e | docs/security/final-report-localhost-security-2026-11-06.md |
| 8 | Connected Systems + Integration Catalog guided workflow | 54cd9c0d | final-report-connected-systems-catalog-2026-11-06.md |
| 9 | Integration Quality Review guided workflow | dce287c1 | final-report-iqr-guided-workflow-2026-11-06.md |
| 10 | Integration Topology tabs + interactive map | 3365d4f8 | final-report-integration-topology-2026-11-06.md |
| 13 | API Quality Review hardening | e3ee1140 | final-report-api-quality-review-2026-11-06.md |
| 14 | Frontend Quality Review guided workflow | 428039fb | final-report-frontend-quality-review-2026-11-06.md |
| 15 | Requirements Traceability hardening | 3ee25080 | final-report-requirements-traceability-2026-11-06.md |
| 16 | Skolenærvær Testing workflow | fc40b887 | final-report-skolenaervaer-testing-2026-11-06.md |
| 17 + 17b | M01 Kode 6/7: static vs runtime, security matrix | b91dc803 | final-report-m01-code67-2026-11-06.md |

## 2. Confirmed defects fixed (root causes)

- **API Quality Review "Automated review: Failed" with 0 executed operations.**
  - Auto resolved to None (the frontend needs no sign-in). The engine then blocked the protected target before any request, and the presentation mapped "0 assessed" to Failed. Run eligibility also ignored the selected mode.
  - **Fix:** a new state "Blocked before execution" (Not assessed). Eligibility takes the effective mode. The explicit Attended choice is explained. No session secret moves.
- **Skolenærvær Overview printed `(configuration.Suggestions.Count - known > 0) { … }`.** Razor read `known@if` as an e-mail address, so the `@if` was printed as text.
- **Requirements Traceability dashboard export showed a mixed-denominator total.** "X% across assessed transitions" was computed over three populations. Unknown requirement → code → test counts rendered as 0.
- **Performance Test Review: Check readiness never checked container connectivity.** Readiness was read-only, and the probe was a separate action.
- **Frontend Quality Review: Auto → None while the scope said "Public + authenticated".** These came from different sources (observed vs saved), with no explanation.
- **Connected Systems showed 0 detected while the catalog had 3 proposals.** Backend detection only knows Altinn. This is a real gap, now explained; no systems were invented.
- **Git Sources:** missing administrator setup was shown as failures and "Not tested". The System Settings header said "All clear" (and its issues pill had no test hook).
- **Test Data Overview:** Blazor dropped whitespace nodes, so identity values ran together.
- **Snapshots:** an invalid date range was queried as "no snapshots", and the attention scope was unstated.
- **Localhost security:** before the change, any local process or page could call the API, and it could bind to non-loopback addresses.

## 3. Shared building blocks introduced

- **`GuidedStep` / `GuidedStepState` / `GuidedStepList`.** Domain-neutral guided steps, reused by:
  - Connected Systems, Integration Catalog, IQR, Topology;
  - API Quality Review, Frontend Quality Review, Requirements Traceability;
  - Skolenærvær Testing, M01.

  Each page derives its steps only from its own authoritative state; no second readiness engine was created. Authentication keeps its own workflow component, unchanged.
- **`PerformanceReadinessCheckService` and de-duplicated container probes.**
- **`SecurityControlMatrix`:** a documented review rule over the existing M01 checks.

## 4. Verification

**Frontend (bUnit)**
- Focused runs per item, all green after fixes:
  - Git Sources / Snapshots / Relationship Review / Test Data / XML / Performance: 231/231
  - Integration/IQR/Auth: 778/778
  - Topology/Skolenærvær/IQR: 223/223
  - API Quality Review: 359/359
  - Traceability: 305/305
  - Frontend Quality Review/WCAG: 871/871
  - M01/Skolenærvær: 169/169
- Full suite, final: **8,398 / 8,398 passed** (see §6).

**Backend (xUnit)**
- Six partitions after the localhost security change: **5,946 passed · 13 skipped · 0 failed**.
- `Services.PerformanceTests` after the reachability change: 285/285 (14 new).
- Final full run: see §6.

**Browser** (published Release build on a localhost-only stack with `birknext_gaps`)

| Check | Result |
|---|---|
| Localhost security acceptance | 12/12 |
| Earlier regressions next53–58 + phase3, on the secured stack | 122/122 |
| UI batch | 15/15 |
| Connected Systems / Catalog | 15/15 |
| IQR | 10/10 (after a wording fix found by the browser) |
| Topology | 14/14 |
| API Quality Review | 8/8 |
| Frontend Quality Review + Traceability | 12/12 |
| M01 + Skolenærvær | 11/11 |

Every browser run recorded no state-changing request (except the read-only recomputation POSTs named in the scripts), no external host and no page errors. Screenshots are in the session scratchpad.

## 5. Runtime unverified / not done (honest list)

- **API Quality Review None/Attended path:** unit-verified only. The local data has no API target, and none was fabricated.
- **Frontend Quality Review None + authenticated-scope note:** unit-verified only. The local data has Attended and Public only.
- **M01 security matrix:** unit-verified only. No stored M01 review exists locally, and no run was started.
- **M01 separate actions:** a runtime-only run and a static review without a Target Environment are **not implemented**. Both need backend changes.
- **Not redesigned in this pass:**
  - FQR post-run tabs and coverage-matrix columns;
  - AQR per-tab redesign;
  - Skolenærvær Journeys/Security/Verify tabs;
  - the Traceability matrix model;
  - URL state for several tabs.

  Each report lists these.
- **Container probe:** no live k6 container probe against M2LB or TT02.
- **Persistence not added:** manual review decisions for AQR and M01 have no storage, and Connected Systems "Ignore" stays session-only.

## 6. Final regression (actual results, after the last commit b91dc803)

The runs used memory-guarded test hosts, one at a time.

**Frontend:** `BirkNext.Web.Tests` full suite, **8,398 passed · 0 failed · 0 skipped**.

**Backend:** `BirkNext.Api.Tests` in six partitions. Partition 6 was split into three hosts so the memory guard never trips.

| Partition | Passed | Skipped | Failed |
|---|---|---|---|
| 1 | 984 | 1 | 0 |
| 2 | 1,054 | 5 | 0 |
| 3 | 806 | 0 | 0 |
| 4 | 645 | 0 | **1** (see note) |
| 5 | 511 | 0 | 0 |
| 6a (Controllers) | 125 | 0 | 0 |
| 6b | 528 | 0 | 0 |
| 6c | 1,306 | 7 | 0 |
| **Total** | **5,959** | **13** | **1 → 0 on re-run** |

**Note on the partition-4 failure:**
- The test was `ReadinessStatusIntegrityTests.AccessibilityPreferenceTrue_OverridesLegacyFalse_AndReachesDependencyProbe`.
- It failed with a `TaskCanceledException` in `LocalHttpsProxyService.StopAsync` while the test host was being disposed. That is a shutdown timeout, not an assertion.
- This flake is known from earlier sessions and is unrelated to this work.
- Re-running the class on its own: **9/9 passed**.

So the backend total is 5,960 passed across the unique tests, 13 skipped, and 0 failing after the re-run.
