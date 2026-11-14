# BirkNext — canonical identity and evidence closure

**Date:** 2026-11-14 · branch `008-traceability-first` · local commits only.

Follows `birknext-next-cross-module-gap-closure.md`. No push and no deployment. No remote environment was contacted: no TT02, m2lbdev, Altinn, MU, MM, Azure DevOps or production. No external browser, API, performance or E2E test was executed. `appsettings.Local.json` and `AIAssisted/testdata/` were not touched. The preserved acceptance databases `birknext_scopes_acc_20261113` and `birknext_gap_acc_20261114` were not modified.

Statuses:
- **Verified fixed:** automated tests plus browser verification on the final Release build.
- **Implemented, browser shows honest empty state:** the behaviour is unit-tested; the real data has nothing to show.
- **Not implemented**, with the reason.

## Follow-up

The open items below were closed in `birknext-persistent-evidence-delivery-scope-e2e-closure.md` on the same date:
- Task Alignment results are persisted and survive a reload, with stale detection, including across imports.
- Delivery has an explicit Scope context vs Filter by scope; filtered snapshots and reports change items and metrics.
- Report-vs-report comparison exists, with like-for-like gating.
- Journey and Test Data links are verified end to end in the browser.
- The Critical E2E gate is versioned and uses test type.
- A guided Relationship Review identity workflow resolves unscoped requirement ids.

The table below is kept as it was recorded.

## Gap closure

