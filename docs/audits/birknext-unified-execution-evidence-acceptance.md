# BirkNext — Unified Test Execution & Evidence Explorer: acceptance (2026-11-10)

Branch `008-traceability-first`. Baseline `35633daf`. No push, no deployment, no external system.

## 1. Environment

| Item | Value |
|---|---|
| Stack | Real BirkNext API (scratch Debug build of the working tree) on `:5098`; Release-published frontend on `:5198`; Edge headless (Playwright). Every non-localhost request is aborted; outbound proxies point at a dead port. |
| Databases | `birknext_uee_20261110`: disposable, recreated per run. `birknext_mse_20261110`: the multi-source acceptance database, reused read-only for the composite phase. |
| Synthetic target | `target.py` on `127.0.0.1:5099` with an independent ledger (`out/ledger.jsonl`: method, path, body, `X-BirkNext-Execution-Id`). Modes: normal / fail (GET /pets → 500). |
| Synthetic projects | `proj-a-uee.zip` (Pets, Orders, Shop, Person, GraphQL `child(id)` and an argument-free GraphQL `status`), environment `uee-a`. `proj-c.zip` (Health), environment `uee-b`: the other-project, other-environment run. Both imported through the real Project Import API; contracts are imported with generated tests and data and bound per environment. |
| Oracles | The ledger (what was sent and received) and PostgreSQL rows queried with `psql`. Never the explorer's own summaries. |

Two Person-like tests needed an identifier ("Select a dataset that provides id"). That is correct product behaviour: generated identifiers need human confirmation. The acceptance used argument-free operations instead and did not bypass the confirmation.

## 2. Results — final run

**Main: 41/41 PASS, 1 NOT RUN. After API restart: 5/5 PASS. Composite (multi-source DB): 10/10 PASS.** There was no external request, page error or unexpected console error.

### Main (fresh synthetic stack)

| ID | Check | Result |
|---|---|---|
| UE-01 | "Test Execution & Evidence" with four areas, Overview first | PASS |
| UE-02 | Empty scope: "No execution results yet." and "Actual test results appear here after an authorized execution.", four guidance links that exist, no Run action, no retention controls | PASS |
| UE-L1 | Empty overview: no page-level horizontal scroll at 1440 / 1280 / 1024 / 768 / 390 | PASS |
| UE-03 | The empty state leads to Contract Testing (execution stays in the owning module) | PASS |
| UE-04 | Contract Testing runs the Pets test: exactly one `GET /pets` on the ledger, carrying this run's correlation header | PASS |
| UE-05 | The run is listed with the outcome PostgreSQL holds (`Passed\|1\|1\|0`) | PASS |
| UE-06 | Run details: outcome, owner status, environment | PASS |
| UE-07 | Timeline: Submitted GET → Responded 200, each with this run's correlation id | PASS |
| UE-08 | Request/response explorer: GET /pets → HTTP 200 `["rex","fido"]`, as the ledger shows | PASS |
| UE-09 | Assertions assessed and passed | PASS |
| UE-10 | Opening, details, timeline and the call explorer sent nothing to the target | PASS |
| UE-11 | The target answers 500: the owner records the run as Failed | PASS |
| UE-12 | FAILED with a colour-independent glyph (✕) | PASS |
| UE-13 | Comparison offers only the earlier run of the same test in the same environment | PASS |
| UE-14 | Comparison: NEW FAILURE against the earlier passing run | PASS |
| UE-15 | GraphQL test from Contract Testing: one `POST /graphql` with the `status` query on the ledger | PASS |
| UE-16 | GraphQL evidence: query operation, data present, 0 errors | PASS |
| UE-17 | Runs & Results total for uee-a equals the PostgreSQL count | PASS |
| UE-18 | Outcome filter is server-side and kept in the URL: only the failed run | PASS |
| UE-19 | Type filter | PASS |
| UE-20 | Project filter proj-a never shows project C's run (same target, other project and environment) | PASS |
| UE-21 | Project filter proj-c shows only its own run | PASS |
| UE-22 | Overview counts equal PostgreSQL: runs 4, passed 3, failed 1 | PASS |
| UE-23 | Stored evidence is the measured JSON; database allocation is not on the overview | PASS |
| UE-24 | Needs attention lists the failed run | PASS |
| UE-L2–L6 | Overview, runs, detail, evidence and storage: no page-level horizontal scroll at the five widths | PASS |
| UE-25 | Traceability: contract version, contract operation and test definition revision recorded by the run | PASS |
| UE-26 | Evidence & Traceability lists results with Trace and Timeline links | PASS |
| UE-27 | Sanitized report: safe file name, FAILED, "Nothing was re-executed", no body values ("synthetic failure", "fido" absent) | PASS |
| UE-28 | Storage & Retention explains stored JSON vs database allocation and lists every stored run | PASS |
| UE-29 | Protecting a run sets the PostgreSQL row's Protected flag | PASS |
| UE-30 | Retention dry run: the eligible count equals PostgreSQL, the protected run is kept, previewing deletes nothing | PASS |
| UE-31 | Deleting one disposable run removes exactly that run and its calls; every other run and call remains; the audit trail records the deletion | PASS |
| UE-32 | A protected run cannot be deleted (Delete is disabled with the reason) | PASS |
| UE-K1 | Areas reachable with Tab and opened with Enter | PASS |
| UE-33 | Nothing in the explorer sent anything to the target: ledger = three Contract Testing runs + project C's setup run | PASS |
| UE-N1 | Integration Journey, Browser E2E (Edge Companion), Performance (k6) and Parquet results in the browser | **NOT RUN**: no such real run exists in this isolated stack and none was fabricated. Their projections are covered by `ExecutionExplorerTests` with owner-shaped records. |
| UE-Z | No external request, page error or console error | PASS |

