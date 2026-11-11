# BirkNext — self-contained Windows tester distribution and runtime profiles: implementation (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `2a8291c2`.
- **Scope:** one implementation serves both queued tasks:
  - the self-contained Windows tester distribution;
  - the DeveloperContainer / TesterNative runtime profiles.
- **Related documents:** audits `../audits/birknext-self-contained-distribution-audit.md` and `../audits/birknext-runtime-profiles-audit.md`; architecture `../architecture/birknext-native-windows-runtime.md` and `../architecture/birknext-runtime-profiles.md`.

## Architectural decisions

1. **One application, two runtime profiles:**
   - the profile is configuration (`BirkNext:RuntimeProfile`), set by the launcher, never inferred, never switched live;
   - DeveloperContainer stays `scripts/start-local.ps1` (unchanged except that it now declares its profile);
   - TesterNative is the new `Start-BirkNext.exe`.
2. **k6 through the existing runtime abstraction:**
   - the native runner implements `IContainerExecutionRuntime`, so the single k6 provider, script generator, parser, thresholds, run store and evidence are reused;
   - what a process cannot do (limits, graceful stop, CA bundle via `SSL_CERT_FILE`, container networks) is reported or refused, never assumed.
3. **Launcher in C#, not PowerShell:**
   - it runs without an execution policy, as a single self-contained executable;
   - its decisions (state classification, configuration, integrity, ports, environment) are unit-tested.
4. **PostgreSQL:**
   - EDB 16.13 Windows binaries (the same version as the developer container), pruned;
   - one cluster per user, owned by the launcher, identified by its system identifier;
   - SCRAM, loopback only, a non-superuser application role, DPAPI-protected credentials;
   - no Windows service, no elevation.
5. **Mutable data outside the package:**
   - `AppDataLocation` (`BIRKNEXT_APP_DATA_DIR`) routes all 15 `App_Data` call sites;
   - development is unchanged: the variable is unset there.
6. **ZIP, not an installer:**
   - nothing is installed, nothing needs administrator rights, and upgrade means extracting to a new folder;
   - the existing `BirkNext-<version>.zip` artifact is kept; the new artifact is `BirkNext-Tester-Windows-x64-<version>.zip`.
7. **Local trusted execution for TesterNative** (product-owner decision, 2026-11-11): the Development-only grant also applies to a TesterNative backend, under the same loopback, Host, Origin and session checks.

## Version pins

`AIAssisted/versioning/native-runtime.lock.json`:

| Component | Version | SHA-256 |
|---|---|---|
| PostgreSQL archive | 16.13-1 | `9b4c7a1b…` |
| k6 archive | 1.0.0 | `0bfbab5c…`, matching the upstream checksums |
| k6 license | — | `45bd5efa…` |
| PostgreSQL license notice | — | `fb33e300…` |

## Files

| Area | Files |
|---|---|
| Shared profile model | `AIAssisted/shared/RuntimeProfileContracts.cs` (new; linked into the backend, frontend and launcher) |
| Native k6 | `Services/ContainerRuntime/NativeProcessExecutionRuntime.cs` (new). `IContainerExecutionRuntime` gains `SharesHostNetwork`/`SharesHostFilesystem`. `K6ContainerProvider.cs`: host paths, target as configured, native status and provenance texts. `PerformanceTestProvider.cs`: `Runner`, `Native` options, `ResolveRunner`. `PerformanceTestExecutionService.cs`: limits item. `PerformanceTestContracts.cs`: `NativeRuntime` id. `Program.cs`: runtime registration by runner |
| Profile in the backend | `Services/SystemInfo/RuntimeEnvironmentService.cs` (new), `SystemInfoController` `GET api/system/runtime`. `Program.cs`: profile validated at startup; TesterNative refuses a schema ahead of its build. `ExecutionAccess.cs`: TesterNative local trusted execution |
| Data location | `Services/AppDataLocation.cs` (new); 15 `App_Data` call sites; `ManagedFileStores`; Microsoft sign-in settings file |
| Launcher | `AIAssisted/launcher/BirkNext.Launcher/` (new): `Layout.cs`, `Support.cs` (log, credentials, owner-only ACL, profile selection, ports, integrity, prerequisites), `PostgresManager.cs`, `Hosts.cs` (job object, backend, frontend host), `TesterLauncher.cs`, `LauncherProgram.cs` |
| Frontend | `Components/Admin/RuntimeEnvironmentCard.razor(.css)` (new) in System Settings → General; `BuildInfoApiService.GetRuntimeAsync`; guide anchor `runtime-profiles`; a run-history runtime label |
| Packaging | `scripts/build-tester-package.ps1` (new); `scripts/package-archive.ps1` (tester archive name); `azure-pipelines.yml` job `BuildWindowsTesterPackage` (not triggered) |
| Developer launcher | `scripts/start-local.ps1` sets `BirkNext__RuntimeProfile=DeveloperContainer` for the backend |
| Tests | `NativeK6RunnerTests` (13), `TesterLauncherTests` (14), `LocalTrustedExecutionTests` (+1), `RuntimeEnvironmentCardTests` (3) |
