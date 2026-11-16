# BirkNext — Consolidation implementation (2026-11-16)

Branch `008-traceability-first`, on top of `b400d304`. Source documents (read in full, not inferred from ids): `birknext-feature-portfolio-consolidation-2026-11-15.md`, `birknext-feature-ownership-matrix-2026-11-15.md` (66 modules), `birknext-feature-overlap-register-2026-11-15.md` (26 rows), `birknext-feature-consolidation-roadmap-2026-11-15.md` (R1–R13), `birknext-feature-inventory-2026-11-15.json`. Since the audit only the branding commit changed code, so every finding was re-checked against HEAD below rather than assumed. The inventory generator still fails on a module without a curated row (66 of 66 present), and `NavigationDispositionTests` pins every module's workspace home.

## 1. Roadmap R1–R13 — assessment against current code

Priority: P1 correctness / data integrity · P2 duplicate user effort · P3 misleading UX · P4 performance and polish.

| ID | Actual finding (re-checked at HEAD) | Still present? | User impact | Priority | Decision | What was done / why not |
|---|---|---|---|---|---|---|
| R1 | Two requirement populations: reviewed coverage (`RequirementCoverageService`, canonical interpretation, scope = Target Environment) vs Test Coverage (selected specification). Requirements Traceability already says "Of N requirements in the interpretation of <date>" and Test Coverage shows its specification scope; the Manual Testing / Test Management focus card said only "(of N)". | Yes (one unlabelled number) | Two numbers for "requirements" without saying which | P3 | **Implement now (label)** | The focus fact now reads "Requirements with reviewed scenario coverage (of N **in the last relationship analysis**)". Calculations unchanged. One shared population is a product decision (which question each view answers) and stays open. |
| R2 | Impact Analysis and AI Change Review read the legacy `Scenarios`/`TraceLinks` store. | Yes | Only when switched on (both off by default) | P1 when on | **Defer — migration risk** | Needs the two modules moved onto canonical relations with old runs kept readable; a cross-store migration, not a safe in-place change. |
| R3 | Implementation Review shows a frontend evidence-graph verdict and the backend `GapReadiness` verdict. | Yes | Two verdicts on one page | P3 | **Defer — product decision** | Which verdict is authoritative (and how the other is shown as supporting evidence) changes what testers read as "implemented"; needs a decision, not a silent swap. |
| R4 | `ExtractionReviewList` and its four view builders (own `FR-\d{3,4}` grammar) — rendered nowhere (re-checked: only `ExtractionReviewList` references the views; nothing references it). | Yes | None (dead code) | P4 | **Defer — no user impact** | Deleting unused code with its tests is a clean-up, not a user improvement; left for a dedicated clean-up commit. |
| R5 | Task Explorer table rows and Task Changes (Task Delta) padded ids to two digits (`FR-1` → `FR-01`), while task bodies and the specification use the shared grammar (`FR-001`). Re-check found it **inside** Task Explorer too (bodies shared, table rows not). | Yes | A row naming FR-1 never matched FR-001; the panel patched some places only | P1 | **Implement now** | Both producers use `RequirementReferenceParser.Normalize`; tests: row and body agree, Task Changes reports `FR-003`/`SC-012`. |
| R6 | Task Alignment's internal names say "ImplementationReview". | Yes | None visible | P4 | **Defer** | Rename without user value; risk of churn. |
| R7 | Project key vs Target Environment scope: canonical interpretation, reviewed coverage and Release Readiness use the Target Environment profile id (documented in code, `CanonicalInterpretationService.Release.cs`); plans, journeys and Manual Testing use the project key. | Yes | The focus card reads plans by project and coverage by environment | P3 | **Integrate (document)** | Mapping written down here (§6); a resolver joining the two needs an owner decision (one environment can serve several projects). |
| R8 | "Demo Projects writes Target Environment profiles". Re-check: both Demo Projects and System Settings write through the same owner (`FrontendAnalysisSettingsService.UpdateProfile` + `SaveAsync`); Demo Projects lists the values it will overwrite and asks first. | **Disproved** as a second writer | — | — | **Disproved** | Remaining risk recorded: an open System Settings draft saved later writes its whole profile and can overwrite hints applied meanwhile (two tabs); a general draft/version check, not a Demo Projects issue. |
| R9 | Integration package import writes ~15 other owners' tables directly. | Yes | Owner rules bypassed for imported rows | P1 | **Defer — migration risk / concurrent ownership** | Each table owner's API must accept imports first; additive but touches many owners. |
| R10 | Generated documentation diagnostics re-run per page (Explorer and Health). | Yes | Repeated work | P4 | **Defer** | Not measured as slow in acceptance; lower value than R13. |
| R11 | Integration Test Suites cannot record the declared journey (id + revision) they were prepared for. | Yes | Journey is context only | P2 | **Defer — migration** | Additive schema change to suites; needs the suite owner's composer to accept a journey scope. |
| R12 | Integration Topology read only `?tab=`; links from Integration Testing opened the page, not the integration. | Yes | Users searched the inventory for the integration they came from | P2 | **Implement now** | `?integration={catalog id}` focuses the topology on the connections carrying that Integration Catalog identity (exact; never by name or address), with the catalog's display name; an honest "no connection carries it" when nothing matches; "Show the whole topology" clears it. Integration Testing links each configured integration and each catalog-backed connection there. |
| R13 | Frontend Quality Review engine status ~16 s. | Yes | Every FQR / System Settings engines-tab visit waited | P4 (felt as P2) | **Implement now (after measurement)** | See §5: Passive Web Security readiness started a ZAP container on every request; status displays now reuse a recent probe per configuration. |

