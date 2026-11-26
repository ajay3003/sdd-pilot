# Scenario grouping, test plan membership and visual status — 2026-11-26

**Scope:** Manual Testing → Finn og forstå (rows, filters, summary). The grouping itself is delivered in `manual-testing-scenario-grouping-hierarchy-2026-11-26.md` (commit 4df809c6) and is reused here.

**Safety:**
- no database change or migration;
- no write on read;
- no Azure DevOps call;
- saved plans are never regrouped.

**Status keys used:** IMPLEMENTED · VERIFIED (tests / browser) · NOT IMPLEMENTED.

## A–C. Audit: the source of truth for membership

| Item | Status | Evidence |
|---|---|---|
| Scenario → saved case → plan | **IMPLEMENTED (server)** | `TestPlanSuggestionService.WorkQueuesAsync` (`backend/.../TestPlanSuggestionService.WorkQueues.cs`) reads every non-deleted plan of the project and of earlier imports of the same application, once per request. It matches each case to its scenario by **stable identity** (`Locate`: source identity, former identities, merged scenarios, an unambiguous renumbering move; a manual case by its `ScenarioKey`). It already returned per-scenario counts (`ScenarioCaseCoverage`: draft/in progress/approved/excluded/historical/archived, active `Plans`). |
| Plan names, case, suite on the row | **MISSING** | Only a case count (`msb-count`) and a status («Under arbeid», «Godkjent») were shown. «Allerede forberedt» was the closest thing to membership. |
| A batch lookup | **IMPLEMENTED** | One request per discovery (1,752 M2LB scenarios); no per-row or per-plan request. |

**Decision:** extend that same pass. There is no new membership engine and no new identity model.

## D–E. Semantics

| State | Source | Shown as |
|---|---|---|
| **Valgt nå** (selection) | browser session (`_selected`) | the checkbox and the selection line («N valgt totalt · M i gjeldende visning · K utenfor gjeldende filter») |
| **Testtilfelle finnes** (prepared case) | `ScenarioCaseCoverage` counts | the status («Under arbeid» / «Godkjent») and the case count, unchanged |
| **I testplan** (membership) | `ScenarioCaseCoverage.Memberships` (stored plans) | **turquoise row** plus the plan name |
| Developer-test coverage | `TechnicalCoveragePresentation` | its own tag, unchanged |
| Execution | (Manual Testing holds no results here) | never shown as membership |

**Membership rules:**
- **Counted:** a *current* case (not Superseded, not Excluded) in a saved plan.
- **Active vs archived:** active = the plan's lifecycle is active; archived plans are kept as `Active = false`.
- **Not membership:**
  - an excluded case (a decision, not membership);
  - a selection;
  - a developer test;
  - a title match.

## F. Data flow

- **The contract:** `ScenarioCaseCoverage.Memberships: List<ScenarioPlanMembership(PlanId, PlanName, Active, CaseId, CaseTitle, SuitePath, Review)>`.
  - `PlanName` is the plan's current name, and `SuitePath` comes from `PlanStructure.PathOf` over the plan's saved suites;
  - the helpers `ActivePlans` (distinct plans), `InActivePlan` and `ArchivedOnly` are derived and not serialized.
- **Where it is filled:** the existing tally loop in `WorkQueuesAsync`. It adds the membership before the archived/active split, so archived history is kept.

## G–H. Visual language

- **One subtle turquoise surface:** tokens `--mt-in-plan-bg/-border/-accent` (light `#e8f8f8` / `#8bd2d0` / `#0f766e`, with a dark variant under `prefers-color-scheme: dark`).
- **The whole row:** `.msb-row.is-in-plan` carries the background plus a 1 px inset border.
- **Precedence:**
  - hover mixes into the turquoise without removing it;
  - the active (open) row keeps its blue left bar on top of the turquoise;
  - selection keeps the checkbox and bold title;
  - source-change, conflict and approval keep their own status badge;
  - approval stays green on the badge only.
- **Colour is never alone:** the plan name or «Ikke i testplan» is always printed.

## I–K. Plan names, list, navigation

- **One active plan:** «▤ Skolenærvær – E2E». The name links to the plan with that case open (`manual-testing/plan?draft={plan}&case={case}`); it is truncated at 28ch with the full name as a tooltip. «Se testplaner» opens the list.
- **Several active plans:** the first name plus «+N plan(er)», which opens the list.
- **The list:** each plan with its suite path («Testgruppe: …»), the case title, «Åpne testtilfelle» (deep link) and the plan link; archived plans are marked «(arkivert)».
- **No membership:** «Ikke i testplan» (muted). **Archived only:** «Kun i arkivert testplan» (neutral; it opens the history).
- **While loading:** nothing is shown while the work view is not loaded, so there is never a false «Ikke i testplan».

## L. Filters and summary

- **«Testplanstatus»** (under «Flere filtre»): I aktiv testplan · Ikke i aktiv testplan · Kun arkivert · I valgt testplan · Ikke i valgt testplan.
- **«Testplan» chooser:** lists the active plans holding scenarios in the list. «I/Ikke i valgt testplan» are disabled until a plan is chosen, never silently the first plan.
- **Composition:** they compose with every existing filter, search and grouping (`ScenarioBrowserPresentation.MatchesPlanStatus` inside `Apply`).
- **Summary:** «N scenarioer · M i aktive testplaner» counts distinct shown scenarios.

## M–N. Grouping and selection

- Grouped rows use the same `Row` fragment, so the colour and plan line appear everywhere.
- Group selection is unchanged.
- A scenario in a plan stays selectable.

