# BirkNext — How REST (OpenAPI) and GraphQL contracts are imported: audit (2026-11-08)

Read-only code audit of branch `008-traceability-first` at `da320a64`. Nothing was built, run or changed for it; no external system was
contacted; nothing under `testdata/` was read. Paths are relative to `AIAssisted/`. Claims marked **✔ verified** were re-checked
in the code by hand after the initial sweep; the rest come from the sweep with the file:line given.

Purpose: the baseline for Contract-Driven API & GraphQL Testing (import contract → operations → test proposals → Test Data → run →
Execution Evidence → contract change → impacted tests).

## 1. Import paths

| # | Path | Trigger | Formats | Parser | Stored |
|---|---|---|---|---|---|
| P1 | Source Analysis archive (incl. Project Import) | Source upload | OpenAPI 3.x / Swagger 2, JSON or YAML; GraphQL SDL + operation documents | `ContractAnalyzer` | Snapshot evidence domain *Contracts*, per snapshot |
| P2 | API Quality Review — REST contract URL | AQR run, from the Target Environment `SwaggerUrl` | Review: JSON 2.0/3.x. Extraction: JSON 3.x only | `OpenApiDocumentReview` + `OpenApiExtractor` | Run result only (summary, hash baseline) |
| P3 | AQR — GraphQL live introspection | AQR run | Introspection JSON | `GraphQlExtractor` | Run result only |
| P4 | GraphQL schema artifact upload | Tester, AQR panel (`GraphQlSchemaArtifactPanel.razor:53`, hosted `ApiQualityReview.razor:411`) | SDL `.graphql/.graphqls/.gql`, ≤ 10 MB | HotChocolate + `GraphQlSdlSchema.FromSdl` | Table `graphql_schema_artifacts`, one row per environment + target, replace-only |
| P5 | AQR — GraphQL schema URL fallback | AQR run | SDL or introspection JSON | as P3/P4 | Not stored |
| P6 | Integration Topology — Discover OpenAPI interfaces | Tester | JSON 3.x, ≤ 2 MB, same-origin relative path | `OpenApiExtractor` | Topology provenance |
| P7 | Altinn — deployed OpenAPI | Altinn setup (approved app base) | JSON 3.x | `AltinnDeployedOpenApiReader` → `OpenApiExtractor` | Altinn contract snapshot + fingerprint |
| P8 | Altinn — OpenAPI import | Tester upload (`AltinnTestSetupPanel.razor`) | JSON, request ≤ 3 MB; must contain `POST /{appId}/instances` | as P7 | as P7, source `ImportedOpenApi` |
| P9 | Generated Documentation — structure | Explorer, stateless | OpenAPI YAML/JSON; GraphQL SDL | `ContractAnalyzer.ReadOpenApi/ReadGraphQl` | Not stored |
| P10 | `ContractDiscoveryService` with `OpenApiSourceFetcher` / `GraphQlSourceFetcher` | **No caller** ✔ verified (registered `Program.cs:858`, never injected) | JSON OpenAPI; live introspection | fetchers | — |

There is **no general upload for an OpenAPI document** (only the Altinn-specific P8), and **no upload for GraphQL introspection JSON**
(P4 accepts SDL only). The Integrations contract store accepts JSON Schema and XSD only (`IntegrationsPane.razor:1173`).

## 2. Parsers and models (duplication)

| Format | Readers | Differences |
|---|---|---|
| OpenAPI | `OpenApiExtractor` (runtime: AQR, Topology, Altinn); `OpenApiDocumentReview` (AQR review); `ContractAnalyzer.OpenApiOperations` (Source Analysis, Generated Docs) | YAML and Swagger 2 operations only via P1/P9. Examples and security schemes only via P1. `OpenApiExtractor` rejects anything not `openapi: 3.*` ✔ verified (`OpenApiExtractor.cs:30-35`). |
| GraphQL | `GraphQlExtractor` (introspection, limits 1000 types / 500 fields / 100 args); `GraphQlSdlSchema.FromSdl`; `ContractAnalyzer` GraphQL (HotChocolate AST) | Three readers, three shapes. |
| Normalized models | `NormalizedContract` / `GraphQlNormalizedContract` (backend); `SourceContract` / `ContractOperation` (shared); `ContractSnapshot` nodes (Contract Evolution) | Adapters exist but `ContractSnapshots.FromOpenApi` is used only by a test; Contract Evolution in production handles XSD and JSON Schema only. |

## 3. Confirmed defects

