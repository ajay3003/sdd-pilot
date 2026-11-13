# Tester package — acceptance

Final release acceptance, 2026-11-13. Every result below comes from the **real ZIP**, extracted and launched through `Start-BirkNext.exe` on the developer machine, with isolated data folders. No clean machine and no AVD session were used.

## Package under test

| Item | Value |
|---|---|
| Archive | `BirkNext-Tester-Windows-x64-1.0.0-local.zip` |
| SHA-256 | `f6b92fc6f656791fbf3be5c6f8a2fd4ee56ccbe0407a8e6d55e80c79f12cf4ac`, equal to the `.zip.sha256` file and re-hashed after the build |
| Source commit | `a6598fb7d33227f07728ee5b6597d0defff2397c`, version `1.0.0-local+a6598fb7….dirty` |
| Contents | 2 741 entries; 2 740 files hashed in `package-manifest.json`; 83 migrations; PostgreSQL 16.13 and k6 1.0.0 from the pinned download cache, no download |
| Build | Official `scripts/build-tester-package.ps1` (including the new Blazor integrity gate: 86/86), from a scratch copy of the tree. All 2 512 packaged source files are content-identical to HEAD (line endings aside). The frontend's `bin`/`obj` were cleaned first (see the next section) |

Why "dirty": the build identity reads the main working tree. That tree has a modified `appsettings.Local.json` and other sessions' untracked documents, and none of them is packaged. A pipeline build from a clean checkout would not be dirty. This is a local acceptance build, **not a distributable release**.

**The final launcher is in the ZIP:**
- The packaged `Start-BirkNext.exe` contains the strings of both launcher fixes: the data-folder message "cannot write to its data folder" (26eda8d1) and `DOTNET_gcServer` (59531f1c).
- The previous acceptance build (`e81a00b6`, sha256 `2d9e73d9…ef142`) contains neither.

**Hidden files and integrity:** `scripts/check-package-hidden-files.ps1` and `scripts/check-blazor-integrity.ps1` (86/86 resources) pass on the extracted package. Only allowlisted hidden files are present, and `AIAssisted/.env` equals the checked-in template.

## Results (all on the final ZIP)

| Group | Checks | Result |
|---|---|---|
| Harness self-check H-01..09: every entry extracted, EXE at the root, version matches the ZIP name, path lengths, imports, localhost only, UTF-8 console | 9 | **9/9 PASS** |
| First start TN-01..17: validate; ready in **29 s**; database loopback-only, password and SCRAM required, no default superuser; DPAPI-protected credentials; no secrets in logs; frontend and deep link served; API needs the session; 4 contexts migrated; clean stop | 17 | **PASS** |
| Native k6 K6-01..05 (twice): bundled k6 runs natively; thresholds are evaluated by BirkNext (Pass, and Fail for an impossible limit) | 12 | **PASS** |
| Restart and persistence TN-20..26: same cluster, no initdb; k6 run and definitions survive; a second launch starts nothing; status; clean stop | 7 | **PASS** |
| Database and profile failures: foreign data folder left untouched; developer or unknown profile refused (exit 64); interrupted initialization kept aside and a new cluster created; port conflict moves to 55433 and persists; PostgreSQL 17 data directory refused, nothing started; diagnostics contain no secrets | 10 | **PASS** |
| Crash recovery CR-00..04: postmaster killed; PostgreSQL recovers itself; committed k6 run intact; clean stop | 5 | **PASS** |
| Backup and restore BK-01..03: verified dump; isolated restore (4 definitions, 4 runs, 73 migrations); truncated backup refused | 3 | **PASS** |
| Connection security BOOT-01..12 + stop: ready in 28 s; browser Origin, non-launcher client and wrong code refused; 64-char one-time token expires in 120 s; HttpOnly SameSite=Strict cookie; replay refused; expiry at 125 s; relaunch shows the code; no code or token in any log | 13 | **PASS** |
| **Startup failure cases FX-01..06** (`acc_fail2.py`, on copies of the package) | 7 | **7/7 PASS** |

**FX details:**
- **FX-01/02:** a missing `postgres.exe` exits with code 2 and a plain message saying what to do. The log names the file, and no database is created.
- **FX-03:** a tampered `k6.exe` exits with code 2; the log names it as changed.
- **FX-04, the launcher fix:** with an unwritable data folder (write denied with icacls, restored afterwards) the launcher **exits with code 2**. It prints "BirkNext cannot write to its data folder: …" and how to proceed, with no stack trace. The previous build crashed with an unhandled exception (0xE0434352).
- **FX-05/06:** a busy backend port moves to 5481 and persists, and the foreign listener is left untouched.

