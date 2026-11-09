# BirkNext — backend partition-6 timeouts: root cause (2026-11-09)

Branch `008-traceability-first` (local only). Source under test: `1c279a0c` (the commit that recorded the failure as "investigated, not resolved")
plus the fix in this report. Previous record: `docs/audits/birknext-rest-graphql-execution-lifecycle-gap-register-2026-11-08.md`
("Partition-6 timeouts — investigation (not resolved)"); that record is kept unchanged.

**Result: root cause confirmed with stack evidence, fixed in the test infrastructure, regression test added. Not a product defect.**

## The failure

Two tests in backend partition 6 (the memory-guarded "rest" partition: `Unit.*`, `Controllers.*`, root `Services.*`, …) failed in 5 of 5 full
partition runs on 2026-11-08 and never in isolation:

- `ExecutionAuthenticationTests.Unattended_client_credentials_authenticate_the_run_and_never_reach_the_evidence` — the token request to the
  loopback test server was cancelled after its 15 s timeout;
- `ContractTestingTests.Negative_proposals_cover_many_variants…` — a GraphQL call to the loopback test server TimedOut.

Original evidence (kept, scratch, not committed): `be-lcK6.trx`, `be-lcR16.trx`, `be-lcR26.trx`, `be-lcP16.trx`, `be-lcM16.trx` and the
isolation/bisection runs (`lcT-*`, `lcB-*`).

## How it was investigated

Nothing in the product or the official tests was changed to observe it. Two scratch-only probes (never committed):

1. **Runtime probe** (`DOTNET_STARTUP_HOOKS`, dedicated non-pool thread, every 250 ms): thread-pool size, queue length and a pool heartbeat;
   thread-pool injections by reason (`Starvation`, `CooperativeBlocking` from runtime events); GC pause time and collections; heap, working set,
   private bytes; process and machine CPU; machine free memory; lock contention and long contentions; OS thread states; Npgsql pool metrics;
   gaps of the sampler itself (whole-process freezes).
2. **xUnit context probe**: a heartbeat posted to xUnit's `MaxConcurrencySyncContext` (the context async tests and their in-test servers continue
   on), the OS state and CPU of the xUnit worker threads, the moment each loopback-server request was accepted, and — when the context stalled
   ≥ 5 s — a 1.5 s EventPipe sample-profiler trace requested from the process's own diagnostics pipe, read offline with TraceEvent (shipped with
   Visual Studio; nothing downloaded).

Partition 6 was then run unchanged (same filter, `MaxCpuCount=1`, `MaxParallelThreads=2`, memory guard) on a clean extract of the commit.

## Evidence

| Measure (failing run `swd6`, 2 failures) | Value |
|---|---|
| xUnit context stall | **51.9 s** (07:12:50 → 07:13:41 UTC) |
| Thread-pool heartbeat latency | ≤ 1 ms the whole run |
| Starvation / blocking-wait injections | 0 / 0 |
| GC pauses ≥ 500 ms | none (longest 194 ms per 250 ms window) |
| Process CPU (24 cores) | 3.8 % average |
| Free memory | ≥ 3.9 GB |
| Long lock contention | 1 (331 ms) |
| Sampler freezes | none (max gap 325 ms) |
| Victims' requests accepted by the loopback server | **07:13:41.4** — the instant the stall ended, ~51 s after they were sent |

Passing runs showed the same stalls, only shorter (14.8 s, 11.3 s, 15.6 s, 15.5 s, 16.6 s): the failure is the stall crossing the clients'
15 s timeout. During the stalls both xUnit workers were in `Wait:UserRequest` with ~0 ms CPU — blocked, not busy.

Stacks (6 sample-profiler traces in two runs, every one showing the same): both xUnit workers were blocked synchronously in

```
Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.Start(IHost)        ← sync-over-async wait
Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory`1.CreateHost / EnsureServer / Dispose
  ← LocalSessionTestClient.CreateLocalSessionClient / factory.Services        (Controllers.*, RuntimeAuthentication*, …)
