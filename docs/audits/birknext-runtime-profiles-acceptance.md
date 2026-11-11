# BirkNext — runtime profiles: acceptance (2026-11-11)

**Evidence source:** the TesterNative package acceptance in `birknext-self-contained-distribution-acceptance.md`, which uses the real launcher, real binaries, isolated data and Microsoft Edge. The IDs below refer to it.

| # | Requirement | Result | Evidence |
|---|---|---|---|
| A | DeveloperContainer selection | PASS (static + unit) | `start-local.ps1` sets the profile; backend default for an unset profile (unit). Live developer start NOT RUN (developer instance on 5000/5173) |
| B | TesterNative selection | PASS | TN-15, BR-02 |
| C | Correct distribution default | PASS | Package default `TesterNative` (TN-15: "package default") |
| D | Explicit profile override | PASS | Unit (`--runtime-profile testernative` = command line) |
| E | No Podman access in TesterNative | PASS | Native runtime registered by profile; no Podman call in the launcher; k6 runs on `process.native` (K6-03) |
| F | No native database in DeveloperContainer by accident | PASS | `start-local.ps1` unchanged (compose PostgreSQL); `Start-BirkNext.exe` refuses DeveloperContainer (PROFILE-DEV) |
| G | Correct database identity | PASS | TN-03, TN-19, TN-21 (system identifier + data directory) |
| H | Correct k6 runner | PASS | K6-03 `process.native`; unit tests for the Container default |
| I | Missing dependency error | PASS | PK-02 (missing `postgres.exe`), PK-01 (tampered `k6.exe`), prerequisite unit test (VC++) |
| J | No automatic fallback | PASS | Unit: contradictory runner stops; nothing falls back to Podman (no code path) |
| K | Restart preserves the profile | PASS | BR-02 after restart, TN-20 |
| L | Application upgrade preserves data | PASS | UP-01 (pre-upgrade backup, same cluster, data present) |
| M | System Settings shows the real profile | PASS | BR-02…05 (Edge) |
| N | Execution provenance remains valid | PASS | Native run records `runtimeId=process.native`, k6 1.0.0 and the executable digest (K6-03); persisted after restart (TN-22) |
| O | Cross-profile data isolation | PASS | Profile-named data root; tester backend's inherited DB settings removed (unit) |
| P | Unsupported capability reported accurately | PASS | Readiness "Not applied (native runner)"; limitations text; CA bundle refused (unit) |

**Browser:** main phase 13/13, after-restart 11/11 (two earlier after-restart runs failed on a harness-induced aborted fetch; see the distribution acceptance run history).

**Clean PC:** BLOCKED (no clean VM).
