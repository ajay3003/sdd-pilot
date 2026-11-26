# Test plan templates, empty plans and manual test cases — 2026-11-26

**Delivery 4.** Builds on the hierarchical test plans of delivery 3 (commit 1a24909e).

**Safety:**
- no database reset, cleanup or destructive migration;
- no Azure DevOps write;
- no internet, AI, Docker or Podman needed;
- tests use temporary databases and temporary directories only.

## A. Summary

- **«Ny testplan»** has three starting points:
  - **Fra mal** (the default);
  - **Helt tom testplan**;
  - **Fra valgte scenarioer**. This option is shown only when scenarios are selected in Finn og forstå.
- **A template is structure only:**
  - it holds suite names, nesting, order, test level and an optional description;
  - a plan made from a template starts with **0 test cases** and gets **new suite ids**;
  - no case, step, test data, source link, approval, revision, Azure DevOps id or transfer history is ever copied.
- **There are four built-in, read-only templates:**
  - «Standard testplan»;
  - «E2E-testplan»;
  - «Integrasjonstestplan»;
  - «Kravbasert testplan».
- **Custom templates** are created in two ways:
  - with **«Lagre som mal»** in Planlegg (Testgrupper panel), from the plan's *stored* suite structure;
  - in **System Settings → Manual Testing → Test plan templates**, where you can create, edit, duplicate (also built-ins), archive and restore.
- **Archiving** hides a template from «Ny testplan» only. No existing plan changes.
- **Empty plans are valid plans:**
  - Planlegg shows «Testplanen er tom.» with **Opprett testgruppe** and **Legg til testtilfelle**;
  - suites can be saved on a plan with no cases.
- **«+ Nytt testtilfelle» inside every suite:**
  - it proposes that suite and its test level;
  - the tester may choose another level, and a short note explains that the suite is only organisation;
  - the new-case form and the case editor both carry **Testnivå** and **Scenariotype**, and the Testmetode is shown beside them.
- **Hand-written cases never show «Kilde mangler».** The source comparison covers discovered cases only; this is now pinned by a test.
- **Scenarios can be added to a template or empty plan later.** The template suites and the manual cases stay.

## B. Design

| Part | Where |
|---|---|
| Contracts, built-ins, validation, `Instantiate` (new ids), `FromPlan` (structure only) | `shared/TestPlanTemplates.cs` |
| Custom template storage | `TestPlanTemplateStore` → `App_Data/manual-testing/plan-templates.json`, beside the dashboard settings and written atomically |
| API | `GET/POST /templates`, `PUT /templates/{id}`, `POST /templates/{id}/{Archive\|Restore\|Duplicate}` |
| Template resolution | `CreateProject` resolves `TemplateId` to template suites. An unknown or archived template gives 400 `template-unknown`. |
| Manual case in a suite | `AddManualTestCaseRequest.SuiteId/TestLevel/ScenarioType`. An unknown suite gives 400 `suite-unknown`. |
| Case's own level and type | `TestCaseEdits.TestLevel/ScenarioType`, kept by `Clean`. Moving a case to another suite never changes them. |
| Planlegg | `PlanSuiteEditing.ShowTree` shows the tree when a hierarchy exists or a suite has no cases; this prevents invisible empty suites. |

**Rules:**
- Built-ins can't be changed or archived.
- Names are unique, ignoring case.
- At most 200 suites.
- Cycles and unknown parents are refused with a reason.
- An unreadable template file never hides the built-ins.

**ADO export:**
- manual cases are exported like any other ready case;
- every suite, empty or not, is listed in the package manifest;
- the existing «Importveiledning» lists empty suites as «tom testgruppe».

## C. Tests

- **Backend:**
  - **`TestPlanTemplateTests` (5):**
    - the built-ins are valid;
    - instantiation gives new ids with the same nesting, order and level;
    - `FromPlan` copies structure only;
    - the store's save, rename, duplicate, archive and restore, with built-ins and duplicates refused;
    - an unreadable file falls back to the built-ins;
    - the export of a template plan includes the manual case and every empty suite.
  - **`SourceDrivenPlanningTests` (temporary database):**
    - an empty plan, then a suite saved on it;
    - a plan from a template (0 cases, the template's suites);
    - a manual case in a chosen suite keeps its own level, type and method, with no extra suite and source state null (no «Kilde mangler»);
    - an unknown suite is refused;
    - scenarios added later keep the suites and the manual case;
    - one revision per change.
- **Frontend:**
  - `ManualTestingPlanTemplateTests`: the empty plan view, per-suite new case with level proposal and the sent suite, level and type, editor level/type saved, «Lagre som mal»;
  - `ManualTestingTestplanerTabTests`: «Fra mal» is the default with a preview and the sent template id, «Helt tom testplan», custom templates offered and archived ones not;
  - `TestPlanTemplatesSettingsCardTests`: built-ins read-only, edit, duplicate and archive.
- **Results:** see section E.

## D. Not done in this delivery

- **NOT IMPLEMENTED:**
  - a test level per suite inside the Planlegg suite panel. Levels come from templates; a suite created by hand has none;
  - template versioning. A template edit replaces it, and plans made earlier never change.
- **Not checked in the browser:** no isolated browser walkthrough was run. The flows are covered by the bUnit and database tests above.

## E. Regression results
- Targeted runs before commit:
  - backend template, hierarchy and scenario-centric tests: 45/45, plus 5/5 for TestPlanTemplateTests;
  - frontend Planlegg, Testplaner and settings tests: 194/194, including 25 new tests.
- The full backend TestPlanning/TestEvidence/ImplementationGap suite and the full frontend suite were still running at commit time. Their results follow in a later commit.
