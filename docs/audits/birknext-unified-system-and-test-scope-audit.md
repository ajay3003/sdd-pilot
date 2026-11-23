# BirkNext unified system and test scope audit

**Audit date:** 2026-11-13  
**Repository:** `BirkNext`, branch `008-traceability-first`; first observed HEAD `92aca19df8dd736f081a846c4d4ca02a90969023`; a later read during this audit observed `026ade937d7938faab3c0b0e6e112c87d27c10b5`  
**Method:** read-only static source and existing-test inspection. No tests, builds, network calls, database access, or runtime claims were made. Existing dirty files were preserved.

## Executive conclusion

BirkNext has useful foundations but no single project-scoped system and test-scope registry. The best small change is a **hybrid canonical registry**: a shared project-owned identity/membership layer, reusing the existing `SnapshotRegistry` and `CanonicalInterpretation` entity/relation infrastructure where their semantics fit; retain Delivery Functional Quality Scope as a specialized Delivery projection and retain environment provider configuration, Test Data assets, contracts, topology, and evidence in their current owning stores. Add a Test Scope projection over those canonical identities rather than moving system identity into Target Environments or replacing the existing engines.

Evidence for this distinction:

- `ExternalIntegration` explicitly represents one external system instance for a Target Environment (`AIAssisted/shared/ExternalIntegrationContracts.cs`); rows are keyed by environment and ID (`AIAssisted/backend/BirkNext.Api/Models/IntegrationCatalogRecords.cs`, `AppDbContext.cs`). This is configured connection data, not an environment-independent system registry.
- Integration Catalog definitions are also environment-keyed and have producer/consumer fields (`IntegrationCatalogRecords.cs`, `IntegrationCatalogContracts.cs`). Topology synthesizes configured component/connection views from them (`Services/IntegrationTopology/TopologyProviders.cs`, `IntegrationCatalogTopologyProvider`).
- Source Analysis already extracts application architecture and named producer/resource/consumer messaging channels; topology consumes that result instead of re-parsing source (`TopologyProviders.cs`, `SourceArchitectureTopologyProvider`).
- Functional Quality Scope is a persisted Delivery-owned scope over parsed specification items. Its data is references and assessment, not a canonical BirkNext-wide Test Scope (`shared/FunctionalQualityScope.cs`; `Data/DeliveryReporting/DeliveryReportingEntities.cs`; `FunctionalQualityScopeController.cs`).
- Canonical Interpretation has source-scoped entities and reviewable relations, and `FunctionalScopesAsync` only counts established `Feature Implements Capability` relations; candidates remain separate. It does not itself define project-selected multi-system test journeys (`CanonicalInterpretationService.Scopes.cs`, `shared/CanonicalScope.cs`).
- Integration Journey Packs already model distinct journeys and scenario prerequisites; Skolenærvær source code explicitly says the Altinn/MU/MM and period-close/Bufdata journeys are not all backed by source, contracts, or executable observers (`Services/IntegrationJourneys/Packs/SkoleNaervaer/SkoleNaervaerJourneyPack.cs`).

## Current capability matrix