| ID | Defect | Evidence | Consequence |
|---|---|---|---|
| CI-DEF-1 | `OpenApiExtractor` parses request and response schemas with an **empty schema map**, so every `$ref` schema becomes `null`; `ResolveReferences` is empty. | ✔ verified `OpenApiExtractor.cs:114,134` (`new Dictionary<string, NormalizedSchema>()`), `ParseSchema` returns `null` for unresolved refs (`:368-378`), `ResolveReferences` no-op (`:387-392`). | AQR `ContractValidation.ValidateAgainstSchema` (`ContractValidation.cs:16-22`) reads `ResponseSchemas`: when all success responses use `$ref` (the normal case for generated documents) the shape check is **silently skipped**; when some codes are inline and the success code is `$ref`, AQR reports a **false "HTTP 200 is not documented" (PotentiallyBreaking)**. Not yet reproduced by a test (no AQR test uses `$ref` responses; only fuzzing tests contain `$ref`). |
| CI-DEF-2 | Operation id falls back to the **HTTP verb alone** when `operationId` is missing. | ✔ verified `OpenApiExtractor.cs:96-98` (`method.ToUpper()`). | Two operations without `operationId` on different paths share the id `GET`; anything keyed by it (Topology `openapi:{operationId}`, operation matching) can collide. |
| CI-DEF-3 | Inconsistent remote-fetch safety. AQR fetches `ContractSource` with the public client, no per-URL check at the fetch site; the frontend allows `http`. The unused fetchers (P10) block private ranges by host *string prefix* (no DNS resolution; `fc`/`fd` prefixes also block ordinary host names; the `127.` block contradicts the loopback exemption). | ✔ verified AQR fetch `ApiReviewEngine.cs:375-385`; fetchers `OpenApiSourceFetcher.cs:39,93-106` (sweep). | Only the run-level environment safety policy protects the AQR fetch. Not exploitable from outside (local, authenticated tool), but there is no single contract-fetch policy to reuse. |

## 4. Persistence and identity

- **No contract registry** for REST or GraphQL. A contract is either inside a source snapshot (P1, versioned per snapshot with provenance:
  snapshot, file, line, format), re-fetched on every AQR run (P2/P3/P5, only a hash baseline kept), a single replace-only GraphQL SDL row
  per environment + target (P4, SHA-256, no history), or Altinn-specific (P7/P8).
- **Operation identity differs per feature**: `operationId`-or-verb (`OpenApiExtractor`), `"VERB /path"` (Source Analysis
  `ContractAnalyzer.cs:378`), `openapi:{operationId}` (Topology), `TestDataOperationRef(SnapshotId, ContractId, Operation)` matched on
  `ContractOperation.Name` (Test Data), and `OperationRef "contractId#operation"` in Execution Evidence, **never assigned** ✔ verified.
- **Test Data** links only to source-snapshot operations (P1), not to runtime or uploaded contracts.

## 5. Consumers

AQR contract validation and drift (P2–P4); GraphQL operation compatibility (runtime schema or artifact); safe fuzzing (re-fetches P2,
uses P4); documentation-exposure probe; Integration Topology (P6); Altinn testing (P7/P8); Test Data operation readiness and the resource
editor (P1); Generated Documentation drift (P1 vs P9, via `SourceEvidenceDiff.ContractChanges`); OpenAPI configuration evidence (P1).
Execution Evidence carries `ContractRefs` / `OperationRef` fields but nothing fills them yet.

## 6. Gaps for contract-driven testing (ranked)

1. **No contract registry** for REST/GraphQL with versions, fingerprint and provenance — the anchor every later step needs.
2. **No stable operation identity**; proposed: `METHOD /path-template` for REST (operationId as an alias), `query|mutation:field` for
   GraphQL root fields, both scoped by contract id + version fingerprint.
3. **Parser split + CI-DEF-1/2**: the runtime reader cannot read YAML or Swagger 2 and loses `$ref` schemas; only Source Analysis reads
   examples and security.
4. **No tester-facing import** of an OpenAPI document (JSON/YAML) or a GraphQL introspection result outside Altinn.
5. **No REST/GraphQL contract history or diff** in Contract Evolution (adapters unused), so "contract change → impacted tests" has no input.
6. **No single contract-fetch policy** (CI-DEF-3).
7. **Test Data ↔ operation link only for snapshot contracts**; Execution Evidence `OperationRef` unused.

## 7. Recommendation for the Contract-Driven vertical slice (REST first)

Reuse, do not add parallel stores: one contract registry built on the existing *Contracts* evidence model (`SourceContract` /
`ContractOperation`, provenance, version groups), fed by (a) Source Analysis snapshots as today and (b) a new tester upload of
OpenAPI JSON/YAML; one OpenAPI reader for the runtime path with `$ref` resolution (fixing CI-DEF-1/2 with regression tests, so AQR
benefits too); stable operation ids shared by Test Data `TestDataOperationRef` and Execution Evidence `OperationRef`; contract diff via the
existing `SourceEvidenceDiff.ContractChanges` to mark impacted tests. GraphQL follows the same registry with SDL + introspection JSON.

## 8. Not confirmed

- That nothing reaches `IContractDiscoveryService` through reflection or minimal APIs (only constructor/DI use was searched).
- Exact line numbers for Altinn P7/P8 and the Integrations contract store (from search offsets).
- Which UI page triggers Topology discovery; where the AQR drift baseline is persisted.
- CI-DEF-1's false "not documented" outcome is derived from the code path, not yet reproduced by a test.
