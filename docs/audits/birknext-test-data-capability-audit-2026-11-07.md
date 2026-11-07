# BirkNext Test Data — Capability Audit — 2026-11-07

Scope: the existing Test Data module before the "Add data" extension. Read-only audit of source on `008-traceability-first` at `eddb4a69`.
The implementation is described in `docs/hardening/birknext-universal-test-data-implementation-2026-11-07.md`.

## Existing architecture (before this change)

| Area | What exists | Evidence |
|---|---|---|
| Page | `/test-data`, four sections (`overview`, `assets`, `fixtures`, `requests`); deep links for consumer requests (`consumer`, `operation`, `variable`, `dataType`, `schema`, `create=xml` …) | `Pages/TestDataPage.razor` |
| Components | Tester readiness (`TestDataTesterReadiness`), readiness summary for AQR/IQR (`TestDataReadinessSummary`), XML fixture editor (`XmlFixturePanel`, ~1,100 lines), asset dialog (edit/usages/delete/replace), candidates, mapping, reset, Connected System bindings, session identities | `Components/TestData*.razor`, `XmlFixturePanel.razor` |
| API | `api/test-data`: catalog, asset detail, candidates, observed-value match, registrations, XML bind, canonical fixtures, approve (per version), rename, validate, schema associations, deprecate, consumers, requirements, matches, usages (bind), references, edit, restore default, delete, replacements, replace, reset | `Controllers/TestDataController.cs` |
| Catalog | One aggregator over providers (`ITestDataProvider`): configured, Altinn/Skolenærvær protected defaults, Schema Studio registrations, registered (candidate) assets. Alias uniqueness, approval by version fingerprint, deprecation, draft/current versions, immutable asset ids with alias history | `TestDataCatalog.cs`, `TestDataIdentityStore.cs` |
| Persistence | Workstation-local JSON under `App_Data/test-data` (`registry.json` — metadata, lifecycle, requirements, usages, protections, **no values**; `identities.json`; `canonical-fixtures.json`; schema associations; protected defaults). No EF tables | `TestDataRegistryStore.cs`, `CanonicalFixtureStore.cs` |
| Types | `TestDataKind` (8), `TestDataDataType` (5), `TestDataValueType` {None, String, Integer, Decimal, Boolean, Date, DateTime, Guid, Other} — no Int64/Double/Time/Duration/Null/Enum, no constraints | `shared/TestDataContracts.cs` |
| Validation | Scalars by value type; JSON **well-formedness only**; XML hardened parse; XSD validation on the server (shared XSD capability, trust levels). No JSON Schema / OpenAPI / GraphQL value validation | `TestDataValidator.cs`, `TestDataSchemaValidation.cs` |
| Approval | Explicit, per version fingerprint; XML must be server-validated (unless a negative test); a changed asset needs review | `TestDataCatalogService.ApproveAsync` |
| Lifecycle | Candidates from contracts/data model/operations/enums; readiness; impact; staleness via value versions | `TestDataLifecycleContracts.cs` |
| References | Alias or immutable id resolution; no `{{…}}` syntax in Test Data. Performance uses `{name}` placeholders | `TestDataReferenceResolver.cs`, `PerformanceScenarioRuntime.cs` |
| Generators | Performance only: Uuid / Timestamp / RandomNumber for technical parameters; XML minimum-valid generation. No seeds | `PerformanceTestContracts.cs`, `XmlFixturePanel.razor` |
| Secrets | Values hidden in `ToString`, default sensitivity Sensitive, display only for non-sensitive scalars, registry stores no values | `TestDataAsset.cs`, `TestDataCatalog.cs` |

## Consumers

| Consumer | Use of Test Data |
|---|---|
| API Quality Review | `TestDataReadinessSummary` (display) |
| Integration Quality Review | Same component (Message/Structured/File/ExpectedResult) |
| Performance Test Review | Smart Scenario Engine: needs from OpenAPI/GraphQL operations, `Match` against approved assets, `{var}` runtime mapping, selection policies |
| Critical E2E | Resolves values by alias/id at run time |
| Skolenærvær | Reporter/Child roles (distinct), canonical fixtures, consumer declarations |
| Connected Systems | Bindings by alias |
| Target Environments / Setup Library | XML fixture readiness for setup packages |
| Schema Studio | Registers saved XML as Test Data |

## Capability matrix

| Capability | Existing | Partial | Missing | Recommended change | Owner |
|---|---|---|---|---|---|
| Exact primitive types (Int32/Int64/Decimal/Double/Boolean/Null/Date/Time/DateTime/Duration/UUID/Enum) | | ✓ (8 value types, no constraints) | | Typed entries with invariant parsing; explicit Null | Test Data (shared rules) |
| Key-value collections | | | ✓ | Typed grid, unique keys | Test Data |
| JSON payload editing + validation | | ✓ (well-formed only) | | Editor with line/column errors, depth limit, typed preview; JSON Schema stays **Not assessed** | Test Data |
| XML + XSD | ✓ | | | Reuse the XML fixture editor (no second editor) | Test Data / Schema Studio |
| GraphQL variables | | | ✓ | Variables resource validated against discovered variable definitions and schema input/enum types | Test Data (contract evidence reused) |
| REST request templates | | | ✓ (Performance steps only) | Template with typed parameters, body, target authentication flag | Test Data |
| Datasets | | ✓ (configuration only) | | Authored datasets with references/inline values, cycle detection | Test Data |
| Generators with seeds | | ✓ (Performance) | | Deterministic generators (definition stored, values materialized) | Test Data (shared) |
| Reference expressions | | ✓ (alias/id) | | `{{scope.name}}` resolver with precedence, missing/cycle detection, masking | Test Data (shared) |
| Versioning / history | ✓ (catalog versions + approval) | | | Append-only versions for authored resources; optimistic concurrency | Test Data |
| Approval / deprecation | ✓ | | | Reuse unchanged | Test Data |
| Consumer binding | ✓ (usages) | | | Contract check of REST/GraphQL resources at bind time | Test Data |
| Operation readiness | | | ✓ | Readiness = contract checks + approval, per operation | Test Data |
| Add data UI | | | ✓ | Guided Kind → Configure → Validate & save | Test Data page |

## Findings during the audit and implementation

- `TestDataCatalog.CurrentVersion` returned an approved older version **without the provider `SourceId`**. Anything keyed on the source then lost the asset: delete through the owning editor, and provider-specific readiness. It was found while verifying an approved v1 that sits behind a v2 draft, and is fixed: the version keeps the provider identity.
- The XML fixture editor works inside a Test Pack contract context (e.g. Skolenærvær's XSD). XML for an arbitrary snapshot XSD is created in Schema Studio and then registered as Test Data. Add data → XML therefore routes to the existing editor, and no second XML editor was added.
- GraphQL operation documents in Source Analysis record variable **names only**, while schema root fields record typed arguments. GraphQL variable typing can therefore be asserted only for schema operations; otherwise the result is reported as "types not recorded" and is never claimed valid.