| Capability | Verified implementation and data owner | Reuse and limitation |
|---|---|---|
| Project / imported project | Project Import API/service (`Controllers/ProjectImportController.cs`, `Services/ProjectImport/ProjectImportService.cs`); snapshot identities in `Data/SnapshotRegistry/SnapshotRegistryDbContext.cs` and `SnapshotRegistryService.cs`. | Import/source identity and immutable archive evidence are reusable. No single project aggregate is a foreign-key parent of environment integrations and functional scopes. |
| Target Environment | Frontend environment model/service plus backend-trusted target and environment-scoped persistence (`frontend/BirkNext.Web/Models/TargetEnvironment.cs`, `Services/TargetEnvironmentService.cs`, `Services/TargetEnvironmentDetection/*`, `Data/AppDbContext.cs`). | Suitable owner of deployment target/configuration, not logical system identity. Environment IDs are used as keys across multiple integration tables. |
| Connected Systems | `ExternalIntegrationsController` route `api/target-environments/external-integrations`; `ExternalIntegrationService`; `ExternalIntegrationRecord` composite environment+id identity. | Configured provider instances, Test Data bindings and readiness. Not global registry. Detection is provider-specific; does not establish arbitrary source topology as a connection. |
| Integration Catalog | `IntegrationsController`, `IntegrationCatalogService`, `IntegrationDefinitionRecord` and platform records keyed `(EnvironmentId, Id)`. | Configured logical producer→consumer connections; topology labels them Configured and explicitly does not claim runtime verification. Not the same entity as an ExternalIntegration. |
| Source snapshot | `IqrSourceSnapshot` and snapshot registry identities; source analysis stores derived facts and provenance, not original source text in the registry. | Stable provenance and immutable source evidence can seed candidate entities. Cross-project use must continue to include source/project identity and not match display names alone. |
| Integration Topology | `IntegrationTopologyController`, topology builder/providers; inputs include trusted environment, source architecture, Integration Catalog, observation and domain profiles. | Best existing discovery aggregation. Configured/discovered/observed states and provenance are represented. Topology view is not a persisted system registry and expected profile paths do not create connections. |
| System Understanding | `SystemGuideService`, `SystemGuideRevisionRecord`, guide catalog/profile/pack; `system_guide_revisions` stores revision JSON, fingerprint and source basis. | Human-readable, versioned guide and scenarios. Installation/catalog keyed guides are not project-scoped canonical entities; guide statements are not runtime evidence. |
| Functional Quality Scope | `FunctionalQualityScopeService`; API `api/delivery-reporting/projects/{projectId}/scope`; Delivery tables `functional_scope`, `functional_scope_reference`, `functional_scope_snapshot` and assessment table. | Reusable membership lifecycle, references and freeze patterns. Domain restricted to Delivery parsed specs/modules/profile flows; project ID is a Delivery project ID, not shared project identity. |
| Canonical functional scopes | `CanonicalInterpretationService.Scopes.cs`, `CanonicalScopeFilter` and canonical entity/relation tables in `Data/CanonicalInterpretation`. | Strong relation provenance, review state and candidates. Scope filter supports source, functional scope, module; does not capture explicit selected systems, environments, or test session. |
| Test Data | `TestDataController` route `api/test-data`; `TestDataCatalog`, provider stores, registry/identity store, lifecycle, schema validation. Asset has immutable ID, version, approval, environment, context and schema references (`Services/TestData/TestDataAsset.cs`). | Reusable reference without value copying is supported at asset level. Context is suggestion metadata; it is not a many-to-many association to canonical scopes. Some consumer-specific bindings exist (ExternalIntegration and suites). |
| Contracts / Schema | Integration contract artifacts, contract testing revisions, Source Schema artifacts and Schema Studio/XSD services. | Reuse by stable artifact references/hash is possible; references and stores differ by module and environment/provider. Not automatically bound to Test Scope. |
| Test journeys / suites | `IntegrationJourneysController` routes `api/integration-journeys/*`; registered journey packs, scenario readiness and immutable run records. Separate integration suite controller/store. | Actual scenario-specific prerequisite and run machinery exists. Journey packs do not share canonical project/Test Scope membership; execution can involve explicit external effects and is separately gated. |
| Evidence / readiness | Integration journey runs, Test Data validation evidence, integration review snapshots, canonical review decisions, snapshot registry. | Multiple purpose-specific evidence stores. They must remain distinct; a new scope should reference them, not duplicate or flatten them. |
| Delivery impact / reporting | Delivery reporting APIs and canonical filter (`DeliveryReportingController`, `DeliveryScopeService`, Delivery pages). | Canonical filter is consumed for source/functional scope in Delivery. Delivery has separate saved Functional Quality Scope. This is a valid specialized projection plus an integration seam, not proof of shared ownership. |

