# Feature Specification: Access Administration Panel (Tilgangsadministrasjon)

**Feature Branch**: `005-access-admin-panel`  
**Created**: 2026-05-07  
**Status**: Draft  
**Input**: User description: "Admin panel with access control (tilgangsadministrasjon)"

---

## Overview

The Access Administration Panel gives authorised platform administrators complete control over the platform's access model. It replaces the current placeholder Admin page and introduces six dedicated screens for managing operations, roles, user assignments, emergency access, and the audit trail.

The module is access-gated: each screen is only visible and functional for administrators who hold the required access rights for that specific area.

---

## Clarifications

### Session 2026-05-07

- Q: Can an expired (naturally timed-out) emergency access event still be reviewed by an administrator? → A: Yes — expired events can be reviewed; the review action is available for any unreviewed event regardless of active/expired status. Review records are audit-logged.
- Q: When the identity directory search returns an error, what should the UI do? → A: Show an inline error message below the search field; the rest of the screen remains usable and the administrator can retry.

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Discover and navigate the admin module (Priority: P1)

An authorised administrator opens the platform and can see the Access Administration section in the navigation menu. Badge counters on two menu items give immediate visibility into pending work: unverified operations and unreviewed emergency access events. The administrator can navigate to any sub-screen they are authorised for; screens they lack access to are hidden from the menu entirely.

**Why this priority**: Navigation and access-gating is the structural foundation — all other screens depend on it. Without it, nothing else can be reached.

**Independent Test**: An administrator logs in, observes the navigation sidebar, sees the Access Administration section with badge counters, and can click through to at least one sub-screen. A user without admin access sees no admin navigation items at all.

**Acceptance Scenarios**:

1. **Given** a user with admin rights, **When** they log in, **Then** the Access Administration section appears in the navigation with the correct sub-screens visible.
2. **Given** a user with no admin rights, **When** they log in, **Then** no Access Administration navigation items are visible.
3. **Given** there are 3 unverified operations and 2 unreviewed active emergency events, **When** the administrator views the navigation, **Then** the Operation Catalogue menu item shows badge "3" and the Emergency Access item shows badge "2".
4. **Given** the administrator completes an action that reduces the unverified count, **When** the action succeeds, **Then** the badge counter updates to reflect the new count without a full page reload.

---

### User Story 2 — Review and classify platform operations (Priority: P2)

An administrator opens the Operation Catalogue and sees all registered operations grouped by service. Unverified operations are visually highlighted as a clear call to action. The administrator can filter the list by service, classification type, and status. Clicking "Classify" on an operation opens a dialog where they select the new classification and optionally add a justification. If the reclassification would affect active roles, the administrator is warned and must confirm explicitly before the change is saved. The administrator can also deactivate an operation, with a similar warning if it is currently assigned to active roles.

**Why this priority**: Operation management is the lowest-level concern in the access model — roles and assignments all depend on correct, verified operations. Keeping the catalogue accurate is the highest-priority administrative duty.

**Independent Test**: An administrator can open the catalogue, see all operations, filter them, reclassify one (including handling the affected-roles warning), and see the row update inline without page reload.

**Acceptance Scenarios**:

1. **Given** operations exist, **When** the administrator opens the catalogue, **Then** operations appear grouped by service with classification and verified status visible per row.
2. **Given** a filter is applied, **When** the list updates, **Then** only matching operations are shown; no new network request is required.
3. **Given** an operation has no affected roles, **When** the administrator confirms reclassification, **Then** the row updates immediately and no warning dialog appears.
4. **Given** an operation is assigned to active roles, **When** the administrator selects a new classification, **Then** an explicit warning listing the affected roles is shown before the change can be confirmed.
5. **Given** an administrator clicks "Deactivate" on an operation in active use, **When** the deactivation dialog opens, **Then** a warning with the affected roles is shown; deactivation is still possible but requires explicit confirmation.
6. **Given** the administrator clicks "History" on an operation, **Then** a side panel opens with a chronological list of past classification changes including timestamp, actor, and justification.
7. **Given** no operations match the current filter, **When** the filter is applied, **Then** an informative message "Ingen operasjoner matcher valgte filter" is displayed.

