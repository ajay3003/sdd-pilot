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

---

## Feature US3 — Deterministic Rule Engine for Scenario Extraction

**Status**: Draft  
**Created**: 2026-05-21

---

### Summary

US3 replaces the hardcoded extraction heuristics currently embedded directly in the extraction pipeline implementation with a structured, configurable rule engine. The external contract remains unchanged: input is pasted text, output is a reviewed candidate list, the review-before-save workflow is unmodified, and the GraphQL API surface is untouched. All extraction logic remains deterministic — no AI or machine learning is introduced. The goal is to improve maintainability, extensibility, and controlled evolution of extraction behaviour as the set of supported specification conventions grows.

---

### User Story

A BirkNext maintainer needs to adjust extraction behaviour — for example, to add a new keyword pattern, change classification priority, or suppress extraction of a particular structural element — without modifying multiple scattered code paths. US3 gives them a single, structured location to define and adjust rules, with predictable outcomes that are fully verifiable by deterministic tests.

---

### Goals

- Improve maintainability by centralising all extraction rules in one governed location rather than distributing them across pipeline stage implementations.
- Improve extensibility so that new rule types (new keyword signals, new block-type filters, new classification patterns) can be added without modifying unrelated pipeline logic.
- Allow controlled evolution of extraction behaviour via rule configuration that can be audited, reviewed, and versioned alongside the codebase.
- Preserve deterministic guarantees: given identical input, the rule engine must always produce identical output.
- Prepare the architecture for future optional AI-assisted extraction without coupling the deterministic path to any AI infrastructure.
- Preserve all existing US1 and US2 functional behaviour and acceptance criteria.

---

### Non-Goals

- AI, machine learning, natural language processing, or any probabilistic inference is explicitly excluded.
- External service calls, network requests, or remote rule repositories are out of scope.
- Runtime rule editing by end users through a UI is out of scope for this version.
- Hot-reloading or live rule modification without a deployment is out of scope.
- Automatic persistence of extraction results; the review-before-save workflow is unchanged.
- Changes to the GraphQL API surface; extraction remains entirely client-side.
- Reclassification or editing of candidates in the review UI; that is deferred to a future version.

---

### User Workflow Impact

US3 introduces no visible change to the end-user workflow. The user still:

1. Pastes specification text into the extraction interface.
2. Triggers extraction.
3. Reviews extracted candidates with their classifications.
4. Selects candidates to retain.
5. Confirms save.

The rule engine is an internal implementation concern. US2 acceptance criteria remain satisfied without modification.

---

### Functional Requirements

- **FR-US3-001**: The system MUST evaluate extraction candidates using a rule set that is defined as structured, enumerable configuration rather than inline code logic.
- **FR-US3-002**: Each rule in the rule set MUST have a well-defined type, a matching condition, and an outcome (classification or filter action).
- **FR-US3-003**: The rule engine MUST evaluate rules in a deterministic, explicitly ordered sequence; the evaluation order MUST be fixed and inspectable.
- **FR-US3-004**: When multiple rules match a single candidate, the rule engine MUST resolve the conflict using a defined priority ordering; the ordering MUST be consistent across all evaluation passes.
- **FR-US3-005**: The rule engine MUST preserve the existing classification signal hierarchy: BDD pattern signals take precedence over RFC-2119 uppercase signals, which take precedence over RFC-2119 lowercase signals, which take precedence over FR-prefix signals, which take precedence over question-terminator signals, which take precedence over deferral-marker signals, which take precedence over the default signal.
- **FR-US3-006**: The rule engine MUST support filter rules that suppress candidates matching specified block types, structural patterns, or content conditions.
- **FR-US3-007**: The rule engine MUST support classification rules that assign a REQUIREMENT, TEST, or NEEDS_CLARIFICATION outcome based on textual patterns and signals.
- **FR-US3-008**: The rule engine MUST support prefix-based classification rules that match on leading keywords or phrases.
- **FR-US3-009**: The rule engine MUST support pattern-based classification rules that match on regular expressions or keyword presence within candidate text.
- **FR-US3-010**: The rule engine MUST support section-aware context rules that can attach section heading information to candidates without altering classification outcomes.
- **FR-US3-011**: The system MUST continue to satisfy all US2 functional requirements and acceptance criteria after US3 is implemented.
- **FR-US3-012**: The system MUST continue to satisfy all US1 functional requirements and acceptance criteria after US3 is implemented; the GraphQL mutation contract is unchanged.
- **FR-US3-013**: The system MUST NOT persist raw pasted text in any rule configuration, log output, or observability payload.

