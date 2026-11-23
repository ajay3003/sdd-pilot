# BirkNext unified scope architecture proposal

**Evidence baseline:** branch `008-traceability-first`; first observed HEAD `92aca19df8dd736f081a846c4d4ca02a90969023`, later observed `026ade937d7938faab3c0b0e6e112c87d27c10b5`.

**Recommendation:** Alternative B in a hybrid form: a small shared, project-local Scope Registry and System Identity/Relationship layer that reuses Canonical Interpretation and Snapshot Registry. Functional Quality Scope remains a Delivery-owned projection; Test Scope is a shared logical boundary consumed by modules. Environment URLs/bindings, contracts, schemas, assets and evidence remain in existing stores.

## Alternatives

| Criterion | A — extend Functional Quality Scope | B — shared canonical Scope Registry | C — system-centric scope |
|---|---|---|---|
| Correct ownership | Weak as sole owner: its model/table/API are Delivery-owned and feature/spec shaped. | Strong: shared project identity and scope membership; specialized views remain owners of their data. | Strong for integration paths, weak for functional work with no multi-system edge. |
| Existing reuse | Reuses scope UI, CRUD, refs, snapshots and test-design assessments. | Reuses those patterns plus Canonical Interpretation entities/relations, Snapshot Registry, Test Data IDs and readiness engines. | Reuses topology/provider evidence but risks turning expectations into membership. |
| Migration/backward compatibility | Low initial, high semantic coupling and later extraction cost. | Moderate additive schema/API; compatibility adapters can preserve Delivery APIs and IDs. | Moderate; would require translating feature/scenario membership into system paths. |
| Multi-project/snapshot | Current Delivery project boundary only. | Explicit project + snapshot/revision identity; aligns with source registry. | Depends on system identity correlation across source projects. |
| Test Data / Delivery | Easy for Delivery requirements, weak for cross-journey assets and test data. | Scope references shared assets and Delivery projections without copying. | Asset association remains secondary and may overrequire system artifacts. |
| Safety/testability | Good isolated CRUD tests; cross-module integration requires coupling. | Additive, explicit relations and deterministic adapters; versioned session allows tests. | Risks inferred edges and tests that assume all topological hops. |

## Proposed logical model

| Entity | Ownership and identity | Purpose |
|---|---|---|
| Project | Existing stable project/import identity; establish one adapter mapping where modules currently use distinct IDs. | Isolation boundary for logical systems, scopes and user decisions. |
| Source Snapshot | Existing immutable source snapshot ID/hash/project association. | Provenance and re-analysis comparison. |
| System Identity | Project-local stable ID; kind/name/aliases, lifecycle and provenance references. | Logical service/app/resource identity independent of environment. |
| System Relationship | Existing canonical relation ID where compatible; directional from/to system IDs, relation kind, provenance, confidence, review state. | Candidate/confirmed configured/dependency/message-flow facts. Never implies runtime verification. |
| Environment Binding | Reference to system ID + target environment ID + safe endpoint/config references + auth requirement reference. | Deploy-specific binding; secrets remain in secure existing configuration. |
| Scope | Project ID, stable ID, name, type, lifecycle, current revision. | Reusable logical boundary; saved group can reference multiple scopes without merging identities. |
| Scope Revision / Member | Immutable revision; member refs typed to system, relation, feature/story/requirement/scenario, contract/schema, asset and environment. Include inclusion status (confirmed/candidate/excluded) and provenance. | Stable, explicit membership with no display-name join. |
| Test Journey | Existing journey/scenario pack identity and scenario definition/revision. | Concrete path with step-specific expected outcome, prerequisites and executor. It may be linked to multiple scopes. |
| Test Data Asset | Existing TestDataAssetId and version/revision policy. | Reusable approved asset; scope associations reference, never copy payload. |
| Test Session | ID, project, scope revision IDs, selected environment IDs, allowed operations/authorization, resolved asset versions, started-by/time. | Context for one explicit test activity; does not mutate environment configuration. |
| Readiness Vector | Per scenario × environment × session: dimensions and evidence references. | Honest, composable readiness; Not assessed remains distinct from blocked/failed. |

