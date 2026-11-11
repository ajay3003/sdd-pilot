# BirkNext — Application versioning & release traceability: audit (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `b92c5215`.
- **Method:** read-only audit of HEAD. Paths below are relative to the repository root.
- **Related documents:**
  - implementation: `../hardening/birknext-application-versioning-implementation.md`
  - model: `../architecture/birknext-build-version-model.md`
  - operations: `../operations/birknext-release-provenance.md`
  - acceptance: `birknext-application-versioning-acceptance.md`
  - gaps: `birknext-application-versioning-gap-register.md`

## 1. The hypothesis

> "BirkNext appears to display a version number in System Settings, but the published pipeline artifact does not visibly expose a corresponding version identity."

**Confirmed.**
- **The number shown:** System Settings showed `1.0.0`. That value is the .NET SDK default: no project sets `Version`/`VersionPrefix`/`InformationalVersion`/`AssemblyVersion`/`FileVersion`, and there is no Directory.Build.props, global.json, GitVersion, MinVer or Nerdbank configuration.
- **Measured on a baseline build:** `ProductVersion=1.0.0`, `FileVersion=1.0.0.0`, AssemblyName version `1.0.0.0`.
- **The pipeline artifact:** published as `birknext-tester-package[-candidate]`, with no version in its name. Inside, `package-manifest.json` carries only `sourceCommit` (from `Build.SourceVersion`).
- **Not passed anywhere:** no `Build.BuildId` or `Build.BuildNumber`.

## 2. Findings

| Area | Finding | Evidence |
|---|---|---|
| Application version source | None; SDK default 1.0.0 (a product version, but accidental) | Projects; baseline `FileVersionInfo` |
| System Settings value | `AdminService.BuildSettings` used the raw `AssemblyInformationalVersion`. `BuildNumber` and `CommitSha` existed in the DTO but were never filled, so the UI hid them. | `AdminService.cs:109-114,162-168`; `SystemSettings.razor:183-202` |
| Commit stamp | `SourceCommit` AssemblyMetadata (backend only), via `git rev-parse` on the agent or `-p:BirkNextSourceCommit`. The pipeline passed neither. | `BirkNext.Api.csproj:240-258` (baseline) |
| Duplicate readers | `FullBackupService.ApplicationIdentity`, `AdminService`, `EnvironmentDiagnosticsService`, `SkoleNaervaerJourneyPack.BackendBuild`, `PerformanceTestExecutionService` (`GetName().Version` = 1.0.0.0), `GeneralPageService` (config keys that do not exist) | Audit sweep |
| Frontend | Blazor WebAssembly, shipped as webcil (`BirkNext.Web.wasm`). It had no version, commit or build identity, and could not detect a backend/frontend mismatch. | `BirkNext.Web.csproj`; published `_framework` |
| Pipeline | `azure-pipelines.yml`: two jobs. **BuildAndPackage** (ubuntu): restore, preflight tests, migration check, build, publish, package, package manifest, clean-copy validation, artifact `birknext-tester-package-candidate`. **SmokeTestTesterPackage** (windows): validate, `start-local -ValidateOnly`, artifact `birknext-tester-package`. No deployment stage; the artifact is the deliverable (the tester downloads it). | `azure-pipelines.yml` |
| Pipeline naming | `name: qa-review-studio-ci` is a literal, so `Build.BuildNumber` is not a reliable unique id. Uniqueness has to come from `Build.BuildId`. | `azure-pipelines.yml:1` |
| Package integrity | `package-manifest.json` format 1 listed files without hashes (only missing files detectable). No checksum, signature or SBOM for the package. | `scripts/validate-package.ps1` |
| Edge Companion | Has its own version (`manifest.json` 0.1.0) and a content-hash `buildId` (`build.mjs` → `build-info.json`), bundled into the backend output (`BrowserCompanion/`). It reports `extensionVersion` and `buildId` on heartbeat. An exact-match check exists only for Dedicated Edge. There is no protocol-version handshake. | `browser-companion/*`; `DedicatedCompanionProvisioner.cs:171-206` |
| Database | Four EF Core contexts: App, DeliveryReporting, CanonicalInterpretation, SnapshotRegistry. Migration state was not shown as compatibility. | `CodeMigrations.cs` |
| Execution Evidence | Execution manifests (Artifact Versioning, `5044ae3b`) recorded `Version` + `SourceCommit`. The version was 1.0.0 and carried no channel or run id. | `ExecutionManifestContracts.cs` |
| Deployment provenance | None in the repository: no deployment stage, no deployment records. | `azure-pipelines.yml`; code search |
| GitHub workflow | `.github/workflows/qa-review-studio-ci.yml` mirrors the flow with `GITHUB_SHA`. | — |
| Global `.gitignore` | The user-level ignore contains `[Bb]uild/`, so files under `AIAssisted/build/` would never be committed. The shared files therefore live in `AIAssisted/versioning/`. | `~/.gitignore_global:19` |

## 3. Pipeline risks (recorded; changed only where in scope)

| Risk | Status in this work |
|---|---|
| No version stamping in build/publish | **Fixed:** one resolved identity is passed to every build and publish |
| Fixed artifact names without a version | **Fixed without breaking consumers:** names kept, plus a versioned archive `BirkNext-<build version>.zip` with an external `.sha256` in a new artifact `birknext-tester-package-archive` |
| No file hashes in the package manifest | **Fixed:** format 2 with per-file SHA-256, verified by the validator |
| Branch name injection | **Mitigated:** the branch is read from the environment, never interpolated into a shell, and restricted to `[A-Za-z0-9._/-]` (max 200) |
| Triggers on `'*'` for CI and PR; floating `ubuntu-latest`/`windows-latest`/`8.0.x`; no tests in CI; `.env` defaults packaged; `dotnet-serve` installed unpinned at start | **Not changed** (outside versioning; gap register AP-G5) |
| GitHub workflow without `permissions:` | **Not changed** (AP-G6) |
