# BirkNext — Application versioning & release traceability: implementation (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `b92c5215`.
- **Related documents:** audit `../audits/birknext-application-versioning-audit.md`; model `../architecture/birknext-build-version-model.md`; operations `../operations/birknext-release-provenance.md`.
- **Scope of changes:** no push, no deployment, no pipeline triggered. Pipeline changes are local repository changes only.

## 1. Version source and stamping

| File | What |
|---|---|
| `AIAssisted/versioning/BirkNext.Version.props` (new) | `BirkNextApplicationVersion` = `1.0.0`: the only product-version source (kept at the value every build has reported) |
| `AIAssisted/versioning/BirkNext.BuildIdentity.targets` (new) | Imported by `BirkNext.Api` and `BirkNext.Web` only. Either imports the run's resolved `build-identity.props` or resolves Local/Unknown from git. Sets Version / AssemblyVersion / FileVersion / InformationalVersion and the `BirkNext*` AssemblyMetadata. Errors on an invalid version or a missing identity file. The frontend publish also writes `wwwroot/build-info.json`. |
| `BirkNext.Api.csproj` | The old `BirkNextResolveSourceCommit` target is replaced by the import (`SourceCommit` metadata kept); links `BuildIdentityContracts.cs` |
| `BirkNext.Web.csproj` | Import, `BirkNextWriteFrontendBuildInfo`, link |

## 2. Scripts (Windows PowerShell 5.1 and pwsh)

| File | What |
|---|---|
| `scripts/BirkNextBuildIdentity.psm1` (new) | Version file reading, SemVer check, safe branch, `Resolve-BirkNextBuildIdentity` (CI / Release / Local / no git, validation, no invented values), identity writing (JSON + XML-escaped props), the manifest builder (component versions read from the compiled files), `Test-BirkNextBuildProvenance` (manifest vs DLL version resource, frontend build-info, WebAssembly bytes, bundled Companion) |
| `scripts/resolve-build-identity.ps1` (new) | Resolves once; writes the identity; sets `BirkNextBuildVersion` / `BirkNextArtifactName` in Azure DevOps; `-Release` |
| `scripts/write-build-manifest.ps1` (new) | `build-manifest.json`, then verifies consistency (fails the package when inconsistent) |
| `scripts/package-archive.ps1` (new) | Versioned ZIP: entries with `/`, fixed order, dotfiles included, never overwritten, a partial file removed. External `.sha256`. `-Verify [-ExtractTo]` re-hashes, extracts and validates. Long paths handled. |
| `scripts/validate-package.ps1` | Package manifest format 2 (per-file SHA-256, long-path-safe hashing); changed files detected; `-RequireBuildManifest` / automatic build-provenance check |
| `scripts/tests/BirkNextBuildIdentity.Tests.ps1` (new) | 29 self-contained checks: version file, SemVer, CI identity, missing/invalid variables, malicious branch, unsafe build number, release, local clean/dirty/no git, CI variables without TF_BUILD, identity files, static YAML checks |
| `azure-pipelines.yml` | Identity tests and resolver before compilation; `-p:BirkNextBuildIdentityProps` on both builds and both publishes; build manifest; hashed package manifest; `-RequireBuildManifest` in both validations; versioned archive + checksum + verified extraction; new artifact `birknext-tester-package-archive`; existing artifact names unchanged |

## 3. Backend

| File | What |
|---|---|
| `shared/BuildIdentityContracts.cs` (new, both projects) | `BuildIdentity`, `BuildIdentityReader` (compiled metadata, contradictions as Problems), `BuildCompatibilityRules`, `ManifestCheck`, `CompanionBuildInfo`, `SchemaState`, `BuildInfoResponse` (namespace `BirkNext.BuildInfo`) |
| `Services/SystemInfo/BuildInfoService.cs` (new) | `Current` (the one provider); packaged-manifest check; bundled/connected Companion; per-context migration state (read-only) |
| `Controllers/SystemInfoController.cs` (new) | `GET api/system/build-info` (no-store; local-session gate) |
| `BrowserCompanionService.ConnectedBuilds()` | Connected Companion versions/build ids (read-only) |
| `AdminService` | System Settings `BuildNumber` / `CommitSha` from the build identity (empty when there is none) |
| `FullBackupService.ApplicationIdentity` | Delegates to the provider (backups and workspace packages) |
| `ExecutionManifestService` + `ManifestBuild` | Channel, run id and informational version of the BirkNext build in every new execution manifest (older manifests unchanged) |
| `Program.cs` | Provider registration; one startup log line with the running build |

## 4. Frontend

| File | What |
|---|---|
| `Services/BuildInfoApiService.cs` (new) | Client; `FrontendBuildIdentity.Current` (this frontend's own assembly) |
| `Components/Admin/ApplicationBuildCard.razor(.css)` (new) | System Settings → General, "Application information": compact summary (version, channel, build, commit, built), component rows (frontend, backend, match, Edge Companion, database, package manifest), not-official note, copy, collapsed technical details, migration table. The client is optional: without it the frontend identity is still shown. |
| `Pages/Admin/SystemSettings.razor` | The card at the top of General (existing sections unchanged) |
| `Layout/NavMenu.razor(.css)` | Sidebar footer "BirkNext <version>" + build / local build (from the compiled frontend) |
| `Program.cs` | Client registration |

## 5. Tests

- **Backend:**
  - `BuildIdentityTests` (6): the real assembly against the props file read as text; CI/Release/inconsistent/identity-less builds from in-memory assemblies; compatibility rules; manifest check; build-info service.
  - `BuildInfoSchemaPostgresTests` (1): Current, then AheadOfBuild on a migrated temporary database.
  - The `ExecutionManifestTests` build-identity assertion.
- **Frontend:** `ApplicationBuildCardTests` (3).
- **Scripts:** `BirkNextBuildIdentity.Tests.ps1` (29).
