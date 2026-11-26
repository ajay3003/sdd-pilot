# Manual Testing — Testoversikt customization v5 (2026-11-26)

Commit: `3c9b43fb`.

## A–B. Architecture and storage (audited)
- **Registry:** `ManualTestingDashboard.Widgets` (ids, groups, defaults).
- **Storage:** `ManualTestingDashboardSettingsStore`, one installation-wide JSON file, with the existing settings-write policy on PUT.
- **No identity layers:** there are no per-user or per-project preferences and no browser-local layout. This installation cannot tell users apart, so the layout is the workspace's and the UI says so.
- **Precedence:** a saved explicit value, otherwise the registry default. KPI order (new) falls back to the default order.

## C. Tile menus — removed
The per-tile ⋯ menus are gone from the normal view. Test `T03` asserts no `mto-tile-menu` and no slot tools.

## D–G. «Tilpass oversikten» — a local mode
- It is a button on Testoversikt, not a link to System Settings. The URL is unchanged (tested) and the scope is kept.
- **Toolbar:** «Tilpass testoversikten», pending indicator, «Ferdig», «Avbryt», «Tilbakestill standardoppsett» (with a confirmation).
- **Panel:** «Tilgjengelige fliser» lists every registered widget by group with a «Vis flis» checkbox, plus own key figures (edit, remove, «Legg til nøkkeltall»).
- **KPI cards:** ← / → buttons (keyboard-operable, labelled) and «Skjul flis».
- **Draft model:**
  - nothing is written before «Ferdig», and «Avbryt» writes nothing (tested);
  - «Ferdig» sends one request containing only the changed switches, the tiles and the order;
  - navigation with unsaved changes is stopped once with an explanation (`NavigationLock`, tested).

## H–I. Administrative defaults
- System Settings is titled «Testoversikt default layout (Standardoppsett for Testoversikt)».
- It explains that it is the same layout testers change with «Tilpass oversikten», and that no per-user layer exists. It is one engine, not two.

## J. Backward compatibility
- Old settings files stay valid. New fields are optional, so `KpiOrder` null means the default order.
- Unknown or repeated saved ids are ignored, not errors: `KpiSequence` (test `T05`, backend store test).
- «Tilbakestill standardoppsett» resets visibility, tiles and order (`ResetLayout`) but keeps the plan-structure setting (backend test).

## L–M. KPI correctness and filter independence
Customization changes presentation only; the numbers come from the same `ManualTestingOverviewPresentation`. Scope (project/module/specification) and layout are independent: scope is not stored in the layout.

## N. KPI row
`data-count` drives the columns:
- 5 cards in one row when the content is ≥ 66 rem;
- 3 + 2 at medium widths;
- 2 columns under 52 rem, where the last odd card spans the row;
- 1 column under 30 rem.

## P. Tests
- **Frontend:** `T03`–`T05` plus the tile tests, 834/834 in the dashboard suites; full 9,833/9,833.
- **Backend:** `Kpi_order_persists_…` (5/5 store tests).

## Q. Browser
v1env18 (`C-no-tile-menus`, `C-local-mode`, `C-cancel`, `C-1920/1440/1024/390`), see the walkthrough report.

## S. Remaining
- Reordering is supported for the KPI row only; chart sections keep their fixed order (visibility only).
- No per-user preferences: limited by the installation.
