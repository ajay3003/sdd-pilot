# Implementation Plan: Traceability-First Workflow (Option B)

**Branch**: `008-traceability-first` | **Date**: 2026-06-16 | **Spec**: [spec.md](./spec.md)

## Summary

Session artifacts and QA Library artifacts are separate concerns. Analysis results auto-persist to the `reviewed_candidates` table (Session Artifact Store). Traceability reads from session candidates in memory, populated on analysis completion and restored from server on session reload. The QA Library (`scenarios` table) receives only deliberately published artifacts. Accept/Reject lives inside an optional "Extraction Review" tab (renamed from "Document View"). No Accept/Save step is required before Traceability works.

---

## Architecture Decision Record

| Concept | Store | Populated by | Required for Traceability |
|---------|-------|--------------|---------------------------|
| Session Artifacts | `reviewed_candidates` table (server-side) | Auto-persist on analysis complete | YES — source of truth |
| QA Library | `scenarios` table (server-side) | "Publish to QA Library" action (explicit) | NO |
| Browser cache | localStorage (`birknext:extraction:session`) | Auto-save on every state change | NO — fast-path only |

**Traceability reads from in-memory session candidates**, which are populated at analysis time and restored from the server on session reload (localStorage miss or expiry).

---

## Technical Context

**Language/Version**: C# (.NET 8), Blazor WebAssembly
**Primary Dependencies**: HotChocolate GraphQL (backend), bUnit (frontend tests), EF Core 8
**Storage**: PostgreSQL — `reviewed_candidates` (session artifacts), `scenarios` (QA Library)
**Testing**: xUnit + bUnit
**Target Platform**: Browser (Blazor WASM) + ASP.NET Core API
**Project Type**: Web application — separate backend and frontend
**Performance Goals**: Auto-persist completes asynchronously in background; Traceability renders immediately from in-memory candidates without waiting for persist confirmation
**Constraints**: Backward-compatible enum extension; EF migration required for `CandidateId` column on `reviewed_candidates`; localStorage expiry extended from 2 hours to 7 days

---

## Constitution Check

### I. Test-First Development ✓
All task groups ordered: write failing test → implement → green. Every FR has a named test in the task list.

### II. Observability ✓
Auto-persist reuses the existing `ReviewBatchSaved` structured log event in `ReviewedCandidateService`. Session restore from server adds a new log event: `SessionRestoredFromServer`. No new service boundaries.

### III. Security-First ✓
No new auth or data-access boundaries. Auto-persist calls the existing `SaveReviewedCandidatesMutation` with the existing project-scoped authorization model. `CandidateId` addition is a nullable additive column.

### Development Standards — API Contract ✓
`CandidateReviewStatus` enum: additive change (`AutoAccepted`). `SaveReviewedCandidatesInput`: additive field (`CandidateId?: Guid`). `ReviewedCandidate` GraphQL type: additive field (`candidateId?: Guid`). All changes are backward-compatible — existing clients that omit `CandidateId` continue to work.

**Gate result**: All gates pass.

---

## Project Structure

### Source Code (affected files)

