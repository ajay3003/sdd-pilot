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

## Final release acceptance follow-up (2026-11-13, HEAD `59531f1c`)

### Snapshot A/B: the open anomaly above is resolved
On the same database (Debug test build), measured warm:
- source-context with the **full** read: ~3.1–3.4 s (8.7 s on the first call);
- with the **identity** read: ~1.6–1.8 s.

The identity read is not a page-load regression. It still fetches the full rows from PostgreSQL and skips members only while deserializing:
- SQL full-row fetch 979 ms (125 MB, 10 rows);
- length-only fetch 594 ms.

A projection on the PostgreSQL side is a possible follow-up; it is not done.

### API memory: root cause (isolated Release API :5496, watchdog-guarded)

Harness: `scratchpad/mem/{guard.ps1,drive.cjs,burst.cjs}`.
- The watchdog samples the API's private bytes and the system's available memory every second.
- It stops only the isolated API, at 3 GB private or under 1.5 GB available.
- The calls are the Recommended Workflow page's read-only GETs, made one endpoint at a time and then in parallel.

| Experiment | Result |
|---|---|
| Sequential, Workstation GC, 1 + 5 full rounds | End-of-round private memory 1 329 / 1 195 / 1 295 / 1 326 / 1 042 MB; handles flat (~520). Memory rises **and falls**: no accumulation, **no leak** |
| Heaviest single calls | bootstrap: +300–700 MB temporary (2.1 MB response). Technology coverage: +500–860 MB, 3.5 s. source-setup: 3.3–4.1 s |
| 9 parallel calls (bootstrap, documents, source-context, technology coverage ×3 environments, source-setup ×3) | 1.0 → **3.6 GB in 3.5 s**: the watchdog stopped the API. This is the earlier 4.2 GB |
| Same burst with the .NET heap hard-capped at 1.5 GB | 8 of 9 calls return 500 (OutOfMemory): the burst's **live** set really exceeds 1.5 GB. Afterwards an idle 1.57 GB fell to 392 MB at the next request, and every sequential call succeeded under the cap: the leftover is collectable garbage |
| Server GC (what the package ran: `System.GC.Server: true` in the published runtimeconfig, not overridden by the launcher), 24-thread machine, sequential only | **3.2 GB within two rounds**: the watchdog stopped the API. Workstation GC: 1.0–1.3 GB |

**Root causes:**
1. Several endpoints deserialize Source Analysis evidence per request:
   - technology coverage read the whole snapshot window in full to choose one;
   - source-setup reads the full list through `ReviewSourceEvidenceProvider.ListAsync`;
   - bootstrap and documents materialize the workspace documents.
2. The browser issues these calls together, across three environments.
3. The packaged backend ran Server GC, which keeps one heap per logical processor.

**Fixed:**
- `bc8a893b`: technology coverage resolves the baseline from the identity read and reads only the chosen snapshot in full. Measured 3.5 s → 2.0 s per call. A test proves the full evidence is still reported, with a negative control.
- `59531f1c`: the launcher sets `DOTNET_gcServer=0` for the TesterNative backend only. Covered by a launcher unit test; the string is present in the packaged `Start-BirkNext.exe`.

**Not fixed (open, TPS-P2):**
- source-setup, bootstrap and documents still allocate hundreds of MB per call.
- A browser burst still peaks at **2.66–2.9 GB** private on Workstation GC: 2.9 GB in the eight-view workflow run and 2.66 GB in the page-timing run. It settles to 1.0–1.3 GB.
- On a tester machine with 8 GB that is a real risk.
- Recommended next step: identity- or summary-only reads for source-setup, and a database-side projection for the identity read.

### Page timings (`perf-pages.cjs final 3`)

Setup: 1 first load in a new browser context, then 3 warm full reloads, Test Lead view. Content-ready means the page's own terminal state.

| Page | First load | Warm median (min–max) | API calls | Slowest call |
|---|---|---|---|---|
| Recommended Workflow | 23 842 ms | 23 445 (22 544–23 623) | 18 | source-setup 6.6–7.7 s |
| Test Plan Suggestions (discover) | 15 061 ms | 13 912 (13 860–13 947) | 9 | technology coverage 2.1–2.4 s |
| Dashboard | 6 980 ms | 6 436 (6 418–6 649) | 4 | bootstrap 1.4–1.7 s |

- About 6.5 s of every reload is the WASM app shell; the rest is the page's API calls.
- The Recommended Workflow figure is **not comparable** with the 6.3 s in the table above. That harness (`tps-perf.cjs`) waited for a different ready marker. This one waits for the Test Lead focus panel's actions, which need source-setup.
- These numbers were measured on the same busy developer machine. No regression suite ran alongside, but about 4–6 GB was available. They are a baseline, not a target.
