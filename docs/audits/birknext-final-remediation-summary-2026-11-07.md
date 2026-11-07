# BirkNext Final Remediation Summary — 2026-11-07

> Current authoritative status, release decisions and sign-off: `birknext-audit-reconciliation-and-signoff-2026-11-07.md`.

Branch `008-traceability-first`, HEAD **`60671884`** (`606718846a1640d6cac867b0f90e50961377fecd`). Local commits only — nothing pushed or deployed.
No external system (TT02, m2lbdev, Altinn, MU, MM, Bufdata, Azure, Azure DevOps, Microsoft sign-in) was contacted; browser runs abort every
non-localhost request. The user's stack on :5000/:5173, their PostgreSQL data, `appsettings.Local.json` and `testdata/` were not touched. Writable
checks ran on the isolated copy (`birknext_g67`, API :5099, web :5199). Earlier report sections are preserved; this file is the master summary.

## 1. Final gate matrix

| Gate | Status | Evidence (details below) |
|---|---|---|
| G1 Source baseline | **PASS** | HEAD recorded per batch; build worktree content compared to HEAD by blob hash before each build/test batch. |
| G2 Backend build | **PASS** | Release publish of the API at `60671884`, 0 errors (package build). |
| G3 Frontend build | **PASS** | Release publish of the web app at `60671884`, 0 errors; the packaged bundle is byte-identical to the browser-verified one. |
| G4 Backend regression | **PASS** | Complete run at final HEAD `ffb75ab1` (all six partitions incl. 6c): **6,011 passed, 0 failed, 19 skipped** (opt-in external tests); 4,716 of 4,716 discovered methods executed — see §11. (Earlier the same day: PARTIAL, partition 6c interrupted by memory protection.) |
| G5 Frontend regression | **PASS** | **8,463 / 8,463** at `60671884`. |
| G6 XML fixture save/reload | **PASS** | 22/22 (2026-11-07 gap closure); no XML/persistence code changed since. |
| G7 Cross-module workflows | **PASS** | Delivery → Release Readiness: backend chain test + browser 16/16; interrupted import: 4 failure-injection tests; A–F from the gap closure. |
| G8 Accessibility (scope) | **PASS (defined scope)** | Defined scope: 18-page axe/keyboard/390 px check (2026-11-06) plus the Git Sources re-scan after its UI change (0 serious/critical). Broader WCAG assurance not verified — see the reconciliation report §7. |
| G9 Localhost security | **PARTIAL** | HTTP_PORTS/HTTPS_PORTS binding bypass found and fixed (in-process); runtime starts of the widening variants were denied by the local permission policy; no second Windows account. |
| G10 Tester package | **PARTIAL** | Rebuilt from `60671884` and validated from a clean copy; launcher fixes verified on it. **No clean PC available — clean-PC acceptance NOT RUN** (checklist: `birknext-tester-package-clean-pc-checklist-2026-11-07.md`). |

**Overall: PARTIAL** — G9 (permission/second account) and G10 (clean PC) need resources this session did not have; G4 passed on the final
source (§11). No gate is claimed beyond its evidence.

## 2. Defects fixed in this pass (each reproduced first)

| Commit | Defect | Before → after |
|---|---|---|
| `503df79c` | **G4 / BQA-018** — mandatory test read `Downloads\M2LB (2).zip`; five more read Downloads and passed silently when absent. | Repository-owned synthetic M2LB-shaped estate (10 test projects incl. the 5 named ones); real-archive tests behind `BIRKNEXT_REAL_ARCHIVE_DIR`, otherwise **Skipped with the reason**. |
| `503df79c` | **BQA-019 (new)** — `DefinitionCount` grouped by project *name*: two projects with the same name each reported the combined total. | Counted per project file (copied-project test red → green). |
| `a6ede4c2` | **BQA-001 (narrowed)** — `ASPNETCORE_HTTP_PORTS`/`HTTPS_PORTS` (.NET 8) bind every interface when no URL is set and were not checked. | Treated as `scheme://*:port` → startup refused unless `Hosting:AllowNonLoopbackBinding`. 3 cases red → green; 31/31 security tests. |
| `4edc0885` | **BQA-016** — packaged launcher could not detect the backend port, so a busy :5000 surfaced only after start. | One resolved backend address (launch profile / ASPNETCORE_URLS / appsettings Urls / Kestrel default) for display, preflight and launch; busy → exit 3 before the database; invalid address → exit 2. |
| `4edc0885` | **BQA-017** — fixed sleep after `compose up`. | Bounded `pg_isready` inside BirkNext's own container (compose project + service labels): Ready / TimedOut / Failed; real Podman run: missing → Failed, fresh → Ready (attempt 2), stopped → Failed, 1 s → TimedOut. |
| `dce2277a`, `60671884` | **Git Sources not usable** (see §8). | Six-step setup, settings applied without restart, stale-test detection, browser 34/34. |

