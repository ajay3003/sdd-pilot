# BirkNext Test Execution Results

**Execution date:** 2026-11-06  
**Execution baseline:** `008-traceability-first` / `546584f1`  
**Final observed HEAD:** `008-traceability-first` / `63ee0980` (not built or tested in this audit)  
**No external systems contacted.** No tests were modified.

## Results

| Suite / command | Result | Interpretation |
|---|---|---|
| `dotnet build AIAssisted/backend/BirkNext.Api/BirkNext.Api.csproj -c Release --no-restore --verbosity minimal` | **PASS** — 0 errors, 175 warnings; 1:05.63 | Current API source built. Warnings were not individually classified as defects. |
| Initial API test project normal reference build | **BLOCKED before discovery** by `BirkNext.Web.csproj:23` stale scoped CSS guard | No tests ran. Not a test failure and not a pass. |
| Focused test project build with `-p:BuildProjectReferences=false` | **BUILD PASS**; current test source compiled with warnings | Web dependency was not rebuilt; it may be a pre-existing assembly. |
| Focused security/import test filter (see manifest) | **PASS: 90, FAILED: 0, SKIPPED: 0**, 24 seconds | Selected source-built test assembly only. Not full regression coverage. |
| Full `BirkNext.Api.Tests` suite / six partitions | **NOT RUN** | No full-suite claim. |
| Full `BirkNext.Web.Tests` suite | **NOT RUN** | Concurrent UI/test changes and Web CSS build guard. |
| Localhost browser journeys | **NOT RUN** | No host/browser launched. |

## Machine-readable execution manifest

```json
{
  "auditDate": "2026-11-06",
  "repository": "BirkNext",
  "branch": "008-traceability-first",
  "executionCommit": "546584f1",
  "finalObservedHead": "63ee0980",
  "executions": [
    {
      "suite": "BirkNext.Api Release build",
      "command": "dotnet build AIAssisted/backend/BirkNext.Api/BirkNext.Api.csproj -c Release --no-restore --verbosity minimal",
      "result": "PASSED",
      "errors": 0,
      "warnings": 175,
      "evidence": "Tool output in audit session; no repository log file created"
    },
    {
      "suite": "Focused API security and Project Import tests",
      "command": "dotnet test AIAssisted/backend/BirkNext.Api.Tests/BirkNext.Api.Tests.csproj -c Release --no-build --no-restore -p:BuildProjectReferences=false --filter \"FullyQualifiedName~LocalSessionSecurityTests|FullyQualifiedName~ActiveEventAuthorizationTests|FullyQualifiedName~AltinnTestingAuthorizationTests|FullyQualifiedName~LocalTrustedExecutionTests|FullyQualifiedName~ProjectImportServiceTests\" --logger \"console;verbosity=minimal\" --results-directory AIAssisted/backend/BirkNext.Api.Tests/bin/Release/audit-results",
      "result": "PASSED",
      "passed": 90,
      "failed": 0,
      "skipped": 0,
      "duration": "24 seconds",
      "buildIdentity": "API test project compiled in Release immediately before execution with BuildProjectReferences=false; API Release project built separately from current source; Web reference not rebuilt",
      "evidence": "AIAssisted/backend/BirkNext.Api.Tests/bin/Release/audit-results (ignored build output; no tracked report/log)",
      "limitations": ["Focused filter only", "No full backend suite", "No frontend suite", "No browser/runtime execution"]
    },
    {
      "suite": "Full backend suite",
      "command": null,
      "result": "NOT_RUN",
      "reason": "Bounded focused pass only; normal project-reference build blocked by Web stale scoped CSS guard"
    },
    {
      "suite": "Full frontend suite and browser journeys",
      "command": null,
      "result": "NOT_RUN",
      "reason": "Web build guard plus concurrent edits to topology UI/tests; no app launched"
    }
  ]
}
```

The first VSTest attempt also failed to create its results directory under `C:\tmp` because access was denied; that attempt ran no tests. The successful rerun wrote results under ignored `bin/Release/audit-results`.

## Test coverage conclusion

BQA-003 is partially addressed: the current API test source was compiled and selected security/import tests executed. Full test-suite acceptance criteria remain unmet. Historical counts from master-hardening reports are not current execution evidence.

## Verification completion — current source `7410e179` (2026-11-06)

