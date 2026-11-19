# M2LB manual testing — end-to-end audit, fixes and verification (2026-11-19)

## Scope

The manual tester's path in BirkNext, run end to end with real data: **Import project → Discover & understand → Discover scenarios → Prepare
test cases → Edit, save, approve → Export test cases (file)**. Agreed boundaries: no contact with M2LB, MU, MM, Altinn, TT02 or any test
environment; the Azure DevOps step is tested as **file export only** (no Azure DevOps write). Manual execution is not in BirkNext (removed in
d1d174ea; Azure DevOps Test Plans executes).

- **Data:** `M2LB (8).zip` (Downloads, 18 Nov 2026, 10.2 MB, 3 008 entries, fingerprint 5353a7b16125), read only.
- **Installation:** Release publish of the working tree, `ASPNETCORE_ENVIRONMENT=Production` with Local Tester Mode, API :5097, web :5197, an
  **empty** disposable database per run (`birknext_mt*_20261119`), browser network guard (only :5097/:5197 allowed). Ports 5000/5173 and
  existing databases untouched.
- **Driver:** a Playwright script (scratchpad `acc/m2lb-mt-e2e.cjs`, 45 checks) and, in the repository, two Playwright tests:
  `Tests/Workflows/ManualTestingWorkflowTests.cs` (synthetic project) and the opt-in `Tests/Workflows/RealM2lbManualTestingTests.cs`
  (real archive).

## What is available (audit)

| Step | Available | Evidence from the M2LB run |
|------|-----------|----------------------------|
| Import | ZIP preview, import with progress, result with notes per role | 2 min 10 s–2 min 50 s; Specification 63, Data Model 44, Task 52 documents kept; Research 99 not selected; 11 ambiguous; snapshot partial (16 files in an unsupported language); Test Data XML fixture not applicable (no XSD with that contract name) |
| Manual Testing home | Project, saved plans, one next step from saved records | Next step follows the records: import → choose specification → discover → export |
| Discover & understand | Requirements with test design state, stored discovery by type, evidence kinds | 65 requirements for SkoleService; 668 suggestions by type; 108 need clarification; other open points by kind |
| Discover scenarios | Six evidence sources | Specification 130 (SkoleService), System Understanding 21, UI 59, API 439 (388 operations, 40 contracts), journeys 19, existing tests 2 563 (associations only). 668 candidates, 199 manual, 469 technical; < 2 s |
| Prepare | Selection across filters, preview with kinds/missing expected/overlaps | 6 prepared: 2 acceptance scenarios, 2 UI behaviour, 2 guide scenarios; 3 start as *Needs clarification* |
| Edit / save / approve | Steps, expected results, preconditions, requirement and Test Data links, version history, review decisions | Norwegian text kept through save, reload and export |
| Hand-written case | Title → Add test case | Added as a draft with no steps (cannot be approved until it has steps) |
| Export | XLSX/CSV for Azure Test Plans + mapping report; per-case readiness; history | Valid XLSX (one sheet, no formulas), CSV with BOM and identical rows, mapping report with source specification and acceptance scenario |

## Gaps found and fixed

| # | Found in the run | Fix |
|---|------------------|-----|
| 1 | The import result's next steps had no way into Manual Testing, and *Recommended Workflow* pointed at the retired `/getting-started` | **Plan manual tests** (→ Manual Testing) and **Project Dashboard** (→ `/dashboard`) |
| 2 | M2LB is a monorepo (63 specification documents in 14 modules). Nothing is chosen automatically, so Discover said *This project has no selected specification* and discovery found **no business scenarios from the specification**, while the home still said *Discover what to test* | Discover names the counts and the choice (*This project contains 63 specifications in 14 modules and none is chosen yet…*) with **Choose the specification**; the home's next step is **Choose the specification** until one is chosen (prepared work is never held back). Discovery with SkoleService chosen: +130 business scenarios |
| 3 | Requirements on Discover read `FR-001 **FR-001**: The system MUST …` (Markdown, id twice); the same text fed the requirement-link search | Requirement options carry the plain statement (`FR-001 The system MUST …`), the rule discovery already used |
| 4 | **Approve** only needed a case-level expected result; the export also needs at least one step and an expected result per step. Two approved M2LB cases (a step added without an expected result; a guide scenario whose steps state none) were refused only at the export | Approve follows the export's rules and says what is missing under the buttons (*To approve: step 2, 3, 4 need an expected result.*) |
| 5 | Wording: the button said *Accept for preparation*, the case chip and stats *Accepted for preparation*, while the home and export say *approved* | *Approve* / *Approved* everywhere (also *Approved for an earlier scenario revision* in requirement coverage) |
| 6 | Step 4 of Plan & prepare said *Not saved* for a saved plan (it meant: no snapshot) | *Plan saved (revision n) · no snapshot yet* |
| 7 | The old *BirkNext JSON of a saved snapshot* export sat under the Azure DevOps export as a second, similar-looking export | Collapsed as *Other export: BirkNext JSON of a saved snapshot* |
| 8 | Leftover execution wording: *test runs stay in run history* (version history, restore), *manual runs* in the documentation catalog | Removed; the catalog says test cases are executed in Azure DevOps |
| 9 | The repository's `ManualTestingWorkflowTests` still drove the removed execution page (`tps-run-case`, `mte-*`) | Rewritten: discover → prepare → edit → save → reload → approve → home → export → read the XLSX back |
| 10 | No repeatable real-data test | `RealM2lbManualTestingTests` (opt-in, `BIRKNEXT_M2LB_MANUAL_ARCHIVE`, module via `BIRKNEXT_M2LB_MANUAL_MODULE`, default SkoleService) |
| 11 | No tester guide for this path | `docs/guides/manual-testing-m2lb.md`; User Guide → Manual testing gains the monorepo and approve rules |

