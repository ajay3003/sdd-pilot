# BirkNext final independent quality and release audit

**Audit date:** 2026-11-07  
**Repository:** `C:\Users\ajaan\source\sdd-repos\BirkNext`  
**Branch / HEAD:** `008-traceability-first` / `ffb75ab1f748e742ee04bcfa3c063a2cf3ede990`  
**HEAD tree:** `606718846a1640d6cac867b0f90e50961377fecd` (Git tree object)  
**Working tree:** pre-existing `M AIAssisted/backend/BirkNext.Api/appsettings.Local.json`; pre-existing untracked `AIAssisted/testdata/`. Both preserved. No production code changed. No push or deployment.

## Executive decision

The architecture is coherent enough for continued development. The system has a clear local single-user boundary, domain-owned persistence contexts, a distinct canonical interpretation store, and an incremental snapshot registry. There are still meaningful operational and evidence limitations: cross-context writes cannot be atomic, a few security acceptance cases remain unverified, one backend regression partition was not run on the latest code-bearing commit, and clean-PC package acceptance has not happened.

**Controlled tester rollout: CONDITIONAL GO for a small, named group using the locally validated setup and synthetic data, with an operator available to handle setup and recovery.** Do not represent this as clean-PC acceptance or as evidence for arbitrary Windows environments. **Broader distribution: NO-GO pending G4 completion, G9 runtime/cross-account checks, and G10 clean-PC acceptance.** Developer readiness is **GO with tracked follow-up**.

This audit does not claim every route or feature works end to end. The functional/UI matrices below distinguish source inspection and prior dated evidence from current execution.

## Baseline and methods

| Item | Evidence / result |
|---|---|
| Branch and HEAD | Captured with `git status`, `git branch --show-current`, `git rev-parse HEAD`; `008-traceability-first`, `ffb75ab1f748e742ee04bcfa3c063a2cf3ede990`. |
| Source delta since latest verification | `git diff --name-only 60671884..HEAD` lists seven `docs/audits/` files only. No production source changed since the code-bearing commit `60671884`. This narrows the relevance of its recorded checks but does not turn them into tests run at this HEAD. |
| Fingerprint | HEAD plus tree object above; source baseline is the tree at `60671884`. Working-tree changes are not part of that committed fingerprint. |
| SDK | Installed .NET SDKs: `9.0.204`, `9.0.320`. Projects target `net8.0`; .NET 8 runtime availability was not independently queried. |
| Frontend / backend | Blazor WebAssembly, ASP.NET Core 8 (`Microsoft.NET.Sdk.BlazorWebAssembly` / `Microsoft.NET.Sdk.Web`). GraphQL uses Hot Chocolate 14; REST uses ASP.NET Core controllers. |
| Database | EF Core 8 with Npgsql/PostgreSQL. Four registered contexts: `AppDbContext`, `DeliveryReportingDbContext`, `CanonicalInterpretationDbContext`, `SnapshotRegistryDbContext`. |
| Container runtime | `podman.exe` is installed; Docker command is unavailable in this shell. No container was started. |
| Available memory | Not verified: Windows management query was denied. |
| Test infrastructure | API xUnit, Web bUnit, Playwright project, RealProjectAcceptance project; package scripts and prior isolated verification artifacts are present. No tests were run in this audit. |
| Concurrency / protected services | No BirkNext process was started and no request was sent to ports 5000/5173. External systems and Entra were not accessed. No process ownership or shared-worktree activity beyond the captured Git status was independently established. |

Relevant prior evidence reviewed: `birknext-final-remediation-summary-2026-11-07.md`, `birknext-verified-gap-register-2026-11-06.md`, tester clean-PC checklist, cross-module verification, security verification, UI browser verification, and test execution results. Prior statuses are attributed to their recorded SHAs, not silently promoted to current-run results.

## Architecture and evidence authority

**Assessment: coherent modular monolith; acceptable tradeoffs with operational risks.** The API composition root registers the four PostgreSQL contexts and runs migrations under a process-level migration lock at startup. Each context owns its schema and migrations. Delivery reporting reads its own delivery snapshots; canonical interpretation reads source and delivery through narrow readers and owns its own records; snapshot registry stores metadata, pointers and lineage while retaining payload ownership in the origin context. This is a defensible separation without a need for microservices.

