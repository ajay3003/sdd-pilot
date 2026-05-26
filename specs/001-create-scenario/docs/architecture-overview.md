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
- shared frontend design system

## Core Architecture Principle

Client owns extraction.

Server owns persistence.

```text
Client-side import/paste
→ Client-side deterministic extraction
→ User review
→ Save selected candidates
→ Backend persistence
```

Raw pasted/imported specification content is not sent to the backend during extraction.

## Extraction Pipeline

```text
Paste or Import Text
→ Normalize
→ Filter
→ Apply Rule Engine
→ Classify
→ Review
→ Save Selected Items
```

## File Import Flow

```text
Local .md/.txt file
→ Browser reads file content
→ Text area populated
→ User may edit text
→ Extraction pipeline runs
→ User reviews candidates
→ Selected scenarios are saved
```

The file itself is not persisted automatically.

## Persistence Boundary

Persisted:

- selected scenarios
- scenario type
- scenario metadata required by backend

Not persisted automatically:

- uploaded file
- raw imported text
- raw pasted text
- temporary extraction candidates
- unselected candidates

## Rule Engine Evolution

US2 introduced deterministic extraction.

US3 introduced an internal deterministic rule engine.

US4 introduced Level 1 configurable deterministic rules.

Current supported configuration includes:

- prefixes
- keywords
- ignore prefixes
- safe rule toggles
- bounded priority behavior, if enabled

Unsupported:

- scripting
- arbitrary regex
- AI-based rules
- runtime code execution

## GraphQL Boundary

GraphQL is used for saving selected scenarios.

The existing create-scenarios mutation remains the persistence boundary.

The extraction engine and file import flow should not require GraphQL schema changes.

## Frontend UX Architecture

The frontend now includes:

- clean application shell
- Extract workflow page
- Scenario management page
- shared design-system CSS
- card-based review layout
- visual badges
- loading states
- notification styles
- import area

## Feature Evolution

US1:
- Manual scenario management

US2:
- Deterministic extraction workflow

US3:
- Internal deterministic rule engine

US4:
- Configurable deterministic extraction rules

Product phase:
- Modern UX
- Shared design system
- Local `.md` / `.txt` import
- Startup scripts and local onboarding documentation

## Key Quality Attributes

- deterministic behavior
- review-before-save workflow
- client-side extraction privacy
- stable GraphQL contracts
- safe observability
- lightweight frontend design system
- local developer friendliness
