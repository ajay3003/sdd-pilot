# Tester distribution and startup reliability — implementation

| Area | Change |
|---|---|
| Backend `LocalSessionService` | `IssueBootstrap(code)` / `ExchangeBootstrap(token)`: a one-time, 2-minute, hash-only token; the shared code check and throttle; cleared on rotation |
| Backend `LocalSessionController` | `POST api/local-session/bootstrap` (launcher client header, no Origin, code); `POST api/local-session` accepts `bootstrapToken`; clear messages for an expired or used link |
| Backend `LocalRequestGuard` | The bootstrap path joins the anonymous session-bootstrap routes (the controller enforces the rest) |
| Frontend `LocalSessionApiService` / `LocalSessionGate` | Reads `#bootstrap=` (fragment only, 64 hex characters), removes it from the address and history, exchanges it once, reloads. On failure the code dialog explains what to do. The help text names the real places (start window; start `Start-BirkNext.exe` again) |
| Launcher | QuickEdit off (`ConsoleMode`); progress every 15 s during the bounded health wait; the code is read within 20 s; `LocalAccess.ConnectedUrlAsync` (token → fragment, fallback to the plain URL and the code); the already-running start shows the code and opens a connected browser; the ready text says the browser connects automatically |
| `scripts/build-tester-package.ps1` | One-page `README.txt`: start, another browser, stop, data, problems, diagnostics, version, AVD/allow-listing |
| `azure-pipelines.yml` | One tester-facing artifact `BirkNext-Tester-Windows-x64`; the internal candidate is labelled; `birknext-developer-package-archive`; the duplicate `birknext-tester-package` is no longer published. **Edited locally, not run** |
| `scripts/tests/BirkNextBuildIdentity.Tests.ps1` | Checks the new artifact contract (PASS locally) |
| Tests | `LocalSessionBootstrapTests` (3), `TesterLauncherTests` (+2), `LocalSessionTests` (+2) |

**Unchanged:**
- self-contained packaging (`build-tester-package.ps1`, pinned PostgreSQL/k6);
- per-user data under `%LOCALAPPDATA%`;
- verified pre-upgrade backup;
- package integrity checks;
- ports and single-instance ownership;
- the manifest and SHA-256;
- workspace persistence (the current content-addressed store, 19C; the package publishes the same backend).
