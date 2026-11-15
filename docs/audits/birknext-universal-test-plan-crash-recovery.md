# Universal Test Plan Suggestions — crash recovery and completion

Date: 2026-11-15 · Branch `008-traceability-first` · Starting HEAD `ef6c42ec85f98f5471b022c0ed838b703cb0ccf3` · Ending HEAD: the local commit that adds this report (section 10); not pushed

**Overall status**
- Universal Test Plan Suggestions: complete; focused tests and browser acceptance (94/94) pass on the final code.
- **Full regression: FAILED.** The full frontend suite has 3 failures in the concurrent navigation task's tests. Backend partition 6 has 3 failures in tests outside this task (environment- and build-dependent; see 7).

This report covers the recovery of the interrupted task "Universal Test Plan Suggestions: project-agnostic scenario discovery, evidence-adaptive manual test planning and UX redesign". It also serves as the implementation report for that task; no earlier report for it existed.

## 1. What happened

The previous session ran three queued tasks. It delegated two of them to background sub-agents and integrated the results itself.

| Time (local) | Event | Evidence |
|---|---|---|
| 23:42 | Navigation-loading task delegated to a sub-agent | session transcript |
| 23:53–00:05 | Navigation sub-agent edits `Layout/*`, `NavigationProgress`, `app.css`, `navigation.js`, Web `Program.cs`; finishes | sub-agent metadata `finished: true` |
| 23:52–00:29 | Universal Test Plan Suggestions (UTPS) sub-agent edits backend, contracts, frontend | sub-agent transcript |
| 00:09 | Lead commits `86f742c7` (Project Test Setup) and `ef6c42ec` (acceptance network guard) | git log |
| 00:29:04 | Lead starts the navigation tests | last main-session entry |
| 00:29:08 | UTPS sub-agent writes the whole new `TestPlanSuggestions.razor` in one write | last sub-agent entry; no `finished` flag |
| ~00:38 | Machine restart: browser processes restart, PostgreSQL containers stop | process start times; `podman ps` |

**Conclusion:** the session crashed mid-implementation. The UTPS sub-agent never built or tested anything, by design: its brief left building to the lead. The rewritten page did not compile. Nothing of the UTPS task was committed.

## 2. Worktree found

- **Committed while the task ran:** `86f742c7`, `ef6c42ec` (other tasks; not touched).
- **Staged:** nothing.
- **UTPS (uncommitted, recovered):** `shared/TestScenarioDiscoveryContracts.cs` (new), `shared/TestPlanSuggestionContracts.cs`, `TestScenarioDiscoveryEngine.cs` and `TestScenarioDiscoveryService.cs` (new), `TestPlanSuggestionEngine.cs`, `TestPlanSuggestionService.cs`, `TestPlanSuggestionsController.cs`, API `Program.cs` (registrations only), `SystemGuideService.cs`, both csproj contract links, `TestScenarioDiscoveryTests.cs` (new), `TestPlanSuggestionServiceTests.cs`, `TestPlanSuggestionsApiService.cs`, `TestPlanSuggestionsPresentation.cs`, `WorkspaceFocusPanel.razor`, `Pages/TestPlanSuggestions.razor`.
- **Navigation-loading task (uncommitted, not this task, left untouched):** `Layout/MainLayout.razor`, `Layout/NavMenu.razor(.css)`, `Layout/NavigationLoadingIndicator.razor`, `Layout/RouteErrorBoundary.razor`, `Services/NavigationProgress.cs`, `wwwroot/css/app.css`, `wwwroot/js/navigation.js`, Web `Program.cs`, `Layout/NavigationProgressTests.cs`.
- **Never touched:** `appsettings.Local.json`, `AIAssisted/testdata/*`, and the untracked audit documents from 2026-11-07…13.

## 3. Requirements checklist

Status after recovery. "Verified" means unit/bUnit tests **and** browser acceptance on the final Release build.