```text
AIAssisted/
├── backend/BirkNext.Api/
│   ├── Models/
│   │   ├── CandidateReviewStatus.cs           ← add AutoAccepted
│   │   └── ReviewedCandidate.cs               ← add CandidateId (Guid?)
│   ├── GraphQL/
│   │   └── SaveReviewedCandidatesInput.cs     ← add CandidateId (Guid?) to item input
│   ├── Services/
│   │   └── ReviewedCandidateService.cs        ← SaveBatchAsync: upsert-with-status-guard
│   └── Migrations/
│       └── [timestamp]_AddCandidateIdToReviewedCandidates.cs   ← new EF migration
│
└── frontend/BirkNext.Web/
    ├── GraphQL/
    │   ├── [CandidateReviewStatus enum file]   ← add AutoAccepted
    │   ├── GetReviewedCandidates.graphql       ← add candidateId field to query
    │   └── SaveReviewedCandidates.graphql      ← add candidateId field to input
    ├── Models/
    │   ├── ExtractionCandidate.cs              ← default ReviewStatus → AutoAccepted
    │   ├── ExtractionSessionSnapshot.cs        ← ActiveViewMode default → Traceability
    │   └── TraceabilityModels.cs               ← add NeedsReviewWarning to TracedRequirement
    ├── Services/
    │   ├── ExtractionSessionService.cs         ← extend expiry: 2h → 7d; server-fallback interface
    │   └── TraceabilityModelBuilder.cs         ← filter Rejected; set NeedsReviewWarning
    ├── Components/
    │   ├── ExtractionReviewList.razor           ← auto-persist, tab rename, action bar, hints
    │   └── DocumentView.razor                  ← add extraction-review banner
    └── Pages/
        └── RecommendedWorkflow.razor           ← Phase 2 rewrite

AIAssisted/frontend/BirkNext.Web.Tests/
├── Components/
│   ├── ExtractionReviewListTests.cs            ← update + new traceability-first tests
│   └── ViewBehaviorTests.cs                    ← tab rename assertions
└── Services/
    └── TraceabilityModelBuilderTests.cs        ← Rejected-filter + NeedsReview-badge tests
```

---

## Research Findings

### R1 — Frontend CandidateReviewStatus enum location
The frontend uses `CandidateReviewStatus` from `BirkNext.Web.GraphQL` namespace (imported in `ExtractionCandidate.cs`). Two `.graphql` files reference it: `GetReviewedCandidates.graphql` and `SaveReviewedCandidates.graphql`. The enum is either in a StrawberryShake-generated file or a hand-authored mirror. Locate it by grepping the `BirkNext.Web/GraphQL/` directory for `CandidateReviewStatus`. Update both backend and frontend enums in the same commit.

### R2 — Session durability strategy
`ExtractionSessionService` uses localStorage with a **2-hour expiry**. localStorage persists across browser restarts (it is not session storage); the expiry is an application-level check in `IsExpired()`. Extending to 7 days covers the "browser restart" and "reopen same session" requirements without any server-query complexity changes.

For the server-side restore fallback (when localStorage is genuinely cleared or expired):
- `GetReviewedCandidates.graphql` already exists on the frontend
- It queries `reviewedCandidates(projectId, sessionId)` which returns review statuses
- Missing: `candidateId` field — once added (Task A3), the frontend can reconstruct `CandidateId → ReviewStatus` mapping and apply it when re-analysis results arrive

Session restore flow after localStorage expiry:
1. User re-opens page → no localStorage snapshot
2. Page loads analysis form (no prior candidates shown — need to re-analyze)
3. User pastes/imports spec and runs analysis → `PipelineResult` arrives with fresh `ExtractionCandidate` list
4. Before auto-persist: frontend queries `GetReviewedCandidates(projectId, sessionId)` for the last known session
5. Matches by `CandidateId`, applies saved review statuses to the fresh candidates
6. Then auto-persists the merged list

This is the "merge prior statuses on re-analysis" flow. The session ID persists in localStorage separately (it is a small string that survives most storage scenarios).

### R3 — SaveBatchAsync upsert-with-status-guard
Current `SaveBatchAsync` does a full delete-and-replace. For auto-persist, we need to preserve manually-reviewed statuses. Two approaches:
- **Frontend merge** (simpler): frontend sends the already-merged list (preserving ManuallyAccepted/Rejected/NeedsReview from InitialSession), so backend still does full replace
- **Backend upsert** (more robust): backend checks existing row status before overwriting

**Decision**: Frontend merge. The frontend has all the information needed (PipelineResult candidates + InitialSession restored statuses). The backend replace-all pattern is unchanged. This avoids adding upsert complexity to the service.

### R4 — "Publish to QA Library" naming
The existing "Save Accepted Artifacts" button calls `CreateScenariosAsync` mutation, which creates `Scenario` records (the QA Library). Renaming to **"Publish to QA Library"** is accurate and clear. The button label, disabled state tooltip, and any related help text all update together. The mutation itself is unchanged.

### R5 — TraceabilityModelBuilder filter
Apply at the top of `Build()` before partitioning:
```csharp
var activeCandidates = candidates
    .Where(c => c.ReviewStatus != CandidateReviewStatus.Rejected)
    .ToList();
```
Use `activeCandidates` throughout instead of `candidates`.

