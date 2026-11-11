# BirkNext — Artifact versioning & execution manifest model (2026-11-11)

Contracts: `AIAssisted/shared/ExecutionManifestContracts.cs`. Service: `backend/BirkNext.Api/Services/ExecutionEvidence/ExecutionManifestService.cs`.

## 1. Three different things that were easy to confuse

| Concept | Meaning | Where |
|---|---|---|
| **Revision** | An immutable stored state of one artifact: contract `key@vN`, test `contract-test:{id}@rN`, chain `@rN`, Test Data `alias@vN`, suite `suite:{key}@rN`, composite `composite:{key}@rN`, flow `flowId` version N. A new revision exists only when the content changed. | Each owner's own table/file (unchanged) |
| **Execution manifest** | What one run executed against: every revision it used, each with its content fingerprint at run time, plus environment and BirkNext build. Written once with the run, never updated. | `execution_manifests` |
| **Applicability** | Whether a run's result is still evidence for what is current. Evaluated when asked; it never changes the result. | `ExecutionManifestService.ApplicabilityAsync` |

**A historical PASS stays a PASS.** Applicability adds a label next to the result; it never rewrites the result.

## 2. Manifest (`execution-manifest/1`)

```
ExecutionManifest
  ManifestId, Owner ("execution-evidence"), RunId, CreatedAt, Initiator
  Environment  { EnvironmentId, EnvironmentType, TargetOrigin, AuthenticationMode, AuthenticationReference }   — never a credential
  Build        { Version, SourceCommit }   — assembly informational version + SourceCommit metadata ("unknown" when not stamped)
  Dependencies [ { Kind, Ref, Key, Version, ContentFingerprint, Origin, Resolution, Detail } ]
  DependencyFingerprint = SHA-256(environment + dependencies ordered by kind, ref)
```

### Dependency kinds and how each is resolved

Dependencies are resolved from the stored record, never from the caller's claim.

| Kind | Ref | ContentFingerprint |
|---|---|---|
| Contract | `key@vN` | `api_contract_versions.Sha256` |
| ContractTest | `contract-test:{id:N}@rN` | SHA-256 of the stored revision document (`contract_test_revisions.DocumentJson`; legacy tests: the current row when it is that revision) |
| ContractChain | `contract-chain:{id:N}@rN` | SHA-256 of `contract_test_chains.DocumentJson` |
| TestData | `alias@vN` | The authored version's `Fingerprint` |
| EndpointBinding | `binding:{key}@{env}` | SHA-256 of (key, env, effective base path, GraphQL path, authentication) |
| SourceSnapshot | `source-snapshot:{id}` | Existence only (always caller-declared) |
| IntegrationSuite | `suite:{key}@rN` | `integration_suites.Fingerprint` |
| CompositeContext | `composite:{key}@rN` | Revision only |

### Provenance of each dependency

- **OwnerResolved:** Contract Testing materialised the run's calls from that stored record. Contract Testing passes these refs through `ExecutionRunRequest.OwnerAttestedRefs` / `OwnerDependencies`, which are `[JsonIgnore]` and so can never be bound from a request body.
- **CallerDeclared:** the ref was only named:
  - in a run started directly through `POST api/execution-evidence/runs`;
  - by a base-path or GraphQL-path override, which means the stored binding is not what ran;
  - by a source snapshot id.
- **NotFound:** the ref could not be resolved when the run started.

### Lifecycle

- The manifest is built in `ExecutionRunService.StartAsync` before anything is sent.
- It is inserted in the same `SaveChanges` as the run and its planned calls (`ExecutionEvidenceStore.CreateAsync`), so the run and its manifest exist together or not at all.
- A PostgreSQL trigger (`execution_manifests_no_update`) refuses UPDATE.
- A manifest is deleted only with its run: evidence deletion and retention (`ExecutionEvidenceManagement.DeleteRunsAsync`), or Local Data Reset (which empties every model table).
- The run summary carries `DependencyFingerprint`, so lists can tell identical definitions apart without loading manifests.

## 3. Applicability rules

`ExecutionApplicabilityRules.Combine` takes, for each dependency, its state now:

| State | Meaning |
|---|---|
| Unchanged | The pinned record is still the current one, with the same content |
| Superseded | It still exists, but a newer version or revision is current. For a contract, "latest" ignores hidden versions. For a test, the current row's revision differs. For Test Data, the current version is newer. Suites, chains and composites compare the max revision. |
| Changed | The same reference now has different content. This happens with mutable records (endpoint bindings), or when an immutable record was altered, which is never expected. |
| Removed | The record no longer exists (deleted test, removed Test Data alias, deleted contract version) |
| NotVerifiable | Unresolved at run time, or caller-declared |

The verdict:
- Any Superseded, Changed or Removed dependency → **Historical**.
- Otherwise, any NotVerifiable dependency, or none at all → **Unknown**.
- Otherwise → **CurrentlyApplicable**.
- A run created before manifests existed is **Unknown**, never current.

## 4. Dependency pinning

- **Integration suite revision:** `IntegrationSuiteService.ExecuteAsync` passes `PinnedRevisions` (TestId → the revision in `SuiteTestRef.Revision`) and the suite/composite dependencies.
  - `ContractTestService.RunAsync` then runs that stored revision (`RevisionAsync`).
  - A retired test, or a pinned revision that no longer exists, is listed as not run; it is never silently replaced by the current revision.
- **Chains:** a chain step runs its test's current revision by design. The manifest records exactly which revision ran.
- **Composite runs:** the suite and composite-context revisions are recorded. Steps are chain steps, so the previous bullet applies (gap AV-G3).

## 5. No artificial revisions

| Path | Rule |
|---|---|
| Contract import (upload/URL/source) | Identical SHA-256 to the latest version → Unchanged (existing) |
| Test edit | Identical content → "Nothing changed" (existing) |
| Test Data save | Identical content and metadata → "Nothing changed" (existing) |
| Suite / composite generation | Identical fingerprint → reused (existing) |
| Critical E2E flow save | Identical `Material()` → same version (existing); history records each version once (new) |
| **Test Data re-pin to the same alias@version** | No new test revision (new) |
| **Regenerate with identical result** | No new test revision (new) |
| **Setup Library new version with identical content** | Refused with "Nothing changed: identical to version N" (new) |

## 6. Critical E2E flow versions

- **Storage:** `App_Data/critical-e2e/flow-versions.json`, append-only. Each record holds `{FlowId, Version, Fingerprint, RecordedAt, Backfilled, Definition}`.
- **When a record is added:** on a new flow, or on a save that creates a new version.
- **Backfill:** flows saved before this history existed get their current version recorded once, with `Backfilled = true`. Their earlier versions are unrecoverable, and the record says so.
- **Deletion:** deleting a flow keeps its history, because runs reference it. Local Data Reset clears the history.
- **On every run:** `CriticalE2ERunResult.FlowFingerprint` holds the SHA-256 of the executed `Material()`. A run whose fingerprint differs from the current definition is stale even when the version number matches (for example after an import re-keys the version).
- **API:** `GET api/critical-e2e/flows/{flowId}/versions`.

## 7. API

| Endpoint | Purpose |
|---|---|
| `GET api/execution-evidence/runs/{runId}/manifest` | The manifest (404 for runs from before manifests) |
| `GET api/execution-evidence/runs/{runId}/applicability` | Verdict + per-dependency state; read-only |
| `GET api/critical-e2e/flows/{flowId}/versions` | Flow definition history, newest first |
