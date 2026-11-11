# BirkNext — Application versioning & release traceability: gap register (2026-11-11)

- **Audit:** `birknext-application-versioning-audit.md`
- **Implementation:** `../hardening/birknext-application-versioning-implementation.md`
- **Acceptance:** `birknext-application-versioning-acceptance.md`

Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN · NOT SUPPORTED.

## Closed

| ID | Gap | Status | Evidence |
|---|---|---|---|
| AP-C1 | No defined application version (SDK default 1.0.0) | PASS | `versioning/BirkNext.Version.props`, the only source; script tests, backend and frontend tests; oracle CI-01 |
| AP-C2 | CI builds had no unique identity | PASS | `Build.BuildId` resolved once; run 152 in the DLL, the wasm, build-info.json and the manifest (CI-02–CI-07) |
| AP-C3 | Binaries carried no build metadata | PASS | InformationalVersion, FileVersion `1.0.0.152`, AssemblyMetadata (CI-02–CI-05) |
| AP-C4 | The frontend did not know its own build | PASS | The compiled frontend identity; the wasm carries it (CI-05); browser BV-11/BV-14 |
| AP-C5 | System Settings showed version only (build and commit never set) | PASS | Application information card; `AdminService` fills build and commit; BV-10–BV-16 |
| AP-C6 | The artifact had no visible version | PASS | `BirkNext-<build version>.zip` + `.sha256` (CI-15–CI-18); stable artifact names kept |
| AP-C7 | Packaged files could be altered undetected | PASS | Package manifest format 2 + build provenance (TP-01–TP-05) |
| AP-C8 | No frontend/backend mismatch detection | PASS | `BuildCompatibilityRules`; BV-20 (Mismatch), BV-11 (Matched), BV-31 (NotAssessed) |
| AP-C9 | Execution evidence recorded version 1.0.0 only | PASS | `ManifestBuild` channel, run and informational version (ExecutionManifestTests) |
| AP-C10 | Five independent version readers | PASS (backend paths in scope) | Backups, packages, System Settings and execution evidence read `BuildInfoService.Current`; see AP-G7 for the rest |

## Defects found by acceptance and fixed

| ID | Defect | Found by | Fix |
|---|---|---|---|
| AP-D1 | `AIAssisted/build/` is ignored by the user-level global `.gitignore` (`[Bb]uild/`): the version files would never be committed | sync to the scratch tree | Moved to `AIAssisted/versioning/` |
| AP-D2 | Windows PowerShell 5.1 could not create the archive (`System.IO.Compression` not loaded), and the failed attempt left a 0-byte "archive" that later counted as existing | round trip RT-02 | Assembly loaded; a partial archive is removed |
| AP-D3 | `$archive` collided with the `-Archive` parameter (PowerShell names are case-insensitive) | round trip | Renamed |
| AP-D4 | Hashing failed for package paths over 260 characters on Windows PowerShell 5.1 (validator crash, not a "path too long" message) | second CI build in a longer folder | Long-path-aware hashing in the validator and the archive writer |
| AP-D5 | The frontend/backend explanation line overflowed at 768 px | browser BV-L1 | `box-sizing: border-box` |

## Remaining gaps

| ID | Gap | Status | Note |
|---|---|---|---|
| AP-G1 | The Azure DevOps pipeline was not executed | NOT RUN | Static YAML parse + script tests + a full local emulation with synthetic variables only. A real run must confirm `pwsh` behaviour on the Linux agent (the scripts ran on Windows PowerShell 5.1 here). |
| AP-G2 | Package authenticity | NOT SUPPORTED | Checksums prove integrity, not the publisher. Signing needs a key-management decision. |
| AP-G3 | No release-candidate channel | NOT SUPPORTED | RCs are CI builds of the intended version; `-Release` exists but no pipeline uses it yet. |
| AP-G4 | No deployment records | NOT SUPPORTED (no deployment stage) | Testers download the artifact; promotion rules are documented for when a deployment stage exists. |
| AP-G5 | Other pipeline hardening (triggers `'*'`, floating images and SDK, no CI tests, `.env` defaults packaged, unpinned `dotnet-serve`) | NOT RUN (out of scope) | Recorded in the audit. |
| AP-G6 | GitHub workflow | NOT RUN | It builds without the resolver, so its builds report channel **Unknown** (never Local or CI). Mirroring the resolver there is future work. |
| AP-G7 | Remaining independent readers: `EnvironmentDiagnosticsService`, `SkoleNaervaerJourneyPack.BackendBuild`, `PerformanceTestExecutionService` (`GetName().Version` = 1.0.0.0), unused `GeneralPageService` | OPEN | Values are not wrong after this change (same assembly), but they are not yet routed through the provider. |
| AP-G8 | Edge Companion protocol compatibility | NOT SUPPORTED | No protocol-version handshake exists, so "Compatibility not assessed" is shown; the Companion's version is manual (`manifest.json`). |
| AP-G9 | Local git dirty state in the browser | PARTIAL | Verified on the real working tree through MSBuild (commit `b92c5215`, dirty); the browser scenario used the scratch tree, which has no git (commit unknown). |
| AP-G10 | Build number uniqueness | PARTIAL | `name: qa-review-studio-ci` is a literal, so `Build.BuildNumber` is not unique; uniqueness comes from `Build.BuildId`. The build-number format was not changed (that is an Azure DevOps-side decision). |
| AP-G11 | Scratch database `birknext_bv_20261111`; acceptance folders `C:\bn-av-extract`, `C:\bn-av-extract-run1` | OPEN (needs approval) | Kept. A tool guard blocks deleting folders at `C:\` root. |
