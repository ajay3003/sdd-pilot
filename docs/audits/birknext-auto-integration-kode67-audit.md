# BirkNext — Automatic integration test generation + Kode 6/7: capability audit (2026-11-09)

Branch `008-traceability-first`. Input: the ATG gap list (ATG-01…ATG-12) and the Kode 6/7 brief. This audit records what already existed, what
was missing, and the disposition chosen — **reuse before build**: no new test engine, no second Kode 6/7 module, no second evidence store.

## Existing capabilities found (reused)

| Capability | Where | Used for |
|---|---|---|
| Source snapshots with contracts, configuration, tests, infrastructure evidence | Source Analysis (`IqrSourceStore`, Project Import) | suite input and provenance (archive, SHA-256, import, modules) |
| Contract Registry + Contract Testing (generation, negative proposals, readiness, runs) | `ContractRegistryService`, `ContractTestService` | contracts and tests; the only execution path |
| Test Data catalog, capabilities, drafts | `TestDataCatalog`, `TestDataResourceService`, `AuthoredTestDataStore` | journey-pack data requirements; synthetic drafts |
| Schema engine | `XmlSchemaPackageAnalyzer` (Schema Studio) | XSD dependency closure; fixtures in Schema Studio |
| Integration journeys (Skolenærvær pack, runner coverage) | `IIntegrationJourneyPackRegistry`, `IntegrationJourneyRunner` | flow stages, executor/observer coverage |
| Integration Topology | `IntegrationTopologyService` | mapping classes and review items |
| **Kode 6/7 — M01 Security Classification** | `Services/SecurityClassification` (static source review, gated GraphQL probe, test-lead approval, consumer projection; Skolenærvær consumer requirements *protected-downstream-handling*, *bufdata-exclusion*) | reused as `M01:*` rows and as the basis of K67-06/07 |
| Existing Kode 6/7 test ids | Skolenærvær catalogue: E2E-05 "Personvern \| Kode 6/7", DATA-03 "Kode 6/7 ikke i Bufdata" | referenced by K67-01/02 and K67-05 |
| Test Data capabilities for protected identities | `classification-level-{n}` (2 = Kode 7, 3 = Kode 6), `protected-identity`, `graded-access-*` | named prerequisites |
| Manual review decisions | `ManualReviewDecisionStore` | new module `integration-suites` |
| Execution Evidence | `ExecutionEvidenceStore` | run outcome of a suite |

## ATG gaps and disposition

| Id | Gap | Disposition | Status |
|---|---|---|---|
| ATG-01 | no single orchestration path from project/snapshot/scope to cross-module definitions | `IntegrationSuiteService` + `POST api/integration-suites` | **Implemented** |
| ATG-02 | evidence in separate stores, unlinked | suite = manifest of references (`key@vN`, `testId@rev`, `alias@vN`, stage/step ids, M01 ids, run ids); suite run-links table | **Implemented** |
| ATG-03 | topology mapping partly curated, ambiguity not guessable | Confirmed/Candidate/Ambiguous/Unresolved from Integration Topology; non-confirmed → review items | **Implemented** (no guessing) |
| ATG-04 | static discovery limits | limitations carried into the suite; missing scope modules listed | **Disclosed**, not solved |
| ATG-05 | no topology-wide suite / domain E2E | positive + negative contract tests per scope; journey stages from the Skolenærvær pack | **Implemented** for contracts; domain E2E steps remain the journey pack's |
| ATG-06 | no safe synthetic identities / domain records | seeded synthetic values for non-identity fields; identity fields refused → prerequisite; XML fixtures via Schema Studio | **Partial by design** — real-like protected persons must be approved Test Data |
| ATG-07 | environment configuration distinct/secret | configuration values with origin (Discovered/Inferred/Unresolved) and evidence; runs use the active Target Environment | **Implemented** (prefill/visibility; no secret retrieval) |
| ATG-08 | authentication prefill | security schemes surface as readiness gaps; credentials resolved only by Contract Testing at run time | **Implemented** as readiness; no credential generation |
| ATG-09 | executors/observers missing for MU/MM/period lock/Bufdata | stage coverage from the runner; missing adapters → waiting / not supported | **Disclosed**; adapters not built |
| ATG-10 | evidence correlation not universal | `Correlation` readiness dimension; downstream stages never Verified without per-message observer | **Disclosed + enforced** |
| ATG-11 | no generation workspace | `/integration-suites` page | **Implemented** |
| ATG-12 | current imported M2LB snapshot not established | snapshot chosen explicitly; provenance (archive, SHA-256, import, analysed at) on every suite | **Implemented** |

## Kode 6/7 brief — answers

- *Find existing Kode 6/7 tests* — M01 requirements are pulled from the Skolenærvær consumer projection with their state and review run id;
  E2E-05 and DATA-03 are referenced, not re-implemented.
- *Link them to the flow* — K67 variants reference the stage they need (Altinn executor, intake observer, MU/MM observer, Bufdata verifier).
- *Fill in configuration and test data* — from source evidence and Test Data capabilities; never real persons.
- *Show exactly what remains* — three groups (can run now / waiting for a named prerequisite / needs a domain decision) per item and as totals.
- Not done (forbidden or out of scope): no real Kode 6/7 persons, no permission escalation, no message submission, no period lock, no Blob
  writes, no privileged runtime execution without authorisation, no fabricated PASS, no M2LB production code, ADR-028 not approved.
