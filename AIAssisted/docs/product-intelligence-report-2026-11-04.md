# Product intelligence — report (2026-11-04)

Branch `008-traceability-first`. Nothing was pushed. Built on the canonical engine (Phases 1–3) and on Functional Quality Scope (#20); nothing was rebuilt.

## PRODUCT INTELLIGENCE AUDIT MAP

| Area | Before this task | Decision |
|---|---|---|
| Impact Analysis (`UnifiedImpactAnalysisPanel`, `SourceChangeImpactPanel`) | Document/spec impact and source-change impact from their own inputs, not from canonical relations | Kept. The new canonical graph impact is offered where canonical entities are shown; merging the panels is listed under Not done. |
| Change Traceability | ADO work item → PR → commit → file (optional) | Unchanged: an external chain, not canonical relations |
| Delivery / Traceability filters | `CanonicalScopeFilter`, `DeliveryScopeService`, FQS scopes | Reused, not duplicated |
| Functional Quality Scope (#20) | Scope resolution, coverage per item, Test Data state, why-included | **Extended** with scenario candidates (same `ScopeResolution`, no second model) |
| Canonical trace / review queue (Phase 3) | Trace counts, review queue, decisions registry | Reused by impact (relation states) and by governance (#24) |
| Test Coverage / Critical E2E / AQR / IQR / FQR / Performance | Own readiness and runs | Not changed here; impact names affected consumers |
| Test Data | Stable `TestDataAssetId`, readiness | Not read by the scenario drafts yet (see Not done) |
| Dashboard / selectors / search | Canonical summary tile | No change |

## What was built

### 1. Graph impact analysis (`impact/1`)

**Model:** `shared/CanonicalImpact.cs` · **Service:** `CanonicalInterpretationService.Impact.cs` · **Endpoint:** `GET api/canonical/{scope}/impact?entity=Type|identity`

**The walk**
- Server-side, breadth-first and cycle-safe. Bounded to **3 hops** and **500 entities**; when truncated, the result says so.
- It follows active, non-rejected relations in their impact direction:
  - **Reverse** (the dependent's change is impacted by its target): Implements, References, DependsOn, Consumes, Uses, Verifies, DerivedFrom, OccurrenceOf, BelongsTo.
  - **Forward** (parts): Contains, Exposes, Produces.

**Levels**
- **Direct:** one established relation away.
- **Downstream:** established all the way.
- **Potential:** a candidate relation lies on the path.
- **Unknown:** unresolved relations are *counted*, never guessed.

**Containment rule** (found with real data)
- A part-of relation in either direction (Contains / Exposes / Produces / BelongsTo) carries definite impact only *from the changed entity itself*.
- Below an impacted container, impact is **Potential**.
- On M2LB, `BarnKommuneEndretEvent` previously made all 17 API operations and 17 classes of a consuming service "Downstream". Those are now Potential.

**Never impact:** alias, same-as, candidate-same-as and supersedes relations (terminology or identity).

**Tests**
- Impacted only through **VERIFIES**.
- An established link means **Must retest**; a candidate link means **Should retest**.
- A test that only *uses* the changed thing is not impacted.

**Test gaps:** impacted (or root) requirements, stories and scenarios that nothing verifies.

**Every impacted entity carries its relation path** (relation, direction, resolution).

**UI:** an on-demand "If this changes…" action in the shared `CanonicalCorrelationPanel`, so every consumer has it: Project Evidence, Topology, AQR, IQR, Correlation Review → Explore.
- Order: regression scope (must/should retest, with why), test gaps, then impacted items and why.
- The result is never computed because a page opened.

### 2. Test scenario intelligence (`scenarios/1`)

**Generator:** `shared/TestScenarioIntelligence.cs`, pure and shared.
- One **draft** per acceptance criterion in the scope that no automated test covers.
- Existing coverage is checked first. Candidate-only coverage says "Candidate coverage only — confirm or replace".
- **Given / When / Then** come only from what the criterion states (Markdown emphasis removed). Otherwise the text is the expected outcome; nothing is invented.

**Explainable priority** (from evidence on the item):

| Priority | When |
|---|---|
| High | Security or Privacy without coverage; or a P1 story without coverage |
| Medium | P1 with a candidate test; P2; Reliability, Data quality or Compliance |
| Low | everything else |

Length or complexity never raises the priority.

**Duplicates:** same feature + same normalized expected outcome are folded into the first draft.

**Provenance:** "Derived from module / feature / story / AC" and **"Draft · not run · not coverage"**.

**UI:** a "Scenario candidates" section in Functional Quality Scope (Delivery → Scope).
- The summary shows draft count and high-priority count.
- The list is ordered by priority and capped at 100.
- Drafts are never persisted, run or published, and never counted as coverage.

### 3. Smart functional views

Functional Quality Scope (#20) already provides module / profile / custom scopes, related-module expansion with why-included, and content-first ordering. #23 adds the scenario layer on top of the same resolution. Canonical *capability* scopes in FQS are listed under Not done.

## Real data (throwaway `birknext_ci2`, scope `ci-live`; localhost only)

**Impact** (each call 0.2–0.35 s, 5–32 KB)

| Root | Result |
|---|---|
| `BarnKommuneEndretEvent` | 39 items: version and occurrence; the consuming service Downstream; its operations and classes Potential |
| `M2LB Proxy` | 7 items |
| `FR-025` (PersonService) | Nothing depends on it; it is its own test gap |

**Scenario candidates** (SkoleService)
- 75 drafts, 13 high priority.
- First draft: "Accept a valid report without keeping the child's national ID — AC-10", P1 story with no known coverage, with Given/When/Then as written.

**Browser** (1440/390 × light/dark): impact view, scenario candidates and governance panel. No horizontal overflow, no console errors, **0 external requests**.

## Tests

- **Backend** `CanonicalImpactTests` (4):
  - Direct / Downstream / Potential with paths;
  - Must vs Should retest; USES does not make a test impacted;
  - test gap; rejected and alias relations ignored;
  - cycle back to the root; depth bound; unknown entity or scope gives 404;
  - containment from the root is Direct, below a container Potential.
- **Frontend:**
  - impact view loads only on request, shows retest / gap / path, and closes;
  - scenario generator: coverage filter, G/W/T parsing, priority reasons, duplicates, length never raises priority;
  - panel section.
- **Full suites at #23:**
  - frontend **8015 / 8015**;
  - backend **5815 passed / 13 skipped** plus 2 known load flakes (`LoopbackHttpTestServer`, `DatabaseMigrationLock`) that pass alone.

## Commits

| Commit | What |
|---|---|
| b24569d1 | Graph impact + scenario generator model |
| 7331edb5 | FQS scenario candidates |
| 340c7687 | Containment below the root is Potential |
| e22a6f3b | BelongsTo is containment |
| 8db7ca52 | Draft badge wording |

## Not done (follow-ups)

- **Baseline vs current impact:** semantic change types between two interpretation snapshots ("what changed → what is impacted"). Today impact starts from one chosen entity.
- **Probably unaffected:** needs evidence of what a test does *not* touch; not claimed.
- **Reviewed impact decisions**, and persisting impact / scenario sets in the Snapshot Registry.
- **Scenario workflow:** accept / edit / reject / link existing / mark duplicate; persisted rejections with evidence fingerprint and rule version.
- **Scenarios from other sources:** contracts (XSD/OpenAPI boundaries), integrations, security and performance requirements; Test Data by `TestDataAssetId`.
- **Capability scopes in FQS:** canonical Capability / System / flow and reviewed aliases with preview counts.
- **Entry points:** Dashboard / Traceability (test-lead summary), and context in AQR, IQR, FQR, Performance, E2E, Code Risk and Doc Drift.
- **Merge with existing panels:** fold `UnifiedImpactAnalysisPanel` and `SourceChangeImpactPanel` onto the canonical walk where their inputs are canonical.

## Confirmations

- The canonical engine is reused (entities, relations, review states, decision registry); no parallel graph or graph database.
- Impact is server-side, bounded and cycle-safe, with a path for every impacted entity; unresolved impact is Unknown, not guessed.
- Candidate links never produce Must retest or Downstream; aliases never carry impact.
- Scenario drafts are drafts: not run, not published, not coverage; priority is explainable, and complexity alone never makes High.
- No real external traffic; `appsettings.Local.json` not intentionally modified; user Test Data preserved; nothing pushed.
