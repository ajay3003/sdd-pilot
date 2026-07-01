# Data Model: Access Administration Panel

**Feature branch**: `005-access-admin-panel`
**Phase**: 1 — Design
**Date**: 2026-05-08

---

## Domain Entities

These map 1:1 to API response shapes. Field names use C# PascalCase here; the service layer handles JSON deserialization.

### Operation

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `Guid` | M2LB UUID |
| `ServiceName` | `string` | Grouping key (e.g. `"PersonTjeneste"`) |
| `Name` | `string` | Operation identifier (e.g. `"LesPersonopplysninger"`) |
| `DisplayName` | `string` | Human-readable label for UI |
| `Classification` | `OperationClassification` | `General` \| `ChildSpecific` |
| `IsActive` | `bool` | |
| `IsVerified` | `bool` | Unverified = badge counter source |
| `AssignedRoleCount` | `int` | Active roles currently holding this operation — used for deactivation warning |

```csharp
public enum OperationClassification { General, ChildSpecific }
```

### OperationHistoryEntry

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `Guid` | |
| `OperationId` | `Guid` | |
| `ChangedAt` | `DateTimeOffset` | |
| `ChangedByUserId` | `Guid` | |
| `ChangedByDisplayName` | `string` | Denormalized from directory |
| `PreviousClassification` | `OperationClassification` | |
| `NewClassification` | `OperationClassification` | |
| `Justification` | `string?` | Optional at classification time |

### GeneralRole

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `Guid` | |
| `Name` | `string` | Unique across all general roles |
| `Description` | `string?` | |
| `IsActive` | `bool` | |
| `Operations` | `IReadOnlyList<OperationSummary>` | Assigned general-type operations |
| `ActiveAssignmentCount` | `int` | Used in deactivation warning (FR-016) |

### ChildSpecificRole

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `Guid` | |
| `Name` | `string` | Unique across all child-specific roles |
| `Description` | `string?` | |
| `IsActive` | `bool` | |
| `GisVedNødtilgang` | `bool` | Emergency access flag — security-critical, no optimistic update (FR-021) |
| `Operations` | `IReadOnlyList<OperationSummary>` | Assigned child-specific-type operations |
| `ActiveRelationCount` | `int` | Used in deactivation warning (FR-022) |

### OperationSummary

Lightweight reference used inside role models to avoid full operation details in every role response.

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `Guid` | |
| `Name` | `string` | |
| `ServiceName` | `string` | |
| `Classification` | `OperationClassification` | |

### GeneralRoleAssignment

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `Guid` | |
| `UserId` | `Guid` | Assignee (EntraID OID) |
| `UserDisplayName` | `string` | Denormalized from directory |
| `RoleId` | `Guid` | |
| `RoleName` | `string` | Denormalized for display |
| `OrgUnitId` | `Guid?` | Optional org unit scope |
| `OrgUnitName` | `string?` | Denormalized for display |
| `ValidFrom` | `DateTimeOffset` | |
| `ValidTo` | `DateTimeOffset?` | Null = no expiry |
| `IsActive` | `bool` | |

### ChildRelation

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `Guid` | |
| `UserId` | `Guid` | |
| `UserDisplayName` | `string` | |
| `ChildId` | `Guid` | M2LB child UUID — **never** placed in URLs or page titles (Constitution VI) |
| `RoleId` | `Guid` | Child-specific role |
| `RoleName` | `string` | Denormalized |
| `ValidFrom` | `DateTimeOffset` | |
| `ValidTo` | `DateTimeOffset?` | |
| `IsActive` | `bool` | |

### EmergencyAccessEvent

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `Guid` | |
| `UserId` | `Guid` | User who activated emergency access |
| `UserDisplayName` | `string` | |
| `ChildId` | `Guid` | **Never** exposed in URLs or browser history |
| `Justification` | `string` | Required at activation by the user |
| `ActivatedAt` | `DateTimeOffset` | |
| `Duration` | `TimeSpan` | Configured window |
| `ExpiresAt` | `DateTimeOffset` | Computed: `ActivatedAt + Duration` |
| `Status` | `EmergencyEventStatus` | `Active` \| `Expired` \| `Revoked` |
| `IsReviewed` | `bool` | |
| `ReviewedAt` | `DateTimeOffset?` | |
| `ReviewedByUserId` | `Guid?` | |
| `ReviewNote` | `string?` | Required when reviewing (FR-029) |
| `RevokedAt` | `DateTimeOffset?` | |
| `RevocationReason` | `string?` | Optional when revoking (FR-030) |