| Area | Requirement | Before recovery | After recovery |
|---|---|---|---|
| A | Uses the selected project and its source snapshot | Implemented, not built | Verified |
| A | No mandatory guide, no hardcoded system or paths | Implemented, not built | Verified (5 unrelated fixtures) |
| A | Cross-project isolation | Implemented, not built | Verified |
| B | Capabilities per evidence kind; honest No evidence / Not applicable / Failed | Implemented, not built | Verified |
| B | Documentation-only and insufficient-evidence projects | Implemented, not built | Verified |
| C | Stable project-scoped ids, provenance, type, confidence, canonical-key dedup, possible-duplicate notes | Implemented, not built | Unit-verified; possible-duplicate case not reached by a browser fixture |
| C | Readable titles and statements | Raw markdown in titles | **Fixed** (see 5) |
| D | Preconditions, steps with per-step expected results, Test Data, access, dependencies, editing, save, reload, history | Implemented, not built | Verified |
| D | Human edits survive re-discovery | Implemented, not built | Unit-verified (refresh keeps edits) |
| D | Historical guide plans load unchanged | Implemented, not built | Verified (guide plan created, survives everything) |
| E | Seven readiness states; "not described" is never "not required" | Implemented, not built | Verified |
| F | Four-step workflow; secondary snapshots, compare, export | Page did not compile | **Fixed** and verified |
| F | Search, filters, grouping, multi-select kept across filters, details on demand | Page did not compile | Verified |
| G | Test areas (Scope Registry), Test Data registry, Critical E2E handoff link, Azure DevOps export (preparation only) | Implemented, not built | Verified (scope unit tests; Test Data linked in browser; export) |
| G | Prepared cases visible to Test Coverage / Requirements Traceability | Not implemented | Not implemented (see 9) |
| H | No fabricated execution, no external writes | Implemented | Verified (network guard: 0 violations) |
| — | Styles for the new page elements | Missing (11 classes) | **Added** |
| — | bUnit page tests updated for the redesign | Not done | **Done** (+8 new tests) |
| — | In-app user guide section | Not done | **Done** |
| — | Acceptance fixtures | Not done | **Done** (generator script) |

## 4. Architecture (as recovered)

- **Owner:** `TestScenarioDiscoveryService` orchestrates one `ITestScenarioCandidateProvider` per existing analyzer. No new interpretation engine was added.
  - Specification: the existing browser-side parser, sent as `CoverageWorkspaceEvidence`.
  - System Understanding guide: `SystemGuideService.RecognisedGuidesAsync`, with `applicationSourceOnly` so that an IaC-only match no longer pulls in a guide.
  - UI source: the Critical E2E `SuggestionEngine` over the snapshot's `UiSurface`.
  - Contracts: Source Analysis `EvidenceDomains.Contracts`, OpenAPI and GraphQL.
  - Integration journeys: declared journeys, and test packs whose source roles recognise the snapshot.
  - Existing tests: the xUnit inventory, explicit `[Trait("Requirement", …)]` references only.
- **Normalisation:** `TestScenarioDiscoveryEngine` (rule version `tsd-1`) is pure, with no I/O.
  - Candidate id = `tsc-` + hash(project | capability | canonical key). It is stable across snapshots and differs per project.
  - Deduplication uses the canonical key only. Candidates of different sources that name the same requirement get a *possible duplicate* note and are never merged.
  - An expected result is filled only when the evidence states it; otherwise it stays *Needs clarification*.
- **Persistence:**
  - Project plans reuse `test_plan_drafts` / `test_plan_snapshots`, with owner key `project:{projectKey}`. Generated content and human `Edits` are stored separately in the JSON.
  - No migration was needed: the new fields are additive JSON.
  - Historical guide plans keep their guide owner key and the unchanged code path.
- **Discovery results:** kept in memory per project (and per project + snapshot) until the API restarts. Preparing cases needs the result from the same run; after a restart the page asks to discover again.
- **API:**
  - `GET project`, `POST discover`, `GET discover/last`
  - `POST project-drafts` (generated content is built on the server from the stored result, so a client cannot inject it)
  - `POST drafts/{id}/manual-cases`

## 5. Implemented during recovery

