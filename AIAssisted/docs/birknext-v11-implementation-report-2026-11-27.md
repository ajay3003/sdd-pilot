# BirkNext V11 — implementation report (2026-11-27)

**Order:** after V10 (`9af21c93`, `afade54e`) and V12 (`ea835190`), as the user decided.
**Commit:** the user asked for V11 to be committed when done (this overrides the brief's «do not commit»). Not pushed.
**Status values:** PASS / FAIL / PARTIAL / NOT VERIFIED (release); IMPLEMENTED / VERIFIED / PARTIAL / NOT IMPLEMENTED (items).

## 1. What V10 and V12 had already solved (not repeated)
V11 did not repeat any of these:
- scenario occurrences without a case, and a case never counted as an occurrence (V10 FUN-015);
- approval ≠ readiness (V10 BUG-004);
- the E2E populations (V10 FUN-014);
- search beyond the initial window (V10 P1);
- the exact drill-down from Testoversikt (V10 stabilization);
- one primary action and a row that opens its scenario (V12);
- create and edit separated (V12);
- blocked ≠ verified in progress (V12);
- «Vurdert av tester» (V12).

## 2. What V11 changed

### 2.1 Backend test stability: the root cause of the memory growth, PROVEN and fixed (IMPLEMENTED + VERIFIED)
**Problem.**
- A full backend run in one process exhausted memory.
- Partitions peaked at 0.6–3.9 GB, and memory «accumulated within one process».
- The integration classes start a complete in-process API host **per test**: xUnit creates one class instance per test, and `InitializeAsync` builds a `WebApplicationFactory`.

**Method:** `Diagnostics/TestHostMemoryDiagnostics`, real PostgreSQL. Repeated create → request → dispose cycles, the managed heap after a full collection, then root hunting:

| Step | Measurement | Conclusion |
|---|---|---|
| 6 hosts, nothing changed | +12.0, +23.8, … +70.6 MB (**11.7 MB retained per disposed host**) | proven retention |
| EF Core `ServiceProviderCache` | stays at 1 entry | **disproven** as cause |
| Static fields, all assemblies, before/after 3 hosts | **only one grows:** HotChocolate `RequestExecutorResolver.ApplicationUpdateHandler._actions` (1 → 4) | candidate |
| Releasing that registration alone | still 11.87 MB/host | **not sufficient alone** |
| Live timers per host | `Timer.ActiveCount` **+1 per host** | candidate |
| Waiting 150 s | nothing released | not the 2-minute HttpClient expiry alone |
| Enumerating `TimerQueue` | 7 × `ActiveHandlerTrackingEntry`, client **`IAiaCertificateFetcher`** (one per host) | identified |
| Typed client released (product fix) **and** HotChocolate registration released | **+0.7 MB after 6 hosts** (was +71 MB); timers back to the 2 runtime/test ones | **proven: two independent roots, each enough to keep a host** |

**Root causes:**
1. **Product defect.** The singleton `PerformanceTestProviderRegistry` resolved the **typed HttpClient** `IAiaCertificateFetcher` at construction. Every host therefore created a handler through `IHttpClientFactory`, and its tracking entry and timer kept the host reachable after disposal.
   - In production this was also a known anti-pattern: a typed client captured by a singleton never rotates its handler, so DNS changes are not picked up.
   - **Fix:** `OnDemandAiaCertificateFetcher` asks the service provider for the typed client **on each use**. Nothing is created at startup.
2. **Library behaviour (HotChocolate 14).** Every request-executor resolver is added to a static hot-reload list that is never pruned.
   - **Fix (test assembly only):** `TestInfrastructure/HostStaticRegistrations`, started by a module initializer, prunes once a second the entries whose resolver is **disposed**.
   - It works under the same lock HotChocolate uses (verified from its IL: `Monitor.Enter` around add and iterate).
   - Live hosts keep their entry. Hot reload never happens in a test run.

**After the fix** (`A_disposed_API_host_releases_its_memory…`): retention oscillates around 0–2 hosts (only hosts disposed within the last second); trend 2.4 MB/cycle. **The diagnostic passes.**

**Full-suite effect:** see §3 (the partitioned run's peaks after the fix).

### 2.2 Finn og forstå: a small filtered result is shown directly (IMPLEMENTED + VERIFIED in bUnit)
- **Before:** filtering by «Ende til ende» (11 results) showed them behind collapsed groups.
- **Now:** any search or filter with **at most 25 results** opens all its groups. Grouping is kept, an explicit close is respected, and without a filter the groups start collapsed as before.
- **Tests:** `V11_a_small_filtered_result_is_shown_directly…`, `V11_without_a_filter_the_groups_start_collapsed_as_before`; G03 updated (the child group is now visible, an intended change).

### 2.3 V12 browser finding fixed: focus could not land on a row (IMPLEMENTED)
- In a real browser, «Lukk» did not return focus to the row (V12 browser check 24/25): `focus()` on an `<li>` without `tabindex` does nothing.
- The same affected V10's «focus the selected scenario».
- Rows now have `tabindex="-1"`: focusable from code, not an extra Tab stop.
- Test asserts the attribute. Browser re-verification: see the release-readiness report.

## 3. Tests
| Suite | Result |
|---|---|
| Frontend, full (after V11 small-result change) | **9,921 / 9,921** |
| Frontend: Discover + work queue (after the focus fix) | **461 / 461** |
| Backend: host memory diagnostics | 3 diagnostics; the release assertion passes after the fix |
| Backend: partitioned, separate output folder | see `birknext-v11-release-readiness-2026-11-27.md` |

## 4. Files changed (V11)
- `backend/BirkNext.Api/Services/PerformanceTests/TlsIntermediateRepair.cs`: `OnDemandAiaCertificateFetcher`.
- `backend/BirkNext.Api/Program.cs`: the registry gets the on-demand fetcher.
- `backend/BirkNext.Api.Tests/TestInfrastructure/HostStaticRegistrations.cs`: pruner and helper.
- `backend/BirkNext.Api.Tests/Diagnostics/TestHostMemoryDiagnostics.cs`: the evidence.
- `frontend/BirkNext.Web/Pages/ManualTestingDiscover.razor`: small result opens groups; row `tabindex`.
- `frontend/BirkNext.Web.Tests/Pages/DiscoverStoryStatusAndTypeTests.cs`, `DiscoverWorkspaceTests.cs`: tests.
- Docs: the six V11 reports.

## 5. Data safety
- All verification ran on isolated instances (disposable database and App_Data) and temporary PostgreSQL test databases.
- A BirkNext API that was already running from the repository's build output (PID 29388, since 12:22) was **not** stopped. The backend tests were built into a separate folder instead.
- No Azure DevOps writes. No reset.