```csharp
public enum EmergencyEventStatus { Active, Expired, Revoked }
```

### AuditLogEntry

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `Guid` | |
| `Timestamp` | `DateTimeOffset` | |
| `ActorId` | `Guid` | |
| `ActorDisplayName` | `string` | |
| `ActionType` | `string` | e.g. `"KlassifiserOperasjon"`, `"OpprettGenerellRolle"` |
| `EntityType` | `string` | e.g. `"Operation"`, `"GeneralRole"`, `"EmergencyAccessEvent"` |
| `EntityId` | `Guid` | |
| `EntityDisplayName` | `string` | Human-readable entity reference |
| `BeforeState` | `JsonElement?` | Structured JSON — rendered human-readable, not as raw text (FR-034) |
| `AfterState` | `JsonElement?` | |

### OrganisationUnit

Read-only from this module (spec §Assumptions).

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `Guid` | |
| `Name` | `string` | |
| `ParentId` | `Guid?` | Hierarchical — may need tree flattening for select lists |

### AdminBadgeCounts

Returned by `IAdminBadgeService` (see research.md Decision 1).

| Field | Type | Notes |
|-------|------|-------|
| `UnverifiedOperationCount` | `int` | Drives badge on Operation Catalogue nav item (FR-002) |
| `UnreviewedActiveEmergencyEventCount` | `int` | Drives badge on Emergency Access nav item (FR-003) |

---

## Frontend-Only View Models

These exist only in the frontend — never serialized.

```csharp
// Filter state for Operation Catalogue — maps to URL query parameters
public record OperationCatalogueFilter(
    string? ServiceName = null,
    OperationClassification? Classification = null,
    bool? IsActive = null,
    bool? IsVerified = null);

// Filter state for General/Child-Specific Roles list — name search only
public record RoleListFilter(string? Name = null);

// Filter state for Audit Log — maps to URL query parameters (FR-035)
public record AuditLogFilter(
    Guid? ActorId = null,
    string? EntityType = null,
    Guid? EntityId = null,
    DateOnly? From = null,
    DateOnly? To = null);

// Paginated result wrapper for Audit Log (server-side pagination, FR-032)
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int PageIndex,
    int PageSize);

// User search result from identity directory (Microsoft Entra)
public record DirectoryUser(Guid UserId, string DisplayName, string Email);

// Effective access summary for a user (read-only, derived server-side)
public record EffectiveAccessSummary(
    IReadOnlyList<string> OperationNames,
    DateTimeOffset ComputedAt);
```

---

## State Transitions

### EmergencyAccessEvent

```
Active ──[admin revokes]──────────────────▶ Revoked
Active ──[duration window elapses]────────▶ Expired
```

Review (`IsReviewed`) is orthogonal to status. Any event with `IsReviewed == false` can be reviewed regardless of status — including expired ones (FR-028, clarification 2026-05-07).

### Operation.IsVerified

```
false (Unverified) ──[admin confirms classification]──▶ true (Verified)
```

Confirming a classification with no change to the value (same-value reclassification) is blocked client-side (FR-009, confirm button disabled).

### Role.IsActive

```
true (Active) ──[admin deactivates, confirms warning]──▶ false (Inactive)
```

No re-activation in v1. Deactivation of a role with active assignments/relations requires the warning dialog (FR-016, FR-022).

### ChildSpecificRole.GisVedNødtilgang

```
false ──[admin activates, confirms explicit dialog]──▶ true
true  ──[admin deactivates, no dialog required]──────▶ false
```

No optimistic update — displayed state only changes after confirmed server response (FR-021).

---

## Validation Rules

| Rule | Enforcement | Requirement |
|------|------------|-------------|
| Role name not empty | Client-side — save blocked, inline error | FR-012 |
| Role name unique | Server-side — 409 response → inline error "Rollenavn er allerede i bruk" | FR-012 |
| Assignment expiry not in the past | Client-side — submit blocked, inline error | FR-026 |
| General role cannot hold ChildSpecific operation | Server-side — 400 → inline error "Kun generelle operasjoner kan legges til en generell rolle" | FR-013 |
| Child-specific role cannot hold General operation | Server-side — 400 → inline error | FR-021 pattern |
| Review note not empty | Client-side — confirm button stays disabled (FR-029) | FR-029 |
| Reclassification to identical value disabled | Client-side — confirm button disabled (FR-009) | FR-009 |
| Emergency access flag on activation requires explicit confirm | Client-side dialog required before mutation | FR-018 |
| Self-assignment blocked | Client-side — write actions disabled when selected user OID equals logged-in user OID | FR-025 |
