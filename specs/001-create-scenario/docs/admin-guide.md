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
- manual Create Test Scenario workflow
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
- verify Create Test Scenario behavior
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

The manual creation page should be named **Create Test Scenario** and should not ask the user to choose Requirement/Test/Clarification.

## Specification Source of Truth

The imported `spec.md` remains the source of truth.

QA Review Studio should:

- analyze specifications
- extract QA artifacts
- identify gaps
- support traceability
- support review workflow

It should not silently rewrite original specification documents.

## AI-Assisted SDD Governance

QA Review Studio was initially bootstrapped using Speckit/AI-assisted development, where `spec.md`, `plan.md`, and `tasks.md` may be generated together.

As the project matures, these artifacts should be treated with different governance levels.

| Artifact | Purpose | Recommended AI Role |
|---|---|---|
| `spec.md` | Defines WHAT the product should do | Suggest/draft changes, but human approval required |
| `plan.md` | Defines HOW the architecture and implementation approach should evolve | May be updated for significant architectural changes, but human review required |
| `tasks.md` | Defines implementation steps | Can be generated or updated more freely |
| code/tests | Implements and verifies behavior | AI-assisted implementation is appropriate |

### Why Claude/Codex May Update `plan.md`

Large implementation prompts can cause Claude/Codex to update `plan.md` even if the prompt does not explicitly say “update plan”.

This is most likely when the requested change affects:

- persistence model
- GraphQL schema
- workflow lifecycle
- domain model
- architecture boundaries
- major UX flow
- review or traceability behavior

Example:

```text
Implement persistent QA Delta Reviews
```

This is not just a UI change. It introduces a new persistent review concept, backend/GraphQL changes, saved comparison lifecycle, and QA evidence workflow. In that case, updating `plan.md` is reasonable.

### Practical Rule

| Change Type | Expected Documentation Behavior |
|---|---|
| Small UI fix | Code/tests only |
| Bug fix | Code/tests, maybe troubleshooting notes |
| New feature | Code/tests, maybe tasks |
| Architectural evolution | Update `plan.md` with human review |
| Product behavior or scope change | Update `spec.md` with human approval |
| Implementation breakdown | Update `tasks.md` |

### Governance Principle

The project is moving from:

```text
AI-generated prototype
```

toward:

```text
AI-assisted governed platform
```

That means humans should increasingly own product intent, terminology, domain boundaries, and architecture decisions, while AI accelerates implementation, tests, and task decomposition.


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
11. Create Test Scenario creates TEST artifacts only
12. dashboard metrics load
13. traceability links work
14. no browser console errors appear
