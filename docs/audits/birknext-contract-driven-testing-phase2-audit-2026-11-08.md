# BirkNext — Contract-Driven Testing phase 2: audit (2026-11-08)

Baseline HEAD `93e38abee495386baf9dfdc4fd9d39df85d9ac2a` on `008-traceability-first` (clean except the user's `appsettings.Local.json`, `AIAssisted/testdata/`
and another session's untracked `docs/audits/birknext-final-*` / `release-assurance` files, none touched). Contract-Driven Testing commits:
`669e055a`, `94163e97`, `ef073aeb`, `d942a277`, `10ab13c5`, `7df5dbb9`, `93e38abe`. Read-only audit of the code as it is, not of the earlier reports.
Paths relative to `AIAssisted/`.

## 1. Owners found in the code

| Concern | Owner (actual code) |
|---|---|
| Contract store | `Services/ContractTesting/ContractRegistryService.cs` (`api_contracts`, `api_contract_versions`: raw `DocumentText` + parsed `SourceContract` JSON) |
| Contract readers | `Services/SourceAnalysis/Evidence/ContractAnalyzer.cs` — `ReadOpenApiDocument` (JSON OpenAPI 3 via `OpenApiExtractor` types + tree walker operations; YAML / Swagger 2 via the generic tree, Partial), `ReadGraphQl`, `ReadGraphQlIntrospection` → `GraphQlSchemaContract` |
| Tests | `Services/ContractTesting/ContractTestService.cs`, `ContractTestRules.cs` (`contract_tests`: one current JSON document, `Revision` counter, **no history kept**) |
| Execution | `Services/ExecutionEvidence/ExecutionRunService.cs` — refuses `UseTargetAuthentication` in `CallBlock`; capture by `EvidenceCaptureHandler`; assertions `ExecutionAssertions` (StatusCode, Header, JsonPath, ResponseTime, GraphQL error count/code/path/data path/has data) |
| Evidence | `ExecutionEvidenceStore` (`execution_runs`, `execution_calls` incl. `DefinitionRef`); Results UI `Pages/ExecutionResults.razor` |
| Attended API credential | Local HTTPS proxy → `TransientAuthenticatedApiContextStore` (memory only, per Target Environment profile, bound to the context fingerprint and approved **HTTPS** authorities; `ITransientCredentialSink.TryApply/TryLease`); used by `AuthenticatedApiExecutionService` (AQR, read-only) and `PerformanceApiCredential` (performance runs) |
| Unattended | `UnattendedLoginRunner` / `UnattendedLoginStore`: a **browser** login (Playwright) with secrets encrypted by ASP.NET Core Data Protection (DPAPI-protected keys) under `App_Data`. `RuntimeAuthenticationCatalog` states: "Unattended login produces a browser session, not API credentials." No OAuth2 client-credentials, API-key or Basic support exists for Target Environment APIs. |
| Safety | `IApiEnvironmentSafetyPolicy` (Local/Development/QA/Test; production refused; loopback allowed); `ContractSourcePolicy` for contract URLs |
| Test Data | `AuthoredTestDataStore` versions + `TestDataCatalog` approval; picker/binding in `ContractTestService` |
| Source Analysis contracts | `IReviewSourceEvidenceProvider` snapshots → `EvidenceDomains.Contracts.Contracts` (`SourceContract` per file, with provenance: snapshot, file, format) |
| AQR contracts | `ApiReviewEngine.ReviewOpenApiAsync` / `FetchSchemaArtifactAsync` fetch `ApiReviewTarget.ContractSource` (URL) through `ContractSourceFetcher`; `graphql_schema_artifacts` (SDL per environment + target) |
| JSON Schema | **No JSON Schema library is referenced.** `JsonSchema.Net 9.3.0` is in the local NuGet cache but its dependency `Json.More.Net 3.0.1` needs `System.Text.Json 10.0.5`, which is not cached: using it would need a nuget.org download (external access, not allowed). |

## 2. Gap reconciliation (original IDs kept)

| GAP | Existing behavior | Missing behavior | Root cause | Owner | Required tests | Acceptance criteria |
|---|---|---|---|---|---|---|
| GAP-CDT-1 | Secured REST operations Blocked; GraphQL anonymous; `ExecutionRunService.CallBlock` refuses target authentication | Authenticated runs (attended proxy session; unattended service credential), readiness per operation security, redaction, auth reference in evidence | No credential path into the run service; no API service credential concept | `ExecutionRunService` + new credential resolver reusing `ITransientCredentialSink` and the Data Protection pattern | attended applied/expired/out-of-scope/wrong profile; unattended client credentials (token fetched, cached, expired), API key; wrong scheme; HTTP to non-loopback refused; no secret in evidence | authenticated REST and GraphQL calls pass against a local synthetic API that checks the credential; evidence shows only `••••••` |
| GAP-CDT-2 | Status + top-level required properties only | Request/response validation against the schema graph (refs, nesting, arrays, composition, nullable 3.0/3.1, enums, bounds, readOnly/writeOnly), undocumented status, GraphQL document/variables/data validation, findings persisted | `SourceContract` keeps lossy type names; no validator | new validator over the stored raw document; run-time hook in `ExecutionRunService`; result stored in the call record | per keyword; direction; unsupported keyword reported; GraphQL | invalid responses fail with source paths; valid ones pass; unsupported keywords listed |
| GAP-CDT-3 | Bind / regenerate / delete only | Edit request, GraphQL document/variables, assertions, timeout, retry, review state; versions, history, compare, revert, duplicate, optimistic concurrency | single mutable row, no revision table | `ContractTestService` + revision table | save/conflict/immutability/duplicate/revert | edits create revisions; evidence keeps its revision |
| GAP-CDT-4 | Base path typed per run; GraphQL `/graphql` | Server-derived base path proposals (OpenAPI `servers`, Swagger 2 `basePath`), per-contract per-environment endpoint binding, GraphQL endpoint path | not modelled | registry + run | binding validation, server variables | runs use the bound path; evidence shows the endpoint |
| GAP-CDT-5 | One negative per operation | Multiple contract-derived negative proposals with explicit or "needs configuration" outcomes | generator scope | `ContractTestRules` | per variant | proposals generated on request, never executed automatically |
| GAP-CDT-6 | Any change to an input → Needs update | Definite / Potential / No evidence / Needs review; negative stays valid when its omitted value is still required; affected assertion/input named; reviewable update proposal | impact rules | `ContractTestRules.Impact` | unaffected negative, ambiguity | per-test explanation; proposal never applied automatically |
| GAP-CDT-7 | Registry fed by upload/URL; Test Data picker lists snapshot operations | Import a Source Analysis contract with snapshot provenance; re-import; cross-snapshot compare | no bridge | registry + `IReviewSourceEvidenceProvider` | dedupe, provenance | source-backed version with snapshot id, path, hash |
| GAP-CDT-8 | YAML / Swagger 2 Partial | Same detail as JSON 3.x (types with required, refs), servers, security, examples, content types; diagnostics | YAML/Swagger 2 bypass the extractor | `ContractAnalyzer` | fixtures: nested `$ref`, composition, multiple content types, YAML | YAML and Swagger 2 report Supported where read fully, otherwise named limitations |
| GAP-CDT-9 | `graphql_schema_artifacts` separate | AQR uses stored GraphQL contract versions | two stores | AQR engine | stored contract used | effective contract shown |
| GAP-CDT-10 | AQR fetches `SwaggerUrl` per run | AQR target may name a stored contract version | no reference | AQR engine + UI | stored REST contract used | same identity in report |
| GAP-CDT-11 | No nav entry, no deletion, no retention | Nav entry; archive/deprecate; hide versions; reference-aware delete | not built | registry + nav | references block deletion | archive default; referenced versions never deleted |
| GAP-CDT-12 | Leaf-only GraphQL selections | Editable documents (via GAP-CDT-3); generator keeps leaf selection + `__typename` | — | editor | — | tester can write any selection |

## 3. Decisions taken from the audit

- **Authentication**: two real sources only. *Attended*: the Local HTTPS proxy credential (the tester's own sign-in, delegated), HTTPS + approved
  authorities, exactly as AQR and performance runs use it. *Unattended*: a new **API service credential** per Target Environment (OAuth2 client
  credentials or an API key), stored with the same Data Protection pattern as the unattended browser login (separate key ring and file), tokens
  memory-only. A delegated (attended) token is never used for an unattended run and vice versa. Basic authentication: **not supported** (no policy
  for it exists). Service credentials are sent only over HTTPS, or over HTTP to a loopback target in a Local environment.
- **Schema validation**: in-house validator over the OpenAPI Schema Object as stored in the raw document (no new package; see § 1).
- **Results** stay in Execution Evidence; validation findings and assertion verdicts are part of the call record.