---

### Acceptance Criteria

1. **Given** extraction is triggered on pasted text, **When** the rule engine evaluates the input, **Then** the output is identical to what the US2 pipeline produced for the same input — all existing US2 acceptance criteria continue to pass.
2. **Given** the rule set contains a classification rule for RFC-2119 uppercase keywords, **When** a candidate line contains MUST, SHALL, or SHOULD, **Then** the candidate is classified as REQUIREMENT with signal Rfc2119Uppercase.
3. **Given** the rule set contains a classification rule for BDD patterns, **When** a candidate line begins with Given, When, Then, And, But, or Scenario, **Then** the candidate is classified as TEST with signal BddPattern, regardless of any lower-priority RFC-2119 keywords present.
4. **Given** the rule set contains a filter rule for non-extractable block types, **When** a block of that type is encountered, **Then** it is excluded from the candidate list without being classified.
5. **Given** two rules both match the same candidate, **When** the rule engine resolves the conflict, **Then** the higher-priority rule's classification is used and the assigned ClassificationSignal reflects that rule.
6. **Given** the rule set is modified to add a new keyword pattern, **When** extraction is triggered on text containing that keyword, **Then** the new rule fires and the candidate is classified accordingly — without altering the behaviour of rules that did not change.
7. **Given** extraction is triggered on any input, **When** the rule engine is run twice on the same input with the same rule set, **Then** the output is byte-for-byte identical on both runs.

---

### Determinism Guarantees

The rule engine is a deterministic function: `(ruleSet, inputText) → candidateList`. The following properties MUST hold:

- **No randomness**: no random number generators, no GUIDs derived from input content, no time-dependent branching within rule evaluation.
- **No external state**: rule evaluation MUST depend only on the rule set and the input text; no network calls, no file reads, no environment variable reads may occur during evaluation.
- **Stable ordering**: candidates MUST be emitted in the same relative order as their source positions in the input text, across all rule engine runs on the same input.
- **Idempotency**: running the rule engine multiple times on the same input with the same rule set MUST produce identical output every time.
- **Rule isolation**: a rule MUST NOT modify any shared mutable state that could affect the evaluation of a subsequent rule in the same pass.

---

### Configurability Expectations

- The rule set MUST be expressible as structured data that is readable and writable without modifying compiled code.
- The classification priority ordering MUST be defined once and referenced consistently; it MUST NOT be duplicated across different rule definitions.
- It MUST be possible to add, remove, or reorder rules without changing any code other than the rule configuration itself.
- The rule engine MUST expose enough structure that a developer can audit the full rule set at a glance and predict the outcome for a given input without running the system.
- Rules MUST be individually nameable so that log output and observability traces can identify which rule produced a given classification outcome.

---

### Observability Requirements

- Each extraction event MUST log the number of rules evaluated, the number of candidates produced, and the total extraction duration — no raw pasted text may appear in any log entry.
- When a candidate is classified, the rule engine MUST record which rule fired (by rule name or identifier) so that this information is available in `ClassificationSignal` or an equivalent diagnostic field.
- Extraction duration MUST remain measurable per event; the performance baseline established in US2 (sub-millisecond for 10,000-character inputs in testing) MUST NOT regress beyond the 200 ms ceiling.
- Rule evaluation counts MUST be measurable so that rule engine bottlenecks can be identified if the rule set grows substantially.

---

### Security Considerations

- Rule definitions MUST NOT accept raw user input; rules are developer-authored configuration, not user-authored content.
- The rule engine MUST NOT execute arbitrary code provided in rule configuration; pattern matching MUST use a safe, bounded evaluation model (e.g., pre-compiled regular expressions with explicit length limits).
- No raw pasted text may appear in rule configuration storage, log output, or any telemetry payload; only derived counts and opaque identifiers are permitted, consistent with the US2 privacy constraint.
- Rules that reference regular expressions MUST be compiled with explicit input length guards to prevent catastrophic backtracking on adversarially crafted inputs.

