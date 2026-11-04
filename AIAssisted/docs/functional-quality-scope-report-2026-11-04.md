# Functional Quality Scope — report (2026-11-04)

Branch `008-traceability-first`. Not pushed. Commits are listed in §44.

## FUNCTIONAL QUALITY SCOPE AUDIT MAP

| Capability | Already present before this task | Owner | Used by Functional Quality Scope as |
|---|---|---|---|
| Features, user stories, scenarios, acceptance criteria, requirements | Yes — parsed per Delivery snapshot. Tables: `feature_snapshot`, `user_story_snapshot` (Kind `UserStory`/`Scenario`, `Removed`), `acceptance_criterion_snapshot`; requirements in `feature_snapshot.requirements` | Delivery Management (`delivery_reporting`) | The **only** item source, newest snapshot per project. Read, never copied. |
| Task ↔ story relations (ExplicitStory, FunctionalGroup, ScenarioGroup, SharedFoundational, RemovedStoryReference, Unresolved) | Yes — Phase 1 `WorkItemInterpreter` | Shared interpretation | Not needed: the scope lists specification items, not tasks. Technical work is excluded by quality classification, not by task relation. |
| Domain flows (components, exact project aliases, paths) | Yes — `SkoleNaervaerTopologyProfile` (`ITopologyProfile`) | Integration Topology / Test Pack | Smart roots `profile:{id}`, and Upstream/Downstream relations. |
| Canonical Capability + IMPLEMENTS relations | Yes — Phase 2 `canonical_interpretation` | Canonical engine | "Related capability". Confirmed, explicit or derived relations include; candidates are only suggested. |
| Canonical VERIFIES relations (test → requirement/story) | Yes — Phase 2 | Canonical engine | Coverage column. |
| Test Coverage Review engine | Yes — `test-coverage-review` page | Test Coverage | Linked, **not** recalculated. Scope coverage reads the same canonical VERIFIES relations (see §37). |
| Test Data stable `TestDataAssetId` + resolver | Yes — shared foundations | Test Data | Required-asset state on the test-design assessment. |
| Snapshot Registry (frozen lineage) | Yes — shared foundations | Snapshots | Frozen scope membership, registered as Delivery / `functional-scope-snapshot`. |
| Status vocabulary (`SemanticStatusBadge`, `MetaBadge`) | Yes | Shared UI | All coverage and Test Data badges. |
| Quality category classification | **No** | — | New: `FunctionalQualityClassifier` (`functional-quality/1`). |
| Scope definitions and explicit references | **No** | — | New: `functional_scope`, `functional_scope_reference`. |
| Test-design assessment (testability, verification types, required Test Data) | **No** | — | New: `test_design_assessment`, keyed by stable item key. |
| Formal test-case artifacts | **No** source in BirkNext | — | Type exists (`TestCase`); the count shows "None recorded". Scenarios are never relabelled as test cases. |

## 1. Existing capabilities reused
- **Delivery's parsed specification items** — the scope does not parse anything itself.
- **Domain topology profile** for flow membership.
- **Canonical Capability, IMPLEMENTS and VERIFIES relations**, used for related capabilities and coverage.
- **Test Data resolver.**
- **Snapshot Registry.**
- **Status vocabulary.**
- **Delivery Reports page shell:** project selector, tabs, query-string conventions.

## 2. Extent of the feature already present
Roughly 70% of the data already existed: items, relations, coverage relations, Test Data identity and frozen-lineage infrastructure. What was missing:
- quality classification;
- scope resolution;
- persisted scope definitions and references;
- test-design assessment;
- the UI.

## 3. New functionality actually required
- `shared/FunctionalQualityScope.cs`: contracts and the classifier.
- `FunctionalQualityScopeService`: resolution, custom scopes, assessments and freeze.
- `FunctionalQualityScopeController`.
- 4 tables, added in one migration on `delivery_reporting` only.
- A registry adapter extension.
- `FunctionalQualityScopePanel` with its presentation rules and API client.
- A Skolenærvær deep link.

