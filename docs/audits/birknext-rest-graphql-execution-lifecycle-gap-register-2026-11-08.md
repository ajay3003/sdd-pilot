# BirkNext — REST & GraphQL complete execution lifecycle: gap register (2026-11-08)

Branch `008-traceability-first` (local only). Baseline `d0a57a2e`; final source `77bdcd8a` + the allow-list test update committed with these
reports. Statuses: PASS / FAIL / PARTIAL / BLOCKED / NOT RUN / NOT SUPPORTED. Acceptance IDs (`LC-…`) refer to
`docs/audits/birknext-rest-graphql-execution-lifecycle-acceptance-2026-11-08.md`.

**Overall: PARTIAL.** The lifecycle is implemented and verified end to end in the browser (44/44 acceptance checks, 172 calls reconciled with an
independent ledger), but the full backend regression is not green: partition 6 has two timeout failures that are load-dependent (investigated
below, not resolved), and several items are deliberately not supported.

## Brief sections

| § | Capability | Status | Commit(s) | Verified by |
|---|---|---|---|---|
| 1–2 | Baseline + architecture trace | PASS | `1806f9d0` | audit report |
| 3 | Attended authentication lifecycle (start, success, cancel, expiry mid-run, renewal, identity change, logout, restart) | **PASS** | `110afb72`, `77bdcd8a` | `ExecutionAuthenticationLifecycleTests` (8); LC-10…LC-14, LC-27…LC-29, LC-41 |
| 3 | Redirect-URI validation, state, nonce, PKCE | PASS (target side, observed) | — | LC-13, LC-14. These belong to the target application and its IdP; BirkNext observes the result and never runs a client of its own |
| 3 | Wrong audience | **NOT SUPPORTED** | — | BirkNext checks tenant and host scope; an expected-audience setting would need new Target Environment configuration. The target API enforces its audience (a rejected call shows as Failed authentication) |
| 3 | Missing scopes | PASS | `110afb72` | LC-31 / LC-32 (403 → Authorization denied) |
| 3 | Concurrent sessions / multiple simultaneous tests | PARTIAL | `110afb72` | One credential per Target Environment profile shared by concurrent runs (backend tests); not exercised concurrently in the browser |
| 4 | Unattended client credentials (reuse, renewal, single-flight, no silent attended↔unattended switch) | PASS | `110afb72` | `Concurrent_calls_share_one_token…`; LC-32 (15 calls, 1 token request) |
| 5 | Multi-call sessions, per-call authentication outcome, no downgrade to anonymous, no credential to other hosts | PASS | `110afb72` | LC-21, LC-27, LC-28; redirects never followed (`A_redirect_is_recorded_and_never_followed`) |
| 6–8 | Test Data rows, typed resolution, dataset-driven execution, row/version identity, policies | PASS | `d27cbbd9` | `DatasetRowResolutionTests`, data-driven tests; LC-04, LC-20, LC-22 |
| 7 | Safe concurrency of independent calls | **NOT SUPPORTED** | — | Runs are sequential by design; no parallel sending was added (bounded, predictable load on the target) |
| 9 | Chained calls (extraction, dependencies, upstream failure, cycles, credential-like outputs) | PASS | `a918cd70` | chain tests, `ExecutionOutputsTests`; LC-26 |
| 9 | Chain × dataset rows (iterate a whole chain per row) | NOT SUPPORTED | — | A step can name one row; a chain runs once |
| 9 | XML extraction | NOT SUPPORTED | — | JSON body, GraphQL data and safe headers only |
| 10 | 100+ execution control (limit, stop on first failure, cancel, partial completion, wire counting, no auto-retry of data-changing calls, unknown outcome) | PASS | `d27cbbd9`, `e79cecd6` | LC-23, LC-30, LC-33; idempotency tests |
| 11 | Request/response capture at the transport boundary | PASS | (existing) + fields | LC-20, LC-24, LC-44 |
| 12 | Local storage audited and documented | PASS | `5f6fa56e` | PostgreSQL only; LC-42, LC-43 |
| 13 | Deletion (one, selected, all eligible, bodies), protection, confirmation, audit, one transaction | PASS | `5f6fa56e`, `cae1e21d` | PostgreSQL + in-memory scenario tests; LC-47, LC-48, LC-53 |
| 13 | Interrupted deletion → "deletion pending" + reconcile | NOT NEEDED | — | No evidence files exist; deletion is one database transaction (all or nothing) |
| 14 | Retention (default Disabled, presets/custom, dry run, protected kept, manual apply, audit) | PASS | `5f6fa56e` | LC-49, LC-50 |
| 14 | Scheduled retention cleanup | NOT SUPPORTED (by design) | — | No uncontrolled background deletion; a person applies retention |
| 14 | Per-project retention and quotas | NOT SUPPORTED | — | Retention is global; scope filters are per environment |
| 15 | Storage usage (measured bytes, table size, protected, oldest/newest, candidates) | PASS | `5f6fa56e` | LC-43, LC-51 |
| 16 | Export (selected runs, manifest, references, hashes, limits, never executes) | PASS | `5f6fa56e` | LC-46 (independent Python hash check) |
| 17 | Results Explorer (filters, multi-select, delete, export, retention, storage, auth outcome, rows, chain steps, evidence) | PASS | `cae1e21d` | bUnit + LC-42…LC-55 |
| 18 | Authorization: execute permission on run start/cancel; write permission on destructive evidence actions | PASS | `110afb72`, `5f6fa56e` | endpoint security tests |
| 18 | Cross-project isolation | PARTIAL | — | Evidence is scoped by Target Environment; there is no project column on runs |
| 19 | Revision drift (contract, definition, dataset row, chain revision) | PASS | all | evidence references; LC-26 chain revision, LC-20 rows |
| 20 | Failure/recovery: API killed mid-run, cancel, timeout, drop, expiry, upstream chain failure | PASS | existing + `a918cd70` | LC-30, LC-31, LC-40 |
| 20 | Disk full, database unavailable, quota exceeded | NOT RUN | — | Covered only by the generic run-recovery path (Interrupted); not provoked |
| 21–22 | Local acceptance ≥ 100 calls + browser acceptance at five widths | PASS | — | 44/44 |

