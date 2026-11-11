# BirkNext — Backup & Transfer gap register (2026-11-11)

| ID | Gap | Status | Note |
|---|---|---|---|
| BT-G1 | "PostgreSQL tools: Not available" in the developer setup: no tool provider configured, and no host client on PATH | **FIXED** | The developer launcher passes the compose PostgreSQL container (label lookup); the backend verifies its identity |
| BT-G2 | The same failure in the Tester Package: the launcher did not pass the bundled `runtime/postgresql/bin` | **FIXED** | `Backup__PostgresTools__BinDirectory` + `Mode=Host`; inherited `Backup__*` variables are cleared |
| BT-G3 | Isolated restore in the Tester Package: the app role is `NOCREATEDB`, so `CREATE DATABASE` fails | OPEN (reported, not changed) | Readiness now shows Restore **Blocked** with the reason. Backups work. A fix (CREATEDB on the app role, or a launcher-mediated restore with admin credentials) changes the package's security model and needs a decision |
| BT-G4 | "Available" was granted when `--version` started: no server-version comparison, no container identity | **FIXED** | Readiness compares majors and verifies the container's server fingerprint |
| BT-G5 | A restore did not check pg_restore against the backup's pg_dump version | **FIXED** | It fails before any database is created |
| BT-G6 | The UI showed tool mode as "Runtime mode", a disabled button without a reason, no retry, and a long exclusions wall by default | **FIXED** | Readiness tiles, a blocker panel with search locations, Retry detection, included/excluded summary with disclosures, live passphrase validation with show/hide, an honest single-step progress note, and the restore assurance |
| BT-G7 | Backup progress is one server call: there are no live stage events | OPEN | The UI says which stages run on the server and lists the backup only after final verification; no fake percentages |
| BT-G8 | The full Tester Package was not rebuilt and accepted end to end with the new backend | NOT RUN | Covered by unit tests plus execution of the bundled binaries |
| BT-G9 | Synthetic fixtures F (port conflict), O (interrupted restore) and P (existing destination) were not exercised in the browser | PARTIAL | P is guarded in code (`TARGET_EXISTS`); O relies on single-transaction `pg_restore` |
| BT-G10 | Scratch databases `birknext_bt_20261111`, `birknext_restore_d31ac0f2cedc` and `birknext_restore_7cf865d9b616` (and earlier `birknext_sn_20261111`, `birknext_su_20261111`, `birknext_pq3_20261111`) | OPEN (needs approval to drop) | Kept; nothing is dropped automatically |
| BT-G11 | "Managed files: 8" vs "6" | EXPLAINED | The count is the files that **exist now** in the eligible stores; missing stores are not counted. The UI now labels it "found" and lists them |
