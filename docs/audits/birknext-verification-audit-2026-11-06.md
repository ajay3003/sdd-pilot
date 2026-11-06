# BirkNext Independent Verification Audit

**Audit date:** 2026-11-06  
**Execution baseline:** `008-traceability-first` / `546584f1`  
**Final observed HEAD:** `008-traceability-first` / `63ee0980`  
**Scope:** Current-source build and bounded security/import test execution, BQA dispositions, baseline/worktree assessment. This is a partial verification report, not a full application acceptance audit.

## Baseline manifest

- Previous six audit reports and the two master-hardening reports were present and used as historical context. They are not treated as proof of current behavior.
- Initial worktree had local changes in `AIAssisted/backend/BirkNext.Api/appsettings.Local.json`, untracked `AIAssisted/testdata/`, and untracked historical reports in `docs/audits/`. Their contents were not inspected or modified.
- During this audit, concurrent Integration Topology UI, styles, and test edits appeared and were not exercised. They were committed during the audit as `63ee0980` (Topology Evidence & Gaps worklist); that commit was not rebuilt or verified here. The API build and focused tests below were performed at `546584f1`.
- The current API project was built in Release. The build was successful with 0 errors and 175 warnings.
- A current-source API test assembly was built with project-reference rebuilding disabled, then a focused test partition passed 90/90. This is not the full backend suite.
- No frontend build, full frontend tests, complete backend partitions, localhost application launch, browser workflow, or accessibility run was completed.

## BQA dispositions

| ID | Disposition | Evidence / conclusion |
|---|---|---|
| BQA-001 | **Conditional risk; not fully closed** | `Program.cs:84-94` checks configured `urls` and `Kestrel:Endpoints:*:Url`, rejecting detected non-loopback addresses unless `Hosting:AllowNonLoopbackBinding` is true. `LocalRequestGuard.cs:78-79, 97-116` separately validates Host/binding inputs. `LocalSessionSecurityTests.cs:39-57` exercises loopback acceptance and explicit override. This audit did not verify every host configuration source or effective socket address. No non-loopback listener was opened. |
| BQA-002 | **Conditional risk; ACL not verified** | `LocalSessionService.cs:33-45, 109-126` creates and accesses the launch-code file without setting explicit ACLs in that service. The supported installer/package ACL and other-Windows-account read access were not verified. The code value was not read. |
| BQA-003 | **Partially closed** | Current API Release build passed. Focused API test run passed 90/90. Full backend and frontend suites were not run. See test report and manifest. |
| BQA-004 | **Not verified end to end** | No synthetic producer-to-consumer application workflow or browser session was run. Historical source/test findings remain historical. |
| BQA-005 | **Not verified** | No browser, viewport, keyboard, focus, accessibility-tree, or visual inspection was performed. Concurrent modifications to the relevant Integration Topology UI made testing it unsafe in this pass. |
| BQA-006 | **Baseline confirmed; regression coverage partial** | HEAD remains `546584f1`; focused tests include current API security/import tests. The twelve commits were not each revalidated through the full UI/browser suite. |
| BQA-007 | **Source-supported; focused tests pass** | `ProjectImportService.cs:95-105, 127-143` implements staged commit/idempotency behavior; corresponding current test group was included in the 90 passing tests. Failure-interruption and full persistent-provider workflows were not exercised. |
| BQA-008 | **Conditional multi-user isolation risk** | `WorkspacePersistenceService.cs:14-15` uses fixed `default-user`; `WorkspacePersistenceController.cs:127-142` lists for that owner, while ID-based mutation routes at `151-205` pass workspace IDs to the service. The supported deployment is local-first, but shared/multi-user isolation was not demonstrated. Treat as unsupported/unknown, not proven cross-principal exploitability. |

## Current-source verification results

1. `dotnet build AIAssisted/backend/BirkNext.Api/BirkNext.Api.csproj -c Release --no-restore --verbosity minimal` — **PASS**, 0 errors, 175 warnings.
2. Initial test-project command with normal project-reference build — **BLOCKED before discovery** by the Web project stale scoped CSS guard (`BirkNext.Web.csproj:17-23`). No tests ran in that attempt.
3. `dotnet test AIAssisted/backend/BirkNext.Api.Tests/BirkNext.Api.Tests.csproj -c Release --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~LocalSessionSecurityTests|FullyQualifiedName~ActiveEventAuthorizationTests|FullyQualifiedName~AltinnTestingAuthorizationTests|FullyQualifiedName~LocalTrustedExecutionTests|FullyQualifiedName~ProjectImportServiceTests" --logger "console;verbosity=minimal" --results-directory AIAssisted/backend/BirkNext.Api.Tests/bin/Release/audit-results` — test compilation succeeded; first VSTest attempt could not create its results directory under `C:\tmp` due access denial; no tests ran in that attempt.
4. Re-run of that focused partition using the ignored `bin/Release/audit-results` directory with `--no-build --no-restore -p:BuildProjectReferences=false` — **PASS**, 90 passed, 0 failed, 0 skipped, duration 24s. API test source had been compiled immediately before the run; the Web reference was not rebuilt.

