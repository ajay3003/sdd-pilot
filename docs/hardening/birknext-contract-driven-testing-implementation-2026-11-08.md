# BirkNext — Contract-Driven API & GraphQL Testing: implementation (2026-11-08)

Branch `008-traceability-first`, not pushed. Baseline: `docs/audits/birknext-contract-driven-testing-audit-2026-11-08.md`. Acceptance:
`docs/audits/birknext-contract-driven-testing-acceptance-2026-11-08.md`. Paths relative to `AIAssisted/`.

| Commit | Content |
|---|---|
| `669e055a` | CI-DEF-1/2/3 fixed with regression tests |
| `94163e97` | REST backend: registry, contract tests, Test Data binding, readiness, runs, impact |
| `ef073aeb` | Contract Testing page (API Quality Review) |
| `d942a277` | CDT-DEF-1 (found in browser acceptance) |
| `10ab13c5` | GraphQL on the same registry, tests, Test Data and evidence |

## 1. Contract import defects (audit findings)

Each was reproduced by a failing test first, then fixed.

| ID | Defect | Fix | Regression tests |
|---|---|---|---|
| CI-DEF-1 | `OpenApiExtractor` parsed request/response schemas against an empty schema map: every `$ref` schema was lost; AQR skipped shape validation or reported a false "HTTP 200 is not documented". Red: the false finding reproduced exactly. | Component schemas parsed first; `$ref` request/response schemas resolve to them. Removed the empty `ResolveReferences`. | `OpenApiExtractorDefectTests.CI_DEF_1_*` (2) |
| CI-DEF-2 | Operations without `operationId` were all identified by their verb. | `NormalizedOperation.Key` = `METHOD /path-template`; `Id` = the `operationId` only when present **and unique** in the document, else the key; `OperationId` keeps the declared value. | `CI_DEF_2_operations_without_operationId_get_distinct_method_and_path_ids` |
| CI-DEF-3 | AQR fetched any configured contract URL (a cloud-metadata address was requested); the unused fetchers blocked by host-name prefix (`fd…`/`fc…` names refused). | One `ContractSourcePolicy` + `ContractSourceFetcher`: http(s) only, no credentials in the URL, link-local / metadata / unspecified / multicast / broadcast refused on the literal host, on every redirect hop (≤ 3, followed manually) and at connect time after DNS (`ConnectCallback`). Loopback and private ranges stay allowed (internal test environments; the run-level safety policy still applies). Used by AQR (OpenAPI and GraphQL schema URLs), the two discovery fetchers and the registry. | `ApiReviewEngineContractFetchTests` (17 incl. policy theory cases) |

## 2. Model

- **Registry** (`api_contracts`, `api_contract_versions`, migration `20261108064908_AddContractTesting`): a contract has a key (slug), name and
  protocol (Rest | GraphQl); a version is immutable: SHA-256, source (Upload | Url), source label (file name, or URL without its query), format
  (json / yaml / graphql / introspection), the document text (≤ 5 MB) and the parsed `SourceContract` JSON. An identical document is not stored again
  (also across contracts: the import says which contract already has it). A contract keeps one protocol.
- **Readers**: OpenAPI 3 / Swagger 2, JSON or YAML, through `ContractAnalyzer.ReadOpenApiDocument` (the same path Source Analysis uses for a
  contract file); GraphQL SDL through `ReadGraphQl`, introspection results (`{"data":{"__schema"}}` or `{"__schema"}`) through
  `ReadGraphQlIntrospection` — both via one mapping `GraphQlSchemaContract`. Operations documents and partial schemas are refused for the
  registry. `ContractOperation.ResponseSchemas` (code → schema/type name) is new.
- **Identity**: operation `METHOD /path-template` (REST) or `query field` / `mutation field` (GraphQL); `ContractIdentity.OperationRef` =
  `key@vN#operation` → Execution Evidence `OperationRef`; run `ContractRefs` = `key@vN`; `DefinitionVersion` = `contract-test:{id}@r{revision}`.
- **Contract tests** (`contract_tests`): `ContractTestDefinition` — contract key and version, operation, kind (Positive | MissingRequiredInput),
  revision, template (`ExecutionCallDefinition` with `{{dataset.x}}` placeholders, never a credential or a resolved value), inputs (name, location,
  required, type), binding (`alias`, `version`), rationale.
- **Execution Evidence**: `execution_calls.DefinitionRef` (indexed) + `ExecutionEvidenceStore.LatestByDefinitionAsync`. No second result store.

## 3. Generation rules (`ContractTestRules`)

| | REST | GraphQL |
|---|---|---|
| Inputs | path parameters (required), query and non-credential header parameters, JSON body fields of the request schema type (or the whole body as one input) | one variable per argument with its exact type; input-object arguments become nested variables whose fields are the inputs |
| Positive assertions | status in the documented 2xx codes (or 200/201/202/204 when none is documented); `$.field` exists for required properties of the documented success schema | HTTP 200, GraphQL error count 0, `data.field` exists and its non-null leaf fields; **an HTTP 200 with errors fails** |
| Negative | omit the first required query parameter or body field; expect 400/422 (the documented ones, else both) | omit the first non-null variable or input field; expect GraphQL errors, HTTP 200 or 400 |
| Not generated | — | subscriptions (no streaming transport) |

Materialization replaces placeholders with the pinned Test Data values: path values URL-escaped, whole-placeholder JSON values take the exact
JSON type (numbers stay numbers) at any depth, an optional input without a value is left out, a missing required value stops the call (never a
default).

## 4. Test Data picker and binding

