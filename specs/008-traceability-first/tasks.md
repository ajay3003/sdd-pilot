# Tasks: Traceability-First Workflow (Option B)

**Input**: Design documents from `specs/008-traceability-first/`
**Prerequisites**: plan.md ✓, spec.md ✓, data-model.md ✓, contracts/ ✓

**Tests**: Included — Constitution Principle I (Test-First Development) is NON-NEGOTIABLE.
Write each test task first, confirm it fails, then implement.

**Organization**: Tasks grouped by user story. US2 (auto-persist) precedes US1 (immediate
traceability) because auto-persist is the infrastructure US1 depends on.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Parallelizable — different files, no incomplete-task dependencies
- **[Story]**: User story label (US1–US6)

---

## Phase 1: Setup — Not Required

This feature modifies an existing project. No new project initialization needed.
Proceed directly to Phase 2.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Enum extension, model changes, and EF migration that ALL user stories depend on.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T001 [P] Add `AutoAccepted` value to `CandidateReviewStatus` enum in `AIAssisted/backend/BirkNext.Api/Models/CandidateReviewStatus.cs` — insert between `New` and `Accepted`; write serialization round-trip unit test first in `AIAssisted/backend/BirkNext.Api.Tests/`
- [ ] T002 [P] Locate frontend `CandidateReviewStatus` enum file by grepping `AIAssisted/frontend/BirkNext.Web/GraphQL/` for `CandidateReviewStatus`; add `AutoAccepted` to match backend; confirm frontend builds
- [ ] T003 Add nullable `CandidateId` (`Guid?`) field to `ReviewedCandidate` model in `AIAssisted/backend/BirkNext.Api/Models/ReviewedCandidate.cs`; add `Guid? CandidateId` to `SaveReviewedCandidateItemInput` and `ReviewedCandidateItem` in `AIAssisted/backend/BirkNext.Api/GraphQL/SaveReviewedCandidatesInput.cs` and `AIAssisted/backend/BirkNext.Api/Services/ReviewedCandidateService.cs`
- [ ] T004 Create EF migration for `CandidateId` column: run `dotnet ef migrations add AddCandidateIdToReviewedCandidates` in `AIAssisted/backend/BirkNext.Api/`; verify generated migration adds nullable `candidate_id uuid` column to `reviewed_candidates` table
- [ ] T005 [P] Add `candidateId` field to `GetReviewedCandidates.graphql` in `AIAssisted/frontend/BirkNext.Web/GraphQL/GetReviewedCandidates.graphql`
- [ ] T006 [P] Update `ExtractionCandidate` default `ReviewStatus` from `CandidateReviewStatus.New` to `CandidateReviewStatus.AutoAccepted` in `AIAssisted/frontend/BirkNext.Web/Models/ExtractionCandidate.cs`; write failing test `NewCandidate_DefaultsToAutoAccepted` in `AIAssisted/frontend/BirkNext.Web.Tests/`
- [ ] T007 Fix `ExtractionSessionSnapshot.ActiveViewMode` default from `ExtractionViewMode.Extraction` to `ExtractionViewMode.Traceability` in `AIAssisted/frontend/BirkNext.Web/Models/ExtractionSessionSnapshot.cs`; write failing test `AnalyzeSpec_DefaultsToTraceabilityCoverage` in `AIAssisted/frontend/BirkNext.Web.Tests/Components/ExtractionReviewListTests.cs` first

**Checkpoint**: Backend and frontend compile with `AutoAccepted` enum value; `ExtractionCandidate` defaults to `AutoAccepted`; snapshot defaults to Traceability view; migration exists.

---

## Phase 3: User Story 2 — Auto-Persist Session Artifacts (Priority: P1) 🎯 MVP

**Goal**: Analysis completion automatically persists all normalized artifacts to `reviewed_candidates` (server-side) without any tester action. Session survives browser restart.

**Independent Test**: Analyze a spec, close the browser, reopen — session artifacts are restored without re-analysis (for 7-day window). Traceability shows data with zero user actions after analysis.

### Tests for US2 (write FIRST — must FAIL before implementation)

