# Feature Specification: Code Traceability

**Feature Branch**: `003-delta-impact-analysis` (added here)  
**Created**: 2026-06-04  
**Status**: Implemented  
**Depends on**: `001-create-scenario`, `002-traceability-coverage`

---

## Purpose

Connect the QA artifact world to the code world.

Help a test lead answer:
- **"If this file changes, which requirements and tests are affected?"**
- **"Which code files implement this requirement?"**

---

## Architecture Context

```
Requirement
    ↓ (TraceLinkType.Covers — existing)
Test
    ↓ (CodeLink — new in this feature)
Code File
    ↓ (future)
Commit
    ↓ (future)
Pull Request / AI Session
```

---

## User Stories

### US1 — Register Code Files

A test lead registers source-code files by path (e.g. `backend/Services/ScenarioService.cs`).
Each file can have an optional description. Files are unique per project.

**Acceptance**:
1. Register `backend/Services/ScenarioService.cs` → appears in registry.
2. Attempt duplicate path → error "already registered."
3. Delete a file → removes it and all its code links.

### US2 — Link Files to QA Artifacts

A test lead selects a registered file and links it to one or more requirements and/or tests.

**Acceptance**:
1. Link `ScenarioService.cs` to `FR-001: System MUST allow users to create a scenario` → link appears.
2. Link same file to a test → test appears under "Linked Tests."
3. Attempt duplicate link → error "already exists."
4. Remove a link → link disappears.

### US3 — Code Impact View

A test lead selects a file and sees which requirements and tests are linked to it.

**Acceptance**:
1. Select `ScenarioService.cs` → see all linked requirements and tests with badges.
2. Unlinked file → "No requirements linked" and "No tests linked" messages.

### US4 — Summary Dashboard

KPI cards show: Total Files, Linked Requirements, Linked Tests, Unlinked Files.

---

## Data Model

### CodeFile
```
id: UUID
project_id: VARCHAR(200)
file_path: VARCHAR(1000) [unique per project]
file_name: VARCHAR(255) [derived from file_path]
description: TEXT?
created_at: TIMESTAMP
```

### CodeLink
```
id: UUID
project_id: VARCHAR(200)
code_file_id: UUID
scenario_id: UUID
scenario_kind: VARCHAR(50) [Requirement|Test]
created_at: TIMESTAMP
[unique: (code_file_id, scenario_id)]
```

Stored separately from `TraceLink` — code links have different semantics and will gain future extensions (git commit hash, AI confidence score, etc.) without affecting the QA trace graph.

---

## Assumptions

- Manual traceability only in v1 — no repository scanning, no Git integration.
- File paths are stored as free text; the app does not validate that the file exists on disk.
- Only `Requirement` and `Test` scenarios can be linked to code files; `NeedsClarification` is excluded.
- Single project scope.

---

## Future Extension Points

| Extension | Status |
|---|---|
| Git commit hash on CodeLink | v2 hook on model |
| Pull request linking | v2 |
| Repository scanning (auto-discover files) | v3 |
| AI-suggested links (Claude matches file to requirements) | v3 |
| Spec Drift integration (drift triggered by file changes) | v3 |