`NeedsReviewWarning` set on `TracedRequirement` when source candidate `ReviewStatus == NeedsReview`.

---

## Data Model Changes

### CandidateReviewStatus (backend + frontend mirror)

```csharp
public enum CandidateReviewStatus
{
    New,            // Legacy: Unreviewed. Included in Traceability. No change.
    AutoAccepted,   // NEW: auto-persisted after analysis. Included in Traceability.
    Accepted,       // ManuallyAccepted. Included in Traceability.
    Rejected,       // Excluded from Traceability.
    NeedsReview,    // Included in Traceability with warning badge.
}
```

### ReviewedCandidate (backend model — additive)

```csharp
public Guid? CandidateId { get; set; }   // NEW — stable pipeline identity for deduplication
```

Nullable for backward compatibility — existing rows have `null`; new auto-persist rows have the pipeline `CandidateId`.

### SaveReviewedCandidateItemInput (additive)

```csharp
public record SaveReviewedCandidateItemInput(
    Guid? CandidateId,   // NEW — nullable for backward compat
    string Title,
    ScenarioKind Classification,
    CandidateReviewStatus ReviewStatus,
    string? SourceDocument,
    string? SourceSection,
    string ProjectId,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt);
```

### TracedRequirement (frontend model — additive)

```csharp
public bool NeedsReviewWarning { get; init; }  // NEW
```

### ExtractionSessionSnapshot (frontend model)

```csharp
// Change default:
public ExtractionViewMode ActiveViewMode { get; init; } = ExtractionViewMode.Traceability;  // was: Extraction
```

### EF Migration required
Add nullable `candidate_id` (`uuid`) column to `reviewed_candidates` table.

---

## Implementation Groups

Tasks ordered: write failing test → implement → green. Groups shown with dependencies.

### Group A — Enum + Model extension (backend + frontend) [no dependencies]

**A1** — Add `AutoAccepted` to `CandidateReviewStatus` (backend)
- `AIAssisted/backend/BirkNext.Api/Models/CandidateReviewStatus.cs`
- Test: serialization round-trip confirms `AutoAccepted` serializes to `"AutoAccepted"` and deserializes correctly

**A2** — Add `AutoAccepted` to `CandidateReviewStatus` (frontend)
- Locate file in `BirkNext.Web/GraphQL/` via grep; add `AutoAccepted`
- Test: enum value exists in frontend namespace

**A3** — Add `CandidateId` to `ReviewedCandidate` model and EF migration
- `AIAssisted/backend/BirkNext.Api/Models/ReviewedCandidate.cs` — add `Guid? CandidateId`
- `AIAssisted/backend/BirkNext.Api/GraphQL/SaveReviewedCandidatesInput.cs` — add `Guid? CandidateId` to item input
- Create EF migration: `dotnet ef migrations add AddCandidateIdToReviewedCandidates`
- `AIAssisted/frontend/BirkNext.Web/GraphQL/GetReviewedCandidates.graphql` — add `candidateId` field
- Test: `ReviewedCandidate` persists and returns `CandidateId`; existing rows without `CandidateId` still load (null)

**A4** — Update `ExtractionCandidate` default ReviewStatus → `AutoAccepted`
- `AIAssisted/frontend/BirkNext.Web/Models/ExtractionCandidate.cs`
- Test: new `ExtractionCandidate` instances have `ReviewStatus == AutoAccepted`

**A5** — Fix `ExtractionSessionSnapshot.ActiveViewMode` default → `Traceability`
- `AIAssisted/frontend/BirkNext.Web/Models/ExtractionSessionSnapshot.cs`
- Test: `AnalyzeSpec_DefaultsToTraceabilityCoverage` — snapshot default is `Traceability`

---

### Group B — Session durability [depends on A3]

**B1** — Extend localStorage session expiry: 2 hours → 7 days
- `AIAssisted/frontend/BirkNext.Web/Services/ExtractionSessionService.cs`
- Change `SessionExpiry = TimeSpan.FromHours(2)` → `TimeSpan.FromDays(7)`
- Test: `SessionArtifacts_SurviveRefresh` — snapshot saved and loaded within 7 days is not treated as expired

