# BirkNext — backend regression stability (2026-11-26/27)

The user asked for commits («continue … and commit»), overriding the brief's no-commit line; nothing was pushed.

**Acceptance: a complete backend test run without failures or resource exhaustion — MET, by running the suite in partitions.** A single-process run of the whole suite is still not stable on this machine; see Remaining risk.

## Starting point
- Failures in full runs:
  - `CI_DEF_3_the_connect_callback_lets_an_allowed_host_through` (`Services/ApiQuality/ApiReviewEngineContractFetchTests.cs`);
  - `ObservedTrafficIsCorrelatedToItsPageByRefererEndToEnd` (`Services/LocalHttpsProxy/LocalHttpsProxyServiceTests.cs`).
- Both pass alone.
- A «quiet» full run was stopped by Claude Code because the system ran critically low on memory.

## Evidence collected
| # | Observation | Source |
|---|---|---|
| 1 | CI_DEF_3: the fetcher cancels after **20 s** (`CancelAfter(TimeSpan.FromSeconds(20))`) but returned after **47.9 s**; its own test server had answered | the test's diagnostic message (quiet run) |
| 2 | **Single worker plus blocking tests:** a controlled experiment (`MaxParallelThreads=1` plus three tests holding the worker for 30 s) passed 7/7 three times. xUnit ran them one after another. | temporary experiment, deleted afterwards |
| 3 | No undisposed in-process API hosts: all 23 `WebApplicationFactory<Program>` creations are disposed (`using`/`await using`, or a field disposed in `Dispose`) | code audit |
| 4 | The test host uses **workstation GC** (`ServerGarbageCollection=false`), so the memory growth is not Server GC heaps | `BirkNext.Api.Tests.csproj`, runtimeconfig |
| 5 | **Single-process run, 8 threads:** test host 743 → 2,930 → **4,428 MB** in the first minute (free memory 8.8 → 6.0 GB). The run was stopped by Claude Code for low memory about 2 minutes in, and **CI_DEF_3 failed again (39 s)** before that. | memory samples every 30 s |
| 6 | **Partitioned, 4 threads:** every partition passed. Peaks 0.6–3.9 GB, released between partitions. **CI_DEF_3 passed** in its partition (peak 0.9 GB); **ObservedTraffic… passed** in its partition. | table below |

Production code also has one synchronous `_gate.Wait()` in `LocalHttpsProxyService.StatusForProfile`. It is real blocking, but small; not changed (no evidence that it matters).

## Root-cause status
| | |
|---|---|
| **Confirmed** | A single process running the whole suite grows to several GB within minutes and exhausted this machine's memory twice. CI_DEF_3's 20 s timer fired 19–28 s late in those runs, and the test passes when memory is bounded. |
| **Suspected (not proven)** | Memory pressure (paging and GC stalls) delays timers and continuations, which causes the timeouts. It fits every observation; which tests allocate the most is NOT identified (that needs a heap/allocation profile). |
| **Ruled out by evidence** | Undisposed test hosts; Server GC. Thread-pool starvation from blocking a single xUnit worker was not reproduced. |
| **Not assumed** | No timeout was increased, no test was weakened or skipped, and parallelization was not disabled globally. |

## Implemented correction
`AIAssisted/backend/test-backend-partitioned.sh`:
- runs the complete suite in partitions built from the test project's folders (everything outside `Services`; groups of `Services` subfolders; the `Services` root namespace);
- one test host process at a time, `MaxParallelThreads=4` by default;
- reports each partition and the total, and exits non-zero on any failure.

No test or product code was changed for stability.

**The verified complete run** used scratchpad partitions with the same kind of filters (`scratchpad/be/part.sh`). The committed script builds its partitions from the folders (53 `Services` subfolders found). It is syntax-checked, and its folder discovery was verified, but **the committed script has not itself been run end to end** in this pass.

## Verified result — complete backend run, partitioned (2026-11-27, 4 threads)

| Partition | Passed | Failed | Skipped | Duration | Peak test host |
|---|---|---|---|---|---|
| outside `Services` | 1,601 | 0 | 8 | 2 m 32 s | 3,909 MB |
| `Services.TestPlanning` | 597 | 0 | 0 | 2 m 45 s | 2,125 MB |
| SourceAnalysis · Integrations · PerformanceTests · CriticalE2E | 1,279 | 0 | 2 | 1 m 19 s | 571 MB |
| TestData · TargetEnvironmentDetection · IntegrationJourneys · LocalHttpsProxy · ContractTesting | 991 | 0 | 5 | 34 s | 648 MB |
| ApiQuality · CanonicalInterpretation · AltinnTesting · AuthenticatedReview · QualityReview · ContractAnalysis | 1,197 | 0 | 0 | 1 m 17 s | 933 MB |
| other `Services` subfolders (37) | 1,330 | 0 | 5 | 3 m 28 s | 3,424 MB |
| `Services` root namespace | 631 | 0 | 0 | 30 s | 1,935 MB |
| **Total** | **7,626** | **0** | **20** | about 12½ min | max 3.9 GB |

- **Completeness:** 7,646 tests = the earlier full count of 7,643 plus the three tests added in this session (the window/search tests, the concurrent-write test, the work-queue currency test). No partition overlaps another and none is missing.
- **Frontend:** 9,876 / 9,876 (full suite, same day).

## Remaining risk
1. **A single-process full run is still unstable on this machine** (memory). Use the partitioned runner, or run with less parallelism on a machine with more memory.
2. **The memory-heavy tests are not identified.** Partitions «outside Services» (3.9 GB) and «other Services subfolders» (3.4 GB) are the places to profile first.
3. Different groupings than the verified one may peak differently; the committed script has not yet had its own full run.
4. One complete partitioned run, not repeated. The two flaky tests passed in it; stability over repeated runs is NOT yet shown.

## Also found during this work (not a test issue)
The repository's `backend/BirkNext.Api/App_Data/reset-backups/` holds a backup from a local data reset at 21:19 on 2026-11-26. The user confirmed the reset was intentional; the backup is left untouched. The isolated test script no longer copies the repository's `App_Data`.
