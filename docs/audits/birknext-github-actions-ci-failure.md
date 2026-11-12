# GitHub Actions CI failure — audit and repair (2026-11-12)

Workflow: `.github/workflows/qa-review-studio-ci.yml` (name `qa-review-studio-ci`, the only GitHub workflow), repository `ajay3003/sdd-pilot` (remote `origin`), branch `008-traceability-first`. Baseline HEAD `e81a00b6`, identical to `origin/008-traceability-first`. Working tree: only the excluded files (`appsettings.Local.json`, `AIAssisted/testdata/`, other sessions' audit documents) were modified; none of them were touched.

## Workflow inventory

| | `build` | `smoke-test-tester-package` |
|---|---|---|
| Runner | `ubuntu-latest` | `windows-latest` (needs `build`) |
| Triggers | push to main, feature/\*\*, bugfix/\*\*, 008-traceability-first; PR to main, feature/\*\*, bugfix/\*\*; manual | |
| Toolchain | `actions/setup-dotnet@v4` 8.0.x; pwsh (image) | setup-dotnet 8.0.x; Windows PowerShell |
| Steps | restore → preflight tests + migration check → build backend sln → stale-CSS check → restore/build frontend sln → publish API + Web → package + `validate-package.ps1 -CreateManifest` + clean-copy validation → upload candidate | download candidate → `validate-package.ps1` + `start-local.ps1 -ValidateOnly -SkipContainers` in a clean folder → (re-upload) |
| Tests | none, by design (same as Azure DevOps) | |
| Secrets / caches / Node | none / none / none | |

## Actual failure evidence

The full job logs need repository admin rights (anonymous API: `403 Must have admin rights`); the GitHub CLI is not installed. The evidence below comes from the public Actions API (runs, jobs, steps) and the check-run annotations, all read-only. **No run was triggered.**

- Run history: 600 runs read. The last success was `96cced6a` (2026-11-04 14:46). All 119 later completed runs failed.
- Failing step per run (binary search over the 119 failures; the step changed twice):

| Period | Example run / commit | Failing job · step | Evidence |
|---|---|---|---|
| 2026-11-04 → ~11-08 | 41860875336 · `1bcd8c62` | build · *Package local startup support and verify runnable output* | annotation: exit code 2. **Not current**: from at least `110afb72` on, the build job passed, up to `5a4f7605` |
| ~2026-11-08 → 11-12 21:13 | 43308067034 · `5a4f7605` (build job **success**) | smoke-test-tester-package · *Validate the package and the launcher preflight from a clean folder* | annotation: exit code 1 (line 25) |
| 2026-11-12 21:41 → HEAD | 43312807849 · `a256e1d7`, 43313551228 · `e81a00b6` | build · *Build backend solution* | annotations: Razor-generated C# errors in **unchanged** files, e.g. `DeliveryAzureDevOpsImport.razor(5)`: "A compilation unit cannot directly contain members such as fields, methods or properties"; `SourceManagement.razor(566)`, `QualityReview.razor(2936)`, `ActivityCenter.razor(125)`, `PerformanceRunResults.razor(239)`, `PerformanceExecutionProgress.razor(79)`, `Program.cs(14)`: "Only one compilation unit can have top-level statements" |

Runner: GitHub-hosted, labels `ubuntu-latest` / `windows-latest`. The image version is printed only in the restricted log.

## Root causes

### 1. Build failure (current first failure) — cause VERIFIED, toolchain reproduction NOT VERIFIED

- **Verified:** none of the files with errors changed between `5a4f7605`, whose build passed at 21:13, and `a256e1d7`, which failed at 21:41. That diff touches 13 files and none of them has an error.
- **Verified:** the runner-image release notes for `ubuntu24/20261111.370` and `ubuntu26/20261111.196` (published 2026-11-11 23:04, rolled out gradually) list **".NET Core SDK 11.0.100" under *Added***. Before that image, the newest SDK was 10.0.403.
- **Verified:** the workflow has no `global.json`, and `setup-dotnet` installs 8.0.x *beside* the image's SDKs. The `dotnet` host then uses the newest SDK. Local proof: with no pin, `dotnet --version` returned 9.0.320; with the pin step pointed at the older 9.0.204, it used 9.0.204.
- **Conclusion:** on the new image, GitHub compiles this net8.0 Blazor project with SDK 11. Its Razor output does not compile, so the failure appeared without any source change.
- **Not verified:** that SDK 11 produces exactly these errors. Reproducing it needs an SDK 11 download, which this task did not approve. The fix makes the question moot for CI, because CI no longer uses SDK 11.
- **Fix:** a step "Pin the .NET 8 SDK for this job" writes `global.json` with `steps.setup-dotnet.outputs.dotnet-version` and `rollForward: disable`. It refuses to overwrite a committed `global.json`, rejects a non-8.0 version, and fails if `dotnet --version` does not match. No repository-wide `global.json` was added: developer machines may have only 9.x SDKs (this one does), and a committed 8.0 pin would break their builds.
- **Follow-up (not done):** whether the code builds with SDK 11 at all matters before any SDK move.

### 2. Smoke-test failure (masked since `a256e1d7`, would come back after fix 1) — mechanism REPRODUCED, CI cause strongly supported

- **Verified:** `actions/upload-artifact@v4` (≥ 4.4) excludes hidden files and folders unless `include-hidden-files: true` is set. The candidate contains:
  - `AIAssisted/.env` and `AIAssisted/.env.example`;
  - `AIAssisted/backend/.playwright/` (337 files: the Playwright driver);
  - `AIAssisted/frontend/.graphqlrc.json` and `wwwroot/.gitkeep`.
- **Reproduced locally (scratch only):**
  - I built the package exactly as the workflow does from the Release publish of the current tree. `validate-package.ps1` and `start-local.ps1 -ValidateOnly -SkipContainers` then **pass** (exit 0, 848 files, 83 migrations).
  - The same copy with hidden entries removed, as the upload does, **fails** with exit 1: "Local environment defaults is missing (AIAssisted/.env); 341 packaged file(s) are missing, e.g. AIAssisted/.env, AIAssisted/.env.example, AIAssisted/backend/.playwright/node/LICENSE". The smoke step turns that into `throw` → exit code 1, which matches the annotation.
- Azure DevOps' `PublishPipelineArtifact@1` keeps dotfiles, which is why the same smoke job works there.
- This was also a functional defect: the GitHub artifact lacked the backend's Playwright driver.
- **Not verified:** that the smoke job fails *only* for this reason. The restricted log was not readable.
- **Fix:** `include-hidden-files: true` and `if-no-files-found: error` on the candidate upload. The included hidden files are the checked-in defaults and runtime files that Azure DevOps already publishes; no secrets.

### 3. Packaging-step failure (2026-11-04 → ~11-08) — historical, not current

From at least `110afb72` on, the build job passed this step. No change was made for it.

## Other changes

- `permissions: contents: read` at workflow level (least privilege). Artifacts use the run's own token.
- **Artifact contract aligned with Azure DevOps:** the smoke job no longer re-uploads the candidate as `birknext-tester-package`. That was a byte-identical copy and the artifact testers mistook for the tester download, and Azure DevOps already stopped publishing it (see `birknext-tester-artifact-inventory.md`). GitHub now publishes only the internal `birknext-tester-package-candidate`.
- Anyone who downloaded `birknext-tester-package` from GitHub should use Azure DevOps' `BirkNext-Tester-Windows-x64`.

## Differences from Azure DevOps (kept deliberately)

| Area | Azure DevOps | GitHub Actions | Decision |
|---|---|---|---|
| SDK isolation | `UseDotNet@2` (own tool directory) | `setup-dotnet` + new pin step | equivalent now |
| Build identity / `build-manifest.json` / `-RequireBuildManifest` | yes (`resolve-build-identity.ps1`, `write-build-manifest.ps1`) | no | **Gap, not fixed:** the resolver only knows Azure DevOps variables (`BUILD_BUILDID`, …). GitHub provenance support would be new work, not a fix |
| `BirkNextBuildIdentity.psm1`, `package-archive.ps1` in the package | yes | no | only needed with a build manifest (`validate-package.ps1` checks provenance only when the manifest exists) |
| Developer package archive (`birknext-developer-package-archive`) | yes | no | GitHub is CI validation only |
| Windows Tester Package `BirkNext-Tester-Windows-x64` (`build-tester-package.ps1`) | yes | no | intentionally **not** added: one tester distribution, from one pipeline. Adding it would need pinned native downloads on GitHub runners |
| Tests | not run in either pipeline | | unchanged; no quality gate removed |

**Shared build contract:** both pipelines call the same repository scripts: `check-migrations.ps1`, `BirkNextPreflight.Tests.ps1`, `validate-package.ps1` and `start-local.ps1`. The package-assembly shell block is still duplicated in the two YAML files. Extracting it into one script would change the working Azure DevOps job, so it is listed as a follow-up and not done here.

## Local validation

- **YAML:** parses (PyYAML). Actions used: `actions/checkout@v4`, `actions/setup-dotnet@v4`, `actions/upload-artifact@v4`, `actions/download-artifact@v4`, all unchanged versions. `include-hidden-files` exists from upload-artifact v4.4.
- **Pin step:** the exact `run:` block, extracted from the YAML, was run in bash in a scratch folder:
  - a non-8.0 version is rejected (exit 1);
  - an SDK that isn't installed fails (exit 145);
  - an installed SDK is pinned and verified (exit 0);
  - an existing `global.json` is refused (exit 1);
  - pinning the older SDK overrides "newest wins".
- **Smoke reproduction:** passes with hidden files, fails without them (see above).
- **Not run:** the backend/frontend build with the 8.0 SDK, because no 8.0 SDK is installed locally. Azure DevOps builds the same sources with 8.0.x.
- Script: `scratchpad/gha/repro-smoke.ps1`; logs in `%TEMP%\bngha-r3`.
- Note: pwsh 7 is not installed locally, so the two build-side script steps ran under Windows PowerShell 5.1. CI runs them under pwsh on Linux.

## Azure DevOps compatibility

`azure-pipelines.yml` is **not modified** in this change (`git diff` shows only the GitHub workflow and this report). Azure DevOps behaviour is therefore unchanged.

Suspected but not verified: Azure DevOps is not exposed to the SDK 11 image change, because `UseDotNet@2` isolates its SDK. Confirm in the next run's "Confirm installed .NET SDK" (`dotnet --info`) step. No Azure DevOps pipeline was triggered.

## Remaining GitHub-hosted verification (NOT VERIFIED)

On the next push or manual run, check that:

1. the "Pin the .NET 8 SDK for this job" step reports an 8.0.4xx SDK and the backend and frontend builds pass;
2. the candidate artifact contains `AIAssisted/.env` and `AIAssisted/backend/.playwright/`;
3. the smoke job passes.

GitHub Actions is **not** claimed to be passing.

Known annotations that are not failures:

- the Node.js 20 deprecation of the v4 actions (forced onto Node 24);
- the `ubuntu-latest` → Ubuntu 26 migration notice;
- nullable warnings.

## Files changed

- `.github/workflows/qa-review-studio-ci.yml`
- `docs/audits/birknext-github-actions-ci-failure.md`

Final HEAD and the local commit are listed in the commit that adds this report. Not pushed.
