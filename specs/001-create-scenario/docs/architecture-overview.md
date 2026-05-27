# BirkNext Architecture Overview

## High-Level Architecture

BirkNext consists of:

- Blazor WebAssembly frontend
- ASP.NET Core backend
- GraphQL API
- PostgreSQL persistence
- deterministic extraction engine
- configurable rule engine
- local file import workflow
- review workflow
- reviewed candidate persistence
- scenario persistence
- shared frontend design system

## Core Principle

Client owns extraction. Server owns persistence.

```text
Client-side import/paste
→ Client-side deterministic extraction
→ User review
→ Save review state
→ Save selected candidates as scenarios
```

Raw pasted/imported specification content is not sent to the backend during extraction.

## Extraction Pipeline

```text
Paste or Import Text
→ Normalize
→ Filter
→ Apply Rule Engine
→ Classify
→ Group by ContextHeading
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
→ Reviewed Candidate
→ Optional Final Scenario
```

## Persistence Boundary

| Concept | Table/Area | Purpose |
|---|---|---|
| Reviewed Candidate | reviewed_candidates | QA review workspace and audit trail |
| Scenario | scenarios | finalized scenario registry |

Reviewed candidates and finalized scenarios are intentionally separate.

## Context Grouping

BirkNext preserves source context using `ContextHeading`.

```text
REQUIREMENT
  Functional Requirements
  Observability

TEST
  User Story 1
  Acceptance Criteria

NEEDS_CLARIFICATION
  Edge Cases
  Open Questions
```

## GraphQL Boundary

GraphQL supports saving/loading reviewed candidates and saving finalized scenarios. Extraction and file import remain client-side.

## Key Quality Attributes

- deterministic behavior
- review-before-save workflow
- client-side extraction privacy
- stable GraphQL contracts
- grouped review usability
- safe observability
- lightweight design system
