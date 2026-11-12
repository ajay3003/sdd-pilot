# Windows startup reliability — audit (TesterNative launcher `Start-BirkNext.exe`)

## Lifecycle (actual code: `AIAssisted/launcher/BirkNext.Launcher`)

1. **Initializing:** parse options, read the runtime manifest, resolve the profile, then create the per-user data folder `%LOCALAPPDATA%\BirkNext\TesterNative` (owner-only ACL) and the log.
2. **Single instance:** a per-user named mutex per data folder. If it is held and a session file exists, the launcher opens the running instance. A stale session or stop file left by a killed launcher is removed once the lock is acquired.
3. **Checking configuration:** prerequisites (OS, disk), then package integrity (critical files every start; every file once per package build).
4. **Checking local data:** PostgreSQL state classification. It never runs initdb over existing files. Missing files, unknown files and an incompatible version stop with a tester message.
5. **Starting services:** PostgreSQL via `pg_ctl` (180 s bound). Its identity is verified (credentials + cluster system identifier), never just a port. A port owned by someone else is never touched; BirkNext moves to the next free port and remembers it.
6. A **verified backup** is made before a different build migrates the database.
7. **Waiting for readiness:** the backend runs in a Windows job object (dies with the launcher). Health = `GET /api/system/build-info` returns 200/401. The wait is bounded to 5 min and stops early if the backend exits. The backend's own failure line becomes the tester message.
8. The **frontend** is served in-process on loopback.
9. **Establishing local access:** the launcher shows the launch code.
10. **Opening the browser:** Edge, else the default browser.
11. **Ready.** Then **stopping:** frontend → backend → `pg_ctl stop`, and the session file is removed.

Every wait is bounded, and every failure is a tester message with an action and the log path. Diagnostics are a zip with logs and state; passwords are redacted and the credential file is excluded.

## Root causes found (verified in code)

| # | Symptom | Cause | Evidence |
|---|---|---|---|
| R1 | "The key needed by the frontend is sometimes not visible" | (a) **Second start** (browser closed, double-click again): the already-running path printed only the URL. **No connection code**, and the new browser was not connected. (b) The code was read only within **5 s** after health; on a slow first start it could be missing from the ready message. (c) The connect dialog said the code is in `App_Data\local-session\launch-code.txt` "next to the backend". In the tester package it is under `%LOCALAPPDATA%\BirkNext\TesterNative\appdata\…`, so the hint led nowhere. (d) Sessions are in memory: **every** BirkNext start requires every browser to enter the code again | `TesterLauncher.cs` (already-running branch; `ConnectionCode` 20×250 ms); `LocalSessionGate.razor` text; `LocalSessionService` (in-memory sessions) |
| R2 | "Startup occasionally hangs" | **Console QuickEdit mode** was not disabled. A click into the start window (a natural reaction while waiting) begins a text selection, and every later console write blocks until a key is pressed. The launcher writes progress between every step, so it stops mid-start and looks hung. Mechanism verified against the code (no `SetConsoleMode`); **not reproduced interactively in this session** | `LauncherProgram.cs` |
| R3 | "Hangs" on slow first starts | The backend health wait (up to 5 min: first database setup, migrations, antivirus scanning) printed nothing, so it was indistinguishable from a hang | `Hosts.cs` `WaitHealthyAsync` |
| R4 | Stalls with the developer package | `start-local.ps1` needs Podman/Docker and installs `dotnet-serve` from the internet. On a locked-down PC or AVD that blocks or waits | `azure-pipelines.yml` README text |

## Fixes
- **R1:**
  - automatic secure connection (launcher-issued one-time token; see the security audit);
  - the already-running path shows the code and opens a connected browser;
  - the code is read within 20 s;
  - the dialog text names the real path: start `Start-BirkNext.exe` again.
- **R2:** QuickEdit is switched off at launcher start (`ConsoleMode.DisableQuickEdit`; Windows only; no effect when redirected).
- **R3:** "Still starting BirkNext (N s; a first start can take a few minutes)" every 15 s during the bounded wait.
- **R4:** testers get only the self-contained package (artifact consolidation).

**Not changed:** ports (preferred 55432/5480/5481, moved when busy, loopback only); single-instance ownership by mutex plus session file; shutdown order; data location; backups.
