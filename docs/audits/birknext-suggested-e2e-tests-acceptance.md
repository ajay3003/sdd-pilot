# BirkNext — Intelligent suggested E2E test generation: acceptance (2026-11-10)

Branch `008-traceability-first`. Baseline `0fca2f6f`. No push, no deployment, no external system, no unattended browser against a real environment.

## 1. Environment

| Item | Value |
|---|---|
| Stack | Real BirkNext API (scratch Debug build) on `:5098`, Release-published frontend on `:5198`, disposable DB `birknext_sug_20261110` |
| Browser | **Real Microsoft Edge** (Playwright persistent context, new headless mode) with the **shipped Edge Companion build** (`browser-companion/dist/current`, rebuilt by `build.mjs` after its 149 tests). Paired **through the extension's own popup** with a pairing code from the API, and approved origin `http://127.0.0.1:5097` only. No mock companion. |
| Attended app | `ui_app.py` on `127.0.0.1:5097`. HTML equivalent to Blazor's rendering of the fixture source: test ids, labels, `ul.validation-errors` summary, a password field holding a synthetic secret, two identical "Cancel" buttons, and a cross-origin iframe from `:5096`. It keeps its own ledger of every page load and form submission. |
| Source | `Synth.Ui.zip` (v1) and `Synth.Ui-v2.zip` (heading test id `page-title` → `heading`): Razor pages + DataAnnotations model, imported through the real Project Import |
| Oracle | `oracle.json`, written by hand from the fixture source: expected suggestions per kind, expected texts and DOM counts. Together with the app's ledger. Never BirkNext's summaries. |
| Network | Every request outside localhost/127.0.0.1 aborted |

## 2. Results — final run

**Main: 31/31 PASS. After API restart: 4/4 PASS.** No external request, page error or console error.

| ID | Check | Result |
|---|---|---|
| SG-00 | The shipped Companion build loads in Edge (service worker running) | PASS |
| SG-01 | Paired through the popup; exactly one approved live page (the attended app); capability `selector-verify` reported | PASS |
| SG-02 | The imported synthetic snapshot is selectable with its UI facts (2 routes) | PASS |
| SG-03 | Generated suggestions match the oracle per kind: 8 (page opens ×2, navigation ×2, required fields, invalid input ×2, valid submission) | PASS |
| SG-04 | Generation sent nothing to the app (offline) | PASS |
| SG-05 | Valid submission: NEEDS EXPECTED RESULT and NEEDS TEST DATA (password field: credentials never generated) | PASS |
| SG-06 | Its expected result is shown as missing, never invented | PASS |
| SG-07 | Required fields: the declared "Name is required" is the expected text | PASS |
| SG-L1 | Suggestions: no page-level horizontal scroll at 1440 / 1280 / 1024 / 768 / 390 | PASS |
| SG-08 | The detail states that the attended page is on the suggestion route | PASS |
| SG-09 | **The real Companion verified** `data-testid="page-title"`: unique and visible (oracle ×1) | PASS |
| SG-10 | Verification clicked and submitted nothing (app ledger unchanged) | PASS |
| SG-11 | The reviewer accepts the verified locator (decision with who) | PASS |
| SG-12 | Approved into a Diagnostic Critical E2E flow; approving executes nothing | PASS (after defect SG-D1) |
| SG-13 | After approval the suggestion is covered by its own flow (no duplicate) | PASS |
| SG-14 | Required fields: Save verified unique; the validation summary is NOT assessed before submitting | PASS |
| SG-15 | Required-fields suggestion approved | PASS |
| SG-16 | Two flows: Diagnostic, source = suggested from source, tagged with suggestion and snapshot | PASS |
| SG-17 | The approved "page opens" flow passes in the real browser through the existing runner (ledger: /children/new loaded) | PASS |
| SG-18 | The approved "required fields" flow passes: Save clicked with an empty name, the summary contains "Name is required", **nothing submitted** (ledger: no submit) | PASS |
| SG-19 | Test Execution & Evidence lists both runs as Browser E2E, Passed | PASS |
| SG-20 | Verifying a form with a password field returns identity only; the synthetic secret never leaves the page | PASS |
| SG-21 | Tab moved to an unapproved origin: the Companion refused ("The approved page could not be reached; it may have been closed."); no new verification recorded | PASS |
| SG-22 | Attended page on another route of the approved origin: refused with the route to open, nothing dispatched | PASS |
| SG-23 | Changed source (page-title → heading): locator revalidation proposed for the approved flow | PASS |
| SG-24 | The approved flow is unchanged (still page-title, same version) | PASS |
| SG-25 | Verifications recorded for v1 are STALE for v2 | PASS |
| SG-L2 / L3 | Mapping and results areas: no horizontal scroll at the five widths | PASS |
| SG-26 | Execution Results area: both approved tests Executed | PASS |
| SG-Z | Clean | PASS |
| SG-R1 | After restart: approved suggestions still approved and covered | PASS |
| SG-R2 | Approved flows and their run history persist | PASS |
| SG-Z | Clean | PASS |

