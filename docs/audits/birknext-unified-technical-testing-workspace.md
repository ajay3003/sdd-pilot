# BirkNext — Unified Technical Testing workspace

Date: 2026-11-15 · Branch: `008-traceability-first` · Built on the workspace/navigation registry (`94d795d0`).

## 1. Executive summary

Technical Testing is now a workspace of its own with a home page and three stages — **Discover & assess**, **Configure & prepare**, **Verify & investigate** — over the five technical areas BirkNext already supports: **Frontend & UI, APIs & contracts, Integrations, Performance, Critical E2E**. It uses the same shared header, guided step list and "one next step" pattern as Manual Testing. It does not add an engine, a settings store or a result store: every number it shows is read from the specialist module that owns it, every action is a link into that module, and nothing is configured or run from the workspace.

For each area it shows three separate states — **Evidence** (does the area apply to this project), **Configuration** (does something the checks need look missing) and **Latest runtime result** (what a check recorded when it ran against a Target Environment) — never one combined pass/fail. Static evidence (contracts in the source, declared journeys, defined flows, browser discovery) is listed apart from runtime results; results that may no longer describe the current source or flow are marked *may be out of date*.

The audit found the technical capability largely **implemented but hard to find or poorly connected** (category A/B): Frontend Quality Review was missing from the Technical Testing workspace, browser and endpoint discovery live inside System Settings, Project Test Setup belonged to no workspace, and the workspace had no home. Those are fixed. Gaps that need new engine work are listed in §19 rather than papered over.

## 2. Capability inventory and classification

A = implemented, hard to find · B = implemented, poorly connected · C = partial · D = missing · E = unsupported by current evidence.

| Capability | Source / static discovery | Configuration | Runtime verification | Persistence of results | Class |
|---|---|---|---|---|---|
| Frontend Quality Review | Technology Coverage applicability (`frontend-quality-review`) | Target Environment (target URL, authentication) | FQR run in the browser against the target | This browser only, per Target Environment, last 10 (`birknext:fqr-history:{profileId}`); no build or source-snapshot identity | B (was not in the Technical Testing workspace) |
| Browser / endpoint discovery | Pages, forms and observed endpoints from the Browser Companion | Target Environments → Browser / Endpoint tabs | — (evidence collection) | This browser only | A (inside System Settings) |
| UI test coverage | Playwright detected only | — | — | — | E (no provider) |
| API Quality Review | Applicability (`api-quality-review`); endpoint discovery | Target Environment (REST/GraphQL base, auth) | AQR run against the target | This browser only, per Target Environment (`birknext:api-review`); no build/snapshot identity | B |
| Contracts (REST/GraphQL) | Contract store per repository identity, source-backed versions; Source Analysis contract evidence marked "not runtime validation" | Contract Testing configure mode | Contract tests via Contract Testing | Backend | B |
| Contract evolution | Backend engine only | — | — | Backend | D (no UI) |
| Integration Quality Review | Applicability (`integration-quality-review`), catalog, readiness | Integrations, bindings, evidence adapters | IQR run (configuration-only to current evidence freshness) | Backend run history per environment | B |
| Integration Journeys | Declared journeys per project (`integration-journeys`) | Declared in the module; runnable packs in IQR Test & verify | Pack journey runs (IQR) | Backend | C (declared and runnable live in two places) |
| Integration Topology | Components and connections from sources/observations | Bindings | — | Backend | B |
| Performance Test Review | Applicability (`performance-test-review`) | Scenarios, readiness per definition, provider | k6 runs (local provider) | Backend runs with source snapshot, version label, drift | B |
| Performance Quality engine | — | — | Collected inside FQR | No stored summary | C |
| Critical E2E | Flow definitions, test type, criticality | Flows, build, environment | Companion-driven flow runs | Backend; Policy 2 gate, frozen verdicts | B |
| Release Readiness (technical) | — | — | Only Critical E2E is read; API/Integration/Frontend/Performance are `Optional`/"Not read into release readiness yet" | — | D for the other four |
| Test Data | Global catalog | Assets, sets, readiness per consumer | — | Backend | B |
| Project Test Setup | Scopes and systems per project | Scope registry | — | Backend | A (in no workspace) |

## 3. Code-evidenced findings

