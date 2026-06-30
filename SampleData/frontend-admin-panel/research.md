# Research: Access Administration Panel

**Feature branch**: `005-access-admin-panel`
**Phase**: 0 — Research
**Date**: 2026-05-08

All decisions below resolve items that were marked NEEDS CLARIFICATION in the technical context, or that required evaluation of alternatives before the design phase could proceed.

---

## Decision 1 — Badge Counter Service Design

**Decision**: Introduce a new `IAdminBadgeService` singleton that exposes a single async method:

```csharp
Task<Result<AdminBadgeCounts>> GetBadgeCountsAsync(string userId);
```

`AdminBadgeCounts` is a record with `UnverifiedOperationCount` (int) and `UnreviewedActiveEmergencyEventCount` (int). The service is registered in DI and injected into both `NavMenu.razor` and the admin page components. `NavMenu` holds badge counts as `int?` fields (null = loading/error); a null badge renders as absent — never as "0" — preventing false reassurance to administrators.

The service is called once at initial nav load and after any mutation that could change the relevant count (reclassification confirms an unverified operation → decrement; review or revocation changes emergency event state → decrement).

**Rationale**: Badge counters are content counts derived from API queries — they are not access rights and do not belong in `IAccessRightsCache`. A dedicated service keeps the access rights cache clean and allows the badge state to update after mutations with a different TTL than access rights.

**Alternatives considered**:
- Extend `IAccessRightsCache` with badge counts — rejected: mixes authorization semantics with content counts; the cache's 5-minute TTL is wrong for badge freshness (badges must update after mutations within the same session, not on a fixed interval).
- Fetch badge counts directly inside `NavMenu` — rejected: `NavMenu` already handles auth state and access rights; adding API calls makes it a hybrid orchestrator, harder to test, and harder to invalidate from page components.

---

## Decision 2 — Dialog Implementation Pattern

**Decision**: Implement confirmation and action dialogs as **inline conditional components** using `@if (_showDialog)` flags, rendered as styled modal overlays within the parent component. No Radzen `DialogService`. Two categories:

1. **Shared `ConfirmDialog.razor`** in `Shared/Components/` — covers low-complexity destructive confirmations. Parameters: `Title`, `Message`, `ConfirmLabel`, `IsDestructive` (controls button color), `ErrorMessage` (string? — shown inline inside the dialog on mutation failure), `OnConfirm` (EventCallback), `OnCancel` (EventCallback). Dialog stays open on error; confirm button re-enables to allow retry.

2. **Module-local dialog components** in `Modules/Admin/Components/` — for high-complexity interactions: `OperationClassifyDialog`, `EmergencyReviewDialog`, `EmergencyRevokeDialog`, `AssignRoleDialog`. These follow the same inline pattern but carry screen-specific form fields.

Focus management (FR-040): when a dialog opens, call `ElementRef.FocusAsync()` on the first interactive element inside it. When it closes, return focus to the trigger element via a stored `ElementRef`.

**Rationale**: The existing codebase removed `DialogService` from at least one component (`ReferralSection.razor`). Inline dialog components are straightforward to test in bUnit (check `Markup` for dialog DOM presence without any service mock). They comply with Constitution Principle IV (components receive state via parameters). Radzen `DialogService` requires JS interop setup in bUnit tests and introduces a second DI channel for dialog state.

**Alternatives considered**:
- Radzen `DialogService` — rejected: requires JS interop mock in bUnit tests; has been removed from at least one component in this codebase; introduces global singleton state that makes testing non-trivial.
- `DynamicComponent` for dialog composition — over-engineered for this use case; inline `@if` is clearer and equally maintainable.

---

## Decision 3 — Operation Strings for the Admin Module

**Decision**: Define 13 operation strings under the `Autorisasjonstjeneste:` prefix. Screen visibility requires holding at least the read operation for that screen (FR-001). Write operations gate specific mutation actions within a screen.

| Screen | Read operation (required to show screen) | Write operations |
|--------|----------------------------------------|-----------------|
| Operation Catalogue | `Autorisasjonstjeneste:LesOperasjonskatalog` | `Autorisasjonstjeneste:KlassifiserOperasjon`, `Autorisasjonstjeneste:DeaktiverOperasjon` |
| General Roles | `Autorisasjonstjeneste:LesGenerelleRoller` | `Autorisasjonstjeneste:OpprettGenerellRolle`, `Autorisasjonstjeneste:EndreGenerellRolle` |
| Child-Specific Roles | `Autorisasjonstjeneste:LesBarnespesifikkeRoller` | `Autorisasjonstjeneste:EndreBarnespesifikkRolle` |
| User Access | `Autorisasjonstjeneste:LesBrukertilgang` | `Autorisasjonstjeneste:TildelBrukertilgang` |
| Emergency Access | `Autorisasjonstjeneste:LesNødtilgang` | `Autorisasjonstjeneste:GjennomgåNødtilgang`, `Autorisasjonstjeneste:TilbakekallNødtilgang` |
| Audit Log | `Autorisasjonstjeneste:LesRevisjonslogg` | (read-only screen — no write operations) |

The existing `NavMenu` check (`op.StartsWith("Autorisasjonstjeneste:", ...)`) continues to control top-level admin section visibility. Per-sub-screen visibility uses specific read operation checks in the expanded nav menu items.

**Rationale**: Fine-grained operations allow partial admin access (an admin with only emergency review rights sees only that screen). This is consistent with the existing `"ServiceName:OperationName"` convention and the spec requirement for per-screen visibility (FR-001). Norwegian identifiers follow the domain specification convention (constitution §Language).

