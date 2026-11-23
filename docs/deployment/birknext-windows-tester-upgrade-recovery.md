# BirkNext Tester for Windows — upgrade, backup and recovery

Your data lives in `%LOCALAPPDATA%\BirkNext\TesterNative`. The package folder holds only programs and can be replaced.

## Upgrade BirkNext with the updater

1. Download the new BirkNext artifact **BirkNext-Tester-Windows-x64** from the approved Azure DevOps pipeline **in your browser**. Keep the ZIP; do not extract it. The browser saves it as `BirkNext-Tester-Windows-x64.zip`, which contains the package `BirkNext-Tester-Windows-x64-<version>.zip` and its `.sha256`: the updater accepts either ZIP and checks the package against the checksum. (Updaters older than 2026-11-23 accept only the inner `BirkNext-Tester-Windows-x64-<version>.zip`.)
2. Open the updater:
   - **first time:** `Update-BirkNext.exe` in the new package (extract that package once, anywhere) or in your current package folder;
   - **after the first update:** Start menu → BirkNext → **BirkNext Updater**.
3. Choose **Select BirkNext ZIP** and pick the downloaded file. The updater checks it:
   - every file against the package's own list (SHA-256);
   - that it is a BirkNext Windows tester package for this PC;
   - that it is newer than the installed version;
   - that Windows recorded it as downloaded from Azure DevOps.

   Nothing is changed while it checks.
4. Read what it will do: the version it replaces, whether BirkNext must be stopped, and whether the database is updated. Confirm with **y**.
5. The updater installs the new version next to the current one, stops BirkNext, switches to the new version and starts it. It shows each step.

You do not stop BirkNext yourself, copy files, run database commands or reset anything. After the first update, **Start menu → BirkNext → BirkNext** always starts the installed version.

- **Where versions are installed:** `%LOCALAPPDATA%\BirkNext\Installations\<version>\`. The current and the previous version are kept; older ones are removed.
- **Updater log:** `%LOCALAPPDATA%\BirkNext\Installations\logs\`.

### Why a package can be refused

| Message | Meaning | What to do |
|---|---|---|
| Not downloaded from the approved Azure DevOps pipeline | Windows has no record that the ZIP came from Azure DevOps: it was copied, mailed, re-zipped or built locally | Download the artifact from the pipeline in your browser and select that file |
| Older than the installed BirkNext / last opened by a newer version | Installing it could damage data written by the newer version | Use the newest pipeline package |
| Cannot be ordered against the installed BirkNext | One of the two is not a pipeline build (for example a developer build) | Use a pipeline package |
| Too old for the updater | The package was built before the updater existed | Use a newer package |
| Damaged, incomplete, or containing unexpected files | A file does not match the package's list | Download the package again |
| BirkNext did not stop | The running BirkNext did not stop within 2 minutes; nothing was changed | Close the BirkNext window and run the update again |

The updater never stops BirkNext by force and never touches other programs.

### Going back

- **No database changes in the new version:** `Update-BirkNext.exe --rollback` makes the previous version active again.
- **Database changes in the new version:** the updater refuses to go back, because the previous version cannot use the updated database. See "Going back to the previous version" below.

### Interrupted updates

An update that is interrupted (power loss, closed window) is resolved the next time the updater opens:

- **Interrupted before the switch:** the unpacked files are removed and your BirkNext is unchanged.
- **Interrupted after the switch:** start BirkNext; its first start finishes the update.

### Trust

BirkNext packages are not signed yet. The updater therefore accepts only ZIPs that Windows recorded as downloaded from Azure DevOps (`dev.azure.com` or `*.visualstudio.com`) and checks every file. This shows where the file came from; it is not a cryptographic signature.

An administrator can limit it to named Azure DevOps organizations in `%LOCALAPPDATA%\BirkNext\Installations\update-trust.json`:

```json
{ "hosts": ["dev.azure.com", "*.visualstudio.com"], "organizations": ["your-organization"] }
```

## Upgrade BirkNext by hand (without the updater)

1. Stop BirkNext: Ctrl+C in its window, close the window, or `Start-BirkNext.exe --stop`.
2. Extract the new version into a **new** folder. Do not extract over the old one.
3. Start the new `Start-BirkNext.exe`.

What happens on that first start (with or without the updater):
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
