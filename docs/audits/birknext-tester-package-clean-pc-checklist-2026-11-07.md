# Tester Package — clean-PC acceptance checklist (G10)

**Why this exists.** G10 requires the downloaded pipeline artifact to work on a clean Windows PC under an ordinary user account. No clean
Windows VM or spare test PC was available or authorized in the 2026-11-07 verification, so the package was verified locally only (see the
verification audit). Run this checklist once on a clean PC and record the results here; G10 stays **PARTIAL** until it has been run.

**Clean PC means:** Windows 10/11, a standard (non-administrator) user, no BirkNext before, no source checkout, no Visual Studio. Note which of
.NET 8 ASP.NET Core Runtime / Podman Desktop / Docker Desktop are installed **before** you start.

Record for every step: result (PASS / FAIL / BLOCKED), the exact message shown, and the launcher log path it names.

## A. Download and extract

| # | Step | Expected |
|---|---|---|
| A1 | Download the tester-package artifact of the build to test from the pipeline run (note the run number and commit). | One ZIP. |
| A2 | Extract it to a folder **with a space** in its path under your profile, e.g. `Documents\BirkNext Test\`. Do not use `Program Files`. | Extracts without errors. |
| A3 | Open `package-manifest.json`. | `sourceCommit` equals the pipeline run's commit; the file count matches the manifest. |
| A4 | Run `.\scripts\validate-package.ps1` from the extracted folder (Windows PowerShell 5.1 is fine). | Exit code 0; no missing file. |

## B. Dependency detection (run each that applies; never install anything during a run)

| # | Situation | Command | Expected |
|---|---|---|---|
| B1 | .NET 8 ASP.NET Core Runtime **not** installed | `.\start-local.ps1 -ValidateOnly` | Exit code **2**; message names the missing runtime and the download page; nothing starts. |
| B2 | Neither Podman nor Docker installed | `.\start-local.ps1 -ValidateOnly` | Exit code **2**; message: "neither Podman nor Docker is installed", names Podman Desktop / Docker Desktop and `-SkipContainers` (before the 2026-11-07 fix `-ValidateOnly` reported "passed" here). |
| B3 | Docker Desktop installed but not running | `.\start-local.ps1` | Fails before starting anything: "Docker is installed but not running. Start Docker Desktop …"; non-zero exit. |
| B4 | Podman installed, machine stopped | `.\start-local.ps1` | "Its machine is not running yet; BirkNext will start it", the machine starts, then the database starts. |
| B5 | Podman installed but its machine cannot start (e.g. WSL disabled) | `.\start-local.ps1` | Fails with "podman machine start failed with exit code …" and the troubleshooting list; non-zero exit; no claim that BirkNext started. |
| B6 | Port 5000 or 5173 already used by another program | `.\start-local.ps1` | Names the port and its owner and the next step; does **not** stop a program it does not own; "Startup stopped …"; exit code **3** (was 0 before the 2026-11-07 fix). |
| B7 | Package extracted into a read-only folder | `.\start-local.ps1 -ValidateOnly` | Exit code 2: "BirkNext cannot write to … Extract the package to a folder you own". |

## C. First run (with .NET 8 runtime and one container engine installed)

| # | Step | Expected |
|---|---|---|
| C1 | `.\start-local.ps1` as the ordinary user (no "Run as administrator"). | No UAC prompt. PostgreSQL container starts; backend and frontend start; the launcher prints the URLs and the launcher log path. |
| C2 | Watch the backend window/log for the first start. | Migrations apply once (all four contexts); no unhandled exception. |
| C3 | Check listeners: `Get-NetTCPConnection -State Listen | ? LocalPort -in 5000,5173` | Only `127.0.0.1` / `::1` — never `0.0.0.0` or a LAN address. |
| C4 | Check the launch-code folder: `icacls <package>\AIAssisted\backend\...\App_Data\local-session` | Only your account and SYSTEM; no inherited entries (`(I)` absent on the folder). |
| C5 | Open the frontend URL in Edge; connect with the launch code. | Connected; the launch code does not appear in the address bar or the launcher output. |

## D. Basic use (synthetic data only)

| # | Step | Expected |
|---|---|---|
| D1 | Dashboard and System Settings open. | No error banner; Config Health states what is not set up (never "All clear" while Git Sources is not set up). |
| D2 | Test Data → overview and XML fixtures. | Catalog renders; built-in Skolenærvær identities are listed as approved defaults. |
| D3 | Open a saved result (FQR/AQR history, if any) and a Source Analysis snapshot (import a small synthetic ZIP if none). | Stored results open and are labelled historical, never as current readiness. |
| D4 | Run an M01 static review, then reload the page. | The review and its control matrix are still shown after reload (BQA-014 fix); runtime controls stay "Not assessed". |

## E. Shutdown, restart, persistence

| # | Step | Expected |
|---|---|---|
| E1 | Close the backend/frontend windows (or Ctrl+C); stop the container if the launcher offers it. | Clean shutdown; no orphaned BirkNext process on 5000/5173. |
| E2 | `.\start-local.ps1` again. | Starts without re-creating data; migrations report up to date. |
| E3 | Reopen the pages from D2–D4. | Everything saved in D (fixtures, snapshot, M01 review) is still there; Target Environment ids unchanged. |
| E4 | Extract a **second** copy to another folder and start it after stopping the first. | Separate logs and local data; nothing from copy 1 appears unless both use the same container database (documented). |

## F. Result

| Item | Result | Notes |
|---|---|---|
| PC / OS build / user type | | |
| Pipeline run / commit | | |
| A–E | | |
| G10 | PASS only if every applicable step passed | |