1. **The page compiled for the first time.** Three `""` string literals inside Razor attributes ended those attributes early (`TestPlanSuggestions.razor`).
2. **Regression: source baseline.** The guide mode lost the "Source baseline" role list (`tps-sources`). Restored.
3. **Regression: rule labels (MT-7).** Guide rule ids were labelled "Requirements". Guide cases now show "Business rules (guide)", including the "none" line. Project cases keep "Requirements", because those are specification ids.
4. **Regression: unsaved marker.** The guide mode lost the "Unsaved changes" marker. Restored.
5. **Readable specification text** (found in browser acceptance). The parser hands over `- **FR-001**: …` and titles a scenario by its Given clause.
   - The engine now shows plain text, without list markers, emphasis or the repeated id.
   - A scenario titled only by its starting situation becomes "Given — When".
   - Covered by a new backend test.
6. **Group ids keep letters of every script.** "periodelås" is no longer turned into "periodel-s".
7. **Clearer expression.** Explicit parentheses in the delivery-comparison context check (`&&`/`||`).
8. **Styles for the new elements.** At narrow widths, each step's action and expected result are stacked rather than squeezed side by side (found at 768 px).
9. **bUnit page tests.**
   - 11 existing tests were updated where the redesign intentionally changed ids or wording. Every data assertion was kept.
   - 8 new tests: no-project landing, unrelated project without a guide, filter + selection + server-side ids, cross-project isolation, empty evidence → manual case, step editing, readiness, catalog/scope matching.
10. **In-app user guide** section and documentation catalog entry, rewritten for the generic workflow.
11. **Acceptance fixtures:** a deterministic generator `make-tps-fixtures.py`, outside `AIAssisted/testdata/`.

## 6. Multi-project acceptance

**Setup (final run, after the title-cleanup and step-editor changes)**
- Release builds of the API and web app, both published from a clean, isolated copy of the sources (no `bin`/`obj`), outside the repository.
  - All 86 framework assets match the hashes in `blazor.boot.json`, so the earlier WebAssembly digest mismatch is gone.
- API on `localhost:5097`, web on `localhost:5197`.
- Fresh disposable database `birknext_utps_verify_20261115`, created with `TEMPLATE birknext_pts_acc_20261115` (the acceptance database that holds the M2LB import). The template database was only read.
- The repository's network guard blocked ports 5000/5173 and all external hosts.
- An earlier run (79/79) on database `birknext_utps_final_20261115` used the build from before the last two changes; the final run below replaces it.

**Fixtures**

| Fixture | What it contains |
|---|---|
| Lendo | Generic Blazor WASM app: form with DataAnnotations, search page, xUnit tests with requirement traits; no spec |
| Parcelo | API only: OpenAPI 3 JSON with bearer security, 401/404/400 responses, and a GraphQL SDL |
| Roomly | Documentation only: speckit spec with 3 Given/When/Then scenarios and FR-001…004 |
| LendoPlus | Spec + Blazor + OpenAPI + tests, sharing FR ids |
| Opaque | A plain class library: the importer accepts it, but nothing in it is testable |

A ZIP with no supported content at all is refused by the importer itself (*Import project* stays disabled).

**Results**

| Project type | Discovery | Manual cases | Persistence | Browser verified |
|---|---|---|---|---|
| M2LB / Skolenærvær (current import) | 6 of 6 capabilities: 130 spec, 21 guide, 59 UI, 419 contract, 19 journey, 1,710 tests as associations | Yes | Edits, Test Data, readiness, snapshot, export | Yes |
| Skolenærvær guide plan (historical) | 21 guide candidates | Yes | Edit + snapshot intact after all other work | Yes |
| Generic Blazor (Lendo) | 10 UI candidates: required fields, invalid input, valid submission, search, clear (UI functional) plus page-opens (UI smoke) | Yes | Yes | Yes |
| API only (Parcelo) | 6: 5 operations (REST + GraphQL) plus 1 security/authorization candidate | — | — | Yes |
| Documentation only (Roomly) | 7 business functional; source capabilities *Not applicable* | — | — | Yes |
| Mixed (LendoPlus) | Business functional, UI functional, UI smoke, API/contract, security | — | — | Yes |
| Insufficient evidence (Opaque) | 0, honest empty state | Created by hand, steps and results edited | Survives reload | Yes |