## 4. Placement in Delivery Management
Delivery Reports → tab **Functional Quality Scope** (`delivery-reporting?tab=scope&scope=…`). It reuses the page's reporting-project selector; there is no new navigation entry.

## 5. Canonical entities consumed
- `Feature` (identity `module/featureKey`, normalised).
- `Capability`.
- `Requirement` / `UserStory`: the VERIFIES targets.
- `Test`: the VERIFIES sources.

## 6. Canonical relations consumed
- `Implements` (Feature → Capability), active and not rejected.
- `Verifies` (Test → Requirement/UserStory), active and not rejected.

Resolution comes from `Explicit`/`Derived`/`Candidate`; review state from `Confirmed`/`Rejected`.

## 7. Built-in smart scope model
`AvailableAsync` lists three kinds of scope:
- **Domain flows**: one per topology profile with at least one mapped module.
- **Modules**: one per Delivery module.
- **Custom scopes.**

Roots are `profile:{id}`, `module:{name}` or, internally, `*`. Mapping a module to a component is exact: either the component name, or a dot-segment of one of its declared aliases. A segment that several components share (`M2LB`, `Api`, `Host`) identifies none of them, and there is no similarity matching.

## 8. Skolenærvær scope
Measured on real M2LB data (`birknext_ci2`, snapshot "Current 2026-11-03").

**Direct only** — 243 items:
- Modules **MeldingsutvekslerReceiver** (→ MM) and **SkoleService** (→ Skoletjenesten), from the profile's primary flow "Rapportmottak".
- 13 user stories, 123 acceptance criteria, 107 requirements.

**Include related** — 399 items:
- Adds **Person** and **SkoleAdapter** as *Upstream* (declared flows "Person events" and "Utdanningsdata").
- 5 technical items excluded.

AutorisasjonService, Frontend and the other modules are never included: no declared flow or confirmed capability connects them.

## 9. Person scope
`module:Person` gives 81 KB and its own features. With "Include related" it adds **Skoletjenesten's module (SkoleService)** as *Downstream*. The neighbour is found through "Person → Skoletjenesten", the same one-hop rule as above.

## 10. Direct vs related logic
- **Direct** = the root's own modules.
- **Upstream/Downstream** = modules one step away on any declared flow of a profile whose component is Direct.
- **RelatedCapability** = another module's feature that implements the same canonical Capability, through an explicit, derived or confirmed relation.

Candidates are listed as *Suggested* and never included. Related modules are always listed with their reason, and are included only when "Include related" is on.

## 11. Inclusion reason / explainability
Every item carries `Relation`, `Inclusion` and `Reason`. For example:

> Owned by MM on the Skolenærvær flow "Rapportmottak (normal report)" (declared domain profile) · feature …

The UI shows this under "Why included", together with the category rule id.

## 12. Technical-item exclusion
- Items are classified `Technical` by rule `fq.technical.migration` (migrations, refactors, MediatR/Wolverine, Terraform/pipeline, ADR, source translation).
- A whole feature is technical when its **title** is technical (`fq.technical.feature`).
- Technical items are excluded by default and counted ("5 technical item(s) excluded").
- "Include technical work" shows them.
- An explicit include overrides the technical exclusion.

## 13–19. Quality classification
Rule set `functional-quality/1`. It is deterministic and bilingual (Norwegian and English), and each result names its rule. Rules are checked in order; the first match wins:

1. Technical
2. Privacy (kode 6/7, personvern, GDPR, skjerming, …)
3. Security (tilgang, autoris*, autentis*, rolle, token, innlogging, …)
4. Performance (ytelse, responstid, latency, throughput, …)
5. Reliability (retry, idempotent, resync, ikke går tapt, dead-letter, …)
6. Data quality (valider*, XSD, skjema, duplikat, konsistens, …)
7. Compliance (forskrift, lovkrav, oppbevaring, revisjonsspor, …)
8. Availability (tilgjengelighet, oppetid, failover, …)

Anything else is **Functional**. Empty or very short text is **Unclassified**: shown, never hidden.