- No `technical-testing` route existed; the Technical Testing workspace landed on Project Quality (`NavigationCatalog.cs`, `WorkspaceView.TechnicalTester` → `"dashboard"`).
- `frontend-quality-review` was in no Technical Testing group (only Test Management "Technical quality").
- Applicability (`ProjectApplicabilityState`) is evaluated for the active Target Environment's main package. Used as is, a project chosen under Import Project would show another project's source applicability. The workspace therefore evaluates the same rules (`ApplicabilityEvaluator.EvaluateAll` over `TechnologyCoveragePresentation.Input`) for the selected project's own snapshot (`ManualTestingWorkspace.ResolveSnapshot`: imported package in scope, else the project evidence main package) and reads no coverage at all when the project has no snapshot — never "the newest snapshot".
- FQR and AQR results are browser-local and carry no build or source identity; the workspace says so and does not invent one.
- `PerformanceTestApiService.OverviewAsync` reconciles executions and probes the local k6 provider; the workspace uses `RunsAsync` (store read) instead. Topology `DiscoverAsync`, companion follow, engine-status revalidation and every `Run*/Start*/Check*` method are not called.
- Release Readiness reads only Critical E2E among the technical areas (`CanonicalInterpretationService.Release.cs`); the workspace does not present API/Integration/Frontend/Performance results as release gates.

## 4. Existing engines reused

Technology Coverage + `ApplicabilityEvaluator` (which areas apply), `FrontendQualityReviewHistory` (FQR runs), `IApiReviewHistoryService` (AQR runs), `IEndpointDiscoveryService` (browser discovery), `IContractTestingApiService.ProjectsAsync` (contracts per repository identity, matched by the project's snapshot), `IDeclaredIntegrationJourneyApiService` (declared journeys), `IIntegrationCatalogApiService.HistoryAsync` (IQR runs), `IPerformanceTestApiService.RunsAsync` (performance runs), `ICriticalE2EApiService.OverviewAsync` (flows and each flow's latest result; Policy 2 untouched), `ITestDataApiService.CatalogAsync` (Test Data count), `ICurrentWorkspaceProjection` (project), `IFrontendAnalysisContextFactory` (active target). Shared UI: `TestingWorkspaceHeader`, `WorkspaceStage`, `GuidedStepList`, the Manual Testing workspace styles.

## 5. Architecture

- `TechnicalTestingPresentation` (pure): stages, areas, area → review id, stage links per area (existing routes only), the three dimensions and their labels/tones, scope rule, next action, guided steps.
- `TechnicalTestingWorkspace` (loader): reads the authorities above once per page load; each area keeps its own read error; mapping functions turn each module's stored result into a `RuntimeResult` (state, verdict, time, environment, identity, owning module, link, staleness, detail) without changing it.
- Pages: `/technical-testing` (home), `/technical-testing/assess`, `/technical-testing/configure`, `/technical-testing/verify` (one page component, optional `?area=` filter). Component `TechnicalAreaCard`.
- No backend change, no new API, no persistence, no migration: the workspace is pure orchestration over existing reads, so no backend API was added (as the task requires for frontend-only orchestration).

## 6. Three-stage journey

1. **Discover & assess** — all five areas with their applicability reason and static evidence (contracts from the analysed source, declared journeys, browser-discovered pages, defined critical flows, Technology Coverage evidence lines) and links to discovery pages.
2. **Configure & prepare** — per area what looks missing (no active Target Environment, a target without URL, no critical flow defined, or the applicability's own action), the active target and the Test Data count, with links to where each is set. Nothing is configured here.
3. **Verify & investigate** — per area the recorded runtime results (up to five, newest or most severe first) with verdict, time, environment, identity where recorded, owning module and a link to the full result; *may be out of date* where applicable; "No recorded run. Static evidence for this area is not a runtime result." otherwise.

The stages are destinations, not gates; each area card links into its specialist module.

## 7. Project and scope behaviour

Project from the shared workspace projection (the same as Manual Testing — no second project selector); source snapshot from the project's imported package or project evidence; target from the active Target Environment. Results that are stored per Target Environment (FQR, AQR, IQR, performance, Critical E2E) are those of the active environment, labelled with it. A project change reloads everything.

## 8. Capability availability rules

Evidence is read from Technology Coverage applicability *and* the project's own source evidence: *Evidence available* / *Partly supported* (the project's source shows the area), *Environment configured · no source evidence* (the review can run because the shared Target Environment has a target or configured integrations, but nothing in this project's source shows the area), *No supported evidence yet*, *Not applicable*, *Not supported by BirkNext*, *Re-analysis needed*. An area is shown on the home when it is source-backed, environment-only or needs re-analysis, or when it has static evidence or a recorded run; only a source-backed area is suggested as the next thing to verify. Other areas are listed once, with the reason (not applicable, not supported by BirkNext, no supported evidence yet). Discover & assess always lists all five.

## 9. Static versus runtime

Separate dimensions and separate sections. Mapping rules (no result is upgraded): FQR *Blocked* → failed, *Review required* → review required, *No automated block detected* → passed only when every required check was assessed (else incomplete). AQR: nothing reviewed or all blocked → blocked; high findings → failed; not every target completed → incomplete; medium findings → review required; else passed. IQR: completed with findings → review required; with limitations / nothing assessed → incomplete. Performance: pass/warning/fail only for completed runs; a run that did not complete or was cancelled is incomplete (not a quality failure). Critical E2E: the module's flow status; *NotRun* flows are not results.

## 10. Configuration readiness

Derived only from what BirkNext can see without contacting anything: applicability *NeedsConfiguration*, an inactive or URL-less Target Environment, no defined critical flow. Each module still checks its own prerequisites before a run; the workspace never shows a run action.

## 11. Result and evidence provenance

Every runtime result names its owning module, environment and time; identity (performance version label) where the module records one; staleness from the module's own signals (performance run on an earlier source snapshot than the project's; Critical E2E `LastRunStale`). FQR/AQR results are labelled as stored in this browser.

