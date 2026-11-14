# BirkNext — release integrity and persistence hardening

**Date:** 2026-11-14
**Branch:** `008-traceability-first`
**Base:** HEAD `08eb0fea` (follows `birknext-persistent-evidence-delivery-scope-e2e-closure.md`)
**Commits:** local only, listed at the end

No push and no deployment. No remote system was contacted (TT02, m2lbdev, Altinn, MU, MM, Azure DevOps, production). No external runtime, security, browser or load test was executed.

The following were not touched:
- `appsettings.Local.json`
- `AIAssisted/testdata/`
- the preserved databases `birknext_scopes_acc_20261113`, `birknext_gap_acc_20261114` and `birknext_final_acc_20261114`. All three exist and contain neither new schema.

Acceptance ran against the fresh disposable `birknext_integrity_acc_20261114` on ports 5097 and 5197.

**Local-environment note:** during an early acceptance attempt, one browser CORS preflight (`OPTIONS`, no body) reached the user's local app on `localhost:5000`. My test hook had released a held auto-save request without the port rewrite. The browser blocked the POST itself, so nothing was sent or written there. The hook now always forwards to :5097.

## Gap closure

| # | Gap | Root cause | Fix | Automated | Browser | Status |
|---|---|---|---|---|---|---|
| 1 | Release Readiness ignored Critical E2E policy 2 | The dimension was hard-coded Not assessed. Release Readiness also had no build identity, so even a wired gate could only say "not evaluated" | `ICriticalE2EService.ReleaseGate`: the same private `Gate()` that the Critical E2E overview uses, so there is no second engine. Release Readiness takes `?build=`, maps the gate result and records policy, build, environment, required and blocking flows and evaluation time | `CriticalE2EReleaseReadinessTests`: every case asserts equality with `Overview().Release`. `ReleaseReadinessCriticalE2ETests` (bUnit) | Passed, blocked, incomplete and policy 1 shown; stale comparison; drill-down keeps environment and build | **Verified fixed** |
| 2 | Intermittent Activity Center test | **Product race:** when one `Refresh()` observed two finished operations, only the last in display order was announced, so "Completed" could be lost. Under load the "Cancelled" refresh ran late | Announce every newly finished operation, in `CompletedAt` order. New deterministic test that holds the renderer dispatcher | New test **fails on the old component** (checked by reverting in the scratch tree) and passes with the fix. 20 isolated runs: 160 executions, 0 failed. Full suite with it: see verification | — | **Verified fixed** |
| 3 | Scenario-query p95 failures | **Measurement:** p95 over 20 samples is the 2nd-largest sample, there was one warmup, and the class ran in parallel with container-heavy tests (median about 130 ms in the failing run). The query is one indexed SELECT with no N+1 | `AsNoTracking`. The performance test moved to a non-parallel collection: 10 warmups, 60 samples, nearest-rank p50/p95/p99 in output, exactly 60 SQL commands asserted. **Threshold unchanged (p95 < 2000 ms)** | 10 isolated runs: p50 2.4–3.7 ms, p95 2.8–6.3 ms, p99 8.8–12.2 ms, 60/60 SQL commands. Under full partition 6: p95 3.4 ms | — | **Verified fixed** |
| 4 | Auto-save console error on navigation | A page unload aborts the in-flight `fetch`, which .NET reports like a network failure and logged as an Error. Separately, a newer edit was marked **Saved** when an older save finished (it waited up to 30 s) | Change sequence: Saved only after the backend confirms the latest sequence. Serialized saves; a stale confirmation stays Pending. In-app navigation flushes without blocking. `beforeunload` warning only while unsaved. An unload abort is classified Interrupted (Warning); real failures stay Failed (Error) and retryable | `WorkspaceAutoSaveLifecycleTests` | Navigate-right-after-edit persisted; forced 500 → Failed, then retry → Saved; overlapping saves → 2 requests, newest wins; reload during save → Warning only, no error | **Verified fixed** |
| 5 | Declared journeys in a machine-local JSON file | Original design | Schema `integration_journeys` (`journey`, `journey_revision`). Revisions are append-only; edits use optimistic concurrency; retire is a soft delete with history kept | `DeclaredIntegrationJourneyPersistenceTests` (InMemory plus one Postgres test); Test Coverage and scope catalog consumers | Declare → reload → stable id and revision 1; edit → revision 2, revision 1 kept; offered as a scope member | **Verified fixed** |
| 5a | Legacy JSON migration | — | Idempotent startup import. The file is opened read-only; ids are preserved; outcomes are Imported / AlreadyPresent / Conflict / DuplicateInFile / Invalid; report at `GET …/declared/migration-report` | Run twice, file bytes and mtime unchanged, conflicts reported | Second start: read 3, imported 0, already present 3. File SHA-256 unchanged | **Verified fixed** |
| 6 | Declare page not discoverable | Reachable only from a Test Coverage link | `/integration-journeys` page (list, revisions, declare, edit), linked from Integration Quality Review → Test & Verify and from Test Coverage. No new sidebar entry | `IntegrationJourneysPageTests` | Reached from IQR without typing a URL; Back/Forward work | **Verified fixed** |
| 7 | Local Data Reset left Task Alignment rows | Separate-schema stores were hand-wired, and Task Alignment was not wired in | `ResettableSchemas` registry (Task Alignment and Integration Journeys), cleared after the local-guarded database reset. Migration history is never touched. The epoch drops held Task Alignment results in other tabs | `ResettableSchemaResetTests`, `TaskAlignmentResetEpochTests` | Plan lists both. Reset run: runs 2 → 0, journeys 5 → 0, revisions 6 → 0, migration histories intact; no stale Task Alignment result shown | **Verified fixed** |
| 8 | Delivery item vs feature counts | Feature-level figures carried item-like labels, and UI, export and comparison worded them separately | One table, `DeliveryMetricDefinitions` (unit and level per metric key), used by UI, export and comparer. Count text: "N in-scope items · F feature(s) containing in-scope items · E items excluded · U scope members unresolved". Stored metrics unchanged | `DeliveryScopeFilterTests`, `DeliveryScopeFilterPageTests` | Feature cards carry "features containing in-scope items", item cards their item unit; the Markdown export uses the same labels and the provenance | **Verified fixed** |
| 9 | Full regression on final code | — | Run on the final build (see below). Two more suite-load flakes were found and fixed at their root (below) | — | — | **Verified** (see verification) |