## 3. Run history

| Run | Main | Restart | What failed and why |
|---|---|---|---|
| 1 | aborted | aborted | Harness: the popup's backend field sits in the collapsed "Advanced" section |
| 2 | 26/31 | 4/4 | SG-12/15 were product defect SG-D1 (the refresh after approval overwrote the "Approved as…" message). SG-17/18 were a harness regex: the runs passed, and the UI's label is "Diagnostic pass · browser journey only". SG-21 was a harness assumption: BirkNext keeps a live page up to its 2-minute lifetime, and the Companion refused the command, which is the safe behaviour. |
| 3 | 30/31 | 4/4 | Harness: a heredoc turned `\b` into a backspace byte in the SG-17/18 regex |
| 4 | **31/31** | **4/4** | none |

## 4. Real M2LB source

| Check | Result |
|---|---|
| Independent offline inventory (stdlib zip reader; `real-ui-oracle.json`) | **M2LB.zip:** 59 UI files, 8 routable pages, 0 static `data-testid`, 0 forms, no tsx/jsx/vue. **M2LB-language.zip:** 60 UI files, the same 8 routes. Routes: `/`, `/admin/audit-log`, `/admin/emergency-access`, `/admin/platform-versions`, `/admin/user-access`, `/person`, and the parameterised `/person/{BarnId:guid}` and `/authentication/{action}`. |
| What that means for suggestions | Only page-open and navigation suggestions apply, for the 6 parameterless routes. Parameterised routes need a real record from Test Data and are never suggested blindly. With no test ids, locators fall back to role + name or label. There are no form suggestions, because no form exists. The Altinn → Bufdata chain has no browser UI here and stays in Integration Journey / API testing. |
| BirkNext generation on the real snapshots | **BLOCKED (2026-11-10).** During the M2LB import the local Podman/WSL VM that hosts PostgreSQL stopped. The insert connection was aborted, then refused; `podman` and `wsl.exe` became inaccessible. Restarting the user's VM is a system action left to the user. No M2LB environment was contacted, and nothing was verified against a real DOM. **Superseded by §7 (2026-11-11): PASS after two fixes.** The inventory above (8 routes) was wrong; see §7.2. |

## 5. Automated tests

| Suite | Result |
|---|---|
| `SuggestedTestsTests` (backend, 6) | PASS |
| Focused backend: CriticalE2E, BrowserCompanion, SourceAnalysis, IqrSource | 594 passed, 1 skipped, 0 failed |
| `CriticalE2ESuggestionsTests` + Critical E2E page tests (bUnit) | 104/104 PASS |
| Extension suite (`node build.mjs`, Playwright fixture browser) | 149 passed, 1 skipped, including `verify.test.mjs` 6/6 |
| Release frontend build | PASS |
| Full regression (six backend partitions, then full frontend) | §6 |

## 6. Full regression (task 6 tree)

Run sequentially: six memory-guarded backend partitions, then the full frontend suite. No timeout was changed and no test was disabled.