## 12. Cross-module integration

Manual Testing and Technical Testing share project, snapshot resolution, header, stage record and guided list; Test Coverage and Requirements Traceability are unchanged (no analysis result is counted as execution). Release Readiness and Critical E2E Policy 2 are untouched. Navigation: Technical Testing workspace lists its four pages first, then Project & environment (Source Management, System Understanding, Target Environments, Project Test Setup), Frontend/APIs/contracts (now including Frontend Quality Review), Integration & E2E, Test data & execution, Performance & risk. Test Management lists Technical Testing in "Technical quality" and Project Test Setup in "Environments & setup"; Manual Testing lists Project Test Setup in "Project setup".

## 13. Navigation and UX

Compact cards (three status chips, at most a few lines, links), one next step, no raw tables or technical ids, area filter as pill links with `aria-current`, focus-visible outlines. Guide: new *Technical testing* section (Getting started) and documentation entries for the four routes.

## 14. Security

Read-only: the browser acceptance records every API request made from a Technical Testing page and checks that each is a GET or the Critical E2E overview read. No target, provider or external host is contacted; no credentials are read or shown.

## 15. Performance

One page load reads each source once (in sequence, each bounded by its own error handling); browser-local histories are read from storage; no polling.

## 16–17. Tests and browser acceptance

### Automated tests

New: `TechnicalTestingPresentationTests` (stages, applicability → evidence mapping including environment-only and "needs configuration without evidence", configuration, scope, next-action order, guided steps, specialist links, and the mapping of FQR/AQR/IQR/performance/Critical E2E results), `TechnicalTestingWorkspaceTests` (coverage is read for the selected project's own snapshot and never without one; nothing project-specific without a project; contracts counted only from the project's own repository identity and never "0 contracts"), `TechnicalTestingPageTests` (shared header and stages, no-project state, failed result as next step with its environment, verify list with identity and out-of-date marking, static-is-not-runtime text, assess lists all five areas with the area filter, configure without a target, a failed read shown on its area).

Updated (deliberate): Feature Visibility counts (+4 always-shown rows: 65 entries, 59 shown, 31 always shown), sidebar section order (+ Technical Testing), Manual Testing "Project setup" group (+ Project Test Setup).

| Run (Technical Testing code before the final evidence-basis fix) | Result |
|---|---|
| Full frontend suite | **9,100 passed, 0 failed** |
| Backend partitions 1–6 | **6,817 passed, 0 failed, 19 skipped** |
| API / Web Release publish | exit 0 / exit 0, 0 errors; SRI baseline (87 assets, the one known item) |
| Existing acceptance baseline | **106 / 106** |
| Technical Testing acceptance (`tt-accept.cjs`, six projects) | first run **73 / 74**, final **74 / 74** |
| Page errors · console errors · HTTP 5xx · guard violations | 0 · 0 · 0 · 0 |
| API calls made from Technical Testing pages | reads only: GET plus the Critical E2E overview read (`POST /api/critical-e2e/overview`) |

### Browser acceptance

Release build, web :5197, API :5097, fresh database `birknext_tt_final_20261115`, Edge, guarded network. Journey on M2LB: Technical Testing sidebar (home and three stages first; Frontend Quality Review, Target Environments and Project Test Setup listed), home with project and target, three stages, one next step, every area with three separate states, no runtime state without a recorded run; Discover &amp; assess with all five areas and applicability; area filter keeps the stage; Configure &amp; prepare → API Quality Review setup → Back returns to the same stage and area with the same project; Verify &amp; investigate lists runs with time and environment or says static evidence is not a runtime result; reload keeps the stage. Six projects (M2LB integration-heavy, Lendo frontend, Parcelo API-only, Roomly documentation-only, LendoPlus mixed, Opaque insufficient evidence): each header names its own project, one next step, no area claims a runtime result without a run, no read errors, the API-only and documentation-only projects get different applicability. Shared context: switching to Manual Testing keeps the project; Back/Forward. Keyboard: the next step is focusable with a visible focus indicator. No horizontal overflow at 1440, 1280, 1024, 768 and 390 px.

