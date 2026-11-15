# Regression stabilization and saved test cases in Test Coverage / Requirements Traceability

Date: 2026-11-15 · Branch `008-traceability-first` · Starting HEAD `1acb8b5862ba0a5884512cc0b7a4bd87c261d3da` (Universal Test Plan Suggestions, verified present)
Nothing was pushed.

## 1. Outcome

| Area | Status |
|---|---|
| Backend regression (partitions 1–6) | **PASS**: 6,787 passed, 19 skipped, 0 failed |
| Full frontend suite (in the working tree) | **PASS**: 9,030 / 9,030. Includes the uncommitted navigation work and its test fixes |
| Navigation test fixes | Fixed and verified, **not committed**: committing them requires committing the navigation task's files, which was refused (section 3) |
| Saved test cases in Test Coverage and Requirements Traceability | Implemented, unit/bUnit-tested, browser-verified |
| Browser acceptance (fresh Release builds, disposable database) | **PASS**: 106 / 106; no page or console errors, no 5xx responses, no guard violations |
| API and Web Release publish (isolated copy) | **PASS**: 0 errors; 86 / 86 WebAssembly assets match the boot manifest |

## 2. Baseline (before any change)

The code at `1acb8b58` is the code the previous session tested; only its report changed after those runs.

| Run | Passed | Skipped | Failed |
|---|---|---|---|
| Full frontend | 9,021 | 0 | 3 (navigation) |
| Backend partitions 1–5 | 4,254 | 11 | 0 |
| Backend partition 6 | 2,521 | 8 | 3 |

## 3. Regression failures: root causes and fixes

No production code was changed for any of the six failures. No assertion was removed or weakened, and no test was skipped.

**Backend (commit `a1e4e977`)**

| Test | Root cause | Kind | Fix |
|---|---|---|---|
| `ManagedEdgeCdpTests.BridgeHasNoAuthenticationExtractionOrBrowserCloseCode`, `ManagedEdgePreflightTests.LaunchCodeNeverTerminatesOrEnumeratesEdgeProcesses` | Both found the source root as the first folder above `bin` that contains a folder named `AIAssisted`. A stale, file-less `bin/Debug/net8.0/AIAssisted/` tree (created 2026-10-09, not by this work) stopped the search inside `bin`. | Fragile test | New `Infrastructure/SourceTree` walks up to the first folder where the *requested file* exists. The stale folder was left in place; both tests pass with it present. |
| `AdminServiceRuntimeSourceTests.SourceRunHasNoPackagingMetadata_AndNothingIsInvented` | Since `edae017a` (application versioning), `CommitSha` comes only from the compiled build identity. `BirkNext.BuildIdentity.targets` (input 3) gives a developer build inside git the working tree's commit, by design. | Outdated expectation | The test asserts `CommitSha` equals the compiled identity, and is a 40-hex commit when present. Build number and package root still must be absent. |
| `UnattendedAltinnAuthenticationTests.OneClick_…` (new, seen once in partition 2; 0 of 25 reruns) | The "no 11-digit identifier is stored" check removed only the run id's `N` form. The run id is also stored as a dashed GUID, whose last 12-hex block holds an exact 11-digit run about 0.4 % of the time. Three sibling tests had the same latent defect. | Flaky test (chance) | New `Infrastructure/PersistedText.HasElevenDigitNumber` removes every GUID (dashed or 32-hex) before searching. The explicit "reporter/child identifier never stored" assertions are unchanged. |

**Frontend (fixed in `BirkNext.Web.Tests/Layout/NavigationProgressTests.cs`; not committed)**

| Test | Root cause | Kind | Fix |
|---|---|---|---|
| `BrowserHistoryNavigation_NotIntercepted_IsTrackedTheSameWay`, `RapidNavigation_OnlyTheLatestCanCompleteOrReveal` | `NavigationGate.Release` assumed the navigation finishes inline. The framework's `NotifyLocationChangingAsync` and bUnit's `NavigateToCore` resume on the renderer dispatcher, so `LocationChanged` happened after `Release` returned, and the assertions raced it. | Test harness defect | `Release` now waits, bounded at 5 s and failing loudly, for the observable decision: `LocationChanged` for that path, or bUnit's history entry marked `Prevented`. The `intercepted` observer subscribes before the gate, which the gate documents. |
| `CancelledNavigation_ClearsPending_AndRestoresTheActiveSidebarItem` | `a[href='dashboard']` matched the navbar brand link first (`NavMenu.razor:17`), which never carries `aria-current`. The navigation-loading production code is correct. | Wrong selector | The test selects the sidebar row, `a.nav-link[href=…]`. |

