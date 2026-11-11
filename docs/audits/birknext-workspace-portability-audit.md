# BirkNext: Workspace backup, export, import and recovery (independent audit, 2026-11-11)

Branch `008-traceability-first`. Baseline `fe12e79ed50ee02ca076ad715be495ddf9944873`. Read-only audit of the repository and of the live database schema. No user database was changed.

**Inputs.**
- Code, read through three independent read-only passes: the database model, the file stores and secrets, and the existing export/import mechanisms with the integration model.
- `information_schema` / `pg_catalog` of the local PostgreSQL 16.13 (read only).

**Concurrent work at baseline.** These are not ours and must not be staged:
- `appsettings.Local.json` (modified);
- untracked `AIAssisted/testdata/`;
- four untracked 2026-11-07 audit documents and `birknext-automatic-integration-test-generation-audit.md`.

**Infrastructure.**
- Podman machine running; `birknext-studio-local_postgres_1` healthy (restarted on 2026-11-11 after the WSL outage).
- `pg_dump` / `pg_restore` 16.13 exist **only inside the container**, not on the Windows host.

## 1. What exists today (reuse, do not duplicate)

| Capability | Where | Reuse decision |
|---|---|---|
| Sanitized Connected Systems package (YAML/JSON, schema v1; portable non-secret fields only; secret/identity/credential-looking values rejected on parse; preflight with New/Same/Conflict per key; transactional apply with explicit conflict decisions; dependency resolution) | `Services/ExternalIntegrations/EnvironmentSetupPackages.cs`, `SetupLibraryService.cs`, `api/target-environments/external-integrations/setup/{export,preflight,apply}` | **Reuse as the Connected Systems section** of an integration package. A second Connected Systems serializer would compete with it. |
| Execution evidence export (ZIP: `manifest.json`, `runs/{id}/…`, `hashes.json`; ≤ 20 runs / 50 MB; already-redacted documents) | `Services/ExecutionEvidence/ExecutionEvidenceManagement.cs:237-301` | Reuse its layout conventions and its `hashes.json` integrity idea for optional evidence. |
| Canonical bounded archive reader (zip-slip, symlink, duplicate, size and entry-count checks) | `Services/Integrations/SourceEvidence/IqrSourceArchiveReader.cs` | Its limits model is reused. It is a *source* reader (50 MB, 2 MB per file) and cannot open a backup, so a package reader with the same checks and package-specific limits is needed. An architecture test (`SourceIngestionOwnershipTests`) guards every `new ZipArchive(`; the new reader and writer must be added to it with a justification. |
| Workspace JSON export/import (saved SDD workspaces, schema 1.0) | `WorkspacePersistenceService.cs:652-833` | Unchanged; it is the per-user document workspace, not a project transfer. It omits `SddLifecycleJson` and has no conflict model (recorded as a gap, not changed here). |
| Local Data Reset (category inventory, reset epoch, synthetic-key floors) | `Services/LocalDataReset/*` | The ownership model starts from its Cleared/Preserved lists. A restore must respect `local-data-reset.json` (epoch and floors). |
| Authentication | Loopback-only host, local-session cookie / Entra bearer, CSRF (`LocalRequestGuard.cs`), capability policies `ActiveEventExecute` and `IntegrationConfigurationWrite` (`ActiveEventAuthorization.cs`) | Reuse the capability-policy pattern for new transfer capabilities. |
| Backup / restore / pg_dump | **none** | New. |
| Project export / import across installations | **none** (Project Import ingests a source ZIP; it does not transfer derived state) | New. |

## 2. Persistence ownership map

### 2.1 Database (one PostgreSQL database, four EF contexts, 108 tables, 36 FK constraints, no extensions, large objects or sequences)

