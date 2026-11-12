# Test Plan Suggestions — implementation (task 20 v2)

Architecture: `docs/audits/birknext-test-plan-suggestions-architecture.md`. Acceptance: `docs/audits/birknext-test-plan-suggestions-acceptance.md`. Gaps: `docs/audits/birknext-test-plan-suggestions-gap-register.md`.

## Files

| Area | Files |
|---|---|
| Shared contracts | `shared/TestPlanSuggestionContracts.cs` (linked into API and Web) |
| Backend | `Services/TestPlanning/TestPlanSuggestionEngine.cs` (pure rules, rule version `tps-1`), `Services/TestPlanning/TestPlanSuggestionService.cs`, `Controllers/TestPlanSuggestionsController.cs` (`api/test-plan-suggestions`), `Models/TestPlanRecords.cs`, `Data/AppDbContext.cs`, migration `20261112115955_AddTestPlanSuggestions` (two tables + immutability trigger), `Program.cs` (DI) |
| Frontend | `Pages/TestPlanSuggestions.razor(.css)` (7 areas), `Services/TestPlanSuggestionsApiService.cs`, `Services/TestPlanSuggestionsPresentation.cs` (filters, labels, save request, focus facts), `Pages/SystemUnderstanding.razor(.css)` (What to test: per-scenario action, multi-select), `Components/WorkspaceFocusPanel.razor(.css)` (Manual Testing: Test Plan facts and actions on Dashboard and Recommended Workflow), `Services/NavigationCatalog.cs` (catalog row; Manual Testing → Test preparation), `Services/DocumentationCatalog.cs`, `Services/UserGuideContent.cs` and `Components/Guide/GuideGettingStarted.razor` (Help Center article) |

## API

| Method | Route | Permission |
|---|---|---|
| GET | `api/test-plan-suggestions?guideKey=` (one workspace call) | session |
| GET | `drafts/{id}`, `snapshots/{id}`, `compare?from=&to=`, `snapshots/{id}/export` | session |
| POST | `drafts` (create from selected scenario ids) | IntegrationConfigurationWrite |
| PUT | `drafts/{id}` (human-owned parts; base revision) | IntegrationConfigurationWrite |
| POST | `drafts/{id}/review`, `drafts/{id}/refresh?baseRevision=`, `drafts/{id}/snapshots` | IntegrationConfigurationWrite |

Errors return `{ code, message }`. 409 codes:
- `draft-changed`
- `guide-changed`
- `expected-result-missing`
- `source-changed`
- `snapshot-conflict`

## Tests

**Backend `TestPlanSuggestionEngineTests`** (10, pure, on the real curated guide):
- drafts use the guide's own words;
- an unreviewed or unclear expected result stays explicit;
- suites follow the stage order;
- links need an exact identifier (`valid-report` and `duplicate-report` are named; "Korrigert rapport" is not linked to "correction");
- ambiguous links are flagged;
- testability is never ready by documentation alone, and the environment is never claimed;
- the snapshot fingerprint ignores bookkeeping;
- the comparison covers added, removed, changed, unchanged and the reviewed-only potential rule change;
- a rule-version-only difference is context only;
- different systems are not compared;
- an unchanged high-risk case stays in regression;
- the export boundary is stated, with no "ADO-ready" claim.

**Backend `TestPlanSuggestionServiceTests`** (7, real PostgreSQL with migrations, real guide):
- 21 candidates and a 7-case plan with stage suites and an unreviewed basis;
- a client cannot rewrite generated content, and a stale save gets 409;
- review history, rationale rules and no acceptance without an expected result;
- Test Data linked by `tda_` id, and an unknown id is ignored;
- snapshot immutability (the trigger's message), identical content saved once, history kept after draft changes and a new DbContext (restart), and the comparison;
- concurrent identical saves (4 parallel) produce 1 row;
- a guide change is flagged and not applied; adding is refused with `guide-changed`; refresh puts an accepted case back under review; the comparison shows ExpectedResult + SourceScenario + Review with no false rule-change claim.

**Frontend `TestPlanSuggestionsPageTests`** (8):
- contextual selection is never an approval, and unknown ids are dropped;
- filters;
- read-only user;
- baseline warning;
- review labels and the accept guard;
- the save request carries only human-owned parts;
- Test Data and testability;
- export boundary;
- focus facts for Dashboard and Recommended Workflow.

**Frontend `SystemUnderstandingPageTests`**: one new test for What to test (single and multi-select links; the guide state is unchanged).

**Updated expectations**, because the new sidebar row is counted and ordered there:
- `ManualTestingViewTests`;
- `NavMenuLayoutTests` (quality-testing order);
- `FeatureVisibilityInventoryTests` (53 → 54 entries, 20 → 21 always shown);
- `SystemSettingsFeatureVisibilityTests` (47 → 48 of 53 → 54).