---

### User Story 3 — Create and manage general roles (Priority: P3)

An administrator opens the General Roles screen, sees all active roles, and can create new ones. Clicking a role opens a detail panel where they can edit the role name and description inline, assign or remove operations (only general-type operations are available), assign the role to users with an optional organisation unit scope and expiry date, and revoke existing assignments. Deactivating a role shows a warning with the count of affected users.

**Why this priority**: General roles are the primary vehicle for granting users access to platform operations. Administrators need to manage these before user-level assignments can be made.

**Independent Test**: An administrator can create a new general role, add an operation to it, assign it to a user with an org unit, revoke the assignment, and deactivate the role — all from the General Roles screen.

**Acceptance Scenarios**:

1. **Given** the administrator holds the "Create general role" right, **When** they click "Create", **Then** a form appears for name and description; submitting creates the role and it appears in the list.
2. **Given** an empty role name, **When** the administrator attempts to save, **Then** an inline validation error is shown and the save is blocked.
3. **Given** a role name already in use, **When** the administrator attempts to save, **Then** an inline error "Rollenavn er allerede i bruk" is shown.
4. **Given** a general role is open for editing, **When** the administrator tries to add a child-specific operation, **Then** an error "Kun generelle operasjoner kan legges til en generell rolle" is shown.
5. **Given** a role has active user assignments, **When** the administrator initiates deactivation, **Then** a warning with the count of affected users is shown before confirmation.
6. **Given** the administrator assigns a role to a user with an optional expiry date, **When** the assignment is saved, **Then** the assignment appears in the active assignments list with the correct validity window.
7. **Given** the administrator revokes an assignment, **When** a confirmation dialog is accepted, **Then** the assignment is removed from the list.

---

### User Story 4 — Manage child-specific roles with emergency access flag (Priority: P4)

An administrator opens the Child-Specific Roles screen and works with roles that apply to child-specific contexts. The flow mirrors the General Roles screen but adds a critical security element: the emergency access flag (`GisVedNødtilgang`). This flag is shown as a prominent badge on every role in the list. Activating the flag — which means users who trigger emergency access automatically receive all operations in this role — requires an explicit confirmation dialog explaining the consequence. The flag cannot be changed without the appropriate write permission. Deactivating a role warns about affected child relations.

**Why this priority**: Child-specific roles carry elevated risk due to the emergency access flag. Getting this right is security-critical.

**Independent Test**: An administrator with write access can toggle the emergency access flag on a child-specific role, receives an explicit warning on activation, and the list badge reflects the new state only after the confirmed mutation succeeds.

**Acceptance Scenarios**:

1. **Given** a role has the emergency access flag active, **When** viewing the role list, **Then** the role shows a prominent "Nødtilgang" badge.
2. **Given** the administrator activates the emergency access flag, **When** they click the toggle, **Then** a confirmation dialog appears explaining "Brukere som aktiverer nødtilgang vil automatisk få alle operasjonene i denne rollen" before the change is saved.
3. **Given** the confirmation dialog is open, **When** the administrator cancels, **Then** the flag state is unchanged.
4. **Given** the administrator deactivates an already-active emergency access flag, **When** they toggle it, **Then** no confirmation dialog is required (deactivation is lower risk).
5. **Given** a user lacks the "Endre barnespesifikk rolle" right, **When** they view the toggle, **Then** it is read-only and cannot be interacted with.
6. **Given** only child-specific operations exist, **When** assigning operations to this role, **Then** only child-specific operations are offered; general operations are excluded.
7. **Given** a role is used in active child relations, **When** the administrator deactivates it, **Then** a warning with the count of affected relations is shown.

---

### User Story 5 — Search users and manage their access (Priority: P5)

