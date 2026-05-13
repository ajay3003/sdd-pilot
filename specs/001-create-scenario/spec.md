# Feature Specification: Scenario Management

**Feature Branch**: `001-create-scenario`  
**Created**: 2026-04-23  
**Status**: Draft  
**Input**: User description: "Create Scenario feature for a web application where users can create a scenario with title, description and type (Requirement, Test, NeedsClarification). The scenario should be validated, stored via backend and displayed in a list. The goal is to support specification work and quality assurance by capturing structured scenarios early."

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Create a New Scenario (Priority: P1)

A user opens the scenario creation form, fills in a title, optionally adds a description, and selects a type (Requirement, Test, or NeedsClarification). After submitting, the scenario is validated, saved to the backend, and the new scenario appears in the list.

**Why this priority**: This is the core capability of the feature. Without the ability to create scenarios, the entire feature delivers no value.

**Independent Test**: Can be fully tested by submitting the creation form with valid data and confirming the new scenario appears in the list.

**Acceptance Scenarios**:

1. **Given** a user is on the scenario creation form, **When** they enter a valid title, an optional description, and select a type, and submit, **Then** the scenario is saved and immediately appears in the scenario list.
2. **Given** a user submits the form with a title but no description, **When** the form is submitted, **Then** the scenario is saved successfully with an empty description.
3. **Given** a user submits the form without a title, **When** the form is submitted, **Then** a validation error is shown near the title field and the scenario is not saved.
4. **Given** a user submits the form without selecting a type, **When** the form is submitted, **Then** a validation error is shown near the type field and the scenario is not saved.
5. **Given** a user submits a valid scenario and the backend is unavailable, **When** the submission is processed, **Then** the scenario is not saved, **And** the user sees a clear error message, **And** the user can try again later.

---

### User Story 2 - View Scenario List (Priority: P2)

A user navigates to the scenario list view and sees all previously created scenarios, each displaying its title, type, and description.

**Why this priority**: Viewing captured scenarios is the second core function — without visibility into stored scenarios, the captured data has no practical use.

**Independent Test**: Can be fully tested by navigating to the list view after creating one or more scenarios and confirming each entry displays correct data.

**Acceptance Scenarios**:

1. **Given** one or more scenarios have been created, **When** a user navigates to the scenario list, **Then** all scenarios are displayed showing title, type, and description.
2. **Given** no scenarios have been created, **When** a user navigates to the scenario list, **Then** an empty-state message is displayed indicating no scenarios exist yet.
3. **Given** a scenario was just created, **When** the user views the list, **Then** the newly created scenario is visible without requiring a manual page refresh.

---

### User Story 3 - Receive Inline Validation Feedback (Priority: P3)

A user submits an incomplete or invalid scenario form and receives clear, inline error messages indicating which fields need to be corrected.

**Why this priority**: Inline validation feedback is critical for data quality and usability but can be delivered as a refinement after the core create/list flows are working.

**Independent Test**: Can be fully tested by intentionally submitting the form with missing required fields and confirming appropriate error messages appear next to the relevant fields.

**Acceptance Scenarios**:

1. **Given** a user leaves the title field empty and submits, **When** the form is submitted, **Then** an error message appears near the title field indicating it is required.
2. **Given** a user does not select a type and submits, **When** the form is submitted, **Then** an error message appears near the type field indicating a selection is required.
3. **Given** a user corrects all validation errors and resubmits, **When** the corrected form is submitted, **Then** the scenario is saved successfully and appears in the list.

---

### Edge Cases

- What happens when the backend is unavailable during scenario submission?
- How does the system handle a user submitting the form multiple times in rapid succession (double-submit)?
- How does the list behave when there are many scenarios (display performance)?
- What happens if the title exceeds the maximum allowed length?

---

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST allow users to create a scenario providing a title, an optional description, and a type.
- **FR-002**: System MUST validate that the scenario title is non-empty before saving.
- **FR-003**: System MUST validate that the scenario type is one of: Requirement, Test, or NeedsClarification.
- **FR-004**: System MUST store validated scenarios in a persistent backend data store.
- **FR-005**: System MUST display all stored scenarios in a list view, showing each scenario's title, type, and description.
- **FR-006**: System MUST display inline validation error messages for each invalid field upon form submission.
- **FR-007**: System MUST prevent scenario submission while validation errors exist.
- **FR-008**: System MUST show an empty-state message in the list when no scenarios have been created.
- **FR-009**: System MUST confirm successful scenario creation to the user (e.g., via success notification or automatic list update).
- **FR-010**: Scenario list MUST be scoped to a project or workspace; all users within the same project share visibility of all scenarios belonging to that project.

---

## Key Entities

- **Scenario**: Represents a captured specification or QA scenario.  
  Attributes:
  - title (required, free text)  
  - description (optional, free text)  
  - type (required: Requirement / Test / NeedsClarification)  
  - created date/time  
  - project/workspace identifier  

---

## Observability

- Successful scenario creation should be logged
- Validation failures should be logged
- Technical failures during scenario submission should be logged with correlation or request context
- Response time for scenario creation should be measurable

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can complete scenario creation (form fill and submit) in under 2 minutes.
- **SC-002**: Newly created scenarios appear in the list within 3 seconds of successful submission.
- **SC-003**: Inline validation prevents submission of incomplete scenarios in 100% of cases.
- **SC-004**: The scenario list correctly displays all stored scenarios without data loss or rendering errors.
- **SC-005**: 95% of valid scenario submissions succeed without a system error.

