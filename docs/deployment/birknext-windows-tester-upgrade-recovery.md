# BirkNext Tester for Windows — upgrade, backup and recovery

Your data lives in `%LOCALAPPDATA%\BirkNext\TesterNative`. The package folder holds only programs and can be replaced.

## Upgrade BirkNext

1. Stop BirkNext: Ctrl+C in its window, or `Start-BirkNext.exe --stop`.
2. Extract the new version into a **new** folder. Do not extract over the old one.
3. Start the new `Start-BirkNext.exe`.

What happens on that first start:
- BirkNext sees that a different build last opened your database. It writes and verifies a backup first: `backups\birknext-<time>-pre-upgrade.dump`.
- BirkNext checks that there is enough free disk space before it backs up; if not, it stops and changes nothing.
- The new version then applies its database updates.
- The five newest automatic pre-upgrade backups are kept. Backups you make yourself (`--backup`) are never removed.

Keep the old folder until you have checked the new version.

## Going back to the previous version

- **No database changes in the new version:** starting the old `Start-BirkNext.exe` works.
- **Database changes in the new version:** the old version refuses to start with *"The database requires a newer BirkNext than this package."* It does not downgrade the database. Then either:
  - use the new version; or
  - restore the pre-upgrade backup (below) and use the old version. Work done in the new version after the upgrade is not in that backup.

## Backups

- **Make one:** with BirkNext stopped, run `Start-BirkNext.exe --backup`.
  - The backup is a PostgreSQL logical backup (`pg_dump` custom format) of the BirkNext database.
  - It is checked with `pg_restore --list` before BirkNext reports success.
  - Copying the database folder of a running database is **not** a backup.
- **Files outside the database:** the Test Data catalog, Critical E2E history and local settings in `app-data\` are files, not database rows. Copy `app-data\` together with the backup, or use BirkNext's own Backup & Transfer for workspace packages.

## Restore a backup (BirkNext owner or IT)

Never restore over the database you are using. Restore into a new data folder:

1. Start BirkNext once with a new, empty data folder, then stop it:
   `Start-BirkNext.exe --data-dir D:\BirkNextRestore --no-browser`
2. Run `Start-BirkNext.exe --status --data-dir D:\BirkNextRestore` to confirm it was created.
3. Restore the backup into it with the bundled tools. The `birknext_app` password is protected for your Windows user, so ask the BirkNext owner for the supported restore script.
   - Gap: no `--restore` command exists yet; see the gap register.
   - Restore into an isolated cluster was verified in acceptance with `pg_restore`.
4. Copy the matching `app-data\` files.
5. Start BirkNext with `--data-dir D:\BirkNextRestore` and check your data before you use it.

## When BirkNext was not closed normally

This covers power loss, a killed window or a crash.

Start BirkNext again. Then:
- **The database is still running:** BirkNext reuses it only after verifying that it is your BirkNext database (its system identifier and folder).
- **The database stopped abruptly:** PostgreSQL runs its own crash recovery on start. Committed data is kept.
- **Stale files:** BirkNext never deletes database lock files or rewrites database files itself.

## Messages that stop the start

| Message | Meaning | Safe action |
|---|---|---|
| A previous first-run setup was interrupted | The first start was interrupted | BirkNext moves the half-created folder aside (`postgres-data.incomplete-<time>`, kept) and creates the database again |
| Database files are present but cannot be opened | The data folder holds database files BirkNext did not create, or the running server is not BirkNext's | Nothing is changed. Do not delete the folder; send `--diagnostics` |
| BirkNext's local database files are missing | `database\postgres-data` is gone but BirkNext's record of it exists | Restore the folder from a backup. BirkNext never silently creates a new empty database |
| The database requires an upgrade before BirkNext can start | The database was created by another PostgreSQL major version | See below. Nothing is changed |
| The selected port is already used by another application | Another program uses BirkNext's port | BirkNext never stops other programs. For the database it moves to the next free port automatically; for the web ports, start again |

## PostgreSQL major-version upgrades

- **Never automatic:** a package with a new PostgreSQL major version does not convert your database on start, and an older PostgreSQL is never started on a newer database.
- **Procedure:**
  1. With the old package: `Start-BirkNext.exe --backup`.
  2. With the new package: start once with a new `--data-dir`, stop, and restore the backup into it (see Restore).
  3. Check the data, then use the new data folder.
  
  The old folder stays until you remove it yourself.
