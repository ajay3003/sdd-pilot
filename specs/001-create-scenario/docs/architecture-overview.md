# QA Review Studio Architecture Overview

## High-Level Architecture

QA Review Studio consists of:

- Blazor WebAssembly frontend
- ASP.NET Core backend
- GraphQL API
- PostgreSQL persistence
- deterministic extraction engine
- configurable rule engine
- Speckit-aware analysis profile
- local file import workflow
- review workflow
- QA Artifact Library
- manual New Test Scenario workflow
- traceability links
- dashboard/coverage metrics

## Core Principle

Client owns extraction and analysis.

Server owns persistence.

```text
Client-side import/paste
→ Client-side deterministic analysis
→ Classify QA artifacts
→ Group by ContextHeading
→ User review
→ Save review state
→ Save selected TEST artifacts as scenarios where applicable
```

Raw pasted/imported specification content is not sent to the backend during extraction unless a future explicit spec repository is implemented.

## Artifact Type vs ContextHeading / Grouping

QA Review Studio separates:

- artifact classification
- source context/grouping

This distinction is important for deterministic extraction, subgrouping, GraphQL modeling, and traceability.

### Artifact Type

Artifact type answers:

```text
What kind of QA artifact is this?
```

Supported artifact types are:

- REQUIREMENT
- TEST
- NEEDS_CLARIFICATION

Examples:

| Example Text | Artifact Type |
|---|---|
| System MUST display all stored scenarios | REQUIREMENT |
| Given one or more scenarios exist... | TEST |
| What happens if backend is unavailable? | NEEDS_CLARIFICATION |

### ContextHeading / Grouping

ContextHeading answers:

```text
Where did this artifact come from?
```

Examples:

- User Story 1 - Create Scenario
- Functional Requirements
- Acceptance Criteria
- Edge Cases
- Observability
- Assumptions

These headings are preserved as metadata and are used for:

- subgrouping
- review organization
- traceability
- source context preservation

Context headings are not automatically separate GraphQL artifact types.

### Example Mapping

Example specification structure:

```markdown
### User Story 2 - View Scenario List

#### Acceptance Criteria

Given one or more scenarios exist...

#### Functional Requirements

System MUST display all stored scenarios

#### Edge Cases

What happens if the backend is unavailable?
```

Produces:

| ContextHeading | Artifact Type |
|---|---|
| User Story 2 - View Scenario List | TEST |
| Functional Requirements | REQUIREMENT |
| Edge Cases | NEEDS_CLARIFICATION |

### Why USER_STORY Is Not Required as GraphQL Type

User stories are currently treated primarily as:

- grouping/context metadata
- source structure
- review organization

They are not persisted as first-class QA artifact types in the MVP.

This keeps the model simple and avoids unnecessary GraphQL/domain complexity.


## Extraction Pipeline

```text
Paste or Import Text
→ Normalize
→ Filter narrative/metadata
→ Apply Rule Engine
→ Classify
→ Attach ContextHeading
→ Group
→ Review
→ Save Review or Save Selected
```

## Review Workflow

Candidates receive a review status:

- New
- Accepted
- Rejected
- Needs Review

```text
Extracted Candidate
→ Reviewed QA Artifact
→ Optional TEST Scenario
```

## Persistence Boundary

| Concept | Purpose |
|---|---|
| Reviewed Candidate / QA Artifact | QA review workspace and audit trail |
| TEST Scenario | Executable/reviewable QA validation flow |
| Candidate Link | Traceability between requirements, tests, and clarifications |
| ContextHeading | Grouping metadata from the source specification |

## QA Artifact Library

The QA Artifact Library is a repository of reviewed QA artifacts.

It may show separate sections or filters for:

- TESTS
- REQUIREMENTS
- NEEDS_CLARIFICATION

The default view should prioritize TEST artifacts for testers.

## New Test Scenario

Manual scenario creation is a separate workflow for creating TEST artifacts.

It should not be used to create requirements or clarification records.

## Specification Source of Truth

The original `spec.md` remains source-controlled and human-owned.

QA Review Studio may produce:

- QA findings
- clarification artifacts
- coverage gaps
- traceability
- suggested tasks

It must not automatically modify the original `spec.md`.

## GraphQL Boundary

GraphQL supports persistence of:

- reviewed candidates / QA artifacts
- saved scenarios
- traceability links
- dashboard data if applicable

GraphQL does not need a USER_STORY artifact type unless the product explicitly decides to persist user stories as first-class artifacts.

## Key Quality Attributes

- deterministic behavior
- review-before-save workflow
- explainable extraction
- source context preservation
- client-side extraction privacy
- stable GraphQL contracts
- grouped review usability
- traceability readiness
- safe observability
