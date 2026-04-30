# Data Model: Scenario Management

**Phase**: 1 | **Branch**: `001-create-scenario` | **Date**: 2026-04-30

---

## Entities

### Scenario

The single domain entity for this feature. Represents a captured specification or QA scenario scoped to a project workspace.

| Field | C# Type | DB Column | Constraints | Notes |
|-------|---------|-----------|-------------|-------|
| `Id` | `Guid` | `id` (PK) | NOT NULL, generated | Client-generated or server-generated UUID |
| `Title` | `string` | `title` | NOT NULL, max 500 chars | FR-002: must be non-empty |
| `Description` | `string?` | `description` | NULL allowed | Optional free text; no max enforced in v1 |
| `Kind` | `ScenarioKind` | `kind` | NOT NULL, stored as `varchar` | FR-003: Requirement \| Test \| NeedsClarification |
| `ProjectId` | `string` | `project_id` | NOT NULL, max 200 chars | FR-010: scopes the scenario to a workspace |
| `CreatedAt` | `DateTimeOffset` | `created_at` | NOT NULL, default `now()` | Set by server on creation; not client-supplied |

**Table name**: `scenarios`  
**Default sort**: `created_at DESC` (spec assumption — most recent first)

---

### ScenarioKind (enum)

```csharp
public enum ScenarioKind
{
    Requirement,
    Test,
    NeedsClarification
}
```

Stored in PostgreSQL as a `varchar` (EF Core value converter) so the column remains readable without joining an enum table.

---

## EF Core Entity Class

```csharp
public class Scenario
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ScenarioKind Kind { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
```

---

## Validation Rules

| Field | Rule | Error message |
|-------|------|---------------|
| `Title` | Required, 1–500 chars | "Title is required" / "Title must not exceed 500 characters" |
| `Kind` | Must be a valid `ScenarioKind` value | "A valid type must be selected" |
| `ProjectId` | Required (supplied by caller, not user) | N/A — server-side assertion only |
| `Description` | No validation in v1 | — |

---

## State Transitions

Scenarios are immutable after creation in v1. No state machine applies.

---

## Database Schema (EF Core migration target)

```sql
CREATE TABLE scenarios (
    id          uuid         NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    title       varchar(500) NOT NULL,
    description text,
    kind        varchar(30)  NOT NULL,
    project_id  varchar(200) NOT NULL,
    created_at  timestamptz  NOT NULL DEFAULT now()
);

CREATE INDEX ix_scenarios_project_id_created_at
    ON scenarios (project_id, created_at DESC);
```

The index on `(project_id, created_at DESC)` satisfies the default list query pattern (FR-010 scoping + reverse-chronological order) in a single index scan.

---

## GraphQL Type Mapping

| Entity field | GraphQL field | GraphQL type |
|-------------|---------------|-------------|
| `Id` | `id` | `ID!` |
| `Title` | `title` | `String!` |
| `Description` | `description` | `String` |
| `Kind` | `kind` | `ScenarioKind!` (enum) |
| `ProjectId` | `projectId` | `String!` |
| `CreatedAt` | `createdAt` | `DateTime!` |

---

## Out of Scope (v1)

- Edit / update scenario
- Delete scenario
- Pagination or cursor-based list
- Scenario relationships or parent/child nesting
