# Feature Visibility — audit (task 13)

Baseline: `50d8e95e` on `008-traceability-first` (System Settings task 12 included). Paths are relative to `AIAssisted/`.

## Where things were defined (before)

| # | List | Location | Role |
|---|---|---|---|
| A | `PlatformFeatureKeys` (4) | `backend/BirkNext.Api/Services/AdminService.cs` | cannot be saved as off |
| B | `CoreFeatures` (22) | same | editable; default from `CoreDefault` |
| C | `AdvancedFeatures` (8) | same | editable; default **false** when absent |
| D | `BuildFeatureVisibility()` | same | the sidebar flags (`GET api/admin/feature-visibility`), with **its own defaults** |
| E | `FeatureVisibilityTaxonomy.Categories` / `AdvancedKeys` / `MenuItems` (29) | `frontend/BirkNext.Web/Services/FeatureVisibilityTaxonomy.cs` | Settings grouping, plus a hand-written Menu Synchronization list |
| F | `FeatureVisibilityDto` defaults | `frontend/BirkNext.Web/Services/AdminApiService.cs` | fallback when the backend does not answer |
| — | `NavigationCatalog.Sections` (53 rows) | `frontend/BirkNext.Web/Services/NavigationCatalog.cs` | the real sidebar: each row's own visibility rule |

## The baseline numbers, reproduced

Configured 30, Visible 22, Hidden 8, Menu Items 29, "Menu synchronization: In sync".

- **Configured 30** counts the B + C toggle keys (22 core + 8 advanced). Platform keys are excluded.
- **Visible 22 / Hidden 8** count the stored values of those keys: core has 21 on and 1 off (Change Traceability); advanced has 1 on (Documentation Drift) and 7 off.
- **Menu Items 29** counts the entries of the hand-written list E. That list:
  - includes the 4 platform keys;
  - omits 5 configured keys (QA Artifact Library, WASM Security Review, Legacy Traceability Navigation, Architecture View, Extraction Review);
  - so 30 − 5 + 4 = 29. The near-equality with 30 is a coincidence.
- **"Visible" was not "visible in the sidebar".** Of the 22 keys that were on:
  - Traceability & Coverage's row was hidden by the legacy master switch;
  - QA Artifact Library, WASM Security Review and WASM Performance Review gate nothing.
  - Only 18 of the 22 produced a sidebar row.
- **"In sync" was not justified.** The check only tested that each `MenuItems` key existed in the configuration, in one direction. It never read the sidebar, so it could not see that:
  - "WASM Performance Review" has no row and no page;
  - 25 rows were missing from the list (20 always-shown rows and 5 shared or borrowed ones);
  - three labels differed from the sidebar's;
  - a row wired to the wrong key would still report "In sync".

## Confirmed defects

| Id | Defect | Evidence |
|---|---|---|
| FV-D1 | The Settings and sidebar defaults disagreed for Documentation Drift, Impact Analysis, Code Traceability and Traceability Suggestions. Advanced defaulted to false in Settings but true in the sidebar, so a key the configuration omitted was counted Hidden while the sidebar showed its page. | AdminService `BuildEditableSettings` vs `BuildFeatureVisibility` |
| FV-D2 | Five toggles had no effect: QA Artifact Library, WASM Security Review (counted but never rendered), WASM Performance Review (listed as a menu item), Extraction Review and Architecture View (modes of a component no page renders). | grep: no consumer and no nav row |
| FV-D3 | Traceability & Coverage was counted Visible while its row was hidden. | `FeatureVisibilityService.TraceabilityCoverage` requires the legacy switch |
| FV-D4 | The Menu Synchronization check used names only and could not detect drift (see above). | `SystemSettings.razor` `UnflaggedMenuItems` |
| FV-D5 | Dead route and link lists (`KnownRoutes`, `RemovedRoutes`, `DocNavLinks`, `DocPageLinks`) were stale and never referenced. | `SystemSettings.razor` |
| FV-D6 | Broken link `/explorers` on the Requirements Traceability "selection required" state. | `Pages/Traceability.razor` |
| FV-D7 | The Dashboard's quick action linked to AI Change Review even when the feature was hidden (off by default). | `Pages/Dashboard.razor` |
| FV-D8 | Orphan configuration keys `CreateTestScenario` and `QaReadiness` were read by no code. | `appsettings.json` |
| FV-D9 | When the backend did not answer, the frontend fallback showed Impact Analysis, Code Traceability and Traceability Suggestions, which the backend defaults to off. | `FeatureVisibilityDto` |
| FV-D10 | Unknown keys were accepted and persisted to `appsettings.Local.json`. | `AdminService.ValidateSettingsUpdate` |
| FV-D11 | No record of who changed what. Only "Local settings saved to {Path}" was logged. | `AdminService.SaveSettingsAsync` |
| FV-D12 | The page told users to "refresh the page" after saving, but the sidebar already updates immediately (`FeatureViz.ApplyLocalFlags`). | `SystemSettings.razor` |

## Visibility, availability and authorization

- **Visibility is presentation only.** The design statement is in `NavigationCatalog`: "BirkNext has no per-module authorization". Every hidden page still opens by its address; browser check FV-18 confirms this.
- **Availability is a separate axis.** `FeatureAvailabilityCatalog` defines it; Environment Analysis is Unavailable but shown.
- **Server-side protection:**
  - `LocalRequestGuardMiddleware` requires a loopback Host and a local session or Entra bearer for every `/api` call, plus CSRF Origin checks and the client header for writes.
  - Feature Visibility is enforced server-side only for Source Analysis evidence and Azure environment evidence.
  - The APIs of disabled features have no flag check.
- **Saving** needs a local session; FV-20 confirmed a request without one is refused with 401. There is no role model.

## Recently added modules

These are all sidebar entries with no switch of their own, so they are always shown, by design:
- System Understanding (borrows the platform key)
- Backup & Transfer (borrows the platform key)
- Integration Test Suites
- Test Execution & Evidence
- Contract Testing
- Test Data
- Schema Studio
- Skolenærvær Testing

Help Center is the platform key User Guide. No duplicate toggle was added.