---

### Performance Expectations

- The rule engine MUST satisfy the US2 performance target: extraction of 10,000-character inputs MUST complete in under 200 ms.
- Rule evaluation overhead MUST be negligible relative to the US2 baseline; introducing the rule engine abstraction MUST NOT degrade extraction performance for typical specification inputs.
- If the rule set grows to 50 or more rules, extraction of 10,000-character inputs MUST still complete in under 200 ms.

---

### Future Extensibility

- The rule engine architecture is designed to be the stable seam through which future extraction enhancements are introduced; this includes but is not limited to: new block-type handling, new keyword vocabularies, multi-language specification support, and context-enriched classification.
- The `ExtractionCandidate.Confidence` field (currently reserved) is intended for future use when AI-assisted extraction is optionally layered on top of the deterministic rule engine; US3 MUST NOT populate this field.
- Future versions may introduce a UI for browsing active rules and their priorities, without requiring changes to the rule engine's evaluation contract.

---

## Feature US4 — Level 1 Configurable Extraction Rules

**Status**: Draft  
**Created**: 2026-05-21

---

### Summary

US4 introduces a bounded configuration layer over the US3 rule engine, allowing teams to tailor extraction behavior to their project's specification vocabulary without writing code or regex patterns. Configuration is compiled at application startup into a configured `ExtractionRuleSet`; no configuration work occurs at extraction time. When no configuration is applied, the system behaves identically to US3 — all US3 acceptance criteria continue to pass unchanged.

---

### User Story

A workspace administrator notices their team's specification documents use vocabulary not recognized by the default extraction rules — for example, "Verify:" as a test prefix or "REQUIRED" as a requirement keyword. Rather than modifying compiled source code, the administrator configures these additions at the application or project level. After the application restarts with the new configuration, extraction produces better-classified candidates without any change to the extraction interface or the underlying rule engine contract.

---

### Goals

- Allow extending keyword sets for existing rule groups using bounded plain string values (not raw regex).
- Allow defining prefix-based classification rules as an alternative to keyword patterns.
- Allow enabling or disabling named rule groups from the default rule set by name.
- Allow optional priority tuning within defined bounds for existing named rule groups.
- Allow defining additional ignore prefixes to suppress specific line patterns from extraction.
- Preserve all determinism guarantees established in US3.
- Reproduce existing US3 extraction behavior exactly when no configuration is applied.
- Preserve all US1, US2, and US3 functional behavior unchanged.

---

### Non-Goals

- Custom scripting or arbitrary user-defined code in rule definitions.
- Unrestricted regex editing — configuration authors cannot supply raw regex patterns.
- AI or machine learning anywhere in the extraction path.
- External service calls during rule evaluation.
- Per-user rule variation — configuration is application-wide or project-wide, not per-user.
- Breaking changes to the GraphQL API contract (schema additions for project-level storage may be considered in future evolution only if clearly justified).
- Hot-reloading of configuration without application restart in MVP.
- A configuration UI for editing rules in MVP.

---

### User Workflow (Application-level, MVP)

1. Administrator identifies that default extraction rules do not match their team's specification vocabulary.
2. Administrator reviews the active rule configuration (configuration file or application settings).
3. Administrator edits the configuration — adding keywords, prefix rules, ignore prefixes, or adjusting named rule group priorities.
4. Application restarts; configuration is compiled and validated at startup.
5. On validation success: all extraction sessions use the updated rule set.
6. On validation failure: application logs a Warning with the rejection reason and falls back to the default rule set; no partial application of invalid configuration occurs.
7. To reset to default behavior: administrator removes all configuration entries; behavior returns exactly to US3 default.

---

### Functional Requirements