Canonical interpretation visibly reuses the shared `WorkItemInterpreter` and `CorrelationRules`, stores snapshot references and limitations, keeps identity separate from occurrence, and preserves human decisions with explicit precedence. The snapshot registry sync is incremental and idempotent for a given `(type,id)`, catches per-source failures, retains metadata when payloads disappear, and reports unavailable evidence instead of deleting history. A process-local semaphore does not serialize multiple application processes; the unique constraint/DB failure recovery and next-sync retry are the actual cross-process fallback. A failure can leave some sources unsynced until retry.

**Persistence limitation:** four contexts on one PostgreSQL database still have independent transactions and migration histories. There is no distributed transaction across source import, registry, canonical interpretation, reporting and readiness. Recovery is implemented as owner-store authority plus idempotent resync/retry in some paths; the audit did not prove recovery for every workflow. Startup applies all four migrations sequentially. Deployment rollback/migration compatibility, backup restore, and crash recovery against a real disposable PostgreSQL instance were not independently exercised here.

| Area | Audit classification | Evidence / limitation |
|---|---|---|
| Dependency direction | Acceptable tradeoff | Domain data contexts are separated and canonical readers are narrow; API composition remains a large registration hub. No full static dependency-cycle scan was run. |
| Shared rules | Positive source evidence | `WorkItemInterpreter`, `CorrelationRules`, typed snapshot references and shared contracts are used on the canonical path. Every review engine is not thereby proven to share identical readiness semantics. |
| Snapshot registration | Architectural risk, mitigated | Idempotent metadata sync, unique-race recovery and retry. The sync gate and 30-second throttle are process-local; runtime multi-instance race behavior not exercised in this audit. |
| Frozen assessments / human decisions | Not independently verified at current HEAD | Prior G7 evidence records immutable frozen assessment behavior and governance blocking; no new execution here. |
| Logging/cancellation/resources | Partial | Cancellation tokens are threaded through major persistence and analysis APIs by source inspection. Whole-repository timeout/log-redaction/resource audit not completed. |
| Migrations | Source-confirmed startup behavior | Four `MigrateAsync` calls run sequentially after relational check. Migration safety on an existing production-like schema is not verified and is outside this local-only audit. |

### Evidence quality and module claims

The code distinguishes imported source facts, canonical inference, runtime probes and human decisions in multiple contracts. The canonical persisted interpretation records source references and limitations. However, provenance depth varies by module: a shared snapshot selector or readiness label does not establish that every output captures the exact rule version, all input IDs, observed-vs-inferred distinction, and human decision provenance. This remains a cross-module audit limitation, not a confirmed defect.

### Functional module matrix

`Source inspected` means code/route/dependency inspection only. `Prior evidence` refers to the dated reports at code-bearing SHA `60671884` or earlier. No row below means “page rendered and feature therefore works.”

| Module family | Current source / prior evidence | Current audit disposition |
|---|---|---|
| Source Management, Project Import, Project Evidence, Source Analysis | Import services and source snapshot adapters inspected; prior interruption/retry and duplicate-commit evidence recorded. | Core source path is plausible and prior workflow evidence is meaningful; PostgreSQL restart/interruption was not rerun now. |
| Specification Review, Constitution, Plan, Task, Data Model explorers | Routes and shared source-analysis contracts are present. | Availability/source only; no current user journey or historical-result check. |
| Documentation Quality / Drift, Requirements Traceability, Relationship Review | Canonical and traceability services/contracts present; prior G7 covered selected producer-consumer paths. | Candidate vs confirmed and full test evidence journey not rerun. |
| Release Readiness, Delivery Overview/Reports | Separate delivery store and canonical reader path inspected; prior Delivery→Readiness evidence recorded. | Historical evidence exists; no current execution; cannot infer global readiness correctness. |
| Snapshot Management, Schema Studio, Test Data | Snapshot registry source inspected; fixture persistence report and test results exist at prior SHA. | No current CRUD/reload validation; Test Data files protected from modification. |
| Target Environments, Connected Systems, Integration Catalog | Shared contracts/routes and previous guided-workflow reports. | Not runtime verified now. External authentication/probes excluded. |
| Integration Topology / Integration Quality Review | Snapshot/canonical linkage and route/source inspected; prior synthetic workflow reports. | No current execution; confidence limited to prior reported scope. |
| API / Frontend Quality Review | Routes and test project structure present; prior browser/accessibility evidence. | Not rerun; no claim of current visual or behavioral pass. |
| Performance Test Review | Page route and local runtime architecture exist. | No external or real load test. Historical status-query observation remains an environment observation. |
| Skolenærvær Testing / M01 Code 6/7 | Routes and prior persistence/redesign reports. | Synthetic live workflow not rerun; runtime assessment must remain separate from static evidence. |

