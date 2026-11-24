# BirkNext — Stabilization, Full Regression, Startup Reliability and Data Safety (2026-11-24)

Branch `008-traceability-first`, on top of the integration baseline 9f1d9454 (Manual Testing interpretation, FR traceability, test design).
No new feature set; this pass only makes the baseline reliable.

## Acceptance criteria

| Criterion | Result |
|---|---|
| The complete backend suite finishes | **Met** (7 317 tests, 12–14 min, twice; never reaped). **Not green:** 11 failures per run from one recurring mid-run stall whose root cause is not yet found; every failing test passes alone. See *Backend regression*. |
| BirkNext starts reliably without manual intervention | **Met.** Root cause found and fixed; `scripts/start-local.ps1` starts the API again with no workaround and no new Defender block. |
| Local Data Reset cannot accidentally destroy plans | **Met.** Every reset scope keeps manual test plans; only an explicit, count-checked, backed-up opt-in removes them. |
| Switching specifications no longer causes a long UI freeze | **Largely met.** Longest freeze 14.7 s → 6.0 s on a first visit to the large SkoleService specification, 7.2 s → 2.8 s switching back. The remaining 6 s is mostly the first WASM parse of a 90 KB specification (see *Limitations*). |

## P1 — Startup reliability

**Root cause.** `BirkNext.Api.exe` was not damaged or locked: Microsoft Defender Attack Surface Reduction rule
`01443614-CD74-433A-B99E-2ECDC07BFC25` — *Block executable files from running unless they meet a prevalence, age, or trusted list criterion* —
blocked it (event 1121 in *Microsoft-Windows-Windows Defender/Operational*, process `dotnet.exe`/`powershell.exe`, path the apphost). Every
rebuild produces a new, unsigned apphost with zero prevalence, so it is denied (also for reading: "Ingen tilgang"). The DLL is not an
executable, which is why `dotnet BirkNext.Api.dll` kept working.

**Fix (no security control weakened, no exclusion).** `BirkNext.Api.csproj` sets `UseAppHost=false` when no RuntimeIdentifier is given.
Developer builds therefore have no apphost, and `dotnet run` (what `start-local.ps1` uses) runs `dotnet exec BirkNext.Api.dll` through the
signed dotnet host. The Tester Package publish (`-r win-x64 --self-contained`) sets a RuntimeIdentifier and still produces its
`BirkNext.Api.exe`. Side benefit: the port-cleanup guard in `start-local.ps1` only ever recognised `dotnet` processes as BirkNext's own.

**Verified.** Backend restarted with the unchanged `start-local.ps1 -SkipContainers -BackendOnly`: process `dotnet exec …\BirkNext.Api.dll`,
API answers on :5000; no ASR event since. The DLL fallback is now simply the normal path.

## P1 — Local Data Reset protects manual test plans

**What was wrong.** Every scope except *Delivery Management only* emptied `test_plan_drafts`, `test_plan_draft_revisions`,
`test_plan_snapshots`, `manual_test_runs` and `manual_run_attachments` — without listing them in the plan the tester reviews. That is how the
tester's plans were removed by a reset earlier on 2026-11-24. The legacy `POST api/admin/reset-local-database` (confirmation `RESET`) did the
same without any plan.

**Now.**
- `LocalDataResetCoordinator.TestPlanEntityTypes` are kept by every scope (and by the legacy endpoint), using the same keep-tables mechanism as
  *Reset project data, keep settings* (AdminService refuses if a kept table depended on an emptied one; a test pins that none does).
- The reset plan states the count: "✓ N manual test plans will be kept" and lists them as preserved.
- Only **Reset everything** offers «Also remove manual test plans» (unticked by default; cleared when the scope changes). The request must
  carry the count the tester reviewed (`ExpectedTestPlans`); a different current count refuses the reset. Before anything is deleted, every
  row of the five tables (logically deleted plans included) is written to `App_Data/reset-backups/test-plans-before-reset-<utc>-<id>.json`
  with write-through; if that fails, the reset is refused and nothing is deleted.
- Contract updated: `docs/local-data-reset.md`.

