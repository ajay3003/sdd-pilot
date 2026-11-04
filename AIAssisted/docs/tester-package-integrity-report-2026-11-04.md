# Tester package integrity — report (2026-11-04)

Branch `008-traceability-first`, commit `154e26b3`. Nothing was pushed.

## Problem

The tester downloaded the pipeline artifact `birknext-tester-package` and ran `scripts/start-local.ps1`. It stopped at once with:

> Migrations directory not found at: …\AIAssisted\backend\BirkNext.Api\Data\Migrations

## Root cause (both suspected causes were true together)

- **The artifact is *published* output.** `dotnet publish` puts `BirkNext.Api.dll`, its dependencies and `wwwroot` under `AIAssisted/backend` and `AIAssisted/frontend`. EF Core migrations are **compiled into `BirkNext.Api.dll`**, so a source `Data/Migrations` folder is never part of a published package. It is also not needed: the backend applies migrations from the assembly.
- **The launcher assumed a source checkout.** `start-local.ps1` always ran `scripts/check-migrations.ps1`, a source-file check, before starting anything. On a tester package that check can only fail, and the failure sent a tester to `MIGRATION_GUIDE.md`.
- **Reproduced:** the old launcher (`HEAD~` version) on a locally assembled package produced the same error text.
- **Also found:**
  - The source check only covered `Data/Migrations`. Three other DbContexts (DeliveryReporting, CanonicalInterpretation, SnapshotRegistry) were never checked.
  - The pipeline's package check verified a handful of files, so a filtered artifact could still be published.

## Decision: what the tester artifact is

**A self-contained, runnable, published package.** Everything `start-local.ps1` needs is in it. Migrations are embedded in the assembly, not copied as source.

The launcher now distinguishes:

| Situation | Detection | Check | Message |
|---|---|---|---|
| Source checkout (developer) | `AIAssisted/backend/BirkNext.Api/BirkNext.Api.csproj` exists | `check-migrations.ps1` over every migrations folder | developer message + MIGRATION_GUIDE.md (unchanged) |
| Tester package | `AIAssisted/backend/BirkNext.Api.dll` exists, no source project | `validate-package.ps1` (whole package) | tester message, never the developer guide |
| Old package without the validator | package mode, `scripts/validate-package.ps1` missing | — | "BirkNext tester package is incomplete: scripts\validate-package.ps1 is missing. Download a new tester artifact or contact the build owner." |

## Package validation (`scripts/validate-package.ps1`)

It is static: nothing is started, no database is touched, no network is used. It checks:

- **Launcher:** `scripts/start-local.ps1` and `start-local.bat`.
- **Backend runtime:** `BirkNext.Api.dll`, `.deps.json`, `.runtimeconfig.json` and `appsettings.json`. No `appsettings.Development.json` or `appsettings.Local.json`.
- **Frontend runtime:** `wwwroot/index.html` and a non-empty `wwwroot/_framework`.
- **Configuration templates:** `AIAssisted/docker-compose.yml` and `AIAssisted/.env`.
- **`package-manifest.json`** (written at build time): every file the build packaged is still present. This covers static, data and schema assets, so a filtered or partially extracted artifact is caught.
- **EF Core migrations:** every migration id the source holds (56 across 4 DbContexts) is present in `BirkNext.Api.dll`, where the `[Migration("<id>")]` attribute is compiled in. If one is missing:

  > BirkNext tester package is incomplete. Required EF Core migration files are missing. Download a new tester artifact or contact the build owner.

- **No source-repo-only layout:** no `.csproj` files, which would switch the launcher to source mode.
- **Windows path limit:** if the deepest packaged file exceeds 259 characters, the tester is told to extract to a shorter folder such as `C:\BirkNext`. This was found during verification: a clean copy in a deep folder made Windows PowerShell report a present file as missing. Deep Downloads folders hit the same limit.

`start-local.ps1 -ValidateOnly` runs all preflights (package or source integrity, runnable detection) and exits without starting containers, the backend or the frontend.

## Pipeline: artifact smoke test before publishing

The same flow runs in both `azure-pipelines.yml` and `.github/workflows/qa-review-studio-ci.yml`:

1. Source migration check over every migrations folder, recording the migration ids (`check-migrations.ps1 -ManifestOut`).
2. Build and publish into the package folder; copy the launcher and `validate-package.ps1`.
3. Write `package-manifest.json`; validate an untouched copy in a clean folder (Linux, `pwsh`).
4. Publish only a **candidate** (`birknext-tester-package-candidate`).
5. **Windows smoke job** on a clean machine:
   - downloads the candidate and copies it to a short clean folder;
   - runs `validate-package.ps1`, then `start-local.ps1 -ValidateOnly -SkipContainers`, with Windows PowerShell as a tester would.
6. Only if that passes is **`birknext-tester-package`** published.

A failure at any step means the tester never receives the artifact.

## Verification (local, Windows PowerShell 5.1)

- **`check-migrations.ps1`:** 4 folders, 56 migrations, all consistent. It previously checked 1 folder. A false "2 snapshots" match on a migration named `…ReconcileWorkspaceReviewProgressModelSnapshot` was fixed.
- **Package assembled from current published outputs:**
  - manifest of 842 files and 56 migrations;
  - clean short folder: "BirkNext tester package is complete (842 files, 56 EF Core migrations embedded)";
  - `start-local.ps1 -ValidateOnly -SkipContainers`: package detected, validated, backend DLL and static frontend detected, nothing started.
- **Negative cases**, each with the right tester message and the launcher stopping:

  | Case | Message |
  |---|---|
  | A migration id absent from the DLL | the EF Core migration message |
  | A removed `_framework` file | "package is incomplete" |
  | The validator missing (old package) | the explicit missing-validator message |
  | A deep extraction folder | the path-too-long message |

- **Original failure:** reproduced with the old launcher on the same package.
- **Not verified locally:**
  - the pipeline YAML runs (both files parse; the jobs are defined);
  - `validate-package.ps1` under `pwsh` on Linux (no `pwsh` on this machine; only cross-platform APIs are used, and the pipelines already ran `check-migrations.ps1` with `pwsh`);
  - a full container + backend start on a clean machine (needs a container runtime on the agent). The smoke job covers package integrity and launcher preflight.

## Leftovers for you

The safety check blocked removing two throwaway folders I created at the drive root for the clean-folder test. They hold only copies of published binaries. Please delete them yourself:

- `C:\bnpkg-smoke`
- `C:\bnpkg-old`
