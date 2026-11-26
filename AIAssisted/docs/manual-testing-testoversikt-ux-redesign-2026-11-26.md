# Testoversikt UX redesign — 2026-11-26

**Scope:** Manual Testing → Testoversikt (`frontend/BirkNext.Web/Pages/ManualTesting.razor`, home view).

**Changes:** presentation only. There are no backend, API, database, metric or widget-id changes, and no Azure DevOps calls.

**Status keys used:** IMPLEMENTED · VERIFIED (tests or a browser measurement) · PARTIALLY VERIFIED · NOT IMPLEMENTED.

## A. Current UI audit (before)

- **Page structure:**
  - the page is the `ManualTesting` component in `.mt.msb-page`, below `TestingWorkspaceHeader` and `SpecificationScopePicker`;
  - then come the level switch (`mto-level`), a KPI grid (`mto-kpis`) and one card grid (`mto-grid`);
  - all charts and panels sat in that one grid, in registry order, followed by the module table.
- **Charts:**
  - every chart is the shared `DeliveryBarChart`: custom HTML bars, each with a printed value and the meaning as tooltip and accessible name;
  - there is no chart library and none was added.
- **Settings:** widget visibility comes from `ManualTestingDashboard.Widgets` (`shared/ManualTestingDashboardContracts.cs`), set in System Settings → Manual Testing → Testoversikt.
- **Data:** one request (`OverviewAsync`) plus the settings and scope read in parallel. All figures come from `ManualTestingOverviewPresentation` (`O.Of`, `O.Compute`, `O.Preparation`, …).

## B. Root cause of the narrow layout

**The cause:** `manual-testing.css` line 2 sets `.mt { max-width: 72rem; }`.
- This caps every Manual Testing page at about 1,152 px.
- At 1920×1080 with the 296 px sidebar, the content area is 1,656 px wide, so about 500 px stayed empty.
- Inside that cap, `.mto-grid` (`minmax(min(100%, 26rem), 1fr)`) fitted at most two narrow columns.
- This is neither the shell, a parent grid, nor zoom; it is this one rule.

**The fix:**
- `.mt.mto-page { max-width: none; container: mto / inline-size; }`, applied only to Testoversikt;
- Testplaner and the other pages keep their reading width;
- the lead text is capped at 80ch.

## C–F. Components, registry, data and metrics (unchanged)

- **Unchanged:**
  - widget ids, default visibility and the System Settings switches (the same registry; no second mechanism);
  - all metric definitions and their texts.
- **Clarified in the subtitles (same meaning):**
  - «Manglende testdekning» = scenarios with no linked manual test case, not developer tests;
  - «Testdekning per modul» = the share of scenarios with at least one stored manual case, not developer-test coverage.

## G. UX problems found

1. A narrow column with large empty space.
2. Every widget had the same weight; there was no hierarchy.
3. Follow-up sat among the charts.
4. Small KPI values.
5. Long module charts with no limit.
6. A plain module table.
7. Notes placed under the bars.

## H–N. New layout (IMPLEMENTED)

| Level | Content |
|---|---|
| **1** | Header, plus a **scope toolbar**: the level switch, a scope chip («Hele prosjektet» / «Valgt modul: …», truncated with a tooltip) and «Tilpass oversikten». The scope picker is not duplicated. |
| **2** | **Four core KPIs** (Scenarioforslag, Testtilfeller, Godkjente, Klar til testing), large values (about 2.6rem at 1920 px), with a thin **meter** for real shares (for example «0 av 120 godkjent (0 %)»: text plus bar, never colour alone, and no meter when the base is 0). The optional figures (Testplaner, US status, scenario types) sit in a quiet secondary strip. No trends are shown. |
| **3** | **«Dette bør følges opp»**, an accented band: P1 attention items as large buttons (the same drill-down), «Testbarhet og blokkeringer» and «Kildeendringer». When nothing in the scope needs follow-up, a short honest empty state replaces any warning. |
| **3** | **«Testberedskap»**: Testforberedelse (overlapping stages, not summed), Scenarioer etter prioritet (order P1, P2, P3, Ikke prioritert, as before) and Manglende testdekning. |
| **4** | **«Moduler»**: «Testdekning per modul» as a wide card showing the **first 8 bars + «Vis alle (N)»** (nothing becomes unreachable), «Godkjenning per modul», and the **module table** (sticky header, right-aligned tabular numbers, row hover, «Trenger oppfølging» highlighted when above 0, its own keyboard-focusable scroll region with an accessible label, module click-through unchanged). |
| **5** | **«Testplaner og mer innsikt»**: plans with an approval meter and «Se alle testplaner», plus the optional charts, the history notice, recent changes and translations. |

**Other layout notes:**
- **Card rows:** cards sit in wrapping flex rows, so two cards (or one) never leave an empty column. This was found in the browser check and fixed.
- **Bars:** they are thicker, with a label column of up to 38 % and right-aligned values.
- **Drill panel:** it is unchanged and placed after the module band.

## O. Drill-down

- **Unchanged and still working:**
  - chart bars open the drill panel;
  - P1 items open the drill panel;
  - the KPI links (Scenarioforslag → Finn og forstå, Testtilfeller → Planlegg, Testplaner → Testplaner);
  - module rows set the module scope;
  - plans open the plan.
