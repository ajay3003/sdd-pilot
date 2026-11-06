# Consolidated report — completing NEXT53–NEXT57 (second pass, 2026-11-06)

Branch `008-traceability-first`. **Nothing pushed or deployed.** This pass follows `final-report-ui-redesign-next53-57-2026-11-06.md`. In that first pass all five modules were Partial, and the post-NEXT57 verification had been stopped by memory pressure.

Constraints held:

- `appsettings.Local.json` and `testdata/` were untouched.
- No live Azure DevOps, TT02, m2lbdev, Altinn or Azure call. Every browser check aborted non-localhost requests and recorded none.
- Only the throwaway `birknext_gaps` database served the stack.
- No load test, readiness check, validation request, freeze, sign-off or review decision was made.
- **Utdanning ↔ SkoleAdapter is still undecided.**

## Phase 1 — memory-safe verification

### Why the previous verification ran out of memory

Measured, not assumed:

1. **The backend suite in one test host needs about 4.4 GB.** Run whole with `xUnit.MaxParallelThreads=2`, it drove free memory from 5.8 GB to below the 1.5 GB floor in about two minutes, and my guard stopped it. The single test host accumulates memory across the suite (in-memory databases, hosted test servers, fixtures).
2. **Build servers stay resident after builds.** A VBCSCompiler server (2.4 GB) and an MSBuild node (0.2 GB) from earlier builds were still running. Shutting them down raised free memory from 6.4 GB to 9.3 GB.
3. **Publishing the WebAssembly app peaks at 2–4 GB.** Earlier passes ran builds, publishes, the stack, Edge and test runs close together.
4. The rest of the machine (WSL, Edge/WebView, Defender, OneDrive) leaves about 5–6 GB free on a 31.6 GB machine.

No orphaned test hosts or headless browsers from earlier runs were found. The API (5000) and web dev server (5173) on the main checkout are the user's own and were left alone.

### How verification now runs

- **A memory guard** (`guarded.ps1`) runs one command at a time. It samples free memory every 3 s, records the peak, and stops the command's process tree below a 1.5 GB floor. It refuses to start below 3 GB free.
- **The backend suite runs in six partitions**, each in its own test host, so memory is released between them. Tests run sequentially with `MaxCpuCount=1` and `xUnit.MaxParallelThreads=2`. The partition totals reconcile exactly with the full suite: **5930 tests = 969 + 1059 + 806 + 644 + 511 + 1941**. The largest partition peaks at 3.5–4.4 GB.
- Builds and publishes use `--disable-build-servers`, and `dotnet build-server shutdown` runs before the heavy steps.
- Backend, frontend and browser runs never overlap. The stack is stopped before the final regressions.

### Verified items

- **The NEXT57 rule:** zero Required dimensions is Not assessed. The targeted backend release tests pass 8/8 (`ReleaseReadinessTests` includes "no required dimension → Not assessed" and "empty list → Not assessed"). The frontend presentation tests assert the same rule.
- **The frontend timing flake** (`ExtractionPipelineCompilerIntegrationTests`, 243 ms against 200 ms):
  - Root cause: the test asserted one wall-clock sample of the *first* call. That call pays JIT and first-use costs, and under full-suite parallel load CPU contention adds to it.
  - Fixed (30bcbc9d): one warm-up call, then the **median of five calls under the same 200 ms**. That is the actual claim: no regex is compiled per call.
  - It then passed inside the full suite, not only in isolation.
- **A second flake found during the final backend run** (`EndpointObservationHistoryServiceTests.Only_safe_metadata_is_persisted`):
  - The test serialized every stored row, including random GUID Ids, and asserted the text never contains the secret `abc`. A hex GUID can spell `abc`. Project memory records the same pattern in a proxy log test.
  - Fixed in both tests: GUIDs are masked before the check, so every other field is still checked for the secret.
  - Both classes then passed 75/75, and partition 2 passed in full (1059).

## Phase 2 — remaining acceptance criteria per module

### NEXT53 Delivery Intelligence — 9a4b66fa (+ 32be9e80)

| Criterion | Result |
|---|---|
| Project Leader milestone timeline | **Done.** A Delivery timeline of frozen whole-project reports with the current one marked. Milestones are only what the current report states ("Checklist completed", "Stated done"); there are no planned dates or forecasts. |
| Scrum Master FQS redesign | **Done.** An evidence ladder: acceptance criteria → explicitly linked tests → **candidate links (never coverage)** → no known coverage → not assessed → **executed and passed: Not assessed**. The coverage rungs filter the scope list. |
| Report history navigation | **Done.** Previous/next within reports of the same scope. A report with no neighbour of its scope says so and links all reports; the browser check found the original gap, fixed in 32be9e80. |
| Role-sensitive default views | **Done.** "View as" Project Leader / Scrum Master / Test Lead picks the default section (Overview: dashboard / features / risks) and tab (Reports: summary / change board / quality scope). It is remembered per browser and never changes a figure. |
| Shared components and calculations | **Done.** `DeliveryIntelligence` and `DeliveryIntelligenceHistory` hold the calculations; the shared components are the KPI grid, bar chart, attention list, feature list and detail, change board, timeline, quality ladder and role picker. |
| Tasks only behind technical drill-down | **Kept.** Tasks appear only under Technical details. |

