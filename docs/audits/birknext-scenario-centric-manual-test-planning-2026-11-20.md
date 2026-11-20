# Scenario-centric manual test planning — audit and implementation (2026-11-20)

Branch `008-traceability-first`, starting HEAD `f9527f59`. Scope: Manual Testing → Plan & prepare (route `/test-plan-suggestions`).

Product principle: **Scenario = what should be tested and why. Test case = how it is tested and what result is expected. BirkNext =
discover, prepare, review, trace and export. Azure DevOps Test Plans = execute and record results.**

## 1. Existing architecture (before)

| Concern | Where | Notes |
|---|---|---|
| Scenario discovery | `TestScenarioDiscoveryEngine`, `TestScenarioDiscoveryService`, `ScenarioDiscoveryStore` | One `DiscoveredScenario` per acceptance scenario / requirement / UI suggestion / contract operation / journey. `CandidateId` = SHA of project key + capability + canonical source identity (`scenario:{module}|{id}`): stable across re-imports, project-scoped, never the snapshot, never the title. |
| Plan, cases, revisions | `TestPlanSuggestionService` → `test_plan_drafts` (JSON content) + `test_plan_draft_revisions` (append-only, one per save) + immutable snapshots | A case's identity is `Source.ScenarioId` (unique in the plan). Generated content is server-built and read-only; human edits are a separate `Edits` record. |
| Requirement links | `ManualRequirementLink` on the case, checked against the server-held `SpecificationCatalogService` catalog (captured at Project Import) or the browser's catalog | Automatic links = the case's own evidence (`Generated.Requirements`). |
| Coverage / traceability | `PreparedCaseTraceability`, `RequirementCoverageService` | Prepared/approved = test design, never execution. |
| Export | `AdoTestPlanExporter`, `AdoTestPlanExportService`, `AdoExportWizard` | Documented Azure Test Plans import columns (one row per step); preview = file. |
| UI | `Pages/TestPlanSuggestions.razor` (+ presentation classes) | Prepare area listed cases flat, grouped by suite. |

## 2. Limitations found

1. **One case per scenario**: a case *was* its scenario (identity = scenario id). A second case for another data condition could only be
   written "by hand", explicitly *not linked* to the scenario.
2. **Flat list** by suite; expanding a case showed every field and, with an empty search, up to 8 requirements of the catalog.
3. **One copied line**: a specification scenario became one step `When → Then`; the observation fact was set equal to "expected known",
   so "how do I see the result?" was never asked.
4. **No requirement for unlinked scenarios**: acceptance scenarios that name no `FR-` id (M2LB SkoleService User Story 6 scenario 3 —
   "Scenario 42", *child past compulsory school age*) got no requirement at all; linking needed a search through the catalog.
5. **No translation**; **Save draft** disabled with no explanation; review, content, testability and export states not separated in the case.

## 3. New hierarchy (Project → snapshot → plan → scenario → cases)

- `TestCaseDraft.ScenarioKey` (+ `ScenarioTitle`): the scenario a hand-written case is written under. Discovered cases do not store it — their
  own id is their scenario. `ParentScenario` (computed) is the grouping key.
- An older hand-written case without `ScenarioKey` is **never assigned** to a scenario: it is listed under *Written by hand — no scenario
  established* (explicit unresolved association).
- `ScenarioPlanning.Groups(plan)` (shared by server and browser) groups cases by scenario, the scenario's own case first.
- No migration: every new field is nullable and omitted from JSON when empty (`WhenWritingNull`), so existing plans keep their content
  fingerprint and their revisions; no table changed; `manual_test_runs` / `manual_run_attachments` untouched.

## 4. One-to-many persistence

`POST drafts/{id}/manual-cases` (`AddManualTestCaseRequest`) gained `ScenarioKey`, `CopyFrom`, `Variant`:

- **Add under a scenario** — the scenario must be in the plan (else 400 `scenario-unknown`); the case starts from the scenario's starting
  situation only; no step or outcome is filled in.
- **Duplicate** — new identity (`manual-…`), the source case's current text, `DuplicatedFrom`; review, history, test-data review state and
  any Azure DevOps id are **not** copied.
- **Variant** — only a proposal from `ScenarioPlanning.Variants` (a further condition clause of the expected outcome, a stated failure
  case, a contract error step); refused (409) if no longer supported. `GET drafts/{id}/variants?scenario=` lists proposals.
- Editing one case never changes a sibling (each case is its own record; the save is per case id); every add/duplicate/save/translation
  is a revision ("Test case added", "Test case duplicated", "Translated to Norwegian").
- A never-reviewed hand-written case can be removed (earlier revisions keep it); reviewed cases are excluded with a rationale.

## 5. Norwegian translation