- [ ] T008 [P] [US2] Write failing test `AnalyzeSpec_AutoPersistsNormalizedArtifacts` in `AIAssisted/frontend/BirkNext.Web.Tests/Components/ExtractionReviewListTests.cs` — mock `ISaveReviewedCandidatesMutation`; set `PipelineResult`; assert mutation called with all `AutoAccepted` candidates when `PipelineResult` first arrives
- [ ] T009 [P] [US2] Write failing test `AutoPersist_DoesNotDuplicateExistingArtifacts` in `AIAssisted/frontend/BirkNext.Web.Tests/Components/ExtractionReviewListTests.cs` — restore `InitialSession` with a `Rejected` candidate; set `PipelineResult`; assert mutation preserves `Rejected` status (does not overwrite with `AutoAccepted`)
- [ ] T010 [P] [US2] Write failing test `SessionArtifacts_SurviveRefresh` in `AIAssisted/frontend/BirkNext.Web.Tests/Services/ExtractionSessionServiceTests.cs` — save snapshot; assert `IsExpired` returns false for a snapshot timestamped 5 days ago (was failing with 2-hour expiry)
- [ ] T011 [P] [US2] Write failing test `ReopenSession_RestoresTraceability` in `AIAssisted/frontend/BirkNext.Web.Tests/Components/ExtractionReviewListTests.cs` — no `InitialSession` (simulates expired localStorage); mock `IGetReviewedCandidatesQuery` returning prior `Rejected` record; set `PipelineResult`; assert `Rejected` status applied to matching candidate by `CandidateId`

### Implementation for US2

- [ ] T012 [US2] Implement auto-persist in `ExtractionReviewList.razor` (`@code` section): in `OnParametersSetAsync`, when `PipelineResult` changes to a new non-null value (compare with `_previousPipelineResult`), merge candidates — preserve `ManuallyAccepted`/`Rejected`/`NeedsReview` statuses from `InitialSession` by `CandidateId`, set remaining to `AutoAccepted`; call `SaveReviewedCandidatesMutation` fire-and-forget; mark saved candidates `SaveState = Saved`; include `CandidateId` in each mutation item — file: `AIAssisted/frontend/BirkNext.Web/Components/ExtractionReviewList.razor`
- [ ] T013 [US2] Extend `ExtractionSessionService` session expiry from `TimeSpan.FromHours(2)` to `TimeSpan.FromDays(7)` in `AIAssisted/frontend/BirkNext.Web/Services/ExtractionSessionService.cs`
- [ ] T014 [US2] Implement server-side session restore in `ExtractionReviewList.razor`: when `PipelineResult` arrives and `InitialSession` is null (localStorage miss/expiry), call `IGetReviewedCandidatesQuery` for `(projectId, sessionId)`; apply `Rejected`/`NeedsReview`/`ManuallyAccepted` statuses to matching candidates by `CandidateId` before auto-persist — file: `AIAssisted/frontend/BirkNext.Web/Components/ExtractionReviewList.razor`
- [ ] T015 [US2] Add `SaveAcceptedArtifacts_NotRequiredForTraceability` test: render component with `PipelineResult` and no `InitialSession`; assert `TraceabilityView` is rendered (active view mode is Traceability) without any user click — file: `AIAssisted/frontend/BirkNext.Web.Tests/Components/ExtractionReviewListTests.cs`

**Checkpoint**: Analysis auto-persists to server. Session survives 7-day window. Prior manual review statuses survive re-analysis. Traceability opens by default.

---

## Phase 4: User Story 1 — Immediate Traceability After Analysis (Priority: P1)

**Goal**: Traceability & Coverage is the default view after analysis. Rejected artifacts are excluded. Needs Review artifacts are visible with a warning badge. No Accept/Save required.

**Independent Test**: Load `ExtractionReviewList` with a `PipelineResult` containing mixed-status candidates. Assert Traceability tab is active; assert Rejected candidates don't appear in matrix; assert NeedsReview candidates show badge.

### Tests for US1 (write FIRST — must FAIL before implementation)