- **No fake interactivity:** Godkjente and Klar til testing stay non-clickable, as before, because there is no matching filtered view.

## P. Responsive behaviour (VERIFIED in the browser, isolated instance)

Measured with Playwright on the isolated instance (`v1env10`: a disposable database `birknext_adoverify_20261126_110540`, ports 5097/5197, with M2LB imported and three seeded plans):

| Viewport | Page width / content width | KPI columns | Readiness columns | Horizontal page overflow |
|---|---|---|---|---|
| 1920×1080 | 1600 / 1656 | 4 | 3 | none (scrollWidth = 1920) |
| 1440×900 | 1120 / 1176 | 4 | 3 | none |
| 1280×800 | 960 / 1016 | 4 | 2 | none |
| 1024×768 | 704 / 760 | 2 | 1 | none (the table scrolls inside its own region) |
| 768×1024 | 448 / 504 | 1 | 1 | none |
| 390×844 | 342 / 390 | 1 | 1 | none |

- **Before vs after:** the cap was 1,152 px. The page now uses 1,600 of the 1,656 px available at 1920.
- **Screenshots:** `scratchpad/v1/v1env10/shots/mto-light-*.png` (full page and first screen), plus dark-theme copies.

## Q. Accessibility

- **Headings:** h2 per band, h3 per card.
- **Charts:** each keeps its `aria-label`, and every bar button has a full accessible name.
- **Meters:** `role="img"` with a textual `aria-label`.
- **Controls:** the follow-up buttons are large targets with a visible focus ring; «Vis alle» uses `aria-expanded`; the table region is focusable and labelled.
- **No colour alone:** values are always printed.
- **Dark theme:** the CSS uses only the existing theme tokens (`--msb-*`, `--clr-*`).

## R. Performance

- **Before:** the earlier profile in this session measured a 2.6 s reload of Testoversikt on the same isolated setup.
- **After:** two reloads measured 3.42 s and 3.02 s. Each reload includes a WASM boot, and the database differs (a fresh import plus three plans), so the numbers are not directly comparable.
- **Request pattern:** unchanged (one overview request plus settings and scope; no new request).
- **Rendering cost:** the redesign adds only markup and CSS. Module bars beyond 8 are not rendered until «Vis alle».

## S. Frontend regression

- **New tests (`ManualTestingWorkspaceTests` R01–R06):**
  - band order and core KPIs, with widget ids unchanged;
  - meters (real shares, no meter on an empty base, no trend text);
  - the follow-up band and its drill-down;
  - an honest empty state;
  - the 8 + «Vis alle» limit reaching every module, with the table still complete;
  - the CSS width cap lifted for Testoversikt only, with container-aware rules and filling rows.
- **Results:**
  - all existing Manual Testing tests pass (471 before the new tests);
  - the full frontend suite before the browser-driven CSS fix: **9,777 / 9,777**.

## T. Browser verification

- **VERIFIED (light):** 1920 and 1440 screenshots were inspected.
  - **First pass:** it showed three defects: the follow-up band had two cards and an empty third column, the module chart was alone at 2/3 width, and the plans band had one card at 1/3.
  - **Second pass:** after the change to wrapping flex rows, the rows are full.
- **Drill-down:** clicking a readiness bar opened the drill panel (`chart.preparation` / `created`).
- **Console:** only 401s from requests made before the local session was connected (the login step); no errors after it.
- **Dark theme: PARTIALLY VERIFIED.** A run with the browser's colour scheme set to dark still rendered the light theme, because BirkNext uses its own theme setting rather than the browser's. The redesign uses only theme tokens, but it was not seen in BirkNext's dark theme.

## U. Modified files

- `frontend/BirkNext.Web/Pages/ManualTesting.razor` — overview markup, the `Chart` helper (wide/limit), `Meter`;
- `frontend/BirkNext.Web/wwwroot/css/manual-testing.css` — the redesign block;
- `frontend/BirkNext.Web.Tests/Pages/ManualTestingWorkspaceTests.cs` — R01–R06.

## V. Remaining limitations

- **«Scenarioforslag 0 % med testtilfeller» with 120 seeded cases:** this comes from the existing metric. The seeded performance plans' cases are not counted as covering a scenario identity in this data set. It is not investigated here, and the metric is unchanged.
- **No before-screenshots:** the before state is documented by the CSS rule and its measured effect (1,152 px).
- **Charts are bars only:** no donut or stacked-bar view was added. The preparation stages overlap, so a stacked bar would mislead.
- **No zoom-level runs:** the viewport runs cover the responsive breakpoints.

## W. Acceptance criteria

| Status | Criteria |
|---|---|
| **VERIFIED** | AC01–AC05, AC08, AC09, AC13, AC14, AC19–AC23, AC25, AC26, AC31, AC33, AC37–AC39 |
| **IMPLEMENTED** | AC06, AC07, AC10–AC12, AC15–AC18, AC24, AC27–AC30, AC32 (by the tests and markup; no screen-reader run), AC34 (tokens only; dark not seen in the browser), AC35/AC36 (measured; not directly comparable) |
| **AC40** | This report |
| **AC41** | Overridden by the user's standing instruction to commit after implementation |