An administrator opens the User Access screen and searches for a platform user by name or email via a live-search field that queries the identity directory. On selecting a user, a panel shows their active general role assignments and child relations, plus a read-only summary of all effective operations they currently hold. The administrator can assign new general roles (with org unit and optional expiry), create child relations (with child-specific role and optional expiry), and revoke existing assignments or relations. Searching for themselves disables all write actions with a clear explanation.

**Why this priority**: User-level access management is the most frequently-used administrative task — it is the bridge between roles and the actual people using the platform.

**Independent Test**: An administrator can search for a user, view their assignments, add a general role assignment, and revoke it. The effective-access summary updates without manual refresh.

**Acceptance Scenarios**:

1. **Given** the administrator types a name in the search field, **When** results appear, **Then** matching users are listed with name and email; a debounce prevents excessive searches.
2. **Given** no users match the search, **When** the search completes, **Then** "Ingen brukere funnet" is shown.
3. **Given** the administrator selects a user, **When** the detail panel opens, **Then** active general assignments and child relations are listed, plus an effective-access summary.
4. **Given** the administrator searches for themselves, **When** their own profile is shown, **Then** all write actions (assign, create) are visibly disabled with the message "Du kan ikke tildele rettigheter til deg selv".
5. **Given** a new general role is assigned to a user, **When** the assignment is saved, **Then** the effective-access summary updates to reflect the additional operations.
6. **Given** an expiry date in the past is entered, **When** the administrator submits, **Then** the submission is rejected with a validation error.
7. **Given** a child relation is being created with an unknown child ID, **When** the form is submitted, **Then** an error is shown stating the child was not found.

---

### User Story 6 — Review emergency access events (Priority: P6)

An administrator opens the Emergency Access screen and is presented with a prioritised queue: unreviewed active events appear first and are visually distinguished. The badge counter in the navigation reflects only unreviewed *active* events. However, the review action is available for any unreviewed event regardless of status — an expired event that was never reviewed can still be reviewed. For each event the administrator can see the user, affected child, justification, activation time, duration, and status. Clicking "Review" opens a dialog showing full event details and requiring a mandatory review note before the review can be confirmed. Active events can also be revoked, which requires a confirmation dialog. Revocation reason is optional.

**Why this priority**: Unreviewed emergency access events represent an active security obligation. The screen is designed as a task queue requiring prompt attention.

**Independent Test**: An administrator opens the screen, reviews an event by entering a mandatory note and confirming, and the event's badge and list status update to reflect it has been reviewed.

**Acceptance Scenarios**:

1. **Given** unreviewed active events exist, **When** the administrator opens the screen, **Then** those events appear first and are visually highlighted.
2. **Given** the administrator clicks "Review", **When** the review dialog opens, **Then** all event details are shown and a note field is present.
3. **Given** the review note field is empty, **When** the administrator tries to confirm, **Then** the confirm button remains disabled and an explanation is shown.
4. **Given** a review note is entered and confirmed, **When** the mutation succeeds, **Then** the row updates inline to show "Gjennomgått" status.
5. **Given** an active event is revoked, **When** the confirmation dialog is accepted, **Then** the event status updates to "Tilbakekalt".
6. **Given** an event has expired without being reviewed, **When** the administrator opens it, **Then** the "Review" action is available and a completed review is recorded in the audit log.
7. **Given** no unreviewed events remain (of any status), **When** the screen is viewed, **Then** a positive message "Ingen ugjennomgåtte nødtilganger" is displayed.

---

### User Story 7 — Search and browse the audit log (Priority: P7)

An administrator opens the Audit Log screen and can search across all recorded mutations in the access model. Filters include actor (user who performed the action), entity type, action type, and date range. Results are paginated and each row shows timestamp, actor, action, and entity. Clicking an entry expands a detail view showing the before and after state of the entity. Links from other screens (e.g. "Show history" from the Operation Catalogue) open the Audit Log with relevant filters pre-populated.

