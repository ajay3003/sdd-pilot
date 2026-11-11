# BirkNext — Backup & Transfer audit (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `b9e48ad4`.
- **Other sessions' files:** left untouched (`appsettings.Local.json`, `AIAssisted/testdata/`, the 2026-11-07 audit files).

## Capability inventory

| Feature | Implementation / owner | Observed | Gap | Risk | Change | Acceptance |
|---|---|---|---|---|---|---|
| Full backup | `FullBackupService.CreateAsync`: exported snapshot, `pg_dump -Fc`, managed files, AES-256-GCM package | Engine sound; blocked by tool discovery | BT-G1, BT-G2, BT-G4 | High: no backup possible in either profile | Resolver + launcher configuration | BT-10…15 (two providers) |
| PostgreSQL tool discovery | `PostgresTools` (`Backup:PostgresTools`) | PATH only unless configured; "Available" = `--version` | Root cause | High | `PostgresToolReadiness` | BT-01…04, unit tests |
| Runtime profiles | `BirkNext:RuntimeProfile` set by the launchers (`RuntimeProfiles`) | Authoritative; DeveloperContainer is the default for a source run | Tool mode shown as "Runtime mode" | Low | Readiness shows the profile separately from the provider | UI |
| Encryption | `TransferCrypto`: AES-256-GCM chunks, PBKDF2, header authenticated | Correct | — | — | Reused | BT-14, BT-16, BT-17 |
| Manifest | `BackupManifest`: format, application version and commit, server version, dump tool version, migration heads, tables and rows, file hashes, exclusions, consistency | Correct; build commit "unknown" in scratch builds without git | — | — | — | BT-19 |
| Managed files | `ManagedFileStores`: 5 eligible stores; count = files that exist | Correct; label unclear | BT-G11 | Low | "N found" + list | BT-23 |
| Exclusions | `ManagedFileStores.Exclusions` (11) | Correct | — | — | Disclosure | BT-24 (canaries) |
| Isolated restore | `RestoreIsolatedAsync`: new database + separate folder, inventory check, no cutover | Correct; no tool/version precheck; impossible under NOCREATEDB | BT-G3, BT-G5 | Medium | Precheck + Blocked reason | BT-17…26 |
| Project / integration export, import preview / apply | `WorkspacePackageService` (two-phase, identity mapping, history-only runs) | Unchanged; covered by its existing tests (in the focused 72) | — | — | — | Existing tests |
| UI | `WorkspaceTransfer.razor` | Text-heavy, disabled action without a reason | BT-G6 | Medium | Redesign of the Full backup card | BT-01…04, BT-L |
| Help | `GuideReference` Backup & Transfer article | No tool guidance | — | Low | Readiness and tools disclosure | — |

The root cause and the fix are in `birknext-postgresql-backup-tool-discovery.md`, the acceptance in `birknext-backup-transfer-acceptance.md`, and open items in `birknext-backup-transfer-gap-register.md`.