| Group | Tables | Primary / logical identity | Scope column | Secrets | Portability | Export | Import | Restore |
|---|---|---|---|---|---|---|---|---|
| Source snapshots | `iqr_source_snapshots` (+ `source_schema_artifacts` FK, `source_contract_documents` UQ (SnapshotId, Path)) | uuid; logical = repository key + archive SHA-256 + analysis time (inside EvidenceJson) | none (`EnvironmentId=""`, `IntegrationId="source-analysis"`) | no (no archive bytes, no source contents kept) | PORTABLE | project / integration closure | same uuid if absent; IDENTICAL if same content; CONFLICTING otherwise (never overwritten) | dump |
| Contract registry | `api_contracts` (UQ Key, `ProjectKey`), `api_contract_versions` (UQ ContractId+Version, Sha256), `contract_tests`, `contract_test_revisions` (UQ TestId+Revision), `contract_review_decisions`, `contract_endpoint_bindings` (UQ ContractId+EnvironmentId), `contract_test_chains` | Key / (contract, version) / (test, revision) | ProjectKey; bindings per Env | none in columns; test documents UNVERIFIED → scanned on export | PORTABLE (bindings ENVIRONMENT_DEPENDENT) | project | by logical key; newer/older revision classification; bindings NEEDS_REVALIDATION | dump |
| Parquet rule sets | `parquet_rule_sets` UQ (ProjectKey, ContractKey, ContractVersion, Version) | natural key | ProjectKey | no | PORTABLE | project | by natural key | dump |
| Integration suites | `integration_suites` UQ (SuiteKey, Revision) + Fingerprint; `integration_suite_runs` | natural key | SuiteKey embeds project; Env nullable | no | PORTABLE; runs = HISTORICAL_EVIDENCE | project / integration | by (SuiteKey, Revision) | dump |
| Composite source contexts | `composite_source_contexts` UQ (ContextKey, Revision), ContextKey = `{scope}:{owningProject}` | natural key | OwningProject | no | PORTABLE (references snapshots by id + archive SHA-256) | integration / project | by natural key; referenced snapshots must resolve | dump |
| Connected Systems | `external_integrations` (id `{env}:{provider}:{slug}`), `altinn_environment_settings` (typed Altinn settings + runtime observations) | id / env | Env | secret fields stripped before persist; secrets session-only | PORTABLE config + ENVIRONMENT_DEPENDENT; observations = evidence | integration via the **existing setup package** | existing preflight/apply; imported systems disabled | dump |
| Integration catalog | `integration_platforms`, `integration_definitions` (PK env+id), `integration_environment_states`, `integration_evidence_bindings` (env+integration → snapshot), `integration_contract_artifacts`, `graphql_schema_artifacts`, `integration_message_flows` | natural composite PKs | Env | "no column ever holds a secret" | PORTABLE, re-keyed to the destination environment | integration | by natural key under the mapped environment | dump |
| Execution evidence | `execution_runs`, `execution_calls` (UQ RunId+Sequence), `execution_evidence_audit`, `execution_retention_settings`, `active_*_runs`, `integration_journey_runs`, `performance_test_runs`, observation tables | uuid | Env | redacted documents | HISTORICAL_EVIDENCE (optional) | optional | IMPORTED_HISTORICAL_EVIDENCE, never a new run | dump |
| Synthetic identity reservations | `active_event_synthetic_identities` PK (Env, Scope, Value), `active_cdc_runs` UQ synthetic PKs | natural | Env | no | NOT portable (they protect against key reuse in a real hub) | never | — | dump |
| Performance | `performance_test_definitions`, `performance_test_data_profiles`, `performance_baselines` | string ids | Env | documents UNVERIFIED | PORTABLE definitions (UNVERIFIED secret content → scanned) | not in v1 packages (gap) | — | dump |
| Reviews and decisions | `manual_review_decisions`, `test_coverage_decisions`, `workspace_review_progress`, `reviewed_candidates`, `traceability_suggestions`, `source_repository_associations`, canonical `review_decision`, `release_sign_off` | mixed | mixed | no | HISTORICAL_DECISION | `source_repository_associations` with the project; others not in v1 | historical, never re-granted | dump |
| SDD workspaces | `saved_workspaces` (+2 FK children) | uuid | user_id | no | existing JSON export | not in v1 | — | dump |
| Delivery reporting | 19 tables, `project` UQ name | uuid | delivery project | no (PAT in a file) | out of scope for workspace packages | — | — | dump |
| Canonical interpretation | 10 tables, scope = Target Environment profile id | uuid | scope | no | out of scope for v1 packages | — | — | dump |
| Snapshot registry | 3 tables | natural | project | no | REGENERABLE ("rebuilt from the remaining stores on next use") | never | — | dump |
| Unused or legacy | `"IntegrationQualitySnapshots"` (no writer), `security_expectation_discoveries` (reader only) | — | — | — | — | never | — | dump |