`DatasetOptionsAsync` offers authored datasets and key-value collections (environment-scoped ones only for that environment) with: approved
version, provided value names, missing required and missing optional values, problems (no approved version, deprecated), compatible = nothing
required missing. Binding accepts only the currently approved version and pins it (`alias@vN`); a later approval of a newer version does not move
the pin (readiness warns). Dataset members: inline typed values, or references to an added single value (pinned or approved version).

## 5. Readiness (per test, for the selected Target Environment)

Contract (impact of the latest version: Broken → Blocked, NeedsUpdate → Missing, Review → Warning) · Test Data (binding, deprecation, missing
required values, newer approved version) · Target Environment (the AQR `IApiEnvironmentSafetyPolicy`, unchanged) · Authentication (an operation
that declares security is **Blocked**: runs cannot use the Target Environment's credential yet and are never sent anonymously in its place;
GraphQL schemas declare no authentication, so GraphQL runs are anonymous) · Changes data (warning: confirmation needed).
Ready = nothing Blocked or Missing. Only Ready tests are sent; the rest are listed as not run.

## 6. Runs

`ContractTestService.RunAsync` builds `ExecutionCallDefinition`s and calls `ExecutionRunService.StartAsync` unchanged. Optional API base path
(relative only: refused when it is not a single-slash path or carries a host, query, fragment or backslash) prefixes every call path. GraphQL posts
to `/graphql` under that base path.

## 7. Contract change and impact

`ContractRegistryService.Changes` = `SourceEvidenceDiff.ContractChanges` (unchanged) + new `OperationDetailChanges` (parameters/arguments
added/removed/changed, request body, success responses removed or changed, result type). New `ContractChangeClass` values: ParameterAddedRequired,
ParameterAddedOptional, ParameterRemoved, ParameterChanged, RequestBodyChanged, ResponseChanged. `ContractTestRules.Impact` per test: operation
removed → Broken; required parameter/argument added, parameter or body changed, a required request/input field added or a request type field
changed, enum value removed in an argument type → NeedsUpdate; success response or result type changed → Review; optional additions →
Unaffected. Regenerate re-derives the test from the latest version, keeping id, name, kind and binding (new revision).

## 8. UI

`/api-quality-review/contracts`, linked from the AQR header ("Contract tests"): contracts (protocol, version, counts), import (file or URL, new
contract or next version, optional name), version facts (source, format, fingerprint, read completeness, limitations), Operations / Tests / Changes
views (state in the URL; string `tabindex` values), test detail with readiness items, inputs, the dataset picker, template (REST request or GraphQL
document + variables), regenerate and delete; run selected Ready tests with explicit data-changing confirmation and a link to the run in Execution
Results; each test's latest result links to its call.

## 9. Defect found during acceptance

| ID | Defect | Fix | Regression test |
|---|---|---|---|
| CDT-DEF-1 | Browser CT-03: after typing an import URL the Import button stayed disabled until the field lost focus (bound on `change`). | URL and base-path inputs bind on `input`. | `Typing_a_URL_enables_Import_without_leaving_the_field` (failed first: no `oninput` handler) |

## 10. Tests added

Backend: `OpenApiExtractorDefectTests` (3), `ApiReviewEngineContractFetchTests` (19 incl. theory cases), `ContractTestingTests` (11 incl. 5 base-path
theory cases: REST end-to-end, GraphQL end-to-end, introspection import, materialization, base path). Frontend: `ContractTestingPageTests` (9).

## 11. Supported / not supported

**Supported (verified)**: OpenAPI 3 JSON and YAML, Swagger 2 (generic reader, Partial), GraphQL SDL and introspection; upload and URL import; immutable
versions with fingerprints and unchanged detection; operation explorer; REST and GraphQL draft generation (positive + one negative); dataset picker
with missing values; pinned binding; readiness; runs through Execution Evidence with operation, contract-version, Test Data and revision references;
latest result per test; version diff and per-test impact; regenerate.

**Not supported (registered gaps)**

| ID | Gap |
|---|---|
| GAP-CDT-1 | Authenticated contract runs (inherits GAP-EE-2): secured REST operations are Blocked; GraphQL runs are anonymous. |
| GAP-CDT-2 | No full JSON Schema validation of request/response bodies: positive REST tests check status and top-level required properties of the documented schema only; arrays and nested objects are not validated. |
| GAP-CDT-3 | Test definitions cannot be edited (assertions, values, names) — only bound, regenerated or deleted. |
| GAP-CDT-4 | OpenAPI `servers` are not applied; the API base path is entered by the tester. The GraphQL endpoint path is fixed at `/graphql` (+ base path). |
| GAP-CDT-5 | One negative test per operation (one omitted required value); no boundary, enum, type or authorization negatives. |
| GAP-CDT-6 | Impact is conservative: a negative test is marked Needs update by a new required input even when it would still hold. |
| GAP-CDT-7 | Source Analysis snapshot contracts cannot be imported into the registry; Test Data's own REST-template operation picker still lists snapshot operations, not registry operations. |
| GAP-CDT-8 | YAML OpenAPI and Swagger 2 are read by the generic tree reader (Partial; `$ref` chains in types not resolved). |
| GAP-CDT-9 | `graphql_schema_artifacts` (AQR runtime fallback) is still a separate GraphQL schema store. |
| GAP-CDT-10 | AQR still fetches its `SwaggerUrl` per run instead of reading a registry version. |
| GAP-CDT-11 | No navigation sidebar entry (reached from the AQR header or the URL); no contract or version deletion; no retention. |
| GAP-CDT-12 | GraphQL selection sets contain leaf fields only (unions/interfaces/nested objects → `__typename`); fragments are not generated. |
