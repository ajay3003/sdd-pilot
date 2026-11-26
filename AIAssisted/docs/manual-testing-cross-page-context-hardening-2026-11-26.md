# Manual Testing — cross-page context, selection and navigation hardening (2026-11-26, P0)

Commit: `20e7cd94`.

## Reported defect
In Finn og forstå: scenario A → prepare / plan it → change module and specification → scenario B → «Forbered testtilfelle». Planlegg og klargjør then opened with the PREVIOUS US/AC/case.

## Exact root causes (two, both confirmed in code and tests)

1. **The intent was never transferred.**
   - `ManualTestingDiscover.PrepareOnlyAsync(id)` added the scenario to the persisted multi-selection, which still held A because selections deliberately survive filter and scope changes. It then navigated to `manual-testing/plan` with **no scenario in the address**.
   - Planlegg (`LoadAsync` → `OpenPlanAsync`) opened the most recently updated plan and restored `_selectedId` or that plan's first case, so A's case won.
2. **Non-scoped scenario identity within one module.**
   - The discovery key was `scenario:{module}|{scenario id}` (and `requirement:{module}|{FR id}`).
   - US1/AC1 of two specifications **in the same module** therefore got the **same candidate ID** and were merged by `Finalize`. Anything keyed by that ID (cases' `Source.ScenarioId`, plan items, the prepare action) could not distinguish B from A.
   - Reproduced by `CrossSpecScenarioIdentityTests` before the fix: the same `tsc-…` ID for both.

## State ownership and reset policy (after)

| State | Owner | Transfer | Reset |
|---|---|---|---|
| Project / module / specification | Shared explorer selection (scope picker) | Shared service | A module change clears an incompatible specification. Finn og forstå clears its open scenario on any scope change. |
| Open scenario in Finn og forstå | Finn og forstå (session work context) | — | Dropped when not in the new population |
| Multi-selection | Finn og forstå (session, per project) | «Forbered testtilfeller →» (multi) | Survives filters by design; shown as «N valgt totalt · M utenfor gjeldende filter» |
| **«Forbered testtilfelle» intent** | **The address**: `?scenario={candidate id}` (+ `&draft=` for the active plan holding it) | URL, so Back/Forward and refresh are exact | Not stored anywhere else |
| Plan and case in Planlegg | Planlegg | `?draft=` / `&case=` | With an intent: only a case of that scenario. Opening another scenario's case ends the intent. A newer plan load discards an older answer (`_planSeq`). |

## Rules implemented in Planlegg (`ApplyIntent`)
1. The scenario must exist in the project's discovery. Otherwise: «Scenarioet finnes ikke i prosjektets gjeldende scenarioer. Ingen testtilfelle er åpnet.»
2. Plan: an explicit `draft`, else the most recently updated plan (as before). The banner says whether the scenario is in it.
3. Cases of the scenario: its own case first, then cases written under it, in plan order. With several: «N testtilfeller hører til scenarioet; det første er åpnet».
4. Planned without a case: «Klargjør AC» for the scenario. No case is created.
5. Not in the plan: «Ikke i testplanen «X». Ingenting er lagt til automatisk.» plus an explicit «Legg til i testplanen» (scenario-only, revision-checked).
6. Never a remembered or first case of another scenario. Nothing is saved or created by navigation.

## Identity fix (backend)
- `TestScenarioDiscoveryEngine.ScopeCollidingSpecificationKeys` (start of `Finalize`): when one key is claimed by more than one specification scenario (different source identities) or requirement (different files), each claimant gets `…|{spec file}|{id}` and its own candidate ID.
- Non-colliding keys keep their IDs exactly, so existing plans keep their links. Source identities are unchanged.
- Colliding scenarios that were merged before now become separate. A plan case linked to the old merged ID keeps its own data and its source identity, and may be reported as changed against the source.

## Backend validation (reads and writes)
- Plan additions take candidates only from a discovery found **for the same project** (`discoveries.Find(key, id)`), filtered by `ProjectKey`. Unknown IDs leave nothing chosen (400 `no-scenarios`). A plan of another project gives 404 `draft-not-found`.
- Review, runs, duplicate and add-under-scenario refuse a case or scenario not in the plan (404 `case-not-found`, 400 `scenario-unknown`).
- Plan save applies edits only to cases the plan already holds. A save cannot add a case or change a case's source or scenario.

## Cross-page audit matrix

| Transition | How context travels | Verdict |
|---|---|---|
| Testoversikt → Finn og forstå (drill-downs) | Writes the filter and resets the work context, then navigates | OK |
| Testoversikt → Finn og forstå (module row) | Selects the module in the shared scope, then navigates | OK |
| Finn og forstå → Planlegg («Forbered testtilfelle») | **Was implicit → now `?scenario=`** | **FIXED** |
| Finn og forstå → Planlegg (multi «Forbered testtilfeller →») | Multi-selection banner «N scenarioer valgt» | OK (intended) |
| Finn og forstå → Testplaner / plan membership | `?draft=&case=` links | OK |
| Planlegg → Finn og forstå («Vis i Finn og forstå») | Writes the work context (story, scenario), then navigates | OK |
| Planlegg → Testplaner | Ordinary link (restores the list) | OK |
| Testplaner → Planlegg («Åpne testplan») | `?draft=` | OK |
| Testplaner → execution | `?plan=` | OK |
| Project switch (any page) | `OnProjectChanged` clears workspace, plan, form, selected case and pending action | OK |

## Regression tests
- **Backend:**
  - `CrossSpecScenarioIdentityTests`: identical US1/AC1 in the same module and in different modules give distinct candidate IDs, source identities, keys and story keys.
  - `CrossSpecScenarioIdentityStabilityTests`: a non-colliding key keeps its ID.
  - The existing deduplication test still passes.
- **Frontend:**
  - `ManualTestingPlanIntentTests` X01–X04: B opens as B and never shows A's case; nothing is added or saved by itself; «Legg til i testplanen» adds exactly B as scenario-only; A opens its own case; an unknown ID opens nothing.
  - `DiscoverWorkspaceTests`: the address carries `?scenario=`.

## Browser verification
v1env19, the exact journey with IDs checked in the address and on the page. See the «Browser» section appended below.

## Remaining risks
- The multi-selection still survives scope changes by design. It is shown with its count, and is not used by the single-scenario action any more.
- A colliding key whose candidate ID changed: an existing plan case linked to the old merged ID is matched by its source identity in source-change checks but not by candidate ID.
- Slow-response ordering is covered for plan loads, not for every API call on every page.

## Browser (isolated v1env19, M2LB, committed tree `20e7cd94`) — VERIFIED
| Check | Result | Observation (actual ids) |
|---|---|---|
| P0-A-intent | PASS | Finn og forstå scenario A → `/manual-testing/plan?scenario=tsc-84470491ee7644e2`, banner `data-scenario` = A |
| P0-B-different | PASS | module changed (project level → AutorisasjonService); B = `tsc-38684830d49e4e99` (AC 3 · US1) ≠ A |
| P0-B-opens-B | PASS | `/manual-testing/plan?scenario=tsc-38684830d49e4e99`, banner = B, «Ikke i testplanen», no editor; A never shown |
| P0-back-forward | PASS | history restores `?scenario=B` exactly |
| P0-refresh | PASS | reload keeps B |
| Console | PASS | no errors |

Limitation of this run: the script could not give A a case first (A's case title «None»), so «A's case is not selected» is proven by the component test X01 (A's case present in the plan), not by this browser run. Screenshots: `scratchpad/v1/v1env19/shots/p0-a-planlegg.png`, `p0-b-planlegg.png`.
