# Test Plan Suggestions — scenario source audit (task 20 v2)

Baseline: `581e6314` on `008-traceability-first`. The tree was clean apart from other sessions' files, which were left untouched: `appsettings.Local.json`, `AIAssisted/testdata/`, and the `docs/audits/birknext-final-*-2026-11-07` documents.

## System Understanding "What to test" (canonical source of business scenario candidates)

| Question | Finding (traced in code) |
|---|---|
| Data model | `GuideTestScenario` in `shared/SystemUnderstandingContracts.cs`. Fields: id, title, objective, preconditions, test data, expected outcome, observable evidence, failure cases, state, existing tests (names), follow-up, step ids, evidence. |
| Identity | A stable `Id` per scenario inside a guide (`gyldig-rapport`, `manglende-barn` …), unique per guide (`SystemGuideValidation`). A guide is identified by `GuideKey`. |
| Source of descriptions | **Curated** content: `Services/SystemUnderstanding/Guides/skolenaervaer.guide.json`, an embedded seed written outside the installation from reviewed source packages. It is **not** generated from imported source. Its evidence entries point at source files, specifications and audits, each with a class (SourceImplemented, Specified …). |
| Versioning / persistence | `system_guide_revisions`: one row per revision, with a content SHA-256. A database trigger makes content immutable beyond Draft. At most one open revision and one published revision per guide. |
| Review state | The shipped seed is revision 1, **Draft by "BirkNext curated seed (not reviewed)"**: "Draft only — not reviewed". A Draft can be edited in place, which changes its fingerprint but keeps revision 1. |
| Scenario state | `Suggested`, `NeedsClarification`, `ExistingDefinition`, `Executed`, `Verified`. In the shipped seed, 18 scenarios are Suggested and 3 NeedsClarification (`ukjent-xml-versjon`, `bufdata-fil`, `bufdata-retry`); none is Executed or Verified. |
| Source snapshot | `SystemGuideService.CurrentSourcesAsync`: the baseline-resolved snapshot per source role. While a source has alternative versions and no baseline, it is marked `BASELINE_NOT_SELECTED` and no version is cited. |
| Links | Rules name their scenarios (`GuideRule.TestScenarioIds`); scenarios name their steps (`StepIds`); each step has a stage (business-process area). |
| Real seed counts | 21 scenarios. All 21 have an expected outcome text. 3 have test-data text, 1 has observable evidence, 14 rules. |

**Classification:** curated, versioned and reviewable. Not generated, not source-derived, and not reviewed in this installation. The source references are evidence pointers, not proof that the scenarios were generated from source.

## Other scenario sources

| Module | Contains | Mapping in Test Plan Suggestions |
|---|---|---|
| Critical E2E Regression (`CriticalE2EFlowDefinition`, file store) | **Executable automated flow definitions** (steps, mode, environment, Test Data aliases) with immutable versions; results are separate history | Correlation target only (`ScenarioSourceKind.CriticalE2EFlow`, flow id + version). Never a draft: automated technical steps are not manual Test Cases |
| Skolenærvær Testing (journey pack `JourneyScenarioDescriptor`) | **Executable journey scenarios** (`valid-report`, `person-not-found`, `overlapping-report` supported; `correction`, `duplicate-report`, `period-locked` … NotAssessed) | Correlation target (`IntegrationJourney`, pack/journey/scenario id), with the pack's own support text. Not a draft |
| Specification Explorer / Specification Review | Requirements, user stories and acceptance scenarios from documents | Not mapped in this increment (gap TPS-G3). Requirements here are the guide's own rules |
| Test Coverage & Overlap Review, Requirements Traceability | Coverage judgements over tests and requirements | Not mapped (gap TPS-G3); no coverage is derived from drafts |
| Integration Test Suites | Generated technical suites (contracts, chains) | Not mapped; technical definitions |
| Test Execution & Evidence | Historical results | Never read as test status for drafts. A draft is never "executed" |

No two of these were collapsed into one type. Drafts come from one owner, the guide. Executable definitions are linked explicitly.

## Cross-source correlation

- The guide names existing tests as **text** (`ExistingTests`, e.g. "BirkNext: Altinn-reise valid-report", "BirkNext: duplicate-report").
- A candidate link is proposed only when that text contains an executable scenario's **exact identifier**. Several executables with the same identifier are marked ambiguous, and none is chosen.
- A reviewer confirms or rejects every candidate, or links one explicitly.
- Titles are never compared. "Korrigert rapport" is not linked to journey scenario "correction", although the meaning is similar.
- Real result: `gyldig-rapport` → `valid-report` and `duplikat-melding` → `duplicate-report` (named by the guide). Every other scenario has no candidate.

## Test Data and Traceability

- Test Data assets have an immutable `AssetId` (`tda_…`, `TestDataIdentityStore`) and a value `Fingerprint`. They are read through `TestDataCatalogService.ViewAsync`, as labels and metadata only.
- Drafts store asset id, alias, fingerprint, approval, validation and schema status. They never store values. A snapshot re-reads them so it records the revision in force.
- Requirements traceability: drafts carry the guide rules that name the scenario (rule id, title, expected behaviour). Nothing is written to the Traceability module (gap TPS-G3).

## Snapshot and persistence infrastructure

| Existing | Suitable for proposals? |
|---|---|
| Source Analysis snapshots (`SnapshotsController`) | No. These are source analysis results, not plans |
| `system_guide_revisions` (fingerprint + immutability trigger) | The **pattern** is reused: typed JSON, SHA-256 fingerprint, a `BEFORE UPDATE` trigger. The table itself belongs to guides |
| Workspace lifecycle blob (`saved_workspaces`) | Not used. Task 19C fixed the 30 MB failure (`437c455f`), but proposals are shared, durable, server-side records that need their own concurrency and immutability guarantees. The workspace blob is a per-browser auto-save |
| Workspace document body store | Content-addressed document bodies, not structured records |

**Decision:** two tables follow the existing guide-revision conventions:
- `test_plan_drafts`: mutable, with an optimistic revision token;
- `test_plan_snapshots`: immutable, with a trigger, unique `(draft, sequence)` and unique `(draft, fingerprint)`.

No generic snapshot platform was added.