**Alternatives considered**:
- Single `Autorisasjonstjeneste:Admin` operation gating all screens — rejected: too coarse; spec requires per-screen visibility control.
- English operation names — rejected: existing convention uses Norwegian domain terms for Autorisasjonstjeneste operations (consistent with backend definition).

**Security note**: These strings are defined by the backend authorization service. The frontend treats them as opaque string constants. Any mismatch between frontend constants and backend-defined strings results in a fail-closed state (no access) — the safer failure mode.

---

## Decision 4 — Filter State Persistence (FR-043)

**Decision**: Persist filter state via **URL query parameters** using `[SupplyParameterFromQuery]` attributes on page components. Filter changes are pushed to the URL via `NavigationManager.NavigateTo(..., replace: true)` so they do not create browser history entries. Navigating back to the screen restores the filter from the URL automatically.

Example for `OperationCataloguePage`:
```csharp
[SupplyParameterFromQuery(Name = "service")] public string? ServiceFilter { get; set; }
[SupplyParameterFromQuery(Name = "classification")] public string? ClassificationFilter { get; set; }
[SupplyParameterFromQuery(Name = "status")] public string? StatusFilter { get; set; }
```

**Rationale**: The constitution explicitly states: "Where state MUST survive navigation, URL parameters are used." URL-based state also enables shareable filter links and browser back/forward navigation. The Audit Log deep-link requirement (FR-035) uses the same mechanism, so a single approach covers both FR-043 and FR-035.

**Alternatives considered**:
- Scoped service holding filter state in memory — rejected: contradicts the constitution's URL-preference; adds a stateful singleton that needs cleanup between sessions.
- `CascadingValue` — rejected: scoped to a single component hierarchy; does not survive navigation.

---

## Decision 5 — Search Debounce Pattern (FR-023)

**Decision**: Implement a `CancellationToken`-based debounce in `UserSearchPanel.razor`. On each input change event, cancel the in-flight `CancellationTokenSource`, create a new one, `await Task.Delay(300, cts.Token)`, then call the identity service. If the delay is cancelled (i.e. another keystroke arrived), the catch block for `OperationCanceledException` swallows it silently. The search field shows a loading indicator while the request is in flight.

```csharp
private CancellationTokenSource _searchCts = new();

private async Task OnSearchInput(string query)
{
    _searchCts.Cancel();
    _searchCts = new CancellationTokenSource();
    try
    {
        await Task.Delay(300, _searchCts.Token);
        _searchResults = await _userAccessService.SearchUsersAsync(query);
    }
    catch (OperationCanceledException) { /* keystroke cancelled — ignore */ }
}
```

**Rationale**: Standard .NET async pattern; no external library needed. 300ms matches established web UX convention. Synchronous mock services in bUnit tests bypass the delay naturally (the `CancellationToken` is never cancelled in a test that doesn't call `OnSearchInput` twice in quick succession).

---

## Decision 6 — Success Notifications (FR-038)

**Decision**: Use Radzen `NotificationService` (already registered via `AddRadzenComponents()` in `Program.cs`) for auto-dismissing success toasts. Configuration: `NotificationSeverity.Success`, duration 4000ms, position `NotificationPosition.BottomRight`. Called by page components after each confirmed successful mutation.

In bUnit tests: `Services.AddSingleton<NotificationService>()` is sufficient — the service records notifications without rendering; tests verify the notification was triggered or simply ignore it if the test concern is the DOM state post-mutation.

**Rationale**: `NotificationService` is already in the DI container. Zero new dependencies. Auto-dismissal with a fixed duration matches "brief, auto-dismissing" (FR-038). Consistent with Radzen Material theme styling.

**Alternatives considered**:
- Custom toast component — rejected: `NotificationService` already exists; adding a second notification mechanism creates inconsistency.
- Inline success messages in the page body — rejected: spec says "brief, auto-dismissing" which implies a transient overlay, not a persistent inline state.

---

## Decision 7 — Error Handling in Dialogs (FR-036)

**Decision**: Dialog components accept a `string? ErrorMessage` parameter. After a failed mutation, the page component sets this parameter on the dialog's state; the dialog renders an inline error alert below the form content using a `RadzenAlert` (severity Error). The dialog remains open. The confirm button re-enables after the error is set, allowing retry.

Pattern in the page component:
```csharp
private string? _dialogError;

private async Task OnConfirmClassify()
{
    _dialogError = null;
    var result = await _operationService.ClassifyAsync(...);
    if (!result.IsSuccess)
    {
        _dialogError = result.ErrorMessage;  // Dialog stays open, shows error inline
        return;
    }
    _showClassifyDialog = false;
    // Refresh list, update badge if needed, show success notification
}
```

**Rationale**: FR-036 is explicit: the dialog must not close on error. Inline error within the dialog keeps the form context visible so the administrator can see what they were doing and retry without re-opening the dialog.

---

## Decision 8 — Audit Log Deep-Link Mechanism (FR-035, SC-008)

**Decision**: `AuditLogPage.razor` reads query parameters via `[SupplyParameterFromQuery]`: `entityType` (string?), `entityId` (Guid?), `actorId` (Guid?), `from` (string?, parsed to DateOnly), `to` (string?, parsed to DateOnly). On `OnParametersSetAsync`, any non-null params are applied to the filter model before the initial fetch. Page components that link to the Audit Log construct URLs like:

```
/admin/audit-log?entityType=Operation&entityId=3fa85f64-5717-4562-b3fc-2c963f66afa6
```

The "Show history" button in `OperationCataloguePage` navigates to this URL. The `AuditLogFilterPanel` reflects the pre-populated filter state so the administrator can see and modify it.

**Rationale**: Built-in Blazor query parameter binding via `[SupplyParameterFromQuery]`. Consistent with Decision 4 (filter state in URL). No extra routing infrastructure.