**Why this priority**: The audit log is essential for compliance and security investigation but does not block any other workflow — it is the least urgent screen for day-to-day administration.

**Independent Test**: An administrator opens the audit log, applies a date range filter, sees paginated results, and expands a row to see the formatted before/after state.

**Acceptance Scenarios**:

1. **Given** the administrator opens the audit log, **When** the screen loads, **Then** a filter panel and paginated results table are shown.
2. **Given** a filter is applied, **When** results are fetched, **Then** only matching entries are shown.
3. **Given** results span multiple pages, **When** the administrator navigates pages, **Then** the correct subset of results is shown per page.
4. **Given** the administrator expands an audit entry, **When** the detail view opens, **Then** the before-state and after-state are displayed in a formatted, human-readable way — not as raw data.
5. **Given** the administrator follows a "Show history" link from the Operation Catalogue, **When** the Audit Log opens, **Then** entity type and entity ID filters are pre-populated with the relevant operation.
6. **Given** no results match the filter, **When** the filter is applied, **Then** "Ingen treff på filter" is displayed.

---

### Edge Cases

- What happens when no operations are registered at all? → Message "Ingen operasjoner er registrert på plattformen ennå".
- What happens when an operation's classification is changed to its current value? → The confirm button is disabled.
- What happens when an administrator tries to assign a child-specific operation to a general role? → An error message is shown and the operation is not added.
- What happens when a user's identity cannot be resolved in the directory search? → Message "Ingen brukere funnet" (no results found).
- What happens when the identity directory search itself fails (network/service error)? → An inline error is shown below the search field; the screen remains usable and the administrator can retry.
- What happens when a badge-count mutation fails? → The displayed count does not change (no optimistic updates on badge counters).
- What happens when the emergency access flag mutation fails? → The displayed toggle state does not change (no optimistic updates on this security element).

---

## Requirements *(mandatory)*

### Functional Requirements

**Navigation & Access Control**

- **FR-001**: System MUST show Access Administration navigation items only to users who hold at least one required access operation for the corresponding screen.
- **FR-002**: System MUST display a live badge counter on the Operation Catalogue menu item showing the number of unverified operations.
- **FR-003**: System MUST display a live badge counter on the Emergency Access menu item showing the number of unreviewed active emergency events.
- **FR-004**: Badge counters MUST be refreshed at login and updated after any mutation that changes the relevant count.

**Operation Catalogue**

- **FR-005**: System MUST display all registered platform operations grouped by service, with classification type, verified status, and active/inactive status visible per entry.
- **FR-006**: System MUST support client-side filtering of the operation list by service, classification type, and status without additional data fetches.
- **FR-007**: System MUST allow authorised administrators to reclassify an operation; if active roles are affected, a warning listing those roles MUST be shown before the change is applied.
- **FR-008**: System MUST allow authorised administrators to deactivate an operation; if the operation is assigned to active roles, a warning MUST be shown before the change is applied.
- **FR-009**: System MUST prevent confirming a reclassification when the new classification is identical to the current one.
- **FR-010**: System MUST show a history panel per operation with a chronological list of classification changes including timestamp, actor, and justification.

**General Roles**

- **FR-011**: System MUST allow authorised administrators to create, edit, and deactivate general roles.
- **FR-012**: System MUST prevent saving a general role with an empty name or a duplicate name.
- **FR-013**: System MUST prevent adding a child-specific operation to a general role.
- **FR-014**: System MUST allow assigning a general role to a user with an optional organisation unit scope and optional expiry date.
- **FR-015**: System MUST show a confirmation dialog before revoking an active role assignment.
- **FR-016**: System MUST show a warning with the count of affected users before deactivating a role that has active assignments.

**Child-Specific Roles**