`13c7821c` added tests only (no product defect found): G7 chain and interrupted import.

## 3. Consolidated BQA register

| ID | Class | Status | Remaining |
|---|---|---|---|
| BQA-001 | Conditional security risk | **PARTIAL** (HTTP_PORTS gap fixed) | Runtime start of `--urls`/Kestrel/`[::]`/LAN/HTTP_PORTS variants denied by permission policy — in-process only. |
| BQA-002 | Confirmed defect | **FIXED** | Cross-account read attempt NOT RUN (no second account). |
| BQA-003 | Verification gap | CLOSED | — |
| BQA-004 | Verification gap | **CLOSED** | G7 complete for the defined workflows. |
| BQA-005, 006 | Verification gap | CLOSED | — |
| BQA-007 | Verification gap | **CLOSED** | Interruption before/at/after the save, restart and concurrent commit tested; no product change needed. |
| BQA-008 | Accepted limitation | ACCEPTED DESIGN BOUNDARY | Single-user local deployment. |
| BQA-009–012, 014, 015 | Confirmed defects | FIXED | — |
| BQA-013 | Observation | OBSERVATION | Unchanged (test-harness navigation). |
| BQA-016, 017 | Confirmed launcher gaps | **FIXED** | Docker path verified with simulated commands only (Docker not installed). |
| BQA-018 | Test defect (environment dependency) | **FIXED** | Real archives are opt-in acceptance runs. |
| BQA-019 | Confirmed defect (new) | **FIXED** | — |

## 4. G4 — backend results and count reconciliation

Accounting is per **result row** (TRX `UnitTestResult`): one row per `[Fact]` and one per `[Theory]` data row. `--list-tests` lists **methods**
(theory rows collapse into one name). At `c10988d4`: 6,000 result rows (5,986 passed, 13 skipped, 1 failed) = 4,697 distinct methods, all
discovered methods executed, no method in two partitions; 16 display names repeat inside one partition (theory rows with equal display
text), not double execution. Excluded: nothing besides the opt-in categories (now visible as Skipped). At `13c7821c` partitions 1–5, 6a, 6b:
4,697 passed, 11 skipped, 0 failed; **6c NOT RUN**. Opt-in real-archive run (Downloads as the explicit folder): 4 passed, 1 skipped (absent),
1 failed — the replaced `M2LB (2).zip` lacks `M2LB.Person.Contract.Tests` (archive drift, expected under opt-in).

## 5. G7

- **Delivery → Release Readiness**: Spec-Kit ZIP → Delivery preview/commit → fresh-context read → interpretation records the Delivery
  snapshot reference → assessment → freeze → newer import (second feature) → re-interpretation. Live view follows the newest interpretation;
  the frozen report (and its stored JSON) and the first interpretation are unchanged; an unknown snapshot id records nothing. Browser
  (`g7rr`, 16/16): Snapshot Registry resolves the stored snapshot; scope Ready from it; functional execution Not assessed; decision
  "Blocked by governance" (an open human-review identity conflict, left untouched) — never Ready from a snapshot alone.
- **Interrupted import**: cancelled mid-analysis (nothing saved; stage survives a restart; retry creates exactly one), crash after the save
  (retry reuses, no duplicate), failed save of a new version (previous stays current, error kept, retry adds one), two concurrent commits (one).

## 6. G9 matrix

| Check | Result |
|---|---|
| `0.0.0.0` via ASPNETCORE_URLS, real process | PASS (refused, no listener) — 2026-11-07 gap closure |
| urls / Kestrel endpoints / `[::]` / LAN / mixed / HTTP_PORTS / HTTPS_PORTS | PASS in-process (TestServer); runtime **NOT RUN** (permission policy) |
| Launch-code ACL, no secret in logs/responses, session/CSRF/Host/extension/revocation | PASS (13 runtime checks, gap closure) |
| Second Windows account | NOT RUN |

## 7. G10 package

Rebuilt from `60671884`: preflight tests 0 failures (23 new cases), 59 migrations, manifest 846 files with `sourceCommit 60671884`, no
developer paths, no sign-in settings file and Microsoft sign-in off in the package; clean copy: `validate-package.ps1` 0,
`-ValidateOnly -SkipContainers` 0, `-ValidateOnly` without Podman/Docker 2, busy :5000 → 3 with the address named. Git Sources settings are
saved under the backend's own `App_Data` (writable, checked by the launcher preflight). Clean PC: **NOT RUN**.

## 8. Git Sources (System Settings)