## Carried forward from Contract-Driven Testing phase 2

| Item | Status now | Notes |
|---|---|---|
| Attended proxy sign-in not browser-verified | **PASS** | Verified with the real proxy and a real authorization-code + PKCE sign-in against the synthetic IdP (LC-11…LC-14, LC-27…LC-29). Limits: the proxy CA was trusted in BirkNext's in-memory store and the browser ignored certificate errors (no Windows trust store change); a real Edge launched by BirkNext with a Windows-trusted CA was not run. |
| AQR stored-contract selection not browser-verified | NOT RUN | Not part of this task's flows; stays open. |
| YAML / Swagger 2 structural parsing partial | **OPEN (PARTIAL)** | Unchanged; not implemented here. Must not be marked resolved until the structural parsing is implemented and tested. |
| Basic authentication | NOT SUPPORTED | Unchanged. |

## Defects

| ID | Found by | Defect | Fix | Regression |
|---|---|---|---|---|
| LC-DEF-1 | Browser acceptance (LC-27) | Local HTTPS proxy kept the browser's side of an intercepted connection open after the target closed an idle keep-alive connection; the browser's next request hung forever (attended renewals were never observed). | `77bdcd8a`: close the client side when the upstream side ends | `WhenTheUpstreamClosesAnIdleKeepAliveConnection…` (hung before the fix); LC-27 PASS after |
| LC-DEF-2 | Audit (stage I) | POST/PATCH/GraphQL mutations could be resent automatically by a retry policy; an unanswered data-changing request was not marked as having an unknown outcome. | `e79cecd6` | `Data_changing_calls_are_never_retried_automatically…` |
| LC-DEF-3 | Audit (stage A) | Run start/cancel had no authorization policy; evidence deletion had no permission, confirmation or protection. | `110afb72`, `5f6fa56e` | endpoint security tests, management scenarios |