## 2. Confirmed overlap fixes in this change

- **Requirement grammar (register row 9 / R5):** one normalisation for task references everywhere Task Explorer and Task Changes read them.
- **Population labels (row 10 / R1):** every requirement-coverage number names its population.
- **Integration identity (R12):** topology, Integration Testing and IQR now all address one configured integration by its catalog id.
- **Disproved (row 17 / R8):** Demo Projects is not a second profile writer.

## 3. Manual Testing workflow

The journey Discover → Plan & prepare (saved cases) → Link requirement → Save → Execute & document → Coverage / Traceability was re-run end to end in the browser on this build (`utps-accept`, `mt-accept` phases a/b/c with an API restart between, `link-accept` link/verify with a restart): saved cases persist across reloads and restarts, unsaved-change warnings, partial run save, finished-run immutability, execution history, requirement-link provenance, Test Coverage and Requirements Traceability agreeing, project isolation and viewports. No duplicate case or run records, no second scenario engine, and Manual Execution stays separate from Critical E2E. Results in §7.

Remaining overlap (not changed): the reviewed-coverage population vs the specification population (R1, labelled), and project vs environment scope (R7).

## 4. Technical Testing and Integration Testing

- Stage navigation (Discover & assess, Configure & prepare, Verify & investigate) re-verified: direct URL, reload, Back/Forward, project switching (`ttnav-accept`), and each stage's content (`tt-accept`).
- Integration Testing (`itx-accept`): discover (39 systems/components, 41 connections on the M2LB template), define a journey with Systems and steps, journey revision saved, suite handoff with journey context and refusal of an unknown journey, IQR handoff by catalog id. New (`cx-accept`): topology focus by catalog id, honest empty focus, clearing it, and Integration Testing's topology links carry each integration's own catalog id.
- Evidence distinctions kept: topology is "discovered/derived", a journey is a declaration, a suite is preparation, IQR static review is not a runtime pass — no wording or state was changed.
- Limitation: the M2LB template database holds 0 configured catalog integrations, so the browser verifies the honest-empty focus and link shape; matching a real catalog integration to its connections is verified by component tests over a topology carrying Integration Catalog provenance.

## 5. Performance — R13

Measured on the Release build (local API, disposable database), inside the signed-in app, `POST /api/frontend-quality-engines/status` with readiness probes for no engine, each engine alone and all engines; 4 samples each (first = cold).

