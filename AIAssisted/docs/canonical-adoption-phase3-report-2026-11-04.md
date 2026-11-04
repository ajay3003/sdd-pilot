# Canonical engine adoption — Phase 3 report (2026-11-04)

Branch `008-traceability-first`. Builds on Phase 2 and its completion (`correlation/2`). This pass introduces `correlation/3`. Nothing was pushed.

## CANONICAL ENGINE ADOPTION AUDIT MAP

**Adoption levels**

| Level | Meaning |
|---|---|
| 0 | Independent (own parser/matcher) |
| 1 | Partial (canonical data, but key relationships reinterpreted locally) |
| 2 | Canonical consumer |
| 3 | Canonical + shared reviewed decisions |

**Column key**

| Short name | Meaning |
|---|---|
| Ent / Rel | Consumes canonical entities / relations |
| Local story match | Matches stories locally |
| Local alias match | Matches aliases locally |
| Local contract / symbol | Matches contracts or symbols locally |
| Local counts | Computes its own counts |
| Local mappings | Stores manual mappings locally |
| Reviewed | Uses shared review decisions |
| Registry | Uses the Snapshot Registry |
| Vocab | Uses the shared status vocabulary |

**Work items, traceability and identity consumers**

| Consumer | Ent | Rel | Local story match | Local alias match | Local contract / symbol | Local counts | Local mappings | Reviewed | Registry | Vocab | Level | Decision |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Correlation Review | yes | yes | no | no | no | no (summary + queue) | no | **yes** | yes | yes | **3** | keep — the shared review surface |
| Requirements Traceability: document coverage | no | no | the shared Phase 1 engine (`WorkItemInterpreter`) | no | no | own document counts | no | no | no | partly | 1 | keep (document traceability is its own dimension) |
| Requirements Traceability: new canonical trace panel | yes | yes | no | no | no | canonical trace summary | no | via the queue | yes | yes | **2** | new |
| Delivery Reports / Overview | no (reads its snapshots) | no | the shared Phase 1 engine (same rules A–F) | no | no | own report metrics | no | no | yes | yes | 1 | keep; now *feeds* canonical structures (no reinterpretation) |
| Functional Quality Scope | yes | yes (capabilities, VERIFIES) | no | no | no | own scope counts | references only | via the queue | yes | yes | **2** | coverage now uses scoped requirement keys |
| Dashboard | summary tile | no | no | no | no | KPIs from workspace documents | no | counts only | no | yes | 1 (2 for the canonical tile) | the tile shows real decisions |
| Integration Topology | aliases | no | no | `ComponentIdentity` stated-name rule | no | own | no | aliases (confirmed) | no | yes | 2 (identity) | keep the stated-name rule (it parses "Name (ABBR)" text) |
| Integration Quality Review | aliases | no | no | pack aliases merged with canonical | no | own assessment | IQR mapping confirmation | aliases only | no | yes | 2 (identity) / 1 (decisions) | mapping confirmations into the shared queue: **debt** |
| API Quality Review | operation lookup panel | panel | no | no | run keys `Method Path` | own | no | via the panel | no | yes | 1 | **debt**: canonical operation id on findings |
| Project Evidence | package panel | no | no | no | no | own | browser-stored bindings | no | no | yes | 1 | **debt**: bindings → shared reviewed associations |
| Source Analysis | owner of parsed facts | — | — | — | analyzer owns syntax | own | — | — | yes | yes | owner | keep (never overwrites canonical identity) |

**Other consumers (own domains; no cross-domain identity to reuse today)**

