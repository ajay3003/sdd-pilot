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

## Gap closure — 2026-11-07 (G4, G6, G7, G9, G10)

The sections above are kept unchanged. This section closes the five PARTIAL gates of the 2026-11-06 completion run (ended at `f1b7f041`).
Branch `008-traceability-first`; code verified at `04845c9b` (tests) and `6e794e94` (launcher); nothing pushed or deployed. Writable
workflows ran against an isolated copy: database `birknext_g67` (created from the verification copy `birknext_gaps`), content root
`api-g67`, API on 127.0.0.1:5099 and web on :5199. The user's stack on :5000/:5173, its PostgreSQL data, `appsettings.Local.json`,
`AIAssisted/testdata/` and the canonical XML fixtures were not modified (hashes compared before/after). Browser runs abort every
non-localhost request; no TT02, m2lbdev, Altinn, MU, MM, Bufdata, Azure or Azure DevOps call was made; no k6 load ran; only synthetic data.

### Fixes (each reproduced first; local commits)

| Commit | Defect | Evidence |
|---|---|---|
| `e0077f15` | **BQA-012** — `LocalHttpsProxyService.StopAsync` awaited its gate with the host-shutdown token. When an earlier hosted service spends the shutdown budget the token is already cancelled and `SemaphoreSlim.WaitAsync` throws even for a free gate, so `WebApplicationFactory.Dispose` failed (the intermittent `ReadinessStatusIntegrityTests` failure). A free gate is now taken without waiting; a gate still busy past the deadline is left to `Dispose`, which stops the session. | New `HostShutdownBudgetTests` (real host, 200 ms shutdown budget, slow-stopping service) and 2 proxy tests: red with the exact stack of the full-run failure, green after; proxy suite 79/79; 5 repeated runs 13/13. |
| `73b9d9e8` | **Git Sources setup** — System Settings → Git Sources told the user to edit configuration files to enable Microsoft sign-in. Sign-in (enabled, client ID, tenant, device code) is now set up in the card; values are validated (GUID client ID, tenant format, client ID required to enable), saved to the local settings file and reported as "restart required" until the running values match. | 146 backend + 17 Git Sources UI tests; browser: save 18/18, after an API restart 10/10 (on the isolated content root, never the user's `appsettings.Local.json`). |
| `04845c9b` | **BQA-014** — in a fresh browser the default Target Environments are not stored yet; M01 Security Review saves/runs and Performance Test Review definitions, data profiles, runs and baselines were sent without asking the client to store the environment, so the stored result was keyed to an environment id that changed on reload ("review disappeared"). These writes now declare that they need a persistent environment. | 2 tests (red before, green after) + 241 related; browser Workflow E 14/14 after republish. |
| `6e794e94` | **Launcher** — (1) `start-local.ps1 -ValidateOnly` reported "preflight passed" (exit 0) on a PC with neither Podman nor Docker; (2) a busy port stopped startup but exited 0. | Reproduced on a copy of the packaged launcher: old exit 0 / 0, new exit 2 (names the missing runtime and `-SkipContainers`) / 3; controls with Podman and with `-SkipContainers` still exit 0; the busy port's owner (the user's frontend) was only read, never stopped. Preflight tests 0 failures. |

### Gate results