**Verified.** 16 new backend tests (`TestPlanResetProtectionTests`) + 7 new panel tests; 80 reset-related backend tests pass. Read-only check
in the real browser (System Settings → Maintenance → Reset data…, plans read for each scope, then Cancel; no reset request sent): your
«Testplan 2026-11-24» is shown as kept in all three full scopes.

## P2 — Specification-switch freeze

Measured on the real M2LB workspace (429 revisions), switching SkoleService's `docs/avhengigheter-bestilling.md` ↔
`specs/001-skoleservice/spec.md` in *Finn og forstå* with a browser long-task observer (`spec_profile3.py`, restores the selection):

| | Before | After |
|---|---|---|
| Switch to the 65-requirement spec: total / longest task | 26.8 s / 14.7 s | 10.5 s / 6.0 s |
| Switch back: total / longest task | 13.2 s / 7.2 s | 6.5 s / 2.8 s |
| Review-context rebuild on switch-back | 3.9 s | 0.7 s (0 models parsed) |
| Separate auto-save task after a switch | 4.7 s | gone (yields every 40 ms) |

What was unnecessary, and what changed:
1. **All five semantic models were re-parsed** (constitution ~3.5 s) although only the specification changed. `ReviewContextProvider`
   now remembers the last three models per role keyed by their exact text (the plan also by the supporting plan documents it reads) and
   parses only what changed, yielding to the browser between models. Models are built from their own text and never mutated, so reuse is exact.
2. **The pack inventory recomputed cross-references (1.8 s) and SHA-256 fingerprints (0.6 s)** of ~50 unchanged documents on every rebuild.
   Both are now cached by the identity of the (immutable) document text.
3. **Each rebuild loaded the explorer scope five times** (~1 s, three rebuilds per switch, two of them "unchanged"). New
   `IArtifactExplorerContext.GetStatesAsync` resolves all roles from one scope load (~0.28 s).
4. **The first auto-save after a page load hashed every document body (~5 MB) in one task.** It now warms its address cache in slices,
   giving the browser its thread every ~40 ms, then builds the save from one consistent lifecycle snapshot. The stored revision fingerprint
   is deliberately *not* trusted as the body address (a body the backend already holds would never be re-verified).

Source selection is unchanged: the signature-based "inputs unchanged" skip, the role resolution and the pack inventory are the same; tests
pin that a memoised context equals a freshly built one.

## Backend regression

(Frontend full suite after all changes: **9 563 passed, 0 failed**.)

Run: `dotnet test --no-build --blame-hang --blame-hang-timeout 15m -- xUnit.MaxParallelThreads=2 RunConfiguration.MaxCpuCount=1`, memory
sampled every 30 s.

| Run | Passed | Failed | Skipped | Duration | testhost peak / lowest free memory |
|---|---|---|---|---|---|
| 1 (default thread pool) | 7 287 | 11 | 19 | 12 min 41 s | 6.9 GB / 3.9 GB |
| 2 (`DOTNET_ThreadPool_ForceMinWorkerThreads=0x40`) | 7 287 | 11 | 19 | 14 min 25 s | 7.0 GB / 5.8 GB |

The suite now **finishes** (earlier today it was reaped for memory). The 11 failures are **not dismissed — but their root cause is not yet
established.** What is known:

- **They recur.** 7 tests failed in both runs (`ApiReviewEngineTests.CI_DEF_3…`, `PerformanceRequestResponseTests.GraphQL_variables…`,
  `LocalDeploymentBoundaryTests.Local_use_is_unchanged(127.0.0.1)`, `IntegrationTemplateApplyGuardTests.ATrustedEnvironmentRecord…`,
  `LocalTrustedExecutionTests.TheAuthenticatedPath…`, `ActiveEventAuthorizationTests.IntegrationCatalogWrites…`, plus TLS/performance tests
  that also failed in the morning run); the rest vary.
