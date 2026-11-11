# Feature Visibility — implementation (task 13)

This extends the System Settings work of task 12. There is no second Settings implementation and no second registry.

## Backend (`AdminService`)

- **One default per key.**
  - `FeatureDefault(key)` returns: platform → true; core → `CoreDefault`; advanced → `AdvancedFeaturesEnabledByDefault` (Documentation Drift only).
  - `BuildFeatureVisibility` (the sidebar) and `BuildEditableSettings` (Settings) both use it.
  - Under the shipped `appsettings.json` nothing changes, because the keys are set explicitly there. The change only matters when a configuration omits a key.
- **Validation** rejects unknown keys (400 "Unknown feature key …") before anything is written. Platform keys still cannot be turned off.
- **Audit log:** "Feature visibility changed by a local session: ImpactAnalysis: off -> on" lists each changed key with its before and after values. There is no persisted audit record (gap FV-G3).
- `appsettings.json`: the orphan keys `CreateTestScenario` and `QaReadiness` were removed.

## Frontend

- **`FeatureVisibilityTaxonomy`** now holds only the declarations code cannot provide:
  - lifecycle (Optional, Legacy, No effect) with notes and successors;
  - retired keys;
  - classified pages reached from other pages.
  - The hand-written `MenuItems`, `Categories` and `AdvancedKeys` were removed.
- **`FeatureVisibilityInventory`** (new) derives:
  - every sidebar entry's required and any-of switches, by evaluating the row's own rule through the same JSON shape the backend sends;
  - whether the entry is shown, and the reason when it is hidden;
  - its availability and its Help anchor.
  - It also produces navigation issues, compared by identity:
    - entry opens no page;
    - entry can never be shown;
    - duplicate entry or switch;
    - switch gates nothing;
    - declared without effect but gates an entry;
    - declared switch not configured;
    - page without a sidebar entry;
    - classified page no longer exists.
- **`Components/Admin/FeatureVisibilityPanel.razor(.css)`** (new) replaces the old Feature & Menu Visibility card:
  - header "Feature Visibility" with the brief's subtitle and a Help link;
  - four metrics with units (Switches, Shown in the sidebar, Hidden, Navigation);
  - search by name, description, group, key or address;
  - filters with counts: All, Shown, Hidden, Unavailable, and Optional, legacy & retired;
  - collapsible groups in sidebar order, plus "Switches without their own entry", "Pages reached from other pages" and "Retired";
  - one compact row per entry, with the hidden reason inline;
  - inline Details (`aria-expanded`) showing sidebar location, address, visibility rule, configuration key, lifecycle and successor, availability, and access. Access says plainly that hiding is not access control.
  - Help and Open links; Open appears only for shown entries.
- **Edit mode:**
  - toggles (`role=switch`) appear only for switches with an effect; platform and no-effect switches get none;
  - the panel previews the sidebar with the edited values;
  - the stale "refresh the page" text was corrected.
- **Configuration Health** counts one issue when the identity-based navigation check has issues (it used to use the names-only check).
- **Fixes:**
  - the frontend fallback defaults match the backend;
  - `/explorers` → the role's Explorer;
  - the Dashboard's AI Change Review quick action follows its switch;
  - dead route lists removed.
- **Help Center:** the new card `#feature-visibility` explains shown vs hidden vs unavailable, optional, legacy and no-effect switches, why not every page has an entry, and the Navigation check.
- **Responsive fix (all pages):** `.page-header-left` has an 18rem flex basis, so header actions wrap below the title instead of squeezing it. The 390 px screenshot showed the overlap.

## Files

- Backend:
  - `backend/BirkNext.Api/Services/AdminService.cs`
  - `backend/BirkNext.Api/appsettings.json`
  - `backend/BirkNext.Api.Tests/Services/FeatureVisibilityDefaultsTests.cs` (new)
- Frontend:
  - `Services/FeatureVisibilityTaxonomy.cs`
  - `Services/FeatureVisibilityInventory.cs` (new)
  - `Components/Admin/FeatureVisibilityPanel.razor(.css)` (new)
  - `Pages/Admin/SystemSettings.razor(.css)`
  - `Services/AdminApiService.cs`
  - `Pages/Traceability.razor`
  - `Pages/Dashboard.razor`
  - `Components/Guide/GuideReference.razor`
  - `Services/UserGuideContent.cs`
  - `wwwroot/css/components.css`
- Frontend tests:
  - `Services/FeatureVisibilityInventoryTests.cs` (new)
  - `Services/FeatureVisibilityTaxonomyTests.cs`
  - `Pages/SystemSettingsFeatureVisibilityTests.cs`
  - `Pages/SystemSettingsEnvironmentDiagnosticsTests.cs`
  - `Pages/GeneratedDocumentationExplorerTests.cs`
  - `Pages/AnalysisNavigationTests.cs`
  - `Layout/NavMenuTests.cs`
  - `Layout/NavMenuLayoutTests.cs`

## Test changes explained

- **Tests that relied on the old frontend fallback** showing Impact Analysis, Code Traceability or Traceability Suggestions with no backend answer (defect FV-D9) now state those flags explicitly, as an installation that enables them would. The behaviour they check (order, hierarchy, legacy group) is unchanged.
- **Removed tests:** the category and `MenuItems` tests were removed together with the hand-written lists they guarded. The inventory tests replace them with identity checks.
