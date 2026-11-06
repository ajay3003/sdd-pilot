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
