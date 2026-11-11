# BirkNext — Backup & Transfer implementation (2026-11-11)

- **Baseline:** `b9e48ad4`.
- **Runtime strategy:** `../architecture/birknext-backup-transfer-runtime-strategy.md`.
- **Root cause:** `../audits/birknext-postgresql-backup-tool-discovery.md`.

| Area | Change | Files |
|---|---|---|
| Contract | `BackupReadinessStatus` (Ready/Blocked/Unavailable/NotChecked/Incompatible), `BackupReadinessItem`, `BackupReadiness` (no secrets) | `shared/WorkspaceTransferContracts.cs` |
| Resolver | `PostgresToolReadiness.EvaluateAsync`: database, configuration, real tool execution, container identity, version compatibility, restore destination; `Major`, `RestoreToolProblem` | `Services/WorkspaceTransfer/PostgresToolReadiness.cs` (new) |
| Tools | `PostgresTools.Options`; `QueryInContainerAsync` (one read-only psql query; argument list, no shell) | `PostgresTools.cs` |
| Service | `FullBackupService.ReadinessAsync`; create/restore refuse unless Ready (`TOOLS_UNAVAILABLE` / `TOOLS_INCOMPATIBLE` / `TOOLS_BLOCKED`); the restore checks the backup's pg_dump version against pg_restore before creating anything; the runtime profile is passed from `Program.cs` | `FullBackupService.cs`, `Program.cs` |
| API | `backups/status` includes `readiness`; new codes map to 503 | `WorkspaceTransferController.cs` |
| Tester launcher | `Backup__PostgresTools__Mode=Host` and `BinDirectory=<package>\runtime\postgresql\bin`; inherited `Backup__*` cleared | `launcher/BirkNext.Launcher/Hosts.cs` |
| Developer launcher | `Get-ComposePostgresContainer` (compose project + service labels, exactly one match) sets `Backup__PostgresTools__Mode=ContainerExec`, `Runtime` and `Container`, unless already configured | `scripts/start-local.ps1` |
| UI | Full backup card: readiness tiles, blocker panel (runtime, expected tools, "Looked in", Retry detection, setup guide link), readiness details, included/excluded summary with disclosures, passphrase show/hide with live mismatch/length state, "Create encrypted backup" with a visible reason when disabled, honest progress note, restore assurance and restore blocker, restore disabled unless ready | `Pages/WorkspaceTransfer.razor(.cs,.css)`, `Services/WorkspaceTransferApiService.cs` |
| Help | "Backup readiness and PostgreSQL tools" disclosure in the Backup & Transfer article | `Components/Guide/GuideReference.razor` |
| Tests | `PostgresToolReadinessTests` (new, 12 cases); `TesterLauncherTests` (bundled tools in the environment); `WorkspaceTransferPageTests` (ready layout, missing tools, retry) | backend/frontend test projects |

**Not changed:**
- the backup format, encryption, consistency model, exclusions and package import/export;
- the Tester Package database role (BT-G3).