On real Skolenærvær data with related modules: Functional 250, Privacy 47, Reliability 39, Data quality 29, Security 23, Compliance 7, Performance 4.

## 20–24. Filters
All filters are pure rules in `FunctionalQualityScopePresentation.Apply`, and they combine:
- artifact type;
- quality category (clickable category chips);
- module;
- relation;
- coverage;
- Test Data state;
- testability;
- priority (shown only when the data has priorities);
- free-text search.

## 25. Custom scope model
`ScopeDefinition` = name, description, smart roots, include-related flag, explicit includes and explicit excludes, version.

- **Persistence:** `functional_scope` holds the roots as jsonb; `functional_scope_reference` holds one row per item key with mode Include or Exclude.
- **Versioning:** the version increments on every reference change.
- **Integrity:** names are unique per project.

## 26. Drag/drop
- Candidates are `draggable`.
- While editing, an always-present **drop zone** surrounds the scope list. It exists even when the scope is still empty, which was a bug found in browser acceptance.
- Dropping sends `POST …/references` with `ExplicitInclude`.
- Verified in Edge with real HTML5 drag events (dispatched with a shared DataTransfer).
- Playwright's synthetic `dragTo` does not reach Blazor's handlers in this setup; that is a test-tool limitation.

## 27. Add/Remove accessibility fallback
Every candidate has an **Add** button. Every scoped item has **Exclude**; explicitly included items have **Remove**. All are keyboard-operable buttons with the same API calls as drag/drop.

## 28–30. Explicit include, exclude, hybrid
Precedence (`FunctionalQualityScopeText.Precedence`): **explicit exclusion > explicit inclusion > smart relation-based inclusion > not included**.

- Each item has one reference row; switching between include and exclude replaces it.
- A scope can combine smart roots (e.g. `module:Person`) with manual includes from other modules and manual excludes. This is tested.

## 31. Saved custom filters
**Not implemented.** Filters are view state only. Debt: persist the filter on the scope definition, or in the URL.

## 32. Canonical-ID persistence
References use stable keys: `story:{module}/{featureKey}/{storyKey}`, `ac:{module}/{featureKey}/{criterionKey}` and `req:{module}/{featureKey}/{reqKey}`. They survive re-import: a new snapshot has new rows but the same keys. This is tested.

## 33. Removed/stale items
An explicitly included key that is missing from the newest snapshot stays visible:
- **Unavailable in current source** (status Unresolved), with a dashed row;
- not openable;
- counted in the summary.

It is never silently dropped.

## 34. Scope snapshot/version behaviour
- **Freeze scope snapshot** stores the exact membership (key, type, category, relation, inclusion, coverage) together with:
  - the scope name and version;
  - the Delivery snapshot id;
  - the rule sets `functional-quality/1;work-items/1`.
- It is registered in the Snapshot Registry as `Delivery` / `functional-scope-snapshot`, FrozenReportEvidence, with lineage `DerivedFrom` its Delivery snapshot. Verified live through `GET api/snapshots`.
- Deleting a scope keeps its frozen snapshots; only the scope id is cleared. This is tested and was verified live: 2 frozen rows kept, 0 scopes, 0 references.

## 35. Delivery integration
- It is a tab inside Delivery Reports with the same project selector.
- Items come from Delivery snapshots. Delivery reports and comparisons are untouched.

## 36. Traceability integration
The item detail links to `artifact-traceability?q={id}` and to Correlation Review. Nothing is recomputed.

## 37. Test Coverage integration
- Coverage reads the canonical **VERIFIES** relations, the same evidence Test Coverage Review consumes, and links to `test-coverage-review?q={id}`.
- An id that **several features use** is never claimed as automated coverage; it becomes "Candidate coverage only" with the reason. Browser acceptance found that real M2LB reuses `US1`, `SC-004`, `AC-1` and even `FR-001` across features. As a result, on real data all VERIFIES links currently show as candidate or none: the canonical engine keys requirements by bare id (feature-pack identity is Phase 3 work).
- Candidate relations are never shown as automated coverage.
- When no interpretation exists, items show "Not assessed" plus a limitation.