- [ ] T016 [P] [US1] Write failing test `RejectedArtifacts_AreExcludedFromTraceability` in `AIAssisted/frontend/BirkNext.Web.Tests/Services/TraceabilityModelBuilderTests.cs` — build model with candidates where one has `Rejected` status; assert that candidate does not appear in requirements, tests, orphans, or gaps output
- [ ] T017 [P] [US1] Write failing test `NeedsReviewArtifacts_AreFlaggedOrHandledConsistently` in `AIAssisted/frontend/BirkNext.Web.Tests/Services/TraceabilityModelBuilderTests.cs` — build model with a `NeedsReview` requirement candidate; assert corresponding `TracedRequirement` has `NeedsReviewWarning = true`
- [ ] T018 [P] [US1] Write failing test `Traceability_WorksWithoutManualAcceptance` in `AIAssisted/frontend/BirkNext.Web.Tests/Components/ExtractionReviewListTests.cs` — render with `PipelineResult` candidates all having `AutoAccepted` status; assert `TraceabilityView` renders matrix and requirements with no Accept action taken
- [ ] T019 [P] [US1] Write failing test `ExistingAcceptedArtifacts_StillWorkAfterMigration` in `AIAssisted/frontend/BirkNext.Web.Tests/Components/ExtractionReviewListTests.cs` — restore session with legacy `Accepted` status candidates; assert they appear in Traceability (not filtered out)

### Implementation for US1

- [ ] T020 [P] [US1] Add `bool NeedsReviewWarning { get; init; }` property to `TracedRequirement` in `AIAssisted/frontend/BirkNext.Web/Models/TraceabilityModels.cs`
- [ ] T021 [US1] Update `TraceabilityModelBuilder.Build()` in `AIAssisted/frontend/BirkNext.Web/Services/TraceabilityModelBuilder.cs`: (1) at top of method, filter out candidates with `ReviewStatus == Rejected` before any partitioning; (2) when constructing `TracedRequirement`, set `NeedsReviewWarning = sourceCandidate.ReviewStatus == NeedsReview`
- [ ] T022 [US1] Add `NeedsReview` warning badge rendering in `AIAssisted/frontend/BirkNext.Web/Components/TraceabilityView.razor`: in requirement row rendering, when `NeedsReviewWarning == true` render `<span class="needs-review-badge" title="Extraction quality not confirmed — review in Extraction Review">Needs Review</span>`; add CSS class to `TraceabilityView.razor.css` (or existing stylesheet)

**Checkpoint**: Traceability opens by default. Rejected artifacts excluded. NeedsReview badge visible. Legacy Accepted artifacts unaffected. All US1 tests green.

---

## Phase 5: User Story 3 + User Story 4 — Extraction Review as Optional Advanced Mode (Priority: P2)

**Goal**: Document View renamed to "Extraction Review". Explanatory banner added. Accept/Reject/NeedsReview remain available inside this tab only. AutoAccepted/NeedsReview status badges display correctly.

**Independent Test**: Open Extraction Review tab; confirm explanatory banner is present; confirm tab is not the default; confirm accept/reject buttons are present inside this tab and absent outside it.

### Tests for US3 + US4 (write FIRST — must FAIL before implementation)

- [ ] T023 [P] [US3] Write failing test `ExtractionReview_IsOptional` in `AIAssisted/frontend/BirkNext.Web.Tests/Components/ViewBehaviorTests.cs` — render `ExtractionReviewList`; assert tab with label "Extraction Review" exists; assert it is NOT the active tab on initial render; assert tab labeled "Document View" does NOT exist
- [ ] T024 [P] [US3] Write failing test `ExtractionReviewBanner_IsShown` in `AIAssisted/frontend/BirkNext.Web.Tests/Components/ViewBehaviorTests.cs` — mount `DocumentView` component; assert element with `data-testid="extraction-review-banner"` exists
- [ ] T025 [P] [US4] Write failing test `ReviewStatusChange_RecalculatesTraceability` in `AIAssisted/frontend/BirkNext.Web.Tests/Components/ExtractionReviewListTests.cs` — change a candidate's review status; assert `_traceabilityRecalcNotice` notification is rendered when viewed from Traceability tab

### Implementation for US3 + US4

