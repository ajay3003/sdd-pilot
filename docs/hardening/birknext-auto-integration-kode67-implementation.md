# BirkNext — Automatic integration test generation + Kode 6/7: implementation (2026-11-09)

Branch `008-traceability-first`. Covers both briefs: *Automatic Integration Test Suite Generation* (ATG-01…ATG-12) and
*Automatic Integration Test Generation + Kode 6/7*. The ATG-named documents point here.

## Goal

> Du velger M2LB og trykker **Generate Suite**. BirkNext finner eksisterende kode 6/7-tester, kobler dem til integrasjonsflyten, fyller inn
> tilgjengelig konfigurasjon og testdata, og viser nøyaktig hva som gjenstår før faktisk testkjøring.

Every generated item is in exactly one of three groups (plus *not supported*):

| Triage | Meaning | Example |
|---|---|---|
| **Can run now** | generated, all prerequisites present, may be run after explicit authorisation | `GET /health` positive test on a loopback target |
| **Waiting for prerequisite** | generated, but a named prerequisite is missing | Kode 7 scenario needs an approved synthetic Kode 7 child (Test Data capability `classification-level-2`) in Person; Altinn needs an authorised configuration |
| **Needs domain decision** | cannot be decided from evidence | whether Kode 6/7 children count in aggregate Bufdata figures |

## Design: a thin orchestrator, no new engine

A suite is a **versioned manifest of references** to records owned elsewhere. It never copies a contract, a test body, a value or evidence.

| Concern | Existing owner reused | How the suite uses it |
|---|---|---|
| Contracts | Contract Registry (`ContractRegistryService.ImportFromSourceAsync`, idempotent) | `key@vN` per OpenAPI/GraphQL file in the scope's modules; a later snapshot continues the previous revision's contract as its next version |
| Tests | Contract Testing (`GenerateAsync`, `ProposeNegativesAsync`, `ListAsync`, `RunAsync`) | `testId@revision`, readiness and gaps from the contract-test readiness model; runs go through the existing run path and Execution Evidence |
| Test data | Test Data (`TestDataCatalog.Resolve`, `TestDataResourceService.CreateAsync`) | journey-pack requirements resolved by capability; synthetic key-value drafts (`gen-…`), **never approved automatically, never overwriting**; identity fields are never synthesised |
| XML | `XmlSchemaPackageAnalyzer` (Schema Studio engine) | per-file dependency closure, namespace and root elements; fixtures are generated in Schema Studio from the exact snapshot (deep link) |
| Flow | `IIntegrationJourneyPackRegistry` + `IntegrationJourneyRunner.Coverage` (Skolenærvær pack) | seven stages Altinn → MU → MM → Skole intake → Person/Tjeneste → period lock → Bufdata, each with executor/observer coverage |
| Topology | `IntegrationTopologyService.BuildAsync` | Confirmed / Candidate / Ambiguous / Unresolved mapping; candidates become review items |
| **Kode 6/7** | **M01 Security Classification** (`SecurityClassificationConsumerService.AssessAsync`, consumer `skolenaervaer`) | existing M01 requirements are reused as `M01:{key}` rows (state + review run id); twelve Skolenærvær variants `K67-01…K67-12` link to them — **no second Kode 6/7 module** |
| Review | `ManualReviewDecisionStore`, new module `integration-suites` | decisions keyed by suite key + item key |
| Execution evidence | `ExecutionEvidenceStore` | the suite's run links table; outcome read from stored calls, never inferred |

### New code

- `shared/IntegrationSuiteContracts.cs` — suite, scope, stage, triage, readiness, execution-outcome contracts.
- `Services/IntegrationSuites/IntegrationSuiteComposer.cs` — pure rules: scopes, module filter, identity-field detection, seeded synthesis, stage
  coverage/readiness, Kode 6/7 scenario derivation, status, fingerprint.
- `Services/IntegrationSuites/IntegrationSuiteService.cs` — generate (locked per suite key), list/get, review, authorised execute.
- `Controllers/IntegrationSuitesController.cs` — `GET scopes`, `GET`, `GET {id}`, `POST` (configuration-write policy), `POST {id}/reviews`,
  `POST {id}/runs` (active-execution policy).
- Tables `integration_suites` (unique `SuiteKey, Revision`) and `integration_suite_runs`; migration `20261109191405_AddIntegrationSuites`.
- Frontend `/integration-suites` (**Generate Integration Test Suite**, Quality & Testing): source + scope → suite with Readiness, Flow,
  Configuration, Test data, Tests, XML, Kode 6/7, Review, Execution tabs. Navigation, documentation catalog and user guide entries.

