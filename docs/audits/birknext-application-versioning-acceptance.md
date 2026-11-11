# BirkNext — Application versioning & release traceability: acceptance (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `b92c5215`.
- **Scope of systems:** no push, no deployment, no Azure DevOps access. Synthetic pipeline variables only.

Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN · NOT SUPPORTED.

## 1. Environment

| Item | Value |
|---|---|
| Synthetic CI | `TF_BUILD=True`, `BUILD_BUILDID=152` (second build: 153), `BUILD_BUILDNUMBER=20261111.1`, `BUILD_SOURCEVERSION=a1b2c3d4e5f60718293a4b5c6d7e8f9012345678`, `BUILD_SOURCEBRANCH=refs/heads/feature-test`, `BUILD_DEFINITIONNAME=qa-review-studio-ci` |
| CI emulation | The pipeline's steps, run locally: resolver, Release publish of backend and frontend with the identity props, packaging, `check-migrations`, `write-build-manifest`, `validate-package -CreateManifest`, then archive. Isolated folders under the session scratchpad. |
| Round trip | The archive verified and extracted to `C:\bn-av-extract`. The **extracted** backend ran on :5296 against a fresh database `birknext_bv_20261111`, and the **extracted** frontend was served on :5298. |
| Contrast | The run-153 frontend (mismatch). A local Release frontend + local Debug backend on :5295 (Local channel; the scratch tree has no git metadata). |
| Browser | Microsoft Edge (Playwright); every non-localhost request aborted |
| Oracles | Expected values computed from the synthetic inputs and the props file read as text. The independent Python oracle reads raw bytes: UTF-8 metadata and the UTF-16 version resource in the DLL, the UTF-8 string in the webcil `.wasm`, JSON files, and SHA-256 recomputed with hashlib. psql for migration counts. Never the scripts' own summaries. |

## 2. Automated tests