### Further reliability defects found in the final regression
- **`BrowserDeliveryTrustConfigurationTests.ChangingTrustPolicy…`:** the test read the Validation tab's rows on the same tick as the click. It now waits for the row; the assertions are unchanged.
- **Culture leak (several tests across classes):** `ContractTestingPageTests` and `ExecutionResultsPageTests` set the process-wide `CultureInfo.DefaultThreadCurrentCulture` to nb-NO while other classes ran in parallel. Unrelated tests then rendered `tabindex="−1"` (U+2212). Both classes now run in the non-parallel collection `ProcessCultureCollection`.

## Release Readiness and the Critical E2E gate

- **One evaluation:** Release Readiness calls `ReleaseGate(environment = canonical scope, build)` in process. It is the same code path as the Critical E2E page, and the tests assert equality.
- **States:**

  | Gate result | Readiness state |
  |---|---|
  | Ready | passed |
  | Blocked | blocked |
  | Incomplete / ReviewRequired / NotConfigured with required flows | incomplete |
  | No build, or no required flows | not assessed |
  | Another build or environment, including frozen-report comparisons | stale |

  Definitions without runs are never passed.
- **Mandatory UI smoke** (kept conservative and explicit): a failed required UI smoke flow shows "Business E2E contribution: No · Mandatory release blocker: Yes (failed required flow on this build)". A non-required UI smoke flow neither counts nor blocks.
- **Historical:** builds whose runs carry no `GatePolicyVersion` are evaluated with `critical-e2e-gate/1` exactly as before. Frozen readiness reports are never recomputed.
- **Behaviour change:** once an environment has required Critical E2E flows, the Critical E2E dimension is Required. Release Readiness is then *not assessed* until a build is entered.
- **Browser fixture** (synthetic, clearly marked):
  - Four flow definitions were saved through the API: required Business E2E, required UI smoke, optional UI smoke, required UI functional.
  - Run evidence cannot be recorded without executing, and nothing may be executed. 12 runs were therefore seeded into the disposable API's `App_Data/critical-e2e/history.json`, with the backend stopped. Each is labelled "seeded synthetic acceptance evidence; not an execution".
  - Builds:
    - B-PASS: all passed → passed.
    - B-BLOCK: smoke failed → blocked.
    - B-OLD: no policy version → policy 1.
    - B-NONE: no runs → incomplete.
  - No run endpoint was called.

## Reliability measurements

