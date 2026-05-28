# QA Review Studio Configuration Guide

## Overview

QA Review Studio supports Level 1 configurable deterministic extraction rules.

The goal is to adapt extraction safely without AI, scripting, arbitrary plugins, or unrestricted runtime behavior.

The configuration model must match the actual runtime implementation used by the extraction engine.

## Configuration Location

Typical location:

```text
wwwroot/appsettings.json
```

Configuration is loaded at startup.

After changing configuration:

1. restart the frontend application
2. verify extraction behavior manually
3. confirm logs show configuration loaded successfully

## Analysis Profiles

The UI may support analysis profiles such as:

- Speckit Structured Spec
- Generic Document

Speckit should normally be the default profile when the tool is primarily used with Speckit `spec.md` files.

## Supported Level 1 Concepts

Supported concepts may include:

- requirement language indicators
- test openers/prefixes
- clarification signals
- ignore prefixes
- narrative/context suppression rules
- safe rule enable/disable behavior
- bounded deterministic rule priority behavior

## Unsupported Configuration

The following are intentionally unsupported:

- arbitrary scripting
- runtime code execution
- unrestricted regex editing
- AI-generated extraction rules
- machine-learning classification
- external executable plugins
- user-provided compiled extensions

## Deterministic Behavior

```text
same text + same configuration = same result
```

This supports:

- QA repeatability
- predictable reviews
- auditability
- regression testing
- stable extraction

## Context Heading Behavior

The extraction engine preserves source headings as ContextHeading metadata.

Examples:

```text
User Story 1
Functional Requirements
Acceptance Criteria
Observability
Edge Cases
Open Questions
```

These headings are used for grouping and traceability context.

They should not become candidate text unless they contain actionable content.

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


## Classification vs Review Status

Classification answers:

```text
What kind of artifact is this?
```

Review status answers:

```text
What did QA decide about this artifact?
```

| Classification | Possible Review Status |
|---|---|
| REQUIREMENT | Accepted |
| TEST | Needs Review |
| NEEDS_CLARIFICATION | Rejected |

This separation should always be preserved.

## Safe Configuration Principles

Configuration should:

- remain bounded
- remain deterministic
- avoid user-defined execution
- avoid arbitrary regex complexity
- avoid hidden runtime behavior
- remain understandable to QA and developers
