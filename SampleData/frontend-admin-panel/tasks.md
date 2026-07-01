---

description: "Task list for Access Administration Panel (Tilgangsadministrasjon)"
---

# Tasks: Access Administration Panel (Tilgangsadministrasjon)

**Input**: Design documents from `/specs/005-access-admin-panel/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅, quickstart.md ✅

**Tests**: Included — bUnit tests are mandatory per Constitution Principle V and SC-009 (47 test cases required).

**Organization**: Tasks are grouped by user story. Each story phase is independently implementable and testable.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no shared-state dependencies)
- **[Story]**: Which user story this task belongs to (US1–US7)
- Exact file paths are included in all descriptions

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Models, interfaces, and HTTP client registration — the prerequisite scaffolding that everything else imports.

- [X] T001 Create M2LB.Frontend.Web/Modules/Admin/Models/AdminModels.cs with all domain entities (Operation, OperationHistoryEntry, GeneralRole, ChildSpecificRole, OperationSummary, GeneralRoleAssignment, ChildRelation, EmergencyAccessEvent, AuditLogEntry, OrganisationUnit, AdminBadgeCounts), view models (OperationCatalogueFilter, RoleListFilter, AuditLogFilter, DirectoryUser, EffectiveAccessSummary), and enums (OperationClassification, EmergencyEventStatus) from data-model.md; also define `IRoleListItem` interface (`Guid Id`, `string Name`, `bool IsActive`, `string? Description`) and have both `GeneralRole` and `ChildSpecificRole` implement it — this is the shared contract used by `RoleList.razor`; also create M2LB.Frontend.Web/Shared/Models/PagedResult.cs with `record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)` as a module-agnostic type (not inside AdminModels.cs — pagination is a general concern for future modules)
- [X] T002 [P] Create all 7 service interfaces in M2LB.Frontend.Web/Modules/Admin/Services/: IOperationService.cs, IGeneralRoleService.cs, IChildSpecificRoleService.cs, IUserAccessService.cs, IEmergencyAccessService.cs, IAuditLogService.cs, IOrgUnitService.cs — each method signature matching the contracts in specs/005-access-admin-panel/contracts/ and returning Result\<T\>
- [X] T003 [P] Create IAdminBadgeService interface in M2LB.Frontend.Web/Shared/Services/IAdminBadgeService.cs with single method `Task<Result<AdminBadgeCounts>> GetBadgeCountsAsync(string userId)` per research.md Decision 1
- [X] T004 Register named HTTP client "AdminApi" with AutorisasjonMessageHandler (matching existing "AuthApi" pattern) in M2LB.Frontend.Web/Program.cs

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that every user story page depends on — service implementations, mocks, DI wiring, and shared UI components.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [X] T005 [P] Implement AdminBadgeService.cs in M2LB.Frontend.Web/Shared/Services/AdminBadgeService.cs calling GET /badge-counts via "AdminApi" client, returning Result\<AdminBadgeCounts\>
- [X] T006 [P] Create ConfirmDialog.razor in M2LB.Frontend.Web/Shared/Components/ConfirmDialog.razor as an inline conditional modal overlay (no Radzen DialogService) with parameters: Title, Message, ConfirmLabel, IsDestructive (bool), ErrorMessage (string?), OnConfirm (EventCallback), OnCancel (EventCallback), AffectedItems (IReadOnlyList\<string\>?), OnConfirmForce (EventCallback), TriggerRef (ElementReference?); when AffectedItems is non-empty, render the items list and swap the confirm button for "Bekreft likevel" firing OnConfirmForce — this standardises the 409 force-re-send pattern used by OperationClassifyDialog, operation deactivate, GeneralRolesPage, and ChildSpecificRolesPage (FR-007, FR-008, FR-016, FR-022); dialog stays open on error and re-enables the active button per research.md Decision 2; build focus management in from the start: call FocusAsync() on first interactive element when dialog opens, call TriggerRef.FocusAsync() when dialog closes (FR-040) — do NOT defer focus management to Phase 10; T041 verifies all dialogs follow this established pattern
- [X] T006b Create M2LB.Frontend.Web/Modules/Admin/Services/AdminServiceBase.cs as a protected abstract base class; constructor receives `HttpClient` (resolved from "AdminApi" named client) and `IAccessRightsCache` via DI; provides protected helpers `GetAsync<T>(string path)`, `PostAsync<T>(string path, object? body)`, `PutAsync<T>(string path, object? body)`, `DeleteAsync(string path)` that deserialise responses to `Result<T>` and call `IAccessRightsCache.Invalidate()` on any 403 (Constitution III MUST — enforced once here, not per-service); all T007 service implementations subclass this; **no [P] marker** — must complete before T007a–g start
- [X] T007a [P] Implement M2LB.Frontend.Web/Modules/Admin/Services/OperationService.cs per contracts/operations-api.md (GET /operations, GET /operations/{id}/history, POST /{id}/classify, POST /{id}/deactivate); subclass AdminServiceBase (T006b) — do not inject HttpClient or IAccessRightsCache directly; 403 invalidation is handled by base class helpers
- [X] T007b [P] Create M2LB.Frontend.Web/Modules/Admin/Services/RoleServiceBase.cs as a protected abstract base that subclasses AdminServiceBase (T006b) and provides shared role CRUD helpers (list, create, PUT edit, add/remove operations, deactivate) typed to the role-specific endpoint prefix; then implement GeneralRoleService.cs per contracts/roles-api.md General Roles section (GET /general-roles, POST, PUT /{id}, POST /{id}/operations, DELETE /{id}/operations/{opId}, POST /{id}/assign, DELETE /{id}/assignments/{aId}, POST /{id}/deactivate) by subclassing RoleServiceBase
- [X] T007c [P] Implement M2LB.Frontend.Web/Modules/Admin/Services/ChildSpecificRoleService.cs per contracts/roles-api.md Child-Specific Roles section by subclassing RoleServiceBase (T007b) — inherits shared CRUD; adds POST /{id}/emergency-flag and uses the child-specific endpoint prefix
- [X] T007d [P] Implement M2LB.Frontend.Web/Modules/Admin/Services/UserAccessService.cs per contracts/user-access-api.md (GET /users/search, GET /users/{id}/access, POST /users/{id}/general-role-assignments, DELETE …, POST /users/{id}/child-relations, DELETE …); subclass AdminServiceBase (T006b)
- [X] T007e [P] Implement M2LB.Frontend.Web/Modules/Admin/Services/EmergencyAccessService.cs per contracts/emergency-access-api.md (GET /emergency-access/events, POST /events/{id}/review, POST /events/{id}/revoke); subclass AdminServiceBase (T006b)
- [X] T007f [P] Implement M2LB.Frontend.Web/Modules/Admin/Services/AuditLogService.cs per contracts/audit-log-api.md (GET /audit-log with all query parameters, returns PagedResult\<AuditLogEntry\> from Shared/Models/PagedResult.cs); subclass AdminServiceBase (T006b)
- [X] T007g [P] Implement M2LB.Frontend.Web/Modules/Admin/Services/OrgUnitService.cs per contracts/user-access-api.md GET /org-units; returns Result\<IReadOnlyList\<OrganisationUnit\>\>; subclass AdminServiceBase (T006b)
- [X] T008 [P] Create mock implementations for all 7 services in M2LB.Frontend.Web/Modules/Admin/Services/Mocks/ (MockOperationService.cs, MockGeneralRoleService.cs, MockChildSpecificRoleService.cs, MockUserAccessService.cs, MockEmergencyAccessService.cs, MockAuditLogService.cs, MockOrgUnitService.cs) and MockAdminBadgeService.cs in M2LB.Frontend.Web/Shared/Services/Mocks/ — all following the pattern in M2LB.Frontend.Web/Modules/Person/Services/Mocks/MockPersonService.cs with seed data
- [X] T009 Register all 7 service interfaces (real implementations + mock implementations in `if (useMockData)` block) and IAdminBadgeService/AdminBadgeService in M2LB.Frontend.Web/Program.cs DI container; create M2LB.Frontend.Web/Shared/Services/INotificationService.cs and NotificationService.cs and register them in the **global** DI block (outside the admin section) so all current and future modules can use them — INotificationService exposes `void Show(string message, NotificationSeverity severity)` wrapping Radzen NotificationService with the settings from research.md Decision 6 (NotificationSeverity.Success, 4000ms, BottomRight)

**Checkpoint**: Foundation ready — user story phases can now begin.

---

## Phase 3: User Story 1 — Discover and Navigate the Admin Module (Priority: P1) 🎯 MVP

**Goal**: Admin navigation section with 6 access-gated sub-items and live badge counters on Operation Catalogue and Emergency Access items. Users without admin rights see nothing.

**Independent Test**: Enable `Autorisasjonstjeneste:LesOperasjonskatalog` in dev tool → admin sub-menu appears with badge counter. Disable all admin ops → no admin nav items rendered.

- [X] T010 [US1] Update M2LB.Frontend.Web/Layout/NavMenu.razor with an expandable admin sub-menu: 6 items each gated by their respective Autorisasjonstjeneste: read operation (see research.md Decision 3), badge counters on Operation Catalogue and Emergency Access items using IAdminBadgeService, refreshed at nav load and invalidated after relevant mutations (FR-001 through FR-004)
- [X] T011 [US1] Replace placeholder M2LB.Frontend.Web/Modules/Admin/Pages/AdminPage.razor (route `/admin`) with redirect logic using NavigationManager to navigate to the first accessible sub-screen based on the user's held Autorisasjonstjeneste: operations; show error state if user holds none (FR-037)
- [X] T012 [US1] Add bUnit test coverage for admin nav section in M2LB.Frontend.Tests/Layout/NavMenuTests.cs: admin items visible when ops held, hidden when no admin ops, correct badge counts shown, badge updates after mutation

**Checkpoint**: Admin navigation works end-to-end. Badge counters display. Access gating verified.

---

## Phase 4: User Story 2 — Review and Classify Platform Operations (Priority: P2)

**Goal**: Full operation catalogue: grouped list, client-side filters with URL persistence, classify dialog with 409 affected-roles warning, deactivate dialog, history panel, inline row updates.

**Independent Test**: Open `/admin/operations`, see all operations grouped by service, filter by classification, classify one operation (trigger and accept the affected-roles 409 warning), view history panel.

- [X] T013 [P] [US2] Create M2LB.Frontend.Web/Modules/Admin/Components/OperationFilterBar.razor with service, classification, and status filter controls; emit `EventCallback<OperationCatalogueFilter> OnFilterChanged` on any control change; the component owns no navigation state and has no `NavigationManager` injection — the parent page is responsible for URL updates (FR-006, FR-043, Constitution IV)
- [X] T014 [P] [US2] Create M2LB.Frontend.Web/Modules/Admin/Components/OperationHistoryPanel.razor as a pure presenter slide-in panel; parameters: `PanelResult<IReadOnlyList<OperationHistoryEntry>> State` (reuse existing `PanelStatus.Loading/Success/Empty/Error` from Shared/Models/PanelModels.cs — one parameter replaces the separate `bool IsLoading` + `IReadOnlyList<OperationHistoryEntry> Entries` pair), `EventCallback OnClose`; renders timestamp, actor, previous/new classification, and justification per entry; no service injection, no API calls (FR-010, Constitution IV)
- [X] T015 [P] [US2] Create M2LB.Frontend.Web/Modules/Admin/Components/OperationClassifyDialog.razor as inline conditional overlay; confirm button disabled when selected classification equals current value (FR-009); handles 409 AFFECTED_ROLES response by populating ConfirmDialog.AffectedItems with affected role names and wiring OnConfirmForce to re-send with `force: true` (FR-007 — uses T006 ConfirmDialog extension, no bespoke list rendering needed); shows ErrorMessage inline on other API errors (FR-036); implement TriggerRef + FocusAsync focus management following the ConfirmDialog pattern (FR-040)
- [X] T016 [US2] Create M2LB.Frontend.Web/Modules/Admin/Components/OperationList.razor rendering operations grouped by ServiceName; unverified operations visually highlighted; each row has Classify, Deactivate, and History buttons; Deactivate uses ConfirmDialog with AffectedItems populated on 409 AFFECTED_ROLES and OnConfirmForce wired to re-send with `force: true` (FR-008 — same pattern as OperationClassifyDialog; no separate dialog component needed)
- [X] T017 [US2] Create M2LB.Frontend.Web/Modules/Admin/Pages/OperationCataloguePage.razor (route `/admin/operations`); loads all operations once via `IOperationService.GetOperationsAsync()`; reads filter URL params via `[SupplyParameterFromQuery]`; on `OnFilterChanged` callback from `OperationFilterBar`, calls `NavigationManager.NavigateTo` with updated query string and `replace: true` (FR-043); on History panel open, calls `IOperationService.GetHistoryAsync(operationId)` and passes the result to `OperationHistoryPanel.Entries` — manages `_historyLoading` bool while in flight (FR-010); shows `AuthorizationError` on initial load failure (FR-037); refreshes badge counts via `IAdminBadgeService` after successful classify or deactivate (FR-004); shows success notification via `NotificationService` after confirmed mutations (FR-038)
- [X] T018 [US2] Write M2LB.Frontend.Tests/Modules/Admin/OperationCataloguePageTests.cs with all 12 bUnit test cases from SC-009: operations grouped and displayed, filters work client-side, classify with no affected roles updates inline, classify 409 shows warning, deactivate 409 shows warning, history panel opens, empty-filter message shown, error state on load failure, badge refreshed after classify, success notification shown

**Checkpoint**: Operation Catalogue fully functional and independently testable.

---

## Phase 5: User Story 3 — Create and Manage General Roles (Priority: P3)

**Goal**: General roles screen with create/edit/deactivate, operation management (general-only), and user assignments with org unit + expiry.

**Independent Test**: Create role, add general operation (reject child-specific with error), assign to user with expiry, revoke assignment, deactivate role with affected-users warning.

- [X] T019 [P] [US3] Create M2LB.Frontend.Web/Modules/Admin/Components/RoleList.razor as a shared role list presenter typed to `IReadOnlyList<IRoleListItem> Roles`; client-side name filter on the loaded list (FR-039); parameters: `Roles`, `EventCallback<IRoleListItem> OnRoleSelected`, `RenderFragment<IRoleListItem>? ExtraRowContent` (nullable template slot — `ChildSpecificRolesPage` passes the `EmergencyAccessFlagBadge` here; `GeneralRolesPage` leaves it null); no service injection (FR-039, Constitution IV)
- [X] T020 [P] [US3] Create M2LB.Frontend.Web/Modules/Admin/Components/AssignRoleDialog.razor as inline dialog for assigning a role to a user; parameters include `IReadOnlyList<OrganisationUnit> OrgUnits` (passed by parent page — do **NOT** inject IOrgUnitService directly, Constitution IV); org unit select bound to OrgUnits parameter; optional expiry date with client-side past-date validation (FR-026); self-assignment check: disable submit and show "Du kan ikke tildele rettigheter til deg selv" when selected userId equals logged-in OID (FR-025); implement TriggerRef + FocusAsync focus management following the ConfirmDialog pattern (FR-040)
- [X] T021 [US3] Create M2LB.Frontend.Web/Modules/Admin/Components/RoleDetailPanel.razor showing edit name/description inline, operations list with add (filtered to correct classification) and remove, assignments list with Revoke button (opens ConfirmDialog per FR-015); 409 DUPLICATE_NAME shows "Rollenavn er allerede i bruk"; 400 WRONG_CLASSIFICATION shows "Kun generelle operasjoner kan legges til en generell rolle" (FR-013)
- [X] T022 [US3] Create M2LB.Frontend.Web/Modules/Admin/Pages/GeneralRolesPage.razor (route `/admin/general-roles`); loads roles via IGeneralRoleService and org units once via IOrgUnitService on init (to pass as parameter to AssignRoleDialog per Constitution IV); create form for new role with empty-name blocking (FR-012); RoleList + RoleDetailPanel side-by-side; 409 on deactivate: populate ConfirmDialog.AffectedItems with affected user names and wire OnConfirmForce to re-send with `force: true` (FR-016 — uses T006 ConfirmDialog extension); success notifications (FR-038)
- [X] T023 [US3] Write M2LB.Frontend.Tests/Modules/Admin/GeneralRolesPageTests.cs with all 9 bUnit test cases from SC-009: role list shown, create role and appears in list, empty name blocked, duplicate name error shown, child-specific operation rejected, deactivate 409 shows user count warning, assignment saved and listed, assignment revoked and removed, role with no assignments deactivates directly

**Checkpoint**: General Roles fully functional and independently testable.

---

## Phase 6: User Story 4 — Manage Child-Specific Roles with Emergency Access Flag (Priority: P4)

**Goal**: Child-specific roles screen mirroring General Roles but with the GisVedNødtilgang toggle — activation requires explicit confirmation dialog; no optimistic updates.

**Independent Test**: View role with active emergency flag (prominent badge), activate flag (confirmation dialog required), cancel keeps state unchanged, deactivate without dialog; read-only when lacking EndreBarnespesifikkRolle.

- [X] T024 [P] [US4] Create M2LB.Frontend.Web/Modules/Admin/Components/EmergencyAccessFlagBadge.razor as a prominent badge + toggle for the GisVedNødtilgang flag; activation opens an inline confirmation dialog with text "Brukere som aktiverer nødtilgang vil automatisk få alle operasjonene i denne rollen" before calling POST /child-specific-roles/{id}/emergency-flag (FR-018); deactivation calls the endpoint directly with no dialog (FR-019); displayed state only changes after successful server response (FR-021); renders read-only when user lacks EndreBarnespesifikkRolle (FR-020)
- [X] T025 [US4] Create M2LB.Frontend.Web/Modules/Admin/Pages/ChildSpecificRolesPage.razor (route `/admin/child-specific-roles`); reuses RoleList and RoleDetailPanel components from US3 with child-specific service; EmergencyAccessFlagBadge on each list row; child-specific operations only in role detail (400 WRONG_CLASSIFICATION error message for general ops); 409 on deactivate: populate ConfirmDialog.AffectedItems with affected relation names and wire OnConfirmForce to re-send with `force: true` (FR-022 — uses T006 ConfirmDialog extension)
- [X] T026 [US4] Write M2LB.Frontend.Tests/Modules/Admin/ChildSpecificRolesPageTests.cs with all 7 bUnit test cases from SC-009: flag badge visible on active-flag role, activate shows confirmation dialog, cancel leaves flag unchanged, deactivate goes directly, read-only without write op, general operation rejected, deactivate 409 shows relation count warning

**Checkpoint**: Child-Specific Roles fully functional including security-critical flag behaviour.

---

## Phase 7: User Story 5 — Search Users and Manage Their Access (Priority: P5)

**Goal**: Live-search users (debounced), view assignments and effective access, assign general roles, create child relations, revoke. Self-assignment disabled.

**Independent Test**: Search user by name, see assignments + effective access summary, assign general role with expiry, revoke it. Search own account — write actions disabled.

- [X] T027 [P] [US5] Create M2LB.Frontend.Web/Modules/Admin/Components/UserSearchPanel.razor with CancellationToken-based 300ms debounce (research.md Decision 5); loading indicator during request; shows "Ingen brukere funnet" on empty results; shows inline error below search field on service error — rest of screen remains usable (FR-023)
- [X] T028 [P] [US5] Create M2LB.Frontend.Web/Modules/Admin/Components/UserAccessDetail.razor showing active general role assignments, child relations, and read-only effective-access summary for a selected DirectoryUser; parameters include `PanelResult<EffectiveAccessSummary> EffectiveAccess` (from existing Shared/Models/PanelModels.cs — one parameter for load/error/ready state) and `IReadOnlyList<OrganisationUnit> OrgUnits` (passed from parent page to forward to AssignRoleDialog, Constitution IV); Assign General Role button opens AssignRoleDialog; Create Child Relation inline form; revoke buttons on each assignment/relation open ConfirmDialog; self-assignment check disables all write actions and shows "Du kan ikke tildele rettigheter til deg selv" (FR-025); effective-access summary refreshes after each mutation (FR-027)
- [X] T029 [US5] Create M2LB.Frontend.Web/Modules/Admin/Pages/UserAccessPage.razor (route `/admin/user-access`); UserSearchPanel left pane + UserAccessDetail right pane; loads org units once via IOrgUnitService on init and passes as `OrgUnits` parameter to UserAccessDetail (which forwards to AssignRoleDialog — Constitution IV parameter chain); 400 CHILD_NOT_FOUND shows "Barnet ble ikke funnet"; 400 PAST_EXPIRY shows validation error (FR-026); success notifications (FR-038)
- [X] T030 [US5] Write M2LB.Frontend.Tests/Modules/Admin/UserAccessPageTests.cs with all 7 bUnit test cases from SC-009: search shows matching users, empty search shows message, select user shows assignments and effective access, self-search disables write actions with message, assignment saved and effective access updates, past-expiry rejected, child-not-found error shown

**Checkpoint**: User Access fully functional. Self-assignment protection verified.

---

## Phase 8: User Story 6 — Review Emergency Access Events (Priority: P6)

**Goal**: Prioritised event queue — unreviewed active events first and highlighted. Review requires mandatory note. Revoke requires confirmation. Inline updates with badge decrement.

**Independent Test**: Open `/admin/emergency-access`, confirm unreviewed active events shown first, review an event with a note, row updates to "Gjennomgått", badge decrements. Revoke active event with optional reason. Review expired event.

- [X] T031 [P] [US6] Create M2LB.Frontend.Web/Modules/Admin/Components/EmergencyReviewDialog.razor as inline dialog showing full event details; review note textarea; confirm button disabled until note is non-empty (FR-029); stays open on API error showing inline ErrorMessage (FR-036)
- [X] T032 [P] [US6] Create M2LB.Frontend.Web/Modules/Admin/Components/EmergencyRevokeDialog.razor as inline confirmation dialog; optional reason field; confirm triggers POST /emergency-access/events/{id}/revoke; stays open on API error (FR-036)
- [X] T033 [US6] Create M2LB.Frontend.Web/Modules/Admin/Components/EmergencyEventList.razor as a pure presenter; parameter `PanelResult<IReadOnlyList<EmergencyAccessEvent>> State` (from existing Shared/Models/PanelModels.cs — replaces bare `IReadOnlyList<>` + separate loading bool); renders sorted event rows (unreviewed active first, then by activatedAt descending per FR-028); unreviewed active rows visually distinguished; Review button on any unreviewed event (active or expired); Revoke button only on Active events; emits callbacks to parent for confirmed mutations
- [X] T034 [US6] Create M2LB.Frontend.Web/Modules/Admin/Pages/EmergencyAccessPage.razor (route `/admin/emergency-access`); loads events via IEmergencyAccessService; EmergencyEventList + dialogs; after successful review/revoke: update row inline without page reload, refresh badge counts via IAdminBadgeService (FR-031); show success notification (FR-038); "Ingen ugjennomgåtte nødtilganger" when all events reviewed (FR-028)
- [X] T035 [US6] Write M2LB.Frontend.Tests/Modules/Admin/EmergencyAccessPageTests.cs with all 7 bUnit test cases from SC-009: unreviewed active events shown first and highlighted, review dialog opens on click, confirm disabled without note, review success updates row inline, revoke success updates row to Tilbakekalt, expired event review available, no-events message shown

**Checkpoint**: Emergency Access fully functional. Security obligation queue works correctly.

---

## Phase 9: User Story 7 — Search and Browse the Audit Log (Priority: P7)

**Goal**: Paginated, filterable audit log with formatted before/after state, deep-link support from other screens.

**Independent Test**: Open `/admin/audit-log`, apply date range filter, paginate, expand row to see formatted before/after. Follow `/admin/audit-log?entityType=Operation&entityId=...` link — filters pre-populated.

- [X] T036 [P] [US7] Create M2LB.Frontend.Web/Modules/Admin/Components/AuditLogFilterPanel.razor with actorId (Guid? input), entityType (dropdown from fixed list in audit-log-api.md contract), entityId (Guid?), from/to date range inputs; filter changes call parent callback; reads initial state from [SupplyParameterFromQuery] params passed from parent (FR-035, research.md Decision 8)
- [X] T037 [P] [US7] Create M2LB.Frontend.Web/Modules/Admin/Components/AuditEntryDetailPanel.razor rendering before/after JsonElement state as formatted key-value pairs (not raw JSON text) — use recursive rendering for nested objects (FR-034)
- [X] T038 [US7] Create M2LB.Frontend.Web/Modules/Admin/Components/AuditLogTable.razor as paginated table; each row shows timestamp, actor, action type, entity display name; click to expand inline AuditEntryDetailPanel; pagination controls calling parent for page change
- [X] T039 [US7] Create M2LB.Frontend.Web/Modules/Admin/Pages/AuditLogPage.razor (route `/admin/audit-log`); reads [SupplyParameterFromQuery] actorId, entityType, entityId, from, to on OnParametersSetAsync and pre-populates AuditLogFilterPanel before initial fetch (FR-035, SC-008); server-side pagination via IAuditLogService (FR-032); "Ingen treff på filter" on empty results; error state on load failure (FR-037); filter changes push to URL with replace: true (FR-043)
- [X] T040 [US7] Write M2LB.Frontend.Tests/Modules/Admin/AuditLogPageTests.cs with all 5 bUnit test cases from SC-009: filter panel and table shown on load, filter applied shows matching results, pagination navigates correctly, expand row shows formatted before/after state, deep-link URL params pre-populate filters on load

**Checkpoint**: All 7 user stories independently functional. 47 bUnit tests written.

---

## Phase 10: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that apply across multiple user stories — focus management, empty state CTAs, and final validation.

- [X] T041 [P] Verify focus management is consistently implemented across all dialogs (FR-040): ConfirmDialog (T006) establishes the canonical pattern (TriggerRef parameter, FocusAsync on open, TriggerRef.FocusAsync on close); check that OperationClassifyDialog (T015), EmergencyReviewDialog (T031), EmergencyRevokeDialog (T032), and AssignRoleDialog (T020) all follow it — fix any gaps; this is a verification pass, not a retrofit
- [X] T042 Add empty state CTAs to all list screens (FR-042): GeneralRolesPage "Ingen roller opprettet — opprett den første rollen" with link to create form; ChildSpecificRolesPage similar; OperationCataloguePage "Ingen operasjoner er registrert på plattformen ennå"; EmergencyAccessPage "Ingen ugjennomgåtte nødtilganger"
- [X] T043 [P] Verify filter URL persistence works end-to-end for all screens with filters (FR-043): OperationCataloguePage, AuditLogPage — navigate away and back; confirm [SupplyParameterFromQuery] restores filter state correctly
- [X] T044 [P] Run quickstart.md constitution compliance checklist: verify no direct HttpClient in components, Result\<T\> on all service calls, GisVedNødtilgang no optimistic updates, no PII in URLs, all dialogs stay open on error
- [X] T045 Run `dotnet test M2LB.Frontend.slnx` and confirm all 47 bUnit test cases pass (SC-009); note that NavMenu admin tests from T012 are additional — 47 is the SC-009 floor, not a ceiling
- [X] T046 [P] Verify FR-041 threshold behaviour: ConfirmDialog.AffectedItems (T006) already renders the item list when provided; confirm that GeneralRolesPage (T022) and ChildSpecificRolesPage (T025) populate AffectedItems with names (not just a count integer) when the affected set is non-trivial — the 409 response body must include the names list, not just the count
- [X] T047 [P] WCAG 2.1 AA accessibility review across all 6 admin screens: verify ARIA landmark roles on all panels and dialogs, colour contrast on badge and unverified-row highlight styles, full keyboard navigation in all dialogs and side panels (tab order, Escape to close, Enter to confirm)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — start immediately. T002 and T003 can run in parallel with T001 once AdminModels.cs exists.
- **Foundational (Phase 2)**: Depends on Phase 1 (interfaces must exist). T005, T006, T006b, T008 can all start in parallel; T006b (AdminServiceBase) must complete before T007a–T007g start; T007a–T007g each depend on interfaces from T002/T003 and on T006b, but are parallel with each other; T009 depends on T005–T008.
- **User Stories (Phases 3–9)**: All depend on Foundational phase (Phase 2) completion.
  - US1 (Phase 3) should be completed before other stories (it provides the navigation shell), but is not a hard code dependency.
  - US2–US7 (Phases 4–9) can proceed in parallel once Phase 2 is complete.
- **Polish (Phase 10)**: Depends on all desired user story phases being complete.

### User Story Dependencies

- **US1 (P1)**: Depends on Foundational only — NavMenu update + AdminPage redirect
- **US2 (P2)**: Depends on Foundational only — no dependency on US1 code at runtime
- **US3 (P3)**: Depends on Foundational only — RoleList is created here and reused by US4
- **US4 (P4)**: Depends on US3 (reuses RoleList and RoleDetailPanel) — implement US3 first
- **US5 (P5)**: Depends on Foundational only — reuses AssignRoleDialog from US3 if desired, but can be written independently
- **US6 (P6)**: Depends on Foundational only
- **US7 (P7)**: Depends on Foundational only

### Within Each User Story

- Models and service interfaces come from Phase 1 (already done)
- Components (marked [P]) before pages that compose them
- Pages before their test files
- Each story complete and tests passing before moving to next priority

### Parallel Opportunities

- T002 and T003 run in parallel (different interface files)
- T005, T006, T006b, T008 run in parallel within Phase 2; T006b must finish before T007a–T007g start; T007a–T007g are parallel with each other
- Once Phase 2 is complete: US2–US7 can all be started in parallel by different developers
- Within each story: components marked [P] (T013/T014/T015, T019/T020, T024, T027/T028, T031/T032, T036/T037) can run in parallel
- T041, T043, T044, T046, T047 run in parallel in the polish phase

---

## Parallel Example: User Story 2 (Operation Catalogue)

```bash
# Launch all parallelizable components at once:
Task T013: Create OperationFilterBar.razor (filter controls + URL params)
Task T014: Create OperationHistoryPanel.razor (history slide-in)
Task T015: Create OperationClassifyDialog.razor (classify dialog + 409 handling)

