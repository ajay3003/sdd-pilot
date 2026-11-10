# BirkNext — Contract Testing final integration & identity hardening: audit (2026-11-10)

Branch `008-traceability-first`. **Baseline `ae1db8e5`** (Contract Testing product hardening + reports). Working tree at baseline: only the user's
`appsettings.Local.json` and other sessions' untracked audit files (not touched). No overlapping uncommitted Contract Testing work.

## Identity map (baseline, before any change)

| Concept | How BirkNext identified it | Where |
|---|---|---|
| Project | **No contract-level project.** Source Analysis has a repository identity per snapshot: the single root `.sln`, else the archive file name (`ReviewSourceEvidenceProvider.Identity`), used by Dependency Review and Security Classification scopes; a person can declare two identities "the same repository" (`source_repository_associations`, never inferred) | `ReviewSourceEvidenceProvider`, `SourceRepositoryAssociationService` |
| Imported archive | `ProjectImportProvenance.ImportId` + archive SHA-256; a byte-identical archive reuses its snapshot | Project Import |
| Source snapshot | `iqr_source_snapshots.Id` | `IqrSourceStore` |
| Source contract | `SourceContract.Id` = `"openapi:" / "graphql:" + path` inside the snapshot | Source Analysis contracts domain |
| Logical API contract | `api_contracts.Key` (slug of the name, unique) — **no project** | `ContractRegistryService` |
| Contract version | `api_contract_versions (ContractId, Version)`; provenance `SourceSnapshotId`, `SourcePath` | registry |
| Document hash | `Sha256` of the version (fingerprint) | registry |
| Operation | `"METHOD /path"` / `"query x"` within a version; `OperationRef = key@vN#op` | `ContractIdentity` |
| Generated test / revision | `contract_tests.Id` + immutable `contract_test_revisions` | `ContractTestService` |
| Test Data version | `alias@vN` pinned in the test binding | Test Data |
| Execution run / evidence | `execution_runs/calls`; calls reference `OperationRef` and `DefinitionVersion` **as strings** | Execution Evidence |
| Integration Suite reference | `SuiteContractRef (SourceContractId, SourceFile, ContractKey, ContractVersion)` | Integration Suites |

There are **no foreign keys** between the contract tables (plain `ContractId` GUIDs) and Execution Evidence refers to contracts by `key@vN#op`
strings. A contract key never changes, so adding project scope cannot change the meaning of historical evidence.

## Confirmed gaps

| Id | Gap (brief) | Baseline behaviour (code) | Root cause | Status after audit |
|---|---|---|---|---|
| CTH-F01 | Project-safe identity | `ContinuedContractAsync` matched **any** contract with a version whose `SourcePath` equals the source contract id, across all snapshots: the same path in two projects continued one contract | continuation keyed on path only; contracts had no owner | CONFIRMED |
| CTH-F01b | Cross-project explicit target | `ImportFromSourceAsync(…, ContractKey)` accepted any existing contract of the same protocol | no ownership check | CONFIRMED |
| CTH-F01c | Discovery across projects | candidates de-duplicated by source contract id across all snapshots — two projects' `Person/autodoc/openapi.yaml` collapsed into one row | same root cause | CONFIRMED |
| CTH-F02 | Rename/move | a renamed folder (`Autorisasjon/` → `AutorisasjonService/`) was always `New` → a second contract | no relocation matching | CONFIRMED |
| CTH-F03 | Bulk environment/auth | binding only per contract (`ContractEndpointPanel`, `PUT contracts/{key}/binding`) | — | CONFIRMED |
| CTH-F04 | Browser execution | runs verified through the API only in earlier acceptances | acceptance gap (feature existed) | CONFIRMED (verification gap) |
| CTH-F05 | Project-wide coverage | coverage per contract version only | — | CONFIRMED |
| CTH-F06 | Chain suggestions | authored chains only; chains had no draft state | — | CONFIRMED |
| CTH-A1 | Project-level authorization | **no project membership model exists** in BirkNext (role policies only, local session) | product scope | NOT SUPPORTED (recorded; project isolation implemented as identity/query scoping) |
| CTH-A2 | Token audience validation | `ExecutionCredentialResolver` does not validate audience/scopes | product scope | NOT SUPPORTED (shown as a visible limitation in bulk configuration) |

No existing capability was found that already resolved any of F01–F06.
