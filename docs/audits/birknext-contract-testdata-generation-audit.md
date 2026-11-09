# BirkNext — Automatic contract test data generation: audit (2026-11-10)

Branch `008-traceability-first`. Baseline HEAD `d2746018` (Integration Suite Generator committed; nothing else of this work in progress).
Audit of the actual code before any change; only confirmed gaps were implemented.

## Baseline capability matrix

| Capability | Existing | Partial | Missing | Evidence (source) | Required fix | Acceptance test |
|---|---|---|---|---|---|---|
| Test definitions generated from a contract | ✔ | | | `ContractTestService.GenerateAsync`, `ContractTestRules.Generate/GenerateGraphQl` | — | `ContractTestingTests` |
| Negative proposals | ✔ | | | `ContractNegativeRules.Propose` (whole-body crash fixed in `0a909fef`) | — | `ContractTestingTests` |
| Typed materialization of values into requests (JSON types, nested, GraphQL variables) | ✔ | | | `ContractTestRules.Materialize` (whole-placeholder values keep their JSON type at any depth) | — | TS-06/07, `ContractTestDataTests` |
| Request/response validation against the stored contract | ✔ | | | `ContractCallValidator` + `OpenApiSchemaValidator` ($ref, allOf/anyOf/oneOf, enum/const, bounds, lengths, pattern, formats, additionalProperties) | — | — |
| **Test data generated for contract tests** | | | ✔ | Generate Tests created definitions only; the suite generator had a separate parameter-type synthesizer that ignored request bodies and copied contract *examples* | one generator in Contract Testing | `ContractTestDataTests`, TD-06 |
| **Generated data validated** | | | ✔ | nothing validated generated values | validate the materialized request with `ContractCallValidator` | TD-09, `…validates_against_the_schema` |
| Generated data persisted | | ✔ | | suite drafts in Test Data (unapproved, never bound) | key-value set per operation, provenance in the set | TD-09, TR-01 |
| **Data linked to tests** | | | ✔ | `BindAsync` requires an approved version; generated drafts were never approved or bound | approve + pin only when certain | TS-03, `Certain_data_is_validated…` |
| Positive vs negative distinguished | ✔ | | | test kinds; negatives share the positive's inputs | bind negatives too | `Certain_data…` |
| Unsupported fields identified | | ✔ | | suite listed refused identity names only | certainty per value with reason | TB-03 |
| Regeneration duplicates | | ✔ | | suite kept existing drafts | deterministic alias, never overwrite, reviewed values reused | TD-09, TR-01 |
| Execution uses the generated values | ✔ (once bound) | | | Execution Evidence `Resolved` values | — | TS-06/07 |
| **Source-imported contracts fully validatable** | | | ✔ | `ImportFromSourceAsync` stored `DocumentText = ""` → structural validation only, so M2LB data could never be schema-validated | retain contract document texts at snapshot save (like XSDs) and store them on import | TD-03 |
| XML/XSD fixtures | ✔ (frontend) | | | Schema Studio `GenerateMinimumValid`, `XmlFixtureAutoDraft`, server-side XSD validation at approval | not changed (see gap register) | — |
| Kode 6/7 data roles | | ✔ | | Test Data capabilities `classification-level-{2,3}`; suite only used them as booleans | separate Kode 6 / Kode 7 roles in suites | `Journey_stages…` |

## Confirmed defects / gaps (each with a red-first or acceptance-found test)

| Id | Finding | Evidence |
|---|---|---|
| TDG-1 | No test data for generated tests; testers filled every field in Test Data | Generate Tests produced unbound tests |
| TDG-2 | Second generator in the suite (parameter types only; examples copied) | `IntegrationSuiteComposer.Synthesize` |
| TDG-3 | Generated values never validated | no call to a validator |
| TDG-4 | Generated drafts never approved/bound, so never "ready" | `BindAsync` approval gate |
| TDG-5 | Source-imported contracts had no document text → structural validation only | `ContractRegistryService.ImportFromSourceAsync` |
| TDG-6 | OpenAPI JSON/YAML files of a snapshot are in `ConfigurationFiles`, not `EvidenceFiles` (found while implementing TDG-5) | `IqrSourceArchiveReader` |
| TDG-7 | A pattern like `^\d{11}$` produced an identity-shaped value (found on real M2LB Person; Test Data refused the set) | acceptance round 1 |
| TDG-8 | Credential-name rule of the generator narrower than Test Data's | `TestDataResourceRules.SensitiveName` |
| TDG-9 | "Not saved in Test Data" did not name the failing check | acceptance round 1 |
| TDG-10 | `GET credentials/{env}` answered 404 for "none configured", logging a browser console error on every Contract Testing visit | acceptance browser run |