### Identity, idempotency, drift

- Suite key = scope + project (repository identity, else archive name) + option hash. **Not the snapshot**: a newer snapshot of the same project
  becomes the next revision of the same suite.
- Fingerprint over identity tuples (contract refs, test ids/revisions, data, stages, security, config, review); same fingerprint → the stored
  revision is returned (`Reused`), nothing is written. A concurrent duplicate is resolved by the unique index (the winner is returned).
- Drift between revisions: contract `vN → vN+1` (existing tests stay on vN — never silently rebound), tests added/removed, snapshot change,
  status change.

### Safety

- Generation is draft-only and makes **no network call**. Execution requires `Authorized = true` per request, only tests of the suite revision,
  only through Contract Testing (environment safety policy, credential resolution, mutating-call confirmation). Tests waiting for a prerequisite
  cannot be selected in the UI and are reported as *not run* by the backend.
- `Verified` only when every stage is executable **and** observable, every stage has a run test, and all its calls passed with assertions.
  Calls whose assertions cannot be fully assessed (e.g. source-backed contract without document text) stay **Not assessed**, never Passed.
- No real persons: generated values are synthetic (`synthetic-{field}-{n}`, ranges, enums, seeded); identity-shaped fields
  (`fødselsnummer`, `personnummer`, `fnr`, D-nummer, DUF, …) are refused and become prerequisites.

## Kode 6/7 scenarios

| Id | Scenario | Triage without further evidence |
|---|---|---|
| `M01:*` | existing M01 requirements of the Skolenærvær consumer (protected downstream handling, Bufdata exclusion, …) | from the M01 review state (Verified → can run, needs review → decision, otherwise waiting) |
| K67-01 / K67-02 | Kode 6 / Kode 7 child reported through Altinn (links E2E-05) | **needs domain decision** — intake rule (store, withhold, reject) not recorded; then waiting for an approved synthetic Kode 6 (`classification-level-3`) / Kode 7 (`classification-level-2`) child in Person and an authorised Altinn configuration |
| K67-03 | protected child at the MU/MM processing boundary | waiting (executor) / not supported without an MU/MM observer |
| K67-04 | protected child reaches Skoletjenesten intake with documented handling | **needs domain decision** (intake rule) |
| K67-05 | protected child excluded from prohibited downstream (Bufdata) records (links DATA-03) | **needs domain decision** — counted in aggregate figures or excluded entirely? Only identifier exclusion is recorded |
| K67-06 / K67-07 | unauthorized lookup denied / authorized read allowed | inherit the M01 runtime probe (test-lead approved context, tokens at run time) |
| K67-08 | BirkNext evidence and exports never expose protected identifiers (synthetic canaries) | can run now (BirkNext's own redaction); target-system logs **not assessed** |
| K67-09 / K67-12 | period lock and publication respect confidentiality / handling after retry and recovery | not supported (no executor or observer) |
| K67-10 / K67-11 | correction/replay; classification change between stages | **needs domain decision** (no approved rule) |

An open question always takes precedence: it must be answered whatever adapters exist. A recorded *Acceptable* review decision on the
scenario's review key turns it into a reviewed business rule and removes the question.

## Tests

- Backend `IntegrationSuiteTests` (14): scope/modules, contract import + tests, synthetic data (identity refused, no overwrite, seeded),
  XSD closure (broken import reported), topology classes, stages, Kode 6/7 reuse + triage + decision precedence, idempotent regeneration,
  new snapshot → revision 2 with contract drift, concurrent generation, review scoping, authorised execution with independent loopback ledger
  (NotAssessed is reported honestly; outcome `Executed`, not `Verified`).
- Frontend `IntegrationSuitesPageTests` (5) + navigation/documentation/guide tests.

## Defects fixed during implementation

| Id | Defect | Fix |
|---|---|---|
| ATG-DEF-1 | New snapshot of the same project imported as a second contract (`ward-api-2`) instead of `ward-api` v2, so no contract drift | continue the previous revision's contract key for the same file and type (fallback: separate contract if refused, e.g. archived) |
| ATG-DEF-2 | Run summary counted only passed/failed ("0 passed, 0 failed of 1") hiding NotAssessed | per-state summary ("1 notassessed of 1") |
| ATG-DEF-3 | Fingerprint included prose and tuples serialised as `{}` | identity tuples only, `IncludeFields = true` |
| ATG-DEF-4 | Neighbouring XSDs polluted a schema's closure | dependency closure per file |