## Regression (scratch worktree, memory-guarded, sequential partitions; source `77bdcd8a` + allow-list update)

| Suite | Result |
|---|---|
| Backend partition 1 | 984 passed, 0 failed, 2 skipped (first run: 1 failure — the source-ownership allow-list still named the old export location; updated, rerun green) |
| Backend partition 2 | 1097 passed, 0 failed, 5 skipped |
| Backend partition 3 | 830 passed, 0 failed |
| Backend partition 4 | 652 passed, 0 failed, 1 skipped |
| Backend partition 5 | 520 passed, 0 failed, 3 skipped |
| Backend partition 6 | **2105 passed, 2 failed**, 8 skipped (see below) |
| **Backend total** | **6188 passed, 2 failed, 19 skipped** |
| **Frontend (full)** | **8494 passed, 0 failed** |

### Partition-6 timeouts — investigation (not resolved)

Failing tests: `ExecutionAuthenticationTests.Unattended_client_credentials_authenticate_the_run_and_never_reach_the_evidence` (token request to the
loopback test server cancelled after 15 s, twice) and `ContractTestingTests.Negative_proposals_cover_many_variants…` (GraphQL call TimedOut) — the
same test as the one-off failure recorded in phase 2 (`g-be-full66.log`; it passed on that rerun).

| Experiment | Runs | Result |
|---|---|---|
| The two classes alone | 5 | 5/5 pass (42/42, 26 s each) |
| Same, thread pool capped at 3 workers (`DOTNET_ThreadPool_ForceMaxWorkerThreads=3`) | 3 | 3/3 pass |
| ContractTesting + ExecutionEvidence namespaces; ContractTesting alone | 1 + 1 | pass |
| Victims + all `Controllers.*`; victims + all `Unit.*` | 1 + 1 | pass |
| Victims + `Unit.BrowserAutomationDiagnostic` | 2 | pass |
| Full partition 6 (regression + 2 reruns) | 3 | **3/3 fail** (the same two tests, or one of them) |
| Full partition 6 with `xUnit.MaxParallelThreads=1` | 1 | fails (plus a 2 s p95 performance threshold test) |
| Full partition 6 with `DOTNET_ThreadPool_ForceMinWorkerThreads=64` | 1 | fails (one test) |
| Partition 6 without `Controllers.*`, `Data.*`, `DeliveryReporting.*` | 1 | fails |
| …and also without `Unit.*` | 1 | **passes** (1022/0) |

Findings: the failure is **load- and composition-dependent, not random**: never in isolation (14 focused runs), consistently in the full partition
(5/5 since the partition grew). The TRX timing shows a **process-wide stall** — 25+ unrelated tests (controller hosts, migrations, unit suites)
overlap the failing test's entire 45 s window and end with it — and the two failing tests are the ones with real 15 s HTTP timers during that
stall. Removing `Unit.*` from the partition removes the failure, but no single suite reproduces it with the victims, and neither lower xUnit
parallelism nor a raised thread-pool minimum prevents it. Classification: **resource contention inside the test host (stall), not a product
defect** — under the stall the product behaves correctly (calls Blocked/TimedOut, nothing invented or resent). **Not resolved.** Options for a
separate task: run timer-sensitive loopback tests in their own non-parallel collection or partition, or find the stalling unit test with a
diagnostic dump during the window.

## Open items

1. Partition-6 timeouts (above) — investigation recorded, not fixed.
2. YAML / Swagger 2 structural parsing (from phase 2) — open.
3. AQR stored-contract selection in the browser (from phase 2) — not run.
4. Expected-audience check, Basic authentication, safe concurrency, chain × rows, XML extraction, scheduled retention, per-project retention/quotas
   — not supported (reasons above).
5. Disk-full / database-unavailable failure modes — not run.
6. A real Edge launched by BirkNext with a Windows-trusted proxy CA — not run (would change the Windows trust store).
