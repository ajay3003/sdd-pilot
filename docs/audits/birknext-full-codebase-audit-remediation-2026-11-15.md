# BirkNext — Full codebase audit remediation and independent verification (2026-11-15)

Branch `008-traceability-first`. Original audit: `docs/audits/full-codebase-2026-11-15/` (commit `4be5bd6f`, baseline `0f3ca7dc`) — not modified here. This report reconciles every finding of that audit with the current code, records what was changed and how it was verified, and reassesses readiness.

## 1. Starting point

- Starting HEAD for this work: `a909061c` (Technical Testing workspace), on top of `94d795d0` (workspace navigation), `1f4dc6a0` (Azure DevOps export) and `4be5bd6f` (the audit). Worktree inspected; unrelated changes (`appsettings.Local.json`, the Kode67 gap register, older audit drafts, `AIAssisted/testdata/`) left untouched.
- Every finding was re-read against current source, not the audit text alone. Code references below are to the current tree.

## 2. Remediation status (links to the original findings register)

| ID (original) | Original severity | Verification | Status now | Root cause | What changed | Remaining risk |
|---|---|---|---|---|---|---|
| [SEC-01](full-codebase-2026-11-15/findings-register.md) | P1 conditional | **Confirmed**, and wider than reported: the Host-header loopback check is caller-controlled, and `Hosting:AllowNonLoopbackBinding=true` started a non-local listener on which any valid Entra token for the API audience could read and change every project (the integration test `TheAuthenticatedPath_IsUnchanged…` expected a LAN bearer request to succeed). A same-host reverse proxy would have the same effect. | **Mitigated — gated by deployment mode** (shared mode cannot start or be reached) | Single-user assumptions not separated from shared hosting; no project-membership model exists | Startup refuses every non-loopback listener, the former opt-out included, with a message naming the missing project authorization (`Program.cs`); `LocalRequestGuardMiddleware` refuses any request whose socket peer is not loopback and any request carrying proxy headers (`Forwarded`, `X-Forwarded-*`, `X-Real-IP`) before authentication (`Services/LocalSecurity/LocalRequestGuard.cs`) | Shared multi-user deployment remains unsupported until project-level authorization exists (§7). Two OS users of one computer are not separated (same as before). |
| [ARCH-01](full-codebase-2026-11-15/findings-register.md) | P2 | **Confirmed**, with concrete defects found while mapping ownership (§8) | **Mitigated** (defects fixed, invariants enforced by tests; package importer writes deferred) | Seven contexts listed by hand in four places with no cross-check | Backup migration inventory completed (3 contexts were missing); `flow-versions.json` added to the backup; suggestion reviews cleared by Local Data Reset; Snapshot Registry reset uses the schema-guarded generic delete; `PersistenceOwnershipTests` checks every registered context has a migration-inventory entry and exactly one reset owner | `WorkspacePackageService` (integration package import) still writes ~15 tables of other services directly (deferred, §8) |
| [PERF-01](full-codebase-2026-11-15/findings-register.md) | P2 | **Confirmed**: `ReadProjectRunsAsync` loaded and deserialized every run of a project per Test Coverage / traceability request; the run list silently capped at 500 | **Resolved** | Whole-history read where only the latest completed run per case is needed | `ReadTraceabilityRunsAsync` selects on narrow columns and deserializes at most two runs per case; `ListPageAsync` + `GET api/manual-test-runs/page` page with a stable order and exact total; Execute & document shows "Showing N of total" with *Show older runs*; index `ix_manual_test_runs_project_started` | The narrow selection still scans the project's run rows (scalar columns only); measured below |
| [TEST-01](full-codebase-2026-11-15/findings-register.md) | P2 | **Confirmed** as a limitation of the audit (no suite ran); suites now run on the final code | **Mitigated** | Persistence invariants were mostly tested on EF InMemory | New PostgreSQL tests (temporary databases) for concurrency, finality, isolation, paging, the bounded read; boundary tests for SEC-01; architecture test for ARCH-01; full regression on final code (§13) | Coverage, mutation and flake analysis still not performed |

