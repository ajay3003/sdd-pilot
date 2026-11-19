# Windows tester artifact updater — 2026-11-19

Branch `008-traceability-first`. Implementation commit `14021365`. Local only; not pushed.

**Principle.** An upgrade is never a reinstall and never a reset:

- the updater manages versions and transitions;
- the launcher manages the running application;
- the existing data stores stay authoritative.

## 1. Existing distribution (audited, unchanged)

- **Package:** one ZIP per pipeline run, built by `scripts/build-tester-package.ps1` (pipeline stage in `azure-pipelines.yml`). It contains:
  - `Start-BirkNext.exe` (launcher, self-contained);
  - `AIAssisted/backend` (self-contained) and `AIAssisted/frontend/wwwroot`;
  - bundled PostgreSQL 16.13 and k6;
  - `config/runtime-manifest.json`, `build-manifest.json` (build identity), and `package-manifest.json` (every file with its SHA-256, plus the
    migration ids).
- **Data:** `%LOCALAPPDATA%\BirkNext\TesterNative`, outside the package, with an owner-only ACL.

  | Store | Path | Engine / format | Upgrade behaviour |
  | --- | --- | --- | --- |
  | Database (projects, imports, source snapshots and associations, saved plans and case revisions, requirement links, Test Data catalog records, technical results, review decisions) | `database/postgres-data` | PostgreSQL 16, four EF contexts | Migrated by the new backend after a verified `pg_dump` |
  | App data (local session, Test Data files, Critical E2E history, local settings) | `app-data/` | Files | Shared as is |
  | Installation configuration (cluster identity, ports, DPAPI credentials, runtime profile) | `config/` | JSON / DPAPI | Shared as is |
  | Backups | `backups/` | `pg_dump` custom format, checked with `pg_restore --list` | Pre-upgrade: newest 5 kept |
  | Logs, diagnostics, runtime state | `logs/`, `diagnostics/`, `runtime-state/` | Files | Shared |
  | Git Sources settings and caches | Inside the data folder (the launcher sets `GitSources__SettingsDirectory`) | JSON / bare repositories | Shared |

- **Separation:** verified in the package layout, the launcher (`DataLayout`) and the runtime architecture document. Nothing persistent is
  inside the extracted package, so no data migration out of the package folder is needed.
- **Existing upgrade safety, reused as is:** on the first start of a different build, the launcher:
  1. takes a verified pre-upgrade backup (checking free space first);
  2. lets the backend apply EF migrations under its advisory lock;
  3. refuses a newer schema with an older backend ("The database requires a newer BirkNext").

  A PostgreSQL major change is never automatic.

What was missing:

- a supported way to install a downloaded ZIP without manual folder handling;
- any validation before an old version is stopped;
- version ordering and downgrade protection before the data is touched;
- a record of which version is active;
- recovery from an interrupted transition.

## 2. Architecture

| Responsibility | Owner |
| --- | --- |
| Validate a package, compare versions, apply the trust policy | `Update-BirkNext.exe` (`AIAssisted/launcher/BirkNext.Updater`) |
| Install, choose the active version, journal and recover | Updater (`Installations\installations.json`, `update-journal.json`) |
| Stop the running BirkNext | The running launcher, asked through its own stop request (`runtime-state/stop-request.json`) |
| Back up, migrate and start | The new launcher and backend on their first start (existing code) |
| Report the result | Updater console, and `Installations\logs\updater-*.log` |

- **Standalone:** the updater is a separate small executable (no web host, `asInvoker`, no administrator rights). It runs even when the
  installed BirkNext cannot start. After the first update a stable copy lives at `Installations\Update-BirkNext.exe`, outside every version
  folder.