Details and class/table ownership appear in [ownership map](birknext-scope-and-system-ownership-map.md); consumers are classified in [impact matrix](birknext-scope-cross-module-impact.md).

## Data flow and breakpoints

1. **Import:** Project Import accepts a repository/archive and creates immutable snapshot identities. Source files are analyzed to `SourceArchitecture`/contracts/schema facts. The registry keeps identity, hash, source and storage metadata, not original text. Imported source identity is not directly a foreign key on ExternalIntegration or Delivery scope.
2. **Discovery:** `SourceArchitectureTopologyProvider` translates already-extracted architecture facts into topology components, interfaces and named messaging resource edges. `TargetEnvironmentTopologyProvider` adds trusted configured components/locations. `IntegrationCatalogTopologyProvider` adds configured producer/consumer paths. Other providers add observation/configuration evidence. The topology builder merges a view with provenance and status; it does not persist a canonical registry membership itself.
3. **Review:** Canonical Interpretation persists entities and relations, including review state, source/snapshot references, rule and explanation. It is reused by Delivery scopes and other relationship/impact consumers, but is not wired as the universal test scope.
4. **Contracts:** Schema/contract sources are persisted in module-specific artifact/revision stores. Integration Catalog has per-environment contract artifacts and evidence bindings; no shared selected-scope relation ties all source contracts to journeys.
5. **Test Data:** `TestDataCatalog` merges providers and identity metadata. Assets use stable IDs and versions; consumers bind references. `ExternalIntegration.TestDataBindings` is requirement→asset ID for that specific configured environment integration. `AppliesTo`/tags are metadata for candidate selection, not explicit scope membership. Copying asset values into scopes would be incorrect.
6. **Definitions:** Delivery's `FunctionalQualityScopeService` resolves features/stories/scenarios/requirements and test design. Journey packs separately define Integration Journey steps/scenarios and compute readiness. Test Plan Suggestions, Test Coverage, API/Integration Quality and other reviews have separate local scope/profile models; they are not automatically consumers of a shared scope ID.
7. **Execution and evidence:** Journey `runs` validates and starts a selected journey; historical run/evidence is stored by the journey engine. Test scope selection is not a common persisted selector attached to that run today. Delivery freezes scope membership separately in `functional_scope_snapshot`.

**Manual re-entry / loss points:** topology’s merged system names are not saved as project-owned logical system identities; configured external instances and catalog entries are separately created per environment; source/guide/profile statements are not automatically linked to confirmed canonical entity IDs; Test Data asset context is not a scope association; scope membership and scenario membership live in different engines; environment and auth configuration remains in their specific owning integrations. Source-derived facts can prefill candidates, but must not silently become a configured endpoint, auth binding, approved relation, or runtime-verified route.

## Source discovery coverage

`SourceArchitectureTopologyProvider` consumes Source Analysis output. It is a topology adapter, not an independent parser. The architecture analyzer and domain extractors should be reused. Based on the inspected call chain:

| Evidence type | Current status | Evidence / boundary |
|---|---|---|
| REST routes and OpenAPI | SUPPORTED for source-analysis facts and topology interfaces; not a complete auto-created runnable test suite. | Source architecture interface extraction; `TopologyProviders.cs`. OpenAPI route/declaration discovery code is in Source Analysis and imported contract artifacts. |
| GraphQL endpoint/schema | SUPPORTED/PARTIAL: GraphQL interfaces/schema facts can be represented and contracts consumed; no universal generated runtime test definition. | Source architecture interface classification and GraphQL contract testing modules. |
| XSD/XML | SUPPORTED for contract/schema and XML Test Data validation; input generation and journey binding remain separate. | `TestDataSchemaValidation.cs`, `TestDataXmlBinder.cs`, Schema Studio/XSD stores. |
| JSON Schema | SUPPORTED as contract artifact/validation capability; automatic cross-module binding is PARTIAL. | Integration contract artifact and contract test revisions. |
| Service Bus/Event Hub/Wolverine | SUPPORTED when Source Analysis detects named channels and producer/consumer endpoints; unresolved channel identity deliberately does not join components. | `SourceArchitectureTopologyProvider`; resource-centred queue/topic/hub construction. |
| Blob/storage resources | PARTIAL: source architecture classifies storage interface evidence; no general Blob Storage Explorer resource identity-to-test-scope binding established in this audit. | topology provider classifier plus separate Blob/Parquet tools. |
| Terraform/environment references | PARTIAL: Skolenærvær pack declares IaC source as optional source role; infrastructure provider can contribute facts; source evidence is not automatically an endpoint binding or trusted runtime environment. | Journey pack/source roles; topology source provider registrations. |
| Application config and dependency/routing | SUPPORTED as extracted source facts where analyzer recognizes it; topology/Integration Quality merges evidence. Exact analyzer coverage per language/framework was not exhaustively enumerated. | source architecture, application messaging and configuration analyzers. |
| Business validation rules | PARTIAL: journey packs and guide contain explicit reviewed/curated rules; no general extraction from arbitrary source/spec into executable assertions. | `SkoleNaervaerJourneyPack`, `SystemGuideService`, source-domain analyzers. |

Unknown analyzer support for a particular source language or imported archive is **Not assessed** here; no source snapshot was selected or reanalyzed in this static audit.

## Test generation status

| Test kind | Status | Current basis |
|---|---|---|
| REST positive/negative | PARTIAL | API Quality and contract test engines can assess and model API obligations; no universal “generate and execute all route tests” path evidenced. |
| GraphQL positive/negative | PARTIAL | Schema/contract-aware capabilities exist; no shared generation into an integration test scope evidenced. |
| XML/XSD fixtures | SUPPORTED/PARTIAL | Test Data schema validation and fixture binding/versions are implemented. Automatic synthetic fixture generation from any imported XSD and use by arbitrary journey is not universal. |
| Test Data records | SUPPORTED | Catalog, providers, immutable IDs/revisions, approval and candidate resolver; reusable binding patterns exist. |
| Authentication requirements | PARTIAL | Integration/provider-specific readiness/config and runtime auth models; no universal source-to-safe-secret binding. Secrets must remain external and never be generated from source. |
| Expected assertions | PARTIAL | Journey scenario steps and domain-specific rules exist; generated arbitrary expected outcomes from source are not evidenced. |
| Multi-step workflows | SUPPORTED for authored/registered packs; PARTIAL for automatic source-generated workflows. | Integration journeys and suites. |
| Dataset-driven runs | SUPPORTED in Test Data and performance scenarios; PARTIAL as a common scope/session feature. | Test Data sets and scenario binding models. |
| Message verification | PARTIAL | Journey message verification and message-flow configuration exist; observer/runtime evidence is scenario/provider-specific. |
| Downstream artifact verification | PARTIAL/BLOCKED by journey | Period-close journey has expected Bufdata path but no general downstream artifact observer/read surface for all paths. |

Scenario descriptions, a journey definition, a rule recommendation, and an executable test are distinct. The above statuses refer to actual engines, not documentation statements.

## Scope ownership and readiness recommendation

Canonical project context owns stable project identity; imported source snapshots remain immutable and source-owned. A shared System Registry should own project-local logical system identity with provenance. Confirmed directional relations should be represented by the canonical relationship engine; source-discovered candidate edges must retain provenance and review state. Target Environment owns deployment/environment identity and its URLs/credentials references. Environment connections reference a logical system ID. A versioned shared Test Scope owns selected membership and revision/snapshot identity, while Delivery continues projecting its own artifacts and journey modules keep scenario semantics. A Test Session owns selected scope revision(s), environment, authorization, and evidence references.

Readiness must be a vector, not one undifferentiated Ready bit: scope definition; system/relation resolution; environment; scenario-required Test Data; contract presence/validation; dependency testability; authorization; execution; observed outcomes/evidence. Calculate applicability per scenario × environment; unknown is Not assessed, and an unrelated prerequisite cannot block another journey.