Verification of the navigation fixes:
- Navigation and menu tests: 72 / 72 on 10 consecutive runs.
- Full frontend suite: 9,024 / 9,024 on 3 consecutive runs.

**Not committed.** `NavigationProgressTests.cs` is untracked and tests the uncommitted navigation-loading work (`NavigationProgress.cs`, `NavMenu.razor`, `MainLayout.razor` and others). A commit containing the test fixes together with that work was refused, because it would commit another task's files. Both remain in the working tree, unchanged except for the test file. The navigation owner should commit the feature together with these test fixes.

## 4. Saved test cases in Test Coverage and Requirements Traceability

### What already existed
- **MT-7 reviewed requirement coverage** (`RequirementCoverageService`, Traceability & Coverage): guide scenarios only. It reads the canonical interpretation of a Target Environment scope.
- **Test Coverage Review** (`TestCoverageReviewService`): reads all drafts, but only to find Test Data of cases linked to a Critical E2E flow (`TestCoverageDataLinks.LinksFlow`).
- **Requirements Traceability** (`ArtifactTraceability.razor`): a browser-side document chain (Constitution → Specification → Plan → Tasks).
- **Project plans:** `TestPlanDraft` with owner `project:{key}`. The project key is the workspace `ProjectSlug`, the same key Test Coverage Review receives as `ProjectKey` and the explorers as `CurrentScope`.

### Decisions
1. **One rule, no new engine or store.** `PreparedCaseTraceability.Build` (pure) turns a project's stored plans into a `PreparedCaseTraceView`.
   - Test Coverage Review stores it in its result as `PreparedCases` (additive JSON; older results show "run the review again").
   - Requirements Traceability reads the same view through `POST api/test-plan-suggestions/project/traceability`.
   - One panel component (`PreparedCaseTracePanel`) renders both.
2. **One identity per saved case:** `{draft id:N}/{case id}`. The drill-down goes to `test-plan-suggestions?draft=…&area=prepare&case=…`; the page opens that case (additive `case` query parameter).
3. **Evidence-backed links only.** A requirement counts only when the case's own discovery evidence names it, and the file matches the reviewed specification file (paths normalised). If an id list is given, the id must be in it.
   - **Never counted:** title or wording similarity. In acceptance, a scenario whose sentence says "(FR-001)" stays unlinked, because the specification parser links scenarios to requirements only by explicit scenario ids.
   - **Never counted:** guide business rules (MT-7 owns guide scenarios), journey-declared ids (no specification file), and excluded or superseded cases.
   - **Visible:** every saved case, with the reason it does or doesn't count.
4. **States.**
   - Suggested = discovery candidate, not saved, never shown here.
   - Prepared = Generated / Under review.
   - Prepared — needs clarification.
   - Approved = Accepted for preparation.
   - Excluded / Superseded: shown, not counted.
   - Executed: not supported for prepared cases, so every case shows "Not executed" and the panel says "Executed: 0 — prepared cases are not executions".
5. **No double counting.** A requirement is counted once, however many cases or plans cover it; each saved case keeps its own identity. A case prepared from a specification acceptance scenario names that scenario id, the same id Test Coverage lists as a QA test, so the two are linked, not added. Critical E2E flow links are shown with the note that the flow's runs belong to the flow.
6. **Project isolation.** Only plans with owner `project:{key}` *and* `ProjectKey == key` are read. Guide plans and other projects' plans are never included.
7. **Existing calculations untouched.** MT-7, Test Coverage's tests, QA tests, behaviours, dimensions and gaps are unchanged; the new view is a separate section.
8. **Compatibility.** No schema change and no migration: new contract fields are additive and nullable. Stored drafts are read as they are.

### Changed files