# Then sequentially:
Task T016: Create OperationList.razor (depends on dialog components)
Task T017: Create OperationCataloguePage.razor (composes all components)
Task T018: Write OperationCataloguePageTests.cs
```

## Parallel Example: User Story 1 (Navigation)

```bash
# T010 and T011 can run in parallel:
Task T010: Update NavMenu.razor (admin sub-menu + badges)
Task T011: Replace AdminPage.razor (redirect logic)

# Then:
Task T012: Write NavMenuTests.cs (admin section bUnit tests)
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks all stories)
3. Complete Phase 3: User Story 1 (navigation + access gating)
4. **STOP and VALIDATE**: Admin nav visible with correct access gating and badge counters
5. Navigate to each sub-screen URL directly to confirm routing works

### Incremental Delivery (Priority Order)

1. Complete Setup + Foundational → foundation ready
2. Add US1 (navigation) → admin shell works
3. Add US2 (operations) → first substantive admin screen
4. Add US3 (general roles) → role management works
5. Add US4 (child-specific roles) → security-critical flag management added
6. Add US5 (user access) → user-level management works
7. Add US6 (emergency access) → security review queue works
8. Add US7 (audit log) → compliance trail browsable
9. Apply Polish (Phase 10) → focus management, empty states, 47 tests passing