| Step | Result |
|---|---|
| Frontend (full suite) | **8576 / 8576 passed** |
| Backend partitions 1–6 | 6273 passed, 21 skipped, **123 failed** |
| — of which caused by the local Podman/WSL outage | **120**: Npgsql "Failed to connect to 127.0.0.1:5432", the startup `DatabaseMigrationLock`, and Testcontainers with no container engine. These tests need the PostgreSQL / container runtime that stopped during the M2LB import. **BLOCKED** until the user restarts the VM. |
| — `ManagedEdgeConnectorTests` (example.com WebSocket validation, 60 s HttpClient timeout) | environmental: passes on rerun (24/24 with the two below) |
| — `ProjectImportPipelineTests.StreamedCommitReportsRealAnalysisStagesBeforeItsTerminalResult` | **real defect caught by the regression:** the new "Analyzing UI source" stage made the stream report stage 19 of a hard-coded 18. Fixed (`StageCount = 19`) and the test updated deliberately; passes. |
| — `MigrationIntegrityTests` | environmental ("Could not query EF migrations: Failed to connect to 127.0.0.1:5432") |
| Previous full regression on the task 4+5 tree, before the outage | 6392 passed, 0 failed; frontend 8571/8571 |

The 120 environment-blocked backend tests could not be re-run here. Restart the Podman machine, then run partitions 2, 4, 5 and 6 again; they passed on the same code paths before the outage.

## 7. Post-outage recovery and final verification (2026-11-11)

Baseline `40ae60e5` on `008-traceability-first`. The sections above are kept as the historical record. Same rules: no push, no deployment, no external system. The user's services on :5000 and :5173 were not touched, and `appsettings.Local.json` was not touched.

### 7.1 Environment

| Item | Before | Action | After |
|---|---|---|---|
| WSL | 2026-11-10: `wsl.exe` "The file cannot be accessed by the system" | `wsl --status` / `--list --verbose` (read only) | Accessible. Distribution `podman-machine-default`, WSL 2, **Stopped**. The earlier error did not recur, so no WSL repair, administrator action or reboot was needed. |
| Podman | `podman info`: "Cannot connect to Podman" | `podman machine start` (the normal start; no reset, rm or prune) | Running. Warning only: the Docker API proxy pipe is unavailable, but Podman clients connect. |
| PostgreSQL | down | `podman start birknext-studio-local_postgres_1` (the container in use before; `aiassisted_postgres_1` also binds 5432 and was left stopped) | Ready after normal crash recovery ("database system was not properly shut down; automatic recovery in progress … redo done"). One WAL warning concerned replaying the already-completed drop of a task 4/5 scratch database. |
| Databases | — | read only | All 41 non-template databases present. User database `birknext`: EF history matches the code exactly in all four contexts (main 66/66 at `20261110160044_AddCompositeSourceContexts`, `delivery_reporting` 6, `canonical_interpretation` 2, `snapshot_registry` 2; 76 in all). Nothing was migrated or changed. |
| Leftovers from the outage | — | none (not ours to delete) | Per-test databases left by tests killed mid-run: `birknext_artifact_repair_*` ×5, `birknext_delivery_reporting_*` ×6, `birknext_evidence_manage_*` ×3, `birknext_migration_lock_*` ×5, `birknext_reset_c640017c5f30`. Left untouched. |
| Scratch tree | — | content comparison (line endings ignored) | 3,702 tracked backend/shared/frontend/extension files equal to `40ae60e5`; binaries newer than every source. |

### 7.2 Real M2LB suggested E2E acceptance

The archives were re-imported through the real Project Import into the disposable `birknext_sug_20261110`. No earlier M2LB snapshot in any database has UI facts (`birknext` 6, `birknext_ci2` 9, `birknext_g67` 9, `birknext_gaps` 9, `birknext_sa10` 6, all with 0 UI facts, all analysed before `40ae60e5`). User databases were only read.