- Stored per case as `TestCaseTranslation` (title, purpose, preconditions, steps, step results, expected result) with
  `SourceFingerprint` (SHA-256 of the original text it translates), `Origin` (Machine / Manual / MachineEdited), provider, who and when.
  The original is never replaced; immutable snapshots are untouched.
- **State**: None / Current / SourceChanged (the original changed after translation → "made from an older original — review it"; the
  Norwegian text is kept).
- **Scope**: `POST drafts/{id}/translate` translates only the chosen scenario's cases. A person's Norwegian text (Manual/MachineEdited) is
  kept unless `Overwrite` is asked. Review decisions are never changed.
- **Identifiers**: `ScenarioPlanning.Protect/Restore` replace requirement and scenario ids, URLs, file paths, code in backticks, camelCase /
  PascalCase / snake_case / dotted identifiers, HTTP status codes, UPPER_CASE values and quoted machine values with numbered placeholders;
  a translation that loses, repeats or invents a placeholder is **not used** (that case is reported as failed).
- **Providers** (`ITestCaseTranslator`, chosen by `ManualTesting:Translation:Provider`; nothing is called to decide availability):
  - `AzureTranslator` — Azure AI Translator REST v3 (`/translate?api-version=3.0&to=nb&textType=html`, `Ocp-Apim-Subscription-Key`/`-Region`);
    placeholders additionally wrapped in `<span class="notranslate">`.
  - `LibreTranslate` — a self-hosted server (offline once its models are installed); only the plain-language pieces between identifiers are
    sent, so identifiers never reach it.
  - `Anthropic` — the AI client BirkNext already had (`Anthropic:ApiKey`).
  - Default: **unavailable** with the reason; the language icon then opens the Norwegian fields for manual writing.
- Text is sent only when a person chooses Translate. Automated tests use deterministic fakes and HTTP stand-ins; no external service was
  contacted in this task.
- **UI localisation**: BirkNext has no localisation infrastructure; UI labels stay English (the Norwegian editor's field labels are
  Norwegian). Content translation is separate from UI localisation (§12 limitation).

## 6. Markdown requirement extraction and matching

Reuses the requirement list the links are checked against (the project's specification as parsed by the existing parser — server catalog
via `SpecificationCatalogService.Read`, browser via `RequirementLinkPresentation.Options`). No second catalog. `ScenarioPlanning.SuggestRequirements`:

| Level | Basis |
|---|---|
| A · Explicit | The scenario's evidence or text names the id. |
| B · Structural | The requirement's wording names the scenario's acceptance-scenario anchor or user story (`US6`, `User Story 6`). |
| C · Same behaviour | Distinctive shared words **and** phrases, weighted by rarity across the specification, above a firm threshold; the shared words are the stated reason. |
| D · Possibly related | Weaker overlap; shown as "not established". |

Linked and rejected requirements are left out. The same id in another specification file is another requirement (key = id + path).
**Why Scenario 42 had no link**: its Given/When/Then names no `FR-` id and acceptance scenarios were linked only by explicit ids. Real M2LB
check (opt-in test `RealM2lbRequirementSuggestionTests`, reads the local archive in memory): see §15.

Confirmation stores `ManualRequirementLink` with `SuggestionLevel` and `SuggestionReason` (what the tester saw), checked against the server
catalog as before. `Scope = "Scenario"` links apply to the scenario as a whole and are **never counted** as a case's test design.
Rejections are stored per case (`RejectedSuggestions`, who and when recorded by the server).

## 7. Test steps from evidence

A specification acceptance scenario now becomes: 1. *Prepare the starting situation in the test environment: {Given}* → *The starting
situation is in place before the action (check the prepared data and state).* 2. *Carry out or trigger: {When}* → *{Then}*. Built in
`TestScenarioDiscoveryEngine.ProcedureSteps` when a case is prepared, so the scenario's evidence fingerprint is unchanged (no false "changed
in the evidence"). How the action is triggered and how the result is observed are not in the specification: the observation fact is now its
own (`Testability.Observation` = known only when `ObservationMethod` is stated), and the case lists *Verification method: Needs clarification
— verification method not identified* until the tester fills **How to verify**. Cases are labelled *Generated — not reviewed*.

## 8–9. Test Data, testability, review

Per-case checklist (environment, access, test data — Available / Required but missing / Needs clarification / Not required / Not assessed —,
execution mechanism, expected outcome, observation mechanism, dependencies): Ready only for what the case states; approval never changes it.
Links go to Test Data (`test-data?section=assets`) and the Testability step. Each open case shows four separate states: **Content**
(Saved / Saved · revised / Unsaved changes), **Review**, **Testability**, **Export** (Azure DevOps id known or not) plus the Norwegian state.
**Save draft** is disabled only when nothing changed and says so ("All changes saved · version n"); approval needs no prior "approve" step
for saving. Approval text states it is not execution, not a passed test and not proof the requirement is right.

