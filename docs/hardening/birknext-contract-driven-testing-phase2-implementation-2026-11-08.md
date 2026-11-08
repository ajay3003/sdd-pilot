# BirkNext — Contract-Driven Testing phase 2: implementation (2026-11-08)

Baseline HEAD `93e38abee495386baf9dfdc4fd9d39df85d9ac2a`, final source HEAD `398a8952` on `008-traceability-first` (this report is committed on top).
The audit is `docs/audits/birknext-contract-driven-testing-phase2-audit-2026-11-08.md`; acceptance and the gap register are the two sibling reports.
Paths are relative to `AIAssisted/`. Everything reuses the existing owners: contract registry, `ContractTestService`, Execution Evidence
(`ExecutionRunService`, `ExecutionEvidenceStore`), Test Data, Source Analysis snapshots and the AQR engine. No second run engine, contract store or evidence
store was introduced.

## Commits (staged as the brief's § 21)

| Stage | Commit | GAP IDs | Summary |
|---|---|---|---|
| 1 Authentication + endpoint binding | `363c3e9c` | GAP-CDT-1, GAP-CDT-4 | API service credential store, credential resolver, per-contract endpoint/auth binding, server proposals |
| 2 Validation | `c00085f3` | GAP-CDT-2 | OpenAPI schema validator, GraphQL request/response validator, contract check per call, Contract tab |
| 3 Editable versioned tests | `c64a0585` | GAP-CDT-3, GAP-CDT-12 | revision table, edit/duplicate/compare/revert, assertion editor, optimistic concurrency |
| 4 Negative generation | `d0fa364b` | GAP-CDT-5 | 12 negative variants proposed on request; contract-derived or "Needs configuration" expectations |
| 5 Source Analysis import | `3432cd2b` | GAP-CDT-7, GAP-CDT-8 | import a snapshot's contract with provenance; reader diagnostics; declared servers |
| 6 AQR unification | `55e0dd5a` | GAP-CDT-9, GAP-CDT-10 | AQR targets may name a stored contract version (REST and GraphQL) |
| 7 Diff/impact | `a7a95122` | GAP-CDT-6 | Definite / Potential / No evidence / Needs human review; reviewable migration proposal |
| 8 Coverage, navigation, retention | `174a9304` | GAP-CDT-11 | evidence-backed coverage, sidebar entry, lifecycle, hide, reference-aware delete, write policy |
| 9 Security hardening | `8b97fe40` | — | bounded token answers, GraphQL depth caps, malformed YAML refusal, endpoint authz tests |
| 10 Acceptance defect fix | `398a8952` | GAP-CDT-1 | missing-authentication negatives run inside an authenticated run without the credential |

## § 2 Authentication

- **Modes**: `ExecutionAuthenticationKind { None, AttendedSession, ClientCredentials, ApiKey }`.
  - *Attended*: the Local HTTPS proxy credential (`TransientAuthenticatedApiContextStore`, memory only). It is used only when the run's profile is the Target Environment, the context fingerprint is present and the host is within the approved HTTPS scope (`ExecutionCredentialResolver.Prepare`).
  - *Unattended*: `ApiServiceCredentialStore` keeps one credential per Target Environment.
    - Storage: `App_Data/api-service-credentials/api-service-credentials.json` holds the configuration only. The secret is in `secret-{hash}.bin`, protected by ASP.NET Core Data Protection with purpose `BirkNext.ApiServiceCredential.Secret.v1` and its own key ring.
    - The secret is write-only (`ApiServiceCredentialSave.ToString()` is redacted). Tokens are cached in memory until 30 s before expiry; `Forget(env)` runs on change.
  - Attended and unattended credentials are never substituted for each other.
  - **Basic: NOT SUPPORTED.** No policy for it exists.
- **Transport policy**: a service credential is sent only over HTTPS, or over HTTP to a loopback target in a Local environment.
  - Token endpoints are validated with `ContractSourcePolicy` when saved and again before each use.
  - Reserved headers (`Authorization`, `Cookie`, …) cannot be used as API-key headers.