| Gate | Result | Evidence |
|---|---|---|
| G4 Full backend regression | **PARTIAL** | At `04845c9b`, 6 partitions (the 6th split in three after the memory guard stopped it): **5,986 passed, 13 skipped, 1 failed**; 4,697 discovered methods all ran, none in two partitions. BQA-012 fixed at its root; `ReadinessStatusIntegrityTests` passed. The one failure (BQA-018) reads a real M2LB archive from the user's Downloads folder that was replaced at 00:57 on the day of the run with a version lacking 4 of the 5 expected test projects; no related code changed. Not a product regression, but not counted as a pass. Frontend: 8,452/8,452 (G5 stays PASS). |
| G6 XML Fixture Save/reload | **PASS** | `g6-xf` 22/22 in Edge on the isolated stack: synthetic archive analysed by the real Source Analysis upload → snapshot and synthetic XSD selected → fields listed → Child mapping applied → XML generated (unsaved) → valid against the XSD while business/runtime tiers stay unverified → an edit invalidates validation and disables Save → an out-of-range value is Invalid with an issue listed → malformed XML fails the well-formed tier and offers nothing to save → reset + validate → Save → confirmed from the reloaded catalog and from the database (`saved_workspace_artifacts`, stored XML byte-equal to the editor XML) → still not approved after a full reload → a duplicate alias is saved under a free alias, never over the first. Approval and binding are covered by Workflow C. Protected data unchanged (hashes): `api-gaps` App_Data, repository `testdata/`; `api-g67` identities only gained the 2 synthetic identities. |
| G7 Cross-module writable workflows | **PARTIAL** | A–F executed with writes on the isolated stack (see cross-module report): A 13/14 scripted + A11 confirmed in the database (the scripted check was not discriminating), B 10/12 scripted + B1 (script timing) re-checked and B10 confirmed through the topology API, C 11/11 (rerun after correcting the script's consumer-count assumption), D 11/12 scripted + D6 confirmed in the stored feature snapshot, E 14/14 (after the BQA-014 fix), F 12/12. Not exercised: Release Readiness consuming the stored delivery snapshot (D), and an interrupted import. |
| G9 Localhost / launch code | **PARTIAL** | Real process started with `ASPNETCORE_URLS=http://0.0.0.0:5098`: refused at startup, no listener opened. Running listeners only on `127.0.0.1`/`::1`. Launch-code folder ACL: owner + SYSTEM, protected. The code appears in no log. `g9` 13/13 runtime negatives (wrong code, foreign origin, cookie HttpOnly + SameSite=Strict, anonymous REST/GraphQL, cross-origin write, missing CSRF marker, Host spoofing, extension origin, revoked session). Remaining: the `--urls`, `Kestrel__Endpoints`, `[::]` and LAN-IP variants were denied by the local permission policy and are covered in-process only; no second Windows account was available (none created). |
| G10 Tester package | **PARTIAL** | Local replica of the pipeline package rebuilt from the final source: API + Web Release publish 0 errors, migration integrity 59 migrations, manifest `sourceCommit 5da3c952` (846 files; product code identical to `04845c9b`), no developer settings or absolute developer paths. Untouched short-path copy: `validate-package.ps1` exit 0; `start-local.ps1 -ValidateOnly -SkipContainers` exit 0 (pipeline smoke test); `-ValidateOnly` with Podman exit 0; `-ValidateOnly` with neither Podman nor Docker exit 2 with a plain explanation (was a false "passed"); busy port exit 3 (was 0); the only file written into the package by the run is `logs/launcher.log`. Open risks BQA-016/017 (see gap register). No clean PC available: `docs/audits/birknext-tester-package-clean-pc-checklist-2026-11-07.md` records what must run there. |

Accessibility regression on the changed UI (System Settings → Git Sources sign-in setup): axe-core 0 serious/critical, visible keyboard focus on
the client ID field (same styles as the other new controls). The other fixes changed no markup.

**Overall: PARTIAL.** G6 now passes; G4, G7, G9 and G10 keep the exceptions stated above. Nothing is claimed beyond them.

## Final remediation — 2026-11-07 (HEAD `60671884`)

Earlier sections are unchanged. Master summary: `birknext-final-remediation-summary-2026-11-07.md`. Gates: G1, G2, G3, G5 (8,463/8,463),
G6, G7 (Delivery → Release Readiness 16/16 in the browser + chain test; interrupted-import tests) and G8 **PASS**; G4 **PARTIAL** (Downloads
dependency removed; 4,697 passed / 11 skipped / 0 failed in partitions 1–5, 6a, 6b at `13c7821c`; partition 6c not run — memory protection);
G9 **PARTIAL** (HTTP_PORTS binding gap fixed in-process; runtime variants denied by permission policy; no second account); G10 **PARTIAL**
(package rebuilt and validated; no clean PC). Git Sources: local setup VERIFIED (browser 34/34); real Microsoft sign-in and Azure DevOps
access NOT VERIFIED (external app registration required). **Overall: PARTIAL.**