- **Scenario query** (60 samples after 10 warmups, nearest-rank):

  | Run | p50 (ms) | p95 (ms) | p99 (ms) |
  |---|---|---|---|
  | 1 | 2.4 | 3.3 | 9.1 |
  | 2 | 3.2 | 4.9 | 10.4 |
  | 3 | 3.1 | 4.4 | 9.7 |
  | 4 | 2.5 | 2.8 | 8.8 |
  | 5 | 2.4 | 3.8 | 10.0 |
  | 6 | 3.0 | 3.8 | 11.1 |
  | 7 | 3.7 | 6.3 | 12.2 |
  | 8 | 2.6 | 2.9 | 9.8 |
  | 9 | 2.8 | 3.6 | 10.6 |
  | 10 | 2.7 | 3.0 | 9.5 |
  | Full partition 6 | 2.5 | 3.4 | 9.4 |

  Each run issued 60 SQL commands on `scenarios`. The earlier multi-second samples came from process contention, not the query.
- **Activity Center:**
  - The new deterministic test fails on the old component and passes with the fix.
  - 20 isolated runs (160 executions) had 0 failures.
  - It passed in full-suite runs.

## Persistence

- **Integration Journeys:**
  - Migration `20261114180000_InitialDeclaredIntegrationJourneys` (additive, own schema and history).
  - Ids `project.{projectKey}:{slug}` are kept, and journeys are project-scoped.
  - Edits append revisions. Delete retires the journey; its history stays readable.
  - The migration report is held in memory and regenerated at each start.
- **Local Data Reset:**
  - The registry `ResettableSchemas.All` lists `task_alignment` and `integration_journeys`. Adding a schema is one line.
  - The Shared Scope Registry is **not** reset; this is an existing gap, documented.
  - After a reset, the legacy journey JSON is imported again at the next start, because the file is deliberately kept.

## Verification on the final code

- **Builds:** backend and frontend test projects, 0 errors. Release publish of the API and web app succeeded.
- **Backend focused:** 837 of 839 passed, 1 skipped. One failure was a word-list guard on a code comment ("person"); the comment was reworded.
- **Frontend focused:** 1096/1096.
- **Full frontend:**
  - Before the last two fixes: 8973/8973 twice. After the guide update: 8972/8973 (trust-configuration timing), then 8973/8973 and 8969/8973 (culture leak).
  - After both fixes: **8973/8973 in three consecutive full runs**.
- **Backend partitions** (sequential, foreground, final backend code):

  | Partition | Passed | Skipped | Failed |
  |---|---|---|---|
  | 1 | 1052 | 2 | 0 |
  | 2 | 1099 | 5 | 0 |
  | 3 | 889 | 0 | 0 |
  | 4 | 676 | 1 | 0 |
  | 5 | 536 | 3 | 0 |
  | 6 | 2507 | 8 | 0 |
  | **Total** | **6759** | **19** | **0** |

- **Browser acceptance (Edge, final Release build, disposable DB):**

  | Section | Result |
  |---|---|
  | Setup and earlier workflows (Task Alignment, identity, Delivery scope and reports) | 31/31 |
  | R Release Readiness | 19/19 |
  | J Integration Journeys (all J checks; run together with R, where the separate R freeze check was then fixed) | 26-check run |
  | S Autosave | 12/12 |
  | D Delivery units + V viewports (no overflow at 1440/1280/1024/768 on 7 pages, Back/Forward) | 39/39 |
  | T Local Data Reset | 9/10 |

  The one T miss was a script truncation of the plan text. A follow-up read of the full plan confirmed both lines ("Task Alignment run history (persisted Task Alignment results; task_alignment schema)" and "Declared Integration Journeys…"), and the reset itself verified 2 → 0 runs and 5 → 0 journeys in the database.

  No page errors and no 5xx responses. No external request, except the one :5000 preflight described at the top. No Critical E2E run. Screenshots: session scratchpad `acc/shots-integrity/`.

## Remaining limitations

- **Release Readiness:** stale is reachable only by comparing with a frozen report, because a live assessment always evaluates the build it shows.
- **Critical E2E fixture:** passed and blocked were shown with **seeded synthetic** run evidence. No real execution was performed, as required.
- **Autosave:** completion cannot be guaranteed on unload, and there is no local draft recovery. The Dashboard and Workflow save labels still show the backend's last persisted status.
- **Local Data Reset:** it does not clear the Scope Registry schema. A legacy journey file still present is re-imported after a reset.
- **Integration Journeys:** no draft/approval workflow (revisions are append-only), and the migration report is not persisted.
- **Task Alignment:** a save already in flight in another tab during a reset could still write one run.

## Commits

- `ff6b6cea` Release Readiness reads the Critical E2E gate (policy 2) for a build
- `d332139e` Integration Journeys: durable project-scoped declarations with revisions
- `9f67694a` Autosave: no false Saved, flush on navigation, interrupted vs failed
- `461ea614` Local Data Reset clears Task Alignment and journeys; Delivery metric units
- `2deb757c` Reliability: Activity Center race, scenario p95 method, two suite-load flakes
- this documentation commit (report and user guide)
