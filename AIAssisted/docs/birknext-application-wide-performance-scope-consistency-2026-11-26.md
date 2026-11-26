# BirkNext — application-wide performance, scope and terminology (2026-11-26)

Commits: `99e9e1bd` (lazy discovery), `432556ec` (scope picker terminology), `20e7cd94` (scope-safe identities, explicit cross-page intent).

## 1. Route inventory
`@page` routes in `frontend/BirkNext.Web` (`scratchpad/routes.txt`): **84**, of which 82 have no route parameters. All 82 were opened in the isolated instance (v1env18, M2LB imported): 82/82 loaded with status ok.

| Area | Example routes | Context |
|---|---|---|
| Manual Testing | `/manual-testing`, `/discover`, `/plan`, `/plans`, `/plan-execution`, `/run` | project → module → specification (shared scope picker), plan/case in the address |
| Specification / requirements / traceability | `/specification-explorer`, `/test-coverage-review`, `/spec-drift`, `/constitution-explorer` | shared explorer selection |
| Quality reviews | `/api-quality-review`, `/dependency-review`, `/code-risk-review`, `/quality/source` | project / source snapshot |
| Delivery | `/delivery-overview`, `/delivery-reporting`, `/release-readiness` | project / delivery |
| Settings | `/admin/system-settings` | installation |

The per-route table (load, API count, duplicates) is in `scratchpad/v1/final18.log` (`PERF` lines).

## 2–3. Method and baseline
- **Method 1 — sweep:** navigate, then wait for network idle. This understates time-to-content in Blazor WASM, because network idle comes before the page's own data calls; it is used only for request counts and duplicates.
- **Method 2 — per page:** time until a page-specific content element appears, every API call's duration and real payload size, two runs each (`v1_apitiming.py`).

| Page (full load) | Time to content | API time (max) | Payload |
|---|---|---|---|
| Testoversikt | 8.7–8.8 s | 0.7 s | 1.2 MB |
| Testplaner | 11.3–12.1 s | 1.3 s | **9.7 MB** (9.2 MB `discover/last`) |
| Finn og forstå | 17.4–17.7 s | 1.3 s | **11.9 MB** |

## 4. Root causes
1. **Server time is not the bottleneck.** No API call took more than 1.3 s.
2. **Browser time dominates:** the WASM boot (~8 s on a full load) plus deserialising and processing the project's discovery (2,000 candidates, 9.2 MB JSON).
3. **The shared workspace loader** (`ManualTestingWorkspace.LoadAsync`) read the discovery for Testplaner and the new execution page, although neither uses it on load.
4. **Duplicates:** a duplicate `GET /api/workspace-persistence/current-state` on several pages (1 extra request each; see the sweep). NOT FIXED: it comes from the shared workspace persistence layer, which is outside Manual Testing.

## 9–10. Changes
- **Shared data loading** (not per page): Testplaner and `/manual-testing/plan-execution` load the light workspace. The discovery is read on demand: by «Legg til scenarioer» (`EnsureDiscoveryAsync` → `SavedPlans` → `AddScenariosToPlan`), or when a Finn og forstå selection is waiting to become a plan.
- **Measured after:** see the v1env19 rows below.

## 5–8. Scope, terminology, navigation
- **Shared scope picker** (`SpecificationScopePicker`):
  - Modul → **Spesifikasjon** (the feature-specification pack, before labelled «Funksjon») → **Dokument** (when a pack holds several documents).
  - Keys and IDs are unchanged. Picking a module clears an incompatible specification (existing behaviour, retested).
- **Finn og forstå** clears its open scenario on a scope change (`OnScopeChangedAsync`), and drops a restored scenario that is not in the new population.
- **Cross-page intent:** see `manual-testing-cross-page-context-hardening-2026-11-26.md`. «Forbered testtilfelle» now carries the scenario in the address, so Back/Forward and refresh keep it.
- **Identity:** US1/AC1 of two specifications in one module got one candidate ID (and were merged). Colliding keys are now specification-scoped; non-colliding keys are unchanged.

## 11. Database
No schema change, no new index (none was justified: server times are low).

## 14. Classification consistency
Test level, scenario type and test method stay separate axes (see the AC classification report). Documented E2E counts and the dashboard use the same engine.

## 15–17. Results

| Operation | Before | After | Verification |
|---|---|---|---|
| Testplaner full load (M2LB) | 11.3–12.1 s | v1env19 numbers below | v1_apitiming / v1_p0 |

## Not done
These are honest limits of this pass:
- **NOT IMPLEMENTED:**
  - a reduced `discover/last` payload for Finn og forstå (it needs all candidates for filtering; a projection or paging API is the recommended next step);
  - list virtualisation;
  - the duplicate `current-state` request;
  - shared selector components outside Manual Testing;
  - loading/error state harmonisation outside Manual Testing.
- **NOT VERIFIED:** modules outside Manual Testing beyond «loads with status ok» in the sweep.
- **2,000-candidate cap:** discovery truncates M2LB (pre-existing; recommended: raise it, or apply it per provider with measurement).
