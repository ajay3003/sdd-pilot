# BirkNext Verified Gap Register

**Execution baseline:** `008-traceability-first` at `546584f1`; **final observed HEAD:** `63ee0980` (2026-11-06). The intervening topology worklist commit was not verified. Findings retain BQA IDs. This register distinguishes demonstrated defects from conditional risks and audit coverage gaps. No production fixes were made.

| ID | Priority / class | Finding and evidence | Impact / confidence | Recommended follow-up | Verification status |
|---|---|---|---|---|---|
| BQA-001 | P2 conditional risk | Startup validates `urls` and `Kestrel:Endpoints:*:Url`; non-loopback values require the explicit `Hosting:AllowNonLoopbackBinding` opt-in (`AIAssisted/backend/BirkNext.Api/Program.cs:84-94`). Unit test source checks loopback and explicit opt-in (`AIAssisted/backend/BirkNext.Api.Tests/Controllers/LocalSessionSecurityTests.cs:39-57`). Other possible host bind sources/effective socket configuration were not exhaustively verified. | Accidental LAN exposure if an unchecked supported setting can widen binding; no bypass demonstrated. **Medium confidence.** | Verify full supported host configuration precedence and actual loopback socket address in a disposable local launch. | Partial; no non-loopback listener opened. |
| BQA-002 | P2 conditional risk | `LocalSessionService` creates `App_Data/local-session` and stores launch-code path without setting explicit Windows ACLs (`AIAssisted/backend/BirkNext.Api/Services/LocalSecurity/LocalSessionService.cs:33-45,109-126`). | Other-account access depends on install directory inheritance and packaging. No actual code value was inspected; no unauthorized access demonstrated. **High confidence in absent explicit ACL code; low confidence in exposure.** | Verify package/install ACLs using disposable package and separate Windows accounts; document supported install model. | ACL runtime/package check not performed. |
| BQA-003 | Test coverage gap | Existing audit reported “No test matches” from stale assemblies. This pass rebuilt API test source and ran focused classes: 90 passed. Full test suite not executed. | Selected security/import behavior now has executable test evidence; broad regression remains unknown. **High confidence.** | Execute full current-source backend partitions and frontend suite once the Web build guard/worktree state is resolved. | Partially closed. |
| BQA-004 | Test coverage gap | No current pass exercised complete producer→consumer flows using a synthetic database and application services. | Evidence freshness, binding, and readiness across modules remain unknown; no new product defect established. **High confidence.** | Add/execute synthetic integration journeys with run/source/snapshot identities preserved end to end. | Not tested. |
| BQA-005 | Test coverage gap | No local browser session or viewport/keyboard/accessibility inspection was performed. UI and tests for Integration Topology changed concurrently during the pass. | Usability and visual regressions cannot be assessed. **High confidence.** | Re-run browser journeys at requested viewports after concurrent UI changes are stable. | Not tested. |
| BQA-006 | Regression coverage gap | HEAD is `546584f1`; selected API tests ran, but each of the twelve hardening commits was not independently revalidated through browser/full regression coverage. | Historical completion claims are not current runtime proof. **High confidence.** | Map changed files/commits to focused regression tests and execute current suite. | Partial. |
| BQA-007 | Previously reported; current source/test evidence | Import service serializes staged commits and compares archive identity/current snapshot (`AIAssisted/backend/BirkNext.Api/Services/ProjectImport/ProjectImportService.cs:95-105,127-143`). Import service tests were included in the focused 90-pass partition (`AIAssisted/backend/BirkNext.Api.Tests/ProjectImport/ProjectImportServiceTests.cs:212-240`). | Duplicate/import idempotency path has focused test evidence. Interrupted operation and relational restart semantics remain unverified. **High confidence for bounded path.** | Add persistence-backed interruption/retry test if the supported store requires durable handoff. | Partial verified working. |
| BQA-008 | P2 conditional design/isolation risk | Service sets `_currentUserId = "default-user"` (`AIAssisted/backend/BirkNext.Api/Services/WorkspacePersistenceService.cs:14-15`); controller lists using that fixed identity (`AIAssisted/backend/BirkNext.Api/Controllers/WorkspacePersistenceController.cs:127-142`); ID-based rename/duplicate/delete actions forward the client workspace ID (`151-205`). | Shared/multi-user isolation is not established. This does not prove cross-user access in an explicitly single-user local deployment. **High confidence in fixed identity; deployment support unknown.** | Declare the supported single-user boundary or bind authenticated principal to owner predicates on every read/write. | Runtime cross-principal test not performed. |
| BQA-009 | P2 build/verification blocker | Web project intentionally fails `PrepareForBuild` if a physical `wwwroot/BirkNext.Web.styles.css` exists (`AIAssisted/frontend/BirkNext.Web/BirkNext.Web.csproj:17-23`). That guard blocked normal test project reference build in this worktree. | Prevents clean current-source frontend/test verification when generated artifact remains; not classified as product defect because artifact may be stale/ignored and was preserved. **High confidence for observed blocker.** | Establish an isolated clean build workspace or safely clean generated artifact under normal build procedure; then rebuild Web/tests. | Build blocked in this worktree; API build succeeded. |

