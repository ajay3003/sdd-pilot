# BirkNext — Contract Testing complete product hardening: audit (2026-11-10)

Branch `008-traceability-first`. Baseline `b8df19f0` (automatic contract test data + completed regression). Read-only audit of the Contract Testing
page (`/api-quality-review/contracts`), its backend (`Services/ContractTesting`, `ContractTestingController`) and the shared contracts, against
the product brief and the three acceptance criteria: (1) import M2LB once and have the contracts available from Source Analysis, (2) Generate
Tests + Test Data created and linked automatically with clear blockers, (3) accurate coverage — definition coverage rises on creation, execution
coverage only after documented execution.

## What already existed (reused, not rebuilt)

| Capability | Owner | State at baseline |
|---|---|---|
| Contract registry: immutable versions, upload / URL / Source Analysis snapshot, protocol kept per contract | `ContractRegistryService` | sound |
| Retained OpenAPI/GraphQL document text per snapshot (full schema validation) | `source_contract_documents` | sound (0ed74d1f) |
| Test generation per operation (REST + GraphQL), negatives on request, update proposals for newer versions | `ContractTestService`, `ContractTestRules`, `ContractNegativeRules` | sound |
| Automatic typed synthetic test data, validated, approved/pinned only when certain | `ContractTestDataService`, `ContractTestDataRules` | sound (3a4fc07e) |
| Readiness per test (Target Environment, endpoint, authentication, Test Data, contract) | `ContractTestService.ListAsync` | sound |
| Runs through Execution Evidence with the data-changing confirmation gate | `ExecutionRunService` | sound |
| Chains, version diff + impact, coverage report, lifecycle/retention | `ContractTestService`, `ContractRegistryService` | functional |
| Integration Suite Generator | `IntegrationSuiteService` | sound (d9426f10…) |

## Findings

| Id | Area | Finding | Evidence | Severity |
|---|---|---|---|---|
| CTP-A1 | Import source | Source Analysis was the third radio option; the default was a file upload, so the contracts a project import had already analysed were hidden behind a click | `ContractTesting.razor` import card (`_importFromSource` default false) | High (criterion 1) |
| CTP-A2 | Discovery | The source list showed **every contract of up to 20 snapshots**: re-analysing a project listed each file again (35 contracts → 70 rows after a second import); no grouping, no search, no state | `SourceCandidatesAsync` loop over snapshots × contracts | High |
| CTP-A3 | Versioning / duplicates (root cause) | Importing a source contract **without choosing a target** always created a **new contract** unless it came from the very same snapshot. A newer snapshot of the same project produced a second contract per file; the "next version" continuation existed only inside the Integration Suite Generator (its own earlier revision), so importing in Contract Testing and then generating a suite (or vice versa) duplicated contracts | `ImportFromSourceAsync` (`UniqueKeyAsync(name)` branch); `IntegrationSuiteService` `continues` lookup | High (criterion 1) |
| CTP-A4 | Fingerprint | The source fingerprint hashed the whole parsed evidence (code references, line numbers, consumer hints) plus the document hash: a byte-identical OpenAPI document in another archive of the same project would have become a spurious new version | `ImportFromSourceAsync` `sha` | Medium |
| CTP-A5 | Bulk | No bulk import: 35 contracts meant 35 clicks, then per contract Generate tests and Generate test data | UI + API surface | High (criterion 2) |
| CTP-A6 | Coverage | Definition and execution figures were mixed in one table; a metric with nothing to count rendered as `0 / 0`, and execution metrics with nothing executed rendered as `0 / 12 (0%)` — read as a measured 0 % | `CoverageView`, `ContractCoverageMetric` | High (criterion 3) |
| CTP-A7 | Coverage | No definition figure for prepared test data (the result of the new automatic data step was invisible in coverage) | `CoverageAsync` | Medium |
| CTP-A8 | Operations | 17-operation contracts rendered as a wide table without search; no per-operation test-data state; data-changing operations not marked before generation; "Generate" was a secondary button and its message still said "not REST" (GraphQL is supported) | `OperationsView`, `GenerateAsync` message | Medium |
| CTP-A9 | Tests / safety | "Select every ready test" selected data-changing tests too (the backend gate still refused them without confirmation, but the selection suggested otherwise); no filter (ready / blocked / missing data / changes data / impacted); blockers only visible per test | `SelectReady`, `TestsView` | Medium |
| CTP-A10 | Readiness | Endpoint, authentication and Test Data blockers were only visible inside each test's detail; no summary of what stops the contract's tests | `TestsView` | Medium |
| CTP-A11 | Chains | No readiness per chain (a chain whose steps are not ready looked runnable until the run refused it) | `ChainsView` | Low |
| CTP-A12 | Changes | No summary (breaking vs compatible vs affected tests) and no way to focus on potentially breaking changes | `ChangesView` | Low |
| CTP-A13 | Suite hand-off | No link from a contract to the Integration Suite Generator; the generator could not be opened preselected | both pages | Medium |
| CTP-A14 | Validation depth | Whether a version gets full schema validation or only a structural check was only in a long limitation sentence | detail header | Low |
| CTP-A15 | Layout | Import form inside the 18 rem side column; long source lists and wide tables made the page form-heavy | layout | Medium |

No finding showed generated data being invented for identities/credentials, a request being sent on import/generation, or a run bypassing the
data-changing confirmation — the safety properties of the earlier work hold.

## Decision

Fix A1–A15 in the existing owners (registry, test service, page); add one thin orchestration service for bulk import that calls the existing
import, test generator and test-data generator (no new engine, no new store, no duplicated logic). Move the source-contract continuation into the
registry so Contract Testing and the Integration Suite Generator share it.
