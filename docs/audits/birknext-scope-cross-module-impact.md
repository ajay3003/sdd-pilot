# BirkNext scope cross-module impact matrix

**Audit source window:** branch `008-traceability-first`; first observed HEAD `92aca19df8dd736f081a846c4d4ca02a90969023`, later observed `026ade937d7938faab3c0b0e6e112c87d27c10b5`. Worktree/HEAD changed during audit; no concurrent changes were investigated.

**Classification meanings:** Owner = canonical identity/membership lifecycle; Consumer = reads selected scope; Producer = supplies candidate evidence/membership; Contributor = review/linked artifact; Not applicable = no meaningful relationship. Local filters may remain local when they narrow a canonical selection without changing its membership.

| Module | Role | Current scope shape / evidence | Recommended relationship |
|---|---|---|---|
| Import Project | Producer | Creates project/source snapshot candidates and source identities. | Emit provenance-bearing candidate entities; never infer confirmed system relations. |
| Source Management | Producer | Selects/imports source snapshots and source roles. | Provide project/snapshot picker context. |
| Target Applications | Producer/Contributor | App identities/configuration occur in source and provider-specific setup; no global owner found. | Link app identity to project system and environment binding after review. |
| Target Environments | Owner (environment only) | Owns target selection/trust and environment settings. | Supply environment identity; do not own system or Test Scope. |
| Project Evidence | Producer/Contributor | Evidence records are module-/environment-specific. | Attach provenance/evidence references to members and sessions. |
| System Understanding | Producer/Contributor | Curated versioned guide, components, paths and source basis. | Provide expected/candidate relations; human guide approval stays distinct. |
| Source Analysis | Producer | Extracts routes, interfaces, schema and messaging architecture snapshots. | Feed candidate systems/edges and contract refs. |
| Technology & Analysis Coverage | Consumer | Coverage is per source/module/technology, not functional Test Scope. | Keep local filters; optionally accept project/source context. |
| Specification Explorer | Producer | Parsed spec artifacts and scenarios from selected source. | Candidate scope members via stable artifact IDs. |
| Specification Review | Consumer/Contributor | Reviews documents/requirements using local snapshot selection. | Consume scope when review asks “within scope”; keep document-specific filters. |
| Plan Explorer | Consumer/Producer | Displays plan/task artifacts and candidate relationships. | Consume selected membership; retain plan-local filters. |
| Task Explorer | Consumer | Task/document selection is local. | Optional scope-aware filter; no ownership. |
| Task Alignment Review | Contributor | Reviews alignment/correlation. | Preserve decisions in canonical relationship engine when same relation semantics apply. |
| Data Model Explorer | Producer/Consumer | Schema/entity discovery is model-specific. | Supply contract/schema references; local model filter stays local. |
| Schema Studio | Producer/Contributor | XSD/schema artifacts and generated data validation. | Attach schema IDs to relevant scenario/member; no global scope ownership. |
| Document Quality Review | Consumer | Document-centric quality review. | Optional scope filter; document type and version stay local. |
| Relationship Review | Owner (relationship decisions) / Contributor | Canonical entities/relations and confirmed/candidate/rejected decisions. | Preserve as canonical relationship owner; scope references its IDs. |
| Requirements Traceability | Consumer/Producer | Trace links/suggestions and requirement mapping are separate records. | Consume scope and contribute only established links; unresolved IDs remain unresolved. |
| Impact Analysis | Consumer | Canonical relations and impact run context. | Reuse canonical scope membership; impact traversal remains its engine. |
| Unified Impact Analysis | Consumer | Combines impact evidence and selected source context. | Consume shared scope revision; avoid a duplicate membership calculation. |
| Release Readiness | Consumer/Contributor | Aggregates delivery/test/readiness evidence. | Read scoped dimension vector; retain release policy and thresholds. |
| Snapshots | Producer/Consumer | Snapshot registry and reference index. | Pin source/scope/session revisions and retain immutable history. |
| Documentation Drift | Consumer/Producer | Compares documentation and source snapshot evidence. | Scope can limit affected systems/docs; drift rules stay local. |
| Test Data | Consumer/Contributor | Catalog assets have stable IDs, versions, approvals, context; bindings are per consuming mechanism. | Link reusable asset ID/revision policy to scope/scenario; readiness computed by required scenario. |
| Test Plan Suggestions | Consumer/Producer | Suggestions and test plan artifacts have local source/snapshot context. | Consume scope; generated plans reference scoped members. |
| Test Coverage & Overlap Review | Consumer/Producer | Test inventory, feature mapping and overlap review are separate from Delivery scope. | Reuse membership identity; keep test-coverage resolution and overlap logic local. |
| Test Execution & Evidence | Consumer/Owner (session evidence) | Execution artifacts and evidence are run-owned. | Test session pins scope revision and environment; evidence stays immutable. |
| Critical E2E Regression | Consumer | Existing E2E suite/scenarios have suite-local scope and evidence. | Allow scope selection and attach suite scenarios without equating labels. |
| Skolenærvær Testing | Consumer | Journey pack defines three separate paths and scenario-specific readiness. | Consume Test Scope; keep pack-specific prerequisites/executor policies. |
| Contract Testing | Consumer/Producer | Contract revisions and test definitions have contract identity/revision. | Link contract artifact to scope scenarios; retain contract version history. |
| Integration Test Suites | Consumer/Owner (suite definition) | Suite definitions/runs and `scope` query are suite-specific (`IntegrationSuitesPage.razor.cs`). | Consume shared scope ID; suite remains executable definition owner. |
| API Quality Review | Consumer | Uses API profile/obligations and review decisions. | Optional scope filter; profile is not Test Scope membership. |
| Integration Quality Review | Consumer/Producer | Per-environment integrations, contract/evidence binding and readiness. | Read system/environment refs; contribute configured relation evidence. |
| Frontend Quality Review | Consumer | Recommended/local source scope for analysis/run (`ApplyRecommendedScopeAsync`). | Keep frontend-specific source selection; optionally constrain by canonical scope. |
| Performance Test Review | Consumer/Owner (performance scenario) | Performance profiles/scenarios/runs and data profiles are specific. | Reference scope and shared asset IDs, keep performance workload assumptions local. |
| Code Risk Review | Consumer | Code/source risk analysis scope is source-driven. | Local file/language selection remains; optionally filter to systems/features. |
| Integration Topology | Producer/Consumer | Merges source, environment, catalog, observation and profile evidence. | Producer of candidate directional relationships; not runtime proof or scope owner. |
| Dependency Review | Consumer | Source inventory/review scope request (`ReviewSourceScopeRequest`) selects snapshots. | Reuse project source selection; dependency-specific filtering remains local. |
| Pipeline Review | Consumer | Pipeline evidence has environment/source-specific review scope. | Scope can link pipeline ownership where evidenced; keep pipeline checks local. |
| Generated Documentation Explorer | Consumer/Producer | Source-analysis-generated documentation findings; pre-existing dirty files exist in worktree. | Consume selected source/scope if useful; current dirty changes were not attributed to HEAD. |
| Generated Documentation Health | Consumer | Health/diagnostic filtering is documentation-specific. | Local filters remain local; optionally report scoped impact. |
| Delivery Overview | Consumer | Uses `CanonicalScopeFilter(SourceId, FunctionalScopeId)` then module/work/quality/search filters. | Retain canonical source/function scope and local presentation filters. |
| Delivery Reports | Owner (specialized Functional Quality Scope) / Consumer | Delivery scope API saves explicit membership, testability assessments and frozen snapshots. | Preserve specialized projection; bridge artifacts to shared scope identity, do not replace semantics. |
| Dashboard / Project Quality | Consumer | Aggregates quality signals by project/environment/source context. | Show scope-aware rollups only where denominator defined; avoid double counting shared assets/scenarios. |
| Recommended Workflow | Consumer | Workflow recommendations aggregate local capability/readiness signals. | Use canonical scope context if selected, but recommendations remain workflow logic. |