- **FR-017**: System MUST show the emergency access flag (`GisVedNødtilgang`) status as a prominent badge on every role in the list.
- **FR-018**: System MUST require an explicit confirmation dialog when activating the emergency access flag; the dialog MUST explain that all operations in the role will be granted to any user who triggers emergency access.
- **FR-019**: System MUST NOT require a confirmation dialog when deactivating the emergency access flag.
- **FR-020**: System MUST make the emergency access flag read-only for users without the "Endre barnespesifikk rolle" right.
- **FR-021**: System MUST NOT optimistically update the emergency access flag toggle; the displayed state MUST only change after a successful server response.
- **FR-022**: System MUST show a warning with the count of affected child relations before deactivating a role in active use.

**User Access**

- **FR-023**: System MUST provide a live-search field that queries the identity directory (Microsoft Entra) with a debounce to prevent excessive requests. If the search query fails, an inline error message MUST be shown below the search field; the rest of the screen MUST remain usable and the administrator MUST be able to retry.
- **FR-024**: System MUST display, for a selected user, their active general role assignments, child relations, and a read-only effective-access summary derived from those assignments.
- **FR-025**: System MUST disable all write actions (assign role, create child relation) when the logged-in administrator searches for their own account, and display the message "Du kan ikke tildele rettigheter til deg selv".
- **FR-026**: System MUST validate that expiry dates on assignments are not in the past.
- **FR-027**: Effective-access summary MUST update automatically after any assignment change without a manual page refresh.

**Emergency Access**

- **FR-028**: System MUST default the Emergency Access screen to showing only active and unreviewed events, with unreviewed active events sorted first and visually distinguished. The review action MUST be available for any unreviewed event regardless of status (active or expired); only the revocation action is restricted to active events.
- **FR-029**: System MUST require a non-empty review note before the confirm button on the review dialog is enabled.
- **FR-030**: System MUST require a confirmation dialog before revoking an active emergency access event; revocation reason is optional.
- **FR-031**: System MUST update the badge counter and row status inline after a successful review or revocation without a full page reload.

**Audit Log**

- **FR-032**: System MUST support server-side paginated browsing of all access model mutations.
- **FR-033**: System MUST support filtering the audit log by actor, entity type, action type, and date range.
- **FR-034**: System MUST display the before-state and after-state of each audit entry in a formatted, human-readable presentation — not as raw serialised data.
- **FR-035**: System MUST support deep-linking to the Audit Log with pre-populated filters from other screens (e.g. entity type and entity ID passed via URL parameters).

**Feedback & Error Handling**

- **FR-036**: System MUST display an error message inline within any active dialog on API failure; the dialog MUST NOT close automatically on error.
- **FR-037**: System MUST show a user-friendly error state on any screen when the initial data load fails.
- **FR-038**: After any successful mutation, the system MUST display a brief, auto-dismissing success notification confirming the action was completed. This applies to all screens in the module (role created, assignment revoked, operation classified, review submitted, etc.).

**Role List Search (General and Child-Specific Roles)**

- **FR-039**: The General Roles and Child-Specific Roles list screens MUST support filtering roles by name. The filter MUST operate client-side on the already-loaded list without additional data fetches, consistent with the Operation Catalogue filter pattern (FR-006).

**User Experience Patterns (SHOULD)**

The following requirements use SHOULD rather than MUST. They represent best-practice patterns that significantly improve usability and accessibility for administrators but are not blocking for core functionality. They SHOULD be implemented from the start to avoid costly retrofits; if deferred, they MUST be tracked as explicit debt.

- **FR-040**: When a dialog or side panel opens, keyboard focus SHOULD move into it automatically. When it closes, focus SHOULD return to the element that triggered it. This applies to all dialogs and detail panels in the module.
- **FR-041**: Confirmation dialogs for destructive actions SHOULD be calibrated to the scale of impact. Low-impact actions (revoke one assignment) MAY use a standard confirm/cancel dialog. High-impact actions (deactivate a role or relation that affects a significant number of users) SHOULD display the explicit list of affected items — not just the count — so the administrator can verify the consequences before confirming.
- **FR-042**: Empty list states SHOULD include a contextual call-to-action guiding the administrator to the primary action for that screen (e.g., "Ingen roller opprettet — opprett den første rollen" with a link to the create form). Static empty messages without a CTA are a dead end for first-time administrators.
- **FR-043**: Filter state applied by the administrator on any list screen SHOULD be retained for the duration of their session. Navigating away from a screen and returning SHOULD not discard previously applied filter state.