| Probe | Before: cold | Before: warm median | After: cold | After: warm median | Samples before → after (ms) |
|---|---:|---:|---:|---:|---|
| No readiness probe | 301 ms | 20 ms | 446 ms | **72 ms** | [301, 161, 17, 20] → [446, 167, 72, 19] |
| Browser Runtime | 683 ms | 913 ms | 14,480 ms | **608 ms** | [683, 1130, 913, 554] → [14480, 608, 566, 931] |
| Accessibility | 835 ms | 876 ms | 886 ms | **867 ms** | [835, 877, 793, 876] → [886, 929, 867, 823] |
| Lighthouse | 15 ms | 15 ms | 21 ms | **17 ms** | [15, 12, 16, 15] → [21, 17, 15, 18] |
| Passive Web Security (ZAP) | 13,061 ms | 12,885 ms | 13,938 ms | **19 ms** | [13061, 12885, 13101, 12881] → [13938, 19, 20, 17] |
| All four (what Frontend Quality Review waits for) | 12,998 ms | 13,111 ms | 838 ms | **896 ms** | [12998, 13111, 13213, 12964] → [838, 866, 896, 896] |

All 48 requests returned 200; response size 1.7–2.1 KB (unchanged). "Before" is the branding build (`b400d304`) right after its acceptance run (browsers already launched); "after" is this change's build right after start, so Browser Runtime's cold 14.5 s is the first browser launch after start (the before-build showed the same one-off, 22.7 s, in an earlier single-sample run). The cases run in table order, so "All four" after the change already reuses the Passive Web Security probe of the row above — which is exactly the repeated-visit situation; the first visit after start still waits for one real probe (~14 s cold). Effect seen in acceptance: `nav-accept`, which leaves each module after 2.5 s, logged the cut-off engine-status request on every earlier run; on this build it logged **no console error**.

**Cause (confirmed):** Passive Web Security readiness runs `podman version`, `podman image inspect` and a whole `zaproxy` container (`zap.sh -version`, a JVM) on **every** status request — ~13 s warm or cold; the four engines are probed in parallel, so it set the page's wait. Browser Runtime's one-off 22.7 s (first single-run probe) was the first browser launch after start; warm it is ~0.9 s. Lighthouse answered in ~15 ms here (its runner is not active in this configuration).

**Fix (proportional):** `EngineReadinessReuse` (singleton) lets status displays reuse the last Passive Web Security probe for the same configuration key (enablement, installation policy, container runtime, network, image): ready results for 5 minutes, not-ready for 30 seconds, timed-out or failed checks never; concurrent requests share one probe; the shared probe keeps its own 120 s bound and is not cancelled when one caller navigates away; a reused result keeps its original checked-at time. **Not weakened:** the readiness shown on Frontend Quality Review and in System Settings is informational by design (the page says readiness is revalidated per engine at execution), and a review run still re-checks Passive Web Security readiness itself before starting ZAP; the explicit revalidation endpoint (`GET readiness/{engine}`) always probes fresh. No UI control was changed. Not done: hiding slowness with a longer timeout; caching the other engines (sub-second).

Navigation cancellation: with the status answered quickly, the "Failed to fetch" console line seen when acceptance left the page mid-request no longer appears in normal visits; a full page unload during the first (fresh) probe can still log it, because the browser aborts the fetch before the app can tell a navigation from a network failure.

## 6. Other workspaces and scope mapping (R7)

Workspaces were re-verified from the rendered sidebars (`nav-accept` P1–P3: every enabled module has a workspace home or is All-modules-only by design; 66 registry modules, no duplicate entries, every enabled module opens at its route). No module was removed or moved in this change.

Scope keys used today (R7):

| Uses the **project key** (selected project) | Uses the **Target Environment profile id** |
|---|---|
| Test plans and saved cases, manual runs, declared Integration Journeys, Project Test Setup scopes, Delivery reporting, Project Import | Canonical interpretation and Relationship Review, reviewed requirement coverage, Release Readiness (and its Critical E2E dimension), Integration Topology, Integration Catalog and IQR, Frontend/API Quality Review settings |

Where one screen shows both (Manual Testing / Test Management focus card: plans by project, reviewed coverage by environment), the coverage number now names its source (R1). A resolver that maps a project to "its" environment needs an owner decision because one environment can serve several projects.

## 7. Results