- **Layout:**
  - `%LOCALAPPDATA%\BirkNext\Installations\<build version>\`, one folder per installed build. The current and previous versions are kept;
    older ones are removed when not in use.
  - `.staging-<id>\` while unpacking, on the same volume, so activation is a rename.
  - Start menu → BirkNext → **BirkNext** (active version) and **BirkNext Updater**, rewritten on every activation.
- **Single instance:**
  - The launcher's per-user mutex per data folder (`SingleInstance`, now its own source file, linked into the updater) is the instance
    identity. Ports and session files are never treated as proof.
  - A second update is refused while a journal is open.

## 3. Package validation (before anything changes)

`PackageInspector.Inspect` reads the ZIP without extracting it. It refuses:

- **Unsafe entry names:**
  - absolute or drive paths, `..`/`.`, alternate data streams (`:`);
  - Windows device names (including `aux.js`-style), and names ending in a dot or space;
  - control characters, and case-insensitive duplicates;
  - symbolic-link entries.
- **Size problems:** more than 20 000 entries, more than 4 GB unpacked, or a file over 1 MB compressed more than 200:1.
- **Missing required files:** `Start-BirkNext.exe`, `Update-BirkNext.exe`, the three manifests, the backend executable and `index.html`.
- **A wrong build manifest:** a product other than `BirkNext`, `WindowsTesterPackage`, `x64`, `TesterNative`, or a valid version.
- **A runtime manifest** without `updateContract` (packages from before the updater) or with a newer contract than this updater supports.
- **A different file set** than `package-manifest.json` lists, whether extra or missing files.

`PackageInspector.Extract` unpacks only into the staging folder:

- every file is hashed while it is written, and the first difference from the manifest stops the update;
- the bytes actually written are bounded (a lying ZIP header cannot exceed them).

The real package from `daa84ff7` (2 746 files, 577 MB) sits well inside every limit: highest compression ratio 9.1, no device-name or
character collisions.

## 4. Trust policy

Packages are not signed today: `azure-pipelines.yml` has no signing step. An SHA-256 inside the same ZIP proves integrity, not origin.

Decided with the product owner on 2026-11-19:

- **Pipeline origin.** A package is accepted only when Windows' Mark of the Web (`Zone.Identifier`) says it was downloaded from
  `dev.azure.com` or `*.visualstudio.com` (https). `*.` matches subdomains only, never the `evil-suffix` trick.
- **Organization limit.** `Installations\update-trust.json` can limit trust to named organizations, read from the referring pipeline URL.
- **Fails closed.** Copied, mailed, re-zipped or locally built packages are refused, with the reason given.
- **Not authentication.** The mark is written by the browser and can be changed by the same Windows user. It is provenance, combined with
  the full integrity check.
- **Policy source.** The policy comes from the updater or the per-user file, never from the package being checked.

**Dependency for publisher authentication.** A code-signing certificate in the pipeline is needed:

- Authenticode-sign `Start-BirkNext.exe`, `Update-BirkNext.exe` and `BirkNext.Api.exe`;
- sign `package-manifest.json` (a detached signature);
- pin the certificate thumbprint in the updater.

Until then the updater says what it checked and never calls a package "authenticated".

## 5. Versions

The identity comes from `build-manifest.json` (`applicationVersion`, `channel`, `buildVersion`, `pipelineRunId`, `sourceCommit`), never from
file names.

| Candidate vs installed | Result |
| --- | --- |
| Higher application version | Upgrade |
| Same application version, both pipeline builds (CI or Release) | Ordered by pipeline run id (Azure DevOps run ids only grow) |
| Same informational version or run id | Same build: refused, nothing to do |
| Lower | Downgrade: refused |
| A Local or Unknown build against another build of the same version | Not comparable: refused |
| Data last opened by a newer build (`runtime-state/last-run.json`), for example a package extracted by hand | Refused |
| The candidate lacks database migrations the installed version applied | Refused: it could damage data |

## 6. Shutdown

The running launcher stops in order (frontend, backend, PostgreSQL `pg_ctl stop -m fast`) through its own stop request. The stop is confirmed
only when the data folder's instance lock is released.

- **Bounded:** 2 minutes. On timeout the request is withdrawn, so it cannot stop BirkNext later, and the update ends with nothing changed.
- **No force:** the updater never terminates a process. Port ownership is never used as identity.
- **Prerequisite:** the launcher close-window fix (`f3f16650`). The launcher now owns its backend and database in a job object, so closing
  its window or killing it leaves no orphan.

## 7. Backup, migration and failure handling

The updater never touches the database. Every upgrade, with or without the updater, goes through the launcher's existing path: a verified
`pg_dump` (checked with `pg_restore --list`), then EF migrations.

- **Explicit confirmation:** before confirming, the updater reports how many migrations the new version adds, from the two packages'
  `package-manifest.json` migration lists. When the database changes, the wording is stronger: the previous version can no longer open the
  data.
- **Failure before activation:** nothing changes. The staging folder is removed and the journal cleared.
- **The new version does not start:**
  - with database changes, the result names the backup folder and says not to start the previous version;
  - without database changes, `--rollback`.
- **Rollback:** only when the active version added no migrations. Otherwise it is refused, and the way back is the pre-upgrade backup.
- **Incompatible executables:** an old executable can never run against a newer schema. The backend refuses it, and the updater refuses to
  activate it.

## 8. Journal and recovery

`update-journal.json` records the stages `Validated → Staging → Staged → Stopping → Stopped → Activated → Started → Completed`, with the
from and to builds and the staging or candidate folder. It never holds a secret.

On every start, the updater resolves an open journal:

- **Before Activated:** the unpacked files are removed and the installed version is unchanged.
- **After Activated:** the new version is active; the tester starts it, and its first start backs up and migrates.

Activation is one atomic replace of `installations.json`, and the version folder is renamed from staging on the same volume.

## 9. Pipeline contract

- The package now contains `Update-BirkNext.exe` (self-contained single file, published by the package script).
- `config/runtime-manifest.json` declares `updateContract: 1`.
- One ZIP format serves fresh installation and upgrade.
- The existing pipeline gate (archive checksum, `--validate`, isolated start and stop) is unchanged. An upgrade step against the previous
  pipeline artifact is recommended but not added: it needs that artifact in the pipeline.

## 10. Tester workflow

`docs/deployment/birknext-windows-tester-upgrade-recovery.md`, together with the package README's UPGRADE section, covers:

- download the ZIP in the browser;
- open BirkNext Updater;
- Select BirkNext ZIP, check it, confirm;
- what the updater does;
- the refusal reasons;
- going back, interrupted updates and trust.

## 11. Automated tests

`UpdaterTests`, with synthetic packages and disposable folders:

- version ordering;
- Release vs CI ordering, and invalid versions;
- the trust policy (no mark, Azure DevOps hosts, look-alike hosts, http, an organization limit, reading the real `Zone.Identifier` stream);
- 17 package rejections, from not-a-zip to bomb;
- a damaged file found during extraction, with nothing written outside staging;
- first install then upgrade (the previous version kept, the data folder byte-for-byte unchanged);
- refusals: same, older, unordered, untrusted, other origin, schema-losing, and data newer than the candidate;
- a running instance stopped through its stop request;
- an instance that does not stop (the request withdrawn, the version unchanged);
- interrupted before and after activation;
- rollback allowed or refused by migrations;
- repeated upgrades keeping two versions;
- the identical instance lock name in launcher and updater.

**Launcher + updater: 74 passed, 0 failed.**

## 12. Real Windows acceptance

**Packages.** Two real tester packages were built from `14021365` with CI build identities (`TF_BUILD`, `BUILD_BUILDID` 1001 and 1002):

| Package | SHA-256 |
| --- | --- |
| A = `1.0.0-ci.1001` | `7f7ea69a…` |
| B = `1.0.0-ci.1002` | `031f26b8…` |

- The pinned PostgreSQL/k6 files were reused from the verified 416921 package, checked against its manifest; nothing was downloaded.
- Both ZIPs were given a Mark of the Web naming `dev.azure.com/…` and `*.visualstudio.com`, which simulates the browser download.
- Everything ran in isolated installation and data folders under `%TEMP%`, with the real executables.

| Step | Result |
| --- | --- |
| 1. First install of A with A's own `Update-BirkNext.exe` | 2 746 files checked; A activated and started; API `buildVersion 1.0.0-ci.1001` |
| 2. Data in A | Synthetic LendoPlus imported through the browser: workspace `Auto_20261119_114032`, 4 artifacts |
| 3. Upgrade A → B with the stable updater copy **while A runs** | B unpacked and verified **before** A was stopped. A stopped through its stop request. B activated and started. API `buildVersion 1.0.0-ci.1002`. The **same workspace id, name, save status and 4 artifacts** were read back from B. The new launcher wrote `backups/birknext-20261119-114229-pre-upgrade.dump` |
| 4a. Corrupt ZIP (truncated) | Refused ("not a ZIP archive, or it is damaged"); active version unchanged |
| 4b. Same bytes without Mark of the Web | Refused ("not downloaded from the approved Azure DevOps pipeline"); unchanged |
| 4c. Older package (A while B is active) | Refused (downgrade); unchanged |
| 4d. Same package (B) | Refused ("already installed"); unchanged |
| 5a. `--rollback` (B added no migrations) | B stopped; A active again, "Your data is unchanged" |
| 5b. Upgrade to B again after the rollback | **Defect found:** refused with "That version folder is in use", because B's folder was the recorded previous version. **Fixed:** a previous-version folder is re-verified file by file against the package's SHA-256 list and reused; a changed folder is refused. Covered by `UpdaterTests.Rollback_is_allowed_only_when_the_database_was_not_changed` |
| 6. Stop through the active version's launcher | Nothing from the test package left running; installations = current + previous only |

**Not run on real Windows:**

- **Failure injection during staging or activation** (killing the updater mid-way). The journal recovery is covered by unit tests with the journal states those kills leave behind.
- **A migration failure.** No pair of real packages with different migrations exists yet.
- **A non-admin or AVD profile, a read-only folder, a locked file, or a path with spaces or non-ASCII characters.**
- **A Start menu shortcut check.** Shortcuts are written only for the default installation root, and the acceptance run used an isolated root.

## 13. Known limitations

- **No publisher authentication** until the pipeline signs (§4).
- **Pre-updater packages can't be installed with the updater.** Packages from before `14021365` have no `updateContract`, so the first update
  is done with the new package's own `Update-BirkNext.exe`.
- **Managed PCs.** Enterprise or AVD policies (AppLocker, WDAC) may block executables under `%LOCALAPPDATA%`; that is decided by IT, not
  bypassed.
- **Manual first update.** The first update has no Start menu shortcut yet: the tester runs the `Update-BirkNext.exe` they extracted.
- **No pipeline upgrade gate.** The pipeline does not yet run an upgrade from the previous artifact (§9).