The sections above are the **historical** audit at `546584f1` (final observed HEAD `63ee0980`). They are kept unchanged. This section records the
completion run against the current source. Source state verified: branch `008-traceability-first`, HEAD `7410e1793be162bd589762c9e65749d0f8621c46`; the isolated build worktree's
3,541 source files (`AIAssisted/`, `scripts/`) are byte-identical to that commit (blob hashes compared; only 7 `AIAssisted/docs/*.md` reports absent).
Uncommitted user files left untouched: `AIAssisted/backend/BirkNext.Api/appsettings.Local.json`, `AIAssisted/testdata/`. No push, no deployment,
no external system (M2LB, TT02, Altinn, MU, MM, Bufdata, Azure DevOps) contacted; browser runs abort every non-localhost request.

### Results at current source

| Suite | Source | Result |
|---|---|---|
| API Release build | `fa521a9d` (API identical except `LocalSessionService`, rebuilt in Release by the package publish at `37b918f7`) | **PASS** 0 errors, 175 warnings |
| Full backend, 8 partitions | `37b918f7` (= HEAD for backend/shared) | **5,972 passed · 13 skipped · 1 failed** (5,986 results) |
| Backend reconciliation | — | 5,970 discovered names; every one executed (theory rows listed without arguments appear with them; 6 identical names are xUnit-truncated distinct theory rows) |
| Skipped (13) | — | gated real-system tests: real M2LB DEV detection (5), real ZAP (6), real headless Edge (1), real archive (1) — correctly not run |
| Failed (1) | — | `ReadinessStatusIntegrityTests…ReachesDependencyProbe` (BQA-012), isolated re-run 3/3 passed (9/9 class tests each) |
| Security/import focused | `37b918f7` | **92/92** |
| Full frontend | `7410e179` (HEAD) | **8,444 / 8,444** |
| Tester package (local replica) | `37b918f7` | preflight tests 0 failures; migrations 4 folders / 59 OK; publish OK; clean-copy validation exit 0; launcher `-ValidateOnly` exit 0 |
| Browser suites | `7410e179` build | 16 scripts, all passing (see UI/browser report) |

### API warning classification (175, all `BirkNext.Api.csproj`)

| Code | Count | Assessment |
|---|---|---|
| CS1573 / CS1570 / CS1587 / CS1574 / CS1734 | 113 / 13 / 7 / 5 / 1 | XML documentation only; no runtime effect. Accepted. |
| CS8604 / CS8603 / CS8602 / CS8601 / CS8629 | 8 / 4 / 3 / 2 / 1 | Nullability. Higher-risk sites inspected: `PerformanceTestExecutionService:912` (`StartedAt` is set from a non-null local first), `TestDataXmlBinder:37` (path validated non-null above), `FrontendAccessibilityReviewService:451`, `BrowserEvidenceSanitizer` (returns its null input) — no confirmed defect. Accepted, listed for follow-up. |
| CS0612 | 8 | Obsolete Playwright `NoWaitAfter` in browser-auth helpers; behaviour unchanged. Accepted. |
| CS1998 | 4 | async without await; no correctness impact. Accepted. |
| EF1002 | 2 | Data-reset `DELETE FROM` with EF-model table names delimited by `ISqlGenerationHelper`; no user input. False positive. |
| CA1416 | 2 | Registry calls guarded by `OperatingSystem.IsWindows()` (analyzer cannot see the guard). Accepted. |
| CS0114 / CS9113 | 1 / 1 | `AzureEnvironmentController.SignOut` hides `ControllerBase.SignOut()` (explicit route, harmless); an unread constructor parameter. Accepted. |

No warning was suppressed; none hides a confirmed defect.

## Gap closure — 2026-11-07: test execution

The sections above are kept unchanged. Source: HEAD `04845c9b` for all product code (later commits `6e794e94`/`5da3c952` change only
`scripts/start-local.ps1` and a document); the isolated worktree's content equals HEAD (blob hashes compared). Every run was sequential
under the 1.5 GB memory guard, one test host at a time, `MaxCpuCount=1`.

### Backend full regression (G4)

