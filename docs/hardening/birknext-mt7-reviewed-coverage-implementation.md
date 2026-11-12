# MT-7 — reviewed requirement coverage: implementation

Audit and decision: `docs/audits/birknext-mt7-requirement-coverage-audit.md`.

## Shared
- `shared/CorrelationRules.Guides.cs` (new):
  - `GuideScenarioInput` and `GuideRequirementReference`;
  - rules `CR-S-EXPLICIT`, `CR-S-VIA-TEST` and `CR-S-SCOPE`;
  - `SpecScope(spec path)` → module and feature;
  - `RequirementTextFingerprint`.

  The rule runs after the test rule inside `CorrelationRules.Correlate`. Only pack-defined requirements are used, and title or text similarity never is.
- `CorrelationInput.GuideScenarios`: optional. `RuleSetVersion` is unchanged (`correlation/4`): the same input gives the same outcome, and the new rule acts only on the new field.
- `CanonicalReviewQueue`: new `ReviewItemType.ScenarioCoverage`, labelled "Scenario coverage", so the suggestions appear in Relationship Review under their own type.
- `TestPlanSuggestionContracts.cs`: `AssociationTrust`, `RequirementCoverageState`, `RequirementScenarioAssociation`, `RequirementCoverageRow` and `RequirementCoverageView`. The view carries the fixed boundary sentence: not test execution, not a pass, not an ADO Test Case.

## Backend
- `SystemGuideCanonicalReader` (new) implements `ICanonicalGuideReader`. It reads the revision readers see (published, else the draft) for every guide whose source roles recognise an interpreted snapshot (`SystemGuideService.RecognisedGuidesAsync`). It passes on only what the guide states:
  - requirement ids, with the specification file they cite;
  - test names;
  - rule ids;
  - the scenario fingerprint.
- `CanonicalInterpretationService.InterpretAsync` adds the guide scenarios when source snapshots are interpreted. Decisions go through the existing `DecideAsync`, which already records history, supersession and revalidation.
- `RequirementCoverageService` (new) is read-only. It combines:
  - the latest interpretation's pack-scoped requirements (the denominator);
  - the active guide-scenario relations with their decisions;
  - the Test Plan Suggestions reviews, through `IScenarioReviewSource`, implemented by `TestPlanSuggestionService.ScenarioReviewsAsync`/`ScenarioFingerprintsAsync`.

  The newest draft containing a scenario wins.
- `GET api/test-plan-suggestions/requirement-coverage?scope=…[&rows=false]`. With `rows=false` it returns counts and associations only: 1.2 KB instead of 357 KB on the real workspace.

## Frontend
- `ReviewedRequirementCoveragePanel` on **Requirements Traceability**:
  - four filter counts with their denominator;
  - rows with associations, their trust, the decision (reviewer, date) and the tester review;
  - "Decide in Relationship Review" on suggested associations;
  - **Unknown** with its reason.
- **Test Plan Suggestions**, inside each case:
  - "Specification requirements": verified, suggested ("not verified, not counted") and rejected, kept apart;
  - a link to Relationship Review;
  - the text "Accepting this Test Case for preparation does not approve any requirement association."

  The guide's own rule list is now labelled **Business rules (guide)**, never "Requirements".
- **Dashboard / Recommended Workflow** (Manual Testing focus panel) reuse the same view with `rows=false`:
  - facts "Requirements with reviewed scenario coverage (of N)" and "Requirements needing association review";
  - actions "Review requirement/scenario associations", "Review linked scenarios" and "Re-run relationship analysis" (when the guide changed after the interpretation).

  Unknown coverage adds no count and no action.
- `RequirementCoveragePresentation`: one wording for all of these surfaces.
- Relationship Review CSS: long tokens such as 64-hex fingerprints and canonical keys wrap inside the inspector (previously horizontal overflow at ≤ 1024 px), and the filter selects are at least 28 px.

## Test Plan Suggestions performance (task 20 follow-up)

Measurements are in `docs/audits/birknext-test-plan-suggestions-performance.md`.
- `IqrSourceStore.ListSourceAnalysisIdentitiesAsync` reads the stored snapshots for identity and recognition only. An allow-list of members (`IdentityMembers`) is materialized; test inventories, database architecture, behaviour evidence and the other large sections are skipped. Nothing is cached.
- It is used by the source context (`SourceBaselineService.SnapshotsAsync`/`ContextAsync`) and System Understanding's source roles (`SystemGuideService.GetAsync/CurrentSourcesAsync`).
- The Test Plan Suggestions workspace, create and refresh share one load between the guide and the baseline context.
- Equivalence is tested: identity members equal the full read, and the source context is equal from both reads.

## Not changed
- No Azure DevOps read or write.
- No new table or migration.
- Test Data links, MT-6 review flags and snapshots are untouched.
- Authorization is unchanged: deciding needs an authenticated BirkNext session, as for every Relationship Review decision.