## Skolenærvær evidence case

The repository has an explicitly curated `skolenaervaer.guide.json`, Skolenærvær journey pack, topology profile, Altinn reference, source-role rules and focused tests. The guide describes the candidate path Altinn→MU→MM→Skoletjenesten and separately period close→Bufdata; it states source-generation uncertainty. The journey pack explicitly separates `rapportmottak`, `periodeavslutning`, and `utdanningsdata`, and says no MU, MM, report-service, Bufdata or Altinn source/contract/read surface is present for those paths. This audit did not independently open the user's external imported M2LB archives, so no claim is made about their current availability or contents.

| Candidate scope | Evidence-backed members/prerequisites | Do not infer |
|---|---|---|
| Skolenærvær — report intake | Altinn submission scenarios include valid report, person-not-found and overlapping report; Altinn app reference and scenario-specific synthetic reporter/child/XML requirements; pack source roles include MU and M2LB for architecture evidence. | That MU→MM→service is executable end-to-end; that candidate topology edges are confirmed; that period-close/Bufdata prerequisites apply. |
| Skolenærvær — period lock and Bufdata delivery | Separate period-close journey and expected `skole.perioder` event path; period lock lifecycle and Bufdata dataset are source-generation-dependent; a downstream observer/readiness is required. | That all M2LB source generations implement lock/export; that this journey requires Altinn reporter/XML fixture; that the diagram alone proves deployed routing. |
| Utdanningsdata | Separate BiRK→Debezium→Event Hub→SkoleAdapter→Utdanning path delegated to existing Active Event provider, with its own source, trusted-environment and execution prerequisites. | That it is a dependency of report intake. |

Journey-specific evidence is in `Services/IntegrationJourneys/Packs/SkoleNaervaer/SkoleNaervaerJourneyPack.cs`, `SkoleNaervaerTopologyProfile.cs`, `SystemUnderstanding/Guides/skolenaervaer.guide.json`, and tests `Services/IntegrationJourneys/SkoleNaervaerJourneyPackTests.cs`, `TestPackSourceRoleTests.cs`, `JourneyReadinessTests.cs`, and `Services/ExternalIntegrations/ConnectedSystemTestDataTests.cs`.

## Gap register and architecture alternatives

Gap IDs and detailed acceptance conditions are in the [implementation plan](birknext-unified-scope-implementation-plan.md). Principal gaps are project-wide system identity, common scope identity/membership consumed by journeys and Test Data, source-to-canonical mapping with review, and scope-aware multi-dimensional readiness.

Alternative A (extend Delivery Functional Quality Scope) reuses snapshots/selection but inherits Delivery DB and feature semantics; rejected as sole canonical owner. Alternative B (shared Scope Registry) gives shared identities and specialized projections, but must reuse canonical entities/relations and existing stores rather than duplicate them; **recommended hybrid form**. Alternative C (system-centric scope) helps integration topology but cannot represent story/requirements-only functional work or scenarios independent of cross-system edges; rejected as primary scope model.

See [target architecture proposal](birknext-unified-scope-architecture-proposal.md) for identity, lifecycle, data flow and UI; [migration plan](birknext-unified-scope-implementation-plan.md) for increments, tests, migration and rollback.

## Audit limitations

- Static source and test-source inspection only; no tests or runtime behavior verified.
- No external/imported M2LB archives were opened during this audit, and no external service was contacted.
- The full route-by-route extractor support of each language analyzer and every module's persistence schema was not exhaustively enumerated; unknowns are identified rather than asserted.
- Existing user edits and local configuration were left untouched. This report uses committed/currently readable source and does not claim those dirty changes are part of HEAD.
- HEAD and dirty-file status changed during the audit; no cause was investigated and no concurrent activity was touched. The exact source snapshot for every observation should therefore be treated as unpinned between the two recorded HEADs.
