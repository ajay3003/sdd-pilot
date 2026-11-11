# BirkNext — Workspace backup, export, import and recovery: implementation (2026-11-11)

Branch `008-traceability-first`. Baseline `fe12e79e`. Audit: `../audits/birknext-workspace-portability-audit.md`. Formats:
`../architecture/birknext-workspace-package-format.md`. Operations: `../operations/birknext-backup-recovery.md`. No push, no deployment.

## Commits

| Commit | Phases | Content |
|---|---|---|
| `35a498a2` | A–D | Audit; encrypted full backup; isolated restore; crypto; bounded archive; WorkspaceTransfer capability; SourceCommit stamp |
| `28e99c6c` | E–N | Portable packages: closures, secret scan, two-phase import, conflicts, identity map, history, migration `AddWorkspaceTransfer` |
| `7982fafd` | P | Backup & Transfer page, client, navigation, User Guide section, documentation catalog |
| `e688c8e6` | — | Restores line endings changed by `sed` in two test files |
| `826e8717` | Q–S fixes | Evidence closure gap and 768 px layout, both found by browser acceptance |
| `f641cbb3` | CI fix | .NET 8 parse error at `WorkspacePackageService.Import.cs:215` (parenthesised pattern), 14 CS1570 doc-comment warnings, GUIDs no longer mistaken for identifiers by the secret scanner and redaction |

## 1. Shared building blocks (`Services/WorkspaceTransfer/`)

| File | What |
|---|---|
| `TransferCrypto.cs` | AES-256-GCM STREAM encryption and decryption streams, PBKDF2-SHA256, plus `TransferFormatException` / `TransferAuthenticationException` |
| `TransferArchive.cs` | `TransferArchiveWriter` (hashes every entry; works on a non-seekable stream) and `TransferArchiveReader` (path, prefix, duplicate, symlink, size, ratio and declared-size checks; `VerifyHashesAsync`). Registered in the source-ingestion ownership test with its justification. |
| `PostgresTools.cs` | `pg_dump` / `pg_restore` in Host or ContainerExec mode. Argument lists only (no shell); the password only in the child's environment; stderr bounded and sanitised; timeout; cancellation kills the process tree. |
| `ManagedFileStores.cs` | An explicit allow-list of owned file stores, and the exclusion list with reasons |
| `WorkspaceTransferAuthorization.cs` | The `WorkspaceTransfer` capability policy (claim, or local Development + loopback via `Authorization:LocalWorkspaceTransfer`) |
| `CodeMigrations.cs` | The migrations this build knows, per context (compiled model only) |
| `BirkNext.Api.csproj` | `SourceCommit` assembly metadata: `-p:BirkNextSourceCommit`, else `git rev-parse HEAD`, else `unknown` |

## 2. Full backup and isolated restore (`FullBackupService.cs`)

- **Single-flight** (one backup or restore at a time); disk-space precheck.
- **Consistency:** files hashed → exported REPEATABLE READ snapshot → files re-read and compared (retried 3×) → inventory and `pg_dump --snapshot` from the same snapshot (§3 of the operations guide).
- **Output** goes straight from the ZIP into the encryptor into `{name}.partial`, which is renamed on success and deleted on failure. The backup directory is owner-only.
- **Restore:** decrypt and verify in an owner-only staging folder → compatibility gate → `CREATE DATABASE birknext_restore_{12hex}` → `pg_restore` → managed files into a separate folder → independent validation against the manifest → `report.json`. A database it created is dropped only if `pg_restore` fails.
- **No cutover.**

## 3. Portable packages (`WorkspacePackageService*.cs`, `PackageModel.cs`)

- **Closures**, always computed on the server; a caller never lists records:
  - **Project** = Source Analysis repository key: its snapshots, contracts with every version, tests and test revisions, Parquet rule sets, suites on its snapshots, composite contexts it owns, associations and Test Data. Other projects' stage sources become EXTERNAL_REFERENCE.
  - **Integration** = Target Environment id: Connected Systems through the existing `EnvironmentSetupService.ExportAsync` (portable, non-secret fields only), the catalog, source bindings and their snapshots, contract artifacts, GraphQL schemas, message flows, contracts bound to the environment with those bindings, its suites, every composite context touching its sources (every stage source included, each in its own project), Critical E2E flows and modules, Test Data assets and authored values, journey-pack references, and an optional sanitised profile.
  - **Diagnostic** = one suite or flow, redacted.
- **Optional evidence:** suite runs, the environment's execution runs, runs of the package's contract tests (by DefinitionRef), Critical E2E runs and journey runs, as `historical-record` objects. Contract review decisions are always historical.
- **Secret scan** (`PackageSecretScanner`): the setup-package rules (`SetupProfileSchema.ValueIssue`) plus secret-named fields, applied to every value including JSON stored as text.
  - Blocking findings stop the export.
  - Identifier-shaped values need an acknowledgement that covers only the findings shown.
  - Diagnostic redaction walks the JSON tree (not serialised text) and must scan clean afterwards.