## Confirmed source observations (not all are defects)

- API startup has loopback input validation plus deliberate non-local opt-in; runtime effective binding remains unverified.
- `/api` and `/graphql` requests pass through `LocalRequestGuardMiddleware` after authentication middleware and before authorization middleware (`Program.cs:917-925`). The guard requires an authenticated session or valid bearer outside bootstrap/companion exceptions, applies Host/origin/CSRF checks, and does not itself provide project ownership authorization (`LocalRequestGuard.cs:35-72`).
- JWT and local-session schemes are registered; named capability policies are registered (`Program.cs:104-124`). This focused pass does not claim every mutation has an authorization policy.
- Workspace ownership is fixed to `default-user` in the service/controller path. This prevents asserting multi-principal isolation.
- The Web build guard fails if a physical generated scoped CSS bundle exists (`BirkNext.Web.csproj:17-23`). The observed build failure is an environment/worktree blocker; this audit did not remove or move the file.

## Coverage boundary

Not completed: full backend partitions, full frontend tests, localhost startup with disposable database, browser journeys, screenshots/log capture, all readiness invariants, M01 synthetic/manual-assessment runtime, persistence restart checks, and complete cross-module evidence flows. These remain **NOT TESTED**, not passes or defects.

Detailed stable findings and evidence are in [verified gap register](birknext-verified-gap-register-2026-11-06.md), [cross-module verification](birknext-cross-module-verification-2026-11-06.md), [security verification](birknext-security-verification-2026-11-06.md), [browser verification](birknext-ui-browser-verification-2026-11-06.md), and [test execution results](birknext-test-execution-results-2026-11-06.md).

## Verification completion — current source `7410e179` (2026-11-06)

The sections above are the **historical** audit at `546584f1` (final observed HEAD `63ee0980`). They are kept unchanged. This section records the
completion run against the current source. Source state verified: branch `008-traceability-first`, HEAD `7410e1793be162bd589762c9e65749d0f8621c46`; the isolated build worktree's
3,541 source files (`AIAssisted/`, `scripts/`) are byte-identical to that commit (blob hashes compared; only 7 `AIAssisted/docs/*.md` reports absent).
Uncommitted user files left untouched: `AIAssisted/backend/BirkNext.Api/appsettings.Local.json`, `AIAssisted/testdata/`. No push, no deployment,
no external system (M2LB, TT02, Altinn, MU, MM, Bufdata, Azure DevOps) contacted; browser runs abort every non-localhost request.

### Environment

Windows 11 Enterprise 10.0.26200 · .NET SDK 9.0.320 / 9.0.204, runtimes 8.0.16 and 9.0.x · Node 24.16 · Edge via Playwright · 32 GB RAM, 5–9 GB
free during runs · PostgreSQL database `birknext_gaps` (verification copy) · isolated API :5099 and web :5199 (the user's own stack on :5000/:5173
was running and was not touched). Every heavy step ran alone under a memory guard (1.5 GB floor, `--disable-build-servers`, no shared compiler).

### Fixes made (each reproduced first, each with tests; local commits)