## Not promoted to findings

- The API build’s 175 warnings are recorded as build output; no warning was promoted to defect without evaluating its semantics.
- No false-readiness defect was confirmed because no application workflow/browser test ran.
- No new security exploit, cross-project leak, data corruption, or race condition was reproduced.
- Historical MH findings remain tracked by the prior hardening report; they are not reclassified as new findings here.

## Verification completion — current source `7410e179` (2026-11-06)

The sections above are the **historical** audit at `546584f1` (final observed HEAD `63ee0980`). They are kept unchanged. This section records the
completion run against the current source. Source state verified: branch `008-traceability-first`, HEAD `7410e1793be162bd589762c9e65749d0f8621c46`; the isolated build worktree's
3,541 source files (`AIAssisted/`, `scripts/`) are byte-identical to that commit (blob hashes compared; only 7 `AIAssisted/docs/*.md` reports absent).
Uncommitted user files left untouched: `AIAssisted/backend/BirkNext.Api/appsettings.Local.json`, `AIAssisted/testdata/`. No push, no deployment,
no external system (M2LB, TT02, Altinn, MU, MM, Bufdata, Azure DevOps) contacted; browser runs abort every non-localhost request.

### Current status (the register above is historical)

| ID | Current status | Evidence at `7410e179` |
|---|---|---|
| BQA-001 | **PARTIAL** | Binding-guard unit tests pass; both running stacks observed on loopback only (`127.0.0.1`/`::1` for :5000, :5099, :5173, :5199). Live start with a widened URL **NOT RUN** (permission policy denied it). |
| BQA-002 | **FIXED (confirmed defect)** | `37b918f7`: owner-only ACL; tests fail on old code; runtime repair of an existing install observed. The user's own dev install is repaired the next time its API is rebuilt and started. |
| BQA-003 | **CLOSED** | Full backend (5,972 / 13 skipped / 1 intermittent) and full frontend (8,444) executed at current source. |
| BQA-004 | **PARTIAL** | See cross-module report: supported local portions verified; write-heavy end-to-end flows not run. |
| BQA-005 | **CLOSED (within scope)** | Browser journeys, 390 px layout, keyboard focus, axe-core; screen reader not tested. |
| BQA-006 | **CLOSED** | Every commit up to `7410e179` is covered by the final full frontend run; backend unchanged since `37b918f7`, which the full backend run covers. |
| BQA-007 | **PARTIAL (unchanged)** | Import service tests pass (in the 92-test set and the full run); interruption/restart still not exercised. |
| BQA-008 | **ACCEPTED DESIGN BOUNDARY** | Single-user local deployment: loopback-only binding + per-installation launch code (now owner-only) + session cookie; the fixed `default-user` owner is consistent with that model. Not a supported multi-user server; would need principal-bound ownership before any shared deployment. |
| BQA-009 | **FIXED** | `fa521a9d` (see verification report). |
| BQA-010 (new) | **FIXED** | `7410e179` concatenated status text on Skolenærvær. |
| BQA-011 (new) | **FIXED** | `8cc2139e` WCAG AA contrast (12 axe nodes on 4 pages). |
| BQA-012 (new) | **OPEN — nondeterministic** | `ReadinessStatusIntegrityTests.AccessibilityPreferenceTrue_OverridesLegacyFalse_AndReachesDependencyProbe` is intermittent (failed in the full run at `37b918f7`, passed in the previous full run and 3/3 in isolation): `WebApplicationFactory.Dispose` → `LocalHttpsProxyService.StopAsync` waits on its gate with the host-shutdown token, which is cancelled after the test body itself ran 3 m 26 s under load; isolated runs pass in 6 s. Assertions are not weakened; needs a deterministic shutdown/timeout design. |
| BQA-013 (new) | **OBSERVATION** | Performance Test Review's engine-status query was still in flight after 45 s of network idle wait in the local verification environment (aborted by the harness's full-page navigation; in-app navigation does not abort it). |

