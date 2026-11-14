# BirkNext — cross-module gap closure (after 5fbf5395, 46d61452, 5834e288)

**Date:** 2026-11-14 · branch `008-traceability-first` · local commits only. No push, no deployment, no remote environment contacted. `appsettings.Local.json` and `AIAssisted/testdata/` were not touched.

## Gap closure

Statuses: **Verified fixed** (automated and browser verification on the final build) · **Implemented, not fully verified** · **Blocked by documented technical dependency** · **Not implemented**.

| # | Gap | Root cause | Implemented fix | Automated verification | Browser verification | Final status |
|---|---|---|---|---|---|---|
| 1 | Backend partition 6 not run | It was stopped twice because the machine was low on memory while running in the background | Ran in the foreground, alone, on the final code | 2476 passed, 8 skipped, 0 failed (partitions 1–6 total: 6652 passed, 19 skipped, 0 failed) | — | **Verified fixed** |
| 2 | Feature Readiness does not use Task Alignment | Task Alignment results live in the browser session (`TaskAlignmentSessionService`), keyed by spec and tasks hashes | The open feature shows Task Alignment status per task, **only** when the stored result's tasks hash equals this feature's tasks document. Otherwise "Not assessed — run Task Alignment Review". Never counted as implementation. | `Canonical_requirements_resolve…` asserts Not assessed when no result exists | "Not assessed — no current Task Alignment result" on the blocked feature (real M2LB) | **Implemented, not fully verified**: a matching Task Alignment result was not produced in the browser |
| 3 | Feature Readiness does not use Canonical Interpretation | The engine never read canonical evidence | Each requirement id is looked up in the shared requirement catalog by its exact source identity (`module/feature/id`). Unresolved ids are listed as identity gaps. Established Scenario→Verifies links (explicit, derived or confirmed) are counted. New column *Requirements (canonical)*. | Exact match (FR-019 resolves; the same FR-036 in another pack does not); established-scenario count | Real M2LB: "0 of 12 resolved …" on every feature. The source-only interpretation holds **unscoped** ids (`Requirement\|FR-012`), so nothing resolves. That is reported, not guessed. | **Verified fixed** (behaviour). The data gap is recorded below. |
| 4 | Feature Readiness does not use Scopes | No scope consumer | Shared `ScopeFilter`. A feature is in scope only through its resolved canonical requirement keys. Unresolvable features stay visible, marked *Scope association not confirmed*. | In-scope feature through canonical key | Real M2LB: 0 in scope, 9 "not confirmed" (shown, marked), 0 hidden | **Verified fixed** |
| 5 | Critical E2E does not use Scopes | No flow member kind | `ScopeMemberKind.CriticalE2EFlow` (appended; JSON-compatible), with a catalog and check (`ICriticalE2EStore.Flows(environment)`; unknown or other-environment ids refused). Page `ScopeFilter`: flows named, prepared, executable, executed and passed, from existing fields. Critical flows are separated from smoke/diagnostic flows, which never count. Selecting a scope never runs or enables anything. | 3 backend + 4 frontend tests (no run started, smoke excluded) | Real M2LB environment: scope filter applied, 0 flows named, no run call made | **Verified fixed** (with no real flows to name) |
| 6 | Test Coverage does not use Scopes | No scope consumer | Shared `ScopeFilter`. A scope comparison table (in scope / not confirmed / whole project), deduplicated (counted once, "from N scopes"). Matching by stable identity only: QA test `e2e:{flowId}` ↔ flow member; requirements by module/feature/id; journeys and Test Data reported as not mappable. | 4 frontend tests | Real M2LB, two overlapping scopes: "counted once (1 member is named by more than one scope)"; requirements 0 in scope, 3 not confirmed, 65 whole project | **Verified fixed** for requirements, tests and flows. Journeys and Test Data are **Blocked** (no stable shared identity; see limitations). |
| 7 | Impact Analysis does not use Scopes | No scope consumer | The shared `CanonicalImpactSummary` lists scopes whose newest revision names the changed or impacted entity (canonical key = member id). Each is marked the changed item / direct / downstream / candidate relation only (not confirmed). Approved revisions are never changed. | `An_impact_trace_lists_the_scopes…` | Not exercised in the browser (Relationship Review entity inspector) | **Implemented, not fully verified** |
| 8 | Delivery does not use Scopes | No adapter between Functional Quality Scope items and shared scopes | `FunctionalQualityScopePanel`: the shared `ScopeFilter` narrows the view by canonical key (type + `CorrelationRules.Scoped(module, feature, id)`; acceptance criteria and test cases follow their parent), with a deduplicated count and the scope revisions named. The Functional Quality Scope, its saved definitions and frozen snapshots are unchanged. | `Delivery_items_map_to_canonical_keys…` | Not verified: the isolated database has no Delivery project or snapshot | **Implemented, not fully verified** |
| 9 | No connection graph | Only a list and a three-column map | `SystemConnectionGraph`: Blazor SVG, no script and no new library. The focus system sits in the middle, incoming on the left, outgoing on the right. Edges are styled by evidence state (dashed = candidate or needs review; never observed without runtime evidence). Zoom, drag/keyboard pan and Fit. A node refocuses; an edge opens its evidence row. At most 14 per side, the rest counted. Without a focus: "choose a system" quick links (no hairball). | `The_connection_graph_draws…` | Real M2LB: graph for Skoletjenesten, 12 edges all "from source evidence"; an edge opens its evidence; Relationship Review round trip keeps the focus | **Verified fixed** |
| 10 | Reviewer decisions not browser-verified | The real SkoleService run had no top-level finding | Synthetic fixture (`dqr-findings.zip`: vague and TBD requirements, an unchecked constitution). Decisions now take a rationale, show history, and keep the input on a failed save for retry. | Rationale, history, failed save then retry, rule result unchanged | Fixture: decision recorded (reviewer, timestamp, rationale) → reload → re-run → same decision and rationale, history (1), issue card unchanged | **Verified fixed**. Concurrency: the store is append-only, latest wins, history kept; there is no stale-write conflict to raise. |
| 11 | Only sequence and flowchart diagrams drawn | The parser handled two types | State, class (relationships) and ER (relationships) diagrams are parsed into the existing graph layout. Other types (gantt, pie, journey, mindmap, …) are now recognised and fall back with a reason naming the type. Labels stay plain text. | Parse and render per type; hostile labels; gantt reason | Fixture: `stateDiagram-v2`, `classDiagram` and `erDiagram` drawn; gantt reason shown; zoom causes no page overflow; expanded dialog | **Verified fixed** |
| 12 | No cross-document search | Search covered one document | A toggle extends search to every generated document of the selected import: bounded, debounced and cached, grouped by document with snippets and counts, and a type filter. Opening a result switches module, highlights the term, and offers "Back to …". | Grouping, import isolation, open, highlight, back | Real M2LB: 5 results across 4 modules; opening one highlights it; Back returns to the previous document | **Verified fixed** |
| 13 | Health findings not shown in the Explorer | The Explorer only linked to Health | Provenance shows the **existing** Health run for the document's snapshot (`RunAsync`, once per import and snapshot): parse problems for this path, missing expected documents of the module, source and cross-artifact candidates, freshness. Without a run: "Health not evaluated for this document". Deep link to Health. | Relevant findings only; not evaluated cases | Real M2LB Person data flow: evaluated, "Required documents missing in module Person: 1"; never "healthy" | **Verified fixed**. Health module focus via query is not implemented (see limitations). |

