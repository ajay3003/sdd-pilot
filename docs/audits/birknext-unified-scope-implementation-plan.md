# BirkNext unified scope implementation plan

**Evidence baseline:** branch `008-traceability-first`; first observed HEAD `92aca19df8dd736f081a846c4d4ca02a90969023`, later observed `026ade937d7938faab3c0b0e6e112c87d27c10b5`.

This plan follows the static audit in `birknext-unified-system-and-test-scope-audit.md`. It is a proposal only; no implementation was performed.

## Gap register (substantiated)

| ID | Class | Root cause and source evidence | Reuse | Proposed change | Complexity / priority / risk | Acceptance criteria |
|---|---|---|---|---|---|---|
| US-01 | PERSISTENCE_GAP / MODULE_INTEGRATION_GAP | ExternalIntegration and IntegrationDefinition are environment-keyed (`Models/IntegrationCatalogRecords.cs`, AppDbContext); topology merges facts but lacks project-owned durable logical system CRUD (`Services/IntegrationTopology/TopologyProviders.cs`). | Snapshot Registry, Canonical Interpretation, topology builder, project import IDs. | Add project-local logical system identity adapter/registry and references; keep per-environment configurations. | M / P1 / medium: wrong ID mapping can cross projects. | Same system stable across environments; same display name in two projects is isolated; no endpoint/secret is stored as system identity. |
| US-02 | MAPPING_GAP | Canonical interpretation relations use source-scoped entity identities and human review, while topology, system guide, IQR integrations and journey pack references are separate (`CanonicalInterpretationService.Scopes.cs`, `SkoleNaervaerJourneyPack.cs`). | Canonical entity/relation store, source provenance, review state. | Map discovery evidence to candidate relation IDs with explicit reviewed correlation; no name-only auto-confirm. | M / P1 / medium. | Candidate links remain candidates; confirmed/rejected decisions survive reanalysis; unrelated project cannot correlate. |
| US-03 | MODULE_INTEGRATION_GAP | Delivery scope API/tables are project-specific; canonical functional filters and Integration Journeys have separate identity/membership (`FunctionalQualityScopeController.cs`, Delivery entities, journey controllers). | Delivery scope refs/snapshots; canonical filters; journey IDs. | Add shared scope revision and adapters; preserve Delivery APIs and project legacy behavior. | L / P1 / medium-high migration risk. | Delivery current snapshots/API results unchanged without shared scope selection; shared selection resolves exact same artifacts across two modules. |
| US-04 | TEST_DATA_GAP | Stable TestDataAssetId/revisions and integration-level bindings exist, but no shared scope membership relation (`TestDataAsset.cs`, ExternalIntegration contracts/service). | TestDataCatalog, identities, approval, validation, bindings. | Link assets by ID/revision policy to scenario/scope; compute requirements at scenario×environment. | M / P1 / high if versions copied or values leaked. | One approved asset reused in multiple scopes with no copies; each scope/environment gets independent readiness; payload never appears in scope API. |
| US-05 | TEST_GENERATION_GAP | Journey packs authored and executable; no general generator from source snapshot/topology/contracts to reviewable executable scenario definitions. | Journey definitions, Test Plan Suggestions, contract/test suggestion engines, TestData generation/schema validation. | Generate draft definitions with source/provenance refs and expected assertions only where explicit evidence exists. | L / P2 / high false-confidence risk. | Generated definitions are draft; uncertainty flags; no execution until explicit authorization; tests prove supported generators and no fabricated assertions. |
| US-06 | EXECUTION_GAP / EVIDENCE_GAP | Journey run history is pack/executor-specific; no common Test Session pins scope revisions, environment, authorization and asset versions. | Existing journey run/evidence and Delivery frozen scope snapshots. | Add session envelope linking immutable run records; do not merge evidence stores. | M / P2 / medium. | Historical result resolves exact scope revision, environment and asset revisions after later edits/reanalysis. |
| US-07 | ENVIRONMENT_GAP / AUTHENTICATION_GAP | Environment bindings/provider-specific auth are separate from system identity and source facts; no source-derived settings may authorize runtime. | Target Environment trust, provider setup and runtime auth policies. | Prefill draft safe config refs; require explicit trusted environment/auth review and operation authorization. | M / P1 / high operational risk. | Draft never sends traffic; execution blocked without explicit permitted environment and scenario operations; no secrets in registry/report. |
| US-08 | UX_GAP | Scopes/system identity are configured in separate module-specific surfaces; cross-navigation does not share a stable scope revision. | Delivery scope panel and existing Target Environments/Connected Systems UI. | Add project Systems context and Testing Scope library with shared picker/context link. | M / P2 / low-medium. | Selected IDs survive browser navigation/reload; stale membership preview precedes approval; no duplicate selection panel on each page. |

No infrastructure gap is established. The first phases fit existing DBs, services, stores and APIs; schema additions are ordinary additive persistence, not new runtime infrastructure.

## Phase A — Connect existing modules (can begin without new infrastructure)

1. Document and normalize identity adapters for Project Import, Delivery project, source snapshot and Target Environment IDs.
2. Add optional shared scope/revision references to existing Delivery and journey read models without changing default behavior.
3. Use existing Topology and Canonical Interpretation read paths as scope candidate sources; preserve candidate/confirmed distinction.
4. Feed Test Data applicability from scenario requirements and environment compatibility using existing TestDataCatalog; add no scope asset copies.

**Tests:** API compatibility/default response; identity mapping/isolation; candidate relations excluded from confirmed membership; existing Delivery scope tests; journey readiness tests; Test Data stable-ID/version/approval tests.

