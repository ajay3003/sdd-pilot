# Manual Testing UX consolidation — audit

**Baseline:**
- `1e322261` (after MT-7: `32b9d043`, `dbe51a2b`, `1e322261`);
- branch `008-traceability-first`;
- only the excluded files are uncommitted.

## Three similar-looking experiences (verified on HEAD)

| Route | What it is | Data | Own behaviour |
|---|---|---|---|
| `/system-understanding` | The system guide (Overview & Flow, Step by Step, Data & Contracts, Business Rules, What to test, Gaps & Evidence, Revisions) | `SystemGuideService` (curated guide, versioned) | Learning the system. The **What to test** tab lists 21 scenarios with "Prepare in Test Plan Suggestions" (multi-select and per scenario) |
| `/system-understanding?tab=tests` — sidebar "Functional Test Scenarios" | **A view shortcut** (`WorkspaceViewLink` in the Manual Testing preset) to the same tab | the same guide | **None.** Same page, same component, same data; only a second sidebar row |
| `/test-plan-suggestions` (Discover) | Selection for test preparation | the same guide scenarios (`ScenarioCandidate`, read by `TestPlanSuggestionService` from `SystemGuideService`) | Selection, filters, proposal creation. Its 21 cards in a 4-column grid looked like a second guide |

**Data ownership:** one scenario source (the guide). Test Plan Suggestions copies nothing: candidates are read from the guide revision readers see; drafts store a `ScenarioReference` (guide, scenario id, revision, fingerprint). There is no duplicated store, engine, review lifecycle or snapshot history. The duplication was **navigation and presentation only**.

**Selection hand-off:** `?guide=&scenarios=a,b` from What to test. Selection is client state and approves nothing; drafts are created only by "Prepare Test Plan" / "Add N to proposal".

## Decisions

1. **Remove the redundant "Functional Test Scenarios" row** from the built-in Manual Testing view. It had no independent capability.
   - The What to test tab, its route `system-understanding?tab=tests` and every deep link stay. The System Understanding row is highlighted on that link.
   - No other view, customization or feature depended on it: it was a shortcut only in that preset.
   - Authorization and Feature Visibility are unchanged.
2. **Clear responsibilities:**
   - **System Understanding** = learn the system;
   - **What to test** = discover possible test situations, with one contextual action, "Prepare in Test Plan Suggestions" (multi-select and per-scenario, the same `PrepareLink` handler; unchanged);
   - **Test Plan Suggestions** = the manual test-preparation workspace.
3. **Redesign Discover for choosing and preparing:**
   - compact rows grouped by the guide's own business-process stages (in the guide's order; no invented categories; "Other scenarios" only for candidates without a stage);
   - each row has title, two-line purpose, guide state, "In proposal", "No expected result", "No test data described", named executable tests and one **Evidence** link;
   - a selection bar: count, "**N hidden by filters — still selected**", "Select visible (n)" and "Clear selection";
   - **one** primary action in a sticky bar: "Prepare Test Plan (n)", or "Add n to proposal" for an open proposal, with a hint when nothing is selected;
   - the "Requirement" filter is now labelled **Business rule**, because these are guide rules, not specification requirements (MT-7);
   - the static "Source module" field was removed.
4. The other areas (Suggested Test Plan, Review Test Cases, Test Data & Testability, Saved Snapshots, Compare Deliveries, Prepare for Azure DevOps), Dashboard and Recommended Workflow are **unchanged**. They already show planning status, not a scenario catalogue.
5. **Help Center** ("Getting started" → Manual Testing workflow): System Understanding = learn; What to test → "Prepare in Test Plan Suggestions"; Test Plan Suggestions = prepare; Test Data; Azure DevOps = formal Test Plans and execution.
6. **Readability fixes** found by the responsive acceptance:
   - labels, eyebrows and tags on Test Plan Suggestions and System Understanding were 11.5–11.8 px and are now 12 px;
   - the What to test checkbox now has a 28 px target.

Source context is unaffected: logical application, source snapshots, analysis baseline, guide revision and delivery context are shown as before. No branches are mixed, and nothing is inferred about deployment.
