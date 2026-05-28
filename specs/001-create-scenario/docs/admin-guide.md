# QA Review Studio Admin Guide

## Overview

QA Review Studio supports:

- deterministic specification analysis
- configurable extraction rules
- local `.md` / `.txt` import
- Speckit-aware analysis profile
- review workflow
- reviewed candidate persistence
- QA Artifact Library
- manual New Test Scenario workflow
- traceability links
- coverage/dashboard metrics
- local developer startup scripts

## Responsibilities

Administrators or maintainers should:

- validate local startup configuration
- verify database/container startup
- verify deterministic extraction behavior
- configure extraction rules if supported
- monitor logs
- verify raw specification text is not logged
- maintain startup scripts
- verify review persistence
- verify QA Artifact Library behavior
- verify New Test Scenario behavior
- verify dashboard and traceability metrics

## Local Startup

Recommended startup method:

```text
scripts/start-local.bat
```

The PowerShell launcher uses Podman by default, detects compose files, checks Podman readiness, starts containers, starts backend, and then starts frontend.

## Persistence Responsibilities

| Area | Purpose |
|---|---|
| reviewed_candidates | QA review workspace and audit trail |
| scenarios / artifacts | Saved QA artifacts and TEST scenarios, depending on implementation naming |
| candidate links | Traceability between requirements, tests, and clarifications |

## Artifact Model

Current supported artifact classifications are:

- REQUIREMENT
- TEST
- NEEDS_CLARIFICATION

User Story headings should normally be stored as context/grouping metadata, not as a separate GraphQL type unless the backend explicitly supports it.
### Artifact Type vs ContextHeading

User Story, Functional Requirements, Acceptance Criteria, Edge Cases, Observability, and Assumptions are normally treated as `ContextHeading` / grouping metadata.

They should not become new GraphQL artifact types unless the product explicitly decides to persist them as first-class artifacts.


## Manual Scenario Creation

Manual creation is intended for TEST scenarios only.

The manual creation page should be named **New Test Scenario** and should not ask the user to choose Requirement/Test/Clarification.

## Specification Source of Truth

The imported `spec.md` remains the source of truth.

QA Review Studio should:

- analyze specifications
- extract QA artifacts
- identify gaps
- support traceability
- support review workflow

It should not silently rewrite original specification documents.

## Observability Expectations

Logs should help diagnose:

- application startup
- rule loading
- extraction/analyze events
- review save
- artifact save
- scenario save
- validation failures

Logs must not contain:

- raw pasted specification text
- uploaded file content
- candidate body text
- private configured vocabulary values

## Post-Deployment Checks

Verify:

1. Backend starts
2. Frontend starts
3. GraphQL endpoint is reachable
4. `.md` / `.txt` import works
5. Speckit profile is available and selected by default if intended
6. Analyze Specification produces grouped artifacts
7. review statuses can be changed
8. Save Review persists reviewed artifacts
9. QA Artifact Library loads
10. TEST filter is default in library if implemented
11. New Test Scenario creates TEST artifacts only
12. dashboard metrics load
13. traceability links work
14. no browser console errors appear