- **FR-US4-001**: The system MUST provide a mechanism to add extra keywords to the BDD opener set; added keywords MUST be matched word-boundary, case-insensitive.
- **FR-US4-002**: The system MUST provide a mechanism to add extra keywords to the RFC-2119 uppercase set; added keywords MUST be matched case-sensitively with word-boundary anchoring.
- **FR-US4-003**: The system MUST provide a mechanism to add extra keywords to the RFC-2119 lowercase set; added keywords MUST be matched case-insensitively with word-boundary anchoring.
- **FR-US4-004**: The system MUST provide a mechanism to add extra keywords to the deferral marker set; added keywords MUST be matched case-insensitively.
- **FR-US4-005**: The system MUST provide a mechanism to define prefix-based classification rules as (prefix string, ScenarioKind) pairs; candidates whose stripped text begins with the configured prefix are classified accordingly.
- **FR-US4-006**: The system MUST provide a mechanism to add ignore prefixes; candidates whose stripped text begins with a configured ignore prefix MUST be excluded from the candidate list before classification.
- **FR-US4-007**: The system MUST allow named rule groups to be disabled by their registered rule name; a disabled rule is excluded from evaluation as though it were not present in the rule set.
- **FR-US4-008**: The system MUST allow optional priority overrides for named rule groups within a bounded integer range (greater than 0, less than 100).
- **FR-US4-009**: Configuration MUST be at application or project level; per-user rule variation is not supported in MVP.
- **FR-US4-010**: When no configuration is applied, the system MUST produce results identical to US3 (`ExtractionRuleSet.Default()`); all US3 acceptance criteria MUST pass without modification.
- **FR-US4-011**: Configured keyword additions MUST be word-boundary-matched by the rule engine; configuration authors MUST NOT supply raw regex patterns; the rule engine is responsible for escaping all configuration string values before incorporating them into patterns.
- **FR-US4-012**: All configuration values MUST be validated at application startup before any extraction session proceeds; invalid configuration MUST fail fast with a descriptive structured error.
- **FR-US4-013**: When configuration validation fails, the system MUST fall back to the default rule set and MUST log a Warning identifying the reason; no partial application of invalid configuration is permitted.
- **FR-US4-014**: Prefix-based classification rules MUST be assigned an explicit or default priority within the defined bounds (proposed default: 10).
- **FR-US4-015**: The system MUST NOT allow configuration to override, replace, or remove the unconditional default fallback rule (`Classify:Default`, priority 0).
- **FR-US4-016**: A configured rule set MUST satisfy all determinism guarantees from US3: no randomness, no external state, stable ordering, idempotency, and rule isolation.

---

### Acceptance Criteria

1. **Given** no configuration is applied, **When** extraction is triggered on any pasted text, **Then** the output is identical to what the US3 pipeline produced for the same input — all US3 acceptance criteria pass without modification.
2. **Given** "Verify" is added to the BDD opener keyword set, **When** a candidate line begins with "Verify ", **Then** it is classified as TEST with signal BddPattern.
3. **Given** "REQUIRED" is added to the RFC-2119 uppercase keyword set, **When** a candidate contains "REQUIRED", **Then** it is classified as REQUIREMENT with signal Rfc2119Uppercase.
4. **Given** a prefix rule "REQ-" → REQUIREMENT is configured, **When** a candidate's stripped text begins with "REQ-", **Then** it is classified as REQUIREMENT.
5. **Given** an ignore prefix "NOTE:" is configured, **When** a candidate's stripped text begins with "NOTE:", **Then** it is excluded from the candidate list and does not appear in the extraction result.
6. **Given** "Classify:FrPrefix" is disabled in configuration, **When** a candidate matches the FR-prefix pattern, **Then** the next-highest matching rule determines the classification outcome.
7. **Given** a configured keyword value contains a regex metacharacter (e.g., `(MUST)`), **When** the application starts, **Then** startup validation rejects the configuration with a descriptive error and the system falls back to the default rule set.
8. **Given** a valid configuration is applied and extraction is triggered twice on the same input, **When** both results are compared, **Then** the output is byte-for-byte identical — the determinism guarantee holds under configuration.
9. **Given** the priority of "Classify:Rfc2119Lowercase" is raised above that of "Classify:Rfc2119Uppercase" in configuration, **When** a candidate matches both, **Then** the lowercase rule wins and the candidate is classified with signal Rfc2119Lowercase.
10. **Given** configuration is reset to empty, **When** extraction is triggered, **Then** the output returns exactly to US3 default behavior with no residual state from the previous configuration.