## Security

Source inspection confirms loopback binding inputs include `urls`, Kestrel endpoint URLs and .NET 8 `http_ports`/`https_ports`; non-loopback widening is rejected unless explicit opt-in is enabled. The API installs a central request guard after authentication and before authorization for `/api` and `/graphql`; local-session/CSRF/origin and host checks are centralized, with a constrained Browser Companion exception. This is a substantial mitigation for the supported single-user local deployment.

**Loopback is not user authorization.** The local session provides a local caller boundary, not multi-user identity or capability separation. The legacy fixed `default-user` workspace owner is acceptable only under the declared single-user local model. A shared or LAN deployment is not supported by this evidence. Historical checks exercised session/Host/CSRF/companion behavior and tested some widened bindings; runtime variants and second-account ACL verification remain incomplete.

| Boundary | Finding |
|---|---|
| API/GraphQL authorization | Central guard present in source. The present pass did not systematically enumerate every mapped route or independently probe anonymous access. No current runtime claim. |
| Binding | Source guard includes environment port-only settings. Historical final report calls G9 PARTIAL because several widening variants were only tested in-process. |
| Launch code | Historical report records owner-only ACL fix and local runtime checks; separate Windows-account read attempt not run. |
| Origin / CSRF | Guard and local session controller source show exact frontend origin/client-header checks for session bootstrap and unsafe cookie calls; current HTTP requests not sent. |
| Git Sources | Current commit includes six-step configuration UI and persisted non-secret settings; real Entra authentication was not attempted and requires tenant/app registration/admin consent. Security of the settings-change route is not independently runtime tested here. |
| ZIP/file import | Bounded archive readers and prior focused tests are documented. No hostile archive was supplied or attack performed in this audit. |
| Container/runtime | Safe local target restrictions are claimed in historical evidence; no container execution or k6/ZAP/external scan run now. |
| Secrets/logging | Source includes redaction-oriented handling and reports; no exhaustive secret scan or runtime log capture. |

No critical vulnerability was confirmed in this source-bounded pass. This is not a clean security certification.

## UI/UX and accessibility

The prior browser report records scoped Git Sources viewport/keyboard/axe verification, and prior workflow reports cover selected redesigned pages. Those are useful dated evidence, not a complete audit of the listed routes. This pass did not run browsers, inspect screen-reader output, or test responsive/keyboard behavior across all pages. Git Sources remains contingent on user-supplied Entra app registration values and administrator approval; “connection eligible” is not authenticated access. Other modules need clear separation between observed evidence, inferred status, unavailable evidence and human judgment.

No new UI defect was reproduced. Open coverage is captured as G8 partial for this independent pass (the historical G8 scope was PASS for its limited Git Sources subset).

## Runtime and Tester Package

The latest remediation report records a locally validated package from `60671884`, manifest/source commit, launcher preflight, PostgreSQL readiness polling, port checks, migrations/assets, and fixes for `-ValidateOnly`, busy-port exit codes and startup readiness. This audit found Podman installed but started no container. Docker execution path was simulated in prior evidence only. The checklist itself states that clean Windows PC acceptance was not run. Therefore package completeness and predictable first-run experience on a clean ordinary-user Windows machine remain open.

## Test status and reconciliation

No test suite was run at `ffb75ab1` in this audit. The code-bearing delta from `60671884` is documentation-only, so the latest report’s results remain relevant to the unchanged source tree, but they retain their original SHA and are not relabeled as current-run evidence.

| Suite / gate | Status | Evidence and precise limit |
|---|---|---|
| API/backend build | PARTIAL | Prior report records Release API publish success at `60671884`; not repeated now. |
| Frontend build | PARTIAL | Prior report records Release publish success at `60671884`; not repeated now. |
| Backend regression | PARTIAL | Prior partitioned runs at `13c7821c` omit partition 6c; later Git Sources affected suites reportedly pass (181), but a complete full backend run at final code-bearing SHA is not established. |
| Frontend regression | PASS (dated) | Prior report records 8,463/8,463 at `60671884`. |
| Fixture persistence | PASS (dated) | Prior report records 22/22 at gap closure; no code change since, but not rerun here. |
| Cross-module workflows | PASS (scoped, dated) | Prior report records G7 and defined A–F, not every module combination. |
| Security | PARTIAL (dated + source review) | Historical local checks; widened runtime variants and second-account test absent. |
| Browser/accessibility | PARTIAL (dated) | Prior Git Sources-specific axe/keyboard/390px checks; broad route accessibility not verified. |
| Clean-PC acceptance | NOT RUN | Explicitly documented as not available in prior report/checklist. |

