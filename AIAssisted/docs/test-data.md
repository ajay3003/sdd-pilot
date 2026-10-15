# Test Data

Test Data is a general typed BirkNext capability.

> **Test Data** is any approved typed value, payload, file, entity reference, data set or expected result used to prepare, execute or
> verify a test.

It is not an identity system. Persons, such as a synthetic reporter, are one kind of asset among many: an *entity reference*.

## Concepts

| Concept | Meaning | Where |
|---|---|---|
| Asset | One typed piece of test data, identified by a safe alias | `shared/TestDataContracts.cs` (`TestDataAssetSummary`), backend `Services/TestData/TestDataAsset.cs` |
| Kind | Scalar, Structured, File, EntityReference, Message, ExpectedResult, ReferenceData, Other: the nature of the data, never its domain meaning | `TestDataKind` |
| Value type | For scalars: String, Integer, Decimal, Boolean, Date, DateTime, Guid | `TestDataValueType` |
| Media type | For payloads and files: `application/json`, `application/xml`, `text/csv`, … | `MediaType` |
| Sensitivity | Public, Internal, Sensitive. Only non-sensitive scalars may show their value; sensitive values never leave the backend | `TestDataSensitivity` |
| Approval | Approved, Unapproved, Disabled, Expired. Nothing is approved silently | `TestDataApproval` |
| Validation | Unknown, Validated, NeedsValidation, Invalid, Unavailable. Separate from approval | `TestDataValidation` |
| Environment | The environment an asset applies to, or none (any environment). Only applicability: Target Environment stays the owner of environments | `Environment` |
| Capabilities | Declared tags a consumer can require (`reporter`, `valid-report`, `bufdata-expected`, …) | `Capabilities` |
| Expected result | Expected data, including *expected absence* (`ExpectsAbsence`: nothing should exist) without a fake document | `TestDataKind.ExpectedResult` |
| Data set | Heterogeneous assets bound to consumer-defined roles (`Reporter → reporter-a`, `InputPayload → normal-report`) | `TestDataSetSummary` |
| Requirement | What a consumer needs for one role: kind, value/media/entity type, capabilities, environment, validation | `TestDataRequirement` |
| Constraint | A simple explicit rule between roles. `Distinct` means the roles' values must differ, compared in the backend | `TestDataConstraint` |
| Provider | A source of assets; it never creates or changes data elsewhere | `ITestDataProvider` |
| Catalog | All providers' assets and sets. An alias offered twice is disabled until it is unique | `TestDataCatalog` |
| Resolver | Selection, never creation | `TestDataResolver` |

### Resolution

Per role:
1. An explicit choice wins when it qualifies. It is never replaced by a default.
2. Otherwise, exactly one qualifying asset is chosen.
3. Or, among several, exactly one marked default is chosen.

Anything else is *Ambiguous* (the candidates' aliases; the tester chooses) or *Unresolved* (why). Nothing is generated: no identity, no
D-number, no business value.

When a consumer names data-set capabilities, one approved data set that satisfies every required role is used first. Otherwise roles are
resolved asset by asset.

### Validation is type-aware

| Data | Check |
|---|---|
| Integer, Decimal, Boolean, Date, DateTime, Guid | Parsed |
| JSON | Well-formedness |
| XML | Well-formedness, read with a hardened reader (DTDs prohibited, no resolver) |
| Entity reference | Validated only by its provider (for example a confirmed Altinn TT02 profile) |

Validation never means business validity. *Approved for test use* is not *valid*: an approved negative fixture (for example
`invalid-report-missing-required-field`) is deliberately invalid and stays usable.

Schema validation of fixtures is not done in the backend. Schema Studio's validator runs in the browser and is not reused here.

### Providers

- **Configured catalog** (`TestData` section): typed scalars, inline values, fixture files, expected results and data sets.
  - Files are read only from inside the test-data folder (`TestData:Root`, default `TestData` under the backend content root). An absolute path or `..` is refused, and files are limited to 1 MB.
  - A sensitive value belongs in local configuration (an environment variable), never a committed file.
- **Local approved configuration**: the existing `AltinnTesting:UnattendedIdPortenTest:Reporters` and `AltinnTesting:SyntheticChildren` (set by `scripts/configure-skolenaervaer-tt02-local.ps1`). They become sensitive entity references. No migration or re-entry is needed.

Credentials are not test data. The ID-porten client id, scopes, redirect URI, client secret and Enabled keep their owners (IQR and local
execution configuration).

### Page and API

- **Page:** `/test-data` (Quality & Testing → Test Data) lists assets, data sets and sources.
  - Filters: kind, environment, status.
  - Details on demand: sensitivity, validation, fingerprint for payloads, and a bounded preview for non-sensitive values only.
- **API:** `GET api/test-data` (safe summaries) and `GET api/test-data/assets/{id}` (detail and preview). No raw sensitive value, secret or file-system path is ever returned.

## Skolenærvær Testing: the first consumer

`SkoleNaervaerTestData` is Skolenærvær's profile. It declares requirements, not storage:
- **Reporter:** an approved EntityReference `person` with capability `reporter`, for TT02, validated (Altinn TT02 profile confirmed).
- **Child:** an approved EntityReference `person` with `synthetic`, `child` and `known-to-person` (or `unknown-to-person`), for TT02 or any environment.
- **Constraint:** Reporter ≠ Child (`Distinct`). A child that is any reporter's person is also never approved.

The run page's reporter and child preselection, readiness rows (source: *Test Data*), dry run and execution all resolve through the
generic resolver. Run history keeps aliases only.

The other distinctions still hold:
- Configured ≠ approved ≠ validated ≠ authenticated.
- Alias ≠ value.
- Expected ≠ observed.

The readiness action for a missing reporter or child is *Open Test Data*.

**Critical E2E Regression is not migrated or modified in this change.**

## Adding another consumer

A feature that wants test data provides only:
- its role requirements, constraints and capability names (a profile);
- any provider for data it owns, if needed.

It does not add another catalog or resolver.