## Verification

| Run | Build | Result |
|-----|-------|--------|
| 1 | before fixes | 18/25 — gaps 1–2 found; script could not see grouped candidates |
| 2 | fixes 1–2 | 30/34 — specification choice works (65 requirements, 130 business scenarios); gap 3 found; script order issues |
| 3 | fixes 1–3 | 40/44 — gap 4 found (approved cases refused by the export) |
| 4 | fixes 1–8 | **45/45** — two approved cases exported (3 rows), Norwegian step intact, 0 blocked requests, 0 browser or server errors, no overflow at 390 px |
| 5 | final | **45/45**, plus the repository's `ManualTestingWorkflowTests` (synthetic project) **passed** on the same installation (32 s) |
| 6 | final, fresh empty database | the repository's real-data test `RealM2lbManualTestingTests` on `M2LB (8).zip` **passed** (2 min 39 s): import → *Choose the specification* → SkoleService → requirements → discovery (specification available) → prepare 3 → Norwegian step, save, approve → home offers the export → XLSX read back |

How to rerun the real-data test: start an isolated BirkNext with an empty database, then set `BIRKNEXT_PLAYWRIGHT_FRONTEND_URL`,
`BIRKNEXT_PLAYWRIGHT_BACKEND_URL`, `BIRKNEXT_PLAYWRIGHT_LAUNCH_CODE_FILE` and `BIRKNEXT_M2LB_MANUAL_ARCHIVE` (optionally
`BIRKNEXT_M2LB_MANUAL_MODULE`) and run `dotnet test BirkNext.Web.PlaywrightTests --filter FullyQualifiedName~RealM2lbManualTestingTests`. Without
the variables it is skipped with that reason.

Unit/component tests: frontend Plan & prepare, Manual Testing, requirement linking, coverage, traceability, guide and import suites 501/501;
new tests `SpecificationChoiceTests` (3), `Approve_follows_the_export_rules_and_says_what_is_missing`, the monorepo next-step test; backend
`TestPlanning` + `RequirementCoverage` 107/107.

## Still open (not changed)

- **Requires a person's choice in a monorepo.** By design BirkNext does not choose one of several specifications; a tester testing several
  services chooses each in turn. Discovery must be run again after changing the choice (*Discover again*).
- Guide scenarios often list steps without expected results (Skolenærvær: 3 of 4 steps); they still need a tester's input, but are now named
  before preparing (see the follow-up below).
- **Importing the file into Azure DevOps** was not tested (agreed scope: file export only). The mapping report and the in-page *How to import*
  steps describe it.
- `manual_test_runs` / `manual_run_attachments` remain dormant tables (separate cleanup decision; untouched).

## Follow-up the same day (gaps closed)

| # | Gap | Fix |
|---|-----|-----|
| 12 | Scenario selection lost on reload | Kept in the browser tab (sessionStorage, per project; never sent, nothing prepared by it); only ids the current discovery still has are restored, with a message |
| 13 | Export field values lost on reload unless saved as the project's settings | Kept in the browser tab with a note (*kept from before the page was reloaded* / *differ from this project's export settings*); saving or changing back clears it; remembered Azure DevOps ids always come from the project |
| 14 | Hand-written cases start without steps and the next action was unclear | The case opens with focus on its first step, a *No steps yet* line, and the new-step row takes the step's expected result too |
| 15 | Incomplete steps in the evidence surfaced only after preparing | Candidates name the gap (*No steps in the source*, *2 of 3 steps without an expected result*) and the preparation preview counts them (project discovery only; guide-mode candidates do not carry their steps) |
| 16 | A chosen module with several specifications still read *This project contains 63 specifications in 14 modules* | Names the module: *Module SkoleService has 3 specifications* (or *n features, each with its own specification*) and says to discover again for the next one |