### 2.2 Files (not in the database)

| Store | Class | Secrets | Classification | Backup | Package |
|---|---|---|---|---|---|
| `App_Data/critical-e2e/flows.json`, `history.json`, `modules.json` | `CriticalE2EStore` | no (by design) | MANAGED_FILESYSTEM, authoritative | yes | flows/modules per environment; history optional |
| `App_Data/critical-e2e/suggestion-reviews.json` | `CriticalE2ESuggestionStore` | no | MANAGED_FILESYSTEM | yes | per snapshot (project) |
| `App_Data/test-data/registry.json`, `authored.json`, `identities.json`, `canonical-fixtures.json`, `schema-associations.json`, `protected-defaults.json` | Test Data stores | no values-as-credentials (rules reject them); synthetic identifier-shaped values in protected defaults | MANAGED_FILESYSTEM | yes | assets per environment / snapshot |
| `App_Data/local-data-reset.json` | `LocalDataReset` | no | MANAGED_FILESYSTEM, safety-critical (synthetic key floors) | yes | never |
| `App_Data/microsoft-sign-in.json` | `AdminService` | no (public client ids) | installation configuration | yes | never |
| `%LOCALAPPDATA%/BirkNext/git-sources.json` | `GitSourcesService` | no | installation configuration outside App_Data | yes (when present) | never |
| `App_Data/api-service-credentials/`, `azure-devops/`, `browser-authentication/` | credential stores | **YES** (DPAPI-protected secrets + key rings) | SECRET_STORE | **no** (DPAPI keys do not decrypt on another user or machine; copying them would ship secrets for nothing) | never |
| `App_Data/local-session/` | `LocalSessionService` | **YES** (bearer launch code) | SECRET_STORE, regenerable | no | never |
| `App_Data/parquet-sessions/` | Parquet explorer | possibly real data rows | REGENERABLE_CACHE (2 h) | no | never |
| `%TEMP%/birknext/project-import-staging/` | Project Import | customer source | temporary (30 min) | no | never |
| Browser profiles, Companion builds, Windows certificate store | various | **YES** (session cookies, launch token, CA private key) | SECRET_STORE / machine state | no | never |
| `logs/` | Serilog | possibly | diagnostic | no | never |
| `appsettings.Local.json` | AdminService | may hold keys a user added | configuration | no (documented) | never |

### 2.3 External or derived state (referenced, not owned)

| Item | Where | Consequence |
|---|---|---|
| **Target Environment profiles** | browser `localStorage` (`birknext:frontend-analysis-settings`); secrets are `[JsonIgnore]` | **Not in any backend backup.** A full backup cannot contain them. A package can carry one only when the browser supplies it at export. It is sanitised again server-side and imported as a *new disabled draft*, never merged into an existing profile. |
| Trusted execution environments | configuration `TargetEnvironments:Trusted` | not importable (configuration only) |
| Integration topology | computed per request (`IntegrationTopologyService.cs:75-79`) | regenerable; never exported as data |
| Integration Journey definitions | compiled packs (`SkoleNaervaerJourneyPack`) | capability references only |
| Executors / observers | DI services (`IJourneyStepExecutor/Observer`, `ICriticalE2EStepExecutor`), provider capability strings | packages carry **capability ids**; the destination checks them against its own registrations |
| Original source archives | **not retained** after Project Import commit | a package cannot contain the original ZIP; it carries the derived snapshot, its XSD package and contract documents, keyed by archive SHA-256 |