| Consumer | Notes | Decision |
|---|---|---|
| Code Risk | Owns code-risk analysis; feeds the symbol table (v3) | keep (owner) |
| Technology Coverage | Own file/technology facts | keep (domain) |
| Documentation Drift / Generated Documentation | Document-version comparison; no cross-domain identity | keep (domain) |
| Critical E2E / Performance | Own scenarios and runs; Test Data uses stable asset ids | keep (domain) |
| Test Data | Own stable `TestDataAssetId` | keep (domain) |
| Endpoint / Browser Discovery | Route templating (`PerformanceScenarioEngine.Template`, shared with the AQR lookup); page identity | keep; canonical `NormalizeRoute` is applied on top of the same template |

## 1. Adoption level per consumer
See the map. Correlation Review is at level 3. The canonical trace panel and Functional Quality Scope are at level 2. Topology and IQR are at level 2 for identity. Delivery, AQR, Project Evidence and the Dashboard KPIs remain at level 1.

## 2. Duplicate parsers found
None for the work-item or contract syntax:
- `spec.md` / `tasks.md` are parsed once (Delivery parser and `SpecStoryHeadings`).
- The canonical engine reads stored rows and parses no Markdown.
- Requirements Traceability parses workspace documents in the browser for *document* coverage. That is a different input (workspace artifacts, not Delivery snapshots) and is kept.

## 3. Duplicate matchers found

| Matcher | Owner | Canonical replacement | Status |
|---|---|---|---|
| `ComponentIdentity` "Name (ABBR)" rule | Topology | canonical stated-name aliases | Equivalent for stated names; kept (it also merges catalogue labels) |
| `IntegrationIdentityResolver` | Integrations setup | canonical aliases | Different job (setup identity); kept |
| IQR pack aliases | IQR | canonical aliases (already merged) | Already canonical-fed |
| AQR `Method Path` keys | AQR | canonical `ApiOperation` id | Debt (behaviour not yet proven equivalent) |
| FQS shared-id guard | FQS | scoped requirement keys | Now prefers the scoped key; the guard remains for unscoped (old) interpretations |
| Test Data aliases | Test Data | n/a (different domain) | Kept |

## 4. Legacy interpretation retired
**None removed in this pass.** No matcher was proven equivalent, with tests, in a way that makes deletion safe. Retirement is documented as debt in §44.

## 5. Legacy logic intentionally retained
- Topology's stated-name rule (syntax of the label itself).
- `IntegrationIdentityResolver` (setup).
- Delivery's own report metrics: Delivery is the Phase 1 engine's primary consumer, and its classification is exactly what the canonical structures persist.

## 6. Delivery adoption
Delivery and the canonical engine share one work-item engine:
- The canonical reader (`DeliveryCanonicalWorkItemReader.StructuresAsync`) classifies stored task rows with `WorkItemInterpreter.Classify`.
- It persists the result: tasks, groups, stories, scenarios and requirements.
- Nothing is re-derived.
- Delivery reports do not yet record an interpretation id (debt).

## 7. Traceability adoption
- New: a canonical end-to-end trace panel on Requirements Traceability (§26).
- Document coverage keeps its own dimension and is never merged into the canonical counts.

## 8. Project Evidence adoption
Level 1. The package panel shows canonical identities and capabilities; bindings are still browser-stored (debt).

## 9. Source Analysis role
Owner of parsed facts, including the code-risk v3 type table. It never writes canonical identity.

## 10. Integration Topology adoption
Applies canonical explicit and confirmed aliases. MU resolves to one component (`ComponentIdentityTests`; live Topology shows one MU). The conflicting `M2LB.SkoleAdapter.Worker` alias is **not** applied until reviewed.

## 11. AQR adoption
Level 1: the canonical operation panel. Run keys are not migrated (debt).

## 12. IQR adoption
Aliases come from the shared registry. IQR's own mapping confirmation is not yet written as a shared decision (debt).

## 13. Dashboard adoption
The tile reads the canonical summary: real **decisions** (13 on real data) and **suggestions** (1,207), shown apart.

## 14. Code Risk and Technology Coverage
Code Risk owns the symbol table the engine consumes. Technology Coverage is not applicable.