- **Missing credentials**: readiness is Blocked with the reason, and the run is refused. If a credential fails per attempt, the attempt is recorded as `NotSent` with "Not sent: …", and the call is Blocked. Nothing is sent without the credential the binding requires.
- **Missing-authentication negatives** (`398a8952`): `ExecutionCallDefinition.WithoutAuthentication` sends that call without the run's credential. A call can only drop the credential, never add one. Before this fix, "Select ready" picked both kinds of test and the run was refused for mixing authentication; the browser acceptance found this.
- **Evidence**:
  - `ExecutionRunSummary.AuthenticationMode` (e.g. "Unattended: OAuth2 client credentials") and `AuthenticationReference` (`api-credential:{env}:{kind}:v{n}`).
  - The applied credential headers are masked by `EvidenceCaptureHandler` before persistence.
- **Write permission**: `PUT/DELETE credentials` require `IntegrationConfigurationWritePolicy`. That is a permission claim, or `Authorization:LocalConfigurationWrites=true` in Development from loopback.

## § 3 Configurable endpoints

- `ContractAnalyzer.DeclaredServers` records OpenAPI `servers` (with variable defaults applied) and Swagger 2 `scheme://host/basePath` as **metadata**.
- `ContractTestService.ServerProposals` offers only the path part of each server.
- Table `contract_endpoint_bindings` (contract × Target Environment) holds the base path, the GraphQL endpoint path and the authentication kind. `PathProblem` refuses hosts, schemes, `..` and query strings. The Target Environment origin stays authoritative.
- GraphQL has no hard-coded `/graphql`. It is only the default when no binding exists, and the acceptance used `/api/gql-keyed`.

## § 4 Contract validation

- `OpenApiSchemaValidator` works over the stored raw document (JSON or YAML; numbers via `JsonNode`).
  - **Evaluated**: local `$ref` (cycle-guarded, depth 64); `type` (3.0 string; 3.1 string or array incl. `"null"`); 3.0 `nullable`; `enum`; `const`; `required`; `properties`; `additionalProperties` (boolean or schema); `min/maxProperties`; `items`; `min/maxItems`; `uniqueItems`; `min/maxLength`; `pattern` (bounded time); `minimum`/`maximum`/`exclusiveMinimum`/`exclusiveMaximum` (3.0 boolean, 3.1 number); `multipleOf`; `allOf`/`anyOf`/`oneOf`/`not`; `readOnly` (refused in requests) and `writeOnly` (refused in responses); `format` for date-time, date, uuid, email, int32 and int64.
  - **Reported as unsupported** (met, not evaluated): `patternProperties`, `propertyNames`, `if`/`then`/`else`, `dependentRequired`, `dependentSchemas`, `unevaluatedProperties`, `unevaluatedItems`, `contains`/`minContains`/`maxContains`, `prefixItems`, `$dynamicRef`, `$recursiveRef`, `discriminator`, external `$ref`s and other formats. A response that is valid under the evaluated keywords but meets an unsupported one is **not** a schema pass (the `ContractSchema` assertion is NotAssessed).
  - Supported JSON Schema dialects: OpenAPI 3.0 Schema Object and OpenAPI 3.1 (JSON Schema 2020-12 subset as listed). Swagger 2 schemas are validated from the document.
- `GraphQlContractValidator` checks the request (one operation, root field exists, arguments, required variables, variable types, unknown input fields) and the response (envelope, selected fields, nullability, enum values, list shape; error paths excuse nulls). Nesting is capped at 32/64 and reported as not evaluated.
- `ContractCallValidator` (`IExecutionContractValidator`):
  - The request is validated before sending. Under `Enforce`, an invalid request is Blocked and never sent.
  - The response report is stored in `ExecutionCallRecord.ContractValidation` with tier, instance path, schema path and keyword.
  - Source-backed versions without document text get structural validation only, and name what was not evaluated.
- New assertion kinds: `ContractSchema`, `BodyText`, `XPath`. Generated positives use `Enforce` plus a contract assertion; negatives use `Record`.

## § 5–6 Editable, versioned tests and assertions