## 3. Confirmed gaps

| ID | Gap | Severity | Required |
|---|---|---|---|
| WP-G1 | No full backup of any kind | High | PostgreSQL-native dump (custom format) of all four schemas, with the managed file stores and a manifest |
| WP-G2 | No consistency boundary between the database and the file stores | High | an exported database snapshot (`pg_export_snapshot` + `pg_dump --snapshot`) so the inventory and the dump are the same moment; file stores hashed before and after the dump, recaptured, and labelled if they moved |
| WP-G3 | No encryption for anything leaving the installation | High | authenticated encryption (AES-256-GCM, chunked) with a passphrase-derived key (PBKDF2-SHA256); no plaintext temporary copy |
| WP-G4 | No restore | High | isolated restore into a new database name and a separate file directory; never the active database; validation report; no cutover action |
| WP-G5 | No portable package for derived project state | High | versioned `.birknext` ZIP with manifest, inventory, content hashes, dependency manifest |
| WP-G6 | No integration-workspace transfer | High | integration closure keyed by the Target Environment id: Connected Systems (via the existing setup package), catalog, contract artifacts, evidence bindings → snapshots, composite contexts, suites, Critical E2E flows, Test Data, profile draft |
| WP-G7 | No portable identity convention | Medium | logical identities from existing natural keys (§2.1); uuids kept only when they do not collide; an import mapping recorded per operation |
| WP-G8 | No conflict model across installations | Medium | NEW / IDENTICAL / NEWER_REVISION / OLDER_REVISION / CONFLICTING_CONTENT / MISSING_DEPENDENCY / UNSUPPORTED / ENVIRONMENT_REBIND_REQUIRED, with explicit decisions |
| WP-G9 | No two-phase import with a persisted plan | Medium | read-only preview → plan fingerprint + destination state fingerprint → approved apply (database transaction) → file stores (idempotent) → reconciliation; durable states |
| WP-G10 | No per-project authorization exists anywhere in BirkNext | Medium | cannot be invented by this feature. New capability `WorkspaceTransfer` (claim, or the local configuration-write rule in Development) gates export, import, backup and restore. The package scope is enforced by the closure, never by a caller-supplied id list. Per-project authorization stays a platform gap. |
| WP-G11 | Application commit is not known at runtime (assembly version `1.0.0`, no SourceRevisionId) | Low | `SourceCommit` assembly metadata from the build (git when available, otherwise `unknown`), recorded in manifests |
| WP-G12 | `CriticalE2EStore.ClearAll` locks `_flows`, not `_gate` | Low (seen during audit) | out of scope; recorded |
| WP-G13 | `suggestion-reviews.json` and `parquet-sessions` are not cleared by Local Data Reset | Low | out of scope; recorded |

## 4. Design decisions forced by the audit

1. **A full backup is a PostgreSQL custom-format dump plus a file-store copy**, never a JSON export. Restoring it is `pg_restore` into a new database.
2. **`pg_dump` runs either on the host or through `podman exec -i <container>`**, configured explicitly (`Backup:PostgresTools`). The password goes in the process environment, never in argv. In container mode the container-local socket is used, so there is no password at all.
3. **Secrets never travel.** DPAPI stores, sessions, launch codes, browser profiles and certificates are excluded from backups and packages. A restored installation must re-enter credentials, and the backup says so.
4. **Project identity in packages = the Source Analysis repository key (ProjectKey).** That is the key the contract registry, parquet rule sets, suites and composite contexts already use.
5. **Integration identity in packages = the Target Environment id**, with mandatory environment remapping on import: an existing environment chosen by a person, or a new draft. Names never decide.
6. **Connected Systems use the existing setup package.** Imported systems are disabled, and every environment-dependent value is NEEDS_REVALIDATION. Nothing connects or executes on import.
7. **Imported evidence is IMPORTED_HISTORICAL_EVIDENCE.** It is never a new run and never validates the destination.