One source tree (this commit), Release publish from a clean copy, fresh disposable databases from template `birknext_pts_acc_20261115` (`birknext_cx_20261116`; for the in-order reruns `birknext_cxlink_20261116`, `birknext_cxado_20261116`), isolated ports 5097/5197. No existing database, the user's API (:5000) or web (:5173), Azure DevOps or any remote system was touched.

| Check | Result |
|---|---|
| Frontend build (non-incremental) | 0 errors |
| Frontend suite | **9,154 / 9,154 passed** (new: topology focus ×3, Integration Testing topology links, task reference grammar ×3, R1 label) |
| Backend build (in-repo `bin/Verify/net8.0`; the user's API on :5000 locks `bin/Debug`) | 0 errors |
| Backend suite, 6 partitions | 1,052 + 1,099 + 888 + 682 + 541 + 2,593 = **6,855 passed, 19 skipped, 1 failed**. New: 6 readiness-reuse tests (partition 4). The failure: `ManagedEdgeConnectorTests.AdvertisedWebSocketMustRemainAtValidatedEndpoint("ws://example.com…")` — the in-process loopback test server's response did not reach the client within the 60 s timeout while partition 6 ran (no sleep this time; Windows power log checked). Not touched by this change; passed in four other full runs today and **100 / 100** in 10 isolated repetitions on this build. Root cause not established (open). A rerun of partition 6 was started and **stopped by Claude Code because the machine ran low on memory**; it was not restarted. |
| Release publish (API + Web) | exit 0, 0 errors; web asset integrity check unchanged from the baseline (`assets=87 problems=1`, the known missing-hash entry) |
| Browser: consolidation (`cx-accept`) | **6 / 6** — topology focus by catalog id (honest empty, no invented name, clear), Integration Testing topology links carry each integration's own id |
| Browser: project setup (`utps-accept`) | **106 / 106** |
| Browser: Manual Testing journey (`mt-accept` a / b / c, API restart between a and b) | **23 / 23**, **48 / 48**, **7 / 7** |
| Browser: requirement linking (`link-accept` link / verify, API restart between) | In the combined sequence: 12 / 12 and 25 / 26 — K6 ("no case executed") failed because the Manual Testing journey had just executed those cases (the page truthfully showed "Executed: 2 cases — 1 passed, 1 failed"). In its intended order on a fresh database: **12 / 12** and **26 / 26** |
| Browser: Azure DevOps export (`ado-accept`) | In the combined sequence: 32 / 34 — run after link-verify, which unlinks a requirement, so that case correctly needed attention and blocked the download. In its intended order on a fresh database: **38 / 38** |
| Browser: Integration Testing (`itx-accept`) | **42 / 42** |
| Browser: Technical Testing / stage navigation (`tt-accept`, `ttnav-accept`) | **76 / 76** / **48 / 48** |
| Browser: navigation and inventory / search-open-return (`nav-accept`, `nav-accept2`) | **41 / 41** / **32 / 32** — `nav-accept` logged **no console error** (every earlier run logged the cut-off engine-status request) |
| Browser: branding (`brand-accept`) | **17 / 17** |
| Page errors / 5xx / guard (non-allow-listed network) | none in any run |

The two combined-sequence misses came from the acceptance scripts sharing one database in an order they were not written for (each assumes the state its own predecessor leaves); both were re-run in their intended order on fresh databases and passed in full. No assertion was changed.

## 8. Remaining gaps and next iteration

1. **R11** — record the declared journey (id + revision) on an Integration Test Suite (additive; suite owner).
2. **R1** — decide which requirement population each view answers to; today both are labelled.
3. **R2** — move Impact Analysis and AI Change Review onto canonical relations before either is switched on by default.
4. **R9** — route package import through each owner's API.
5. **R3** — one "implemented?" verdict on Implementation Review (product decision).
6. **R4 / R6 / R10** — clean-ups without user-visible change.
7. A draft/version check for whole-profile saves in System Settings (the residual risk found while disproving R8).

## 9. Git

One local commit on `008-traceability-first` (code, tests and these documents: this report, status updates appended to the overlap register and roadmap, and a results addendum to the branding report); not pushed. Unrelated working-tree changes (`appsettings.Local.json`, `AIAssisted/testdata/`, the Kode67 gap register, other audit drafts) are left untouched and uncommitted.
