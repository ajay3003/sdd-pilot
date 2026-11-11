# BirkNext — Artifact versioning & provenance: implementation (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `ab49d94d`.
- **Related documents:**
  - audit: `../audits/birknext-artifact-versioning-audit.md`
  - model: `../architecture/birknext-artifact-versioning-model.md`
  - acceptance: `../audits/birknext-artifact-versioning-acceptance.md`
  - gaps: `../audits/birknext-artifact-versioning-gap-register.md`
- No push, no deployment, no pipeline trigger.

## 1. Shared contracts

- **`shared/ExecutionManifestContracts.cs` (new, linked in both projects):**
  - `ExecutionManifest`, `ManifestDependency` (kind, ref, key, version, fingerprint, origin, resolution), `ManifestEnvironment`, `ManifestBuild`;
  - `ExecutionApplicability`, `DependencyCurrency`;
  - the `ExecutionApplicabilityRules.Combine` verdict;
  - string enums.
- **`ExecutionRunRequest`:** `[JsonIgnore] OwnerAttestedRefs` and `OwnerDependencies`, for server-side owners only.
- **`ExecutionRunSummary`:** `DependencyFingerprint`.
- **`ContractTestRunRequest`:** `[JsonIgnore] PinnedRevisions` and `OwnerDependencies`. **`ContractChainRunRequest`:** `[JsonIgnore] OwnerDependencies`.
- **`CriticalE2ERunResult`:** `FlowFingerprint`. New `CriticalE2EFlowVersionRecord`.

## 2. Backend

| File | Change |
|---|---|
| `Services/ExecutionEvidence/ExecutionManifestService.cs` (new) | Builds the manifest from the stored records (contract version SHA-256, test revision document SHA-256, chain revision, authored Test Data fingerprint, source snapshot existence, owner dependencies); dependency fingerprint; `GetAsync`; `ApplicabilityAsync` with per-kind currency rules |
| `ExecutionRunService.StartAsync` | Builds the manifest before anything is sent; the summary carries the dependency fingerprint |
| `ExecutionEvidenceStore.CreateAsync` | Inserts the manifest in the same `SaveChanges` as the run and its calls |
| `ExecutionEvidenceManagement.DeleteRunsAsync` | Deletes manifests with their runs (PostgreSQL transaction and non-relational branch) |
| `Models/ExecutionEvidenceRecords.cs`, `Data/AppDbContext.cs` | `ExecutionManifestRow` → `execution_manifests` (unique Owner+RunId, index on DependencyFingerprint) |
| `Data/Migrations/20261111094759_AddExecutionManifests.cs` | Table and indexes; on PostgreSQL the `execution_manifests_immutable()` function and the `execution_manifests_no_update` BEFORE UPDATE trigger |
| `Controllers/ExecutionEvidenceController.cs` | `GET runs/{id}/manifest`, `GET runs/{id}/applicability` |
| `Services/ContractTesting/ContractTestService.cs` | `RunAsync`/`RunChainAsync` attest the references they materialised the calls from and add the endpoint binding dependency (caller-declared when a base path or GraphQL path override is used). `RunAsync` honours `PinnedRevisions` (refuses retired tests and missing revisions; never substitutes). `BindAsync` re-pinning the same version and `RegenerateAsync` with identical content add no revision. |
| `Services/IntegrationSuites/IntegrationSuiteService.cs` | Suite execution pins `SuiteTestRef.Revision` for every test and adds suite and composite-context dependencies (`SuiteDependencies`) |
| `Services/IntegrationSuites/IntegrationSuiteService.Composite.cs` | Composite runs add the suite and context dependencies |
| `Services/ExternalIntegrations/SetupLibraryService.cs` | A new version with identical content is refused ("Nothing changed") |
| `Services/CriticalE2E/CriticalE2EStore.cs` | Append-only `flow-versions.json`; backfill once; `FlowVersions`; `Fingerprint`; cleared by `ClearAll`, kept on `Delete` |
| `Services/CriticalE2E/CriticalE2ERunner.cs`, `CriticalE2ECoverage.cs` | Runs carry `FlowFingerprint`; a different fingerprint under the same version is stale |
| `Controllers/CriticalE2EController.cs` | `GET flows/{flowId}/versions` |
| `Program.cs` | `ExecutionManifestService` (scoped, with the authored Test Data store) |

## 3. Frontend

- **`Services/ExecutionEvidenceApiService.cs`:** `ManifestAsync` and `ApplicabilityAsync`. The interface methods have defaults, so other implementations keep compiling. A 404 for a manifest is treated as "no manifest", not as an error.
- **`Pages/ExecutionResults.razor`, the "Executed against" panel on the run page:**
  - a verdict pill: Current definitions / Historical result / Currency unknown;
  - the sentence "The *passed* result is unchanged.", followed by the explanation;
  - one row per pinned dependency: kind, reference, short SHA-256, stated by (BirkNext materialised / caller declared), and its state now;
  - the BirkNext build, the dependency fingerprint and the schema version.

## 4. Tests

- **Backend, `ExecutionManifestTests` (in-memory DB, synthetic loopback target, real Execution Evidence and Contract Testing services):**
  - A run pins every definition, each fingerprint checked independently against the database or the Test Data store.
  - A historical PASS stays a PASS while a contract, Test Data, test or binding change makes it Historical. The manifest is byte-identical afterwards.
  - Identical reruns share the dependency fingerprint.
  - A pinned revision runs exactly that revision; the target is the oracle.
  - Caller-declared refs and overrides are never current, and owner-only fields cannot be bound from JSON.
  - A run without a manifest is Unknown; deleting a run deletes its manifest.
  - Re-pinning and identical regeneration add no revision.
- **Backend, `ExecutionManifestPostgresTests` (migrated temporary database):** the trigger refuses UPDATE; deletion with the run works.
- **Backend, `CriticalE2EFlowVersionHistoryTests`:** one record per version, with the old definition kept; identical save adds none; persistence; delete keeps the history and reset clears it; backfill once; stale by fingerprint.
- **Frontend, `ExecutionResultsPageTests` (+2):** a historical result keeps its outcome and shows what moved; a run without a manifest shows "Currency unknown" and no table.
