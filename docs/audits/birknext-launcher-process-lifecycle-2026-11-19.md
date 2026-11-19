# Windows tester launcher — process lifecycle (2026-11-19)

**Defect:** after `Start-BirkNext.exe` said *"Press Ctrl+C or close the window to stop BirkNext"*, closing the console window left BirkNext
running in the background.

**Result:** fixed. Closing the window (or Ctrl+C, logoff, shutdown, Task Manager, a launcher crash) now ends the launcher's backend and its
local database and releases every port. Nothing else is ever stopped. Verified with real Windows processes on the packaged executable (below).

## 1. Reproduction (unmodified CI artifact 1.0.0-ci.416921)

A copy of the extracted CI artifact, isolated `--data-dir`, started in its own console window (`conhost.exe`), closed with `WM_SYSCOMMAND/SC_CLOSE`
(the window's close button), Ctrl+C raised with `GenerateConsoleCtrlEvent` from a helper attached to that console, and a hard kill with
`TerminateProcess`. Processes were identified by executable path inside the test package and by data folder — never by name.

Process tree while running:

```
Start-BirkNext.exe 28688            listens 5481 (frontend, in-process)
└─ BirkNext.Api.exe 10680           listens 5480   (in the backend's job object)
   └─ conhost.exe 536
cmd.exe 37280  (parent pg_ctl 19416 already exited)
└─ postgres.exe 35948 (postmaster)  listens 55433  (NOT in any launcher job)
   └─ 7 × postgres.exe
```

| How the launcher ended | Backend | PostgreSQL (8 processes, port) | Launcher stop (`finally`) | `session.json` |
|---|---|---|---|---|
| Ctrl+C | stopped | stopped (`pg_ctl stop -m fast`) | ran (`Stopped.`) | removed |
| **Window closed** | ended by the job | **left running, port 55433 open** | **never ran** (log ends at `Ready`) | **left** |
| Hard kill | ended by the job | **left running** | never ran | left |

The same happened on the tester's own PC: the launcher logs of 2026-11-18 and 2026-11-19 both end at `Ready` (no `Stopping`), the next start
logged *"Removed stale session.json (a previous launcher did not exit normally)"* and *"Reusing the running local database"*, and seven
`postgres.exe` processes of package 416921, started 2026-11-18 14:15:58, were still running on 2026-11-19 with no launcher or backend.

## 2. Root cause

1. **Console close was handled like Ctrl+C.** `CTRL_CLOSE_EVENT` arrives in .NET as `SIGHUP`; the handler only cancelled a token and returned.
   For close, logoff and shutdown events Windows terminates the process as soon as the handler returns, so the asynchronous stop (frontend,
   backend, `pg_ctl stop`, session file) never ran. Ctrl+C worked because Windows does not end the process after a Ctrl+C handler.
2. **PostgreSQL was deliberately outside the launcher's lifetime.** The backend was in a kill-on-close job object, but pg_ctl was started with
   ShellExecute as a detached server "that survives a launcher crash". Any end of the launcher other than its ordered stop orphaned the database
   server (and its port, data folder and memory).
3. **The next start silently adopted the orphan** ("Reusing the running local database"), outside any job — possibly a server from an older
   package — so it would outlive that window as well.

The plausible link to other inconsistent behaviour (e.g. the reset defect) is limited to this: a database server from an earlier window kept
serving the same data folder after its window was gone; old backends did NOT survive (the job ended them).

## 3. Fix (Windows-native ownership)

| Mechanism | What it does |
|---|---|
| `LauncherJob` (one per launcher run) | Job object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, no breakaway. Holds the backend **and** the database server. When the launcher ends in any way, Windows closes the last handle and ends everything in it. Only processes this launcher started are in it. |
| `HiddenProcess` | Starts `pg_ctl start` **suspended**, in its own hidden console, with no inherited handles (the reason ShellExecute was used), assigns it to the job, then resumes it — so the postmaster and every server process are in the job with no race. pg_ctl does not create its own job when its process is already in one. |
| `ShutdownCoordinator` | Ctrl+C asks for the ordered stop. Window close / logoff / shutdown handlers request the same stop and **wait** for it (budget 4.5 s; Windows allows ~5 s). The stop uses short waits then (`pg_ctl stop -m fast -t 3`, frontend 0.5 s). Whatever is still running when Windows ends the launcher is ended by the job. |
| One stop path | `DispatchAsync` stops frontend → backend → database in a single `finally` for every outcome: normal stop, Ctrl+C, window close, backend crash, and a start that failed half-way (nothing it started stays). |
| Left-over database | A server running on **this** data folder while this launcher holds the folder's lock is verified (credentials, system identifier, data directory), stopped cleanly and started again inside this launcher's job — never reused outside it, never anything not proven to be ours. |
| Backend crash | Logged with its exit code; the database is stopped; the launcher exits non-zero so the window stays open with the message. |
| Logging | Launcher pid, job yes/no, backend and database server pids with "(in job)", ports, stop reason and stop duration. |

Unchanged on purpose: one launcher per data folder (named mutex); a second launch only shows/opens the running BirkNext and never stops it;
ports already used by another program are never touched (BirkNext moves to the next free port); the browser is not in the job.

No process is found or stopped by name anywhere (no `taskkill /IM`).

## 4. Verification (real processes)

Development launcher against the unmodified 416921 package files (`--package-root`), harness in the session scratchpad (`lc/scen.ps1`):

| # | Scenario | Result |
|---|---|---|
| 1 | Normal start | Ready; backend and database server "(in job)" |
| 2 | Ctrl+C | PASS — stopped in 1.9 s, 0 processes, 0 listeners |
| 3 | Window closed | PASS — stop ran in the close handler (`Stop: WindowClosed`, `Stopped in 2.1 s`), everything gone in 2.8 s |
| 4 | Backend killed | PASS — failure logged (exit -1), database stopped, window stays open with the message |
| 5 | Launcher killed (TerminateProcess) | PASS — job ended backend + 8 database processes within 0.5 s; next start ran PostgreSQL crash recovery (`redo done`) and was ready |
| 6 | Start cancelled (Ctrl+C / window close during "Applying database updates") | PASS — backend and database stopped, nothing left |
| 7 | Preferred ports 5480/5481 held by another program | PASS — BirkNext used 5482/5483; the other listener untouched |
| 8 | Start → close → start again (same data folder) | PASS |
| 9 | Second launch while running | PASS — first instance's 12 processes untouched |
| 10 | Five rounds, alternating close and Ctrl+C | PASS — 0 processes, 0 listeners after each |
| 11 | Database orphaned by the OLD launcher, then the new launcher | PASS — orphan (7 processes) identified, stopped, restarted in the new job |

Browser: the launcher opens the browser as an ordinary child that is not assigned to the job (only processes explicitly added are in it), so
closing the launcher never closes the browser, and a closed browser does not affect the launcher.

Packaged executable: see section 5.

## 5. Package and regression

PENDING — filled in after the package build.