- [ ] T026 [US3] Rename "Document View" tab to "Extraction Review" in `AIAssisted/frontend/BirkNext.Web/Components/ExtractionReviewList.razor`: change tab label text (`📄Document View` → `🔍Extraction Review`); update `title` attribute to "Advanced: inspect and optionally correct extraction quality."; update any `data-testid` for the tab button if present
- [ ] T027 [P] [US3] Add explanatory banner to `AIAssisted/frontend/BirkNext.Web/Components/DocumentView.razor` at the top of the rendered output: `<div class="extraction-review-banner" data-testid="extraction-review-banner">Artifacts have already been extracted and are available in Traceability &amp; Coverage. Use this view only if you want to review or adjust extraction quality.</div>`
- [ ] T028 [US4] Implement "Traceability recalculated" notification: in `ExtractionReviewList.razor` `HandleReviewStatusChanged`, set a `private bool _traceabilityRecalcNotice = false` flag to `true`; render a dismissible `<div class="notification notification-info" data-testid="traceability-recalc-notice">Traceability recalculated after review changes.</div>` when flag is true and `_activeViewMode == Traceability`; auto-clear flag when user dismisses or switches views — file: `AIAssisted/frontend/BirkNext.Web/Components/ExtractionReviewList.razor`
- [ ] T029 [P] [US4] Update `ReviewStatusBadge.razor` in `AIAssisted/frontend/BirkNext.Web/Components/ReviewStatusBadge.razor` to handle `AutoAccepted` status: render badge with label "Auto-Accepted" and CSS class `review-status-badge-auto-accepted`; add corresponding CSS to the component stylesheet

**Checkpoint**: Document View is gone; Extraction Review tab is present but not default. Banner present inside Extraction Review. NeedsReview badge shows in Traceability. Recalculation notice appears. AutoAccepted badge renders.

---

## Phase 6: User Story 5 — UI Buttons and Workflow Banner (Priority: P2)

**Goal**: "Save Accepted Artifacts" renamed to "Publish to QA Library" throughout action bar. Workflow hint updated. Empty state text updated. No misleading "accept before save" framing.

**Independent Test**: After analysis, verify no primary button reads "Save Accepted Artifacts". Verify "Publish to QA Library" label is present in the action bar. Verify workflow hint text references Traceability first.

### Tests for US5 (write FIRST — must FAIL before implementation)

- [ ] T030 [P] [US5] Write failing test `PublishToLibrary_IsOptional` in `AIAssisted/frontend/BirkNext.Web.Tests/Components/ExtractionReviewListTests.cs` — render component in Extraction Review view mode with selected candidates; assert button with text "Publish to QA Library" exists; assert no button or element with text "Save Accepted Artifacts" exists
- [ ] T031 [P] [US5] Write failing test `WorkflowHint_ReferencesTraceabilityFirst` in `AIAssisted/frontend/BirkNext.Web.Tests/Components/ViewBehaviorTests.cs` — render `ExtractionReviewList`; find element `data-testid="analysis-workflow-hint"`; assert text contains "Traceability" and "Extraction Review"; assert it does NOT contain "Document View"

### Implementation for US5

- [ ] T032 [US5] Update all three button states in the action bar to use "Publish to QA Library" naming in `AIAssisted/frontend/BirkNext.Web/Components/ExtractionReviewList.razor` (lines ~487–523): (1) disabled state: "Save Accepted Artifacts" → "Publish to QA Library"; (2) active save state: "Save @N Accepted Artifact@(s)" → "Publish @N Artifact@(s) to QA Library"; (3) disabled-nothing-selected state: "Save Accepted Artifacts" → "Publish to QA Library"; (4) update `title` tooltip: "Accept selected artifacts before saving them" → "Review artifacts in Extraction Review, then publish to QA Library."
- [ ] T033 [US5] Update `analysis-workflow-hint` text in `AIAssisted/frontend/BirkNext.Web/Components/ExtractionReviewList.razor` (line ~128): new text: `"Traceability &amp; Coverage is your primary workspace — review coverage and gaps immediately after analysis. Use Extraction Review only if you want to inspect or correct extraction quality."`
- [ ] T034 [US5] Update empty-state flow text in `AIAssisted/frontend/BirkNext.Web/Components/ExtractionReviewList.razor` (line ~29): change `"Review &rarr; Accept &rarr; Save &rarr; Track on Dashboard"` to `"Analyze &rarr; Traceability &amp; Coverage &rarr; Investigate Gaps"`

