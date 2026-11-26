# Testoversikt → Finn og forstå dashboard subset and scope synchronisation (blocker fix) — 2026-11-26

## Root cause

- **How the subset travels:** «Åpne alle N» in Testoversikt (`ManualTestingDrillNavigation.OpenScenariosAsync`) selects the module and specification and stores a Finn og forstå filter with `Identities` (the subset's source identities, `ScenarioBrowserPresentation.FromOverview`). The filter goes in session storage, not in the URL, so there is no query parameter.
- **Defect 1 (left/right mismatch):** `ScenarioWorkspacePresentation.Stories` built the story list from **every** scenario of the scope; only the scenario list applied `Identities`. The left column therefore listed unrelated stories.
- **Defect 2 (stale subset):** `ManualTestingDiscover.OnScopeChangedAsync` cleared the shown data on an explicit module/specification change but **kept the filter**: its `Identities`, and also a `Story`/`Requirement` of the previous specification. The tester had to press «Fjern utvalget».

## Fix (the existing filter model; no second filter system)

- **`ScenarioWorkspacePresentation.Stories`:** with a subset, only the subset's scenarios form the story list. Stories use their specification-scoped keys; story-less scenarios of the subset appear under «Uten brukerhistorie». The story list, scenario list, group headers and counts all derive from the same subset.
- **`ManualTestingDiscover.OnScopeChangedAsync`:**
  - **What it clears:** an explicit scope change clears `Identities`, `Selection`, `Story` and `Requirement` through `Set()`. That writes the stored filter, so a reload or Back never reapplies the old subset.
  - **When it runs:** the picker reports only the tester's own change (`SpecificationScopePicker.ApplyAsync` → `Changed`), never the initial restore, so arriving from Testoversikt keeps the subset.
  - **What is kept:** search, type, priority and the other view filters, plus the checkbox selection. The selection line reports selections outside the scope.
- **Kept unchanged:**
  - the subset survives opening the drawer, checkboxes, grouping changes and expanding/collapsing groups;
  - «Fjern utvalget» still ends it explicitly.

## Regression tests (bUnit, `DiscoverStoryStatusAndTypeTests`)

| Test | Covers |
|---|---|
| S01 | dashboard subset → the scenario list and the story list show the same subset (US2 not listed) |
| S02 | drawer open, checkbox, grouping change → the subset is kept; «Fjern utvalget» → full scope, the story list follows, the selection kept |
| S04 | explicit **specification** change → the subset ends |
| S05 | a subset with a story-less (standalone) scenario → listed under «Uten brukerhistorie» with only its own scenarios; scenarios outside the subset never appear |
| S03 | explicit module change → the subset and the old-specification story filter end, the new scope shows all its scenarios, the stored filter no longer holds the subset (no reactivation), the selection kept |

**Results:**
- **The class** (S01–S05 plus the grouping tests): 16 / 16.
- **Full frontend suite (after S01–S03):** 9,798 / 9,798.

## Remaining limitations

- **Not tested as separate cases:** project switching (the existing `OnProjectChanged` already resets the filter to the default), Back/Forward (the subset is not in the URL; the stored filter is rewritten), and cross-module subsets.
- **Not verified in the browser:** this commit is checked by bUnit only; the isolated instance was republishing.
