# BirkNext — launcher / package integrity, BUG-005 (2026-11-27)

The original brief said no commits; the user later asked for commits, and the changes are in `7d08f3a3` (not pushed). Everything was tested on temporary package copies under `%TEMP%\bnlx` with their own `--data-dir`. Nothing touched `%LOCALAPPDATA%\BirkNext`, the repository's `App_Data` or any reset backup.

## Observed
- **Error:** «This BirkNext package has been changed after it was built: 6 file(s) differ», for example `runtime/translation/lib/.../__pycache__/__init__.cpython-312.pyc`.
- **When:** the package had been used for hours. The application stopped, and the restart ran a full check: 2,817 files, about 811 MB, about 530 s. The check then refused the package.

## Root cause — CONFIRMED (reproduced on a temporary copy)
1. **Bytecode caches entered the package.**
   - `scripts/build-offline-translation.ps1` deletes `__pycache__` and writes `translation-manifest.json`.
   - **After that** it runs the helper probe with `python -I`. Isolated mode does **not** stop Python writing bytecode, so the probe creates six `.pyc` inside `lib`.
   - The bundle is then moved into the package stage, and `validate-package.ps1 -CreateManifest` hashes **every** file, these six included.
   - This happens when the build builds the translator (no usable Git LFS copy, as in CI). The LFS path copies only files listed in `translation-manifest.json`.
2. **Python rewrites them on the tester PC.**
   - Python checks a `.pyc` against its source's mtime and size.
   - `scripts/package-archive.ps1` wrote `entry.LastWriteTime = LastWriteTimeUtc`. A ZIP stores local time without a zone and at 2-second precision, so every extracted file's mtime is shifted by the UTC offset and truncated.
   - Every packaged `.pyc` is therefore stale on the tester PC, and the first use of the translator rewrites it.
3. **The next full check refuses the package.**
   - The launcher verifies the SHA-256 of every listed file. It does not compare timestamps or sizes.
   - A full check runs on the first start in a folder, on `--validate`, after a new build, or when the package path changes. The six rewritten `.pyc` then «differ».

