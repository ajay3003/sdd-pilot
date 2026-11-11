# BirkNext — runtime profiles

A runtime profile says **how this installation runs**: where PostgreSQL and the k6 runner come from. It is **not a user role**. Both profiles have the same features, API, EF Core model and migrations, test engines, authorization rules, evidence model and versioning.

| | DeveloperContainer | TesterNative |
|---|---|---|
| Display name | Developer (containers) | Tester (native Windows) |
| Distribution | Source checkout / developer build | Windows Tester Package |
| Launcher | `scripts/start-local.ps1` (sets `BirkNext__RuntimeProfile=DeveloperContainer`) | `Start-BirkNext.exe` (sets `TesterNative`) |
| PostgreSQL provider | `postgres:16` container via Podman/Docker | Bundled PostgreSQL 16.13 |
| PostgreSQL lifecycle owner | The container runtime (compose) | The launcher (`pg_ctl`, this Windows user) |
| Database identity | The configured connection string (`AIAssisted/.env`) | `cluster.json`: system identifier + data directory, verified before use |
| k6 provider | `container.podman` (`PodmanContainerExecutionRuntime`) | `process.native` (`NativeProcessExecutionRuntime`) |
| Application hosting | `dotnet run` / published DLL + `dotnet-serve` | Self-contained `BirkNext.Api.exe` + the launcher's static host |
| Data | `App_Data`, the Podman volume | `%LOCALAPPDATA%\BirkNext\TesterNative` |
| Required capabilities | .NET SDK, Podman (or Docker) | Windows 10/11 x64, Visual C++ 2015–2022 x64 runtime |

## Canonical definition and selection

- **Definition:** `shared/RuntimeProfileContracts.cs` (`RuntimeProfile`, `RuntimeProfiles.Parse`, `RuntimeEnvironmentInfo`). It is shared by the backend, the frontend and the launcher. There is no second configuration engine: the profile is the configuration key `BirkNext:RuntimeProfile`.
- **Precedence in `Start-BirkNext.exe`:**
  1. `--runtime-profile`;
  2. the package's `config/runtime-manifest.json` default (`TesterNative`);
  3. otherwise fail.
  
  `DeveloperContainer` and unknown values exit with code 64.
- **Backend:** `BirkNext:RuntimeProfile` from configuration. Unset means `DeveloperContainer` (a source checkout); an unknown value stops startup.
- **Never inferred** from whether Podman, PostgreSQL or k6 is installed. There is never a fallback to the other profile when something is unavailable.

## What the profile selects (and nothing else)

- **k6 runner:** `PerformanceTestOptions.ResolveRunner`.
  - TesterNative gives `Native`; a configured `Container` under TesterNative stops startup.
  - DeveloperContainer gives `Container` unless `Native` is configured explicitly.
  - An unknown runner value stops startup.
- **Local trusted execution:** honoured for a Development backend or a TesterNative backend, under the same loopback, Host, Origin and session checks.
- **Newer schema:** TesterNative refuses to run on a schema migrated by a newer build. DeveloperContainer logs a warning, because developers switch branches routinely.
- **Database:** chosen by the launcher. The two profiles never share a data directory, and the tester launcher removes inherited database settings before it starts the backend. There is no migration between profiles.

## Visibility

**System Settings → General → Runtime & Environment** (`GET api/system/runtime`) shows:
- the profile and its source;
- the distribution;
- the database mode;
- the database status and the server version reported by the server itself;
- the k6 runner, its availability and its version.

Schema status and the application build are shown in the Application information card above it. There is **no switch**: changing the profile means starting BirkNext with the other launcher. No live database switch is possible.

## Evidence

A performance run records the runtime that actually executed it (`RuntimeId` `process.native` or `container.podman`, the provider version and the executable or image digest) together with its existing execution manifest (BirkNext build, definition revision, environment). A later change of profile rewrites no historical record.
