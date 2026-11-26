# Scenario grouping and hierarchy — 2026-11-26

**Scope:** Manual Testing → Finn og forstå (the scenario list) and «Ny testplan» (Testplaner).

**Safety:**
- no database change, migration or reset;
- no Azure DevOps call;
- no change to source identities or classification;
- existing plans are never regrouped.

**Status keys used:** IMPLEMENTED · VERIFIED (tests) · NOT VERIFIED (browser) · NOT IMPLEMENTED.

## A–B. Audit: what existed

| Area | Status | Evidence |
|---|---|---|
| A «Grupper etter» control | **PARTIAL** | `ManualTestingDiscover.razor`, `StoryGroupingPresentation.Mode` — one select mixing two concerns: how the **US sidebar** is grouped (Brukerhistorier, Funksjonsområde, Forretning, Ingen) and two **list** modes (Testnivå, Scenariotype). |
| The list modes | **PARTIAL** | They sorted the one paged list and inserted flat, non-collapsible headers (`mtd-type-group`). No secondary level, no collapse, no group selection, no module, area, story or method axis. |
| Classifications | **IMPLEMENTED** | `TestLevelClassifier` (test-level/1), `ScenarioTypeClassifier` with reviewer decisions, functional area (`Item.Area`), method (`Item.Relevance` plus the reviewer's kind), module (`PlanStructure.ModuleOf`). |
| New plan from a selection | **IMPLEMENTED** | `PlanStructure.Build` with templates (LevelArea, ModuleStory, TypeLevel, None), the same in the browser preview and on the server. |

No duplicate engine was built: grouping reads the item's existing facts, and plan creation reuses `PlanStructure.Build`.

## C–D. «Integrasjon / E2E»

**What the value is:**
- It is **not a test level or classification value**.
- It is the label of `ExecutionApproach.IntegrationE2E` (`shared/TestDesignAdvice.cs:238`): a *suggested execution approach* from `TestDesignAdvisor.Suitability`, shown in the row's approach text and in the «Testmetode/fremgangsmåte» filter.

**How it is handled:**
- The test level is a separate fact (`TestLevelClassifier.Of(candidate.Type)`).
- A scenario whose source does not state its boundary stays **«Uavklart testnivå»**.
- Nothing is reclassified, and the approach label is unchanged.
- When grouping by test level, these scenarios fall under their own test level. They are never moved into E2E because of the approach.
- E2E **candidates** (documented flows, `TestLevelResult.Candidate`) are counted on the E2E group header as «N kandidater (ikke gjennomgått)».

## E–G. Model

- **Shared, `shared/TestPlanStructure.cs`:**
  - `ScenarioGroupAxis { None, Story, TestLevel, ScenarioType, Area, Module, Method }`;
  - `ScenarioGroupAxes`: Norwegian labels («Ende-til-ende-tester», «Integrasjonstester», «Komponenttester», «Uavklart testnivå»; «Funksjonelle», «Ikke-funksjonelle», «Blandede», «Uavklart type»; «Scenarioer uten brukerhistorie», «Uavklart område», «Uten modultilknytning»), plan-structure keys `axes:Primary>Secondary`, parsing and validation.
- **Frontend, `Services/ScenarioGrouping.cs`:**
  - pure: `ValueOf(item, axis)`, `Build(shown, primary, secondary)`, `Default`, `Parse`/`Format`, `Paths`;
  - one value per axis, so a scenario appears **once** per tree;
  - counts are unique ids (`DistinctBy(Id)`);
  - story groups are **specification-scoped** (`{SpecificationPath}#{Key}`), so «US1» of two specifications are two groups;
  - stories are ordered by number (US2 before US10).

## H. Defaults and preference

- **Defaults:**
  - a selected specification → **Brukerhistorie**;
  - otherwise (whole project) → **Testnivå → Scenariotype**.
- **Preference:**
  - an explicit choice is saved per browser, and so per user (`localStorage`, key `birknext.manualTesting.scenarioGrouping`);
  - it overrides the default and is never stored on a scenario or a template.

## I–L. UI (IMPLEMENTED, VERIFIED by bUnit)

- **Toolbar above the results:**
  - «Grupper etter» (7 choices, including «Ingen gruppering»);
  - «Deretter etter» («Én gruppering» plus the other axes; the primary axis is excluded);
  - «Vis alle grupper» / «Skjul alle»;
  - a selection line: «N valgt totalt · M i gjeldende visning · K utenfor gjeldende filter».
- **Group sections** (`mtd-sgroup`):
  - a chevron toggle with `aria-expanded`, the label and a count;
  - «N valgt» and E2E candidates on the header;
  - «Velg alle (N)» / «Fjern valg» per group;
  - nested secondary groups;
  - the same scenario row as the flat list, through a shared `Row` fragment, so the drawer, «Vis detaljer» and selection are unchanged.
- **Pipeline:** scope → search → filters → **grouping** (`ScenarioGrouping.Build(shown, …)`), so groups only hold scenarios that pass the filters. Selection survives filters, collapse and grouping changes.
- **Large lists:** groups start collapsed when more than 200 scenarios are shown, and each group renders 50 rows at a time («Vis flere i gruppen»).
- **The sidebar control** is renamed «Brukerhistorier etter» and keeps only the sidebar modes. Test level and type moved to the list's grouping.

## M–N. Standalone and cross-module scenarios

- A scenario without a story goes to «Scenarioer uten brukerhistorie»; no US is invented.
- **Module:**
  - it comes from the scenario's own specification path, or else the specification evidence reference;
  - otherwise «Uten modultilknytning»;
  - each scenario is in one group, so it is never copied.

## O–Q. New plan from the grouping

- **In «Ny testplan» → «Fra valgte scenarioer»:**
  - the structure list offers «Bruk grupperingen fra Finn og forstå: Testnivå → Scenariotype» when a grouping is saved;
  - the preview and the server use the same `PlanStructure.Build("axes:…")`, so the persisted static suites are exactly the preview;
  - suite ids are stable (path-based), each case is in one suite, and a test-level suite keeps its level.
- **Excluded:** test method cannot be a plan structure (it is a decision about a scenario, not a fact of a case).
- **Existing plans** are never touched. A test verifies an existing plan's revision, suites and memberships are unchanged when another plan is created with a grouping.
- **ADO:** export and transfer use the same suite tree as before.

## T–V. Tests

- **Backend:**
  - `HierarchicalTestPlanTests`: the grouping becomes two-level suites with list labels, stable ids and one suite per case; test method, unknown and duplicate axes are refused while the old templates still work;
  - `SourceDrivenPlanningTests` (temporary database): a new plan with `axes:TestLevel>Area` persists the suites; an existing plan is unchanged.
- **Frontend:**
  - `ScenarioGroupingTests`: specification-scoped stories, the story-less group last, numeric story order, honest unknown groups, candidates, defaults and preferences;
  - `DiscoverStoryStatusAndTypeTests` G01–G03: the default story grouping, collapse/expand and expand/collapse all, the saved preference overriding the default, two levels, the secondary axis never repeating the primary, method grouping, the preference saved, «Ingen gruppering» as a flat list, group select/clear, and the selection outside the filter;
  - existing tests updated: the type grouping moved to the list control, and the drawer test now uses scenario ids instead of positions, because the grouped order differs.
- **Results:**
  - Discover tests 416/416;
  - grouping tests 12/12;
  - backend 10/10 + 2/2;
  - full frontend suite: see the commit follow-up.

## W. Browser

**NOT VERIFIED** in the browser for this delivery. The isolated instance in use runs the build from before these changes, and a republish and re-import were not run here.

## Y. Remaining gaps

- **Grouping template presets:** not added to the template store; the built-in templates are unchanged.
- **Group expansion state:** it is per page session (not saved), apart from the grouping choice itself.
- **Scenario type source:** the list uses the reviewer-decided type; plan facts use the proposed type (`FactsOf`, as before). A reviewer-changed type can therefore land in a different suite than its list group. The difference is documented, not changed.