## O–Q. Duplicates and existing plans

- **«Legg til i eksisterende plan» (`CreateProject` with `DraftId`)** already skips scenarios the plan holds, by scenario id and source identity (`TestPlanSuggestionService.cs`, `known`/`knownSources`). An accidental duplicate in the same plan is therefore not created.
- **A new plan** may intentionally hold the same scenario (multi-plan membership is supported and shown).
- **NOT IMPLEMENTED:**
  - an explicit «finnes allerede i valgt testplan» dialog before adding;
  - a destination-suite chooser for an existing plan.
- **No regrouping:** grouping and the membership display never change saved plans. A test asserts that computing membership writes no revision.

## R. Source identity

- Membership uses the server's identity match (never titles).
- Cases that cannot be matched are counted in `UnmatchedCases` (existing).
- **NOT IMPLEMENTED:** a per-row «Testplankobling må avklares», because an unmatched case has no scenario to attach to. It is not shown as membership or absence on any row.

## T. ADO

- Unchanged: local membership never implies export.

## U–W. Tests

- **Backend (`ScenarioWorkQueueTests`, temporary database):**
  - no plan → no membership;
  - one scenario in two active plans, with names, case id and the **saved** suite path;
  - read writes no revision;
  - archive → archived history and active membership drops;
  - both archived → archived only;
  - restore plus rename → the current name;
  - an excluded case is not membership;
  - another project never shows this project's plans.
  - **27/27** in the class.
- **Frontend (`ScenarioWorkQueuePageTests` M01–M02):**
  - the turquoise class on the row, the plan name and deep link, «+1 plan», the list with both plans and the suite path;
  - archived-only is neutral with «Kun i arkivert testplan»; no plan → «Ikke i testplan»;
  - the summary counts distinct scenarios;
  - the status filters (in plan / archived / not in plan / in the chosen plan) compose with search.
  - **12/12** in the class.
- **Updated:** two older tests now choose «Ingen gruppering» before checking the flat rank order and the keyboard Home behaviour.

## X. Browser verification against a real saved plan

See the addendum below (isolated instance `v1env11`, disposable database, M2LB import plus seeded plans). It compares every row's turquoise state and plan name with the plans the API reports for that scenario.

## Addendum — Scenario details drawer (first pass, from the «Scenariodetaljer» brief)

- **«Forventet resultat»** is shown once at the top (`mtd-expected`). When the source has no expected result, the drawer says «Forventet resultat må avklares» instead of inventing one.
- **«Testplaner»** (`mtd-plans`):
  - the active plans with their suite path, case title, review state and «Åpne testtilfelle»;
  - archived plans sit behind «Arkiverte testplaner (N)»;
  - «Testplankoblingene er ikke hentet ennå» shows while the work view is loading.
- **One primary action** from the real state: «Åpne testtilfelle» / «Åpne første testtilfelle», or «Forbered testtilfelle». Opening the drawer creates nothing.
- **Readiness** is a compact «Testberedskap N / M kriterier oppfylt · Klar/Ikke klar til kjøring». The criteria appear on demand («Vis kriterier og mangler»).
  - The 1/6 fraction comes from `ScenarioWorkspacePresentation.Checklist(ExecutionReadiness)`: scenario-level questions where an unknown answer never counts as met. It is not coverage and not execution.
- **The row's approach text** now reads «Foreslått: Integrasjon / E2E». It is the `TestDesignAdvisor` suggestion (an execution approach), while the drawer's «Testnivå» is the separate test-level fact. Both are unchanged in substance.
- **Test:** `ScenarioWorkQueuePageTests.D01` (plans, the primary action and its deep link, the expected result once, readiness collapsed, archived only, and no-case «Forbered testtilfelle») — **passed**.
- **Results at commit:**
  - full frontend suite **9,785 / 9,785** before the drawer pass;
  - Discover-related tests **437 / 437** after it;
  - backend `ScenarioWorkQueueTests` **27 / 27**.
- **Browser comparison against real saved plans:** the script `scratchpad/v1/v1_membership.py` is prepared. It creates two overlapping plans plus one archived plan in the disposable instance, reads them back through the API and compares every row. It was **not run at commit time** (the isolated instance was still publishing); its result follows in a later commit.

## Addendum — browser comparison against real saved plans (VERIFIED)

**Setup:** isolated instance `v1env12` (disposable database), M2LB imported; script `scratchpad/v1/v1_membership.py`.
- It created «Verifisering A» (5 scenarios) and «Verifisering B» (5, overlapping 2) through the normal API, plus «Verifisering arkiv» (2, then archived).
- It read every plan back (`GET drafts/{id}`, the stored truth) and mapped the cases to scenarios by source identity, independently of the membership code.
- It then compared every Finn og forstå row (all queues, all kinds, no grouping).

**Results:**
- **Rows and colour:** 2,000 rows. **8 turquoise rows = 8 scenarios in active saved plans. Mismatches: 0** (colour and plan name).
- **Overlap:** both overlapping scenarios show «Verifisering A» plus «+1 plan», and the list holds both real plan names with their suite and case.
- **Archived plan:** its 2 scenarios are neutral (not turquoise) with «Kun i arkivert testplan».
- **Colours:** in plan `rgb(232, 248, 248)`; neutral rows transparent.
- **Filters:** «I aktiv testplan» → 8 rows; «Kun arkivert» → 2 rows.
- **Performance:** the full flat list was ready in 12.3 s (2,000 rows expanded through «Vis flere», headless).
- **Observation:** the suite path for plans created without a structure is the existing area suite named by the specification path. That is existing plan data, shown as stored.