| # | Gap | Root cause | Fix | Automated | Browser (Release, isolated DB) | Status |
|---|---|---|---|---|---|---|
| 1 | Source-derived journey ids vs scope journey ids | Test Coverage journeys are source-derived (`journey:a>b`); Integration Journeys have pack ids. No rule linked them. | **Association candidates, never automatic matches.** A source journey is a candidate for an Integration Journey only when the journey's ordered component chain is contained in the journey's declared step chain (exact normalised equality, no fuzzy match). A person confirms or rejects the candidate with reviewer and rationale through the shared `ManualReviewDecisionStore` (module `journey-association`). Only a confirmed association lets a scoped journey count as in scope. | Engine: candidates come from the declared chain only; unrelated or partial chains produce none. UI: confirm, reject, reviewer required, and the effect on scope counts. | Real M2LB: 40 source journeys; **0 candidates**, because the copied database registers no Integration Journey whose declared chain contains a source chain. The table keeps journeys under "not confirmed". | **Implemented, browser shows honest empty state** |
| 2 | Test Coverage lacks Test Data asset ids | `QaTestCandidate` had no data link | `QaTestCandidate.TestData` (asset ids from existing Test Data consumer references, `TestCoverageDataLinks`) and `DataNeed` (`NotAssessed` / `Linked` / `RequiredButMissing`) with its basis. Scope rows count Test Data assets by asset id. With no link recorded: "not assessed", never "no data needed". | Engine and presentation tests: linked, missing, not assessed | Real M2LB: "65 QA tests are not assessed for Test Data (no link recorded; never read as "no data needed")"; 0 assets claimed | **Implemented, browser shows honest empty state** |
| 3 | Delivery comparisons don't record scope revisions | Frozen snapshots and reports stored no scope selection | `DeliveryScopeProvenance` (scope id, name, revision number, revision id, resolution rule, server time):<br>• on frozen Functional Quality Scope snapshots, stored inside the existing `items` jsonb as `{items, scopeProvenance}` — legacy array rows load as "not recorded";<br>• on Delivery reports (`ContentJson`).<br>An A vs B snapshot comparison says whether revisions are the same. A percentage is shown **only like-for-like**. Exports carry provenance in the header; ids go in the evidence appendix. | 5 backend tests (persist and read back, legacy load and export, normalise, compare) + 1 freeze test; 2 frontend files (revision id sent, mismatch, same and legacy notices; report header; Generate sends the selection) | Fixture snapshot imported into a new reporting project. Frozen with scope A rev 1, then B rev 1. Each row shows "Final A … rev 1 · approved revisions; canonical key match type+module+feature+id". Compare: `DifferentRevisions`, "Compared deliveries used different scope revisions: … percentages are not like-for-like", "Items A → B: 6 → 6 (0; percentage not shown: not like-for-like)" | **Verified fixed** |
| 4 | Task Alignment in Feature Readiness not browser-verified | No matching Task Alignment result had been produced | Synthetic fixture `ta-fixture.zip`:<br>• TaFixture project with Payments and Billing modules, both naming FR-001;<br>• one checked task with its source file, one missing source, one missing test file, one gated task.<br>Specification and Tasks for Payments are selected in their explorers, Task Alignment Review runs, then Implementation Review opens inside the same browser session. | Existing component tests | "4 Tasks analyzed · 4 Direct spec links". Feature Readiness for "Pay an invoice": Task Alignment "**3 linked to the specification**" (hash-bound to the same tasks document), next to "traceability, not implementation or verification evidence". The gate still blocks T004. After a full page reload: "Not assessed — no current Task Alignment result", because the result is session-scoped and is never claimed stale. | **Verified fixed** |
| 5 | Impact scope not browser-verified | — | Integration Topology → Shared system identities → Impact | Existing test | Scope over 23 canonical systems. Impact on Altinn lists "Final systems … (Approved, revision 1) · 1 impacted member: Altinn (the changed item)" and "their approved revisions are not changed" | **Verified fixed** |
| 6 | Delivery scope not browser-verified | No Delivery project in the isolated DB | Reporting project created in the disposable DB; fixture snapshot imported through the UI | — | "Shared scope filter (Final A … rev. 1): 0 items named by the selected scope … 6 outside of 6 in this Functional Quality Scope. Matched by canonical identity". This is correct: the M2LB scope does not name TaFixture items. | **Verified fixed** |
| 7 | Health deep links don't focus the module | The Health page ignored the query | The Explorer link carries `module` and `document`. `GeneratedDocumentationHealthPage` passes them to the panel and `AutodocDiagnosticsSection`, which focuses that module, or says it is not found. | Health page, panel and section tests | "Showing module MeldingsutvekslerReceiver from the link · document MeldingsutvekslerReceiver/autodoc/CHANGELOG.md." | **Verified fixed** |
| 8 | Health summary omits authored-vs-generated drift | The Explorer sent no authored artifacts | The request includes the import's authored artifacts. The summary is split into three groups: generated documentation health, source/document mismatch, and authored documentation drift. The drift group is `assessed` / `partial` / `not-assessed`, with its basis named. | Context tests | Authored drift `assessed`: "Authored/generated consistency candidates: 0", comparing the Constitution and Specification by name | **Verified fixed** |
| 9 | Cross-document search requires an open document | The toggle lived in the document toolbar | Library search on the Explorer landing (`gde-library-search`): runs with no document open, type filter, count, open result, and "Back to search results" with the query kept | Library search, open, back (test fixed: FluentAssertions `Equal(params)` swallowed the reason; heading waits made async-safe) | "Skoletjenesten": 10 matches in 5 documents with no document open; opening a result highlights it; Back restores the results and the query | **Verified fixed** |
| 10 | Critical E2E: UI Smoke vs UI Functional | A flow stored only Critical vs Diagnostic | `CriticalE2ETestType` (Unknown, UiSmoke, UiFunctional, BusinessE2E, IntegrationApi, Other), **separate from criticality**:<br>• set on suggestion approval, editable in the flow editor;<br>• the fingerprint ignores the default, so legacy flows keep fingerprint and version and load as "Not classified (legacy)".<br>The scope panel has a test-type × counts-toward table: UI smoke never counts, UI functional is functional coverage only. | 1 backend file (6 cases) + 1 frontend file | Table rendered with all seven rows, all zero (the copied DB has no flows); no run call | **Verified fixed** (no real flows to classify) |
| 11 | Source-only M2LB requirements lack module/feature identity | The copied canonical interpretation came from Source Analysis only, so requirement ids are `Requirement\|FR-012` | **Not invented.** Feature Readiness counts unscoped identities. When every canonical requirement is unscoped, it says why nothing resolves and links to Relationship Review: include a Delivery snapshot in Analyze relationships. Nothing is matched by display id. | New component test (notice shown, `0 of` resolved, no guess); existing test asserts no notice when scoped ids exist | Real M2LB keeps "0 of N resolved"; the TaFixture shows "FR-001 · identity not resolved" | **Implemented**. The data itself stays unscoped until a person reinterprets with Delivery evidence. |

