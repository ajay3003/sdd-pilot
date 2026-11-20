# Schema Studio — source-aware XSD discovery and default selection (2026-11-18)

Branch `008-traceability-first`, starting HEAD `fc0a40b9`. Local commits only; nothing pushed. Isolated Release instance (API :5097, web
:5197), disposable database, synthetic archive; no external system contacted, no imported code executed. `appsettings.Local.json`,
`AIAssisted/testdata/` (read only, to learn the archive pattern) and the Kode67 gap register were not modified.

## 1. The real source (Tiltak 2 / Meldingsutveksler and M2LB)

| Archive | How the XSD is chosen | Evidence |
|---|---|---|
| Tiltak 2 / Meldingsutveksler | **By namespace from a registry**: `bufdir.xsd.oversikt.json` lists the namespaces (with `gyldigFra`); `XsdUtils.InitAsync` loads every XSD and matches `Nmsp` to `TargetNamespace`; `XmlValidatorClient` picks `Xsds.FirstOrDefault(x => x.Nmsp == nameSpace)` and builds an `XmlSchemaSet` | `SkjemaVerktoy/Xml/XmlSchemaRegister.cs:13`, `Xml/XsdUtils.cs:22-41`, `Service/src/Skjemaverktoy/XmlValidatorClient.cs:25-40` |
| — tests | Namespace literal `…skolenaervaer.v2.0.0.flat` | `FagKlient.Tests/Enhet/DeadLetterHandlerTests.cs:63`, `Service.Tests/.../InboundMessageHandlerTests.cs:123` |
| — packaging | All 27 XSDs under `TestData/xsds/v3/xsd/`, `<None Include="TestData\**">` (bulk) | `Buf.Meldingsutveksler.TestData.csproj:10` |
| M2LB (MM, English receiver) | **By file name**: `[SchemaV2Flat] = "bufdir.barnevern.tiltak.skolenaervaer.v2.0.1.flat.m2lb.xsd"` compiled through `XmlSchemaSet`; embedded `Skjema\*.xsd`; namespace `…v2.0.1.flat` in `appsettings.json` | `SkolenaervaerMessageType.cs:19-49`, `.csproj:25`, `Host/appsettings.json:19` |

So for Tiltak 2 the registry lists v0.8.2.flat, v2.0.0.flat, v2.0.0 (and Altinn variants); the version used per message is decided by the
message's namespace at runtime. Static evidence alone says which versions are **registered for validation**; tests name **v2.0.0.flat**.
Neither shows which version a deployed MM applied (the Skolenærvær contract profile says so explicitly: "Which MM is deployed is not known").

## 2. Schema Studio before

* List: `GET api/source-analysis/{snapshotId}/schemas` — every `*.xsd` of the snapshot, stored in ordinal path order; each file had only
  path, text and fingerprint (no namespace, version or usage).
* Default: `_sourceContractPath = artifact.Files.FirstOrDefault(…)` — **the first path**. For the Tiltak 2 archive:
  `bufdir.barnevern.henvisning.familierad.v2.0.0.xsd` (a referral schema). The dropdown was sorted by file name, so the pre-selected item
  was not even the first one shown.
* The backend already recorded `SourceContract.CodeReferences` (literal file names: Validator, Code, Configuration, Test, Embedded resource)
  — unused by Schema Studio — and missed namespace-based selection entirely.

## 3. Changes

**Analyzer (`ContractAnalyzer`, version 2 → 3)** — two grounded reference kinds beside literal file names:

* An XSD's exact target namespace in source, configuration or test text → the existing kinds (a file that also loads XML schemas is a
  `Validator`). Whole-namespace match only: `…v2.0.0` never matches inside `…v2.0.0.flat`.