## 38. Test Data integration
The test-design assessment stores required **stable `TestDataAssetId`s**, resolved through `TestDataCatalog.ResolveReference`:
- all resolve → *Available*;
- any missing → *Missing required*;
- an empty list → *No Test Data required*;
- no assessment, or no catalog → *Unknown*, never claimed available.

## 39. Skolenærvær Testing deep link
The Skolenærvær Testing overview has a "Functional scope" row → **Open Functional Quality Scope**, which goes to `delivery-reporting?tab=scope&scope=profile%3Askolenaervaer`. Verified in the browser: it opens "Skolenærvær".

## 40. API/payload design
Base path: `api/delivery-reporting/projects/{id}/scope`. All aggregation is server-side, and detail is loaded lazily.

| Purpose | Method and path |
|---|---|
| List available scopes | `GET available` |
| Resolve a scope (summary + items + related modules + limitations) | `POST resolve` |
| Builder candidates, searched and bounded | `GET items?search=&take=` (≤200) |
| Lazy item detail | `GET items/detail?key=` |
| Create a scope | `POST scopes` |
| Update a scope | `PUT scopes/{id}` |
| Clone a scope | `POST scopes/{id}/clone` |
| Delete a scope | `DELETE scopes/{id}` |
| Add or switch an explicit reference | `POST scopes/{id}/references` |
| Remove an explicit reference | `DELETE scopes/{id}/references?key=` |
| Save a test-design assessment | `PUT assessments` |
| Freeze membership / list frozen snapshots | `POST/GET snapshots` |

## 41. Performance
Isolated stack on throwaway `birknext_ci2` (real M2LB snapshot), warm API.

| Call | Payload | Latency |
|---|---|---|
| resolve Skolenærvær, direct (243 items) | 278 KB | 0.05–0.39 s |
| resolve Skolenærvær, related (399 items) | 432 KB | 0.11–0.17 s |
| resolve `module:Person` | 82 KB | 0.09 s |
| resolve `module:Person`, related | 243 KB | 0.17 s |
| resolve `module:Frontend` | 198 KB | 0.03–0.18 s |
| available | 1.5 KB | 0.19 s |
| builder candidates (take 120, search) | 120 KB | 0.06 s |
| item detail | 9.7 KB | 0.17 s |

The builder list was **1.35 MB** before server-side search/take; it is now 120 KB.

- **Browser:** first render of the tab with 243 items took 3.2–3.6 s, including WASM boot.
- **DB growth:** the 4 tables total about 184 KB, mostly the empty-table baseline. A frozen snapshot is about 190 bytes per item (91 items = 17 KB).

## 42. Automated tests

**Backend** — `FunctionalQualityScopeTests`, 23 tests:
- classification theory (8 cases) and technical-feature rule;
- alias mapping, including generic segments;
- Skolenærvær direct vs related;
- technical exclusion;
- artifact types / no formal test cases;
- precedence;
- re-import stability and unavailable items;
- clone / duplicate name / delete keeps frozen snapshots;
- registry registration and lineage;
- VERIFIES coverage, including ids shared across features;
- confirmed vs candidate capabilities;
- test-design assessment and Test Data state;
- empty project limitation;
- server-side candidate bounding.

**Frontend** — `FunctionalQualityScopeUiTests` (8) plus a page test:
- filters and grouping;
- vocabulary mapping and deep-link href;
- the deep-linked scope renders with type, reason and coverage;
- related/technical toggles are sent to the server;
- custom-scope drop, Add and Exclude;
- drop into an empty manual scope;
- unavailable item;
- lazy detail and assessment save;
- the page renders the real panel, not an unknown HTML element (regression for a missing `@using` found in browser acceptance).

**Full suites:**
- frontend **7974 / 7974 passed** (was 7965);
- backend: see §42a.

## 43. Browser acceptance
Isolated stack: API :5099 on `birknext_ci2`, web :5199, Edge. Every non-localhost request was aborted.