- **They are one event, not 11 defects.** Dozens of unrelated tests that start ~215–245 s into the run all take ~3 minutes; the failing ones
  are in-memory `TestServer` requests that time out after 100 s inside the app pipeline (e.g. `GET /api/performance-tests/providers`), a
  connect callback, TLS probes. ~250 tests are in flight at that moment (xUnit's `MaxParallelThreads` does not cap async tests).
- **Every failing test passes in isolation** (the 11 classes: 200/200), and the Controllers + PerformanceTests + ApiReviewEngine subset passes
  together (648/648).
- **Ruled out:** memory (≥ 3.9 GB free throughout) and plain worker-thread starvation (a raised minimum changed nothing). None of today's
  changes touch these areas; two of them already failed in the morning run on 9f1d9454.
- **Leads, unverified:** long Postgres migration/DB tests running in the same window (`DatabaseMigrationLockTests` 224 s,
  `SourceSchemaArtifactsRepairTests` 262 s) holding locks the app hosts wait on; and the unbounded `process.WaitForExit()` after the bounded
  async wait in `SystemProcessRunner` / `NativeProcessExecutionRuntime`, which can block when a child process (podman CLI) keeps the
  redirected pipes open.
- **Next diagnostic step:** one more full run with `dotnet-stack` (or a `--blame-hang-dump-type mini` dump read with `dotnet-dump`) taken at
  ~240 s to see what the stalled requests wait on. These tools are not installed here and need a NuGet download, so this was not done.

## P2 — Validating test recommendations with people (prepared, not done)

People must do this part; BirkNext cannot. `AIAssisted/docs/test-design-review-sheet-2026-11-24.csv` (UTF-8, semicolon — opens in Excel)
holds **20 representative scenarios** from the 57-scenario benchmark: all 5 Person, all 5 SkoleAdapter and 10 SkoleService scenarios chosen to
cover every recommended approach (Manuell, Manuell + teknisk, Integrasjon / E2E, Teknisk, Automatisering anbefalt) and every proposal kind.
Each row has the verbatim Given/When/Then, BirkNext's provisional approach, verification surfaces, proposals and open questions, and empty
columns for the tester: agree (Ja/Delvis/Nei), correct approach, missing proposals, wrong or useless proposals, would you use it, reviewer,
date, notes. Agreed results go back into `test-design-benchmark.json` → `humanReview`. Until then the benchmark establishes structural
safety (no invented requirements or expected results, no lost negatives), **not** that BirkNext chooses the right approach.

Suggested session: 2–3 experienced manual testers, ~60–90 min, each reviewing the sheet independently before comparing; count agreement
per approach, and treat any "Nei" on an approach as a rule to fix before adding more intelligence.

## Deferred deliberately (not in this stabilization)

- **FR identifier casing (FR-027a shown as FR-027A).** Agreed that display must keep the source spelling while comparison uses a canonical
  key. `RequirementReferenceParser.Normalize` is currently both, in ~26 call sites (specification explorer, tasks, traceability, test evidence,
  catalog ids). Plan: keep `Normalize` as the comparison key only; add a `RequirementId(Display, Key)` value carried from extraction; render
  `Display`; export both; migrate call sites module by module with tests pinning that FR-015 and FR-015a stay distinct and that historical links
  (stored upper-case keys) still match.
- **Norwegian-only specifications.** Agreed direction: one language-independent canonical rule representation with Norwegian and English
  input parsers feeding the same engine, rather than translating before interpreting.
- **Restoring plans from a reset backup.** The backup is complete JSON, but there is no restore action yet; restoring is a manual/developer step.

## Limitations

- The remaining 6 s freeze on a *first* visit to a large specification is mostly `SpecExplorerService` parsing ~90 KB on the WASM interpreter
  (~3.3 s). Revisits are free (memoised); making the first parse faster or moving it off the UI thread is separate work.
- Two cheap "inputs unchanged" rebuilds still run after a switch (~0.3 s each) because two events announce the same change.
- When test plans are explicitly removed, manual-run attachment *files* on disk are not deleted (only their rows, which are in the backup).
- The reset protection is verified by unit tests and a read-only browser check; no destructive reset was run against the real database
  (by instruction).
- A blocked, unreadable `BirkNext.Api.exe` from before the fix may remain in `bin/Debug/net8.0`; it is no longer built or used.
- The specification profile ran headless Chromium on this machine; absolute times vary with the machine and load.
