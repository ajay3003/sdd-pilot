# Playwright regression recovery and independent browser quality audit (2026-11-17, recovered 2026-11-18)

Base: `1f660de6` (task 6) on `008-traceability-first`. Local commits only; nothing pushed. Isolated ports (API 5097 / web 5197, a second copy
on 5098 / 5198 for acceptance), disposable PostgreSQL databases cloned from `birknext_pts_acc_20261115`, synthetic data only. No external system
(m2lbdev, Altinn, TT02, Azure DevOps, production) was contacted: every browser context runs behind a network guard that aborts and records any
request outside the isolated instance. The user's services on 5000/5173, `appsettings.Local.json`, `AIAssisted/testdata/` and the Kode67 gap
register were not touched.

Machine-readable migration register: `birknext-playwright-migration-register-2026-11-17.json` (every original failing execution, per variant,
with its classification, cause, change, product defect and current outcome).

## 1. Interruption and recovery

The PC shut down unexpectedly on 2026-11-17 at 14:44 (Windows event 6008). State found on 2026-11-18:

| Item | State at the restart | Evidence |
|---|---|---|
| Inventory of the 55 failures (13 Verify, 42 VerifyPre) | Complete | task 6 TRX `pw2-Verify.trx`, `pw2-VerifyPre.trx` |
| Root-cause probes, classification | Complete | session transcript, probes `acc/t7p2…t7p22.cjs` |
| Product fixes (4) + bUnit regression tests | Implemented, verified by unit tests (each test shown failing without its fix) | uncommitted; frontend 9218/0 at 13:42 — before the last product edit, so not counted |
| Test modernization (shared scenarios, page helpers, network guard, synthetic projects, workflow tests) | Implemented, never executed | uncommitted |
| Release republish with the fixes | Complete (14:04) | `t7-pub40.out`; source copy identical to the working tree |
| Filtered VerifyPre run | **Interrupted** — built, never finished, no TRX | `t7-r1.out` (build line only) |
| Full regression, report, commits | Not started | no commits after `1f660de6` |

Nothing was recreated: the uncommitted work was reviewed (no assertion weakened, no test deleted: every original test method or its named
successor exists), committed, and verification was rerun from scratch against the final code.

## 2. The 55 original failures

Classes: **A** product defect · **B** outdated interaction · **C** replaced behaviour · **D** environment/fixture · **E** isolation ·
**F** timing · **G** unresolved. (Several executions have more than one.)

| Test class (variant) | Executions | Class | Cause | Resolution |
|---|---|---|---|---|
| EndpointDiscoveryUITests (both) | 6 + 6 | C, E | Expected the target check to list discovered endpoints/integrations with Apply buttons (retired on purpose: endpoints belong to Endpoint Discovery); pointed the check at the real m2lbdev host | Six successor tests with synthetic check answers; product defect 2 found |
| TargetEnvironmentDetectionPlaywrightTests (both) | 3 + 3 | B, E | "Check target application" / renamed links; real hosts | Shared `TargetEnvironmentDetectionScenarios`; loopback discard port for the real-backend check; defects 1, 3 |
| TargetEnvironmentDetectionPlaywrightTests_PreStarted | 7 | B, E | As above + a fixed fake URL no longer matching the QA environment | Same shared scenarios (the two copies had diverged) |
| TargetEnvironmentsUIResponsiveTest_PreStarted | 7 | B, F, E | Tabs counted before the detail rendered; `.fa-environment-list button` gone (some tests asserted nothing); Diagnostics tab removed; conflict looked for in shared data | Chip selection, wait for the detail, Validation tab, test-owned conflicting environment |
| TargetEnvironmentBrowserContinuationPlaywrightTests_PreStarted | 5 | B, E | Old labels, Continue moved into the evidence disclosure, hard-coded backend :5000 | Current labels, backend from `BrowserTestServers`, synthetic answers for the requested URL |
| SystemSettingsPlaywrightTests_PreStarted | 5 | C, F | Engines section became Frontend Testing Capabilities (switches under "Technical details"); maintenance rows renamed; actions counted before render | Current structure; waits for the settings header |
| FrontendQualityReviewLayoutMeasurementTest (both) | 2 + 2 | F, B | Run read during its stated loading state; the result is now five tabs (engine matrix on Coverage inside "View engine diagnostics", category cards replaced by assessment-domain cards, issue search on Issues, release decision on Summary) | `WaitUntilRunnableAsync`, `EngineMatrixAsync`, `OpenResultTabAsync`; the same readability invariants measured on the view that shows each element |
| QualityReviewPlaywrightTests_PreStarted (FQR) | 4 | B, F, D | "Target Environments" link renamed "Change target"; 5 s wait on a 8–10 s cold start; regions moved into tabs; Lighthouse unavailable in a published build | Current link; condition waits; each region checked on its tab; defect 10 |
| QualityReview DataModelQuality (both) | 1 + 1 | D, A | Selected a project on Demo Projects (none in the isolated instance); conditional asserts | `DataModelQualityScenario`: own synthetic data model, exact 7 findings, 5/7 cards, two toggle cycles; defect 4 |
| SddProjectionResponsiveAccessibilityTests (both) | 1 + 1 | D | Demo Projects | Own synthetic Spec Kit project |
| **Total** | **13 + 42 = 55** | | none unclassified (G = 0) | |