**Checkpoint**: No "Save Accepted Artifacts" label anywhere in the UI. "Publish to QA Library" appears in the action bar. Workflow hint references Traceability first. Empty state no longer implies accept-before-save.

---

## Phase 7: User Story 6 — Recommended Workflow and User Guide (Priority: P3)

**Goal**: RecommendedWorkflow page Phase 2 updated. No wording implies Accept/Save is required before Traceability. Session vs Published artifacts distinction explained.

**Independent Test**: Open `/getting-started` page. Read Phase 2. It must describe Extraction Review as optional and Traceability as immediately available. Phase 3 must not reference saved artifacts as a prerequisite.

### Tests for US6 (write FIRST — must FAIL before implementation)

- [ ] T035 [P] [US6] Write failing test `UserGuide_ExplainsTraceabilityFirstWorkflow` in `AIAssisted/frontend/BirkNext.Web.Tests/Pages/RecommendedWorkflowTests.cs` (create file if not present) — render `RecommendedWorkflow` page; find Phase 2 section; assert it contains "optional" or "Optional"; assert it does NOT contain "Accept or reject each candidate" as a required step
- [ ] T036 [P] [US6] Write failing test `LibraryContainsOnlyPublishedArtifacts` in `AIAssisted/frontend/BirkNext.Web.Tests/Pages/RecommendedWorkflowTests.cs` — render page; find Phase 2; assert "Publish to QA Library" or "publish" appears; assert no Phase 2 or Phase 3 text implies "saved artifacts" are required for Traceability

### Implementation for US6

- [ ] T037 [US6] Rewrite Phase 2 of `AIAssisted/frontend/BirkNext.Web/Pages/RecommendedWorkflow.razor`:
  - Phase name: "Build QA Knowledge" → "Extraction Review (Optional)"
  - Goal: "Inspect and optionally correct extraction quality. Traceability &amp; Coverage works without this step."
  - Actions list: (1) "Traceability &amp; Coverage is available immediately after analysis — start there." (2) "Open Extraction Review only if you want to inspect or correct extraction quality." (3) "Reject artifacts to exclude them from coverage; mark Needs Review to flag uncertain ones." (4) "Use Publish to QA Library to permanently preserve a curated artifact set."
  - Outcome: "Extraction quality corrections applied. Testers who skip this phase have full Traceability access immediately."
  - CTA link: keep `href="scenarios"` but update label to `Open QA Library →`
- [ ] T038 [US6] Update Phase 3 in `AIAssisted/frontend/BirkNext.Web/Pages/RecommendedWorkflow.razor`: remove any wording that implies Phase 2 (accept/save) must happen first; update outcome text: "Full visibility of coverage across session artifacts — requirements covered, gaps identified, traceability established." (remove "saved artifacts" reference)

**Checkpoint**: Recommended Workflow page Phase 2 is clearly optional. Traceability is described as immediately available. QA Library is described as a curated/published store, not a prerequisite.

---

## Phase 8: Polish & Regression

**Purpose**: Regression coverage, cross-cutting verification, final cleanup.