## Identity rules (unchanged principle, extended)

- **Matching:** consumers match only by stable key: canonical entity key, flow id, Test Data asset id, revision id. A display id such as `FR-001` exists in several Artifact Packs and is never enough.
- **Association:** source journeys and Integration Journeys are associated **only** by a person's confirmed decision. The rule proposes candidates from an exact chain containment.
- **Provenance:** frozen and reported Delivery results now record the scope revisions they were made under. Comparisons across different revisions are labelled and give no percentage.
- **Classification:** criticality and test type are separate axes. The Critical E2E release gate (`CriticalE2EReleaseRules`) is **unchanged**. Making the release gate read the test type would reclassify existing flows, so that is left as a product decision.

## Verification on the final code

- **Builds:**
  - Backend tests: 0 errors.
  - Frontend tests: 0 errors.
  - Release publish of the API and web app: succeeded.
- **Backend focused** (Critical E2E, Test Coverage, Manual Review, Scope Registry, Delivery Reporting, Implementation Gap, Scope Member): 452 passed, 1 skipped.
- **Frontend focused:** 1539/1539. The Generated Documentation suite was repeated three times: 76/76 each run.
- **Full frontend regression:** 8892/8892.
- **Backend partitions** (foreground, sequential, same build):

  | Partition | Passed | Skipped | Failed |
  |---|---|---|---|
  | 1 | 1052 | 2 | 0 |
  | 2 | 1099 | 5 | 0 |
  | 3 | 859 | 0 | 0 |
  | 4 | 652 | 1 | 0 |
  | 5 | 524 | 3 | 0 |
  | 6 | 2482 | 8 | 0 |
  | **Total** | **6668** | **19** | **0** |

- **Browser acceptance (Edge, Release build):**
  - Setup: API :5097 on the fresh disposable `birknext_final_acc_20261114`, cloned from the preserved template. Web :5197. Ports 5000 and 5173 untouched. Non-localhost requests aborted: none attempted.
  - Result: **57/57**. This includes no horizontal overflow at 1440, 1280, 1024 and 768 px on seven pages: Implementation Review, Test Coverage, Critical E2E, the Explorer, the Delivery scope tab, Integration Topology and Task Alignment. There were no page errors and no 5xx responses.
  - Screenshots: session scratchpad `acc/shots-final/`.

## Remaining limitations (honest)

- **Journey associations** need an Integration Journey whose declared step chain contains the source chain. The copied database has none that matches, so the browser shows 0 candidates. The confirm and reject flow is covered by component tests only.
- **Test Data links** come from recorded Test Data consumer references. The copied QA tests have none, so all 65 show "not assessed".
- **Critical E2E test type:** there are no flows in the isolated database, so badges were not seen on a real flow. The release gate does not yet use the test type (product decision above).
- **Delivery:**
  - Selecting a shared scope records provenance but does not narrow the frozen membership or the report content. That is the existing "narrow this view only" contract.
  - There is no report-vs-report comparison view, so the two-sided notice exists for frozen scope snapshots only.
  - Frozen snapshots with provenance store `{items, scopeProvenance}` in the `items` column. Nothing else reads that column.
  - Backend tests do not run `GenerateAsync` end to end: the in-memory fixture lacks report-source rows.
- **Task Alignment** results are browser-session state. After a reload, Feature Readiness says "Not assessed" until Task Alignment runs again. Persisting them would be a separate change.
- **M2LB requirement identities** stay unscoped in the copied interpretation. Resolving them needs a person to run Analyze relationships with a Delivery snapshot.