### Key Entities

- **Operation**: A discrete platform capability belonging to a service; classified as General or Child-Specific; can be active or inactive; carries a verified/unverified flag.
- **General Role**: A named set of General operations; can be assigned to users with an optional org-unit scope and expiry; can be active or inactive.
- **Child-Specific Role**: A named set of Child-Specific operations; carries the emergency access flag (`GisVedNødtilgang`); linked to child relations rather than direct user assignments.
- **General Role Assignment**: A link between a user, a General Role, and an optional org-unit scope with optional expiry; can be active or revoked.
- **Child Relation**: A link between a user, a child (by ID), and a Child-Specific Role with optional expiry; grants the user access to operations for that specific child.
- **Emergency Access Event**: A recorded instance of a user activating emergency access for a child; carries a justification, activation time, duration, status (active/expired/revoked), and reviewed/unreviewed flag; reviewed events carry a review note and reviewer identity.
- **Audit Log Entry**: An immutable record of every mutation in the access model; includes timestamp, actor, action type, entity type, entity ID, before-state, and after-state.
- **Organisation Unit**: A node in the platform's hierarchical org structure; read-only from this module.

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Administrators can navigate to any access-gated screen in under 3 clicks from the platform home page.
- **SC-002**: Badge counters accurately reflect the current count of pending items at all times; after any relevant mutation, the counter updates within the same session without a page reload.
- **SC-003**: All inline updates (reclassification, role deactivation, review submission) reflect in the list without a full page reload.
- **SC-004**: The emergency access flag displayed state is never out of sync with the server's confirmed state; no optimistic updates result in a misleading security status.
- **SC-005**: Self-assignment is never possible through the UI; the restriction is visible before any attempt is made.
- **SC-006**: 100% of Emergency Access review submissions require a non-empty review note — the confirm action cannot be triggered without it.
- **SC-007**: Audit Log results with more than one page of entries are fully navigable; no entries are lost or duplicated across pages.
- **SC-008**: Deep-links to the Audit Log with filter parameters pre-populate the correct filters on load, allowing an administrator to see the relevant entries immediately.
- **SC-009**: All 47 specified bUnit test cases pass (12 Operation Catalogue, 9 General Roles, 7 Child-Specific Roles, 7 User Access, 7 Emergency Access, 5 Audit Log).
- **SC-010**: Every confirmed mutation results in a visible success notification; no mutation completes silently.

---

## Assumptions

- The existing authentication and identity system (Microsoft Entra / MSAL) is already in place and does not need to be built as part of this feature.
- Access rights are evaluated via the existing `IAccessRightsCache` pattern; this module extends it with new operation strings following the same convention.
- Organisation unit data is read-only within this module; creation or modification of org units is out of scope.
- The identity directory search (for the User Access screen) is performed against the existing Entra tenant; no separate user database is maintained.
- The seven service classes required for this module (Operations, General Roles, Child-Specific Roles, User Access, Emergency Access, Audit Log, Org Units) do not yet exist and must be created.
- The current Admin placeholder page (`/admin`) will be replaced by this module; the route `/admin` will be retained and serve as the entry point for the new module.
- Mobile-specific layout optimisation is out of scope for v1; the module targets desktop/tablet administrator workflows.
- Audit log entries are immutable — no editing or deletion of audit records is supported in this module.
- The deep-link mechanism for the Audit Log uses URL query parameters; the filter model handles both direct user interaction and query-parameter initialisation uniformly.
- Badge counters are fetched once at login and invalidated/updated after specific mutations; real-time push updates from the server are out of scope for v1.