I did not run tests because API test code includes repository-adjacent `testdata` readers and test suites write local settings in some test hosts; with user data already present and an explicit preserve constraint, safety of the whole suite’s side effects was not established. No false PASS is reported.

## G1–G10 release gates

| Gate | Requirement | Verification / evidence | Status | Gap, risk and next action |
|---|---|---|---|---|
| G1 | Source baseline known | Current branch/SHA/tree/worktree captured; source-only delta since latest tests is docs-only. | PASS | Keep SHA associated with every future run. |
| G2 | Backend build | Prior Release publish at `60671884`. | PASS (dated) | Repeat in isolated clean build workspace if source changes. |
| G3 | Frontend build | Prior Release publish at `60671884`. | PASS (dated) | Repeat with next source change. |
| G4 | Backend regression complete | Partitioned run incomplete at `13c7821c`; final backend affected suites partial. | PARTIAL | Run missing partition and current full regression in isolated safe environment; reconcile discovery/results. |
| G5 | Frontend regression complete | 8,463/8,463 at `60671884`. | PASS (dated) | Re-run after source changes. |
| G6 | XML fixture persistence | 22/22 prior isolated tests. | PASS (dated) | Clean-PC workflow still required for distribution confidence. |
| G7 | Cross-module evidence workflows | Delivery→Readiness plus defined A–F synthetic journeys and interrupted import tests in prior evidence. | PASS (scoped, dated) | Extend only where unsupported source boundaries or untested modules need evidence. |
| G8 | Accessibility | Git Sources subset has axe/keyboard/mobile evidence. | PARTIAL (overall scope) | Run route-level keyboard/accessibility review for remaining high-use workflows. |
| G9 | Localhost security | Guard source and focused tests; runtime widening variants and second account absent. | PARTIAL | Run safe isolated startup variants and ACL access check under separate standard account. |
| G10 | Tester package acceptance | Local clean-copy package validation; no clean PC. | PARTIAL | Execute supplied checklist on clean Win 10/11 ordinary-user machine. |

## BQA register disposition

Retain the latest master register’s statuses; no issue is automatically closed by this audit. BQA-001 remains PARTIAL; BQA-002 FIXED with cross-account validation open; BQA-003 CLOSED; BQA-004 CLOSED within defined workflows; BQA-005/006 CLOSED within their recorded scopes; BQA-007 CLOSED for tested interruption cases; BQA-008 accepted single-user boundary; BQA-009–012 and BQA-014/015 FIXED as recorded; BQA-013 remains an observation; BQA-016/017 FIXED (Docker path simulated); BQA-018/019 FIXED. Git Sources has prior scoped fix evidence. See the gap register for residual limitations. No new product defect was confirmed in this pass.

## Explicit limitations

- No tests or application runtime started at current HEAD.
- No full route-by-route behavior, database crash/recovery, migration rollback, backup/restore, or multi-process race test.
- No clean Windows PC, second Windows user, Entra tenant/admin, external target, real performance run, ZAP or k6 run.
- No broad screen-reader or all-module accessibility inspection.
- Available memory query was denied; resource availability is unknown.
- Historical audit material contains evidence from multiple SHAs. Only the entries with explicit matching source versions are used.

## Next actions

1. Complete G4 in the isolated test environment, including the previously omitted partition, and publish reconciled counts at a named SHA.
2. Complete G9 runtime bind matrix and second-account ACL check without touching the developer stack.
3. Run G10 clean-PC checklist with the exact pipeline artifact and an ordinary user account.
4. Expand route-level accessibility and evidence-provenance checks only for materially used workflows and any source changes.

## Release recommendation

**Developer readiness: GO. Controlled tester rollout: CONDITIONAL GO for selected, supported local Windows setups with synthetic data and documented limitations. Broader distribution: NO-GO until G4, G9, and G10 gaps close.** Major hardening is substantially complete for the defined local workflow, but not complete as an independently verified release acceptance package.