Totals: **84/84 PASS** in `scratchpad/win/acc8.log` (77 checks plus FX-01..06), with 0 failures.

### Backend memory configuration (59531f1c), verified in the running package
Both packages were started fresh with no browser on this 24-thread machine, and the backend was measured 15 s after ready:

| Build | Threads | Private memory |
|---|---|---|
| Final ZIP (Workstation GC) | 20 | 151 MB |
| Previous ZIP (Server GC) | 44 | 475 MB |

The ~24 extra threads are Server GC's per-core GC threads.

### Browser (packaged, `pkg-ui.cjs`, isolated Edge profile, non-localhost aborted): **21/21 PASS**

The launcher ran with `--no-browser`, and the URL it would open was obtained exactly as the launcher does.

| Check | Result |
|---|---|
| UI-00..02 | Ready in 31.6 s; launcher-chosen ports 5480/5481/55432 (not 5000/5173); 64-char token |
| UI-03/04 | **The launcher URL connects automatically in 5.2 s**, with no code dialog; the token is removed from the address bar (`/dashboard`) |
| UI-05..07, UI-11 | Dashboard usable; refresh, new tab and browser restart stay connected (persistent cookie) |
| **UI-08** | A consumed token in another browser does not connect; the code dialog is shown |
| **UI-09** | An invalid token does not connect; the code dialog is shown |
| **UI-10** | An invalid session cookie is refused; the code dialog is shown |
| UI-12 | A second launch starts nothing new and shows the code |
| UI-13..16 | Clean stop 2.8 s; restart 7.6 s; the old tab gets the code dialog; the new launcher URL connects in 3.8 s |
| **UI-17** | A token opened after 2 minutes is refused, and the dialog says to enter the code from the BirkNext window |
| UI-18..20 | Final stop 0.9 s; no package-owned process left; no token in any log |

UI-08/09/10/17 were the four cases left unverified in the previous session; they now pass on the final ZIP.

## Defect found and fixed in this acceptance: a ZIP whose app never started

The first rebuild (`944704427b15…`, commit 59531f1c) passed all 84 non-browser checks, yet **the browser stopped at "An unhandled error has occurred" (98 %)**.
- The console showed a Subresource Integrity failure for `_framework/dotnet.native.wasm`.
- `blazor.boot.json` and the `.br`/`.gz` copies had been regenerated for runtime pack **8.0.34**, but the incremental publish in the scratch tree kept the older uncompressed **8.0.33** `dotnet.native.wasm` (and its JS loaders). Every package-manifest hash was self-consistent, so no existing check noticed.
- A clean checkout, as on a pipeline agent, would not hit this, but nothing stopped such a ZIP from being produced.
- Fix `a6598fb7`: `scripts/check-blazor-integrity.ps1` runs inside `build-tester-package.ps1` after the frontend publish and fails the build. Proven on the real outputs: the broken ZIP fails (3 of 86 resources), the previous working ZIP passes (86/86). 8 synthetic tests, also added to the GitHub preflight step.
- The final ZIP was then rebuilt from a cleaned frontend and **the complete acceptance above was repeated on it**.

## Not run

| Area | Status |
|---|---|
| Clean Windows machine; standard user without development tools; endpoint security | **NOT RUN**: no clean machine available. The developer machine is not substituted |
| Azure Virtual Desktop (first launch, sign-out, reconnect) | **NOT RUN**: no AVD session |
| Folders with spaces or non-ASCII characters | Not separately run on the final ZIP. The harness ran from `%TEMP%\bnacc7`, a short ASCII path |
| Azure DevOps pipeline build of this commit | **NOT RUN**: no pipeline trigger allowed |

## Harness root causes found and fixed (not product defects)
- `Expand-Archive` silently rolled back on paths over 260 characters, which looked like "wrong EXE path". Extraction now uses .NET into a short root, and H-01 checks every entry. Testers need an extract folder of about **138 characters or fewer**.
- Imports failed under `python -I`, fixed with an explicit `sys.path`. The cp1252 console crashed on non-ASCII text, fixed by forcing UTF-8.
- The browser check's wait could not survive the app's own URL replacement (removing `#bootstrap=`): a destroyed execution context is now retried, never counted as a result.
- The browser check counted a rendered navigation as "connected" before the gate's asynchronous session check had finished. It now requires `GET api/local-session` from the page to return `connected: true`.
- A republished web app reset `BackendUrl` to :5000, so a browser run silently talked to nothing. A wiring probe now runs before every browser phase.
