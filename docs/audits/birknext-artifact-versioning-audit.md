# BirkNext — Artifact versioning & provenance: audit (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `ab49d94d`.
- **Method:** read-only code audit. Every finding has a file reference, relative to `AIAssisted/`. I re-read the execution path and every storage owner I changed; the inventory of the other owners is the independent sweep's, with its references.
- **Related:** implementation in `../hardening/birknext-artifact-versioning-implementation.md`, the model in `../architecture/birknext-artifact-versioning-model.md`, the acceptance record in `birknext-artifact-versioning-acceptance.md`, and the gap register in `birknext-artifact-versioning-gap-register.md`.

## 1. Summary

- **Revisions:** BirkNext has no shared revision registry. Each artifact owner versions itself, and several do it well:
  - contract versions, contract-test revisions and chains;
  - integration-suite revisions and composite contexts;
  - authored Test Data, Parquet rule sets and Setup Library.
- **No history at all:** Critical E2E flows, performance definitions, Connected Systems, the integration catalog and Target Environments keep only their current state.
- **Execution pinning is partial and string-based:**
  - Execution Evidence records `key@vN`, `contract-test:{id}@rN` and `alias@vN` as text.
  - It records no content fingerprints, no endpoint binding and no BirkNext build.
  - Anyone who can POST a run can declare those strings.
  - **Integration-suite runs did not run the revisions their suite pinned:** they ran the tests' current revisions.
- **Historical results:** there is no "historical vs currently applicable" concept. A PASS against contract v3 was, in practice, read as the test's latest result even after v4 existed. The exceptions are Critical E2E (`LastRunStale`) and a per-test readiness hint.

## 2. Capability matrix

| Capability | Existing owner | Behaviour before this work | Gap |
|---|---|---|---|
| Cross-artifact revision/lineage registry | Snapshot Registry (`Data/SnapshotRegistry/SnapshotRegistryDbContext.cs`, `Services/Snapshots/*`) | Entries, edges and pointers for 14 snapshot types (source, delivery, interpretation, review, execution/perf, Critical E2E) | No contract, test, chain, suite, flow, Test Data or Execution Evidence types |
| Immutable content revisions | `api_contract_versions` (unique ContractId+Version), `contract_test_revisions`, `contract_test_chains`, `integration_suites`, `composite_source_contexts`, `parquet_rule_sets`, `setup_library_versions`, authored Test Data `Versions` | Each owner has its own number + fingerprint scheme | Critical E2E flows (`CriticalE2EStore.Save` overwrote `_flows[id]`), performance definitions, Connected Systems, the integration catalog and Target Environments keep the current state only |
| No artificial revision on identical content | Contract import (`ContractRegistryService.cs:123`, `.Source.cs:315`), suite fingerprint, composite fingerprint, Test Data update ("Nothing changed"), test edit ("Nothing changed"), flow `Material()` | Mostly correct | `ContractTestService.BindAsync` re-pinning the same version and `RegenerateAsync` producing identical content each added a revision; Setup Library `NewVersionAsync` added a version for identical content |
| Declared vs content version | Contract registry | `info.version` kept apart from Sha256 | Only contracts |
| Execution pinning | Execution Evidence (`ExecutionRunService.StartAsync`, `ExecutionEvidenceContracts.cs:243-281`) | Strings `key@vN`, `@rN`, `alias@vN`, chain ref | No fingerprints; no binding; no build; caller-declared (POST `api/execution-evidence/runs` accepts any refs); not immutable as a unit |
| Suite revision pinning | `IntegrationSuiteService.ExecuteAsync` | The run named the suite revision | Ran `ContractTestService.RunAsync` → `LoadAsync` = **current** test revision, not `SuiteTestRef.Revision`; the mismatch was invisible |
| Historical vs current | Contract readiness (`ContractTestService.cs:318-358`), Critical E2E `LastRunStale` (`CriticalE2ECoverage.cs:46`), composite `NewerSources`, Test Data usage NeedsReview | Per-owner hints | No result-level verdict; "last result" by test prefix returns any revision |
| Flow version history | Critical E2E | `FlowVersion` on runs | The definition of an earlier version is gone, and so is its content identity |
| BirkNext build provenance | `SourceCommit` assembly metadata (Backup & Transfer) | Used in backups and packages | Not recorded per execution |
| Version-history UI | Contract Testing versions/diff, test revision compare/revert, suite drift, Schema Studio compare, Snapshot Browser | Page-specific | No shared component (deliberately not prioritised: see §4) |

## 3. Execution pinning per owner (before this work)

| Owner | Pinned | Not pinned |
|---|---|---|
| Contract tests / chains (Execution Evidence) | Contract `key@vN`, test `@rN`, Test Data `alias@vN`, dataset row, resolved values, chain `@rN`, environment id/type, target origin, auth reference | Contract SHA-256, test revision content hash, Test Data fingerprint, endpoint binding, BirkNext build; owner vs caller provenance |
| Integration suite runs | `integration_suite_runs.SuiteId` (revision row), TestIds | **The executed test revisions** (current revisions ran) |
| Composite runs | PlanJson (stages, sources with ArchiveSha256), ChainRef | Context revision in the run evidence; chain steps use current test revisions by design |
| Critical E2E | FlowVersion, BuildId/ReleaseId/CommitSha of the target, environment, profile, Test Data aliases/ids | The definition content of that version; Test Data versions |
| Integration journeys | PackVersion, source snapshots, Test Data alias + fingerprint | Environment configuration, BirkNext build |
| Performance | Definition id/version/fingerprint + full `DefinitionSnapshot`, ComparisonFingerprint, baseline, source snapshot, runtime image digest, `Host.BirkNextVersion` | Test Data versions |
| Parquet inspections | File SHA-256, contract version, document SHA-256, rule-set version | — |
| Active Event / Active CDC | Source contract ref, scenario/fixture versions, snapshot, archive SHA-256, commit, analyser version | Test Data (Active Event) |

## 4. Decision

Following the stated priority (*"implement immutable execution manifests and dependency pinning before spending too much effort on version-history UI"*), this work:

1. Adds an **immutable execution manifest** per Execution Evidence run. It is resolved server-side from the stored records, fingerprinted, separates owner-resolved from caller-declared dependencies, is written in the run's own transaction, and is protected by a database trigger.
2. **Pins the suite revision:** suite runs execute exactly the test revisions their suite revision names.
3. Adds a **result applicability** evaluation: CurrentlyApplicable / Historical / Unknown, per dependency. It never changes the result.
4. Removes the three **artificial-revision** paths found.
5. Adds an append-only **Critical E2E flow version history** with a content fingerprint on every run.
6. Adds one compact UI panel ("Executed against") on the run page. A shared version-history component is deliberately not built (gap AV-G7).