## Architecture (unchanged principle)

Source evidence → canonical interpretation (identity, relations, review decisions) → the shared Scope Registry (versioned, project-scoped, catalog-validated members) → consumers.

Each consumer:
- reads `api/scopes/selection` (approved revisions, deduplicated, with attribution) or `api/scopes/membership`;
- matches **only by stable key** (canonical entity key, flow id, asset id);
- keeps its own results.

No new registry, scope engine, correlation engine, search backend or decision store was added. Reviewer decisions use the shared `ManualReviewDecisionStore`. Selecting a scope never authorizes execution.

## Cross-module integration

| Module | Scope support | Shared evidence | Tests | Remaining limitations |
|---|---|---|---|---|
| Systems & Scopes | Owner | Canonical systems and relations, Integration Topology, review decisions | page and backend suites | — |
| Feature Readiness (Implementation Review) | Filter by canonical requirement keys; "not confirmed" kept visible | Canonical requirement identity, established scenario links, Task Alignment (hash-bound) | component tests | M2LB source-only interpretation has unscoped requirement ids, so nothing resolves until a Delivery snapshot is interpreted |
| Critical E2E Regression | Flow members; named / prepared / executable / executed / passed | Flow store, existing run status | 4 + 3 tests | UI Smoke and UI Functional are indistinguishable on a flow (both Diagnostic) |
| Test Coverage & Overlap | Requirements, tests, flows; deduplicated | Review engine output | 4 tests | Journeys (source-derived ids) and Test Data (no asset ids) have no shared identity |
| Impact (shared summary) | Scopes naming impacted entities | Canonical impact paths | 1 test | Not browser-verified |
| Delivery (Functional Quality Scope view) | View filter by canonical key | Canonical identity function | 1 test | Not browser-verified (no Delivery project in the isolated database); Delivery Reports comparisons do not yet show scope provenance |
| Test Data, Test Plan Suggestions, Integration Journeys | (earlier commits) | — | — | — |