**Why earlier attempts did not help.** (1) The user's running stack was started at 00:54 with `dotnet run --no-build` from the main
repository's existing build, older than `73b9d9e8` (02:33); builds happen only in an isolated worktree, so the running page never had the
setup form — the screenshot is the old administrator checklist. (2) `73b9d9e8` added a sign-in form that wrote `appsettings.Local.json`,
but every Azure/Git singleton and the MSAL client read `IOptions` once at startup, so saved values needed an API restart. (3) The page
remained a checklist of configuration keys without one next action. (4) `AzureEnvironment:Enabled=false` / `ClientId` missing persist
because no app registration ID was ever entered — that value can only come from a Microsoft Entra app registration.

**What BirkNext now does** (`dce2277a`, `60671884`): six revisitable steps with every locally manageable field (GS-01…GS-15); values saved to
`App_Data/microsoft-sign-in.json` (non-secret, atomic, never `appsettings.Local.json`) and **applied without restart**; a sign-in made
with a previous client/tenant no longer counts; a test made with other settings is **stale**; Test Git connection only after sign-in and an
organization; discovery only after a current passed test; import and fetch-latest through Import Project's existing preview → review →
commit (read-only). Health line per area (Git Sources / Entra / Azure DevOps) beside System Settings' own "No failures".

**Verified**: backend 181, frontend 101 component tests, browser 34/34 (real backend: unconfigured, incomplete, invalid input, saved
synthetic configuration, reload; mocked API responses: signed in, test, denied, discovery, import hand-off, restart required, stale,
expired). **Local Git Sources setup: VERIFIED.** **Real Microsoft sign-in: NOT VERIFIED — external Entra app registration required.**
**Real Azure DevOps access: NOT VERIFIED.**

**Administrator actions (external)**: register a public client (Mobile and desktop applications, redirect `http://localhost`, public client
flows on for device code, no secret); add delegated Azure DevOps `user_impersonation`; grant admin consent if the tenant requires it; send
the Application (client) ID and tenant; give the user Azure DevOps access (Basic/Stakeholder, repository read). The exact text is on the
page under "Microsoft Entra administrator setup" with Copy.

**What the user must do**: rebuild and restart their own stack (it runs a build from before these commits), then follow steps 1–6.

## 9. Remaining risks and external dependencies

- ~~Partition 6c of the backend suite on the final source~~ — historical: completed in the final run (§11), G4 PASS.
- Runtime binding variants (permission policy), second Windows account, clean PC.
- Real Entra registration and Azure DevOps access for Git Sources.
- Docker path of the readiness check only simulated.

## 10. Local commits (this pass)

`503df79c` G4 fixture/gate · `a6ede4c2` BQA-001 HTTP_PORTS · `4edc0885` BQA-016/017 launcher · `13c7821c` G7 tests · `dce2277a` Git Sources
live settings + guided steps · `60671884` Git Sources six-step setup · this report.

## 11. G4 complete run on the final source (user-requested, 2026-11-07)

Source: HEAD `ffb75ab1` before and after the run; the build worktree's 3,482 tracked source files (`AIAssisted/`, `scripts/`, docs excluded)
byte-identical to HEAD (blob hashes); backend tests rebuilt from it. Existing memory-safe partitions, foreground, one test host at a time,
1.5 GB floor — never reached (lowest free 3.3 GB); no safeguard disabled.

| Partition | Passed | Skipped | Failed | Peak memory |
|---|---|---|---|---|
| 1 | 983 | 2 | 0 | 3.7 GB |
| 2 | 1,056 | 5 | 0 | 3.0 GB |
| 3 | 806 | 0 | 0 | 0.6 GB |
| 4 | 649 | 1 | 0 | 1.2 GB |
| 5 | 520 | 3 | 0 | 0.8 GB |
| 6a / 6b / 6c | 138 / 550 / 1,309 | 0 / 0 / 8 | 0 | 0.8 / 0.4 / 2.1 GB |
| **Total** | **6,011** | **19** | **0** | |

Reconciliation: 6,030 result rows (facts + theory rows) = 6,011 + 19 + 0; `--list-tests` discovers 4,716 methods, all 4,716 executed, none
discovered-but-not-run, none in two partitions; 16 identical full names are theory rows within one partition. The 19 skips are opt-in
external tests, each reported as Skipped with its reason: 7 real-archive acceptance tests (`BIRKNEXT_REAL_ARCHIVE_DIR` / generated-documentation
archive), 5 live M2LB dev tests (network), 6 real ZAP container tests, 1 real headless Edge test — excluded by the no-external-systems rule,
not failures. Frontend 8,463/8,463 at `60671884` still applies (only documentation changed since). **G4: PASS.**