The four skips in each variant are honest environment prerequisites (real-project dataset, dedicated Schema Studio / Active Event acceptance
instances) and are reported as skipped, never as passed.

## 3. Application defects found and fixed

| # | Defect | Fix | Commit |
|---|---|---|---|
| 1 | Target summary said "Check target again" for a target never checked | "Check target" until the first check | `04c3b306` |
| 2 | An environment chosen while Target Environments was still loading was replaced by the active one | Keep the tester's choice; default only when nothing (or a removed environment) is selected | `04c3b306` |
| 3 | The detection summary showed the saved URL while the check ran — and failed — for the draft URL | Summary shows the URL the check is about (draft while editing, saved after Cancel) | `04c3b306` |
| 4 | Markdown tables dropped empty cells, shifting later cells one column left (an empty Type made Nullable the type) — shared with the API | Only the segments outside the outer pipes are dropped | `04c3b306` |
| 5 | **Project Import → "Import another application"** showed the new application as the target, while Manual Testing, Technical Testing and Plan & prepare kept working on the application the active Target Environment is bound to, with nothing telling the tester | Decision (user, 2026-11-18): *follow the chosen application*. The environment's project outranks the current import only within the same application; an environment bound to another application is named in every testing workspace header with a link to re-bind it | see §6 |
| 6 | Target Environments renders its detail and accepts clicks while initialization still runs; on a loaded PC a check clicked then was handled ~14 s later (reproduced with 3× CPU throttling), and a focused button was re-rendered | The detail is `aria-busy` / `data-ready="false"` until initialization has finished; tests and assistive technology wait on it | see §6 |
| 7 | **Data model parser dropped real columns** named `name`, `field`, `column` or `property`: inside a column table any row whose first cell was a header word was skipped as a repeated header, so the most common column name vanished from every data model (and from Data Model Quality, Data Model Explorer and everything fed by them) | Inside a table a row is a header only if it looks like one (a name heading plus a type/description heading); the loose first-cell rule still finds where a table starts. `DataModelSyntheticFindingsTests` pins the synthetic model's exact nine findings | see §6 |
| 8 | **SDD traceability matrix** (min-width 1100 px) scrolled horizontally in a region keyboard users could not reach (axe `scrollable-region-focusable`, serious) | The scroll wrapper is a named, focusable region, as Implementation Review already does | see §6 |
| 9 | **Document Quality Review collapsed a pack the tester had just opened**: the result renders while the post-run steps (auto-save, diagnostic export) are awaited, and the view state was reset after them — ~300 ms after the click (measured with probe `t7p23`) | The new result's view state is reset once, when the report arrives, before any await; bUnit `APackOpenedWhileThePostRunStepsRun_StaysOpen` (fails without the fix) | see §6 |
| 11 | **The first check click on Target Application could land beside the button** (reproduced 6/6 on a first visit with probe `t7p24`; the click hit the summary heading): (a) the Application block first showed "Application not chosen" + the picker, then shrank ~130 px when the environment's application was read; (b) when the URL edit made checking the next step, the check button was swapped for a separate primary button, and the remaining button slid ~160 px right in the right-aligned row | (a) the block says "Reading the application…" (role=status) with the usual answer's height reserved until it knows; (b) one check button, never replaced, last in the row so siblings cannot move it; it only becomes primary. bUnit: loading state (fails without the fix), one-button invariant; probe after the fix: 5/5 clicks hit the button | see §6 |
| 10 | **Lighthouse never shipped in a published build**: `run-lighthouse.mjs` was declared with `Content Update`, but `.mjs` is not a default Content item, so nothing was published and every Release/packaged build reported "Lighthouse runner is unavailable" | `Content Include` for the runner; verified in the tag-43 publish. Its npm dependencies are still not published (see §5) | see §6 |