**Viewports:** light/dark × 1440/390. In every combination:
- 243 → 399 items;
- no horizontal overflow, no off-screen elements, no uppercase badges;
- minimum text contrast **6.85**;
- the detail drawer stays inside the viewport;
- no console errors;
- **no external hosts**.

**End-to-end flow:**
1. create a scope from Person;
2. Add/drop;
3. Exclude (version 2);
4. freeze (91 items);
5. delete;
6. open the Skolenærvær deep link.

**Defects found and fixed during acceptance:**
- the panel did not render (missing namespace import);
- the empty manual scope had no drop target;
- coverage was over-claimed for ids reused across features;
- the builder downloaded 1.35 MB.

## 44. Commits
1. `delivery: functional quality scope model, classifier and persistence`
2. `delivery: functional quality scope resolution, API and registry lineage`
3. `ui: Functional Quality Scope tab, builder and Skolenærvær deep link`
4. `docs: functional quality scope report`

(Hashes are in the git log after this report's commit.)

## 42a. Backend full suite
First full run: **5744 passed, 13 skipped, 3 failed**, in the scratch worktree, run in parallel with the frontend suite and the live stack.

1. `DeliveryReportingDatabaseTests.CleanDatabase_ReportingMigrationCreatesOnlyTheDeliveryReportingSchema`. This test pins the `delivery_reporting` table list, and it failed on the 4 new tables. The pin was updated deliberately. It passes.
2. `LoopbackHttpTestServerTests.UnexpectedHandlerFailure_StillFailsTheOwningTest` (17 s) failed under load and passes when rerun in isolation.
3. `PerformanceRequestResponseTests.GraphQL_variables_use_runtime_values_and_errors_are_separate_from_the_http_status` (17 s) also failed under load and passes when rerun in isolation.

Net result: **5747 passed, 13 skipped, 0 failed**.

## Remaining debt
- **Saved custom filters (§31).**
- **Canonical relations are not filtered by Target Environment scope.** Relations from several interpretation scopes are combined.
- **Coverage on real M2LB is all candidate/none** until requirement identity is feature-pack scoped (queued Phase 3, #22).
- **Formal test cases:** no source in BirkNext yet.
- **Delivery Overview** has no summary card for scopes yet. Only the Skolenærvær deep link exists.

## Confirmations
- No new duplicate source of truth was created: items are read from Delivery snapshots, and only definitions, references and assessments are stored.
- User Stories, Scenarios and Acceptance Criteria are referenced by stable key, never copied. Frozen snapshots store keys plus classification, not text.
- Formal Test Cases remain distinct: scenarios are labelled "Acceptance scenario", and the Formal test cases count shows "None recorded".
- Technical tasks are not listed; technical migration/refactoring items are excluded from the default view.
- Functional, security, privacy, performance, reliability, data-quality, compliance and availability behaviour is included.
- Selecting Skolenærvær resolves the cross-system items on its declared flows. Selecting Person resolves Person's features, plus Skoletjenesten when related is on.
- Direct only and Include related produce different scopes, and each can be explained.
- Every automatically included item explains why it is in scope.
- Smart resolution uses declared flows and canonical relations, not text search.
- Custom scopes support drag and Add of existing user stories, scenarios, criteria and requirements. Formal test cases have no source to add yet.
- Drag and Add create references only. Explicit includes and excludes survive refresh and re-analysis while the stable key exists.
- Custom scopes combine smart roots with manual curation.
- Test Coverage evidence is reused (the same canonical VERIFIES relations, linked to Test Coverage Review). It is not re-derived from source.
- Test Data is referenced by stable id, never copied.
- Traceability is linked, not recreated.
- The global status vocabulary is used for coverage and Test Data states.
- Frozen scope membership uses the shared Snapshot Registry.
- Historical Delivery artifacts are untouched. Frozen scope snapshots record their rule-set versions and never change.
- The feature is a thin projection over existing BirkNext capabilities.
- No real external traffic was generated.
- `appsettings.Local.json` was not intentionally modified, and it is excluded from the commits.
- User testdata was preserved; `AIAssisted/testdata/` is untracked and excluded.
- Nothing was pushed.
