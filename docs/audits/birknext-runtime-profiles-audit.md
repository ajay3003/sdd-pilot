# BirkNext — runtime profiles (DeveloperContainer / TesterNative): audit (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `2a8291c2`.
- **Context:** this audit ran together with the self-contained distribution work (same session); its findings are in `birknext-self-contained-distribution-audit.md`.

| Capability | Behaviour at baseline | Required | Gap | Implementation | Verification |
|---|---|---|---|---|---|
| Profile model | None. "Tester package" meant the same container setup, framework-dependent | Two explicit profiles, one definition | Missing | `shared/RuntimeProfileContracts.cs` (`RuntimeProfile`, `RuntimeProfiles.Parse`, `RuntimeEnvironmentInfo`), config key `BirkNext:RuntimeProfile` | Unit tests; TN-15 |
| Selection | `start-local.ps1` chose a container runtime (`auto`: env, healthy Podman, Docker, installed Podman), not a profile | CLI > package default > fail; never from installed tools | Missing | `ProfileSelection.Resolve` (launcher); backend `RuntimeEnvironmentService.Resolve` (unset = DeveloperContainer, invalid = stop) | Unit tests; PROFILE-DEV/INVALID |
| Developer mode | `start-local.ps1` + Podman PostgreSQL + Podman k6 | Preserved | — | Unchanged; the launcher now also sets `BirkNext__RuntimeProfile=DeveloperContainer` for the backend | Full regression; see acceptance (live developer start not re-run) |
| Tester mode | Did not exist natively | Native PostgreSQL, native k6, self-contained | Missing | `Start-BirkNext.exe`, `NativeProcessExecutionRuntime`, `build-tester-package.ps1` | Package acceptance |
| k6 provider selection | Always Podman | From the profile | Missing | `PerformanceTestOptions.ResolveRunner` | Unit tests; K6-03 |
| PostgreSQL ownership | Compose/container | Distinct per profile | Missing for the tester | Launcher-owned cluster with a verified identity in `%LOCALAPPDATA%\BirkNext\TesterNative` | TN-03/19/21 |
| Data isolation | `App_Data` in the backend folder | Per profile | Missing | `AppDataLocation` (`BIRKNEXT_APP_DATA_DIR`), profile-named data root | TN-08/09; unit tests |
| System Settings | Application information (build, schema, Companion) | Runtime & Environment section | Missing | `RuntimeEnvironmentCard` + `GET api/system/runtime`; read-only, no switch | bUnit; TN-15 |
| Live switching | n/a | None, or safe restart only | — | None: separate launchers | Design |
| Execution evidence | Runs record `RuntimeId` from the provider result | Runner identity recorded | Present | Native runs record `process.native` + the `k6.exe` SHA-256 | K6-03 |
| Pipeline | Ubuntu build + Windows smoke of the container package | Windows tester artifact | Missing | `BuildWindowsTesterPackage` job (not triggered) | YAML parse; local package build |