### NEXT54 Test Data — eb7a0bbc

| Criterion | Result |
|---|---|
| Eight-part readiness | **Done.** Test identities · Contract (XSD) · Mapping · Fixture saved · XSD validation · Consumer binding · Environment compatibility · **Runtime (always Not assessed by Test Data)**. Each part has its own evidence and next action. |
| Required-field mapping table | **Done.** Required comes from the XSD: the element and every ancestor have `minOccurs` ≥ 1. The table is XML field \| Required \| Value source \| Status, filtered by required, required without a mapping, suggested, mapped, optional mapped, or all. Unmapped required fields are named "synthetic placeholder". |
| XML syntax highlighting | **Done.** Read-only tokens (tags, attributes, values, comments, declaration). The text is never changed; a test locks this. |
| Generation, validation, persistence and reload feedback | **Done.** Steps, status strip and four validation outcomes (first pass). The stored identity is confirmed only by the reloaded catalog, and **"Check Test Data again"** re-reads it while a save is not yet listed. |
| XSD vs business vs runtime | **Done.** Kept apart in the builder (four outcomes) and in the readiness parts. |
| **Live Tiltak 2 import → fixture → save → reload** | **Not verified.** The package is not in `testdata/`, and the M2LB copy has no source snapshot with the Skolenærvær XSD. Covered by automated tests; the manual checklist is in the NEXT54 report. |

### NEXT55 Performance Test Review — 2d8f39b3

| Criterion | Result |
|---|---|
| Explicit draft → v1 save | **Done.** Check readiness and Run test on a new draft ask "Save as v1 and check readiness"; nothing is saved silently. |
| Clear unsaved-change indicators | **Done.** A version flow (Draft → Saved vN → Readiness checked → Ready to run) and an unsaved banner naming the version a save would create. |
| Consistent authentication readiness across steps and tabs | **Done.** One scenario status strip on every tab from one calculation over the shared runtime rules: defined, runtime values, authentication (not fully known without endpoint evidence) and executable. |
| Runtime parameter validation | **Done.** Missing values are counted and listed per step. **Defect fixed:** a step was shown and counted as Executable when only its credential was usable; it now also needs its definition and runtime values. |
| Simplification of the remaining tabs | **Partial.** The repeated status, version and blocker explanations are replaced by the shared strip, flow and banner. The Workload, Thresholds, Resources and Results tab layouts were not redesigned. |
| No live performance test | **Held.** |

### NEXT56 Relationship Review — 0dbc0a5d

