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
| BirkNext generation on the real snapshots | **BLOCKED.** During the M2LB import the local Podman/WSL VM that hosts PostgreSQL stopped. The insert connection was aborted, then refused; `podman` and `wsl.exe` became inaccessible. Restarting the user's VM is a system action left to the user. No M2LB environment was contacted, and nothing was verified against a real DOM. |

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