**Acceptance:** Existing module outputs unchanged without scope context; scope context narrows them deterministically; no duplicated facts or new service infrastructure.

## Phase B — Automate configuration discovery and prefilling

1. Add project Systems view over Source Analysis topology candidates and canonical entities.
2. Review/confirm identity aliases and directional relationships with provenance; candidate updates are snapshot-aware.
3. Prefill draft Environment Binding and Integration Catalog links from known safe configuration/source values, excluding credentials and untrusted endpoints.
4. Maintain per-environment bindings and provider handlers; discovery alone never marks a system connected or runtime verified.

**Tests:** two same-name systems in separate projects; cross-environment stable logical ID; ambiguous correlation; source-only candidate; configured-only edge; reanalysis and retained review decisions; secrets excluded.

## Phase C — Generate test definitions and datasets

1. Generate draft contract-driven test definitions using contract-test/API/GraphQL/XSD engines where they already support it.
2. Reuse `TestDataCatalog`, schema validation and existing generators to suggest synthetic values/fixtures; save as new asset revisions through existing lifecycle and require approval.
3. Associate definitions with stable scenario/contract/scope member IDs and expected assertions only when explicit schema/rule evidence supplies them.

**Tests:** positive/negative generation for each supported engine; malformed/missing data; XSD revision pinning; no generated real identifiers; asset reuse across scopes; draft approval and immutable revision behavior.

## Phase D — Generate integration-level E2E workflows

1. Generate draft journey skeletons from selected directional relationships and explicitly selected scenarios.
2. Insert existing executors and observers only where registered; unresolved hop/observer remains visible prerequisite.
3. Add Test Session with scope revision, environment, authorization, operation set and asset versions.
4. Readiness remains vector per scenario/environment/session.

**Tests:** one-system and multi-system scope; separate Skolenærvær report-intake and period-close paths; no assumed Altinn→MU→MM; no Altinn requirements on period-close; cross-project isolation; exact session/evidence references.

## Phase E — Extend messaging and downstream verification

1. Reuse Messaging Topology evidence to map producer→queue/topic→consumer only where channel names resolve; preserve unresolved endpoints and provenance.
2. Add or adapt message observer verification and downstream artifact verification (e.g. Blob/Parquet) only after verifying existing Messaging Topology, Parquet Viewer and Blob Storage Explorer APIs and their safe read boundaries.
3. Keep infrastructure deployment/creation outside this scope; require separate operational authorization for any future active executor.

**Dependencies:** Messaging Topology is a useful producer of named directional resource edges. Parquet Viewer and Blob Storage Explorer may provide read-only artifact inspection, but no shared observer interface was verified in this audit. Their implementation is a dependency to inspect before adding a connector, not presumed new infrastructure.

## Migration, compatibility and rollback

- Add nullable mappings/reference columns and additive tables. No value/asset/source payload copying.
- Backfill only deterministic ID mappings. Ambiguous mappings become candidates and require review; never join by display name alone.
- Existing `api/delivery-reporting/.../scope` behavior and persisted functional scope IDs remain supported through adapter projection.
- Existing connected systems/integration catalog data remains readable under legacy environment-scoped keys until explicit mapping is confirmed.
- Existing runs and Delivery snapshots remain immutable and readable; new sessions link them without rewriting.
- Rollback disables new API/UI routes and retains unreferenced additive rows; do not drop old columns/tables until long-term evidence migration is approved.

## Acceptance strategy

Automated tests to reuse/extend (source inspected, not run):

- `AIAssisted/backend/BirkNext.Api.Tests/DeliveryReporting/FunctionalQualityScopeTests.cs`
- `AIAssisted/frontend/BirkNext.Web.Tests/Components/FunctionalQualityScopeUiTests.cs`
- `AIAssisted/backend/BirkNext.Api.Tests/Services/CanonicalInterpretation/CanonicalInterpretationServiceTests.cs`
- `AIAssisted/backend/BirkNext.Api.Tests/Services/IntegrationJourneys/SkoleNaervaerJourneyPackTests.cs`
- `AIAssisted/backend/BirkNext.Api.Tests/Services/IntegrationJourneys/TestPackSourceRoleTests.cs`
- `AIAssisted/backend/BirkNext.Api.Tests/Services/IntegrationJourneys/JourneyReadinessTests.cs`
- `AIAssisted/backend/BirkNext.Api.Tests/Services/ExternalIntegrations/ConnectedSystemTestDataTests.cs`
- Test Data catalog, identity, XML fixture/version and schema-validation tests under `AIAssisted/backend/BirkNext.Api.Tests/Services/TestData/`.

Add tests for single/multi-scope; multi-system scope; one asset linked to several scopes; distinct readiness by scope/environment; unknown/ambiguous links; no presumed Altinn/MU/MM relationship; project isolation; scope membership diff across source snapshots; immutable historical result; retained human decisions after reanalysis; no invented requirement↔rule link; unique-entity coverage counts; existing Delivery behavior; existing Test Data behavior; backward-compatible API defaults; restart persistence; browser navigation selection; and no external traffic (fake executor/network-deny test fixture only).

## Unresolved decisions (human judgment)

1. Which existing project ID is the canonical isolation key where Project Import, Delivery and Target Environment models currently differ?
2. Which users may approve a scope and authorize active execution, given the local single-user deployment boundary and current Development/Tester package split?
3. Should a scope approval snapshot pin exact Test Data asset versions, or permit an explicit “latest approved” policy? Historical sessions must always pin the resolved version.
4. Which source evidence confidence qualifies for a generated confirmed membership versus candidate-only? Safe default is candidate-only until a user confirms.