| Partition | Passed | Skipped | Failed | Notes |
|---|---|---|---|---|
| 1 SourceAnalysis, Integrations, PerformanceTests | 984 | 1 | 0 | |
| 2 TargetEnvironmentDetection, TestData, LocalHttpsProxy, AltinnTesting | 1,056 | 5 | 0 | includes the new shutdown tests |
| 3 IntegrationJourneys, CriticalE2E, ApiQuality, AuthenticatedReview | 806 | 0 | 0 | |
| 4 Canonical…AzureEnvironment (incl. FrontendQualityEngines) | 646 | 0 | 0 | `ReadinessStatusIntegrityTests` (BQA-012) passed |
| 5 Topology…IntegrationQuality (incl. TestEvidence) | 515 | 0 | 1 | environment-dependent failure, below |
| 6a Controllers | 127 | 0 | 0 | partition 6 split in three: the unsplit run was stopped by the memory guard (1.3 GB free) |
| 6b remaining Services | 547 | 0 | 0 | |
| 6c everything else | 1,305 | 7 | 0 | |
| **Total** | **5,986** | **13** | **1** | 6,000 results |

**Reconciliation:** `--list-tests` discovers 4,697 test methods; all 4,697 ran; no method ran in two partitions. 16 display names occur more
than once inside one partition — theory rows whose data renders to the same display name (e.g. case variants, truncated long paths), not
double execution. The 13 skips are the existing opt-in/conditional skips.

**The one failure** — `TestEvidenceTests.RealM2lb_DiscoversItsXunitV3TestEstate_WithTheGenericProvider` (BQA-018) reads
`%USERPROFILE%\Downloads\M2LB (2).zip`, a real archive outside the repository, and skips when it is absent. The file was replaced on
2026-11-07 00:57 with a version that contains only 1 of the 5 test projects the test expects. No discovery code changed since `f1b7f041`,
where it passed. Not a product regression, but not a pass either.

**G4: PARTIAL** — the previously intermittent failure (BQA-012) is fixed at its root and passed; every product test passes; the single
failure depends on a file outside the repository that changed during the run window. It becomes PASS when that test is run against the
archive it was written for (or is changed to a fixture the repository owns).

Focused runs during the fixes: proxy suite 79/79 and 5× 13/13 (BQA-012); Git Sources backend 146/146; BQA-014 frontend 241 related tests.

### Frontend full regression (G5 kept)

Web.Tests rebuilt from the worktree (content equal to HEAD), then the whole suite: **8,452 / 8,452 passed**, 0 skipped, 0 failed
(8,444 at `7410e179` + 6 Git Sources UI tests + 2 Target Environment materialization tests). **G5 stays PASS.**

### Launcher (G10)

`scripts/tests/BirkNextPreflight.Tests.ps1`: 0 failures. Launcher behaviour on a copy of the packaged launcher (PostgreSQL pointed at an
unused port; nothing started):

| Case | Before (`04845c9b`) | After (`6e794e94`) |
|---|---|---|
| `-ValidateOnly`, neither Podman nor Docker on PATH | "preflight passed", exit 0 | missing runtime explained, exit 2 |
| `-ValidateOnly`, Podman running | exit 0 | exit 0 |
| `-ValidateOnly -SkipContainers` (pipeline smoke test) | exit 0 | exit 0 |
| start with port 5173 owned by another program | "Startup stopped …", exit 0; owner not stopped | same message, exit 3; owner not stopped |

## Final remediation — 2026-11-07

Frontend full: **8,463 / 8,463** at `60671884`. Backend: partitions 1–5, 6a, 6b at `13c7821c` **4,697 passed, 11 skipped, 0 failed**;
partition 6c **NOT RUN** (stopped by memory protection; not restarted without the user's go-ahead); backend suites touched afterwards pass
(Git Sources/Azure/Admin/sign-in 181). Count reconciliation: result rows (facts + theory rows) vs `--list-tests` methods — see the summary §4.
Preflight tests: 0 failures (7 endpoint, 8 readiness, 8 container-probe cases added). Opt-in real archives: 4 passed, 1 skipped, 1 failed
(archive drift).

## G4 complete run — final HEAD `ffb75ab1` (2026-11-07)

All six partitions (6 split in 6a/6b/6c) on source byte-identical to HEAD: **6,011 passed, 0 failed, 19 skipped** (opt-in external tests:
real archives 7, live M2LB dev 5, real ZAP 6, real headless Edge 1). 6,030 result rows; 4,716 of 4,716 discovered methods executed; none in two
partitions. Memory floor never reached. **G4: PASS.** Details: `birknext-final-remediation-summary-2026-11-07.md` §11.
