# Manual Testing — AC-level classification and scenario grouping (2026-11-26)

Commits: `ff6bdc69` (classification, documented tests, grouping), `3205eacc` (requirements group), `7585de65` + `55…` (removed stories; see Finn og forstå v3 report).

Status legend: IMPLEMENTED · VERIFIED (browser, isolated instance) · PARTIAL · BLOCKED · NOT IMPLEMENTED.

## A–C. Architecture, model and the US-vs-AC root cause

| Stage | Where | Finding |
|---|---|---|
| Classification | `shared/ScenarioTypeClassification.cs` — `ScenarioTypeClassifier.Propose/Classify` | Already per scenario: reads only the scenario's own title, Given/When/Then and its linked requirement ids. Never the story. |
| Human review | `StoryGroupingDocument.ScenarioTypes` (`ScenarioTypeDecision`) | Keyed by the scenario's source identity; `StoryKey` is context only and never cascades. A review made on another scenario text (fingerprint) is not applied. |
| Grouping | `frontend/.../Services/ScenarioGrouping.cs` | `ValueOf(…, ScenarioType)` uses each item's own `TypeResult`. |
| Dashboard | `TestPlanSuggestionService.Overview.cs` | Per acceptance scenario, with the reviewer's decision. |
| **Defect 1** | default grouping | Was Brukerhistorie → Testnivå. Now **Brukerhistorie → Scenariotype** (`ScenarioGrouping.Default`). |
| **Defect 2** | `shared/TestPlanStructure.cs` `PlanStructure.FactsOf` | Suites of new plans used `Propose` only, so a reviewed AC type was ignored. It now uses `Classify(s, decision)` on the server, and the overview's resolved type in the browser preview. |
| **Defect 3** | discovery | Documented E2E tests (TEST-E) in the modules' test-specification documents were never read (see H). |

No story-level type exists anywhere. Story groups now show a **computed** summary, «Akseptansescenarioer: 3 funksjonelle · 1 ikke-funksjonell» (`ScenarioGrouping.TypeSummary`, `ScenarioGroupAxes.TypeCount`). It is never stored and never a story type.

## D–G. Rules

Rules are unchanged (`scenario-type/1`): functional, non-functional, mixed and unclassified are decided per scenario, clause by clause. «Mixed» applies only when the *same* AC asserts both kinds. Unclear stays «Uavklart». Group labels are now «Funksjonelle / Ikke-funksjonelle / Blandede / Uavklarte».

## H. Standalone documented E2E tests — IMPLEMENTED, VERIFIED

**Root cause:** no provider read `*/docs/*Testspesifikasjon.md` or `*Test-Specification.md`.

**Inventory** (M2LB.zip, read-only):
- HendelseService: 37 documented tests (3 E, 21 I, 13 U);
- RevisjonService: 20 documented tests (5 E, 7 I, 6 U, 2 S);
- spec.md, plan.md and tasks.md only *mention* TEST-E ids.

**Implementation:**
- `shared/DocumentedTestSpecification.cs`: `DocumentedTestReader` reads documents qualified by *name*, with blocks of the form `**TEST-X-NN** — title` and Gitt / Når / Og / Forventet.
- The result is stored at Source Analysis as typed facts in `IqrSourceSnapshot.DocumentedTests`; the documents themselves are not stored.
- `TestSpecificationScenarioProvider` (capability `test-specification`) offers **declared E2E only**:
  - key `testspec:{file}#{id}`, so TEST-E-01 in two modules is two scenarios;
  - no user story;
  - level `BusinessE2E`, which is a *candidate* (declared, not verified);
  - its own scenario type via `ScenarioTypeClassifier.IsClassifiable`.
- The provider is registered directly after the specification provider.
- **Pre-existing finding:** discovery keeps the first `MaxCandidates = 2000`, and M2LB reaches exactly 2,000, so later providers are truncated («Only the first 2000 candidates are kept»). This is NOT fixed (see W).