### After API restart

| ID | Check | Result |
|---|---|---|
| UE-R1 | The history reopens with every stored run | PASS |
| UE-R2 | The failed run and its comparison reopen unchanged | PASS |
| UE-R3 | The protected run is still protected | PASS |
| UE-R4 | Reopening sent nothing to the target | PASS |
| UE-Z | Clean | PASS |

### Composite E2E and real M2LB (multi-source acceptance database)

| ID | Check | Result |
|---|---|---|
| UE-C1 | Every composite run is listed as Composite E2E (5) | PASS |
| UE-C2 | Each outcome follows the owner's verdict: full → Passed; subflow / incomplete → Partial, never a full pass; wrong correlation → Failed; unknown → Unknown outcome | PASS |
| UE-C3 | Failed-handoff timeline: recorded events with times; stages whose calls were never transmitted are listed as "not observed" with no time | PASS (after defect UE-D1) |
| UE-C4 | The stage without an observation is not verified (TimedOut / Blocked) | PASS |
| UE-L7 | Composite detail layout at five widths | PASS |
| UE-C5 | The full E2E run traces its six correlated boundaries | PASS |
| UE-C6 | Real M2LB project: no execution exists and the explorer says so; nothing fabricated, no false E2E | PASS |
| UE-C7 | Real M2LB overview: empty state with the available test sources | PASS |
| UE-C8 | Synthetic composite runs are attributed to their owning project only (`synth.skole`) | PASS |
| UE-Z | Clean | PASS |

## 3. Run history

| Run | Main | Restart | Composite | What failed and why |
|---|---|---|---|---|
| 1 | aborted | 0/2 | 9/10 | The setup's other-project test needed an identifier from Test Data (correct product behaviour), so there was no run and the main phase aborted. UE-C3 was a product defect (UE-D1). |
| 2 | 39/41 | 4/5 | 10/10 | Harness bugs: psql renders the concatenated boolean as `true`, not `t`; and the harness read the GraphQL request label after switching to the Response tab. |
| 3 | **41/41** (+1 NOT RUN) | **5/5** | **10/10** | none |

## 4. Automated tests

| Suite | Result |
|---|---|
| `ExecutionExplorerTests` (backend) | 7/7 PASS |
| Focused backend (ExecutionExplorer, ExecutionEvidence, IntegrationSuites) | 70/70 PASS |
| Focused frontend (ExecutionResults, ExecutionExplorer, Navigation, Documentation, Guide, ContractTesting, BlobStorage, MessageObservations) | 403/403 PASS |

## 5. Full regression (tasks 4 + 5 tree)

Run sequentially on the committed tree (multi-source E2E `35633daf` + this task): six memory-guarded backend partitions, then the full
frontend suite, then the Release build. No timeout was changed and no test was disabled.

| Step | Result |
|---|---|
| Backend partition 1 (SourceAnalysis, Integrations, PerformanceTests) | 1031 passed, 2 skipped, 0 failed |
| Backend partition 2 (TargetEnvironmentDetection, TestData, LocalHttpsProxy, AltinnTesting) | 1098 passed, 5 skipped, 0 failed |
| Backend partition 3 (IntegrationJourneys, CriticalE2E, ApiQuality, AuthenticatedReview) | 830 passed, 0 failed |
| Backend partition 4 (CanonicalInterpretation … AzureEnvironment) | 652 passed, 1 skipped, 0 failed |
| Backend partition 5 (IntegrationTopology … IntegrationQuality) | 520 passed, 3 skipped, 0 failed |
| Backend partition 6 (everything else, including ExecutionExplorer and IntegrationSuites) | 2261 passed, 8 skipped, 0 failed |
| **Backend total** | **6392 passed, 19 skipped, 0 failed** (peak memory 4.9 GB in partition 6, floor never reached) |
| Full frontend suite | **8571 / 8571 passed** |
| Release frontend build (`dotnet publish -c Release`) | PASS |