| Criterion | Result |
|---|---|
| Decision-impact preview for Confirm, Reject and Defer | **Done.** Before deciding, each outcome lists its consequences: the relation, the consumers that read it, both entities' reach from the shared impact walk (direct, downstream, potential, tests, unknown) and governance. **Defer never clears a blocker.** For identity conflicts the outcomes are Same entity / Separate / Defer. |
| Scalable rendering | **Done (server paging).** 20 questions and 25 entities per page; nothing renders the whole graph. Client-side `Virtualize` was not added. Pages never hold more than one page of rows, which avoids a nested scroll region. |
| Preserve evidence, confidence and decision history | **Done.** Confidence, rule and resolution are shown, with the append-only decision history per question (superseded entries stay listed). |
| Established separate from candidates | **Kept** (explorer and inspector). |
| Utdanning ↔ SkoleAdapter not decided automatically | **Held.** It has two recorded Defers (#4 superseded by #5), which the new history shows. |

### NEXT57 Release Readiness — 16fffd8a (+ 32be9e80)

| Criterion | Result |
|---|---|
| Actionable findings table | **Done.** One row per root cause, worst first: blocking, required not assessed, limitation, optional not ready. Each row has the affected dimensions and the resolving action (for example the exact review item), with a severity filter and search. |
| Per-requirement test evidence mapping | **Done.** Per feature pack in scope (lazily): confirmed vs candidate test links and imported results mapped **only by an explicit requirement reference or a confirmed test's exact name, never through a candidate**. Packs without a trace are stated. "Functional test execution" in the assessment stays Not assessed (results are not yet validated for scope, environment and version). |
| Comparison between two frozen assessments | **Done.** Both are read as frozen, never recomputed; the comparison with the current draft is kept. |
| Required/optional semantics; missing or unassessed evidence | **Done** (first pass plus the rule fix). |
| Never Ready with missing required dimensions or governance blockers | **Verified.** Rule tests pass, and on the copy: **Blocked** with 38 packs and 676 requirements. |

## Tests

| Run | Result |
|---|---|
| Backend targeted (release readiness) | 8 passed |
| Backend full, before this pass's fixes (6 partitions) | 5917 passed, 13 skipped, 0 failed |
| Backend full, final (6 partitions) | **5917 passed, 13 skipped, 0 failed**: P1 968 (+1 skipped) · P2 1054 (+5) · P3 806 · P4 644 · P5 511 · P6 1934 (+7) = 5930. P2 first failed once on the GUID flake (above); after the fix it passed in full. The largest partition peaked at 4.4 GB (2.5 GB free remained). |
| Frontend full, after NEXT57 (first pass, before this pass's changes) | 8265 passed, 0 failed |
| Frontend full, final | **8289 passed, 0 failed** (peak 2.6 GB) |

New tests in this pass:

- NEXT53: 9 (timeline, neighbours, milestones, ladder, role ×3, history navigation, sole report).
- NEXT54: 6 (XSD required rule, highlighter, field table, check again, eight parts ×2).
- NEXT55: 3 (status model, every-tab status and unsaved banner, row Executable rule) plus updated explicit-save tests.
- NEXT56: 2 (preview without deciding, test-link preview).
- NEXT57: 5 (findings, mapping rules, findings table, frozen-vs-frozen, per-requirement page).

Only intended behaviour changes altered existing assertions: an explicit save instead of a silent v1, a role default, and section/part navigation.

## Browser verification (M2LB copy, Edge, localhost only)

- **Regression scripts:** NEXT53 18/18 · NEXT54 13/13 · NEXT55 13/13 · NEXT56 14/14 · **NEXT57 14/14** (first run; the earlier attempt was stopped by memory pressure).
- **Phase 3 acceptance (`phase3-e2e.cjs`): 25/25.** Highlights:
  - **Error/retry**: Delivery Overview and Test Data with their API failed in the browser show the error with Retry, and Retry recovers.
  - **NEXT53**: a timeline of 4 reports with the current one marked. "Test Lead" opens Risks & quality and the Reports quality-scope tab. The ladder reads criteria 123 · linked 1 · **candidate 127** · none 115 · executed and passed Not assessed. History navigation shows "The only report of this scope · All 5 reports".
  - **NEXT54**: eight parts: identities Ready · contract Blocked (no XSD on the copy) · fixture Blocked · runtime Not assessed.
  - **NEXT55**: the same status on Overview, Scenario, Readiness and Run (executable false). The version flow is draft and saved done, readiness done, ready current. An edit shows "Unsaved changes to v13 … save as v14", and nothing is saved.
  - **NEXT56**: preview Confirmed/Rejected change governance and Deferred does not. History shows #5 Deferred and #4 superseded. No decision request was made.
  - **NEXT57**: findings start with the Blocking row, whose action opens the exact identity-conflict item in Relationship Review. Requirement evidence: "37 feature packs with trace data (of 38 in scope — 1 without a requirement trace) · 676 requirements · 1 with an explicit test".
  - **Accessibility sweep** (Delivery Reports, Test Data, Release Readiness): no unnamed buttons or links and no unlabelled fields.
  - **Mobile 390 px**: no overflow on Delivery Overview, Test Data, Relationship Review or Release Readiness.
  - **Read-only:** the only POSTs were the compute-only `runtime-authentication/resolve` and `scope/resolve`, which store nothing (`FunctionalQualityScopeService.ResolveAsync` has no persistence). No external host was contacted and there were no page errors.

The stack was stopped afterwards (ports 5099 and 5199 freed).

## Module status

| Module | Status | Why |
|---|---|---|
| NEXT53 Delivery | **Done** | Every listed criterion is implemented, tested and browser-verified. |
| NEXT54 Test Data | **Partial** | Only the live Tiltak 2 import scenario is unverified (package unavailable locally). The UI and functional criteria are done. |
| NEXT55 Performance | **Partial** | The Workload, Thresholds, Resources and Results tab layouts were not redesigned. The other criteria are done. |
| NEXT56 Relationship Review | **Done** | Scalable rendering is server paging, not client virtualization; this is stated above. |
| NEXT57 Release Readiness | **Done** | Every listed criterion is implemented, tested and browser-verified. |

## Manual checks still required

1. **Decide Utdanning ↔ SkoleAdapter** (Relationship Review → Identity conflict). Use the new "Preview the impact of each decision" and the side-by-side alias evidence, and give a rationale. Release Readiness stays Blocked until then.
2. **Run the NEXT54 Tiltak 2 checklist** where the package and the Skolenærvær pack exist.
3. Optional: judge the NEXT55 Workload, Thresholds, Resources and Results tabs, which were not redesigned.
4. Review before pushing: `git log 5243545c..HEAD`.
