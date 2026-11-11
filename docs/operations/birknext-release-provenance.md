# BirkNext — Release provenance: operations guide (2026-11-11)

This guide explains how to tell exactly which BirkNext build a tester has, where it came from, and whether it was altered.
Model: `../architecture/birknext-build-version-model.md`.

## 1. Changing the application version

1. Edit `AIAssisted/versioning/BirkNext.Version.props` (`BirkNextApplicationVersion`, `MAJOR.MINOR.PATCH`) in a reviewed commit.
2. Nothing else needs changing. System Settings, the sidebar, the package name and the binaries all follow the props file.
3. Do not create Git tags or releases as part of this change; tagging is a separate, explicit decision.

## 2. What the pipeline produces (`azure-pipelines.yml`, local change — not yet run in Azure DevOps)

| Job | Step | Output |
|---|---|---|
| BuildAndPackage | Build identity tests (synthetic variables), then `resolve-build-identity.ps1` | `$(Build.ArtifactStagingDirectory)/identity/build-identity.{json,props}`; pipeline variables `BirkNextBuildVersion` and `BirkNextArtifactName` |
| BuildAndPackage | Backend and frontend build and publish with `-p:BirkNextBuildIdentityProps=…` | Stamped `BirkNext.Api.dll`, `BirkNext.Web.wasm`, `wwwroot/build-info.json` |
| BuildAndPackage | `write-build-manifest.ps1`, then `validate-package.ps1 -CreateManifest`, then clean-copy validation with `-RequireBuildManifest` | `build-manifest.json` and `package-manifest.json` (format 2, per-file SHA-256) inside the package |
| BuildAndPackage | Publish | Artifact `birknext-tester-package-candidate` (name unchanged) |
| SmokeTestTesterPackage | Validation with `-RequireBuildManifest`, launcher preflight | Artifact `birknext-tester-package` (name unchanged) |
| SmokeTestTesterPackage | `package-archive.ps1` create, then `-Verify -ExtractTo` | Artifact `birknext-tester-package-archive`: `BirkNext-<build version>.zip` + `.zip.sha256` |

- **Unchanged consumers:** both existing artifact names stay as they are, so any download or consumer keeps working.
- **New:** the versioned archive is the file to hand out.

## 3. Verifying a package you received

```powershell
# The archive against its checksum, then a fresh extraction: every file hash and the build provenance.
powershell -ExecutionPolicy Bypass -File scripts\package-archive.ps1 -Verify -Archive BirkNext-1.0.0-ci.152.zip -ExtractTo C:\BirkNext-check
```

Or, for an already extracted package: `scripts\validate-package.ps1 -RequireBuildManifest`. It checks:
- every file against `package-manifest.json`;
- `build-manifest.json` against the compiled backend (version resource), `wwwroot/build-info.json`, the WebAssembly bytes and the bundled Companion.

**The file name alone proves nothing:** a file called `BirkNext-1.0.0` is only that build if these checks pass.

**A matching checksum proves the bytes are unchanged, not who published them.** Obtain the `.sha256` from the pipeline run itself.

## 4. Identifying the running build

| Where | What it shows |
|---|---|
| System Settings → General → **Application information** | Version, channel (Local / CI build / Release / Unknown build), pipeline run, commit, frontend and backend builds, frontend/backend match, package manifest match, Edge Companion versions, database migration state. **Copy version information** puts the facts on the clipboard. |
| Sidebar footer | The running frontend's version and build |
| `GET api/system/build-info` (local session) | The same facts as JSON |
| Backend log, at startup | One line: `BirkNext <informational version> (channel, run, provenance)` |
| Execution evidence | Each run's manifest records the BirkNext build that ran it (`Build`: version, informational version, channel, run, commit) |

**A local build always says so.** "A local build: not an official CI build or release" appears, and the commit is shown as unknown when there is no git metadata.

## 5. Deployment provenance

- **What exists:** this repository has no deployment stage. Testers download the artifact. There is therefore no deployment record to extend, and none was invented.
- **If a deployment stage is added:**
  - promote the same immutable archive;
  - record the environment, time, pipeline run, archive name and its SHA-256 outside the package;
  - never rewrite `build-manifest.json`.

## 6. Troubleshooting

| Message | Meaning |
|---|---|
| `BUILD_BUILDID is missing or not numeric` | A CI build without its run id. Fix the pipeline; never set a value by hand. |
| `Unknown build` in System Settings | Built in CI without `resolve-build-identity.ps1`, or a component built without the identity targets |
| `Different builds` | The frontend and backend come from different builds. Use one package. |
| `Differs from the running build` (package manifest) | The binaries are not the ones the manifest describes. Get a fresh archive. |
| `packaged file(s) differ from what the build produced` | A packaged file was changed after the build |
| `Migrations pending` / `Newer than this build` | The database is behind or ahead of this build's migrations. Nothing is migrated by looking. |