Other items the audit listed as suspected/not promoted (Markdown XSS sanitizer fixtures, dependency advisories, cross-module staleness, accessibility, scalability) were not re-audited here and keep their original status.

## 3. Confirmed problems (beyond the register, found while verifying)

1. A case could get two runs in progress: `StartAsync` checked and then inserted with no constraint (concurrent starts both insert). Fixed with the partial unique index `ux_manual_test_runs_one_in_progress`; a losing concurrent start resumes the winner's run.
2. A completed or stopped run was final only in the service. The database now refuses an `UPDATE` of it (trigger `manual_test_runs_no_rewrite`, same pattern as test plan snapshots and execution manifests); `DELETE` stays possible for Local Data Reset.
3. Test Plan Suggestions: an edit made while a save was in flight was overwritten by the response and marked saved; leaving the page with unsaved edits discarded them without asking. Both fixed (§10).
4. Backup/restore and reset gaps listed under ARCH-01 (§8).

## 4. Conditional risks

- SEC-01 in a shared deployment (now refused, §7).
- Requirement links are checked against the browser's reading of the specification, not server-owned evidence (§10).

## 5. False positives / not reproduced

None of the four findings was a false positive. The audit's "Data integrity 4/5" observations (pinned runs, concurrency token) were confirmed on PostgreSQL.

## 6. Code changes (summary)

Backend: `Program.cs` (listener refusal), `LocalRequestGuard.cs` (peer and proxy refusal), `ManualTestRunService.cs` (bounded traceability read, paging, start race), `ManualTestRunsController.cs` (`/page`), `AppDbContext.cs` + migration `20261115201037_ManualRunPagingAndSingleInProgress` (two indexes, finality trigger), `TestCoverageReviewService.cs` / `TestPlanSuggestionService.cs` (bounded read; requirement-link provenance), `PreparedCaseTraceability.cs` (limitation text), `CodeMigrations.cs`, `ManagedFileStores.cs`, `CriticalE2ESuggestionStore.cs`, `LocalDataReset.cs`, `DatabaseMigrationLock.cs` (comment). Shared: `ManualRunPage`, `ManualRequirementLink.CheckedAgainst`/`CatalogFingerprint`. Frontend: `ManualTestingExecute.razor` (paged history), `ManualTestRunsApiService.cs` (`PageAsync`), `TestPlanSuggestions.razor` (leave prompt, save race).

## 7. Deployment modes and authorization decisions

| Mode | Implemented? | Authentication | Authorization | Decision |
|---|---|---|---|---|
| Local single user, loopback | **Yes — the only supported mode** | Launcher-paired local-session cookie, or an Entra bearer token | Named capability policies (execution, configuration writes, workspace transfer); every authenticated caller sees every project | Kept unchanged for loopback use |
| Local network / non-loopback listener | Previously possible via `Hosting:AllowNonLoopbackBinding=true` | as above | **None per project** | **Refused at startup** (no opt-out) and at request time (non-loopback socket peer → 403) |
| Shared multi-user Entra (incl. behind a reverse proxy) | Not implemented (no project-membership model, no per-project policy) | Entra JWT (validated by authority/audience) | None per project | **Refused**: proxied requests (forwarding headers) → 403; non-loopback peers → 403; listener cannot start |

