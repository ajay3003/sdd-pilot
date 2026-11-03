# Project Test Data ownership audit map — 2026-11-03

Phase 0 of "create once → identify once → persist once → reuse everywhere → each consumer judges suitability". Read-only audit of `AIAssisted/` at
`84234333`; key claims re-verified in code (✔). BE = `backend/BirkNext.Api/`, FE = `frontend/BirkNext.Web/`.

## Owners and storage

| Store | Holds | Notes |
|---|---|---|
| `App_Data/test-data/registry.json` (`TestDataRegistryStore`) | registered assets (metadata only), approval/deprecation records, requirements, explicit + declared-consumer bindings (by asset id), protection records | `ClearAll` always keeps protection records ✔ |
| `App_Data/test-data/canonical-fixtures.json` (`CanonicalFixtureStore`) | canonical XML fixture versions (full XML, append-only) | records SourceSnapshotId, SelectedContractPath, ContractFingerprint, RootElement, bindings, `alias@vN` value versions |
| `App_Data/test-data/protected-defaults.json` (`ProtectedTestDataDefaultsStore`) | edits of the built-in reporter/child (identifier, label, version) | identifiers in clear text |
| DB `SavedWorkspaceArtifacts` (SchemaStudioGeneratedXml) + browser | other generated XML fixtures (full bodies) | delete/reset done by the client |
| memory (`AltinnSessionStore`) | session identities | gone on restart |

Asset ids are `{provider}:{alias}` (e.g. `altinn-local:child:skolenaervaer-child-default`, `schema-studio:skolenaervaer-valid-report`, `registered:{alias}`):
stable across restart and edits, but **derived from the alias**, so a new asset reusing a deleted alias gets the same id.

## Concepts

| Concept | Owner | Persisted | Stable id | Versioned | Protected (how) | Consumers | Copied into consumer? | Referenced by | Validation | Env dependency | Reset | Gap |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Child (built-in) | `AltinnTestDataProvider`/`AltinnTestDataEditor`, seed `SkoleNaervaerTestIdentityDefaults` | seed in code + overlay file | alias-derived | `ValueVersion` counter (no history) | typed `BuiltIn` → `ProtectedDefault` | journeys, XML binder, Connected System, declared consumer | value written into generated XML | alias (journeys), id (bindings) | syntax only (`IsTestPerson`); Validation Unknown | none (one value per installation) | except-protected keeps edits; everything → product value (ccfa4936) | no per-environment values; clear-text overlay |
| Reporter (built-in) | same | same | alias-derived | `ValueVersion` | same | submission actor, Connected System | read live, not in XML | alias | profile "confirmed" hard-coded true | hard-coded `TT02` | same | no Dev/QA values |
| Session identities | `AltinnSessionStore` | memory | alias-derived | `ValueVersion` | no | same | same | alias | same | session expiry | lost on restart | references unresolved after restart |
| Valid-report XML (canonical) | `CanonicalFixtureStore` + `SchemaStudioTestDataProvider`, declared by `SkoleNaervaerCanonicalFixtures` | canonical-fixtures.json | `schema-studio:skolenaervaer-valid-report` | versions 1..n; approval by fingerprint | protection record matched by id (first resolved by alias + capability) | Altinn readiness, journey readiness, declared consumer | submission builds XML from the form; fixture gates readiness | id | XSD checked live per GET (not stored); `RecordedValidation` is **client-claimed** and used as approval fallback | `Facts.EnvironmentId` from client | kept / deleted per scope; protection record kept | approval trusts client; stays Approved/Ready when child changes |
| Other generated XML | Schema Studio workspace | DB + browser | `schema-studio:{alias}` (duplicates across scopes are disabled) | registration version | no | XML mappings, Performance/E2E payloads, AQR/IQR readiness | values copied into XML | alias | as above | `Facts.EnvironmentId` | client removes on reset | backend cannot see XML-mapping usages |
| Registered / source-candidate | `RegisteredTestDataProvider` | registry.json | `registered:{alias}` | metadata fingerprint | no | bindings, requirements | no value | id | contract drift → NeedsReview | `EnvironmentId` | removed by every reset | value-supplier path unclear |
| Scenario variables (Performance) | `PerformanceTestStore` | DB (alias only) | alias | definition versions | — | Performance | value revealed per run, not stored | **alias** | `Issues()` | definition env | untouched; becomes unresolved | alias reuse silently re-targets |
| Critical E2E flow data | `ICriticalE2EStore` | App_Data (alias only) | alias | — | — | Critical E2E | revealed per run | **alias** | `Issues()` | flow env | same | same |

"Used by" = live scan (Performance/E2E by alias) + stored links (Connected Systems by id+alias in DB, explicit/declared bindings by id in registry.json) +
browser-only XML mappings, merged in `TestDataLifecycleService.UsagesAsync`. Delete: protected/configuration → 409; in-use → refused unless
`leaveReferencesUnresolved`. List API `GET api/test-data` returns summaries (no XML bodies), but every call reloads all workspace XML from the DB and runs
XSD validation per asset, and can write registry.json.

## Prioritised gaps

1. ✔ **Duplicate aliases crash consumers**: `catalog.Assets().ToDictionary(a => a.Alias)` throws in `PerformanceScenarioSources.cs:97` and
   `CriticalE2ETestData.cs:23` when Schema Studio produces the same alias in two scopes; `Reveal(alias)` can return a disabled duplicate.
2. **Approval trusts a client-claimed validation** (`CanonicalFixtureStore.Add` accepts `facts.validationStatus`; `ApproveAsync` falls back to it when the
   schema cannot be evaluated).
3. **An approved fixture is not invalidated when its dependency (Child) changes**: staleness is a label; readiness stays Ready; the XML keeps the old identifier.
4. **Performance/E2E reference Test Data by alias only** (no asset id in usages): alias reuse silently re-targets; deleted-then-recreated aliases resolve.
5. ✔ **XML bind endpoint returns approved sensitive values inside caller-supplied XML** (`TestDataController.cs:45-50`, `TestDataXmlBinder.cs:36-41`),
   bypassing "Sensitive: never shown". Fixtures by design store the (synthetic) identifier, so this needs a product decision on where bound XML may live.
6. ✔ **Protection records survive "Reset everything"** (`TestDataRegistryStore.ClearAll` keeps `Protected`); a later XML reusing the canonical alias is
   protected automatically, even a negative test.
7. Clear-text identifiers and unsalted value fingerprints (`protected-defaults.json`, `ApprovedFingerprint`, 8-hex run hash in Altinn submission history).
8. Backend cannot see XML-mapping usages; generated-fixture removal on reset is client-reported.
9. No environment-specific values (reporter hard-coded TT02; child environment-less); built-in reporter "profile confirmed" is a constant.
10. Validation is never persisted; no "Stale because schema changed" state; list GET re-reads all workspace XML.

## Scoped plan

- **Correctness**: duplicate-alias-safe consumer resolution (enabled asset wins, no crash); approval only on server-side validation; an approved fixture
  whose mapped Test Data changed is not Ready for consumers (Stale — regeneration required), content never mutated.
- **Stable references**: Performance and Critical E2E record the asset id beside the alias when bound, resolve by id first (alias fallback for legacy
  definitions), usages carry the id.
- Reported (larger / product decisions): bind endpoint and fixture storage of identifiers, protection-record lifetime on full reset, per-environment
  values, persisted validation, client-side XML usages.
