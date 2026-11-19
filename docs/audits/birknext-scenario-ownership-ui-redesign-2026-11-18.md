# Scenario discovery, workspace ownership and the Plan & prepare redesign (2026-11-18)

Branch `008-traceability-first`, starting HEAD `e30b7728`. Local commits only; nothing pushed. Isolated Release instance (API :5097,
web :5197), disposable databases, synthetic archives; no external system contacted and no imported code executed. The real M2LB inventory was
read once, read-only (`BEGIN READ ONLY` on the user's database), to reconcile the counts — nothing was written there. `appsettings.Local.json`,
`AIAssisted/testdata/`, the Kode67 gap register and the unrelated audit drafts were not modified.

## 1. The problem

Manual Testing → Plan & prepare showed 668 scenario candidates for M2LB in one long list: hundreds of API / GraphQL operations next to the
business scenarios, four large step cards, a large evidence block above the work area, repeated headings and counters, and no indication of
where a candidate should be tested. The success criterion (the user's): a manual tester finds the right 10–20 scenarios for an assignment
without losing access to the technical evidence behind them — not a smaller number.

## 2. The actual M2LB inventory (stored discovery result, 2026-11-18 21:22, `M2LB.zip`, rules `tsd-1`)

| Producer (capability) | Candidates | What they are |
|---|---:|---|
| Specification (`specification`) | 130 | 65 acceptance scenarios + 65 requirements no scenario names (Module SkoleService) |
| System Understanding guide (`system-guide`) | 21 | 18 business functional + 3 business end-to-end (guide "Skolenærvær", revision 1, draft) |
| UI source (`ui-source`) | 59 | 48 UI behaviours (forms, search, actions) + 11 "page opens" checks; 61 pages analysed, 13 with a route |
| API / contract (`api-contract`) | 439 | 388 operation entries + 51 authorization checks; 388 operations in 40 contracts in 36 files |
| Integration journeys (`integration-journeys`) | 19 | Skolenærvær test-pack scenarios (0 declared journeys) |
| Existing tests (`existing-tests`) | 0 own | 2 563 xUnit tests, 1 206 reference a requirement; attached to 50 candidates |
| **Total** | **668** | |

Reconciliation:

* **Operation entries vs operations.** The 388 operation entries are **262 distinct operations**; 62 operations are declared in more than one
  file. The same GraphQL schema exists up to four times (specification contract, `autodoc/schema.graphql`, the frontend client's copy, the
  contract-test `schema.snapshot.graphql`), e.g. `AutorisasjonService` 42 operations × 4 files. Each file stays a candidate (they are different
  evidence), and the copies are now shown as *Same operation in* (§6).
* **Manual vs technical** (§4): **199** for manual testing (65 acceptance scenarios, 65 requirements, 21 guide scenarios, 48 UI behaviours) and
  **469** technical (388 operation entries, 51 authorization checks, 11 page checks, 19 integration journeys). 668 candidates are not 668
  necessary manual tests.
* **Needs clarification / attention:** 108 candidates have no expected result in the evidence (51 authorization checks without a documented
  401/403, 46 UI candidates, 8 pack scenarios, 3 guide scenarios); 120 need attention in total (108 unclear expectation, 5 test prerequisite
  missing, 3 verification mechanism missing, 1 implementation evidence missing, 3 possible duplicate) — 51 of them in the manual view.
* **Missing prerequisites:** 7 candidates with a classified *Test prerequisite missing* (pack scenarios); 21 manual and 285 technical
  candidates state Test Data needs.
* **Existing tests:** 50 candidates have an explicitly associated source test (49 of them manual: specification candidates via `FR-` traits).
* **Overlap:** 8 candidates carry a *possible duplicate* (FR-001 named by both the specification and the LandingPage UI candidates); 6
  requirements are named by more than one candidate. Existing prepared manual cases: none in that result's project at the time of reading.

## 3. Scenario producer audit

| Producer | Authoritative service | Identity (canonical key) | Expected behaviour | Notes |
|---|---|---|---|---|
| Specification | `TestScenarioDiscoveryEngine.FromSpecification` over the browser's parsed specification (`CoverageWorkspaceEvidence`) | `scenario:{module}\|{id}`, `requirement:{module}\|{id}` | the scenario's *Then* / the requirement statement | Requires the module's specification to be chosen in Specification Explorer when a project has several modules |
| System Understanding guide | `FromGuide` over `SystemGuideService.RecognisedGuidesAsync` (application source roles only) | `guide:{guide}/{scenario}` | the guide's expected outcome (not for *needs clarification*) | E2E when the guide's steps cross more than one component |
| UI source | `FromUi` over the Critical E2E `SuggestionEngine` (Razor/HTML) | `ui:{suggestionId}` | only when the analyzer states it | a page that only opens is UI smoke, never business E2E |
| API / contract | `FromContracts` over Source Analysis contract evidence (OpenAPI, GraphQL schema, GraphQL operations) | `contract-op:{file}#{op}`, `contract-auth:{file}#{op}` | documented 2xx / declared result type; 401/403 for authorization | capped at 600 per discovery |
| Integration journeys | `FromDeclaredJourneys` (declared, ≥ 2 components) + `FromPackJourneys` (recognised packs) + run evidence | `journey:{pack}:{journey}[:{scenario}]` | stated by the pack | association with the project is by source roles → always *needs review* |
| Existing tests | `TestLinks` over the xUnit inventory | — (associations only) | — | attached by explicit requirement reference, never by name |

All candidates are finalised in `Finalize` (dedup only on the same candidate id; possible duplicates noted, never merged). Candidate ids are
project-scoped and independent of the snapshot. There is one registry: no second scenario engine or store was created by this task.

## 4. Workspace ownership model (new, `shared/ScenarioOwnershipContracts.cs`)

`ScenarioOwnership.Of(candidate)` classifies from what the producer recorded — its capability and the candidate type it assigned — never from a
title or file name. It returns the purpose, the producer's own kind of evidence, a primary and an optional secondary workspace, the specialist
module and the basis (shown in the candidate's details).

| Kind of evidence | Purpose | Primary | Secondary | Specialist module |
|---|---|---|---|---|
| Acceptance scenario, requirement without a scenario | Business functional | Manual Testing | — | — |
| Guide scenario | Business functional | Manual Testing | — | — |
| Guide scenario through several systems | Business end-to-end | Manual Testing | Technical Testing | Integration Testing |
| UI behaviour (form, search, validation, action) | UI functional | Manual Testing | Technical Testing | Frontend Quality Review |
| UI journey | Business end-to-end | Manual Testing | Technical Testing | Critical E2E Regression |
| Page check (page opens / navigation) | Frontend technical | Technical Testing | Manual Testing | Critical E2E Regression |
| API operation | API / contract technical | Technical Testing | Manual Testing | Contract Testing |
| Authorization check | Security / authorization technical | Technical Testing | Manual Testing | API Quality Review |
| Integration journey (declared / test pack) | Integration functional | Technical Testing | Manual Testing | Integration Testing / the pack's page |
| Unknown producer | Unclassified — needs review | none | — | — (listed under *Needs attention*) |

Classification never moves a saved case, never changes a candidate (id, content and fingerprint stay as discovered) and never hides one: every
candidate stays in *All candidates*, and any technical candidate can be prepared as a manual case on purpose.

## 5. Plan & prepare — before and after

Before (`before-*.png`): a long header lead, a context card, a large open "Evidence in this project" block, four large step cards with an
*Open* button each, a separate tab row, the step purpose, a section heading repeating the step name with its own description, six filters in one
or more rows, and one list of every candidate.

After (`after-*.png`):

* **Header** — one-line lead; project selector, source snapshot, test boundary and existing plan in one compact context row.
* **Summary** — five numbers: discovered, for manual testing, technical (with "N operation entries = M distinct operations"), cases in the plan,
  need attention. The per-source detail is behind a closed *Evidence sources* disclosure.
* **Stepper** — one row of four numbered steps with their stored status (*Prepare test cases first.* when waiting); every step opens and a
  waiting one explains itself in its area; the current step is marked (`aria-current="step"`). Saved snapshots, Compare deliveries and Export sit
  beside it. The step heading and its one-line purpose are one line; the repeated section heading is gone.
* **Views** — *Manual testing* (default), *Technical candidates*, *All candidates*, *Needs attention*, each with its count and the number of
  selected candidates it holds; the view is in the address (`?view=technical`). All four read the same in-memory inventory (classified once per
  discovery result).
* **Adaptive default** — a project with only technical candidates shows "No business-focused scenarios identified. You can prepare technical
  candidates manually or open Technical Testing." with *Show technical candidates (N)* and *Open Technical Testing*.
* **Filters** — search, area, source and group by in one row; type, existing tests and *only needing attention* under *More filters*; group by
  area, type, source, kind of evidence or requirement.
* **Rows** — checkbox and title with *Details*, one-line purpose, chips: kind of evidence, type, area, *Technical Testing* (outside the manual
  view), *In the plan*, number of source tests, the attention label. Technical ids and paths are in the details only.
* **Details** — where to test (workspace, purpose, basis, handoff link), existing case (*Open existing case*, *Write a separate case by hand*),
  expected behaviour, steps, requirements, Test Data, existing tests (source tests, not executed manual tests), source, evidence, *Same operation
  in*, related candidates, what is still open (classified), sources checked.
* **Preparation** — the sticky bar shows selected, *N not shown here*, *N already in the plan*, *N may overlap existing cases*. One ordinary
  case is prepared at once; 5 or more, or 2 or more with technical, overlapping, unclear or prerequisite-missing candidates, show a preview first
  (types, already in the plan, technical, overlaps, missing expected results, missing prerequisites) that takes focus; *Back to the selection*
  keeps the selection; only the explicitly selected ids are sent.

## 6. Duplicate and related candidates

* **Genuine duplicates:** none were merged. Candidate ids are unique per producer and source identity.
* **Same operation in several files:** a structural identity — GraphQL operation + input + declared result; REST method and path + the
  contract's own type, title and version. Two services' `GET /health/live` stay two endpoints; two copies of one document are one operation.
  Shown as *Same operation in N other contract files — related, kept separate*, counted in the summary and in the preview's overlap count.
  (Found in browser acceptance: a first version keyed REST operations on the first path folder, which is the archive's root folder in some
  archives and linked two services' endpoints — fixed and pinned in `ScenarioOwnershipTests`.)
* **Same requirement from different sources:** the existing *possible duplicate* note (shared requirement id, different producer) is shown as
  *Related candidates* and counted as a possible overlap when one of them is already in the plan.
* **Business E2E vs its API operations:** related, not duplicates; no relationship is inferred between them because no grounded link
  (requirement id, reviewed association, operation id) exists in the evidence. Title similarity is never used.
* **Reviewer actions** (confirm / reject / keep separate / reverse): the existing durable relationship review is Relationship Review
  (requirement ↔ scenario associations, `correlation-review`) and Test Coverage's association review; no competing registry was added. A
  persisted "keep separate / confirmed same" decision for operation copies does not exist yet (limitation).
* **Existing cases:** a candidate in the plan shows *In the plan*, *Open existing case*, and *Write a separate case by hand* (prefills a
  hand-written case title; not linked to the candidate). Re-selecting it adds nothing.

## 7. Technical Testing integration

No second candidate list. Technical Testing › Discover & assess shows **Technical candidates from scenario discovery**: the technical share of the
project's last stored discovery per specialist module (Contract Testing, API Quality Review, Critical E2E Regression, Integration Testing / the
pack page) with kinds, counts and distinct operations, a link to each module and *Browse or prepare them in Plan & prepare*
(`?area=choose&view=technical`). It only reads the stored result (never discovers) and only for the selected project.

## 8. Cross-workspace navigation

| From a candidate | Link | What the destination reads |
|---|---|---|
| API operation | `api-quality-review/contracts` | the selected project (global selection); its source contract list; the contract file is named in the link text |
| Authorization check | `api-quality-review` | selected project, active Target Environment |
| UI behaviour | `frontend-quality-review` | selected project |
| Page check, UI journey | `critical-e2e-regression` | selected project |
| Declared integration journey | `technical-testing/integrations?stage=prepare&journey={pack}:{journey}` | `?journey=` (verified: `IntegrationTestingPresentation.Journey(id)`) — project keys may contain `:` (handled) |
| Test-pack scenario | `{pack page}?tab={journey}` (`extensions/skolenaervaer?tab=rapportmottak`) or Integration Quality Review | `?tab=` (verified on the pack page) |
| Technical Testing → Plan & prepare | `test-plan-suggestions?area=choose&view=technical` | `?view=`, `?area=` |

Contract Testing has no operation-level deep link yet; the queued Contract Testing task adds "Open in Contract Testing" with project, snapshot,
contract and operation. No query parameter is passed that a destination does not read.

## 9. Evidence, clarification and coverage semantics

Unchanged and verified: gaps stay classified (expectation, implementation, prerequisite, verification, association, runtime, contradiction,
not evaluated); a candidate is never ready because related source exists; source tests are shown as source tests, not as manual execution;
preparing creates reviewable drafts only (nothing approved); a saved case is never an executed test; Test Coverage counts execution only from
runs. Case preparation reuses `CreateProjectAsync` / the saved-case model, revisions, requirement links, autosave guard, version history,
execution links and the Azure DevOps export unchanged.

## 10. Performance (bUnit, M2LB-shaped synthetic inventory, `ScenarioInventoryPerformanceTests`)

| Candidates | Classify | First render | View switch | Search | Select visible | Preview | Group expand | Details |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 50 | 0.1 ms | 7 ms | 1 ms | 8 ms | 6 ms | 7 ms | 15 ms | 1 ms |
| 500 | 0.5 ms | 7 ms | 2 ms | 10 ms | 17 ms | 7 ms | 2 ms | 2 ms |
| 1 000 | 1.1 ms | 8 ms | 2 ms | 12 ms | 23 ms | 8 ms | 11 ms | 2 ms |
| 5 000 | 13 ms | 307 ms | 14 ms | 72 ms | 61 ms | 26 ms | 5 ms | 15 ms |

Rows render 60 at a time (*Show more*); the inventory is classified once per result; a view, filter or search change never calls the backend
or re-runs discovery (verified: one `LastDiscoveryAsync`, zero `DiscoverAsync`). Test-process managed heap 78–89 MB.

## 11. Browser acceptance

Fresh Release publish (API + Web, tag 61; web asset integrity: 86 of 86 assets match their recorded hashes — the checker's one "missing
hash" line is the manifest's own aggregate `hash` field, not a file), disposable database `birknext_own3_20261119` (template copy), API :5097,
web :5197, repository network guard (ports 5000/5173 and every external host blocked). Synthetic archives: **SchoolHub** (M2LB-shaped: two
modules, a specification with 8 acceptance scenarios and 10 requirements, three Razor pages, one GraphQL schema in three files — service,
frontend client, contract-test snapshot — two services' OpenAPI documents both with `/health/live`, xUnit tests with `FR-` traits) and the
earlier `tps-api` (API only), `tps-docs` (documentation only) and `tps-empty` archives. Script `acc/own-accept.cjs`.

**Result: 50 of 50 checks passed** (a first run on the previous publish: 47/49 — the REST identity defect of §6 and two script issues).

| # | Step (brief §40) | Result |
|---|---|---|
| 1–3 | Import SchoolHub; the tester chooses the SchoolService module in Specification Explorer; Manual Testing → Plan & prepare | pass |
| 4 | Totals: 53 discovered = 24 manual + 29 technical; evidence-source counts add up | pass |
| 5 | Five-number summary; *Evidence sources* closed; "24 operation entries = 12 distinct operations" | pass |
| 6 | Compact stepper opens every step, marks the current one; one row (60 px) at 1440 px | pass |
| 7–9 | Manual default; acceptance scenarios, requirements, UI behaviour shown; no API operation, authorization or page check | pass |
| 10–11 | Technical view lists operations, authorization and page checks; `?view=technical` in the address | pass |
| 12–13 | Details: where to test; *Same operation in* for the GraphQL copies; two services' `/health/live` **not** linked; handoff to `api-quality-review/contracts`, which opens for SchoolHub | pass |
| 14–16 | Back: the technical view and the stored discovery (no re-run); *All candidates* = 53 | pass |
| 17 | Filter by source (Specification) | pass |
| 18–20 | Three business scenarios selected; a hiding filter keeps them selected (*3 hidden by filters*); view switch keeps them; keyboard view switch | pass |
| 21–23 | Details show expected behaviour and `spec.md#…` evidence; no saved case yet | pass |
| 24–27 | Prepare → exactly 3 cases; reload: 3 in the plan, *In the plan*, *Open existing case*; re-selecting adds nothing; a bulk selection (21) shows the preview (kinds, 3 already in the plan, 6 without an expected result) and *Back* creates nothing | pass |
| 28–29 | Test Coverage: no execution from prepared cases | pass |
| T1–T2 | Technical Testing › Discover & assess: Contract Testing 24 · Critical E2E 3 · API Quality Review 2 = the 29 technical candidates; link to `?view=technical` | pass |
| 41 | API-only (Parcelo): "No business-focused scenarios identified…" with *Show technical candidates (6)*; documentation-only (Roomly): 7 manual, 0 technical; empty (Opaque): nothing found, hand-written case possible; no SchoolHub candidate or plan leaks into another project | pass |
| 42 | 1440, 1280, 1024, 768, 390 px: no horizontal scroll; the preparation bar is sticky down to 768 px and static at 390 px (never covers the list on a phone) | pass |
| — | No console or page errors; no prohibited network destination | pass |

Before / after (same SchoolHub project, same plan, same API; the "before" frontend is the previous Release build, tag 54):
`scenario-ownership-2026-11-18/before-1440-choose.png`, `after-1440-choose.png`, `before-390-choose.png`, `after-390-choose.png`, and
`after-1440-technical.png`, `after-1440-preview.png`, `after-1440-technical-testing.png`. The first candidate row moved up from 1 260 to
1 053 px at 1440 px and from 2 421 to 2 130 px at 390 px; the four step cards (≈ 290 px) became one 60 px row, and the manual view lists 24 of
53 candidates instead of all 53. (After the screenshots, one CSS-only correction: the "operation entries" note was rendered at the summary
number's size; it now uses the small note style. The regression below runs on that final code.)

## 12. Tests and regression

On final code (focused, guarded, sequential):

| Run | Passed | Failed | Skipped |
|---|---|---|---|
| Backend `TestPlanning` (incl. new `ScenarioOwnershipTests`: producer classification, guide E2E, pack journeys, unknown producer, GraphQL copies vs REST services, classification changes nothing) | 118 | 0 | 0 |
| Frontend Plan & prepare, Requirement linking, Manual Testing, Technical Testing, User Guide, Documentation, Project selector, new view / handoff / preview / rediscovery / stepper tests, `TechnicalCandidateSummaryTests`, `ScenarioInventoryPerformanceTests` (50–5 000) | 393 | 0 | 0 |
| API + Web Release publish (tag 61) | exit 0 | — | — |
| Browser acceptance (§11) | 50 | 0 | 0 |

Changed existing tests (deliberate behaviour changes, no assertion removed): the unrelated-project and type-filter tests now expect the manual
default (3 of 4 rows, the API operation under *All candidates*) and confirm the preparation preview for a selection that includes a technical
candidate; the "Open now" test now reads the step's own name on the compact stepper.

**Full frontend suite and backend partitions 1–6: started on the final code (sequential, memory-guarded) and still running at commit time.**
Their results are added in a follow-up commit; until then they are *not run to completion*, not passed.

## 13. Remaining limitations

* Specification grouping: the parser gives no user-story heading per acceptance scenario, so specification candidates group by module, kind of
  evidence or requirement, not by user story.
* No persisted review decision for operation copies (*confirmed same* / *keep separate*); the relationship is shown, not decided.
* No grounded link between a business E2E scenario and the API operations it uses; none is guessed.
* Contract Testing has no operation-level deep link yet (queued task).
* The specification is read from the module chosen in Specification Explorer; a multi-module project without a chosen module discovers no
  specification candidates (the evidence sources list says so).
* Classification is per producer; a future producer is *Unclassified — needs review* until the rule names it.

## 14. Local commits

One local commit on `008-traceability-first` for the implementation, tests, User Guide and this document (starting HEAD `e30b7728`); a follow-up commit records the full regression. Nothing pushed.