Avoid a second entity/relation engine. Where Canonical Interpretation entity types and project/source scope can represent systems and confirmed relationships without breaking existing semantics, adapt it. Introduce only a thin scope registry and typed member references. First phase must verify project key semantics and DB isolation.

## Lifecycle and membership rules

Scope lifecycle: Draft → Reviewed → Approved → Archived. Approval pins a scope revision, not successful test execution. Revision membership is immutable after approval; edits create a new draft revision. Source reanalysis creates candidate adds/removals/changes with provenance and a diff. User confirmation is a separate decision record; reanalysis never overwrites it. Ambiguous name or artifact matches remain candidates.

Membership has two states: **confirmed member** and **candidate** (plus explicit excluded/rejected decision). Deterministic inclusion is allowed only through reviewed rules/explicit IDs. A dependency edge, expected topology profile, matching display name or guide prose alone is not confirmed membership.

A saved group selects several scope IDs but preserves each scope's identity and denominators. Shared scenarios/assets are deduplicated by stable ID for storage, while coverage counts distinguish scope attribution from unique test/scenario count. One asset may be referenced by many scope revisions; the asset value and approval remain in Test Data.

## Persistence and API seams

Add only the minimum project-scoped scope tables/API after mapping project ID identities:

- `scope`, `scope_revision`, `scope_member`, optional `scope_group`/membership if needed, and `scope_session` or session envelope.
- Member references store typed stable IDs and provenance JSON/foreign references where possible; avoid storing contract/test data/evidence payload copies.
- System identity and relations should reuse Canonical Interpretation tables where semantics support it. If not, introduce an adapter first, not a duplicate generic topology database.
- Environment binding remains in Target Environment/provider stores; add logical-system reference columns/IDs with nullable compatibility migration.
- Test Data association references `TestDataAssetId` and version-selection policy; retain existing environment integration bindings during transition.
- Evidence remains in existing immutable run stores; session links evidence IDs and pins the scope revision/environment/assets.

Suggested shared API surface (exact route is implementation decision): project-scoped scope list/read/create draft, revision/member preview/diff, review/approve/archive, group selection, readiness resolution, and session creation. Existing Delivery scope and journey APIs remain valid and gain optional shared scope/revision references. Never execute as a side effect of preview or readiness.

## Readiness

Compute dimensions separately for each included scenario and selected environment:

1. Scope defined/revision approved or draft status.
2. System IDs and required directional relationships resolved (candidate/unknown shown as review required).
3. Environment selected, trusted, and operation explicitly authorized.
4. Scenario-specific data asset reference/version approved and compatible.
5. Required contract/schema present and validated for that scenario.
6. Required dependency/observer/executor testable.
7. Authentication mechanism configured; secret availability only, never secret content.
8. Execution evidence state and result, only after explicit run.

Unknown applicability = Not assessed; required missing prerequisite = Blocked only if journey metadata says required; optional = Not applicable/optional-missing; execution absent = Not run/Not verified, never failed. Aggregate as counts/vector and retain each reason.

## UI information architecture

- **Project context → Systems**: compact registry list with discovered/configured/confirmed states, provenance drawer, add/merge/review actions. Connected Systems stays in Target Environments as deployment bindings, linked back to system IDs.
- **Testing → Scopes**: scope library, create from discovered systems/scenarios, select one or multiple scope revisions, save group, preview membership and readiness. Avoid adding a full settings tab duplicate.
- **Scope builder**: progressive steps: boundary (systems/scenarios), evidence/contract links, environments/data, review. Show uncertain associations as candidates with source location and confirm/reject. Empty state offers “start from source snapshot” and “start empty”.
- **Module pages**: one shared context chip/picker when relevant; local filters remain local. Navigation preserves stable IDs/revision, not labels.
- **Test Data**: show applicable assets by scenario requirements and selected environment; explain why an asset applies. Scope membership is a reference, never duplicate asset values. Only required scenario prerequisites affect readiness.
- **Change preview**: before approval, show additions, removals, changed source hash, stale refs and unresolved correlations; allow retaining prior approved revision and decisions.

## Security / operations

System identity cannot contain secrets. Environment and credentials references remain in existing protected stores. Session creation requires explicit environment and allowed operation profile; default generated suite is draft-only. Test execution requires explicit authorization and preserves existing per-provider guards. No “configured” or “source-discovered” relation is presented as runtime-verified.