## Gap closure — 2026-11-07

The register and the 2026-11-06 status table above are kept unchanged. Status changes and new findings from the gap-closure run (code at
`04845c9b` / `6e794e94`; see the verification audit for the method and isolation):

| ID | Status 2026-11-07 | Evidence |
|---|---|---|
| BQA-001 | **PARTIAL** (narrowed) | A real process started with `ASPNETCORE_URLS=http://0.0.0.0:5098` refused to start and opened no listener. The `--urls`, `Kestrel__Endpoints`, `[::]` and LAN-IP variants were denied by the local permission policy; they are covered by the in-process binding-guard tests only. |
| BQA-002 | **FIXED** (unchanged) | Runtime ACL re-checked on the isolated content root: owner + SYSTEM, protected. Separate-account read attempt not run (no second Windows account; none created). |
| BQA-004 | **PARTIAL** (narrowed) | Writable synthetic workflows A–F executed end to end on an isolated database (cross-module report). Open: Release Readiness consuming the stored delivery snapshot; interrupted import. |
| BQA-007 | **PARTIAL** (unchanged) | Duplicate import refused without confirmation (Workflow D); interruption/restart still not exercised. |
| BQA-012 | **FIXED** | `e0077f15`: root cause was the proxy's `StopAsync` awaiting its gate with an already-cancelled shutdown token. Reproduced deterministically (`HostShutdownBudgetTests`), fixed, repeated 5×; `ReadinessStatusIntegrityTests` passed in the full run. |
| BQA-014 (new) | **FIXED** | `04845c9b`: M01 Security Review results and Performance Test Review definitions/data profiles/runs/baselines created in a fresh browser were keyed to a not-yet-stored default Target Environment and vanished after a reload. Reproduced by test and in the browser; fixed; Workflow E 14/14. |
| BQA-015 (new) | **FIXED** | `6e794e94`: the launcher's `-ValidateOnly` reported "passed" with no container runtime (exit 0; now exit 2), and a busy port stopped startup with exit 0 (now exit 3). Reproduced on a copy of the packaged launcher. |
| BQA-016 (new) | **OPEN — P3 risk** | In the published package the launcher cannot detect the backend port ("Backend launch port could not be detected. Skipping backend port preflight."), so a program already on port 5000 is not reported before start; the backend then fails to bind after the database has started. Observed, not exercised end to end. |
| BQA-017 (new) | **OPEN — P3 risk** | After `compose up` the launcher waits a fixed `ContainerDelaySeconds` instead of checking that PostgreSQL accepts connections; a slow first container start can make the first backend start fail. Code reading only; not reproduced. |
| BQA-018 (new) | **OBSERVATION — environment-dependent test** | `TestEvidenceTests.RealM2lb_DiscoversItsXunitV3TestEstate_WithTheGenericProvider` reads the real archive `%USERPROFILE%\Downloads\M2LB (2).zip` (skips when absent). That file was replaced on 2026-11-07 00:57 with a version that lacks 4 of the 5 test projects the test expects, so it fails. No discovery code changed since `f1b7f041`. Not a product regression; the test depends on a file outside the repository. |
| Git Sources | **FIXED** | `73b9d9e8`: Microsoft sign-in for Git Sources is set up in System Settings instead of by editing configuration files (requested repeatedly by the user). |