---

### Configuration Boundaries

**Level 1 configurable elements:**

| Element | Configuration Mechanism | Constraint |
|---|---|---|
| BDD opener keywords | Additional string list | Plain strings; no regex metacharacters; word-boundary applied by engine; case-insensitive |
| RFC-2119 uppercase keywords | Additional string list | Uppercase strings; no regex metacharacters; word-boundary applied by engine; case-sensitive |
| RFC-2119 lowercase keywords | Additional string list | Lowercase strings; no regex metacharacters; word-boundary applied by engine; case-insensitive |
| Deferral marker keywords | Additional string list | Plain strings; no regex metacharacters; word-boundary applied by engine; case-insensitive |
| Prefix-based classification rules | (prefix, ScenarioKind) pairs | Literal prefix; no regex metacharacters; explicit or default priority |
| Ignored line prefixes | String list | Literal prefix match on stripped candidate text |
| Enable/disable named rule groups | Boolean per registered rule name | Only rules present in `ExtractionRuleSet.Default()` may be targeted |
| Priority overrides | Integer per registered rule name | Must be greater than 0 and less than 100 |

**Not configurable at Level 1:**

| Element | Reason |
|---|---|
| Arbitrary regex patterns | Unbounded ReDoS security surface |
| Custom code or scripts | Security; determinism cannot be guaranteed |
| Block type filter rules | Structural; changes what blocks reach the extraction stages |
| Default fallback rule (`Classify:Default`) | Safety invariant; must remain to guarantee every candidate receives a classification |
| Per-line sub-cap (`MaxLineLengthForPatternMatching`) | Relaxing it relaxes the ReDoS guard established in US3 |
| Pipeline stage order | Core architectural invariant |
| `ClassificationSignal` → `ScenarioKind` mapping | Core semantic invariant |

---

### Determinism Guarantees

All US3 determinism guarantees extend to configured rule sets:

- Configuration is compiled at application startup, not at extraction time; no compilation work occurs during evaluation.
- All configuration string values are escaped by the rule engine before incorporation into regex patterns; configuration authors cannot introduce regex non-determinism.
- Rule ordering and conflict-resolution semantics from US3 are unchanged — higher priority wins; first-registered wins on tie.
- A configured rule set satisfies the same `(ruleSet, inputText) → candidateList` functional contract as an unconfigured rule set.
- Running the same input through the same configured rule set multiple times MUST produce byte-for-byte identical output.

---

### Validation Requirements

At application startup, the following invariants MUST be enforced before any extraction session begins:

- **Keyword values**: non-empty; maximum 200 characters; printable ASCII only; MUST NOT contain regex metacharacters (`\ ^ $ . | ? * + ( ) [ ] { }`); maximum 50 additions per rule group.
- **Prefix values**: non-empty; maximum 200 characters; printable ASCII only; MUST NOT contain regex metacharacters.
- **Rule names in enable/disable entries**: MUST exactly match a rule name present in `ExtractionRuleSet.Default()`; "Classify:Default" MUST NOT appear in a disable list.
- **Priority overrides**: MUST be a positive integer strictly greater than 0 and strictly less than 100.
- **Escaped keyword uniqueness**: if a configured keyword, after escaping and word-boundary wrapping, produces a pattern that is already covered by the default rule, the system MAY log a Warning but MUST NOT reject the configuration.

---

### Security Requirements

