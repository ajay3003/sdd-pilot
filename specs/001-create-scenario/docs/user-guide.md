# QA Review Studio User Guide

## Overview

QA Review Studio helps test teams analyze specification documents and turn them into structured QA artifacts.

Users can:

- paste specification text manually
- import a local `.md` or `.txt` file
- analyze the specification
- review extracted QA artifacts
- classify review decisions
- save review state
- save selected TEST artifacts as test scenarios
- browse artifacts in the QA Artifact Library

## Core Concepts

| Concept | Meaning |
|---|---|
| QA Artifact | A reviewed item extracted from a specification |
| Requirement | Expected system behavior |
| Test | Verification, acceptance criteria, or Given/When/Then flow |
| Needs Clarification | Open question, ambiguity, unresolved decision, or risk |
| Context Heading | Source section/group such as User Story, Functional Requirements, Edge Cases |
| Test Scenario | Executable or reviewable QA verification flow |

## Artifact Type vs ContextHeading

QA Review Studio separates artifact type from source context.

Artifact type means:

```text
REQUIREMENT
TEST
NEEDS_CLARIFICATION
```

ContextHeading means the source section/group, for example:

```text
User Story 1
Functional Requirements
Acceptance Criteria
Edge Cases
Observability
Assumptions
```

A User Story is normally treated as context/grouping metadata, not as a separate GraphQL artifact type.

Example:

| Spec Structure | Meaning |
|---|---|
| User Story 2 - View Scenario List | ContextHeading |
| Given one or more scenarios exist... | TEST |
| System MUST display all stored scenarios | REQUIREMENT |
| What happens if backend is unavailable? | NEEDS_CLARIFICATION |


## Main Workflow

1. Open **Specification Review**
2. Paste text or import a `.md` / `.txt` file
3. Choose analysis profile
   - Speckit Structured Spec
   - Generic Document
4. Click **Analyze Specification**
5. Review grouped QA artifacts
6. Mark items as:
   - New
   - Accepted
   - Rejected
   - Needs Review
7. Click **Save Review** to persist review state
8. Select TEST artifacts that should become executable scenarios
9. Click **Save Selected** where supported

## Review Statuses

| Status | Meaning |
|---|---|
| New | Not yet reviewed |
| Accepted | Useful and approved for further work |
| Rejected | Noise, duplicate, or not useful |
| Needs Review | Requires human clarification or follow-up |

## QA Artifact Library

The QA Artifact Library stores reviewed artifacts.

It may contain:

- requirements
- tests
- clarification findings

The default library filter should prioritize **TEST** artifacts because testers usually want executable scenarios first.

## New Test Scenario

The manual creation flow is for TEST scenarios only.

Use **New Test Scenario** for:

- exploratory testing
- regression scenarios
- bug reproduction flows
- manual QA validation

The manual creation page should not require a type selector because manually created scenarios are always TEST artifacts.

## Specification Review Session

After running **Analyze Specification**, the current review session should remain available when navigating away and back.

The session may restore:

- extracted artifacts
- review decisions
- filters
- search text
- expanded/collapsed groups
- selected analysis profile

This is temporary working-session continuity, not permanent spec storage.

## Working With Speckit/AI-Generated Artifacts

In early project phases, Speckit and AI can generate `spec.md`, `plan.md`, and `tasks.md` quickly.

Later, when the product becomes more stable:

- `spec.md` should be treated as the human-approved source of product intent
- `plan.md` should describe architectural direction and should be reviewed when updated
- `tasks.md` can change more often as implementation evolves

If a large feature such as persistent QA Delta Reviews is implemented, the plan may be updated because the architecture has changed.


## Important Principles

- Nothing is auto-saved
- Extraction is deterministic
- The original `spec.md` is not modified automatically
- Users review before saving
- Raw specification text should not be logged
- QA artifacts and test scenarios are related but not identical