| Suite | Result |
|---|---|
| `scripts/tests/BirkNextBuildIdentity.Tests.ps1` (Windows PowerShell 5.1) | 29/29 PASS |
| `scripts/tests/BirkNextPreflight.Tests.ps1` (unchanged module, regression) | PASS, 0 failures |
| Backend focused (SystemInfo, BuildInfoSchema, ExecutionManifest, FullBackup, WorkspacePackage, AdminService, BrowserCompanion) | 157/157 PASS |
| Frontend focused (ApplicationBuildCard, SystemSettings, NavMenu, ExecutionResults) | 144/144 PASS (after making the card's client optional; the first run failed 30+ System Settings tests whose hosts did not register it) |
| YAML | Parsed offline (PyYAML); both publish commands carry the identity props; static checks in the script tests |

## 3. Synthetic CI package: independent oracle (18/18 PASS, final package `ci152b`, run on the extracted archive)

| ID | Check | Result |
|---|---|---|
| CI-01 | Application version 1.0.0 from the props file | PASS |
| CI-02 | Backend DLL metadata `1.0.0-ci.152+<sha>` | PASS |
| CI-03 | DLL version resource: ProductVersion and FileVersion `1.0.0.152` | PASS |
| CI-04 | DLL metadata: channel, run id, commit, build number, branch | PASS |
| CI-05 | The compiled frontend (`BirkNext.Web.wasm`, webcil, trimmed) carries `1.0.0-ci.152+<sha>` | PASS |
| CI-06 | Frontend `build-info.json` | PASS |
| CI-07 | `build-manifest.json` fields (schema, versions, run, number, commit, branch, provider, definition, provenance) | PASS |
| CI-08 | Manifest component versions equal the compiled components | PASS |
| CI-09 | Edge Companion keeps its own version (0.1.0) and build id, recorded as bundled | PASS |
| CI-10 | Artifact name `BirkNext-1.0.0-ci.152` | PASS |
| CI-11 | Package manifest format 2: all 850 file hashes recomputed and matching | PASS |
| CI-12 | No developer or local settings files | PASS |
| CI-13 | No secret-shaped value or user path in any packaged file | PASS (see run history) |
| CI-14 | The build-only identity props file is not packaged | PASS |
| CI-15–CI-18 | One archive named from the build version; external `.sha256` = SHA-256 of the final bytes; entries use `/`; build manifest, package manifest and `.env` included; no checksum inside | PASS |

## 4. Round trip and tampering (11/11 PASS, copies only)

| ID | Check | Result |
|---|---|---|
| RT-01 | Clean copy validates with `-RequireBuildManifest` | PASS |
| RT-02/03 | Archive + checksum created; checksum verifies; the fresh extraction validates (hashes + provenance) | PASS |
| RT-04 | An existing archive is never overwritten | PASS |
| TP-01 | A modified packaged file → "differ from what the build produced" | PASS |
| TP-02 | A modified build manifest → build provenance failure | PASS |
| TP-03 | A patched version string inside the `.wasm` → "does not carry" | PASS |
| TP-04 | Frontend `build-info.json` disagreeing with the manifest → detected | PASS |
| TP-05 | One flipped archive byte → SHA-256 mismatch | PASS |
| TP-06 | The original archive is untouched | PASS |

## 5. Browser acceptance (21/21 PASS, final run)

| ID | Check | Result |
|---|---|---|
| BV-01–BV-06 | `api/system/build-info` from the extracted CI backend: CI 152, complete provenance; the packaged manifest Consistent with SHA-256 equal to the file's; Application migrations applied = psql count, all four contexts Current; Companion bundled 0.1.0, not connected, NotAssessed; no paths, connection strings or configuration | PASS |
| BV-10–BV-13 | System Settings: version 1.0.0, "CI build", "Build 152 · 20261111.1", short commit; frontend row from the compiled CI frontend; "Same build"; manifest "Matches the running build"; schema "Current"; Companion "Compatibility not assessed"; no "not official" note | PASS |
| BV-14 | Sidebar: "BirkNext 1.0.0 / build 152" (from the frontend itself) | PASS |
| BV-15/16 | Keyboard: the details open with Enter (full commit, manifest SHA-256); Copy by keyboard puts the actual frontend, backend, commit, run and match on the clipboard | PASS |
| BV-L1 | No horizontal scroll at 1440 / 1280 / 1024 / 768 / 390 px with the details open | PASS (after AP-D5) |
| BV-20/21 | Run-153 frontend + run-152 backend: "Different builds" with both versions; the summary shows the backend's 152, the sidebar this frontend's 153 | PASS |
| BV-30–BV-33 | Local: "Local build", no build number, "Commit unknown", "not an official CI build or release"; match Not assessed; no manifest row; sidebar "local build" | PASS |
| — | Unauthenticated `GET api/system/build-info` → 401 on both backends | PASS |
| — | Startup log line on both backends (CI 152 Complete; Local Unknown) | PASS |
| BV-Z | No request left localhost; no page or console error | PASS |

## 6. Local build with real git

On the real working tree, MSBuild ran only the identity target: `git rev-parse` and `git status`, with no build output. It gave:

```
InformationalVersion 1.0.0-local+b92c5215e608a96f701491c110fde04d1ee43c9a.dirty
Channel Local · SourceCommit b92c5215… (= git rev-parse HEAD) · SourceDirty true (33 changed/untracked paths) · FileVersion 1.0.0.0 · AssemblyVersion 1.0.0.0
```

- `resolve-build-identity.ps1` locally: Local, the same commit, with a warning about uncommitted changes.
- With `-Release` it is refused, both as local and as dirty.

## 7. Run history

| Run | Result | What failed and why |
|---|---|---|
| Round trip 1 | RT-02 FAIL | **AP-D2:** `System.IO.Compression` not loaded on Windows PowerShell 5.1. A 0-byte partial archive was left, which made RT-04 pass for the wrong reason. Fixed; the partial file was removed and the run repeated. |
| Round trip 2 | RT-02 FAIL | **AP-D3:** `$archive`/`-Archive` name clash. Fixed. |
| Round trip 3 | 11/11 | — |
| Oracle 1 | 17/18 (CI-13) | Harness: the pattern `.claude` matched XML doc text about `.claude/skills` paths in analysed repositories, not a local folder. The pattern was narrowed to user-profile and session paths. |
| Browser 1 | 20/21 (BV-L1 at 768 px) | **AP-D5:** layout. Fixed and the **whole CI package rebuilt** (`ci152b`). |
| CI build 2 | Package manifest step failed | **AP-D4:** long-path hashing on Windows PowerShell 5.1. Fixed in the validator and the archiver; rebuilt. |
| Round trip / oracle / browser on `ci152b` | 11/11, 18/18, 21/21 | — |

## 8. Full regression

Run sequentially with the existing memory guards; no timeout changed, no test disabled.

| Step | Result |
|---|---|
| Backend partitions 1–6 (on `edae017a`) | **6,461 passed, 19 skipped, 1 failed** (1031 / 1098 / 840 / 652 / 520 / 2320). The one failure is `ScenariosQueryTests.Scenarios_With100Records_…WarmedP95CompletesWithinTwoSeconds`, a timing test: p95 was 2,513 ms against 2,000 ms, with two slow samples (5,569 and 2,513 ms) while free memory fell to 1.6 GB during partition 6. That query path is not touched by this change. |
| The same test class rerun alone (no timeout changed) | **5/5 passed** |
| .NET 8 parse guard (`CompilerCompatibilityTests`, partition 6) | PASS |
| Frontend full suite | **8,586 / 8,586 passed** |
| Release publish of backend and frontend | PASS (synthetic CI builds 152, 153 and a local Release frontend) |
| Azure DevOps pipeline | NOT RUN (no pipeline triggered; .NET 8 SDK not installed locally) |