- **SEC-US4-001**: Configuration values MUST be treated as untrusted input at validation time; the validation layer MUST NOT execute, evaluate, or interpret configuration values as code, file paths, or network addresses.
- **SEC-US4-002**: All keyword and prefix additions MUST be escaped by the rule engine before incorporation into regex patterns; the rule engine is solely responsible for escaping; configuration authors MUST NOT supply partially escaped values.
- **SEC-US4-003**: A configuration value that would produce an invalid or unsafe regex after engine escaping MUST be rejected with a descriptive structured error; the system MUST fall back to the default rule set.
- **SEC-US4-004**: Configuration MUST NOT carry raw pasted specification text; configuration entries describe structural rule additions (keywords, prefixes, flags, priorities) only.
- **SEC-US4-005**: Configured rule sets are subject to the same per-line sub-cap (`MaxLineLengthForPatternMatching`) that US3 uses to guard against catastrophic backtracking; this cap is not configurable.
- **SEC-US4-006**: Modifying rule configuration requires application-level or project-administrator authorization; end users interacting with the extraction interface MUST NOT be able to modify rule configuration.

---

### Observability Requirements

- **OBS-US4-001**: When a configured rule fires, `ClassificationSignal` reflects the winning rule group's signal and `WinningRuleName` identifies whether the winning rule was from the default set or from configuration.
- **OBS-US4-002**: At application startup, the active configuration MUST be logged in structured format: total keyword additions per rule group, prefix rule count, disabled rule names — keyword content and prefix text MUST NOT appear in log output.
- **OBS-US4-003**: A configuration validation failure MUST produce a Warning log entry identifying the rejection reason (validation rule violated, offending field name); the fallback to the default rule set MUST also be logged.
- **OBS-US4-004**: The `rulesEvaluatedCount` metric continues to count all rule evaluations including configured rules; no new log fields are required.
- **OBS-US4-005**: No log event produced by the extraction pipeline or the configuration layer MAY carry the text content of configured keyword values or prefix values; only counts and developer-assigned rule names are permitted in log fields.

---

### Performance Expectations

- All configured keyword additions and prefix rules are compiled into patterns at application startup; zero compilation work occurs at extraction time.
- Keyword additions extend an existing pattern (one regex evaluation per candidate, not N separate keyword checks per candidate).
- Maximum configuration footprint: 50 additional keywords across all groups + 50 prefix rules applied to 87 candidates ≈ 4,350 additional evaluations per typical run — negligible relative to the 200 ms ceiling.
- The extraction performance ceiling of 200 ms established in US2 and confirmed in US3 MUST be preserved under any valid configuration.

---

### Fallback and Default Behavior

- **No configuration applied**: extraction behavior is identical to US3 (`ExtractionRuleSet.Default()`); no behavioral difference.
- **Configuration validation failure**: the system logs a Warning, falls back to the default rule set for all subsequent sessions, and continues operating; no partial application of invalid configuration is permitted.
- **Named rule disabled**: the remaining rules are re-evaluated in their existing priority order; no gaps or undefined behavior arise from removing a named rule.
- **Keyword addition produces no matches**: output for that input is identical to the output from the unextended rule set.
- **Configuration reset to empty**: behavior returns exactly to US3 default; no residual state carries over from the previous configuration.

---

### Future Evolution

- **Project-level configuration storage**: per-`projectId` rule configuration stored in the database; requires a new table, new GraphQL mutations, and a project settings interface — out of scope for US4 MVP.
- **Configuration UI**: a read-only configuration viewer (active rules, priorities) could be added without any schema change; a write interface requires project-level storage.
- **Level 2 configurability**: custom regex patterns subject to static ReDoS analysis at configuration time; patterns that exceed the complexity threshold are rejected with a descriptive error.
- **Level 3 configurability**: full user-defined rule types, requiring either a sandboxed execution environment or a purpose-built DSL.
- **Rule versioning**: each configuration snapshot carries a version identifier; extraction results can reference the rule set version that produced them.
- **Rule sharing**: export and import of configuration snapshots between projects.
- **AI-rules interoperability**: configured deterministic rules act as high-confidence overrides; an AI classifier handles candidates that reach the `Classify:Default` fallback; consistent with the reserved `ExtractionCandidate.Confidence` field introduced in US3.
- An opt-in AI classification tier may eventually consume the deterministic rule engine's output as a baseline and apply probabilistic re-scoring; US3 MUST preserve the architectural seam (`IScenarioExtractionService`) that would allow this without coupling the deterministic path to AI infrastructure.
- Versioning of rule sets to support reproducible extraction across deployments is a candidate future enhancement; the structured rule configuration introduced in US3 is the prerequisite.