**Result: 94/94 checks passed.** No page errors, no console errors, no 5xx responses, no guard violations.

Rechecks of the latest changes and regressions, all passed:

| Recheck | Where | Result |
|---|---|---|
| Requirement titles are plain text: no `**`, list marker or repeated id | Roomly, LendoPlus, Lendo, Parcelo, M2LB first page | e.g. `FR-001: The system MUST let an employee book a free room for a time slot.` (was `FR-001: - **FR-001**: …`) |
| Scenario titles name the starting situation and the action | Roomly, LendoPlus | e.g. `A room is free from 10:00 to 11:00 — the employee books it for that slot` (was `a room is free from 10:00 to 11:00`) |
| Step editor at 768 px | M2LB case with 2 steps | Action and expected result 230 px wide each, tools beside them, no overlap, nothing clipped (902 px at 1440) |
| Guide "Source baseline" | Skolenærvær guide plan | Shown |
| Business rules vs requirements (MT-7) | Guide case | "Business rules (guide)", never "Requirements" |
| Unsaved-changes marker | Guide mode; project mode | Shown after an edit; cleared after Save (project mode) |
| Manual case editable and saved | Opaque | Title, step, step result, expected result survive reload |
| Project isolation | Each fixture; return to M2LB | No other project's plan shown; no fixture case in the M2LB plan; guide plan and its edit intact |
| No horizontal overflow | 4 steps × 1440/1280/1024/768 px | 16/16 |

M2LB lists 60 candidates per page ("Show more" for the rest). Its first page holds API/contract and security candidates, so the plain-title check of its 130 specification candidates was done on the first page only. The specification-title rules themselves are covered by Roomly, LendoPlus and a backend unit test.

Screenshots: session scratchpad `acc/shots-utps/`. Machine-readable result: `acc/utps-result-GPBADMERV.json`.

## 7. Regression on the final code

All runs on the final code, one test host at a time, memory-guarded. Backend tests ran on the in-place Debug build of the final backend; the frontend test project was rebuilt after the last change.

| Run | Passed | Skipped | Failed | Status |
|---|---|---|---|---|
| Backend focused (TestPlanning, SystemUnderstanding, CompilerCompatibility, Architecture) | 189 | 0 | 0 | PASS |
| Frontend: Test Plan Suggestions page / Help Center / User Guide / Documentation Catalog / Workspace Focus (within the full run) | 25 / 16 / 19 / 7 / 7 | 0 | 0 | PASS |
| **Full frontend suite** | 9,021 | 0 | **3** | **FAILED — three navigation tests** |
| Backend partition 1 | 1,052 | 2 | 0 | PASS |
| Backend partition 2 | 1,099 | 5 | 0 | PASS |
| Backend partition 3 | 888 | 0 | 0 | PASS |
| Backend partition 4 | 676 | 1 | 0 | PASS |
| Backend partition 5 | 539 | 3 | 0 | PASS |
| **Backend partition 6** | 2,521 | 8 | **3** | **FAILED — outside this task** |
| Backend total | 6,775 | 19 | 3 | FAILED |
| API Release publish (isolated copy) | — | — | 0 errors | PASS |
| Web Release publish (isolated copy) | — | — | 0 errors | PASS |
| Browser acceptance | 94 | — | 0 | PASS |

**Frontend failures (concurrent navigation task, not investigated further, not modified).** All three are in `BirkNext.Web.Tests/Layout/NavigationProgressTests.cs`, an untracked file written by the navigation-loading sub-agent at 00:03. It renders only `NavigationProgress`, `NavMenu`, `MainLayout` and `NavigationLoadingIndicator`, and never refers to Test Plan Suggestions.
- `BrowserHistoryNavigation_NotIntercepted_IsTrackedTheSameWay`: expected `intercepted` False, found null.
- `RapidNavigation_OnlyTheLatestCanCompleteOrReveal`: expected state Idle, found Navigating.
- `CancelledNavigation_ClearsPending_AndRestoresTheActiveSidebarItem`: bUnit `WaitForAssertion` timed out.