**B2** — Session restore: apply saved review statuses when re-analyzing after storage expiry
- `AIAssisted/frontend/BirkNext.Web/Components/ExtractionReviewList.razor` (`@code`)
- When `PipelineResult` arrives and `InitialSession` is null: query `IGetReviewedCandidatesQuery` for `(projectId, sessionId)`
- Match returned records by `CandidateId` to incoming pipeline candidates
- Apply `ReviewStatus` from server record to matched candidate (only for `ManuallyAccepted`, `Rejected`, `NeedsReview` — not for `AutoAccepted` or `New`)
- Test: `ReopenSession_RestoresTraceability` — mock `IGetReviewedCandidatesQuery`, confirm review statuses applied to re-analyzed candidates

---

### Group C — Auto-persist on analysis completion [depends on A1, A2, A4]

**C1** — Auto-persist to `reviewed_candidates` after analysis
- `AIAssisted/frontend/BirkNext.Web/Components/ExtractionReviewList.razor` (`@code`)
- In `OnParametersSetAsync`: when `PipelineResult` changes to a new non-null value (compare with `_previousPipelineResult`)
- Merge: candidates restored from `InitialSession` keep their ReviewStatus; new candidates use `AutoAccepted`
- Call `SaveReviewedCandidatesMutation` with merged list (fire-and-forget; don't block UI)
- Include `CandidateId` in each item
- Mark persisted candidates `SaveState = Saved`
- Test: `AnalyzeSpec_AutoPersistsNormalizedArtifacts` — mock mutation, verify called with all AutoAccepted candidates on first PipelineResult set
- Test: `AutoPersist_DoesNotDuplicateExistingArtifacts` — set `InitialSession` with Rejected candidates, verify those are not overwritten (still Rejected in persisted call)
- Test: `SaveAcceptedArtifacts_NotRequiredForTraceability` — no user action; Traceability shows data

**C2** — "Traceability recalculated" notification after review status change
- In `HandleReviewStatusChanged`: set `_traceabilityRecalcNotice = true`, call `StateHasChanged()`
- Render dismissible notice when flag is true (auto-clear after navigation or dismiss)
- Test: `ReviewStatusChange_RecalculatesTraceability` — change status, assert notice rendered

---

### Group D — Traceability filter [depends on A2]

**D1** — Filter `Rejected` artifacts from `TraceabilityModelBuilder`
- `AIAssisted/frontend/BirkNext.Web/Services/TraceabilityModelBuilder.cs`
- Add filter at top of `Build()`: exclude `Rejected` candidates
- Test: `RejectedArtifacts_AreExcludedFromTraceability` — builder with Rejected candidates; assert they don't appear in output requirements, tests, orphans, or gaps

**D2** — Add `NeedsReviewWarning` to `TracedRequirement` and builder
- `AIAssisted/frontend/BirkNext.Web/Models/TraceabilityModels.cs` — add `bool NeedsReviewWarning`
- Builder: set `NeedsReviewWarning = true` when source candidate has `NeedsReview` status
- Test: `NeedsReviewArtifacts_AreFlaggedOrHandledConsistently` — builder output has `NeedsReviewWarning = true` for NeedsReview candidates

---

### Group E — UI changes [depends on C1, D2]

**E1** — Rename "Document View" tab → "Extraction Review"
- `ExtractionReviewList.razor` line 109: label, title attribute, data-testid (if present)
- Test: `ExtractionReview_IsOptional` — tab label is "Extraction Review"; tab is NOT selected by default

**E2** — Add explanatory banner to DocumentView component
- `AIAssisted/frontend/BirkNext.Web/Components/DocumentView.razor`
- Add at top: `<div class="extraction-review-banner" data-testid="extraction-review-banner">Artifacts have already been extracted and are available in Traceability &amp; Coverage. Use this view only if you want to review or adjust extraction quality.</div>`
- Test: banner renders when DocumentView is mounted

**E3** — Rename "Save Accepted Artifacts" → "Publish to QA Library" throughout action bar
- `ExtractionReviewList.razor` lines 487–523
- All three states of the save button (disabled-all-already-saved, has-accepted-unsaved, no-accepted):
  - "Save Selected Artifacts" → "Publish to QA Library"
  - "Save @N Accepted Artifact@(s)" → "Publish @N Artifact@(s) to QA Library"
  - "Save Accepted Artifacts" (disabled) → "Publish to QA Library"
  - Disabled title tooltip: "Accept selected artifacts before saving" → "Review and accept artifacts in Extraction Review before publishing to QA Library."
  - Already-saved tooltip: "All selected items are already saved to the QA Artifact Library." (keep this one — accurate)
- Test: `PublishToLibrary_IsOptional` — assert no element with text "Save Accepted Artifacts" exists; assert "Publish to QA Library" label is present

**E4** — Update `analysis-workflow-hint` and empty-state text
- `ExtractionReviewList.razor` line 128: update hint text
  - New: `"Traceability &amp; Coverage is your primary workspace — review coverage and gaps immediately after analysis. Use Extraction Review only if you want to inspect or correct extraction quality."`
- Line 29: empty state flow text
  - Old: `"Review → Accept → Save → Track on Dashboard"`
  - New: `"Analyze → Traceability & Coverage → Investigate Gaps"`
- Test: assert updated hint text in rendered output

**E5** — Add NeedsReview warning badge in `TraceabilityView.razor`
- In requirement/test row rendering: when `NeedsReviewWarning == true`, render inline badge
- CSS class: `needs-review-badge`; `title="Extraction quality not confirmed — review in Extraction Review"`
- Test: TraceabilityView with a NeedsReview candidate shows badge on the relevant row

---

### Group F — RecommendedWorkflow update [independent]

**F1** — Rewrite RecommendedWorkflow.razor Phase 2
- `AIAssisted/frontend/BirkNext.Web/Pages/RecommendedWorkflow.razor`
- Phase 2 name: "Build QA Knowledge" → "Extraction Review (Optional)"
- Phase 2 goal: "Inspect and optionally correct extraction quality. Traceability works without this step."
- Phase 2 actions:
  1. "Traceability &amp; Coverage is available immediately after analysis — start there."
  2. "Open Extraction Review only if you want to inspect or correct extraction quality."
  3. "Reject artifacts to exclude them from coverage; mark Needs Review to flag uncertain ones."
  4. "Publish to QA Library when you have a curated set you want to preserve permanently."
- Phase 2 outcome: "Extraction quality corrections applied and optionally published to QA Library. Testers who skip this step have full Traceability access immediately."
- Update Phase 3 preamble: remove any wording that implies Phase 2 (accept/save) must happen first
- Test: `UserGuide_ExplainsTraceabilityFirstWorkflow` — Phase 2 contains "Optional"; no text "Accept or reject each candidate" as a mandatory step; Phase 3 does not reference "saved artifacts" as prerequisite
- Test: `LibraryContainsOnlyPublishedArtifacts` — user guide language distinguishes session artifacts from QA Library

---

### Group G — Regression coverage [depends on D, E, F]

**G1** — `Traceability_WorksWithoutManualAcceptance` — render ExtractionReviewList with PipelineResult, no user action, assert Traceability shows matrix and requirements

**G2** — `AnalyzeSpec_DefaultsToTraceabilityCoverage` — assert `_activeViewMode == Traceability` on initial load with a non-null PipelineResult

**G3** — `ExistingAcceptedArtifacts_StillWorkAfterMigration` — restore session with legacy `Accepted` status candidates; assert they appear in Traceability (not filtered out)

---

## Task Generation Notes (for /speckit-tasks)

Execution order: A → (B, D in parallel) → C → E → F → G

- A1–A5: can be parallelized; A must complete before C and D
- B1, B2: depend on A3; can run in parallel with D
- C1, C2: depend on A completion; C1 is the critical path
- D1, D2: depend on A2; D must complete before E5
- E1–E5: depend on C1 and D; E1–E4 are independent of each other; E5 depends on D2
- F1: fully independent — can run in parallel with any group
- G1–G3: final verification after E and F
