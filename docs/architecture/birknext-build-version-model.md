# BirkNext — Build version model (2026-11-11)

## 1. Separate identities

These identities are never conflated.

| Identity | Example | Source of truth |
|---|---|---|
| **Application version** (product) | `1.0.0` | `AIAssisted/versioning/BirkNext.Version.props`, `BirkNextApplicationVersion`; the only place it is written |
| **Build version** | CI `1.0.0-ci.152` · Release `1.0.0` · Local `1.0.0-local` · Unknown `1.0.0-unknown` | Derived from the application version and the channel |
| **Source revision** | full 40-character commit | CI: `Build.SourceVersion`. Local: `git rev-parse HEAD` plus a dirty flag from `git status --porcelain`. Otherwise `unknown`. |
| **CI build identity** | run id `152`, number `20261111.1` | `Build.BuildId` (unique per run) and `Build.BuildNumber` (display only; not unique with the current literal `name:`) |
| **Artifact identity** | `BirkNext-1.0.0-ci.152.zip` + its SHA-256 | The archive name comes from `build-manifest.json`. The digest sits in an external `.sha256` file, computed after the archive is final. |
| **Deployment identity** | — | Not present in this repository (no deployment stage). See the operations document. |
| **Edge Companion version** | `0.1.0` + content-hash build id | `browser-companion/manifest.json` + `build-info.json`; independent of BirkNext's version |
| **Managed artifacts** (contracts, tests, Test Data) | `children-api@v3`, `contract-test:{id}@r2` | Artifact Versioning (`birknext-artifact-versioning-model.md`); a different domain |

An execution manifest records both kinds of identity, separately:
- `Build`: which BirkNext build ran the test;
- `Dependencies`: which artifact versions it ran against.

## 2. Policy

- **Application version:** SemVer core `MAJOR.MINOR.PATCH`, changed by a reviewed commit to `BirkNext.Version.props` when a release is planned.
  - MAJOR: incompatible changes (data or package format).
  - MINOR: features.
  - PATCH: fixes.
  - Hotfix: PATCH+1 on the release line.
  - The version is never incremented automatically by a build. No tag is created.
- **Channels:**
  - **Local:** a developer build. It is never official, has no CI number, and states its commit and dirty state when git is available. Without git the commit is `unknown`.
  - **CI:** an Azure DevOps run with a complete identity: `TF_BUILD=True`, a numeric `BUILD_BUILDID` and a 40-character `BUILD_SOURCEVERSION`, or the resolver fails.
  - **Release:** `resolve-build-identity.ps1 -Release`. It requires a complete CI identity from a clean checkout. The build version is the bare application version.
  - **Release candidates:** CI builds (`1.0.0-ci.N`) of the intended version. No separate channel exists yet (gap AP-G3).
  - **Unknown:** a CI environment (`TF_BUILD`, `GITHUB_ACTIONS`, `CI`) that did not run the resolver. It is never labelled Local or CI.
- **Assembly fields:**

  | Field | Value | Why |
  |---|---|---|
  | `Version` | the application version | — |
  | `AssemblyVersion` | `MAJOR.MINOR.0.0` | Binding stays stable across patches and builds; 1.0.0 gives 1.0.0.0, unchanged from before |
  | `FileVersion` | `MAJOR.MINOR.PATCH.(BuildId mod 65535)`, `.0` locally | — |
  | `InformationalVersion` | `<build version>+<commit>[.dirty]` | — |

  The SDK's own source-revision suffix is disabled, so the commit appears only once.
- **Assembly metadata** (both components): `BirkNextIdentitySchema` (`birknext-build-identity/1`), `BirkNextBuildChannel`, `BirkNextBuildVersion`, `BirkNextBuildId`, `BirkNextBuildNumber`, `SourceCommit`, `BirkNextSourceBranch`, `BirkNextSourceDirty`, `BirkNextBuildTimestamp` (CI only, for deterministic local builds), `BirkNextBuildConfiguration`, `BirkNextPipelineDefinition`.

## 3. Resolution: once per run

```
versioning/BirkNext.Version.props ─┐
Azure DevOps variables (CI) ───────┼─► scripts/resolve-build-identity.ps1 ─► build-identity.json + build-identity.props
git (local) ───────────────────────┘                                              │
                       dotnet build/publish -p:BirkNextBuildIdentityProps=… ◄─────┘  (backend AND frontend, same file)
                                         │
     compiled BirkNext.Api.dll, BirkNext.Web.wasm (+ wwwroot/build-info.json)
                                         │
     scripts/write-build-manifest.ps1 ─► build-manifest.json (identity + versions READ from the compiled components; fails on disagreement)
                                         │
     validate-package.ps1 -CreateManifest ─► package-manifest.json (format 2: every file + SHA-256)
                                         │
     package-archive.ps1 ─► BirkNext-<build version>.zip + .zip.sha256 (after the archive is final)
```

- **Builds without a props file** (developers, tests) resolve the identity in `versioning/BirkNext.BuildIdentity.targets`, which applies the same rules for the Local and Unknown channels.
- **Two implementations of local resolution:**
  - MSBuild handles each build;
  - the PowerShell module handles packaging.
  - Both are tested against the same rules: the module by `scripts/tests/BirkNextBuildIdentity.Tests.ps1`, the targets by the backend and frontend identity tests and by acceptance.

## 4. Reading the identity at runtime

- **Backend:** `BuildInfoService.Current` (shared `BuildIdentityReader`) is the single provider. `AdminService` (System Settings), `FullBackupService.ApplicationIdentity` (backups and packages) and `ExecutionManifestService` (execution evidence) all read it.
- **Contradictions are reported, never resolved silently.** They are listed in `Problems` with provenance Inconsistent:
  - the informational version disagrees with the metadata;
  - a CI or Release build has no run id;
  - the commit is malformed.
- **Frontend:** `FrontendBuildIdentity.Current` reads the frontend's own compiled assembly. It is never taken from the backend.
- **API:** `GET api/system/build-info`, behind the local-session gate (unauthenticated requests get 401). It returns:
  - the backend identity;
  - the packaged manifest check: Consistent, Inconsistent, Invalid, or NotPackaged for a source run;
  - the Companion's bundled and connected versions;
  - per-context migration state (Current / PendingMigrations / AheadOfBuild / Unknown). Reading it never migrates anything.
  - It returns no paths and no configuration.

## 5. Compatibility

**Frontend vs backend** (`BuildCompatibilityRules.Compare`): BirkNext ships both components from one build in one package.

| Result | When |
|---|---|
| **Matched** | Same informational version, channel and run id |
| **Mismatch** | Both identities known and different |
| **NotAssessed** | An identity is unknown or inconsistent, or either side was built from uncommitted changes (the same commit then does not prove the same source) |
| **CompatibleDifferentBuilds** | Exists in the contract, but no rule produces it (none is defined) |

**Edge Companion:** **compatibility is not assessed**, because there is no protocol-version handshake. The UI states whether a connected Companion is the bundled build. The Dedicated Edge exact-match check is unchanged.

## 6. Package manifest vs archive digest

- `build-manifest.json` is inside the package. It names the archive but cannot contain the archive's digest, because that would be circular.
- The digest is in `BirkNext-<build version>.zip.sha256`, next to the archive, and is computed after the archive is final.
- The archive is never rewritten: creation refuses an existing file and removes a partial one.
- **A checksum proves integrity, not who published the file.** There is no signature (gap AP-G2).
