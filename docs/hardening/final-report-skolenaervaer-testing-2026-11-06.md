# Skolenærvær Testing — workflow redesign (2026-11-06)

## 1. Confirmed defects

1. **The Overview printed `(configuration.Suggestions.Count - known > 0) { … }` as text.**
   - **Root cause:** in `SkoleNaervaerTesting.razor`, `<dd data-testid="snt-config-known">@known of @configuration.Suggestions.Count known@if (…) { … }</dd>`. Razor treats `@` between two letters as an e-mail address, so `known@if` never switched to code. The condition and the opening brace were printed literally, followed by a stray `}`.
   - **Why it was missed:** no test asserted `snt-config-known`.
   - **Fix:** an explicit expression `@($"{known} of {total} known")@if (…)`.
   - The same glued pattern after an expression (`@source.Role.Label@if`, `@category.Owner@if`) was made explicit too (`@(…)@if`).
   - A scan of all pages and components found no other literal-word `…@if` / `@identifier` case.
2. **No orientation.** The Overview mixed source evidence, configuration, Test Data, journey setup and execution with no order or next step.
3. **"N things need attention" did not say what it counts.** It sums attention items across journeys, grouped by dimension and owner (`JourneyReadinessPresentation.Attention`).

## 2. Root causes and grouping

- **Attention items.** The per-journey readiness dimensions that need attention (Missing / Blocked / Partial / ReviewRequired, not optional) are grouped by (dimension, precise label) into owner-aware categories.
- **Per-journey blocker groups** already exist (`TestPackPresentation.BlockerGroups`, at most six, ordered by root cause: execution, integration, mapping, source, run configuration, authentication, authorization, test data, contract, verification).
- **The Overview now states both counts:** "N underlying items in M root-cause groups: each item is counted once, and grouping never removes an item" (`snt-attention-counting`). Nothing is merged in persistence.

## 3. Before / after workflow

The Overview now starts with seven guided steps (`SkoleNaervaerWorkflow`, shared `GuidedStepList`). They are derived from what the page already loads; there is no second readiness engine.
1. **Environment and source evidence:** required sources bound / available-but-not-bound ("bind them; no re-import needed") / missing. Available is not bound. Link: IQR source binding.
2. **Integration configuration:** the existing integration coverage. "Discovered integration proposals are not configured integrations; identity conflicts are decided in Relationship Review". Link: Integration Catalog.
3. **Test Data:** the **shared Test Data readiness** as its owner computed it, received through the component's `OnStatus` and never recomputed. "Saved is not approved; XSD valid is not business validated". Link: Test Data.
4. **Select journeys:** Utdanningsdata, Rapportmottak, Periodeavslutning, linking to the Journeys tab.
5. **Execution readiness:** the existing execution configuration. Blocked by root cause, pointing to the grouped attention; safety rules are never relaxed.
6. **Execute and verify:** explicit only; "executed is not verified"; no live journey runs from the Overview.
7. **Results and history:** "a historical pass is not current readiness".

## 4. State distinctions preserved

Each pair below is kept distinct:
- available ≠ bound;
- discovered ≠ configured;
- candidate ≠ confirmed (the Utdanning ↔ SkoleAdapter conflict is untouched);
- saved ≠ approved;
- XSD valid ≠ business valid;
- configured ≠ executable;
- executed ≠ verified;
- historical pass ≠ current readiness.

## 5. Files changed

- `frontend/BirkNext.Web/Pages/SkoleNaervaerTesting.razor`: the bug fix, steps, Test Data status hookup and attention counting.
- `frontend/BirkNext.Web/Services/SkoleNaervaerWorkflow.cs` (new).
- `frontend/BirkNext.Web.Tests/Pages/SkoleNaervaerTestingPageTests.cs` (+2): the configuration counts never render a raw expression (regression for the bug); seven steps and the items-vs-groups counting.

## 6. Tests and browser verification (actual runs, 2026-11-06)

- **Focused run** (SkoleNaervaer*, TestPack* plus the M01 set): **169/169 passed**.
- **Browser** (`m01-snt-e2e.cjs`, published build, `birknext_gaps`, real Skolenærvær data): passed (11/11 together with M01).
  - Seven steps: sources current ("bind, no re-import"), integrations waiting, journeys done, execution Blocked.
  - **The bug is fixed on the real page:** it now shows "8 of 12 known · 4 need attention" (the screenshot's values), and no `Suggestions.Count` or brace text is rendered.
  - **The screenshot's 17 explained:** "17 underlying items in 6 root-cause groups: each item is counted once, and grouping never removes an item".
  - No state-changing request, no external host, no page errors.

## 7. Remaining limitations

- The Journeys, Security and Verify tabs keep their existing structure; their table/filter redesign was not done in this pass.
- The Overview still contains its existing cards under the steps. The steps order them but do not replace them.
- No live journey or message was executed in this task.