Test infrastructure defects fixed: the network guard (`BrowserNetworkGuard`) made every fixture context fail on any request outside the
isolated instance; `BrowserTestServers` now fails at once when an external frontend's `BackendUrl` is not the backend under test (a scratch
republish keeps the `:5000` default — on 2026-11-18 this made 30 tests time out against the guard before the cause was found).

## 4. Verification (final code)

Final code = the Task 7 commit (§6), on top of `a2c993fc`. Isolated Release instance (publish tag 47: API and web exit 0, 0 build errors;
web asset integrity check: 87 assets, the one known pre-existing "missing hash" note), disposable database `birknext_t7k_20261118`,
API :5097 / web :5197. Lighthouse runner dependencies copied from the local install (§5).

| Run | Passed | Failed | Skipped | Total |
|---|---|---|---|---|
| Playwright Verify (self-starting fixture), `pw-v6` | 34 | 1 | 4 | 39 |
| Playwright VerifyPre (pre-started), `pw-p6` | 74 | 2 | 4 | 80 |
| Frontend unit suite (bUnit/xUnit), final code | 9242 | 0 | 0 | 9242 |
| Backend, affected areas after `a2c993fc` (AuthenticatedReview, LocalHttpsProxy, ManagedEdge, AzureSignIn, LocalTrustedExecution) | 531 | 0 | 0 | 531 |

Every one of the 55 original failing executions now passes or is an honest skip (register: `currentOutcome`).

**Still failing (not counted as passed):** `TargetEnvironment_UrlChange_InvalidatesDetection` — the self-starting class in both
variants and the pre-started class (3 executions). The check click sends no detection request inside the suite; the same sequence in
fresh sessions (probe `t7p27`) completes 3/3 with "Detection complete", so it depends on state built up by earlier tests in the class.
Not resolved; two real causes of the same symptom were found and fixed on the way (defect 11).

Backend partitions 1–6 were last run in full on `451c0359` (1–5: 4270/0; 6: 2683 passed + 1 known file-lock flake, 3/3 green in
isolation); later backend changes are `a2c993fc` (531/0 above) and the Lighthouse `Content Include` (publish verified). Browser acceptance
scripts on `acc2`: 357/357 on the earlier build of this recovery; not rerun on the final build.

## 5. Remaining gaps

- The four skipped executions per variant need their dedicated environments (real-project dataset, Schema Studio and Active Event acceptance
  instances); they were not run.
- Target Environments still needs several seconds to initialise on a cold WASM start; the page now says it is busy, but the time itself is
  unchanged (the Local HTTPS proxy runtime is polled repeatedly during startup).
- "Failed to fetch" console lines when a test navigates away during a request (known; recorded by `rw-accept` as before).
- Lighthouse's npm dependencies (`Tools/LighthouseRunner/node_modules`) are not part of a publish or of the Windows Tester Package, so
  Lighthouse stays unavailable there (an optional engine; the review says why). For the isolated acceptance instance they were copied from
  the developer's existing local install — no download. Shipping them (or an offline `npm ci` from a pinned cache) is a packaging decision.
- With Lighthouse installed, System Settings → Frontend Testing Capabilities needs ~17 s before it answers (Lighthouse readiness starts
  Node and Chromium); the page says "Checking local capabilities…" meanwhile. Caching the readiness answer would shorten it.
- Engine capabilities (Browser Runtime, Accessibility, Lighthouse, Passive Security) are allowed at Layer 1 only by the developer's
  `appsettings.Local.json`; a pipeline artifact or tester package depends on persisted System Settings for them (not changed; reported).

## 6. Commits

| Commit | Content |
|---|---|
| `04c3b306` | Four product fixes (defects 1–4) |
| `5c2e61c1` | Playwright modernization: shared scenarios, page helpers, network guard, synthetic projects, workflow tests |
| Task 7 completion commit (this report) | Defects 5–11, test adaptations to the tabbed FQR result, migration register, this report |

Related commits made during the recovery at the user's request: `cbf909df` (launcher progress), `251f1f6e` (maintenance reset),
`451c0359` (Local Tester Mode), `a2c993fc` (HTTPS inspection certificate in pipeline artifacts). Nothing was pushed.