---

## Assumptions

- Users are already authenticated; this feature does not include its own authentication mechanism.
- Scenario description is optional; only title and type are required fields.
- Editing and deleting scenarios are out of scope for this initial version.
- The scenario list displays entries in reverse-chronological order (most recent first) by default.
- No pagination is required for the initial version; all scenarios are displayed in a single list.
- The three scenario types (Requirement, Test, NeedsClarification) are fixed and not user-configurable.
- This feature will be implemented using the application's backend API and persistent data storage.

---

## Feature US2 — Deterministic Scenario Extraction

**Status**: Draft  
**Created**: 2026-05-13

---

### Summary

US2 enables users to paste specification text directly into BirkNext and have the system extract candidate scenarios using deterministic, rule-based logic — no AI or machine learning is involved in this version. Extracted candidates are classified into three categories (REQUIREMENT, TEST, NEEDS_CLARIFICATION) and presented to the user for review. No data is persisted automatically; the user decides what to keep before initiating any save action.

---

### User Story

A user pastes the text of a specification document (such as a spec.md file) into the extraction interface. The system applies deterministic rules to the pasted text, identifies bullet points and relevant lines, and presents a list of extracted candidates. Each candidate is labelled with a classification. The user reviews the full list, makes decisions about which candidates to retain, and explicitly confirms a save action. Nothing is persisted until the user acts.

---

### User Workflow

1. User navigates to the extraction view within BirkNext.
2. User pastes spec text into the provided text area.
3. User triggers the extraction action.
4. System processes the pasted text using deterministic rules and displays a list of extracted candidate scenarios.
5. Each candidate is shown with its assigned classification: REQUIREMENT, TEST, or NEEDS_CLARIFICATION.
6. User reviews the extracted candidates.
7. User selects which candidates to retain.
8. User explicitly confirms the save action.
9. Only the user-selected candidates are persisted; all others are discarded.

---

### Functional Requirements

- **FR-US2-001**: System MUST accept pasted text as the sole input method; file upload is not supported in this version.
- **FR-US2-002**: System MUST extract candidate scenarios from pasted text using deterministic rules only; no AI or machine learning may be involved.
- **FR-US2-003**: System MUST extract bullet points and relevant lines from the pasted text as candidate scenarios.
- **FR-US2-004**: System MUST classify each extracted candidate as exactly one of: REQUIREMENT, TEST, or NEEDS_CLARIFICATION.
- **FR-US2-005**: System MUST display all extracted candidates to the user before any persistence occurs.
- **FR-US2-006**: System MUST NOT automatically persist any extracted candidate; persistence requires an explicit user action.
- **FR-US2-007**: System MUST allow the user to select which candidates to save before committing.
- **FR-US2-008**: System MUST show an appropriate message when no candidates can be extracted from the pasted text.
- **FR-US2-009**: System MUST show a validation message and not attempt extraction when the pasted text area is empty.

---

### Acceptance Criteria

1. **Given** a user pastes spec text containing bullet points, **When** extraction is triggered, **Then** all bullet points are extracted and displayed as candidate scenarios.
2. **Given** extracted candidates are displayed, **When** the user views the list, **Then** each candidate shows its classification label (REQUIREMENT, TEST, or NEEDS_CLARIFICATION).
3. **Given** extracted candidates are displayed, **When** the user has not performed a save action, **Then** no candidates are persisted to the data store.
4. **Given** a user pastes text that contains no extractable candidates, **When** extraction is triggered, **Then** the system displays a message indicating no candidates were found.
5. **Given** a user selects a subset of extracted candidates and confirms save, **When** the save action is processed, **Then** only the selected candidates are persisted; unselected candidates are discarded.
6. **Given** a user triggers extraction on an empty text area, **When** the input is evaluated, **Then** the system shows a validation message and does not attempt extraction.

---

### Non-Goals

- AI or machine learning classification is explicitly excluded from this version.
- File upload is not supported; text must be pasted directly.
- Automatic persistence of extracted candidates is excluded.
- Editing or reclassifying individual extracted candidates before save is deferred to a future version.
- Batch processing of multiple documents in a single extraction session is out of scope.
- Export of extracted candidates to external formats is out of scope for this version.

---

### Observability Requirements

- The number of candidates extracted per extraction event MUST be measurable.
- Each extraction trigger event MUST be logged with sufficient context to identify the session.
- User save actions (candidates selected and confirmed) MUST be logged.
- Extraction processing time MUST be measurable per event.

---

### Security Requirements

- Pasted text MUST be treated as untrusted user input and sanitized before being rendered in the interface.
- No extracted candidate data may be persisted without an explicit, affirmative user action.
- This feature introduces no new authentication or authorization surface beyond what US1 establishes.

---

### Performance Expectations

- Extraction MUST complete and results MUST be displayed within 2 seconds for pasted text up to 10,000 characters.
- The extraction interface MUST remain responsive during processing; blocking the user from interacting with the page is not acceptable.

---

### Future Evolution

- AI-assisted or ML-based classification may be introduced in a subsequent version to improve accuracy beyond deterministic rules.
- File upload support (e.g., uploading a spec.md file directly) is a candidate for the next iteration.
- In-line editing and reclassification of extracted candidates prior to save is a planned enhancement.
- Bulk selection and deselection of extracted candidates is a candidate for improved review UX.
- Integration with the scenario list from US1, so that saved candidates become first-class Scenarios, is the intended long-term connection between these two features.