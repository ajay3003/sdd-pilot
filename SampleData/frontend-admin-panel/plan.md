# Implementation Plan: Access Administration Panel (Tilgangsadministrasjon)

**Branch**: `005-access-admin-panel` | **Date**: 2026-05-08 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/005-access-admin-panel/spec.md`

## Summary

Replaces the placeholder Admin page (`/admin`) with a full access administration module comprising six screens: Operation Catalogue, General Roles, Child-Specific Roles, User Access, Emergency Access, and Audit Log. All screens are access-gated via new operation strings under the existing `Autorisasjonstjeneste:` prefix. The module extends `NavMenu.razor` with an expandable admin section and live badge counters (unverified operations, unreviewed active emergency events). Implementation follows the existing Blazor WASM patterns: page components orchestrate, typed service classes encapsulate API calls, `Result<T>` for all results, and bUnit tests for all access-gated components.

## Technical Context

**Language/Version**: C# 13 / .NET 10 LTS  
**Primary Dependencies**: Blazor WebAssembly standalone, Radzen.Blazor (Material theme), Microsoft.Authentication.WebAssembly.Msal, bUnit 2.6.2 + xUnit  
**Storage**: N/A (frontend-only; all state via API calls)  
**Testing**: bUnit 2.6.2 + xUnit — `BunitContext` and `Render<T>()` per CLAUDE.md  
**Target Platform**: WASM (desktop/tablet browser; mobile layout deferred to v2 per spec)  
**Project Type**: Blazor WebAssembly SPA — new module within existing frontend  
**Performance Goals**: Badge counter refresh after relevant mutations within the same session (no real-time push); inline row updates after mutations (no full page reload)  
**Constraints**: WCAG 2.1 AA accessibility; fail-closed on API error; no optimistic updates on emergency flag or badge counters (FR-021, SC-004); mobile layout out of scope for v1  
**Scale/Scope**: 1 module, 6 screens, 7 service interfaces, 47 specified bUnit test cases (SC-009)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Compliance | Notes |
|-----------|-----------|-------|
| I. Headless API Communication | ✅ PASS | All 7 service classes return `Result<T>`, no direct `HttpClient` in components |
| II. Authentication and Identity | ✅ PASS | MSAL already in place; `userId` from `oid` claim; no new auth mechanism required |
| III. Access-Based Navigation | ✅ PASS | 13 new `Autorisasjonstjeneste:` operation strings gate each sub-screen; fail-closed per FR-037 |
| IV. Component Design | ✅ PASS | Pages in `Pages/`, reusable pieces in `Components/`, shared dialogs in `Shared/Components/` |
| V. Testing is Mandatory | ✅ PASS | 47 bUnit tests in SC-009; all dialog, access-gating, and error scenarios must be covered |
| VI. Security in the Presentation Layer | ✅ PASS | All URL params use UUIDs; no PII in URLs, page titles, or browser history; child identity never in nav state |

**Gate result: PASS. No violations. Proceed to Phase 0.**

*Post-design re-check: See research.md Decision 3 — operation string design is the most security-critical decision. Reviewed and confirmed compliant.*

## Project Structure

### Documentation (this feature)

```text
specs/005-access-admin-panel/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── contracts/           # Phase 1 output (/speckit-plan command)
│   ├── operations-api.md
│   ├── roles-api.md
│   ├── user-access-api.md
│   ├── emergency-access-api.md
│   └── audit-log-api.md
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code

```text
M2LB.Frontend.Web/
├── Layout/
│   └── NavMenu.razor                               # Update: admin sub-menu + badge counters
├── Modules/
│   └── Admin/
│       ├── Pages/
│       │   ├── AdminPage.razor                     # Replace placeholder; /admin → redirect to first accessible sub-screen
│       │   ├── OperationCataloguePage.razor        # Route /admin/operations
│       │   ├── GeneralRolesPage.razor              # Route /admin/general-roles
│       │   ├── ChildSpecificRolesPage.razor        # Route /admin/child-specific-roles
│       │   ├── UserAccessPage.razor                # Route /admin/user-access
│       │   ├── EmergencyAccessPage.razor           # Route /admin/emergency-access
│       │   └── AuditLogPage.razor                  # Route /admin/audit-log
│       ├── Components/
│       │   ├── OperationList.razor                 # Grouped operation rows + filter
│       │   ├── OperationFilterBar.razor            # Service/classification/status filter controls
│       │   ├── OperationClassifyDialog.razor       # Inline classify/reclassify dialog
│       │   ├── OperationHistoryPanel.razor         # Slide-in history list
│       │   ├── RoleList.razor                      # Role rows with name filter (shared for general + child-specific)
│       │   ├── RoleDetailPanel.razor               # Edit name/desc, operations, assignments
│       │   ├── AssignRoleDialog.razor              # Assign role to user (org unit + expiry)
│       │   ├── EmergencyAccessFlagBadge.razor      # GisVedNødtilgang badge + toggle
│       │   ├── UserSearchPanel.razor               # Debounced Entra search
│       │   ├── UserAccessDetail.razor              # Assignments + effective access summary
│       │   ├── EmergencyEventList.razor            # Sorted event rows
│       │   ├── EmergencyReviewDialog.razor         # Review dialog with mandatory note
│       │   ├── EmergencyRevokeDialog.razor         # Revoke dialog with optional reason
│       │   ├── AuditLogTable.razor                 # Paginated table with expand
│       │   ├── AuditLogFilterPanel.razor           # Filter controls
│       │   └── AuditEntryDetailPanel.razor         # Before/after state formatted view
│       ├── Services/
│       │   ├── IOperationService.cs / OperationService.cs
│       │   ├── IGeneralRoleService.cs / GeneralRoleService.cs
│       │   ├── IChildSpecificRoleService.cs / ChildSpecificRoleService.cs
│       │   ├── IUserAccessService.cs / UserAccessService.cs
│       │   ├── IEmergencyAccessService.cs / EmergencyAccessService.cs
│       │   ├── IAuditLogService.cs / AuditLogService.cs
│       │   └── IOrgUnitService.cs / OrgUnitService.cs
│       └── Models/
│           └── AdminModels.cs                      # DTOs and view models for the Admin module
├── Shared/
│   ├── Components/
│   │   └── ConfirmDialog.razor                     # Reusable inline confirm dialog (no DialogService)
│   └── Services/
│       ├── IAdminBadgeService.cs                   # Badge counter fetcher
│       └── AdminBadgeService.cs
└── Program.cs                                      # Register 7 new service interfaces

M2LB.Frontend.Tests/
└── Modules/
    └── Admin/
        ├── OperationCataloguePageTests.cs          # 12 test cases (SC-009)
        ├── GeneralRolesPageTests.cs                # 9 test cases
        ├── ChildSpecificRolesPageTests.cs          # 7 test cases
        ├── UserAccessPageTests.cs                  # 7 test cases
        ├── EmergencyAccessPageTests.cs             # 7 test cases
        └── AuditLogPageTests.cs                    # 5 test cases
```

**Structure Decision**: Single module under `Modules/Admin/` following the established module convention. A shared `ConfirmDialog` is placed in `Shared/Components/` as it is used by all six screens. `IAdminBadgeService` is in `Shared/Services/` because `NavMenu` must access it alongside the page components.

## Complexity Tracking

> No constitution violations — section not required.