- Table `contract_test_revisions`: every save is an immutable revision with a change note and review state (Draft / Reviewed / Retired).
- Saves use optimistic concurrency (a stale `ExpectedRevision` is a conflict and never overwrites). Tests can be duplicated (revision 1, Draft), compared between revisions and reverted (a revert adds a revision).
- Execution Evidence records `DefinitionVersion = contract-test:{id}@r{n}`, so history keeps the exact revision a run used.
- `TemplateProblems` reuses `ExecutionRunService.CallBlock` and refuses credential-looking headers and values.
- UI: `Components/ContractTestEditor.razor`.

## § 7 Negative generation

- `ContractNegativeRules.Propose` offers the variants that apply to the operation: InvalidType, InvalidEnum, NullNotAllowed, EmptyValue, BoundaryViolation, InvalidContentType, InvalidShape, MissingAuthentication, UnsupportedMethod, UnknownInputField, InvalidListShape and InvalidSelection.
- Expected outcomes come only from documented statuses (e.g. 400/422/401/405). Otherwise the test is marked "Needs configuration", has no assertions and is never Ready.
- Proposals are generated only on request and are never run automatically.

## § 8 Source Analysis → contract store

- `ContractRegistryService.SourceCandidatesAsync` and `ImportFromSourceAsync` use `IReviewSourceEvidenceProvider` to list the snapshots' OpenAPI and GraphQL schema contracts and import one without re-upload.
- The new version records `SourceSnapshotId`, `SourcePath`, the archive, the commit and the evidence hash. The snapshot itself is never changed, identical evidence is not stored again, and a newer snapshot becomes the next version.

## § 9 AQR unification

- `ApiReviewTarget.ContractRef` plus `IStoredContractSource`: AQR reviews a stored REST or GraphQL version instead of fetching a URL. The post-run source label reads "Stored contract …".
- UI: `ApiReviewStoredContractPicker` sits under "Technical setup & diagnostics → Stored contract versions".
- `graphql_schema_artifacts` is kept as the runtime fallback (not migrated).

## § 10 OpenAPI / Swagger support

- JSON OpenAPI 3.x is read in full.
- YAML and Swagger 2 are still read structurally by the generic tree reader (ParseSupport Partial, with a named limitation). The **validator** reads their raw document, so validation does not depend on that limitation.
- Diagnostics:
  - Malformed JSON reports its line and position.
  - YAML anchors/aliases and tags are named as limitations.
  - An unclosed flow collection with nothing readable is refused with its line.
  - Deep JSON (> 128) and deep YAML (> 128) are refused.

## § 11 Diff / impact

- `ContractTestImpactLevel { NoEvidence, NeedsReview, Potential, Definite }`. Each reason is prefixed with its class.
  - *Definite*: the operation was removed, or a new required input is not sent.
  - *Potential*: a removed or changed field that is required or asserted.
  - *No evidence*: an optional, unasserted field changed, or a negative stays invalid on purpose.
  - *Needs human review*: ambiguous cases.
- New change classes: SecurityChanged, Deprecated, ServersChanged.
- `MigrationAsync` proposes an updated test bound to the new version. `ApplyMigrationAsync` saves it only on request, as a new revision; earlier revisions and their executions stay bound to the old version.
- Readiness: Definite with a removed operation → Blocked; Definite → Missing; Potential and Needs review → Warning.

## § 12 Coverage

`ContractTestService.CoverageAsync(key, version)` → `ContractCoverageReport`. Each metric has a numerator, a denominator, a scope (naming `key@vN`), a not-assessed count and an evidence source:

| Metric | Numerator / denominator | Evidence |
|---|---|---|
| operations-with-tests | operations with a stored test bound to the version / operations | stored tests (says "not evidence that the operation works") |
| operations-executed | operations with a call that got a response / operations | Execution Evidence calls whose `operationRef` names `key@vN#` |
| operations-passing | operations whose latest results all passed / operations | same |
| operations-with-negative-tests | operations with a negative test / operations | stored tests |
| documented-responses-observed (REST) | explicit documented codes observed / explicit codes | same; ranges and `default` excluded |
| responses-valid | latest responses Valid with no unsupported keyword / latest executed calls | same |
| assertions-passed | passed / assertions in latest executed calls | same |

