# BirkNext — runtime profiles: implementation (2026-11-11)

- **Baseline:** `2a8291c2`.
- **Commits:** `6dc8db98` (implementation) and `f469ded5` (line-ending restoration only).
- **Shared with:** the self-contained distribution task. The full file list and design decisions are in `birknext-self-contained-distribution-implementation.md`; this note covers only the profile model.

## Design

- **One definition:** `shared/RuntimeProfileContracts.cs` (`RuntimeProfile { DeveloperContainer, TesterNative }`, `RuntimeProfiles.Parse`, `DisplayName`, `RuntimeEnvironmentInfo`). It is linked into the backend, frontend and launcher. There is no new configuration engine: the key is `BirkNext:RuntimeProfile`.
- **Selection:**

| Where | Rule |
|---|---|
| `Start-BirkNext.exe` | `--runtime-profile`, else the package default (`config/runtime-manifest.json`), else fail. Only TesterNative is accepted (DeveloperContainer → exit 64 with "use scripts/start-local.ps1") |
| `scripts/start-local.ps1` | Sets `BirkNext__RuntimeProfile=DeveloperContainer` for the backend |
| Backend | Unset → DeveloperContainer (source checkout); unknown value → startup stops |

- **Effects (the only places the profile is read):**

| Concern | DeveloperContainer | TesterNative |
|---|---|---|
| k6 runner (`PerformanceTestOptions.ResolveRunner`) | Container, unless Native is configured | Native; `Container` configured → startup stops |
| Local trusted execution | Development backend | TesterNative backend (same loopback, Host, Origin and session checks) |
| Schema ahead of the build | Warning (branch switching) | Startup refused |
| `api/system/runtime` | Profile, source, distribution, DB, k6 | same |

- **No live switch:** the System Settings card is read-only; changing profile means starting the other launcher. There is no fallback and no data migration between profiles. Separate data roots: `%LOCALAPPDATA%\BirkNext\TesterNative` vs the developer's `App_Data` and Podman volume.
- **Evidence:** runs record the runtime that executed (`process.native` / `container.podman`). Historical records are never rewritten.

## Tests

- `NativeK6RunnerTests`: runner per profile; contradictory and invalid configuration stops startup; profile parsing.
- `TesterLauncherTests`: CLI and package precedence; DeveloperContainer refused; profile-named data folder.
- `LocalTrustedExecutionTests.TesterNative_GrantsUnderTheSameChecks_AndProductionWithoutItStillDenies`.
- `RuntimeEnvironmentCardTests`: the reported profile is shown, there is no switch, and an unreachable backend is not a profile.
