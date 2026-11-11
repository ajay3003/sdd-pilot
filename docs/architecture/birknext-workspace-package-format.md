# BirkNext workspace package format (`.birknext`, v1) and full backup format (`.bnbackup`, v1)

Two formats with different safety models. A package moves derived, non-secret workspace state between installations. A backup recovers one
installation. Neither format contains executable code, and neither is ever executed on import.

## 1. Portable package — `.birknext`

A ZIP archive (deflate). Every entry path is checked on write and on read:
- relative, `/`-separated;
- no `..`, `.` or empty segment;
- no drive letter, backslash or control character;
- at most 400 characters;
- under one of the allowed folders below.

| Entry | Content |
|---|---|
| `manifest.json` | `PackageManifest` (§1.1) |
| `inventory/objects.json` | `PortableObject[]`: type, logical id, label, revision, owning project, environment, portability, fingerprint, entry path, dependencies |
| `inventory/dependencies.json` | `DependencyEntry[]`: from → to, status (INCLUDED, EXTERNAL_REFERENCE, MISSING, OPTIONAL, UNAUTHORIZED, UNSUPPORTED), required |
| `sources/`, `contracts/`, `test-definitions/`, `integration-suites/`, `topology/`, `reviews/`, `connected-systems/`, `schemas/`, `browser-e2e/`, `test-data/`, `environments/`, `evidence/` | one JSON payload per object: `{folder}/{type}/{readable-id}-{hash10}.json` |
| `integrity/hashes.json` | `{ entry path: SHA-256 }` for every other entry; written last |

### 1.1 Manifest

`format` = `birknext-workspace-package`, `packageFormatVersion` = 1, plus:
- `exportType`: ProjectWorkspace, IntegrationWorkspace or Diagnostic;
- `birkNextApplicationVersion`, `sourceCommit` (or `unknown`), `exporterInstanceId` (a random, non-secret installation id), `exportTimestamp`, `label`;
- `includedProjects`, `includedIntegrationScopes`, `schemaVersion` (the exporter's main migration head);
- `requiredCapabilities`: `provider:{type}:{capability}`, `critical-e2e:{mode}`, `journey-pack:{id}`;
- `contentFingerprints`, `dependencyManifest`, `redactionProfile`, `optionalEvidencePolicy`;
- `completeness`: COMPLETE, or PARTIAL (explicitly allowed by the exporter);
- `exclusions`, and `authenticity` = "UNSIGNED …".

**A reader refuses** an unknown `format`, a `packageFormatVersion` other than 1, or a backup type. A newer format fails safely; it is never "best effort".

### 1.2 Object types and logical identity

| Type | Logical id | Payload (the owner's own record) |
|---|---|---|
| `source-snapshot` | snapshot uuid (global) | `iqr_source_snapshots` row + `source_schema_artifacts` row + `source_contract_documents` rows |
| `contract` | contract `Key` (unique) | `api_contracts` + versions + tests + test revisions (+ endpoint bindings of the exported environment only) |
| `parquet-rule-set` | `{project}:{contract}@{contractVersion}#v{version}` | `parquet_rule_sets` row |
| `integration-suite` | `{SuiteKey}@{Revision}` | `integration_suites` row |
| `composite-source-context` | `{ContextKey}@{Revision}` | `composite_source_contexts` row (stage → snapshot id + archive SHA-256, stored compatibility) |
| `source-association` | `{keyA}\|{keyB}` | `source_repository_associations` row (original author/time) |
| `connected-systems` | `setup` | the existing sanitised **setup package** (JSON) + system ids |
| `catalog-platform`, `catalog-integration` | catalog id | catalog row (re-keyed to the destination environment) |
| `evidence-binding`, `contract-artifact`, `graphql-schema`, `message-flows` | integration id / role / target | catalog rows (re-keyed) |
| `critical-e2e-flow`, `critical-e2e-module` | flow id / module name | Critical E2E store records |
| `test-data-asset`, `test-data-values` | alias | registry asset metadata / authored synthetic values with every version |
| `environment-profile` | `profile` | browser Target Environment profile with secret-, identity- and trust-shaped keys removed |
| `historical-record` | `{kind}:{originalId}` | an original run, decision or approval, verbatim with its timestamps |

Local database keys are never the identity, except snapshot uuids, which are global by construction. On import an object keeps its uuid only when
that uuid is free; otherwise it gets a new one and the change is recorded in the identity map.

### 1.3 Integrity, not authenticity

`integrity/hashes.json` and each object's `fingerprint` detect accidental or deliberate changes in transfer. Both live inside the same unsigned
archive, so they cannot prove who produced it. A package is evidence of content, not of origin.

### 1.4 Limits (reader)

| Limit | Value |
|---|---|
| Entries | 20,000 |
| Per entry, expanded | 64 MB |
| Total, expanded | 1 GB |
| Compression ratio (entries > 1 MB) | 200 |
| Upload | 512 MB, streamed to a staging file, never held in memory |

Symlink entries and duplicates after case folding are refused. Reads are bounded by the declared size.

## 2. Full backup — `.bnbackup`

```
magic "BNBACKUP" | version (1) | PBKDF2 iterations (int32) | salt (16) | nonce prefix (7) | header length (int32) | header JSON | chunks…
chunk = ciphertext length (int32) | ciphertext | GCM tag (16)
```

- **Encryption:** AES-256-GCM in the STREAM construction, 1 MiB chunks.
  - Nonce = prefix ‖ chunk counter ‖ final flag.
  - Associated data of every chunk = the whole preamble.
  - Key = PBKDF2-HMAC-SHA256(passphrase, salt, iterations ≥ 100,000; default 600,000).
  - Truncation, reordering, appended data, a changed header and a wrong passphrase all fail authentication. A wrong passphrase cannot be told apart from tampering, by design.
- **Header** (`BackupPublicHeader`, plaintext but authenticated): backup id, time, application version and commit, PostgreSQL and dump-tool versions, migration heads per EF context, KDF parameters, note. Before decryption the header is only a claim.
- **Payload:** a ZIP containing:
  - `database/birknext.dump` (pg_dump custom format, every schema);
  - `database/inventory.json` (row counts, migration ids and FK count from the same snapshot);
  - `files/{store}/{file}` (managed file stores);
  - `manifest.json` (`BackupManifest`: inclusions and exclusions with reasons, consistency method, file inventory with SHA-256, snapshot references from files and the dangling ones);
  - `integrity/hashes.json`.
- The ZIP is streamed into the encryptor, so no plaintext copy exists.