Found and fixed during acceptance and review:
1. The empty project showed "0 contracts from the analysed source" as static evidence (first run 73/74). Contracts are now listed only when the project's own source has some.
2. Reviewing the per-project results showed that the documentation-only and empty projects read "Evidence available" for every area. The shared applicability rules answer *Applicable* whenever the active Target Environment has a target, and *Needs configuration* both when the source shows an area without a target and when nothing is known. The workspace now uses the project's own source evidence to tell them apart: *Evidence available* (source-backed), *Environment configured · no source evidence*, or *No supported evidence yet*; only a source-backed area is suggested as the next thing to verify. A new acceptance check (I4) pins it. This fix is verified in the final regression of the audit-remediation commit that follows (same tree plus that work), not in the table above.

## 18. Known limitations

- FQR and AQR results are browser-local and carry no build or source identity; they cannot be checked for staleness against a new build.
- Runtime results are per Target Environment, not per project: two projects using the same Target Environment see the same FQR/AQR/IQR/performance/Critical E2E results (labelled with the environment).
- Configuration readiness is what BirkNext can see without running a check; each module's own readiness remains authoritative.
- Release Readiness does not read the technical reviews other than Critical E2E (unchanged).

## 19. Remaining backlog

- Record build / source snapshot identity on FQR and AQR runs (files: `FrontendQualityReviewHistory.cs`, `ApiReviewService.cs`; acceptance: a result recorded against an older snapshot is marked *may be out of date*).
- Contract Evolution UI over the existing backend engine (D).
- One place for declared and runnable integration journeys (C).
- Release Readiness reading API/Integration/Frontend/Performance under an explicit policy (D; must not change frozen verdicts).
- A stored summary for the Performance Quality engine inside FQR (C).

## 20. Git

- Starting HEAD `94d795d0` (workspace/navigation). This work is one local commit on top (see `git log`). Nothing pushed.
- Not committed here: `appsettings.Local.json`, the Kode67 gap register, older audit drafts, `AIAssisted/testdata/`, and the concurrent full-codebase-audit remediation (separate commit).
- Temporary databases (template copies, not dropped): `birknext_tt_acc_20261115`, `birknext_tt_final_20261115`.

## 21. Stage navigation fix (user-reported)

**Defect.** "Clicking Configure & prepare or Verify & investigate appears to do nothing." Reproduced in the browser on the Release build: moving between two stages from the header changed the address but left the previous stage's title and content on screen; on the home, the stage titles in the guided list were inert headings (only a small *Open* button navigated).

**Root cause.** One component (`TechnicalTestingStage`) served all three stage routes and read the stage from the address in `OnParametersSet`. Navigating between two of its routes reuses the instance, and with no parameter change Blazor calls nothing, so the stage never changed. The earlier acceptance missed it because its stage switch also changed `?area=`, which is a parameter change.

**Fix.** Each stage has its own small page (`TechnicalTestingAssess/Configure/Verify.razor`, one static route each) rendering the shared stage component with its stage, so the router renders every stage change; the home's stage titles are links (`GuidedStepList.LinkTitles`, opt-in, other hosts unchanged); a stage card shows the state that stage is about (evidence / configuration / runtime result), so the three stages read as different pages. Routes, bookmarks and the `?area=` filter are unchanged.

**Verification (fix build).** Frontend 9,114 / 9,114 (new tests: each stage is its own page with exactly one route; switching the stage changes the stage-specific content, not only the highlight; every stage has content for a project without technical evidence; unknown stage; home titles are links). Release publish 0 errors. Browser: baseline 106 / 106, Technical Testing 76 / 76, stage navigation 48 / 48 (M2LB and documentation-only Roomly: home title link, header clicks in every direction with content checks, Back/Back/Forward, reload of each stage, Enter on a focused stage, area filter, a specialist link and Back, 1440/1024/768 px, no console errors). Two script-side issues were corrected during acceptance (a wait that could see the outgoing page, a project-selection helper); the Technical Testing request check now allows the app shell's read-only `POST /api/recommended-workflow/build-steps` (it writes nothing and is not issued by these pages).