## 10. Azure DevOps export

`AdoExportRequest.Language` = `nb` or original. Each case is exported **in one language**: Norwegian only with a complete Norwegian text
(every step and every stated step result), otherwise the original with a note; a stale translation is exported with a warning. Each case
carries `ScenarioKey`, `ScenarioTitle`, `Language`, `CaseRevision`; the Description of a hand-written case names its scenario; the mapping
report gained *Scenario* and *Language* columns. Duplicates never carry an Azure DevOps id; unapproved cases are labelled drafts in the
wizard. The documented import columns are unchanged; downloading is not importing (boundary text unchanged).

## 11. Coverage semantics

Unchanged engines, sharper inputs: a suggestion is never a link; a scenario-level link is never counted; a manual link counts only when
confirmed, saved and in the reviewed specification; prepared/approved/exported are test design, never execution.

## 12. Source snapshot changes

Scenario ids do not drift with titles. Re-discovery keeps human edits, translations and review history (`RefreshProjectAsync` unchanged);
an approved case whose scenario changed returns to review; a translation made from an older original is flagged, never overwritten.
Links keep the catalog revision and wording hash they were checked against.

## 13. Accessibility and layout

Scenario headers are buttons inside headings with `aria-expanded`/`aria-controls`; the language icon has the accessible name *Translate
scenario and test cases to Norwegian* and its SVG is `aria-hidden`; status lines use `role=status`; focus-visible outlines; the scenario
tools wrap on narrow screens (`max-width: 600px`), long titles use `overflow-wrap:anywhere`; one scenario and one case editor open at a time.

## 14. Tests

Backend (`BirkNext.Api.Tests/Services/TestPlanning`):
- `ScenarioPlanningTests` (13) — grouping, unresolved cases, duplicates, identifier protection, one-language export, A/B/C/D suggestions
  (same id in another module, unrelated requirements, rejected/linked excluded), evidence-only variants.
- `TestCaseTranslatorProviderTests` (10) — provider choice, Azure request/headers/notranslate/errors, LibreTranslate pieces-only, offline errors.
- `ScenarioCentricPlanningServiceTests` (11, disposable PostgreSQL) — procedure generation, add/duplicate/variant, sibling isolation and
  revisions, translation (scope, identifiers, person's text kept, overwrite, stale, unavailable/failed/lossy), suggestions + scenario-level
  link not counted, project isolation, Norwegian export.
- `RealM2lbRequirementSuggestionTests` (opt-in, `BIRKNEXT_M2LB_MANUAL_ARCHIVE`).
- Updated: `TestScenarioDiscoveryTests` (procedure steps; the open action result is still null).

Frontend (`BirkNext.Web.Tests`): `ScenarioCentricPrepareTests` (8); `TestPlanSuggestionsPageTests` updated to open a case's scenario first
(one scenario open at a time) and to address fields by test id instead of position.

Browser (`BirkNext.Web.PlaywrightTests`): `ScenarioCentricManualTestingWorkflowTests` with `LibreTranslateStandIn`; the workflow script
configures the isolated API's LibreTranslate provider at a per-run port (`BIRKNEXT_PLAYWRIGHT_TRANSLATE_PORT`).

## 15. Results

See the commit message and the handoff for the exact numbers of this run. Focused runs on the final code before commit:
backend Test Planning + traceability 185/185 (before the new files) and new backend files 34/34; frontend focused 128/128 and new 8/8.
The browser run (`run-workflow-tests.ps1 -Filter` Manual Testing workflows) was started on the final code; its result is reported
separately (not awaited before commit, at the user's request). Full suites, backend partitions and the Release package checks were not
re-run for this commit.

## 16. Known limitations

- UI labels are English (no localisation infrastructure); only content is translated.
- Level B structural matching depends on requirement wording naming the user story / acceptance scenario; M2LB requirements rarely do, so
  its scenarios are matched at level C. `tasks.md` story→FR references are not yet read (the User Story hierarchy task will add story identity).
- Translation providers were verified against stand-ins/fakes only; no real Azure Translator or LibreTranslate server was called.
  LibreTranslate's code for Norwegian bokmål (`nb`) should be confirmed against the installed server (`/languages`).
- Provider choice is a startup setting; a System Settings → Translation tab (switch without restart, managed LibreTranslate container) is the
  next task.
- Re-import with a changed specification was verified in backend tests (refresh, stale translation), not in the browser run.

## 17. Commits

Recorded in the handoff (local only; nothing pushed).