**Browser (v1env17):**
- `A-standalone-discovered` PASS: capability Available with 8 candidates, none with a story, unique ids.
- `A-standalone-visible` PASS: shown in Finn og forstå under «Scenarioer uten brukerhistorie».
- `A-standalone-in-plan` PASS: added to a sprint plan with no story.

## I–J. Independence

Test level (`TestLevelClassifier.Of(candidate.Type)`), scenario type and test method (ownership / kind decision) are separate axes, filters and grouping values. Covered by test `AC01_…` in `ScenarioGroupingTests`: test level → type gives the same items.

## K. Grouping defaults — IMPLEMENTED, VERIFIED

- The default is Brukerhistorie → Scenariotype, with every group collapsed. «Tilbakestill gruppering» returns to it.
- Browser: `A-default` PASS (Story / ScenarioType, 0 expanded). `A-story-mixed-types` PASS: US1 «Overblikk ved innlogging» → «Funksjonelle 3», «Ikke-funksjonelle 1», and the header says «Akseptansescenarioer: 3 funksjonelle · 1 ikke-funksjonell» (no story type).

## L–P. Rows, drawer, Planlegg, Testplaner, Testoversikt

- Rows and drawer show the AC's own `TypeResult`. `A-drawer-type` PASS.
- Plan suites use the AC's reviewed type. `A-plan-type-suites` PASS.
- Testoversikt counts unique scenarios (`TileScenarios`, DistinctBy identity), never stories. Documented tests are included from the latest discovery (`DocumentedTestSpecificationsAsync`).

## Q–R. Translation and identity

Classification is language-neutral (enum values). Identities are unchanged: no scenario id or source identity was altered, and documented tests get new identities scoped by document.

## S–T. Tests

**Backend:**
- `HierarchicalTestPlanTests.AC_level_types_in_the_plan_structure_…`
- `DocumentedTestScenarioTests` (3)

Related suites 27/27. The full backend run under parallel environment load had 8 failures; 7 passed on rerun (timeouts), and 1 was a pre-existing ownership allow-list, fixed in `ff6bdc69`.

**Frontend:**
- `ScenarioGroupingTests.AC01_…`, `Requirements_without_a_scenario_…`
- updated G01/G05/G06

Full frontend suite **9,833/9,833** (after `c18c2218`).

## V. Files

- `shared/DocumentedTestSpecification.cs` (new), `ScenarioTypeClassification.cs`, `TestPlanStructure.cs`, `IqrSourceContracts.cs`
- `backend/.../TestSpecificationScenarioProvider.cs` (new), `TestScenarioDiscoveryEngine.cs`, `IqrSourceStore.cs`, `TestPlanSuggestionService(.Overview).cs`, `Program.cs`
- `frontend/.../ScenarioGrouping.cs`, `ScenarioBrowserPresentation.cs`, `ManualTestingDiscover.razor`, `ManualTesting.razor`
- tests

## W. Remaining limitations

1. **The 2,000-candidate cap truncates M2LB discovery** (pre-existing). Recommendation: raise it or make it per provider; this needs a performance review.
2. Integration and unit documented tests (TEST-I, TEST-U) are not offered: they are developer tests.
3. A reviewed type for a standalone documented test is stored in the grouping document of the specification open in Finn og forstå. The dashboard reads decisions per test document, so such a review may not reach the dashboard (PARTIAL).
4. Older snapshots need a re-import before documented tests appear (the provider says so).

## X. Acceptance criteria

- **VERIFIED:** AC01–AC05, AC13, AC15, AC16, AC23, AC28.
- **IMPLEMENTED** (unit tests): AC06–AC12, AC14, AC17–AC22, AC24–AC27, AC29–AC30.
- **Process:** AC31–AC34 held (no destructive migration, no data mutation, tests run). AC35 VERIFIED (v1env17). AC36 held (no Azure DevOps writes).
- **AC37 (no commits): not held.** The user explicitly asked for commits.