Each prepared case keeps the specification file it came from (export *Description* column, mapping report), so cases from several
specifications can share one plan.

Verification: frontend 9 234/9 234 (new page, export wizard, preview and specification-choice tests). Playwright on an isolated Release
build with an empty database: `ManualTestingWorkflowTests` passed, and the new `ManualTestingReloadTests` passed (selection kept on reload,
hand-written case focus and a step with its expected result, export field kept on reload and cleared after saving). Backend partition 6
rerun: 2 723 passed, 1 failed (`CompositeSuitePostgresTests.Failures_are_reported_as_what_they_are_never_as_a_pass`: *Failed* instead of
*Incomplete* under load, in integration suites, unrelated to manual testing); it passed in isolation (26/26 in its class). The five earlier
timeout failures did not recur.

## Document explorers on M2LB: long specifications and several documents per module

A browser probe went through every explorer (Specification, Plan, Task, Data Model, Constitution), every module and every feature of
`M2LB (8).zip` on an empty database, before and after the fixes.

| # | Found | Fix |
|---|-------|-----|
| 17 | Review analysis refused four specifications as *too large* (HendelseService 003: 98 078 characters, SkoleService: 91 941, MeldingsutvekslerReceiver: 63 791, HendelseAdapter 004: 58 064; the bound was 50 000) | The extraction is linear (28 ms for SkoleService on .NET, 0.6 s in the browser): the bound is 1 000 000 characters, the 1 MB of a specification import; beyond it the message gives the size and the bound |
| 18 | A feature without its own `spec.md` (AutorisasjonService 001, 002) loaded a project-level note as its specification (an Aspire skill reference under `.claude/`, `docs/analysis` notes); a choice for one feature carried over to its siblings; every feature listed six such notes as alternatives *for this feature*. Data Model showed *Skolenærvær — Code Review* for features without a data model | For a module feature's spec, plan, tasks and data model, project-level documents are neither alternatives nor used automatically; the empty state offers them, and opening one applies to that feature only. Module-shared documents and the constitution keep their shared meaning |
| 19 | 37 AI assistant files (`.claude/skills/…`) were classified as project artifacts | Assistant configuration folders (`.claude`, `.cursor`, …) are never classified as artifacts; `.specify/memory/constitution.md` is unaffected |

After: 0 analysis failures (all 42 features), 0 page errors, 0 server errors. Still open: `Frontend/docs/tilgangsadministrasjon/…Implementation-Notes`
is classified as a task list; as a module-shared document it is offered for Frontend features without `tasks.md` after a choice (not silently).

## Choosing the specification inside Manual Testing; several documents per feature

| # | Found | Fix |
|---|-------|-----|
| 20 | Switching feature meant leaving Manual Testing for the Specification Explorer, and the feature in use was not shown | **Module / Feature / Specification** lists on Discover & understand and Plan & prepare → Choose (the same saved choice the explorers use), with the specification in use named; the home's *Choose the specification* opens them |
| 21 | A change arriving while the review context was being rebuilt was dropped (*rebuild already in progress, skipping*): after a quick switch the requirements could stay those of the previous feature | A follow-up rebuild always runs; awaiting it returns the latest state; the lists wait for it |
| 22 | A chosen feature without a specification read *…61 specifications in 14 modules and none is chosen yet* | *Feature … of module … has no specification of its own, so it has no requirements* |
| 23 | The project-level module (root `specs/`) could not be chosen in the lists (its key is empty, like the placeholder) | Own option value |

`MultipleDocumentsPerFeatureTests` covers every multiple-document shape for every role: one document per role; extra documents of a role in the
feature (the Spec-Kit file is shown, others choosable, research never a plan); several and no Spec-Kit file (the tester chooses, per feature
and role); a feature missing a role (module-shared used, never a sibling's or a project note); a module without feature folders and two
specifications; a single-feature module with a module document; switching modules and back; quick switches (requirements follow the last choice).

Real data (M2LB, empty database, Release build): every module and feature chosen once with the Manual Testing lists, then all five explorers
opened — 37 module features and the 4 project-level features: every document is the feature's own or its module's shared one, every
explorer follows the choice, 0 documents from another feature or module, 0 page or server errors.