## Verification on the final build

- **Backend focused:** 312 passed, 1 skipped.
- **Frontend focused:** 1312/1312, plus 2 adapter tests.
- **Full frontend regression:** 8856/8856.
- **Backend partitions** (foreground, sequential, same build):

  | Partition | Passed | Skipped | Failed |
  |---|---|---|---|
  | 1 | 1052 | 2 | 0 |
  | 2 | 1099 | 5 | 0 |
  | 3 | 852 | 0 | 0 |
  | 4 | 652 | 1 | 0 |
  | 5 | 521 | 3 | 0 |
  | 6 | 2476 | 8 | 0 |

- **Release publish** of the API and web app: succeeded.
- **Browser acceptance (Edge):**
  - Setup: API on :5097 using `birknext_gap_acc_20261114`, a fresh disposable clone made from the preserved `birknext_scopes_acc_20261113`, which was not modified. Web on :5197. Ports 5000 and 5173 untouched; no external request.
  - Result: **52/52**, including no horizontal overflow at 1440, 1280, 1024 and 768 px on Systems & Scopes, Implementation Review, Test Coverage, the Explorer and Document Quality Review; no page errors; no 5xx responses.
  - Screenshots: session scratchpad `acc/shots-gap/`.

## Remaining limitations (honest)

- **Unscoped canonical requirements.** The copied M2LB interpretation was built from Source Analysis only; its requirements are `Requirement|FR-012` with no module or feature. Feature Readiness, Test Coverage and Delivery therefore confirm no scope association on real data. Interpreting with the Delivery snapshot (Relationship Review → include Delivery) gives scoped identities. This was not done in the isolated run.
- **Critical E2E classification.** A flow stores only Critical vs Diagnostic. UI Smoke and UI Functional suggestions both become Diagnostic and are counted together as "never counts".
- **Test Coverage:** journeys (`journey:a>b`) and Test Data have no shared identity, so they are reported, never matched.
- **Delivery Reports:** historical comparison does not yet carry scope revision ids in report provenance. Only the Functional Quality Scope view filters.
- **Health:** the Health page does not accept a module from the query. The Explorer's health summary does not include authored-vs-generated drift, because it sends no authored artifacts.
- **Explorer search:** the search toggle is in the document toolbar, so a document must be open.
- **Not browser-verified:** the Impact scope section and the Delivery scope filter. They are covered by component tests only.
