# Test Plan Suggestions — performance (task 20 follow-up)

**Setup:**
- isolated Release install (API :5496, web :5497), real `birknext_mb` workspace: 10 stored Source Analysis snapshots holding **125 MB** of evidence JSON, plus the curated Skolenærvær guide and an existing proposal (7 cases, 2 snapshots);
- harness `scratchpad/mb/tps-perf.cjs`: per operation 1 cold sample (the first after an API start) + 5 warm samples; API over HTTP on localhost, pages in Edge;
- same machine, same data;
- no regression suite running during the four runs below (the final regression started afterwards).

## Root cause (profiled from the API's database log)

One warm workspace request loaded and **deserialized every stored snapshot's full evidence 5 times**:
- `SystemGuideService.GetAsync` read the list twice (current sources + change detection);
- `SourceBaselineService.ContextAsync` read it once more;
- Test Data drift read it again.

SQL accounted for about 0.4 s; the rest was .NET deserializing about 125 MB each time (≈ 1.6 s per load).

The largest evidence sections are database architecture (30 MB), test inventory (21 MB), test behaviour (18 MB) and evidence domains (17 MB). Source recognition needs only identity, projects, architecture components and evidence domains.

## Changes
1. **One load per request:** the guide and the baseline context share the list (workspace, create, refresh).
2. **Identity-only read:** `IqrSourceStore.ListSourceAnalysisIdentitiesAsync`, an allow-listed members read for the source context and System Understanding's source roles. Equivalence is tested. Nothing is cached, so project authorization and baseline correctness are untouched.
3. **MT-7 summary mode:** `requirement-coverage?rows=false` returns 1.2 KB instead of 357 KB for Test Plan Suggestions and the focus panel.

## Measurements (ms)

| Operation | Before (`85d4a48c`) cold / median / max | After one load per request | After identity read (final) cold / median / max |
|---|---|---|---|
| API workspace (page load, discovery, Test Data options) | 8 842 / **3 865** / 5 817 | 6 775 / 3 482 / 3 989 | 1 592 / **1 704** / 2 013 |
| API summary (Dashboard, Workflow) | 68 / 22 / 34 | 93 / 53 / 85 | 95 / 49 / 63 |
| API draft (Test Case details, Test Data links) | 77 / 15 / 21 | 101 / 24 / 143 | 35 / 41 / 84 |
| API snapshot retrieval | 54 / 12 / 17 | 56 / 27 / 67 | 24 / 35 / 82 |
| API delivery comparison | 59 / 11 / 37 | 121 / 24 / 38 | 45 / 33 / 75 |
| API export | 55 / 14 / 430 | 99 / 47 / 62 | 46 / 39 / 82 |
| API Test Data catalog | 159 / 10 / 11 | 240 / 12 / 36 | 29 / 20 / 25 |
| API requirement coverage, rows / summary (MT-7) | — | — | 106 / 72 / 77 · 76 / 79 / 83 |
| **Page, full reload:** Test Plan Suggestions (discover) | 9 165 / **8 999** / 9 136 | 8 480 / 7 498 / 8 107 | 14 847 / **14 098** / 14 216 |
| Page, full reload: Proposal review | 9 004 / 8 759 / 8 910 | 9 203 / 7 990 / 8 735 | 13 854 / 14 012 / 14 347 |
| Page, full reload: Recommended Workflow | 2 277 / **2 146** / 2 360 | 2 801 / 2 536 / 2 861 | 6 278 / **6 269** / 6 411 |
| In-app navigation: Test Plan Suggestions | not measured | not measured | 3 914 / 4 646 / 5 117 |
| In-app: Proposal review | — | — | 5 029 / 4 684 / 5 683 |
| In-app: Requirements Traceability (coverage panel) | — | — | 1 413 / 824 / 897 |

## Reading the numbers honestly

- **API:** the workspace call (the Test Plan Suggestions read path) is **2.3× faster warm** (3.9 s → 1.7 s) and its slowest sample dropped from 5.8 s to 2.0 s. Every other Test Plan Suggestions operation stays under 100 ms warm.
- **Full-page reload:** the network timeline shows that about 12 s of a reload is the shared app shell booting:
  - workspace bootstrap 1.6 s;
  - workspace documents 1.9 s;
  - Technology Coverage 3.7 s;
  - WASM processing.

  Test Plan Suggestions' own requests are the last ~2.2 s.
- **OPEN ANOMALY — not explained, not hidden:** between the "one load" build and the "identity read" build, full-page loads rose from ~8 s to ~14 s, and Recommended Workflow from 2.5 s to 6.3 s.
  - The only code difference between those two builds is the identity read in the backend.
  - `api/source-context` (identity read) measured 2.1–3.6 s inside the page, where Recommended Workflow sends it 4 times in parallel.
  - A clean A/B timing of `source-context` with the full and the identity read was not completed: the final regression was occupying the machine, and the user asked not to wait.
  - Until that A/B is done, the identity read must be treated as a **possible page-load regression**, despite the faster API call it serves. Gap MT7-G7 / TPS-P1.

## Not changed
- No caching across requests.
- No change to authorization, baseline selection or Test Data drift (drift needs the full evidence for its review state).