### Parallel Team Strategy (if multiple developers)

1. Complete Phases 1–2 together (shared scaffolding)
2. Dev A: US1 + US2 (navigation + operations)
3. Dev B: US3 + US4 (general + child-specific roles) — note US4 reuses US3 components
4. Dev C: US5 + US6 (user access + emergency access)
5. Dev D: US7 (audit log)
6. All: Polish phase (Phase 10)

---

## Notes

- bUnit test files use `BunitContext` and `Render<T>()` — **not** `TestContext`/`RenderComponent` (CLAUDE.md)
- Auth in bUnit: `AddAuthorization().SetAuthorized(userId).SetClaims(new Claim("oid", userId))`
- Each test method should carry a comment `// T[NNN]` matching the test case number from SC-009 per quickstart.md
- GisVedNødtilgang toggle: zero tolerance for optimistic updates — always wait for `200 OK` before rendering new state
- Dialog focus management (FR-040): store trigger `ElementRef`, call `ElementRef.FocusAsync()` on open and on close
- Success notifications: `NotificationService` with `NotificationSeverity.Success`, 4000ms, `NotificationPosition.BottomRight` (research.md Decision 6)
- Norwegian UI text throughout; all code, comments, identifiers in English (constitution §Language)
- Filter state via `[SupplyParameterFromQuery]` + `NavigationManager.NavigateTo(..., replace: true)` — no scoped services for filter state