Guarantees: on a running instance, every `/api` and `/graphql` request comes from this computer, addressed to a loopback name, not through a proxy; cookie-authenticated state changes still require the frontend Origin and client header (CSRF). Limitations: BirkNext does not separate projects between people; anyone who can authenticate on this computer is the operator. Prerequisites for a shared deployment (not built, deliberately — no speculative RBAC): an authoritative principal-to-project membership store; a single authorization handler applied to every project-scoped controller (project key from route/query resolved server-side, nested ids checked against the authorized project, unknown/foreign → 404); deny-by-default for unknown projects; tests with two principals across every API family (the audit's regression plan); and only then a deliberate opt-in that also re-enables a non-loopback listener.

Cross-project isolation that does exist (data scoping, not authorization) is now tested on PostgreSQL: another project's run is never read, listed, changed or stopped (404) and never enters this project's traceability.

## 8. Persistence ownership matrix

Seven EF Core contexts, all registered in `Program.cs` (six at 174–204, `IntegrationJourneysDbContext` at ~831), all migrated at startup under one PostgreSQL advisory lock (`DatabaseMigrationLock`).

| Context | Schema | Migrations | Reset owner | In backup migration inventory |
|---|---|---|---|---|
| `AppDbContext` | `public` | `Data/Migrations` (76 incl. this one) | `AdminService.ResetLocalDatabaseAsync` (one transaction, every table) | yes |
| `DeliveryReportingDbContext` | `delivery_reporting` | `Data/DeliveryReporting/Migrations` | `DeliveryReportingDataReset` | yes |
| `CanonicalInterpretationDbContext` | `canonical_interpretation` | `Data/CanonicalInterpretation/Migrations` | `CanonicalInterpretationDataReset` | yes |
| `SnapshotRegistryDbContext` | `snapshot_registry` | `Data/SnapshotRegistry/Migrations` | coordinator step → `ResettableSchemas.DeleteAllRowsAsync` (schema-guarded; was three hard-coded DELETEs) | yes |
| `ScopeRegistryDbContext` | `scope_registry` | `Data/ScopeRegistry/Migrations` | `ResettableSchemas` (only "Reset everything") | **added** |
| `TaskAlignmentDbContext` | `task_alignment` | `Data/TaskAlignment/Migrations` | `ResettableSchemas` | **added** |
| `IntegrationJourneysDbContext` | `integration_journeys` | `Data/IntegrationJourneys/Migrations` | `ResettableSchemas` | **added** |

Entity ownership (write owner → table; consumers read only):

| Entity | Store | Write owner | Revisions / immutability |
|---|---|---|---|
| Project import staging | memory + temp dir | `ProjectImportStagingStore` | 30-minute staging, never retained |
| Workspaces / imported projects | `SavedWorkspaces`, `SavedWorkspaceArtifacts` | `WorkspacePersistenceService` | — |
| Source snapshots | `iqr_source_snapshots` | `IqrSourceStore` (also written by the package importer — deferred) | immutable snapshots |
| Source decisions (baselines) | `SourceDecisions` | `SourceBaselineService` (also the package importer — deferred) | append-only (trigger) |
| Scenario discovery | `scenario_discovery_results` | `ScenarioDiscoveryStore` | keyed by content id |
| Test plan drafts / snapshots | `test_plan_drafts` / `test_plan_snapshots` | `TestPlanSuggestionService` | draft revision check; snapshots immutable (trigger) |
| Manual test runs | `manual_test_runs` | `ManualTestRunService` | content pinned at start; revision concurrency token; one in progress per case (index); final runs immutable (trigger) |
| ADO export profiles / records | `ado_export_profiles` / `ado_export_records` | `AdoTestPlanExportService` | records append-only by use |
| Test Data | `App_Data/test-data/*.json` | `TestDataRegistryStore`, `AuthoredTestDataStore`, `TestDataIdentityStore`, `CanonicalFixtureStore` (reset via `TestDataLifecycleService`) | asset revisions |
| Shared test areas (scopes) | `scope_registry.*` | `ScopeRegistryService` | revisions; kept by ordinary reset |
| Functional-quality scopes (different concept) | `delivery_reporting` scope tables | `FunctionalQualityScopeService` | cleared by delivery reset |
| Declared integration journeys | `integration_journeys.*` | `DeclaredIntegrationJourneyStore` (legacy import reuses its rules) | append-only revisions |
| Review decisions | `ManualReviewDecisions`, `TestCoverageDecisions`, `ContractReviewDecisions`, `SourceDecisions`, canonical decisions | their services (`ManualReviewDecisionStore`, `TestCoverageReviewService`, …, `CanonicalInterpretationService`) | per store |
| Delivery snapshots / reports | `delivery_reporting.*`, `WeeklyReports` | `DeliveryReportingImportService`, `DeliverySnapshotStore`, `WeeklyReportService` | frozen snapshots |
| Critical E2E flows, versions, runs | `App_Data/critical-e2e/*.json` | `CriticalE2EStore`; suggestion reviews `CriticalE2ESuggestionStore` | gate verdict computed (`CriticalE2EReleaseReadiness`), Policy 2 versions kept |
| Release reports / sign-offs | canonical `ReleaseReports`, `ReleaseSignOffs` | `CanonicalInterpretationService.Release` | — |

Invariants now enforced by tests: every registered context has exactly one reset owner and a migration-inventory entry (`PersistenceOwnershipTests`); the Critical E2E store is backed up whole; suggestion reviews are cleared with the flows. Deferred (documented, not changed): the integration package importer (`WorkspacePackageService.Apply.cs`) inserts rows of ~15 tables owned by other services, bypassing their rules (e.g. `SourceBaselineService`'s monotonic decision time) — consolidating it means routing an import through each owner, a larger change than this remediation.

## 9. Run-history changes

- **Bounded read** (`ReadTraceabilityRunsAsync`): `SELECT Id, DraftId, CaseId, State, CompletedAt, StartedAt` for completed and in-progress runs of the project; per case the latest completed run (completion, then start time — the same rule `PreparedCaseTraceability` applies) and the newest in-progress run; only those rows are read in full. Stopped runs never give an outcome and are not read. Verified against an oracle computed from the seed data and against the full read.
- **Paging** (`ListPageAsync`, `GET api/manual-test-runs/page?skip&take`, take 1–500): `ORDER BY StartedAt DESC, Id DESC`, exact `Total`; the unpaged list keeps its first 500 for compatibility. Execute & document pages the history (50 per page) and states "Showing N of total runs".
- Semantics preserved: latest-run selection, pinned case revisions ("an earlier revision of the case"), Passed/Failed/Blocked/Not run, stopped runs not executed, coverage computed from all cases (never from a page).

## 10. Test confidence, autosave and requirement links

- PostgreSQL integration tests (temporary database, production migrations): concurrent saves at one revision → exactly one stored, the other 409; completed/stopped runs refuse save/complete; the database refuses `UPDATE` of a final run but allows progress and `DELETE`; another project's run is never read/listed/changed; one run in progress per case; bounded read = oracle; paging stable across 1,203 runs with an exact total; paging arguments bounded.
- Test Plan Suggestions (explicit save, not autosave): a save completing after a newer edit now keeps that edit unsaved and rebases it on the saved revision (the next save is not refused as stale); leaving the page or opening another plan with unsaved edits asks first; switching areas of the same plan does not. Failed saves keep the edits (retry by saving again).
- Requirement links: the server does not hold the specification (it is parsed in the browser), so a link cannot be verified against server-owned evidence without a new upload path. The server now records `CheckedAgainst = "browser-specification"` and a SHA-256 `CatalogFingerprint` of the requirement list on every new link, and Test Coverage / Requirements Traceability state that manual links are checked against the browser's reading of the specification, not independently verified. Existing links keep their recorded provenance (null = before this was recorded).
- Historical immutability: test plan snapshots (trigger, existing test), execution manifests, guide revisions, source decisions (triggers) — unchanged; manual runs now also protected in the database.
- Local Data Reset: not executed against any user database; behaviour checked by the existing reset tests and the new ownership tests.

## 11. Security verification

All on isolated local services (API :5097, web :5197, port :5099 for the listener probe), disposable databases, guarded browser network (5000/5173 and external hosts blocked); no remote host, real Entra tenant or user service was contacted.

| Check | Layer | Result |
|---|---|---|
| Published API started with `ASPNETCORE_URLS=http://0.0.0.0:5099` **and** `Hosting:AllowNonLoopbackBinding=true` | real process | **Refused at startup** ("…no project-level authorization, so a shared or network deployment would let every signed-in caller read and change every project (Hosting:AllowNonLoopbackBinding no longer enables one)"); port 5099 never listening |
| Same, every binding source (`urls`, `Kestrel:Endpoints`, `HTTP_PORTS`/`HTTPS_PORTS`) and with the former opt-out | integration (in-process host) | refused (`LocalSessionSecurityTests`, `LocalDeploymentBoundaryTests`) |
| Request from a non-loopback socket peer (192.168.1.20, 10.0.0.5, 203.0.113.7, ::ffff:192.168.1.20) — anonymous, with a bearer token, and the session bootstrap with the correct launch code | integration | **403** before authentication |
| Non-loopback peer with a forged `Host: localhost` | integration | **403** |
| Loopback peers 127.0.0.1, ::1, ::ffff:127.0.0.1 | integration | unchanged (200 with a session) |
| `X-Forwarded-For`, `Forwarded`, `X-Forwarded-Host`, `X-Real-IP` on a loopback request | integration + real HTTP against the Release API | **403** ("does not run behind a proxy") |
| Request addressed to another host name | real HTTP | **400** (unchanged rebinding protection) |
| Direct loopback request without a session | real HTTP | 401 (asks to connect; not mistaken for remote) |
| Bearer token from this computer with local trust off | integration | authenticated path unchanged (200 with the permission, 403 without) |
| Cross-project run access (get, save, stop, list, traceability) | PostgreSQL | 404 / not listed; other project unchanged |

Not verified: behaviour on a real multi-user Entra deployment (none exists; the mode is refused), Markdown sanitizer hostile fixtures, dependency advisories, archive fuzzing.

## 12. Performance measurements

`Traceability_read_cost_follows_cases_not_history` on PostgreSQL (temporary database, production migrations), 100 cases, runs with ~1.5 KB notes each, measured after one warm-up read on the same connection; one run each, same machine — indicative, not a benchmark.

| Stored runs | Read every run (before) | Bounded read (after) |
|---|---|---|
| 1,000 | 17 ms · 9,967 KiB allocated · 1,026 runs deserialized | 32 ms · 1,766 KiB · 126 runs |
| 10,000 | 231 ms · 97,608 KiB · 10,030 runs | **28 ms · 4,776 KiB · 130 runs** |

At 1,000 runs the bounded read is slower in wall time (two round trips instead of one) while allocating 5.6× less; at 10,000 runs it is 8× faster and allocates 20× less, and its deserialization stays bounded by cases (≤ 2 per case) while the old read grew with history. No performance threshold was changed. The paged list (`ListPageAsync`) reads one page plus a `COUNT`; the new index `ix_manual_test_runs_project_started` matches its order.

## 13. Regression results

All on the final code of this commit (builds non-incremental; the test binaries were checked to contain the new code before running).

| Run | Result |
|---|---|
| Full frontend suite (`BirkNext.Web.Tests`) | **9,109 passed, 0 failed, 0 skipped** |
| Backend partitions 1–6, sequential (`BirkNext.Api.Tests`) | **6,848 passed, 0 failed, 19 skipped** (1,052+2 · 1,099+5 · 888 · 676+1 · 539+3 · 2,594+8) |
| of which PostgreSQL remediation tests (`ManualRunPersistencePostgresTests`, temporary databases) | **11 / 11** |
| of which deployment-boundary, ownership and adjusted security tests | included above (focused run: 102 / 102) |
| API Release publish / Web Release publish | exit 0 / exit 0, **0 errors** |
| WebAssembly asset integrity (SRI check) | 87 assets, the one known baseline item |

New tests: `LocalDeploymentBoundaryTests` (listener, remote peer, forged host, proxy headers, loopback unchanged), `ManualRunPersistencePostgresTests` (bounded read vs oracle and vs full read, cost measurement, paging, argument bounds, concurrent saves, finality in service and database, isolation, one run in progress), `PersistenceOwnershipTests` (contexts ↔ migration inventory ↔ reset owners, Critical E2E backup files, suggestion-review reset), `ManualRunHistoryPagingTests` (bUnit), three Test Plan Suggestions tests (save race, leave prompt, same-plan area switch), and Technical Testing evidence-basis tests. Deliberately changed expectations: the non-local opt-out now refuses; two `LocalTrustedExecutionTests` cases use a loopback peer (a LAN peer can no longer even open a session).

Observed during the work and resolved without changing assertions: `LocalDataResetTests` returned "Blocked" when run in parallel with another reset test class (the reset gate is process-wide); run alone 10 / 10 and in the partitioned suite they pass.

## 14. Browser results

Release build of this code, web :5197, API :5097, fresh database `birknext_rem_final_20261115` (template copy), Edge via Playwright, guarded network.

| Acceptance pass | Result |
|---|---|
| Existing baseline (7 project categories) | **106 / 106** |
| Manual Testing phase a (plan, run, evidence, defect, resume) | **23 / 23** |
| Manual Testing phase b (after an API restart: persistence, completion, history, coverage/traceability execution) | **48 / 48** |
| Manual Testing phase c (failed step) | **7 / 7** |
| Requirement linking | **12 / 12** |
| Azure DevOps export | **38 / 38** |
| Workspace navigation pass 1 / pass 2 | **38 / 38** · **32 / 32** |
| Technical Testing (six projects, incl. I4: documentation-only and empty projects show no source-backed area) | **76 / 76** |
| Remediation (`rem-accept.cjs`) | **25 / 25** |
| Page errors · HTTP 5xx · guard violations (all passes) | 0 · 0 · 0 |

Remediation pass: a recorded run was copied 130 times in the disposable database (132 runs for LendoPlus); Execute & document showed "Showing 50 of 132 runs", *Show older runs* reached all 132 runs, each once, and the count followed; no horizontal overflow at 1440, 1280, 1024 and 768 px; Manual Testing home and Test Coverage still read executions after the bounded read; editing a plan and leaving through the sidebar asked first, declining kept the plan and the unsaved edit, accepting left; the relay and rebinding checks in §11 against the running Release API.

One console line in navigation pass 1 ("Failed to fetch frontend quality engine status") is the script leaving the page mid-request: the API log shows that request ended 499 (client closed) while another returned 200; the API log has 0 errors.

## 15. Remaining limitations

- No project-level authorization: shared deployment is refused, not supported.
- The integration package importer bypasses owning services (ARCH-01, deferred).
- Requirement links are consistency-checked against browser-supplied requirements.
- FQR/AQR results are browser-local without build identity (Technical Testing report).
- Coverage, mutation and flake analysis, dependency advisories and XSS fixture review were not performed.

## 16. Readiness

| Use | Assessment | Remaining gates |
|---|---|---|
| Local development | **Suitable** | — |
| Supervised internal evaluation (one person per computer) | **Suitable** | Treat derived analysis as reviewable evidence |
| Controlled internal pilot (several people, each on their own computer and database) | **Conditionally suitable** | Backup/restore exercised on the pilot machines; migration of existing databases (the new unique index is skipped with a notice if a case already has two runs in progress) |
| Shared multi-user Entra deployment | **Not supported — refused by the application** | Project membership store, central project authorization on every project-scoped endpoint, two-principal negative tests per API family, an explicit opt-in |
| Broader production | **Not established** | All of the above plus operations (monitoring, restore drills), accessibility and performance verification at scale, dependency advisory process |

## 17. Local commits

- Starting HEAD `a909061c`. This work is one local commit on top (see `git log`). Nothing pushed.
- Not committed (other work, untouched): `appsettings.Local.json`, the Kode67 gap register, older audit drafts, `AIAssisted/testdata/`.
- Temporary databases (template copies, not dropped): `birknext_rem_final_20261115`; the PostgreSQL tests create and drop their own randomly named databases. No existing database was changed.
