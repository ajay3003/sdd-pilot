# Tester package — acceptance

| Area | Status |
|---|---|
| Pipeline artifact contract (`BirkNextBuildIdentity.Tests.ps1`) | **PASS** (local; the pipeline itself was not run) |
| Focused unit and integration tests (bootstrap token, launcher QuickEdit/URL, frontend fragment/gate, existing local-session security and launcher suites) | **PASS**: backend 52/52 (`LocalSession*`, `TesterLauncher*`, `Launcher*`), frontend 6/6 (`LocalSession*`) |
| Assembled package (`build-tester-package.ps1 -AllowDownload`) + `Start-BirkNext.exe --validate` / start / stop | **NOT RUN**: needs the pinned PostgreSQL and k6 downloads, an external transfer not approved in this session |
| Clean Windows / Azure Virtual Desktop (first launch, relaunch, reconnect, sign-out, spaces/non-ASCII paths, standard user, endpoint security) | **NOT RUN**: no clean machine or AVD session available. The developer machine is not substituted |
| Controlled failure cases (backend exits, no readiness, port collision, missing files, unwritable data, stale process, multiple launches, browser failure, expired token, interrupted shutdown) | The code paths exist (see the startup audit) and are partly covered by existing launcher tests; **not exercised end to end** here |
| Browser and visual acceptance of the packaged app | **NOT RUN** |
| Startup performance (cold/warm launch) | **NOT MEASURED** |
| Full regression | **NOT RUN** on this commit |

No PASS is claimed for anything above marked NOT RUN, NOT VERIFIED or NOT MEASURED.