| File | Change |
|---|---|
| `shared/TestPlanSuggestionContracts.cs` | `PreparedCaseStage`, `PreparedCaseLinkBasis`, `PreparedCaseRequirementLink`, `PreparedCaseTrace`, `PreparedRequirementTrace`, `PreparedCaseTraceView`, `PreparedCaseTraceRequest` |
| `shared/TestCoverageContracts.cs` | `TestCoverageReviewResult.PreparedCases` (nullable) |
| `Services/TestPlanning/PreparedCaseTraceability.cs` | The rule (new, pure) |
| `Services/TestPlanning/TestPlanSuggestionService.cs` | `ProjectDraftsAsync`, `PreparedCaseTraceAsync` |
| `Controllers/TestPlanSuggestionsController.cs` | `POST project/traceability` (read-only) |
| `Services/TestCoverage/TestCoverageReviewService.cs` | Adds `PreparedCases` to each run for the request's project |
| `Components/PreparedCaseTracePanel.razor(.css)` | Shared panel (new) |
| `Pages/TestCoverageReview.razor` | Panel at the top of the Requirements view |
| `Pages/ArtifactTraceability.razor` | Panel on the Overview tab, for the selected specification |
| `Pages/TestPlanSuggestions.razor` | `case` query parameter opens that case |
| `Services/TestPlanSuggestionsApiService.cs` | `PreparedCaseTraceAsync` |

### Tests added (15, all passing)
- **Backend (`PreparedCaseTraceabilityTests`, 8):**
  - counted link and drill-down;
  - never executed;
  - unlinked visible but not counted;
  - counted once across cases and plans;
  - only this project's plans;
  - links outside the specification, guide rules, journey ids and excluded cases not counted;
  - no specification given;
  - path normalisation.
- **Backend (`TestPlanSuggestionServiceTests`, 1):** real Postgres. Discover → save → accept → reload in a fresh context → trace. Covers another project and the Skolenærvær guide plan unchanged.
- **Frontend (6):**
  - Test Coverage Requirements view: drill-down href, not executed, boundary, unlinked case, older result.
  - Panel explains a not-counted link.
  - Requirements Traceability traces an imported project's cases with the project's own key and specification path; the document traceability result is unchanged.
  - The plan page opens the case named in the link.

## 5. Regression after all changes (final code)

| Run | Passed | Skipped | Failed | Status |
|---|---|---|---|---|
| Backend partition 1 | 1,052 | 2 | 0 | PASS |
| Backend partition 2 | 1,099 | 5 | 0 | PASS |
| Backend partition 3 | 888 | 0 | 0 | PASS |
| Backend partition 4 | 676 | 1 | 0 | PASS |
| Backend partition 5 | 539 | 3 | 0 | PASS |
| Backend partition 6 | 2,533 | 8 | 0 | PASS |
| **Backend total** | **6,787** | **19** | **0** | **PASS** |
| **Full frontend suite** | **9,030** | 0 | 0 | **PASS** (working tree, including the uncommitted navigation work) |
| Focused: backend TestPlanning + TestCoverage | 89 | 0 | 0 | PASS |
| Focused: frontend TPS, TestCoverage, Requirements Traceability, MT-7 panel | 115 | 0 | 0 | PASS |
| Navigation + menu tests (×10) | 72 | 0 | 0 | PASS |
| API / Web Release publish | — | — | 0 errors | PASS |

## 6. Browser acceptance

**Setup**
- Release builds published from a clean source copy outside the repository.
- API on `localhost:5097`, web on `localhost:5197`.
- Disposable database `birknext_integ_acc2_20261115`, from `TEMPLATE birknext_pts_acc_20261115`; the template was only read.
- The repository network guard blocked ports 5000/5173 and all external hosts.

**Result: 106 / 106.** These are the 94 earlier checks plus 12 integration checks (I1–I11 and I5b).

**Seven project categories**
- **Skolenærvær guide plan:** guide plan intact, Source baseline shown, "Business rules (guide)" label (MT-7), unsaved marker shown.
- **M2LB project:** full workflow; readiness "Required and available" / "Needs clarification".
- **Generic Blazor (Lendo):** 10 UI candidates.
- **API-only (Parcelo):** 6 candidates.
- **Documentation-only (Roomly):** 7 business candidates with plain titles.
- **Mixed (LendoPlus):** 18 candidates.
- **Insufficient evidence (Opaque):** a hand-written case persists.

**Viewports.** No horizontal overflow at 1440, 1280, 1024 and 768 px on all four steps. At 768 px each step input is 230 px wide, with no overlap.

**Integration (LendoPlus)**
- A scenario case (its text mentions FR-001), the FR-001 requirement case and the FR-003 requirement case were prepared; the FR-003 case was approved.
- **Test Coverage → Requirements:**
  - `FR-001 Prepared, not approved yet (0 approved, 1 prepared)` and `FR-003 Approved test case (1 approved, 0 prepared)`, each counted once.
  - The scenario case is listed, unlinked and not counted.
  - All 3 cases show "Not executed"; Executed: 0.
  - The drill-down opened the FR-003 case in its plan.