## Local selector policy

Promote a selector to shared scope only when it changes the set of assessed systems/scenarios/members and should be repeatable across modules. Keep filters such as text search, status, priority, file extension, quality category, open/closed state, date, pagination, performance workload, dependency severity and environment evidence view local. A module should carry `ProjectId + ScopeId + Revision` through navigation rather than translate a scope label into its own locally recomputed membership.

## Cross-module disconnected edges

| Existing boundary | Verified disconnection | Reuse path |
|---|---|---|
| Topology ↔ system identity | Topology composes components/connections but no project-owned durable registry CRUD. | Persist canonical IDs and topology provenance, keep topology read model. |
| Environment ↔ logical systems | ExternalIntegration and IntegrationDefinition both keyed to environment, but separate IDs and schemas. | Add reviewed references from each configuration to canonical system/relationship IDs; maintain compatibility. |
| Delivery scopes ↔ canonical scopes | Delivery scope definition is in Delivery DB; canonical functional scopes derive from canonical relations. | Bridge identities via explicit mappings and migration, retain Delivery snapshot membership. |
| Delivery scope ↔ journeys | Functional artifacts and journey scenarios have separate membership stores. | One shared scope revision references both artifact IDs and journey scenario IDs. |
| Test Data ↔ scopes | Stable asset references and consumer bindings exist, but no scope-to-asset association. | Many-to-many link table by scope revision/member and asset stable ID; preserve provider-owned value/version. |
| Readiness ↔ scope/environment | Journey readiness and connected system readiness are context-specific; not a common scope-session vector. | Compose existing readiness adapters per scenario/environment without moving evidence. |
| Snapshot changes ↔ user decisions | Canonical review and source basis exist, but no shared scope membership diff/rebase workflow. | Compare IDs and fingerprints across snapshots; keep approval and generated candidate membership separate. |