- **Import:**
  - **Stage:** the raw request body is streamed to staging (≤ 512 MB); the operation is STAGED.
  - **Preview** writes only the operation record:
    - verifies archive limits and hashes, the manifest format and version, unique identities, payload fingerprints and capabilities;
    - classifies each object against the destination: NEW / IDENTICAL / NEWER_REVISION / OLDER_REVISION / CONFLICTING_CONTENT / MISSING_DEPENDENCY / UNAUTHORIZED / UNSUPPORTED / ENVIRONMENT_REBIND_REQUIRED;
    - stores the plan, its fingerprint and a destination-state token per object.
  - **Apply:**
    - the plan fingerprint must match, and the environment-independent destination tokens must be unchanged (otherwise `DESTINATION_CHANGED`);
    - each decision must be one of the item's allowed actions;
    - the environment is chosen explicitly: ReuseVerified (one of the candidates offered at preview), CreateDisabled (`imported-{8hex}`) or LeaveUnresolved;
    - states persist APPROVED → APPLYING;
    - **Phase 1:** all database objects in one transaction (FAILED_ROLLED_BACK on any error);
    - **Phase 2:** owner stores, each step idempotent (FAILED_RECOVERABLE, retryable into the same new environment):
      - Connected Systems through `EnvironmentSetupService.Preflight/ApplyAsync`, with every system forced `enabled: false` and every conflict kept;
      - Test Data `Register` / `Create` / `AddVersion`;
      - Critical E2E `Save` (disabled, not release-gating, tagged `imported:`) and `RegisterModule`;
      - the profile draft returned to the browser;
    - then COMPLETED with reconciliation, readiness dimensions and the identity map.
- **Never transferred as current:** imported integration definitions are disabled; flows lose `Enabled` and `RequiredForRelease`; Test Data approvals are not carried; runs and decisions go to `imported_historical_records` (unique per origin, kind and original id, so re-import adds none), never to destination run or decision tables.
- **Storage:** migration `20261111065052_AddWorkspaceTransfer` adds `workspace_transfer_operations` (audit and plan; no secret, no passphrase, no package content) and `imported_historical_records`.

## 4. API (`WorkspaceTransferController*.cs`, route `api/workspace-transfer`, policy WorkspaceTransfer)

- **Backups and restores:**
  - `GET backups/status`, `GET backups`, `POST backups`;
  - `POST backups/{file}/inspect`, `GET backups/{file}/download` (the encrypted file as is);
  - `GET/POST restores`.
- **Packages and history:**
  - `POST packages/scopes`, `POST packages/export/preview`, `POST packages/export`, `GET packages/exports/{file}/download`;
  - `POST packages/import` (raw body; never multipart);
  - `POST packages/import/{id}/preview`, `POST packages/import/{id}/apply`, `GET packages/import/{id}/report`;
  - `GET operations`.
- Errors return `{code, message}` with 400/404/409/410/413/422/502/503/507 mappings.

## 5. UI (`Pages/WorkspaceTransfer.razor`, Help & Settings → Backup & Transfer)

Four deep-linked areas (`?area=overview|export|import|backup`, `&kind=project|integration|diagnostic`):
- **Overview:** five operation cards and recent activity.
- **Export:** a stepper; a searchable scope grid; a dependency review with counts, a stage map built only from stored contexts, dependencies not in the package, capabilities and the security review; never-exported details; blockers with acknowledge and partial options; a download with SHA-256.
- **Import:** a stepper; file upload; checks; the manifest; environment mapping (unresolved by default; reuse only offers the browser's own profiles; create disabled); objects grouped by status, with decisions only where several actions are allowed; the verification report with colour-by-meaning readiness, reconciliation, identity map, a "Create the disabled draft Target Environment" action and retry.
- **Backup & Restore:** status, inclusions and exclusions, the passphrase form with a key-loss warning, the backup list with Verify / Restore, and restore reports with their cutover policy.

The browser supplies only its Target Environment list and the profile JSON. An imported profile is created as a new, unverified draft and never merged.

## 6. Tests

- **Backend:**
  - `TransferCryptoAndArchiveTests`: round trips at boundary sizes, wrong passphrase, changed header, flipped byte, truncation, reordering, appended data, unsupported version and parameters, passphrase rules; zip slip variants, unexpected entry, duplicate, symlink, oversize, entry count, zip bomb, tampered / unlisted / missing hashes, non-ZIP.
  - `FullBackupServiceTests`, against real `pg_dump`/`pg_restore` and temporary databases: exact restore with an independent oracle (the source unchanged; staging removed); wrong passphrase and tampering create no database; a newer backup is refused; file drift is retried and labelled; refusals leave no file.
  - `WorkspacePackageRoundTripTests`, two temporary databases: a project round trip with re-import idempotency; an integration round trip into a new disabled environment; unresolved environment; reuse limited to offered candidates; conflicts and newer revision; DB rollback; recoverable file failure and retry; destination change and plan change; tampered / corrupt / future / duplicate / cross-project packages; secrets, acknowledgement and diagnostic redaction; unknown capabilities.
- **Frontend:** `WorkspaceTransferPageTests` (5); the navigation, documentation catalog and User Guide guards were updated deliberately.
