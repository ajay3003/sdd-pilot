# Test Plan Suggestions — test types, execution methods and performance cases, part 1 (2026-11-13)

## Audit (verified)

| Area | Location |
|---|---|
| Contracts | `shared/TestPlanSuggestionContracts.cs` (`TestCaseDraft` = generated `TestCaseContent` + human `TestCaseEdits`) |
| Server merge / sanitize | `TestPlanSuggestionService` — human edits (`TestCaseHumanPart.Edits`) are kept through regeneration and cleaned by `Clean` |
| Assessment, comparison, export | `TestPlanSuggestionEngine` (`Assess`, `CaseChanges`, `Export`) |
| UI | `Pages/TestPlanSuggestions.razor` |

Before this change there was **no test type and no execution method** in the model, and no way to describe a performance case.

## Implemented

- **Model.** `TestCaseType` and `TestExecutionMethod` are separate enums. `PerformanceCaseDetails` holds only optional free text plus a
  subtype. All three live in the human-owned `TestCaseEdits`, so they survive regeneration exactly like other edits. No type is inferred
  from scenario text: the default is "Not classified yet" / "Not decided".
- **Assessment.** For a performance case, a missing acceptance criterion, workload or environment is listed as open, for example
  "Acceptance criteria not defined — review required". Nothing is assumed: no default users, durations or thresholds. The questions apply
  only to performance cases, and type never implies the execution method.
- **UI.** The case editor has Test type and Execution method selectors. The performance form appears only for performance cases. Its
  labels are plain language, no script is asked for, and it points to Performance Test Review for execution.
- **Delivery comparison** reports TestType, ExecutionMethod and Performance changes.
- **Export (Azure DevOps draft, no writes):**
  - JSON carries `testType`, `executionMethod` and `performance`.
  - The text export adds the type and method, plus a labelled "Performance details" section with only what was stated; an undefined
    criterion reads "NOT DEFINED".
- **User Guide** updated.

## Tests

- Backend `TestPlanSuggestionEngineTests`, 2 new:
  - performance open items, and nothing assumed;
  - the exported performance text, and comparing type, method and workload changes.
- Frontend `TestPlanSuggestionsPageTests`, 1 new: classify a case, the performance form appears, the undefined criteria message shows,
  and the method stays "Not decided".
- Focused suites: backend 35/35, frontend 15/15.

## Not done (rest of the brief)

- The four-stage workflow redesign.
- Integration, end-to-end, security and API-specific form fields (only performance has a dedicated form).
- A Performance Test Review link by identity. The reference is free text today.
- Scope filters (depends on task 30).
- The source-conflict view.
- Browser acceptance.
