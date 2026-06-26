# Feature Specification: Traceability-First Workflow — Accept/Reject as Advanced Feature

**Feature Branch**: `008-traceability-first`
**Created**: 2026-06-16
**Status**: Draft

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Immediate Traceability After Analysis (Priority: P1)

A QA tester runs "Analyze Specification" on a new spec document. Without performing any Accept, Reject, or Save actions, they immediately see Traceability & Coverage as the active view, showing the requirements matrix, coverage percentages, gaps, and orphan tests.

**Why this priority**: This is the core workflow change. Every tester is blocked by the old flow; fixing this unblocks everyone immediately and removes the most significant friction point.

**Independent Test**: Can be fully tested by loading a spec, running Analyze Specification, and verifying that Traceability & Coverage opens by default with populated data — without touching any Accept/Reject controls.

**Acceptance Scenarios**:

1. **Given** a spec has been analyzed and normalized artifacts exist, **When** analysis completes, **Then** Traceability & Coverage opens as the default active view.
2. **Given** Traceability & Coverage is open, **When** no manual Accept/Reject has been performed, **Then** the matrix, requirements, artifacts, gaps, and graph tabs all display data.
3. **Given** no artifacts have been manually accepted or saved, **When** the tester views coverage metrics, **Then** coverage percentage, gap count, and orphan test count are shown correctly.

---

### User Story 2 — Auto-Persist Normalized Artifacts After Analysis (Priority: P1)

When analysis completes, all normalized artifacts (Requirements, Acceptance Tests, Success Criteria, Clarifications, Decisions, Assumptions, Architecture Notes, Metadata) are automatically persisted into the active analysis session without any tester action.

**Why this priority**: Without auto-persist, Traceability would have nothing to show. This is a prerequisite for US1.

**Independent Test**: Can be fully tested by analyzing a spec and then querying the session — all artifact types must be present without the tester clicking Save or Accept.

**Acceptance Scenarios**:

1. **Given** analysis completes, **When** the session is queried for artifacts, **Then** all artifact types are present with status AutoAccepted or Unreviewed.
2. **Given** a session already contains artifacts from a prior analysis, **When** analysis runs again on the same session, **Then** existing artifacts are updated in place — no duplicates are created.
3. **Given** auto-persisted artifacts exist, **When** Flow View, Spec Explorer, Architecture View, or QA Artifact Library are opened, **Then** they all show the auto-persisted data without a Save step.

---

### User Story 3 — Extraction Review as Optional Advanced Mode (Priority: P2)

A tester who wants to inspect or correct extraction quality opens the "Extraction Review" view (formerly Document View). This view is clearly labeled as optional/advanced. The tester can Accept, Reject, or mark artifacts as Needs Review. Changes immediately recalculate Traceability.

**Why this priority**: Keeps the power-user workflow intact while clearly demoting it from required to optional.

**Independent Test**: Can be fully tested by opening Extraction Review after Traceability is already showing data, changing a review status, and verifying that Traceability recalculates without disrupting existing coverage.

**Acceptance Scenarios**:

1. **Given** Traceability is showing data, **When** the tester opens Extraction Review, **Then** an explanatory banner reads "Artifacts have already been extracted and are available in Traceability & Coverage. Use this view only if you want to review or adjust extraction quality."
2. **Given** the tester Rejects an artifact in Extraction Review, **When** Traceability recalculates, **Then** the rejected artifact no longer appears in the matrix, coverage, or gap calculations.
3. **Given** the tester marks an artifact as Needs Review, **When** Traceability recalculates, **Then** the artifact remains visible in Traceability with a "Needs Review" warning badge.
4. **Given** the tester had previously rejected an artifact, **When** they re-accept it in Extraction Review, **Then** Traceability recalculates and the artifact is re-included.
5. **Given** review status changes have been saved, **When** the tester views Traceability, **Then** a confirmation message "Traceability recalculated after review changes." is shown.

---

### User Story 4 — Updated Review Status Model (Priority: P2)

The review status model is extended to include AutoAccepted and Unreviewed states. Existing Accepted, Rejected, and NeedsReview statuses continue to work. All views display status badges consistently.

**Why this priority**: Enables the auto-persist flow to express whether extraction quality has been manually checked, without breaking existing data.

**Independent Test**: Can be fully tested by inspecting auto-persisted artifacts (should show AutoAccepted or Unreviewed) and existing reviewed artifacts (should retain their prior status).

**Acceptance Scenarios**:

1. **Given** artifacts were auto-persisted after analysis, **When** review status is inspected, **Then** each artifact shows AutoAccepted (included in Traceability) or Unreviewed (extraction quality unchecked).
2. **Given** an existing session with Accepted artifacts, **When** the session is loaded, **Then** those artifacts retain Accepted (ManuallyAccepted) status without data loss.
3. **Given** an existing session with Rejected artifacts, **When** the session is loaded, **Then** those artifacts remain excluded from Traceability.

---

### User Story 5 — Updated UI Buttons and Workflow Banner (Priority: P2)

After analysis, the primary UI action is "Open Traceability & Coverage". "Review Extraction Quality" is a secondary action. "Save Reviewed Set" / "Export Curated Artifacts" is an advanced/optional action. The workflow banner reflects the new Traceability-first sequence.

**Why this priority**: Prevents the old UI language from implying the wrong workflow. Tester confusion is eliminated by making primary/secondary hierarchy clear.

**Independent Test**: Can be fully tested by observing the post-analysis UI and confirming that no primary button says "Save Accepted Artifacts" or implies that acceptance is required before Traceability.

**Acceptance Scenarios**:

1. **Given** analysis has completed, **When** the tester looks at post-analysis actions, **Then** "Open Traceability & Coverage" is the primary action (most prominent button/link).
2. **Given** the post-analysis UI, **When** "Save Accepted Artifacts" or "Save Selected Artifacts" labels are present, **Then** they are demoted to secondary/advanced with no primary visual emphasis.
3. **Given** the workflow banner or guidance panel is visible, **When** the tester reads the recommended sequence, **Then** it reads: Analyze → Traceability & Coverage → Investigate Gaps → (Optional) Review Extraction Quality.

---

### User Story 6 — Updated Recommended Workflow and User Guide (Priority: P3)

The Recommended Workflow page and User Guide are updated to describe the Traceability-first flow. All references implying that Accept/Save is required before Traceability are removed or corrected.

**Why this priority**: Documentation accuracy prevents training and onboarding errors. Lower priority because runtime behavior changes are more critical.

**Independent Test**: Can be fully tested by reading the Recommended Workflow page and User Guide and confirming no step says "Accept artifacts before using Traceability."

**Acceptance Scenarios**:

1. **Given** the Recommended Workflow page is open, **When** Phase 2 guidance is read, **Then** it no longer says "Accept/reject candidates, save to QA Artifact Library" as a prerequisite for Traceability.
2. **Given** the User Guide, **When** coverage workflow is described, **Then** it explains that Traceability is available immediately after analysis.
3. **Given** the User Guide, **When** Extraction Review is described, **Then** it is labeled as optional and explains that rejected artifacts are excluded and needs-review artifacts are flagged with badges.

---

### Edge Cases

- What happens when analysis produces zero artifacts? Traceability should show an empty state with a clear message, not an error.
- What happens if a session already has manually reviewed artifacts and analysis runs again? Auto-persist must not overwrite ManuallyAccepted or Rejected statuses; only add new artifacts as AutoAccepted/Unreviewed.
- What happens if the tester rejects all artifacts? Traceability shows 0% coverage with a gap-only view; no crash.
- What happens if network/persistence fails during auto-persist? Traceability must still load from in-memory session state; show a non-blocking warning about persistence failure.
- What happens if an existing session used the old Pending status? It must map to Unreviewed on load.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: After Analyze Specification completes, the system MUST automatically persist all normalized artifacts into the active analysis session without any tester action.
- **FR-002**: Auto-persisted artifacts MUST be assigned review status AutoAccepted (included in Traceability by default).
- **FR-003**: The system MUST NOT overwrite existing ManuallyAccepted, Rejected, or NeedsReview statuses when re-running analysis on the same session.
- **FR-004**: The system MUST NOT create duplicate artifacts when auto-persisting; stable artifact identity is determined by SourceDocumentId + CandidateId (if available) + ArtifactType.
- **FR-005**: After analysis completes, the system MUST open Traceability & Coverage as the default active view.
- **FR-006**: Traceability & Coverage MUST display the requirements matrix, requirements list, artifacts, gaps, and graph using auto-persisted artifacts — without requiring manual Accept/Reject or Save.
- **FR-007**: Artifacts with status Rejected MUST be excluded from the Traceability matrix, coverage calculations, gap analysis, and orphan test detection.
- **FR-008**: Artifacts with status NeedsReview MUST remain visible in Traceability with a "Needs Review" warning badge; they MUST NOT be excluded from coverage by default.
- **FR-009**: When the tester changes an artifact's review status in Extraction Review, the system MUST recalculate Traceability and display "Traceability recalculated after review changes."
- **FR-010**: The Document View tab MUST be renamed to "Extraction Review" and MUST display an explanatory banner: "Artifacts have already been extracted and are available in Traceability & Coverage. Use this view only if you want to review or adjust extraction quality."
- **FR-011**: Accept, Reject, and Needs Review actions MUST only be accessible inside the Extraction Review view; they MUST NOT be required primary actions in the main post-analysis UI.
- **FR-012**: The post-analysis primary action MUST be "Open Traceability & Coverage"; "Review Extraction Quality" MUST be secondary; "Save Reviewed Set" / "Export Curated Artifacts" MUST be advanced/optional.
- **FR-013**: "Save Accepted Artifacts" and "Save Selected Artifacts" labels MUST be removed or demoted; they MUST NOT carry primary button emphasis.
- **FR-014**: The workflow banner MUST reflect the new sequence: Analyze → Traceability & Coverage → Investigate Gaps → (Optional) Review Extraction Quality → Accept/Reject/NeedsReview → Recalculate Traceability.
- **FR-015**: The Recommended Workflow page MUST be updated: Phase 2 must no longer describe Accept/Save as a prerequisite for Traceability.
- **FR-016**: The review status model MUST support: AutoAccepted, Unreviewed, ManuallyAccepted, Rejected, NeedsReview. Existing Accepted values MUST be treated as ManuallyAccepted; existing Pending values MUST be treated as Unreviewed.
- **FR-017**: QA Artifact Library MUST display auto-persisted artifacts immediately after analysis; it MUST NOT require a Save Accepted Artifacts step for basic artifact visibility.
- **FR-018**: Existing sessions with previously reviewed artifacts MUST load correctly; no data loss or status regression may occur.