```

and, less often, in other synchronous test work (`CompilerCompatibilityTests` source scan, `ProjectImportStagingStore` file I/O). The hosts
being started were doing ordinary startup on their own entry threads (MVC application model, configuration watchers, Serilog file sink).

## Root cause

xUnit 2 runs async tests on a `MaxConcurrencySyncContext` with `MaxParallelThreads` workers (2 under the memory guard; the processor count by
default). `WebApplicationFactory` (.NET 8) has no asynchronous start: creating, starting and disposing an in-process host blocks the calling
xUnit worker for its whole startup. Partition 6 holds dozens of classes that start such hosts, so both workers are repeatedly inside host starts.

`LoopbackHttpTestServer` — the shared test server that **stands in for the remote target** — started its accept loop on the caller's context, so
every accept and every handler continuation was queued on those same two workers. While they were blocked, the simulated target could not accept
or answer; the product's real HTTP client (on the thread pool, correctly) timed out after 15 s against a server that never replied. More tests in
the same queue (`Unit.*`) made the stalls longer, which is why removing `Unit.*` hid the failure; lower xUnit parallelism made it worse and a
larger thread-pool minimum could not help, because the thread pool was never the bottleneck.

Ruled out with data: thread-pool starvation (heartbeat ≤ 1 ms, no starvation or blocking-wait injections), GC pauses, CPU saturation, memory
pressure, lock contention, database connection-pool pressure (Npgsql meters: at most 3 connections in use across all test data sources, no pending
connection request ever reported; 1 sample in 6 traces inside the pool), leaked hosts or background tasks (no sampler freezes), and the product's
own blocking (its synchronous waits are bounded or follow `WhenAll`; none appeared in the stacks).

## Fix (test infrastructure only)

| File | Change |
|---|---|
| `TestInfrastructure/LoopbackHttpTestServer.cs` | The accept loop (and so every handler) runs on the thread pool (`Task.Run`), never on the caller's synchronization context. |
| `Services/LocalHttpsProxy/LocalHttpsProxyServiceTests.cs` (`FakeUpstream`) | Same, for the fake TLS/plain origins. |
| `Unit/FrontendAccessibility/RealAxeIntegrationTests.cs`, `Unit/FrontendLighthouse/RealLighthouseIntegrationTests.cs` | Same, for their local page servers. |

The other in-test servers already accepted on the thread pool. No timeout was raised, no test disabled or excluded, no parallelism or partition
changed; the synchronous `WebApplicationFactory` starts remain (they are how .NET 8 hosts start) and still stall the xUnit context — they no longer
stall a simulated remote.

**Regression test:** `LoopbackHttpTestServerTests.KeepsServing_WhileEveryThreadOfTheContextThatStartedItIsBlocked` starts the server on a
one-thread synchronization context and blocks that thread (as a host start blocks an xUnit worker). Before the fix it failed exactly like
partition 6 (`TaskCanceledException … HttpClient.Timeout of 10 seconds elapsing`); after the fix it passes in milliseconds.

## Verification

| Run (partition 6 unless stated) | Source | Result |
|---|---|---|
| 2026-11-08 regression + 4 reruns (kept) | `1c279a0c`, old scratch tree | **5 × failed** (one or both victims) |
| 2026-11-09 probed, before the fix (`swa`–`swf`) | `1c279a0c`, clean extract | 1 failed (`swd`: both victims, 51.9 s stall), 5 passed with stalls of 11–16 s |
| Regression test before the fix | — | **failed** (`TaskCanceledException`, 10 s client timeout) |
| Regression test + affected classes after the fix | fix | 139/139 passed |
| Probed, after the fix (`fix1`, `fix3`, `fix4`) | fix | 3 × passed (2108/0); xUnit context still stalled 10–16 s, the victims took 6–19 s and passed |
| Probed, after the fix (`fix2`, `fix5`) | fix | **invalid**: the scratch probe attached to the regression test's private context and crashed the test host (probe defect; the probe now attaches only to xUnit's context, and the test's context ignores posts after disposal) |
| **Full backend suite, no probes** (`rc`) | fix | **P1 984/0/2 · P2 1098/0/5 · P3 830/0/0 · P4 652/0/1 · P5 520/0/3 · P6 2108/0/8 = 6192 passed, 0 failed, 19 skipped** |
| Partition 6 repeated ×4, no probes (`rr1`–`rr4`) | fix | 3 × passed; 1 × **1 failure unrelated to this cause**: `ScenariosDeletionTests` could not start its Testcontainers PostgreSQL container (`rootlessport listen tcp4 0.0.0.0:37783: bind: address already in use`, Podman). Both former victims passed in every run. |

The former victims passed in every valid run after the fix (8 of 8). What is **not** claimed: that partition 6 can never fail again — the
synchronous host starts still stall the xUnit context (expected, by design of `WebApplicationFactory`), so any *other* test that serves a
simulated remote from that context would be exposed the same way (none remain in the suite: every in-test server now accepts on the thread
pool), and the Podman port-allocation failure above is a separate, environmental flake recorded here and in the gap register.

Evidence kept (scratch, not committed): `kept-partition6-evidence/original-2026-11-08/` (the failing TRX files and logs) and
`kept-partition6-evidence/investigation-2026-11-09/` (probe CSVs, stall stack summaries, TRX of every probed run, probe and reader sources).