**Reproduction** (`scratchpad/launcher/repro.ps1`, the repository's embedded Python, the same ZIP code as `package-archive.ps1`, temporary copy):

| Step | Result |
|---|---|
| Build probe (`python -I`) | creates exactly **6** `.pyc` (below) |
| `lib/sentencepiece/__init__.py` mtime | build `07:51:30.338`; after ZIP round trip `06:51:30.000` (−1 h, truncated) |
| Use with `-I` (as before) | **6 of 6** `.pyc` rewritten |
| Use with the fix (`-I -B -X pycache_prefix=…`) | **0 of 6** changed |

**The six files** (the reproduction's names; the tester package itself was not available, and the error message names one of them):
- `runtime/translation/lib/ctranslate2/__pycache__/__init__.cpython-312.pyc`
- `runtime/translation/lib/ctranslate2/__pycache__/extensions.cpython-312.pyc`
- `runtime/translation/lib/ctranslate2/__pycache__/version.cpython-312.pyc`
- `runtime/translation/lib/ctranslate2/models/__pycache__/__init__.cpython-312.pyc`
- `runtime/translation/lib/sentencepiece/__pycache__/__init__.cpython-312.pyc`
- `runtime/translation/lib/sentencepiece/__pycache__/_version.cpython-312.pyc`

Independent evidence: the developer's own repository copy (`AIAssisted/runtime/translation/lib`) holds the same six `.pyc`. Git does not track them, the manifest does not list them, and they were written by a local translator run on 2026-11-22 at 18:22. Python writes them during normal use.

**Not proven:** why the tester's restart ran a full check rather than the quick check. Nothing in the launcher deletes `package-verified.json`. A full check runs when:
- the package path differs from the one recorded (another folder, drive letter, shortcut or redirected path);
- the record cannot be read;
- the build is new;
- `--validate` is used.

The tester's launcher log (`<data>\logs`) would show which. Whichever it was, **any** later full check of an affected package fails once the translator has been used.

## Why the check took ~9 minutes
Measured on this PC:

| Run | Result |
|---|---|
| 582 MB package, first read | **89.1 s** |
| same files, warm read | **2.1 s** |
| unique, never-seen content, 843 files / 247 MB, alternating order | 4 readers **18.6 s / 18.1 s**; 1 reader **74.1 s / 73.5 s** |

The cost is the first read of each newly extracted file, mostly antivirus scanning, not SHA-256. Four readers are about **4× faster**.

Two caveats about the measurements:
- An earlier comparison (47.4 s → 10.2 s) used two copies with the **same** content. Defender caches its verdict per content, so the second copy was always fast (in the package run below: 97.5 s, then 0.6 s for an identical copy). Only the alternating unique-content measurement above counts.
- For the tester's 811 MB, 4 readers should give roughly a quarter of the 530 s. This is not measured on the tester's PC.

## Changes (committed in `7d08f3a3`)
| File | Change |
|---|---|
| `AIAssisted/backend/.../OfflineTranslation/OfflineTranslationEngine.cs` | The translator starts with `-I -B -X pycache_prefix=%TEMP%\BirkNext\python-cache`: Python neither writes nor reads `__pycache__` inside the package. `PythonArguments(...)` is pure and tested. |
| `scripts/build-offline-translation.ps1` | The probe runs with `-B`; `__pycache__` is removed after the probe. |
| `scripts/build-tester-package.ps1` | Removes every `__pycache__` from the stage before the package manifest. |
| `scripts/validate-package.ps1` | `-CreateManifest` **refuses** a stage containing `__pycache__` or `.pyc`. Verified: with a `.pyc` it fails and writes no manifest; without one it writes it. |
| `scripts/package-archive.ps1` | ZIP entries get local time (what an extractor restores), not UTC written as local time. |
| `AIAssisted/launcher/BirkNext.Launcher/Support.cs` (`PackageIntegrity`) | (1) A manifest entry `…/__pycache__/*.pyc` is a **generated cache**: reported in `GeneratedCaches`, not hashed; every other file is still verified byte for byte. (2) Up to 4 files hashed at once; progress, cancellation and result order stay deterministic. |
| `AIAssisted/launcher/BirkNext.Launcher/TesterLauncher.cs` | Logs which caches were not hashed. |

## Security implications
- **Not weakened:** every executable, DLL, `.py` source, model, configuration and frontend file is still verified by SHA-256 on the full check. The quick per-start check is unchanged.
- **The one exception:** listed `__pycache__/*.pyc` entries, which exist only in older affected builds.
  - A `.pyc` is executable code. It is safe to skip only because BirkNext's Python is now started with a cache prefix outside the package, so it **never reads** a `.pyc` inside it.
  - The engine change ships in the same verified backend.
  - A tampered `.pyc` in the package is therefore inert for BirkNext. Someone running `python.exe` by hand is outside the launcher's protection either way.
- A `.pyc` outside `__pycache__`, or any other file type inside `__pycache__`, is **not** treated as a cache. A test covers this.
- New builds cannot contain caches: the manifest step refuses them.

## Can an existing package be repaired? Is a new build required?
- **A new package build is required.** The launcher (`Start-BirkNext.exe`) and the backend are part of the package; a tester cannot get the fix without a new package.
- **Existing affected package:** no safe in-place repair. Restoring the six original `.pyc` from the archive only lasts until the translator is used again. Disabling the check is not acceptable.
- **Interim workaround for a blocked tester:** extract the same archive again into a **new** folder and start from there. The tester's data in `%LOCALAPPDATA%\BirkNext\TesterNative` is separate from the package and is kept. The next full check after the translator has been used will fail again, so this is only a bridge to the new package.

## Regression tests
**Backend** (`TesterLauncherTests`, `OfflineTranslation`): **97/97 passed**.
- `BUG005_a_package_listing_python_caches_starts_after_python_rewrote_them_and_real_tampering_is_still_refused` (A–F):
  - freshly extracted package verifies;
  - Python rewrote its caches, and the restart still verifies with the caches reported;
  - a changed `.py` and a changed `python.exe` are refused;
  - a `.pyc` outside `__pycache__`, and a DLL inside it, are not treated as caches.
- `BUG005_the_translator_python_never_reads_or_writes_bytecode_inside_the_package`: `-I`, `-B` and a rooted cache prefix outside the package.
- `BUG005_many_files_are_hashed_by_several_readers_with_deterministic_results_and_progress` (H): 40 files, 3 changed, reported in manifest order; 80 progress reports in ascending order.
- The existing integrity, progress, partial extraction, locked file and cancellation tests pass unchanged.
- **Possible flake:** two launcher tests (`The_backend_gets_a_restore_provisioning_login_only_when_one_was_provisioned`, `Credentials_are_strong_protected_for_this_user_and_never_printed`) failed once, in a run on stale binaries after a failed build. They passed in every later run.

**End to end** (freshly extracted package, own data folder): see the next section.

## End-to-end acceptance

### What was verified (real package, 2026-11-27)
- **Package:** built from the working tree with `scripts/build-tester-package.ps1` (a scratchpad copy, `scratchpad/launcher/build-hybrid.ps1`).
  - Only PostgreSQL and k6 came from the extracted 2026-11-18 package, because the pinned download cache is not on this PC and internet is not used.
  - Built from a clean source copy without `bin`, `obj` or `App_Data`.
  - Result: 2,813 files, translator from Git LFS, manifest with **0** `.pyc`.
- **Two variants**, both archived and extracted freshly under `%TEMP%nlx\e2e2`:
  - **new**: the fixed build.
  - **oldlike**: the affected build. The probe's six `.pyc` are inside and listed (2,818 files), and the archive stores UTC as local time.
- **How it ran:** the package's own `python.exe` ran the translator helper exactly as the backend starts it (model loaded, sample translated). The launcher's own `PackageIntegrity.Verify` from the working tree ran through a `dotnet` harness (`scratchpad/launcher/verifycli`).

**Result: 13/13** (`scratchpad/launcher/e2e2.py`)

| Check | Result |
|---|---|
| oldlike, fresh: verifies | PASS (`caches=6`, `changed=0`) |
| oldlike, used **with the fix**: package unchanged | PASS (0 `.pyc` changed) |
| oldlike, used **as before** (`-I` only): Python rewrites | PASS: exactly the six files listed above |
| oldlike, **old** check after that use | «6 file(s) differ, for example runtime/translation/lib/ctranslate2/__pycache__/__init__.cpython-312.pyc». The tester's error, reproduced. |
| oldlike, **new** check after that use | PASS (`ok=True`, `changed=0`, `caches=6`) |
| tampered `sentencepiece/__init__.py` | refused (`changed: runtime/translation/lib/sentencepiece/__init__.py`); verifies again once restored |
| new build: translates (`{"ok": true}`) | PASS |
| new build: no `.pyc` in the package after use | PASS (0) |
| new build: full check after use, old **and** new verifier | PASS |

### NOT VERIFIED on this PC: the launcher executable itself
- `Start-BirkNext.exe` from a fresh build **cannot run or even be read on this PC**. Microsoft Defender attack-surface-reduction rule `01443614-CD74-433A-B99E-2ECDC07BFC25` («Block executable files unless they meet a prevalence, age, or trusted list criterion», event 1121) blocks it. It is IT policy and was not bypassed.
- So «extract → start → use → close → start again» with the real launcher, PostgreSQL and the browser has **not** been run. The pieces have been run: the verifier code, the translator start, and both package variants.
- The executable was left out of the package-level run, because the PC refuses to read it.
- **Before saying the issue is fixed:** run a CI-built (signed or trusted) package on a tester PC or in a sandbox. The steps:
  1. Extract to a new folder.
  2. `Start-BirkNext.exe`.
  3. In BirkNext, System Settings → Translation → test, or let Planlegg translate.
  4. Close.
  5. `Start-BirkNext.exe` again.
  6. `Start-BirkNext.exe --validate`.

  The launcher log (`<data>\logs`) shows «Package verified (all files …)» and, for an affected older build, «N generated Python cache file(s) … were not hashed».

### Status
- **Root cause:** CONFIRMED.
- **Fix:** IMPLEMENTED and verified at code and package level.
- **Full launcher round trip:** NOT VERIFIED (blocked by Defender ASR here).
- **A new package build is required.**
- **Overall: PARTIALLY VERIFIED, OPEN** (decision 1 of 2026-11-27): it stays open until a CI package passes the steps below on an authorized tester PC.

## CI package verification (required before BUG-005 can be called fixed)

Run on an **authorized tester PC**, or a sandbox VM where the package is allowed by the PC's normal policy. Use a package built by CI from a commit that contains these changes. The package must not be built locally.
- Do not change Defender, attack-surface-reduction, SmartScreen or other security settings.
- If Windows blocks `Start-BirkNext.exe`, stop and report that to IT. It is a separate issue, not a pass.

**Preparation**
1. Note the build: `BirkNext-Tester-Windows-x64-<version>.zip` and the `.zip.sha256` beside it. Check the digest: `Get-FileHash -Algorithm SHA256 <zip>` must equal the `.sha256` file.
2. Check the archive before extracting: no file name may contain `__pycache__` or end in `.pyc`. PowerShell:
   `Add-Type -A System.IO.Compression.FileSystem; ([IO.Compression.ZipFile]::OpenRead('<zip>')).Entries | ? { $_.FullName -match '__pycache__|\.pyc$' }` → must print nothing.
3. Use a **new, empty** folder in the user profile, for example `%USERPROFILE%\BirkNext-verify-<date>`. Use a separate data folder, for example `%USERPROFILE%\BirkNext-verify-data`. Do not use the tester's normal data folder.

**Steps**: record the time and result of each.

| # | Step | Expected |
|---|---|---|
| 1 | Extract the archive into the new folder and wait until extraction has finished | — |
| 2 | `Start-BirkNext.exe --data-dir <data folder>` | Starts; the window reports the full first check with progress; BirkNext opens in the browser. Log: «Package verified (all files: N) in X s». Note X. |
| 3 | In BirkNext: System Settings → Translation → «Test» (or open a test plan in Planlegg og klargjør so that the automatic translation runs) | A translated sample sentence is shown |
| 4 | Check the package folder: `Get-ChildItem -Recurse -Filter *.pyc <package folder>` | **Nothing** (BirkNext's Python writes no cache into the package) |
| 5 | Close BirkNext normally (close the start window, or `Start-BirkNext.exe --stop --data-dir <data folder>`) | Stops cleanly |
| 6 | `Start-BirkNext.exe --data-dir <data folder>` again | Starts; log: «Package verified (runtime files: …)» (quick check, seconds) |
| 7 | Repeat 3 and close | As before |
| 8 | `Start-BirkNext.exe --validate --data-dir <data folder>` (full check of every file) | «The package and this PC are ready». Exit code 0. Note the time. |
| 9 | Tamper check (in a **copy** of the extracted folder only): append one character to `runtime\translation\lib\sentencepiece\__init__.py`, then `--validate` on the copy | Refused: «changed after it was built: 1 file(s) differ, for example runtime/translation/lib/sentencepiece/__init__.py». Delete the copy afterwards. |
| 10 | Optional, for an older affected build installed on the same PC: start it once with the new launcher of the new package. Not possible, because the launcher is part of each package. | Not applicable: older packages are replaced by the new one |

**Pass:** steps 2–8 succeed and step 9 is refused.
**Report:**
- the build version and digest;
- the times from steps 2, 6 and 8;
- the launcher log (`<data folder>\logs`);
- any Windows security prompt.

**Afterwards:** delete the verification folders. The tester's normal data is untouched.
