# BirkNext — Contract-Driven API & GraphQL Testing: baseline audit (2026-11-08)

Baseline for the Contract-Driven Testing task, taken from `docs/audits/birknext-rest-graphql-contract-import-audit-2026-11-08.md` (contract import
paths, parsers, defects) and the Execution Evidence and Test Data work of 2026-11-07. Paths are relative to `AIAssisted/`. Read-only; no external
system was contacted; nothing under `testdata/` was read.

## 1. What existed before this task

| Need of the vertical flow | State on `869665c4` |
|---|---|
| Import a REST contract as a tester | No general OpenAPI upload (only the Altinn-specific import). AQR re-fetched `SwaggerUrl` on every run and kept a hash. |
| Import a GraphQL contract | SDL upload into `graphql_schema_artifacts` (replace-only, one row per environment + target, AQR runtime fallback); no introspection upload. |
| Versioned contract history | Only inside Source Analysis snapshots. No registry. |
| Stable operation identity | Inconsistent: `operationId`-or-verb (runtime reader), `"VERB /path"` (Source Analysis), `openapi:{id}` (Topology); Execution Evidence `OperationRef` never set. |
| One OpenAPI reader with `$ref` | Three readers; the runtime reader lost every `$ref` schema (CI-DEF-1). |
| Contract URL fetch policy | None shared; AQR fetched any configured URL (CI-DEF-3). |
| Draft tests from a contract | None. |
| Test Data for an operation | Authored REST templates / GraphQL variables could name a Source Analysis snapshot operation; readiness per resource. No dataset picker for a test. |
| Execution with evidence | `ExecutionRunService` (safety policy, mutating confirmation, capture, redaction) — no consumer wrote runs yet (GAP-EE-1). |
| Contract change → impacted tests | `SourceEvidenceDiff.ContractChanges` (operations, types, fields) existed; no operation-detail diff and no test to map it to. |

## 2. Decisions

- **One registry, existing model.** Versions store the shared `SourceContract` (the Source Analysis contract model) parsed by the Source Analysis
  readers (`ContractAnalyzer`), not a new contract model. No parallel parser was written: the OpenAPI upload path and the snapshot path call the same
  `OpenApiDocument`; SDL and introspection share one GraphQL mapping (`GraphQlSchemaContract`).
- **Results stay in Execution Evidence.** Contract tests store definitions only; the latest result of a test is read from `execution_calls` by a
  definition reference (new indexed column), not copied.
- **Runs only through `ExecutionRunService`.** No new HTTP executor; safety policy, path rules, credential-header refusal, mutating confirmation and
  capture are unchanged.
- **Test Data owns the values.** Templates hold `{{dataset.name}}` placeholders; a test is bound to a dataset pinned as `alias@vN` (approved version
  only); values are resolved at run time and the pinned reference is recorded in the evidence.
- **REST first, then GraphQL on the same infrastructure**, with GraphQL semantics kept (typed variables, input objects, enums, nullability,
  query/mutation, GraphQL errors with HTTP 200 fail a positive test).

## 3. Pre-existing stores left as they are (not unified in this task)

- `graphql_schema_artifacts` (AQR runtime schema fallback per environment + target) is still separate from the registry. It serves AQR's
  compatibility check, not contract tests. Registered as GAP-CDT-9.
- AQR still fetches `SwaggerUrl` itself (now through the shared fetch policy), rather than reading a registry version. GAP-CDT-10.