- **Requirements Traceability:** the same two rows for the selected specification.

**Integration (Opaque).** Its hand-written case appears in Test Coverage: 1 case, 1 unlinked, no requirement row, no other project's case.

The first integration run (102 / 104) expected the scenario's "(FR-001)" wording to create a link. The parser links only explicit scenario ids, so the expectation was corrected, not the code. The corrected run used a new disposable database.

Evidence: session scratchpad `acc/utps-result-GPBADMERV.json` and `acc/shots-utps/` (21 screenshots, including `I-coverage`, `I-traceability` and `I-opaque-coverage`).

## 7. Remaining gaps (follow-ups; scope not expanded)

**F1. Discovery persistence across API restarts.**
- **Where:** `ScenarioDiscoveryCache` (`Services/TestPlanning/TestScenarioDiscoveryService.cs`) is in memory. `TestPlanSuggestionService` returns 409 `discovery-missing` after a restart.
- **Acceptance criteria:**
  1. A discovery result survives a restart (stored per project + snapshot with rule version `tsd-1`).
  2. A stored result whose evidence fingerprint changed is shown as outdated.
  3. Candidate ids are identical before and after.
  4. Saved plans are unaffected.

**F2. React / Angular UI analysis.**
- **Where:** `UiSourceScenarioProvider` (`TestScenarioDiscoveryService.cs`) reads `UiSurface`, which the existing frontend analyzer fills from Razor / HTML only.
- **Acceptance criteria:**
  1. JSX/TSX and Angular templates produce pages, forms and validation in `UiSurface`; the provider needs no change.
  2. A React fixture yields UI functional candidates.
  3. Nothing is invented when a template is not understood.

**F3. Possible-duplicate notes across evidence types.**
- **Where:** `TestScenarioDiscoveryEngine.Finalize` notes duplicates only between capabilities that set requirement ids (specification, guide, journeys); UI and contract candidates carry none.
- **Acceptance criteria:**
  1. A fixture with a specification and a journey naming the same `FR-nnn` shows "Possible duplicate" on both, each naming the other, both still selectable.
  2. UI and contract candidates gain requirement ids only from explicit references (e.g. a `[Trait("Requirement", …)]`-like annotation or an OpenAPI extension), never from wording.

**F4. Manual requirement links (new, from this work).**
- **Where:** `TestCaseEdits`. Hand-written cases cannot name a requirement, so they are always "not linked".
- **Acceptance criteria:**
  1. A tester can link a case to a requirement of the selected specification, recorded as a reviewer decision with who and when.
  2. It is shown with its basis ("linked by a tester") and counted separately from evidence-based links.

**F5. MT-7 and project plans.**
- **Where:** `RequirementCoverageService` covers guide scenarios in a Target Environment scope only. Project cases appear in Test Coverage and Requirements Traceability, not in the MT-7 panel.
- **Acceptance criterion:** if MT-7 should include project cases, map the specification file + requirement id to the canonical requirement identity (module + feature + id) through the interpretation registry, never by id alone.

**R1. Navigation work uncommitted.** The full frontend suite passes only with the uncommitted navigation work and its test fixes in the working tree.

## 8. Temporary databases (none dropped)

All are absent from the session-start inventory and are disposable; none was dropped here.

| Database | Origin |
|---|---|
| `birknext_utps_acc_20261115`, `birknext_utps_final_20261115`, `birknext_utps_verify_20261115`, `birknext_integ_acc_20261115`, `birknext_integ_acc2_20261115` | Acceptance databases created by this work (template copies) |
| `birknext_bksrc_3d25035bd92e`, `birknext_evidence_manage_5f9a0b348dc6`, `birknext_reset_5b22c098a126`, `birknext_tps_54493cc4002f`, `birknext_xfersrc_926f2a1b2434` | Left by backend test hosts killed during the earlier low-memory event; later runs created none |

To remove them (container `birknext-studio-local_postgres_1`):

`DROP DATABASE <name>;` for each name above, run with `psql -U birknext -d postgres`.

The test servers on `:5097` / `:5197` were stopped.

## 9. Commits (local only)

- `a1e4e977`: regression stabilization (backend tests).
- The integration commit, which contains this report.
- Navigation work and its test fixes: not committed (section 3).