## 15–17. Count consistency
- The summary, the queue, the Dashboard and Correlation Review use one classifier (`CanonicalReviewQueue.Classify` + `Count`). On real data the queue, summary and Dashboard all said **13 decisions**.
- Contract counts are labelled: logical, versions, distinct contents, occurrences.
- Work-item counts are separate: user stories, scenarios, functional groups, scenario groups, tasks, shared/foundational tasks, story references.
- The trace summary and the feature list use the same "feature pack with work items" rule. They disagreed (33 vs 32) during acceptance; this was fixed.

## 18–20. Shared review item model, queue and item types
**`ReviewItemView`** carries: id, kind (Decision/Suggestion), type, priority, reason, target, the relation/alias/subject, **candidates**, affected consumers, rule, rule set, state, interpretation id, last evaluated, and resolution guidance.

**Types:**
- Identity conflict
- Decision to recheck (stale)
- Alias
- Shared capability
- Code reference
- Test link
- Contract version
- Story reference

**Endpoint:** `GET api/canonical/{scope}/review?kind=&type=`. Correlation Review hosts it, with Decisions and Suggestions tabs and a type filter. The Dashboard and the trace panel link to it.

## 21. Decision propagation
Decisions go through the one registry (`DecideAsync`). The queue, summary and every consumer read the same relation state.

Tested: confirming one alternative closes the question; reinterpreting unchanged evidence keeps the decision; the summary reflects it after the next interpretation.

## 22. Rejected candidates
Rejected candidates are never re-asked under unchanged evidence (classification returns none; precedence keeps Rejected).

## 23. Stale and conflicting decisions
- Revalidation → **Decision to recheck**.
- A decision contradicting explicit evidence → **Identity conflict**, priority 0.

## 24. Affected consumers
Derived from the entity types on each side, e.g. "Affects: Integration Quality Review · Integration Topology · Project Evidence".

## 25. Human review count semantics
**One question per ambiguity:**
- Alternatives of the same test, task or declaration are grouped into one item with candidates.
- Naming-only proposals are suggestions, not decisions.

**Real data before → after:**

| Interpretation | "Needs review" before | Decisions after |
|---|---|---|
| Parser-v4 Delivery snapshot | 1,237 | **13** (1 identity conflict, 1 test-link question with 29 alternatives, 11 story references) |
| Older Delivery snapshot | 1,237 | 67, then grouped to 13 |

Suggestions are 1,207 in both cases.

## 26. End-to-end traceability model
**Endpoints:**
- `trace/summary`
- `trace/features?module=`
- `trace/features/{id}`: one feature's chain, loaded on request.

