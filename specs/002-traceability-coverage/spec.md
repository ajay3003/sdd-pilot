# Feature Specification: Traceability & Coverage

**Feature Branch**: `002-traceability-coverage`  
**Created**: 2026-06-04  
**Status**: Draft  
**Input**: User description: "As a test lead, I want to see which requirements are covered by tests, which requirements are missing test coverage, and which tests are not linked to any requirement, so that I can understand QA risk before changes are accepted."

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 - View Coverage Status for All Requirements (Priority: P1)

A test lead opens the Traceability & Coverage page and immediately sees a matrix of all accepted requirements, each showing whether it is covered by at least one accepted test or is missing test coverage. A summary row at the top shows totals and a coverage percentage.

**Why this priority**: Without a readable coverage overview, the test lead cannot assess QA risk. This is the core value proposition of the feature.

**Independent Test**: Can be fully tested by navigating to `/traceability` with scenarios already in the system and confirming requirements appear with correct coverage labels and summary card values.

**Acceptance Scenarios**:

1. **Given** a project has accepted requirements and accepted tests, **When** a test lead opens the Traceability & Coverage page, **Then** each requirement is listed in the matrix with a label of either "Covered" or "Missing Test Coverage".
2. **Given** a requirement has at least one accepted test linked to it with a "Covers" link, **When** the test lead views the matrix, **Then** that requirement shows the label "Covered".
3. **Given** a requirement has no accepted tests linked to it, **When** the test lead views the matrix, **Then** that requirement shows the label "Missing Test Coverage".
4. **Given** a project has accepted requirements, **When** the test lead views the summary cards, **Then** they see: Total Requirements, Covered Requirements, Not Covered Requirements, Coverage %, and Orphan Tests.
5. **Given** a rejected requirement exists in the system, **When** the test lead views the matrix, **Then** the rejected requirement does not appear in the coverage calculations or the matrix.

---

### User Story 2 - Link a Test to a Requirement (Priority: P2)

A test lead identifies a requirement marked as "Missing Test Coverage" and manually links an existing accepted test to it. After linking, the requirement's status updates to "Covered" and the summary cards reflect the change.

**Why this priority**: Without the ability to create links, the matrix is read-only and the test lead cannot act on what they see. This story completes the core workflow loop.

**Independent Test**: Can be fully tested by selecting an unlinked requirement, choosing a test from the available list, confirming the link, and verifying the requirement now shows "Covered".

**Acceptance Scenarios**:

1. **Given** a requirement is shown as "Missing Test Coverage", **When** the test lead uses the link action on that requirement and selects an accepted test, **Then** a trace link is created and the requirement now shows "Covered".
2. **Given** a test lead tries to link a test to a requirement, **When** they open the link dialog, **Then** only accepted tests that are not already linked to that requirement are shown as available options.
3. **Given** a trace link is successfully created, **When** the test lead views the matrix, **Then** the summary cards (Covered count, Coverage %) update to reflect the new link without requiring a full page reload.
4. **Given** a test lead links the same test to a requirement that is already covered, **When** the link is saved, **Then** the requirement remains "Covered" and the test appears in the linked tests list alongside any existing links.

---

### User Story 3 - Identify and Review Orphan Tests (Priority: P3)

A test lead wants to see which accepted tests are not linked to any accepted requirement, so they can decide whether those tests are redundant, misclassified, or waiting to be connected.

**Why this priority**: Orphan tests represent risk and potential waste. Visibility into them is important for QA completeness, though less urgent than seeing requirement coverage.

**Independent Test**: Can be fully tested by verifying that tests with no "Covers" links to accepted requirements are listed in the Orphan Tests section or highlighted by the filter.

**Acceptance Scenarios**:

1. **Given** an accepted test has no "Covers" link to any accepted requirement, **When** the test lead views the Traceability & Coverage page, **Then** the Orphan Tests summary card shows the count of such tests.
2. **Given** the test lead applies the "Orphan Test" filter, **When** the filter is active, **Then** only orphan tests are shown in the matrix/list view.
3. **Given** a rejected test exists in the system, **When** the test lead views the orphan tests, **Then** the rejected test is excluded from the orphan count and list.

---

### User Story 4 - Remove a Trace Link (Priority: P4)

A test lead removes an incorrect trace link between a test and a requirement. After removal, the requirement's coverage status is recalculated and updates if no other links remain.

**Why this priority**: Data accuracy depends on the ability to correct mistakes. However, link removal is less frequent than viewing or adding links.