| Commit | Defect | Evidence |
|---|---|---|
| `fa521a9d` | **BQA-009** — the scoped-CSS guard turned the documented dev workflow into a hard blocker: `setup-dev-scoped-css.ps1` (run by `dev-build.ps1` and `scripts/start-local.ps1`) copies the generated bundle into `wwwroot`; with the guard removed the SDK itself fails (`GenerateStaticWebAssetsDevelopmentManifest`: "Sequence contains more than one element"). The copy is now excluded from build content; the guard is warning `BNWEB001`. | Reproduced with a marked stale copy in an isolated worktree; after the fix Debug build, Release build of `BirkNext.Api.Tests` with normal project references (the exact blocked path), Web.Tests build and Release publish pass and the published bundle does not contain the stale copy. 2 tests. |
| `37b918f7` | **BQA-002 (confirmed)** — the launch-code folder/file only inherited their parent's ACL. The dev install's folder grants Modify to several foreign SIDs and a sandbox users group; a package extracted to `C:\BirkNext` (as the README advises) inherits *Authenticated Users: Modify* from `C:\`. Contrary to the service's documented guarantee, another local account could read the code. Now an explicit protected ACL (current user + SYSTEM) on Windows, 0700/0600 elsewhere, re-applied on every start. | 2 tests fail on the old code (inherited, unprotected ACL) and pass with the fix; runtime: the isolated API's existing folder was repaired on first use (only SYSTEM and the owner remain). Security/import set 92/92. |
| `8cc2139e` | WCAG AA **contrast** (axe-core, serious) on guided steps (opacity .85 → 3.55:1), AQR capability details (4.35:1), Delivery badges (4.38/4.42:1). | axe-core 4.13 on 18 pages: 12 serious nodes before, **0 serious/critical** after. 4 tests compute the ratios from the stylesheets. |
| `7410e179` | **Concatenated text** on Skolenærvær ("4 issuesConfigure integration", "…is boundOpen evidence"): Blazor drops whitespace-only text. | Found by the browser text check; explicit separators; 2 assertions. |

### Final acceptance gates

| Gate | Result | Evidence |
|---|---|---|
| G1 Stable source baseline | **PASS** | HEAD `7410e179` recorded at start and end of every batch; worktree content manifest identical to HEAD (see above). Backend/shared source unchanged since `37b918f7` (`git diff` empty), so the backend run applies to HEAD. |
| G2 Backend build | **PASS** | `dotnet build BirkNext.Api -c Release --no-incremental`: 0 errors, 175 warnings (classified in the test-execution report); Release publish of the API at `37b918f7` (package build) succeeded. |
| G3 Frontend build | **PASS** | Previously blocked path now builds (BQA-009 fix), including with a stale dev copy present; Web Release publish and the package build succeed. |
| G4 Full backend regression | **PARTIAL** | All 8 partitions at `37b918f7`: **5,972 passed, 13 skipped, 1 failed**; every discovered test reconciled (no omission, no double execution). The failure is a known intermittent host-shutdown timeout (`ReadinessStatusIntegrityTests…ReachesDependencyProbe`, 3 m 26 s under load) that passed 3/3 in isolation (6 s); unresolved nondeterminism, not counted as a pass. |
| G5 Full frontend regression | **PASS** | **8,444 / 8,444** at `7410e179`. |
| G6 Browser workflows | **PARTIAL** | All 16 mandatory journeys rendered and checked on localhost (see UI/browser report); XML fixture **Save** and Validate-record were deliberately not executed (writes) and no performance load ran. |
| G7 Synthetic cross-module | **PARTIAL** | Workflows A–H verified through their supported local portions (backend integration suites + read-only browser flows + synthetic stored-result fixtures); end-to-end import → snapshot → analysis in the browser and an interrupted import were not run (writes / no safe fixture). |
| G8 Accessibility | **PASS** (within scope) | axe-core on 18 pages: 0 serious/critical after fixes; keyboard focus visible on every page's first control; no horizontal scroll at 390 px; tab keyboard patterns checked on FQR/AQR/Topology. No screen-reader session. |
| G9 Security/readiness invariants | **PARTIAL** | Security/import 92/92; launch-code ACL fixed and runtime-verified; `sec-e2e` 12/12 (loopback, launch code, CSRF); readiness invariants asserted by the FQR/AQR/M01/Skolenærvær suites. The live non-loopback start (BQA-001 negative control) was **NOT RUN**: the permission policy denied starting a listener on `0.0.0.0`; covered only by unit tests and by observing both stacks listen on loopback only. |
| G10 Tester package | **PARTIAL** | Local replica of the pipeline: preflight tests 0 failures, migration integrity 4 folders / 59 migrations, API + Web Release publish, manifest with `sourceCommit 37b918f7` (846 files), clean-copy `validate-package.ps1` exit 0, `start-local.ps1 -ValidateOnly -SkipContainers` exit 0. Not verified on a clean PC; PowerShell 7 not installed (Windows PowerShell 5.1 used, as in the smoke-test job). |

**Overall: PARTIAL.** Mandatory gates G4, G6, G7, G9 and G10 hold evidence-backed exceptions listed above; nothing is claimed beyond them.