**Run 1, defect SG-D2: one tag erased the whole UI surface.**
- Both archives: `pagesAnalyzed 0`, 0 suggestions, limitation "UI source could not be analysed (ArgumentException)".
- Cause: real Razor puts expressions with nested quotes in attribute values (`class="@(_x ? "a b" : "a")"`, `Message="@($"Er du … du …")"`, 5 tags in M2LB). The attribute pattern splits them into fragments that repeat a name, and `ToDictionary` (`SourceUiExtractor.cs:151`) threw. A single try/catch around the whole extraction then replaced every page with one limitation.
- Fix: the attribute map is first-wins (as in the browser), and extraction is isolated per file, so a failing file is named in a limitation and the other pages are kept.
- The test reproduced the exact real exception (`… same key … Key: role-list`) before the fix.

**Run 2, defect SG-D3: the Blazor host page was suggested as a page.**
- `wwwroot/index.html` became the route `/index.html` and a "page opens" suggestion. In a Blazor WebAssembly app that file is the shell every `@page` renders into; opening it only hands the path to the router.
- Fix: an HTML file that loads `_framework/blazor.(webassembly|web|server)` is a host page, with no route and a limitation.
- The first version of the fix missed the real file, because .NET 9 writes `blazor.webassembly#[.{fingerprint}].js`. The pattern was widened, and the test now uses that real shape (red, then green).

**Oracle correction.** The independent inventory of 2026-11-10 (8 routes) was **wrong**. Five M2LB pages begin with a UTF-8 BOM before `@page`, and Python's `^\s*@page` does not treat U+FEFF as whitespace. The corrected oracle (`ui_oracle2.py`, stdlib, `utf-8-sig`, not BirkNext) finds 13 routes: 11 parameterless, 2 parameterised (`/authentication/{action}`, `/person/{BarnId:guid}`), one Blazor host page, 0 static `data-testid`, 0 `EditForm`/`form`.

**Final run (fixed build), compared with the corrected oracle:**

| Check | M2LB.zip | M2LB-language.zip | Oracle | Result |
|---|---|---|---|---|
| Snapshot identity | `fa11b333-…` project `m2lb` | `3477bfe8-…` project `m2lb-language` | separate projects | PASS |
| UI files / pages analysed | 59 | 60 | 59 / 60 | PASS |
| Routes | 13 | 13 | 13 | PASS |
| Forms / test ids | 0 / 0 | 0 / 0 | 0 / 0 | PASS: no form or test id invented |
| Controls recorded | 245 | 240 | (not counted by the oracle) | — |
| Suggestions | 11 × page opens | 11 × page opens | 11 (one per parameterless route) | PASS: identical route set |
| Parameterised routes suggested | none | none | none (they need a real record from Test Data) | PASS |
| Navigation suggestions | 0 | 0 | 0 | PASS. The only links on routable pages are in-page fragments (`#create-role-form`) or sit on the parameterised `/authentication/{action}`. The menu uses `NavLink` components in the layout, which has no route. |
| Expected-result provenance | 7 from the page's own `<h1>` (e.g. "Revisjonslogg", `AuditLogPage.razor:8`), 4 from the `@page` route (no `<h1>`: `/`, `/admin`, `/person`, `/not-found`) | same | `<h1>` text and line numbers match the source exactly | PASS |
| Locators | `heading` role + accessible name (no test id exists) | same | — | as designed |
| Existing-test reuse | 0 reused, 11 new | same | no M2LB flow exists in the environment | PASS |
| Missing DOM prerequisites | 7 need DOM verification (heading locators), 4 ready for review (route only) | same | — | PASS |
| External traffic | 0 outbound HTTP requests in the API log; every proxy pointed at a dead port | | | PASS |