**Independent Test**: Can be fully tested by removing a link from a requirement that had exactly one linked test and confirming the requirement reverts to "Missing Test Coverage".

**Acceptance Scenarios**:

1. **Given** a requirement has one linked test, **When** the test lead removes the link, **Then** the requirement reverts to "Missing Test Coverage" and the summary cards update.
2. **Given** a requirement has two linked tests, **When** the test lead removes one link, **Then** the requirement remains "Covered" because the other link still exists.
3. **Given** a test lead removes a link that made a test an orphan, **When** the test no longer covers any requirement, **Then** the Orphan Tests count increases by one.

---

### Edge Cases

- What happens when there are no scenarios of any kind in the project?
- What happens when all requirements are covered — does the page clearly communicate 100% coverage?
- What happens when the test lead tries to link a test that has since been rejected?
- How does the matrix behave when there are many requirements (50+) with long titles?
- What happens if the backend is unavailable when the test lead tries to create or remove a link?

---

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST display a Traceability & Coverage page accessible at a dedicated route within the application.
- **FR-002**: System MUST list all accepted requirements in a matrix view, each showing its title and coverage status.
- **FR-003**: System MUST label each requirement as "Covered" when at least one accepted test is linked to it via a "Covers" link type.
- **FR-004**: System MUST label each requirement as "Missing Test Coverage" when no accepted test covers it.
- **FR-005**: System MUST display summary cards showing: Total Requirements, Covered Requirements, Not Covered Requirements, Coverage Percentage, and Orphan Tests count.
- **FR-006**: System MUST allow a test lead to manually create a trace link between an accepted test and an accepted requirement.
- **FR-007**: System MUST allow a test lead to remove an existing trace link.
- **FR-008**: System MUST recalculate and display updated coverage status immediately after a link is created or removed.
- **FR-009**: System MUST identify and count accepted tests that have no "Covers" link to any accepted requirement as "Orphan Tests".
- **FR-010**: System MUST exclude rejected scenarios from all coverage calculations, counts, and displays.
- **FR-011**: System MUST provide filters to view: Covered requirements, Not Covered requirements, and Orphan Tests.
- **FR-012**: System MUST show the list of tests already linked to each requirement within the matrix row.
- **FR-013**: System MUST include a navigation entry for the Traceability & Coverage page in the application sidebar.
- **FR-014**: System MUST NOT affect or break any existing scenario, extraction, or comparison functionality.

### Key Entities

- **TraceLink**: Represents a directed link between two scenarios.  
  Attributes: unique identifier, source scenario identifier, target scenario identifier, link type (Covers or RelatedTo), creation timestamp, optional creator, optional notes.

- **Coverage Status**: A derived label for a requirement based on its trace links.  
  Values: Covered, Missing Test Coverage.

- **Orphan Test**: A derived classification for an accepted test that has no Covers link to any accepted requirement.

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A test lead can open the Traceability & Coverage page and see all requirement coverage statuses within 3 seconds of navigation.
- **SC-002**: After creating a trace link, coverage status and summary cards update within 2 seconds without requiring a full page reload.
- **SC-003**: 100% of rejected scenarios are excluded from coverage calculations — zero rejected items appear in coverage totals or the matrix.
- **SC-004**: A test lead can complete the full workflow (navigate → identify uncovered requirement → link a test → confirm coverage updated) in under 2 minutes.
- **SC-005**: Existing features (scenario creation, extraction, comparison) continue to work correctly after the feature is deployed — no regressions.

---

## Assumptions

- Users are already authenticated; this feature adds no new authentication mechanism.
- Only the "Covers" link type contributes to coverage calculations; "RelatedTo" links are stored but do not affect coverage status.
- A trace link is directional: a test covers a requirement (TEST → REQUIREMENT via Covers). The UI may present linking from either side, but the underlying link direction is consistent.
- NeedsClarification scenarios are neither requirements nor tests for coverage purposes and are excluded from the matrix and orphan calculations.
- The matrix initially shows all accepted requirements with no filtering applied.
- Pagination or virtualisation of the matrix is not required for the initial version; all accepted requirements are displayed in a single scrollable list.
- The "Covers" link type is sufficient for MVP; "RelatedTo" is stored but has no coverage effect in this iteration.
- Project identifier scoping follows the existing pattern in BirkNext; all coverage data is scoped to a project.
- No bulk-link or import functionality is required for this iteration.
- AI-assisted suggestions for linking are explicitly deferred to a future version.