* A JSON registry listing two or more of the archive's XSDs → `Schema registry entry`; when non-test code names that registry file and
  non-test code loads XML schemas → `Validator (schema registry)` on every registered XSD, citing both files ("registered for validation,
  chosen per message by namespace" — not proof a given message used it).
* Consumers updated through `SourceContractReferenceKinds.IsValidator`: contract usage (`ValidationSchema`), contract-pair matching
  (registry = Strong, literal validator = ExplicitUsage), integration contract discovery (accurate wording/rank). Integration discovery
  still accepts version-2 snapshots (no forced re-analysis).

**Schema endpoint** — each file carries `Usage` computed from the snapshot's contract evidence when read (nothing stored twice): target
namespace, version, tier (`ReferencedByValidationCode` / `ReferencedBySource` / `Declared`), basis, references, and
`RuntimeVerified = false` with "Not verified at runtime: no test run in BirkNext records which schema was applied to a message." A snapshot
analysed before version 3 says it needs a re-import to show namespace/registry references.

**Schema Studio**

* `SchemaSourceSelection` ranks schemas (validator naming the file/namespace > validator via registry > configuration > code > tests >
  packaged > registry entry > nothing); equal evidence by family then newest version.
* Pre-selection only for the **single** best-evidenced schema; a tie ("N schemas have equally strong source evidence … none is
  pre-selected") or no evidence ("no XSD is named by validation code, configuration or tests") leaves the choice to the tester. A deep link
  (`?snapshot=&contract=`) and a manual choice always win.
* Message under the list: which schema, version, why, which other versions are also referenced by validation code, and the runtime statement.
* Dropdown grouped by tier ("Referenced by validation code", "Referenced in source", "Declared in source only"), each option with version and
  variant (flat / standard / altinn); the filter also matches target namespaces.
* For the chosen schema, three separate answers: **Declared in source** (archive, namespace) · **Referenced by validation code** (yes/no with
  basis) · **Verified in a runtime test** (never claimed: no run record names the schema) — plus the source references (file:line, kind).

## 4. Similar first-item defaults elsewhere (audited, not changed)

| Page | Default | Note |
|---|---|---|
| Integration Quality Review | first readiness subject without `?integration=` | candidate for the same treatment |
| Integration Test Suites | first scope (`_scopes[0]`); snapshot is context-aware | scope candidate |
| Data Model Explorer | first visible entity | browsing default, low impact |
| Plan & prepare snapshot | context-aware (`ManualTestingWorkspace.ResolveSnapshot`) | correct |
| Contract Testing | no auto-select; diff "from" = previous version | correct |
| API Quality Review | context-aware (`?target=` / active target) | correct |
| Test Data | query-driven | correct |

## 5. Tests

On final code:

| Run | Passed | Failed | Skipped |
|---|---|---|---|
| Backend: contract evidence, analyzer, integration contract discovery, source change impact, schema endpoint (incl. new `SourceAnalysis_RecordsNamespaceAndSchemaRegistryReferences`) | 120 | 0 | 0 |
| Backend: new `SourceSchemaUsageTests` (tiers, packaging = declared, stale snapshot note) | 7 | 0 | 0 |
| Frontend: Schema Studio page + `SchemaSourceSelectionTests` (ranking, ties, no evidence, family/variant, 2 000 schemas < 2 s, page pre-selection, three answers, manual choice) | 134 | 0 | 0 |
| API + Web Release publish (tag 54) | exit 0 | — | — |
| Browser acceptance (§6) | 16 | 0 | 0 |

**Not run:** the full frontend suite and backend partitions 1–6 on this final code. The run was started and stopped by Claude Code because
the machine was critically low on memory (not a test failure); it is to be rerun before relying on full-suite results. The previous full
runs on the preceding commit were frontend 9275/0 and backend 6976/0.

Changed existing test: `SourceAnalysisSnapshotOpensNestedIncludedSchemaWithImmutableProvenance` relied on the first file being pre-selected
(the defect); it now chooses the schema explicitly and asserts that none is pre-selected without evidence. `SourceAnalysis_RecordsValidator…`
asserts the analyzer version is at least the one discovery needs (the analyzer moved to version 3).

## 6. Browser acceptance

Fresh Release publish (tag 54), disposable database `birknext_xsd2_20261118`, API :5097 / web :5197, network guard. Synthetic archive
`HubSynth.zip` reproducing the Tiltak 2 pattern generically: a referral XSD that sorts first, three versions of a report schema (v0.8.2.flat,
v2.0.0.flat, v2.0.0) listed in `example.xsd.registry.json`, a constant naming the registry, an `XmlSchemaSet` validator, a test naming the
v2.0.0.flat namespace, bulk `<None Include="TestData\**">` packaging. Script `acc/xsd-accept.cjs`.

**Result: 16 of 16 checks passed** — import; snapshot offered; `example.report.v2.0.0.flat.xsd` pre-selected (not the first path); the
explanation names the registry validator, version 2.0.0 and "Not verified at runtime"; other registered versions named, not chosen; groups
"Referenced by validation code: 3 · Declared in source only: 1" (the bulk-packaged referral schema is declared, not referenced); labels
"v2.0.0 · flat"; three answers; references cite validator, registry and test; a manual choice of the declared-only schema is respected and
explained; a deep link opens v0.8.2.flat; fits at 1440/1024/768 px; no console errors (404 lines ignored, as in the other audits); no
prohibited network destination.

A first run failed one check: the bulk-packaged referral schema was grouped as "Referenced in source". Packaging (and registry listing) now
counts as *declared*, only naming references raise the tier — fixed and pinned (`SourceSchemaUsageTests`).

## 7. Limitations

* Runtime verification of a schema is not available: Skolenærvær run records show MM's outcome (`Aarsak`) but not the version applied.
* Namespaces built at runtime from parts, or registries in non-JSON formats, are not recognised (reported as declared only, not guessed).
* *Resolved 2026-11-20:* an XSD bound to an integration of the same snapshot (an integration's source-selected contract, or the message
  flow's `SourceContractId`) is the strongest ranking signal: one bound schema is pre-selected with who uses it; several bound schemas are a
  tie the tester decides; a binding that names no file of the snapshot leaves the static ranking in charge (`SchemaSourceSelection.BoundSchema`).
* Existing snapshots need a re-import for namespace/registry references (the page says so).

## 8. Local commits

One local commit on `008-traceability-first` (this document included); nothing pushed. Starting HEAD `fc0a40b9`.