**Not done (by the brief's rules):**
- No real M2LB DOM was opened. Locators stay NEEDS_DOM_VERIFICATION until a tester verifies them through the Edge Companion in an attended, approved session.
- Nothing was approved or executed against M2LB.

### 7.3 Tests for the fixes

| Run | Result |
|---|---|
| `SuggestedTestsTests` with the new test, before each fix | red: 1 failed / 6 passed (the real `ArgumentException`; then `/index.html` as a route; then the fingerprinted host) |
| Focused backend (CriticalE2E, SourceAnalysis, IqrSource, BrowserCompanion, ProjectImport) after the fixes | 666 passed, 1 skipped, 0 failed |

### 7.4 Full backend regression after the recovery

**Run.** Because SG-D2 and SG-D3 changed product code, **all six** partitions were re-run, not only the four blocked ones (2, 4, 5, 6).
- Tree: `40ae60e5` plus the two fixes.
- Same partition script, memory guards (floor 1,500 MB, `MaxCpuCount=1`, `MaxThreads=2`) and timeouts.
- No test was excluded, skipped or weakened.

| Partition | Passed | Skipped | Failed | Peak memory |
|---|---|---|---|---|
| 1 (SourceAnalysis, Integrations, PerformanceTests) | 1031 | 2 | 0 | 750 MB |
| 2 (TargetEnvironmentDetection, TestData, LocalHttpsProxy, AltinnTesting), blocked on 2026-11-10 | 1098 | 5 | 0 | 601 MB |
| 3 (IntegrationJourneys, CriticalE2E, ApiQuality, AuthenticatedReview) | 837 | 0 | 0 | 495 MB |
| 4 (CanonicalInterpretation … AzureEnvironment), blocked on 2026-11-10 | 652 | 1 | 0 | 764 MB |
| 5 (IntegrationTopology … IntegrationQuality), blocked on 2026-11-10 | 520 | 3 | 0 | 699 MB |
| 6 (everything else), blocked on 2026-11-10 | 2261 | 8 | 0 | 4,760 MB |
| **Total** | **6399** | **19** | **0** | |

- **Totals.** 6,418 tests = the 6,417 of the 2026-11-10 run plus the new SG-D2/D3 test.
- **The 120 failures of 2026-11-10 are resolved.** They were Npgsql/Testcontainers failures caused by the stopped VM, and all 120 pass with PostgreSQL restored. `MigrationIntegrityTests` and `ManagedEdgeConnectorTests` pass too. Skips fell from 21 to 19.
- **Release build** of `BirkNext.Api`: succeeded, 0 errors.
- **Frontend not re-run.** No frontend file changed since `40ae60e5`, where the full suite passed 8576/8576 (§6).

**Full backend regression: PASS.** Partitions 1–6, 0 failures.

### 7.5 MU → MM contract

See `birknext-mu-mm-skolenaervaer-contract-gap.md` (MM-G1).
- On the Altinn path MU forwards the Skolenærvær payload **opaquely**: no XSD validation, no transform, version-agnostic routing. Its v2.0.0 XSDs are test data.
- MM accepts exactly `…v2.0.1.flat` and rejects any other version before validating.
- The two versions differ structurally, not only in namespace.

Verdict: a **static candidate**, blocking only if the Altinn app, the real sender, whose source is not imported, emits anything else. There is a separate, open MU channel dependency (MM's order to MU is an unsent draft).

### 7.6 Verification scope and source

- **Source.** The commit that adds this section: `40ae60e5` plus `SourceUiExtractor.cs` (SG-D2, SG-D3) and one test in `SuggestedTestsTests.cs`. The scratch tree was verified equal to the repository before the build.
- **Verified:**
  - environment restore (Podman, PostgreSQL, migrations, read only);
  - full backend regression;
  - Release build of the API;
  - real M2LB suggestion generation against an independent oracle;
  - the MU/MM static evidence.
- **Not verified here:**
  - real DOM verification of M2LB locators (attended session required);
  - the frontend suite (unchanged since its 8576/8576 pass);
  - any live integration traffic.
- **Cleanup.** The disposable database `birknext_sug_20261110` is kept. It holds only task 6 data: 10 snapshots: 2 synthetic, plus 8 M2LB from the four import rounds of 2026-11-11 (before the fix, after SG-D2, the first SG-D3 attempt, and final), and the acceptance flows. No process is connected to it. Deleting it needs the user's separate approval.