The same three tests failed with the same messages in both full frontend runs of this recovery (before and after the final page changes). No navigation-owned file was edited.

**Backend partition 6 failures (not caused by this task; no file of this task is involved).**
- `ManagedEdgeCdpTests.BridgeHasNoAuthenticationExtractionOrBrowserCloseCode` and `ManagedEdgePreflightTests.LaunchCodeNeverTerminatesOrEnumeratesEdgeProcesses`: `DirectoryNotFoundException`.
  - Both tests walk up from the test output folder to the first folder that contains an `AIAssisted` directory.
  - A leftover, file-less folder tree `BirkNext.Api.Tests/bin/Debug/net8.0/AIAssisted/…` (created 2026-10-09, before this task) stops the walk inside `bin`.
  - With that folder renamed for the run, both passed (6/7 of the selected tests). It was then renamed back unchanged.
  - Follow-up: the tests should stop at the repository root (e.g. a folder containing `.git` or `AIAssisted/backend`), or the stray folder should be removed by its owner.
- `AdminServiceRuntimeSourceTests.SourceRunHasNoPackagingMetadata_AndNothingIsInvented`: expected `Application.CommitSha` null, found `ef6c42ec…`.
  - Since `edae017a` (application versioning, 2026-11-11), `BirkNext.BuildIdentity.targets` embeds `git rev-parse HEAD` into every build made inside a git working tree. The test assumes a source run has no commit.
  - It passes only when the backend is built outside a git checkout.
  - Follow-up for the versioning owner: decide whether a developer build reports its commit, then align the test or the targets.

No threshold, assertion or test was changed to obtain a pass.

## 8. Root causes of failures met during recovery

| Failure | Root cause | Fix |
|---|---|---|
| Frontend did not compile (CS1026/CS1513/CS1525) | `?? ""` inside double-quoted Razor attributes; one was a truncated `"""` | `string.Empty` |
| Compiler `OutOfMemoryException` | Build script capped the GC heap at 2.25 GB | Cap raised to 4 GB for builds (≈13 GB free) |
| Backend tests: `Failed to connect to 127.0.0.1:5432` | PostgreSQL container stopped at the machine restart | Started the existing BirkNext container (`birknext-studio-local_postgres_1`, which holds the acceptance databases); nothing created or dropped |
| 13 bUnit failures | Tests written for the 7-tab page | Updated (11), plus 3 real regressions fixed in the page; 2 were navigation-task tests |
| Browser: "Failed to find a valid digest" for `dotnet.native.wasm` | In-place Release publish over a stale `obj/Release` compression cache: the raw and compressed files came from different runtime packs | Web published from a clean scratch copy; all 86 framework assets checked against the boot manifest |
| Fixture landed in the wrong project | Harness bug: `aria-label*="Lendo"` also matched "LendoPlus" | Exact label match |

## 9. Remaining gaps and limitations

- **Navigation-loading task:** still uncommitted and not part of this commit. 3 of its own tests fail (section 7). The Release web build used for acceptance includes it, because it shares the working tree.

None of the four gaps below prevents the generic manual workflow. A tester can still choose, prepare (or write by hand), check testability, save, snapshot and export a plan for any project. All four are therefore follow-ups, not fixed here.

**G1. Prepared cases are not consumed by Test Coverage or Requirements Traceability.**
- Verified in code:
  - `RequirementCoverageService.cs:78-91` builds reviewed requirement coverage only from System Understanding *guide* scenarios (`ScenarioReviewsAsync(guideKeys)`).
  - `TestCoverageReviewService.cs:115-122` reads every draft, project plans included. But `TestCoverageDataLinks.cs:109` (`LinksFlow`) uses a case only when it references a Critical E2E flow, which project cases never do.
  - Project cases carry `RequirementIds` (specification ids), but nothing maps them to canonical requirement identities.
- Follow-up acceptance criteria:
  1. A project case linked to `FR-nnn` appears as test-design evidence for that requirement in Requirements Traceability, counted as *designed*, never *executed*.
  2. Excluded/superseded cases do not count.
  3. Guide coverage is unchanged.
  4. Another project's plans are never counted.