**Chain per feature:**
- Requirements → tests (explicit, suggested or ambiguous).
- Work items (user story, scenario, functional group, scenario group, shared & foundational, removed story, unresolved) → tasks → implementation (source types the task names) → API operations (implemented by the type's members) → contracts the type references → tests of those operations.

Each link carries its relation (state, rule, "why linked"). Missing links are listed as gaps: "Implementation evidence not linked", "No linked test". Nothing is inferred.

## 27. Feature-pack requirement identity
**`Requirement|module/feature/FR-001`**. FR-001 in two packs is two requirements.

Tests are resolved to their pack:
- the only pack defining the id → Explicit;
- otherwise the only pack in the module the test's project/path names → Explicit;
- otherwise one **ambiguity** with alternatives (CR-M-SCOPE).

Naming-only candidates are proposed only when the module narrows them (160 were not proposed on real data, and this is reported). Without Delivery structures, the unscoped Phase 2 behaviour is kept and tested.

## 28. Story and group handling
Entity types:
- UserStory (removed ones flagged)
- Scenario
- FunctionalGroup
- **ScenarioGroup** (new)
- **SharedFoundationGroup** (new)

"Technical work" is not a separate type: the Phase 1 engine classifies migration work as shared/foundational (case F), and that classification is kept.

## 29. Task handling
Each task records: id, text, checked state ("checked in the tasks file", never "done"), phase, relationship, work-item rule and story reference.

A removed-story reference is an Unresolved relation shown under the removed story. A `[US6]`-style unresolved reference is a review item whose resolution is a source fix.

## 30–33. Source, API, contract and test links
- Source: from CR-L-TASK.
- API: via CR-L-MEMBER and the route attribute.
- Contracts: the type's own contract references.
- Tests: VERIFIES to scoped requirements and operations.
- GraphQL operations are included (root + field).

## 34–35. Explicit vs suggested, and explainability
Every link shows the shared status badge (Explicit/Confirmed vs Candidate/Ambiguous) and a "Why linked" disclosure with explanation and rule. Counts never include suggestions.

## 36. Capability-centric traceability
Not built as a separate view. Capabilities stay visible in Correlation Review and Functional Quality Scope (debt: a capability-rooted trace).

## 37. System-flow context
Not added to the trace. Flows stay in Topology and FQS, and no flow is hard-coded.

## 38–39. Snapshot context and historical safety
- Trace and queue report the interpretation id they read.
- Historical interpretation snapshots are frozen membership. `correlation/1` and `correlation/2` snapshots remain unchanged on the throwaway database.
- The trace reads current active relations; a historical trace view is not built (debt).

## 40. Direct navigation
Correlation Review, Requirements Traceability (panel) and the Dashboard load canonical data directly via the summary, trace and review endpoints. Verified in the browser without opening Source Analysis first.

## 41. Real-package regression results
Throwaway copy, 7 real source snapshots + the real parser-v4 Delivery snapshot. Interpretation took 5.1 s and produced 5,227 entities and 7,903 relations.

| Case | Expected | Result |
|---|---|---|
| A | Functional groups (Autorisasjon) | 9 |
| B | Scenario groups (Hendelse) | 5, with 6 scenarios |
| C | Removed-story references (TjenesteService) | 5 |
| D | Frontend `[US6]` unresolved | 6 |
| E | Shared/foundational, never "missing story" | 762 |

Other results:
- 577 scoped requirements across 33 packs.
- The MU alias is not duplicated.
- Contracts: 70 logical / 84 versions / 88 occurrences.

**Defects found only on Postgres, and fixed:**
1. `jsonb LIKE` made the review endpoint fail with 500.
2. jsonb text normalisation made shared/unresolved/removed counts 0.

Attributes are now parsed, never string-matched. A regression test reproduces the normalised jsonb text.

**Not demonstrated live:** implementation links. The stored snapshots predate code-risk v3, which the trace reports as a limitation; re-analysis is needed. Unit tests cover the chain.

## 42. Browser acceptance
- Isolated stack (`birknext_ci2`), Edge, 1440/390 × light/dark.
- Checked: Correlation Review (13 decisions; the 29-alternative question is one item with "Choose this"; story references show a source fix), Requirements Traceability canonical panel (summary → feature list → chain with gaps), Dashboard tile (13 decisions, 1,207 suggestions apart).
- No overflow, no console errors, no external hosts.

## 43. Payload and performance

| Endpoint | Payload | Latency |
|---|---|---|
| review (decisions, 60 items) | 118 KB | 0.16 s |
| review (suggestions, 20 items) | 63 KB | 0.07 s |
| trace summary | 0.6 KB | 0.17–0.20 s |
| trace features | 22 KB | 0.11 s |
| summary | 4.2 KB | 0.09–0.15 s |

- Interpretation with Delivery took 4.7–5.1 s.
- No consumer downloads the graph.

## 43a. Automated regression totals (exact)
- **New tests:**
  - Backend `CanonicalAdoptionTests` (11, including a 4-case theory): A–F persisted with distinct types, feature-pack requirement scoping, unscoped fallback, shared queue (one question per ambiguity, decision propagation, reinterpretation stability), end-to-end trace with gaps, reader using the shared engine, jsonb text normalisation, requirement display text, the shared classification rule.
  - Backend FQS: scoped coverage.
  - Frontend: `CanonicalTracePanelTests` (2) and a review-page test for choosing one alternative.
  - Existing page and summary tests updated for the shared queue API.
- **Pinned updates (deliberate):** the determinism test now reads `CorrelationRules.RuleSetVersion` (now `correlation/3`).
- **Full suites** (scratch worktree, run sequentially with the live stack stopped):

| Suite | Passed | Skipped | Failed |
|---|---|---|---|
| Frontend | **7980** | 0 | **0** |
| Backend | **5791** | 13 | **0** |

- The two loopback timing tests that timed out in earlier parallel, memory-pressured runs passed this time.

## 44. Remaining technical debt (REMAINING LEGACY INTERPRETATION DEBT)

| Item | Owner | Risk | Recommendation |
|---|---|---|---|
| AQR run keys `Method Path` | AQR | Findings not tied to canonical operations | Store the canonical `ApiOperation` id on findings |
| IQR mapping confirmations stay local | IQR | A second answer path for cross-feature facts | Write shared decisions for cross-feature mappings |
| Project Evidence bindings in browser storage | Project Evidence | Not shared or reviewable | Migrate to shared reviewed associations |
| Delivery reports lack an interpretation id | Delivery | History cannot name the canonical state | Record it on generate |
| No capability-rooted or historical trace view | Traceability | — | Add on demand |
| Implementation links on real data need code-risk v3 re-analysis | Source Analysis | Trace shows 0 implemented until re-import | Re-analyze |
| Live counts of test-host DB isolation | Tests | Other contexts may still reach the real DB | Assembly-wide guard (#27) |

## 45. Commits
See the git log after this report.

## Confirmations
**Interpretation source and identity**
- The canonical engine is the interpretation source for the migrated surfaces: Correlation Review, the canonical trace, FQS coverage, the Dashboard tile, Topology/IQR identity.
- AQR, Project Evidence and IQR decisions are partly migrated (documented debt).
- Migrated consumers do not reinterpret work items, systems, contracts or symbols. Delivery and the canonical engine share the one Phase 1 engine.
- Delivery and Traceability share work-item identities through the same interpreter; the canonical trace uses canonical ids.
- Project Evidence shows canonical package identities. Its bindings are not migrated (debt). Topology uses canonical aliases.
- AQR uses the canonical operation lookup only (debt). IQR uses canonical system aliases.
- Dashboard canonical counts come from the canonical summary.
- The same canonical entity has the same identity across surfaces: one id per (scope, type, key).
- Filtered counts state their scope (per feature, decision vs suggestion, labelled contract counts).

**Review**
- Human-review counts are real decisions, not analyzer limitations or naming suggestions.
- One ambiguity is one shared question with alternatives. Decisions propagate through one registry. Rejected candidates are remembered.
- Conflicting evidence surfaces as Conflict.

**Traceability**
- Requirement ids are scoped by module and feature pack; FR-001 is never merged across packs by text.
- The trace supports Requirement → work item → task → implementation → API/contract → test where evidence exists. Incomplete chains stay incomplete and are reported as gaps.
- Explicit links stay distinct from suggestions.
- Capability relations never merge stories.
- Historical interpretations are frozen. The trace view reads current relations, and a historical trace view is debt.
- No migrated consumer silently falls back: the trace panel and queue show "Shared interpretation unavailable" or "Shared review queue unavailable".
- Remaining legacy interpretation code is documented (§3, §44).

**Evidence and safety**
- Real M2LB patterns A–E were used as regression evidence on a throwaway copy.
- No external graph database or new microservice was introduced.
- No real external traffic was generated.
- `appsettings.Local.json` was not intentionally modified.
- User testdata was preserved.
- Nothing was pushed.