### Key Entities

- **ExtractionCandidate**: An artifact extracted from a specification. Has CandidateId, Title, Classification (ScenarioKind), ReviewStatus (AutoAccepted | Unreviewed | ManuallyAccepted | Rejected | NeedsReview), SourceDocumentId, ArtifactType.
- **CandidateReviewStatus**: Enum — AutoAccepted, Unreviewed, ManuallyAccepted, Rejected, NeedsReview (extends existing New/Accepted/Rejected/NeedsReview with backward compatibility).
- **ReviewedCandidate**: Persisted review record in the database. Maps to ExtractionCandidate by (SourceDocumentId, CandidateId, ArtifactType).
- **TraceabilityModel**: The computed coverage model built from session artifacts. Must include/exclude artifacts based on ReviewStatus.
- **AnalysisSession**: The active session containing artifacts for a given project/document. Auto-persist targets this session on analysis completion.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Testers reach a populated Traceability & Coverage view within one step after Analyze Specification completes, with zero manual Accept/Save actions required.
- **SC-002**: 100% of auto-persisted artifacts appear in Traceability & Coverage immediately after analysis, with no additional steps needed.
- **SC-003**: When a tester Rejects an artifact in Extraction Review, Traceability recalculates and excludes the artifact within one user interaction.
- **SC-004**: Zero artifacts are duplicated when analysis is re-run on an existing session.
- **SC-005**: All existing sessions with previously reviewed artifacts load without data loss or status changes — 100% backward compatibility.
- **SC-006**: The Recommended Workflow page contains zero references implying Accept/Save is required before accessing Traceability.
- **SC-007**: Needs Review artifacts are consistently visible in Traceability with a warning badge across all sub-views (Matrix, Requirements, Gaps, Graph).
- **SC-008**: The post-analysis UI primary call-to-action reads "Open Traceability & Coverage" with no competing primary-emphasis button for Save Accepted Artifacts.

## Assumptions

- The existing `CandidateReviewStatus` enum will be extended in-place; no database migration of existing enum column values is needed if the new values (AutoAccepted, Unreviewed) are added as additional options.
- Existing Accepted values in persisted data are treated as ManuallyAccepted semantically; no rename migration is required at the database level unless the enum value name is stored as a string.
- The TraceabilityModelBuilder already reads from session artifacts; it will be updated to filter by ReviewStatus rather than requiring a separate "saved accepted" list.
- Flow View, Spec Explorer, Architecture View, and QA Artifact Library already read from session artifact state; only the Traceability-filter logic needs updating.
- The existing `ExtractionReviewList.razor` is the primary host component for view mode tabs; the Document View tab rename occurs within this component.
- Backward-compatible session restoration from browser storage is already handled by ExtractionSessionService; this feature does not change the session snapshot schema beyond adding the new status values.
- Mobile support is out of scope; all UI changes target the desktop browser experience only.