**G2. Discovery results are lost when the API restarts.**
- Verified: `ScenarioDiscoveryCache` (`TestScenarioDiscoveryService.cs:124`) is an in-memory dictionary (64 entries). After a restart, preparing from an old discovery returns 409 `discovery-missing`, and the page says "Discover test scenarios again" (`TestPlanSuggestionService.cs:514`).
- Saved plans, edits, snapshots and manual cases are in the database and unaffected.
- Follow-up acceptance criteria:
  1. A discovery result survives an API restart (stored per project + snapshot with rule version `tsd-1`).
  2. A stored result whose evidence fingerprint no longer matches is shown as outdated.
  3. Candidate ids stay identical.

**G3. UI analysis covers Razor/HTML only.**
- Verified: `UiSourceScenarioProvider` (`TestScenarioDiscoveryService.cs:49-68`) uses the snapshot's `UiSurface` from the existing frontend analyzer. Its notes state "React and Angular templates are not analyzed" and "Only pages with a parameterless @page route produce candidates".
- A React/Angular project gets *No evidence* for UI, never invented UI candidates.
- Existing tests: xUnit only. OpenAPI YAML is read partially (no `$ref` chains); the fixtures use JSON.
- Follow-up (belongs to the UI analyzer, not to test planning): extend `UiSurface` extraction to JSX/TSX and Angular templates. The provider then needs no change.

**G4. Possible-duplicate notes have no browser acceptance.**
- Verified: `TestScenarioDiscoveryEngine.Finalize` (`TestScenarioDiscoveryEngine.cs:363-383`) notes a possible duplicate only between candidates of *different capabilities* that share a requirement id.
- Only the specification (`:104`, `:119`), guide (`:190`) and integration-journey (`:228`) providers set requirement ids; UI and contract candidates never do.
- So the LendoPlus fixture (spec + Blazor + OpenAPI + tests) cannot produce one: its shared FR ids sit in spec candidates and test traits, and existing tests become associations, not candidates.
- The engine rule is covered by unit tests. The page renders it as a "Possible duplicate" chip and a "Possible duplicate of" detail (`TestPlanSuggestions.razor:561,575`).
- Follow-up acceptance criteria: a fixture with a speckit spec plus an integration journey (or a System Understanding guide) naming the same `FR-nnn`. Both candidates show "Possible duplicate", each names the other, and both stay separately selectable.
- **Server-side isolation:** opening, saving and reviewing a draft by id is not checked against the project. The project check is done in the page, and when adding cases to an existing draft. Draft ids are random GUIDs.
- **Spec selection:** a specification must be the selected one in the project's explorers. Roomly had a single spec, which was picked up automatically.

## 10. Environment and data safety

- Databases: none dropped or modified.
  - This task created three disposable acceptance databases: `birknext_utps_acc_20261115`, `birknext_utps_final_20261115` and `birknext_utps_verify_20261115`.
  - Backend test runs left five auto-named test databases: `birknext_bksrc_*`, `birknext_evidence_manage_*`, `birknext_reset_*`, `birknext_tps_*` and `birknext_xfersrc_*`.
  - All of these can be dropped by the owner; they were not dropped here.
- Containers: the existing `birknext-studio-local_postgres_1` was started after the machine restart. No container was recreated.
- Not contacted: ports 5000/5173, external hosts or Azure DevOps. Not modified: `appsettings.Local.json` and `AIAssisted/testdata/`.
- Scratch API/web processes on :5097/:5197 were stopped after acceptance.

## 11. Commits

One local commit on `008-traceability-first` (not pushed), containing only Universal Test Plan Suggestions files:
- the contracts, backend, frontend and tests listed in section 2;
- `TestPlanSuggestions.razor.css`, `GuideGettingStarted.razor`, `DocumentationCatalog.cs` and `UserGuideContent.cs` (verification stamp);
- this report.

Left uncommitted: the navigation-loading task's files, `appsettings.Local.json`, `docs/audits/birknext-auto-integration-kode67-gap-register.md`, the other untracked audit documents and `AIAssisted/testdata/`.
