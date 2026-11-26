# Manual Testing — Custom KPI tiles (2026-11-26)

Commits: `ff6bdc69` (tiles), `3205eacc` (honest zero), `3c9b43fb` (moved into «Tilpass oversikten», see the customization v5 report).

## Architecture and reuse
- **Registry:** the existing widget registry `ManualTestingDashboard.Widgets` and the settings store `ManualTestingDashboardSettingsStore` (one installation-wide JSON file; writes need the existing settings-write policy). No new database model.
- **Model:** `DashboardKpiTile(Id, Title, Dimension, Metric, Visible, Order)` in `shared/ManualTestingDashboardContracts.cs`.
  - `Tiles` is null means the default «Ende-til-ende-tester» (Testnivå / Ende til ende).
  - `DashboardKpiTiles.Normalize` validates: unique stable ids, title trimmed and capped at 60 characters (empty falls back to the metric's own name), dimension and metric must match.
- **Dimensions:** Testnivå (Ende til ende / Integrasjon / Komponent / Uavklart) and Scenariotype (Funksjonell / Ikke-funksjonell / Blandet / Uavklart). Test method is **NOT IMPLEMENTED**: the overview has no per-scenario method.

## Counting
- `ManualTestingOverviewPresentation.TileScenarios/TileCount` counts **unique scenario identities**: acceptance scenarios plus standalone documented tests. It never counts stories, cases, plan occurrences or requirements without a scenario.
- Unavailable data shows «—», never 0. The title never changes the count (unit test).
- Source-declared versus verified level: `OverviewScenario.LevelDeclared` makes the explanation read «N dokumentert som E2E i kilden – ikke verifisert».
- A zero on a test level explains «N scenarioer har uavklart testnivå – 0 betyr ikke at det ikke finnes slike tester».

## Click-through
`ManualTestingDrillNavigation.OpenFilteredAsync` applies a Finn og forstå filter on exactly the tile's dimension (`TestLevel` or `ScenarioType`). The active scope is not changed.

## Editing
- `KpiTileEditor.razor` provides «Navn på flis», a grouped metric select and a live preview count. Its labels are English in System Settings.
- Since v5, editing happens in «Tilpass oversikten» (draft, saved with «Ferdig») and in System Settings; the per-tile menu is gone.

## Persistence and permissions
- Settings are installation-wide; there is no per-user preference store in this installation, and this is stated in the UI.
- Writes use the existing permission. Plans, cases and scenarios are never touched (asserted by the tests: `SaveAsync` is never called).

## Tests
- **Backend:** `ManualTestingDashboardSettingsStoreTests` (tiles, reset, invalid metric refused).
- **Frontend:** `ManualTestingWorkspaceTests.T01–T05` (counting, normalisation, normal view, draft mode, reset and unknown ids).
- Full frontend suite 9,833/9,833.

## Browser (v1env17)
PASS:
- `B-metric-change`: E2E = 8 → Funksjonell = 376, with the preview showing the same count.
- `B-click-through`, `B-two-tiles`, `B-hide`, `B-restore-from-settings`.
- `B-1920`, `B-1024`, `B-390` (no horizontal scroll).
- `B-reset`, `B-no-plan-change` (plan revisions unchanged).

`B-default` / `B-rename-persists` were reported FAIL only because the script read CSS-uppercased text («ENDE-TIL-ENDE-TESTER»). The DOM text is correct, so this is a script artifact, not a product defect.

## Status
- **IMPLEMENTED / VERIFIED:** AC01–AC05, AC07–AC10, AC12, AC15–AC18, AC20.
- **IMPLEMENTED:** AC11, AC13, AC19.
- **PARTIAL:**
  - AC06: documented tests are counted after a re-import;
  - AC14: «source snapshot» means the latest discovery.
- **Not held:** AC23 (no commits), because the user asked for commits.