- [ ] T039 [P] Write and run `AnalyzeSpec_DefaultsToTraceabilityCoverage` regression test confirming `_activeViewMode` is `Traceability` on initial render with non-null `PipelineResult` — file: `AIAssisted/frontend/BirkNext.Web.Tests/Components/ExtractionReviewListTests.cs`
- [ ] T040 [P] Verify all frontend tests pass: `dotnet test AIAssisted/frontend/BirkNext.Web.Tests/`
- [ ] T041 [P] Verify backend tests pass including new `AutoAccepted` serialization test: `dotnet test AIAssisted/backend/BirkNext.Api.Tests/`
- [ ] T042 Search for any remaining occurrences of "Document View" in `.razor`, `.cs`, `.css` files in `AIAssisted/frontend/BirkNext.Web/` (excluding git history and plan files); update any found references to "Extraction Review"
- [ ] T043 [P] Verify EF migration applies cleanly: `dotnet ef database update` in `AIAssisted/backend/BirkNext.Api/`; confirm `reviewed_candidates` table has nullable `candidate_id` column; confirm existing rows load without error

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 2 (Foundational)**: No dependencies — start immediately; BLOCKS everything else
- **Phase 3 (US2 — Auto-Persist)**: Depends on Phase 2 complete
- **Phase 4 (US1 — Traceability)**: Depends on Phase 2; can run in parallel with Phase 3 for D1/D2 tasks (different files)
- **Phase 5 (US3+US4 — Extraction Review)**: Depends on Phase 3 complete
- **Phase 6 (US5 — UI Buttons)**: Depends on Phase 3 complete; can run in parallel with Phase 5
- **Phase 7 (US6 — User Guide)**: Independent of Phase 3–6; can run at any time after Phase 2
- **Phase 8 (Polish)**: Depends on all prior phases complete

### User Story Dependencies

- **US2 (P1)**: Depends on Foundational complete — critical path
- **US1 (P1)**: Depends on Foundational; D1/D2 (filter + model) can start in parallel with US2 implementation
- **US3+US4 (P2)**: Depend on US2 complete (auto-persist must work before Extraction Review tab changes are meaningful)
- **US5 (P2)**: Depends on US2 complete; independent of US3/US4
- **US6 (P3)**: Fully independent — can work in parallel after Phase 2

### Parallel Opportunities (within Phase 2)

```
T001 (backend enum)   ← parallel with T002 (frontend enum), T005 (graphql), T006 (candidate default), T007 (snapshot default)
T003 (model + input)  ← must precede T004 (migration)
```

### Parallel Opportunities (within Phase 3)

```
T008 / T009 / T010 / T011  ← all test stubs, fully parallel
T013 (session restore)     ← parallel with T012 (expiry extension)
```

### Parallel Opportunities (within Phase 4)

```
T016 / T017 / T018 / T019  ← all test stubs, fully parallel
T020 (model change)        ← parallel with T019 above
T021 (builder)             ← depends on T020
T022 (badge in view)       ← depends on T021
```

---

## Implementation Strategy

### MVP Scope (US2 + US1 only — delivers core acceptance criteria)

1. Complete Phase 2 (Foundational)
2. Complete Phase 3 (US2 — Auto-Persist)
3. Complete Phase 4 (US1 — Traceability)
4. **STOP and validate**: Traceability shows data immediately after analysis; rejected artifacts excluded; session persists 7 days
5. All AC ✓: "Traceability works immediately" / "No Accept required" / "Default view is Traceability"

### Full Delivery (adds Extraction Review polish + User Guide)

1. MVP above
2. Phase 5 (US3+US4 — Extraction Review tab + banner)
3. Phase 6 (US5 — Publish to QA Library naming)
4. Phase 7 (US6 — Recommended Workflow)
5. Phase 8 (Polish + regression)

### Parallel Team Strategy

After Phase 2:
- **Dev A**: Phase 3 (US2 — Auto-Persist): T008–T015
- **Dev B**: Phase 4 (US1 — Traceability filter): T016–T022 (only `TraceabilityModelBuilder` and `TraceabilityView` — no conflicts with Dev A)
- **Dev C**: Phase 7 (US6 — User Guide): T035–T038 (fully independent)

---

## Notes

- Write each test task to FAIL before implementing the feature it covers (Red-Green-Refactor)
- `ExtractionReviewList.razor` is touched by multiple tasks — coordinate changes carefully (T007, T012, T014, T026, T028, T032, T033, T034); consider implementing in the order listed to minimize merge conflicts
- EF migration (T004) must be created before running backend tests that hit the database
- `CandidateId` is nullable in `ReviewedCandidate` — existing tests that construct `ReviewedCandidate` without it must still pass
- The frontend GraphQL enum file location must be confirmed in T002 before downstream tasks reference it
- `ExtractionViewMode.Architecture` and `ExtractionViewMode.SpecExplorer` are unaffected by this feature