Evidence of one version never counts for another. Subscriptions are excluded and the scope says so.

## § 13 Execution Evidence

- Each call records: `OperationRef` (`key@vN#METHOD /path`), `DefinitionVersion`, `TestDataRefs` (`alias@vN`), `ContractCheck`, `ContractValidation` and `WithoutAuthentication`.
- Each run records: `ContractRefs`, `AuthenticationMode` and `AuthenticationReference`.
- Execution Results shows Contract and authentication facts, and the export carries the same redacted records.

## § 14 Navigation / UX

- Sidebar entry "Contract Testing" under Quality & Testing (`api-quality-review/contracts`, exact match, aliases "Contract-Driven Testing" and "Contracts"). The AQR link and the AQR "Contract testing" button are kept.
- User Guide topic `contract-testing` and documentation-catalog entry.
- Views: Operations, Tests, Changes, Coverage, Lifecycle & retention. There is one Tab stop in the tablist.

## § 15 Deletion and retention

- `api_contracts.Lifecycle` (Active / Deprecated / Archived):
  - **Archived** is read-only: import, generation and runs are refused, and tests are Blocked with "The contract is archived".
  - **Deprecated** adds a readiness warning.
- `api_contract_versions.Hidden`: hidden versions leave the version picker. The latest version cannot be hidden.
- `RetentionAsync` computes each version's references: latest version, test revisions bound to it, execution records naming it, source snapshot. A version is deletable only with **no** references. A contract is deletable only when no test, evidence or snapshot references it; otherwise the answer is "archive it instead".
- Deletion requires typing the contract key, checked in the UI and again in the API.
- Lifecycle, hide and delete endpoints require `IntegrationConfigurationWritePolicy`. The policy text is shown on the Lifecycle view.
- Migration: `20261108103656_AddContractLifecycleRetention`.

## § 16 Security hardening

- Token endpoint answers are read with a 64 KB bound and JSON depth 16. An `access_token` that is not a header-safe bearer string (≤ 16 KB, `!`–`~` only) is refused. Redirects are not followed: the clients use `AllowAutoRedirect=false` and a 3xx is a refusal.
- GraphQL selection and response nesting are capped and reported as *not evaluated*, never as a violation or a stack exhaustion.
- Contracts: 5 MB size cap, JSON depth 128, YAML depth 128, and malformed YAML with nothing readable is refused.
- The URL importer refuses `file:`, `gopher:`, credentials in the URL, non-loopback plain HTTP and metadata addresses (`ContractSourcePolicy`). Tests assert these are never fetched.
- `ContractTestingEndpointSecurityTests` covers:
  - destructive actions and credential writes require the policy;
  - GET endpoints don't require write permission and nothing is anonymous;
  - uploads are size-limited;
  - the only executing endpoint (`POST runs`) takes stored test ids, never a URL, method, headers or body.

## Data model changes (EF, `AppDbContext`)

| Migration | Change |
|---|---|
| `…_AddContractEndpointBindings` | `contract_endpoint_bindings` |
| `…_AddContractTestRevisions` | `contract_test_revisions` |
| `…092818_AddContractVersionSourceProvenance` | `api_contract_versions.SourceSnapshotId`, `SourcePath`, … |
| `20261108103656_AddContractLifecycleRetention` | `api_contracts.Lifecycle` (default `Active`), `api_contract_versions.Hidden` (default `false`) |

All migrations only add tables or columns with defaults. Existing rows keep their meaning.

## Limitations (also in the gap register)

- An attended run in the browser was not exercised end to end. It needs the Local HTTPS proxy, an HTTPS target and a real sign-in. It is covered by backend tests (scope, profile, fingerprint, expiry, no substitution).
- YAML / Swagger 2 structural reading stays Partial (validation reads the raw document).
- Source-backed versions without document text get structural validation only.
- The AQR stored-contract picker was verified rendering with no discovered targets. Choosing a stored version for a target is verified by backend and bUnit tests, not in the browser.
- `graphql_schema_artifacts` remains a separate runtime fallback.
