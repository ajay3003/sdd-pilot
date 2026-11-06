# Architecture hardening — database consistency and shared analysis foundations

Branch `008-traceability-first` · 2026-11-06 · local commits only (no push, no deployment, no live systems).

**Status legend**

- **DONE**: implemented and verified by automated tests.
- **PARTIAL**: implemented in part, or documented without a code change.
- **NOT IMPLEMENTED**: deliberately not done; the reason is given.
- **RUNTIME UNVERIFIED**: the behaviour depends on PostgreSQL or several processes, and only the in-process path was tested.

The decision record written before coding is `docs/architecture/data-ownership-and-consistency-2026-11-06.md`. It contains the ownership matrix, the consistency matrix and the source-of-truth decisions.

## 1. Architecture before and after

**Before**

- Four EF Core contexts share one PostgreSQL database. Each has its own schema and its own migration history.
- There is no cross-schema transaction, no outbox and no row-version token.
- The snapshot registry is a projection built lazily, when someone reads it (pull-based).
- Canonical XML fixtures and Test Data approvals are stored in workstation-local JSON files.

**After**

- The same four contexts and the same two file stores. No boundary was moved and no migration was added.
- Each verified failure window now has an explicit, testable behaviour:
  - Check-then-write sequences are serialised per scope with `ScopedWriteLock`: an in-process gate always, plus a PostgreSQL advisory lock held for the duration of a transaction.
  - Delivery imports are idempotent per preview.
  - File stores never report or keep data they could not persist.
  - The registry checks every owner store that can enumerate its records, and reports stale pointers.
  - Performance reconciliation covers every active run.
  - A deferred decision keeps the conflict or stale state of the decision it postpones.

## 2. Confirmed defects and risks

| Finding | Kind | Status |
|---|---|---|
| A concurrent release freeze stored duplicate reports (check-then-insert with no lock) | Defect (V) | **DONE**. Serialised per scope; test with 4 parallel freezes stores 1 report |
| A concurrent baseline promotion could leave two Active baselines with the same version | Defect (V) | **DONE**. Serialised per scope; test with 4 parallel promotions leaves 1 Active and unique versions |
| A retried Delivery commit (lost response) created a second snapshot | Defect (V) | **DONE**. Idempotent per preview; commits serialised per project |
| A failed canonical-fixture write left a ghost version in memory until restart | Defect (V) | **DONE**. Disk first; the failure is reported and the retry gets the next real version |
| An approval write failure was swallowed, so the approval was reported but not persisted | Defect (V) | **DONE**. The change is rolled back and the error returned |
| Deferring a Conflict or a stale confirmation removed the attention state, so a blocker vanished silently | Defect (V) | **DONE**. Deferred keeps the prior decision's Conflict or RevalidationRequired state |
| Performance reconciliation scanned only the 20 newest runs; an older interrupted run stayed "Running" | Defect (V) | **DONE**. Every active run is reconciled |
| A run was persisted as Queued before it was registered as executing, so a concurrent read could reconcile it while it started | Race (V) | **DONE**. Registered first; rolled back if the save fails |
| Registry entries for deleted Delivery, canonical, release, review and performance owners stayed "available" | Defect (V) | **DONE**. Each adapter now reports its owners' current records, scoped by type and module because some types are shared |
| A stored pointer could name a deleted owner | Risk (V) | **DONE**. Reported as `TargetAvailable=false`; computed pointers skip unavailable entries |
| A registry key race across processes failed the whole sync with an exception | Risk (I) | **DONE**. Caught; the next sync completes the registration |
| API, Integration and Frontend page-model readiness showed "Blocked: configure an endpoint" from placeholder detectors | Misleading output (V); no caller (V) | **DONE**. These endpoints now answer Not assessed with `EvidenceAuthority="none"`; the routes are kept |
| A frozen release report cannot be recomputed from its `InterpretationId` alone, because it reads live review states | Provenance risk (V) | **PARTIAL**. The stored `AssessmentJson` stays authoritative and immutable; documented, not rewritten |
| Legacy TraceLinks and canonical relations overlap, with no synchronisation | Design (V) | **PARTIAL**. Documented as two independent authorities (§7); not merged; neither ever promotes the other |
| Project Import retry reuses its snapshot only if it is still the newest | Risk (I) | **NOT IMPLEMENTED**. Documented; requires an import-identity lookup change in Project Import |
| A cancel during the post-load cooldown keeps the run Completed | By design (V) | No change: the workload completed with full metrics; documented |
| The canonical fixture and Test Data registry files are host-local (single-host design) | Constraint (V) | **PARTIAL**. Documented (§10); writes hardened |

## 3. Data ownership matrix

See the decision record, §2. Each data family has one writable owner. Cross-context links are references (owner id plus fingerprint). The registry holds metadata only, and the owner store stays authoritative.

## 4. Cross-context consistency matrix

See the decision record, §3. The table below shows what each workflow now does after a partial failure.

| Workflow | After a partial failure |
|---|---|
| A. Source import → registry | The registry self-heals on the next sync, because it is a pull projection |
| B. Evidence → interpretation | On demand and idempotent: the snapshot is reused when its fingerprints are unchanged |
| C. Decisions → freeze | A freeze either stores one report or refuses a duplicate. A deferral never clears a blocker |
| D. Delivery import → reports | A retry returns the stored snapshot. Deleted reports and snapshots become unavailable in the registry |
| E. Fixture save → catalog | No version exists unless it was written. An approval exists only if it was persisted |
| F. Run → result | Every orphaned active run becomes ExecutionFailed ("Interrupted"), never Pass. Partial metrics are never assessed (unchanged) |

## 5. Reconciliation and retry contracts

- **Registry sync** (`SyncAsync`, forced with `POST api/snapshots/sync`):
  - Idempotent by `(Type, Id)`.
  - Bounded to the owners' id lists.
  - Never deletes anything: a missing owner marks the entry unavailable.
  - Safe to run repeatedly and from several processes.
- **Delivery commit:** the same `stagingId` returns the same snapshot for the 30-minute life of the preview. After that the preview has expired and a new preview is required.
- **Release freeze and baseline promotion:** concurrent requests are serialised. The second freeze of identical evidence is refused with a clear message.
- **Performance reconciliation:** runs on every read of the overview, run list and run, and on start and cancel. It covers all active runs.
- **Fixture save and approval:** a failed attempt returns an error and changes nothing, so a retry is safe.

## 6. Shared analysis foundation inventory

**Kept**
- `CheckOutcome`, `QualityResult` and `ScoreSemantics`.
- `GateDecision` and `ReadinessDimension` (Release).
- Frontend `StatusVocabulary`.

**Added** in `shared/EvidenceContracts.cs`
- `EvidenceFreshness`: Current, Stale, Missing or Unknown.
- `EvidenceReference`: owner, id and fingerprint.
- `EvaluationProvenance`: authority, rule version, evaluation time and input fingerprint.
- `EvidenceFreshnessRules.Of`: freshness is Current only on an exact match of the recorded and current identity. Missing evidence and evidence without an identity are never Current.

**Adopted** in two places that had separate implementations of the same comparison
- Canonical decision precedence, deciding whether a decision's evidence is unchanged.
- Test Data value-version staleness (`TestDataValueVersions.Changed`). The behaviour is identical; existing tests pass.

**Not adopted:**
- Delivery and Performance readiness already carry their own identity (`PerformanceReadinessIdentity`, snapshot ids). Forcing the contract onto them would add a second abstraction without changing behaviour.
- `EvaluationProvenance` is defined but used only as the shared vocabulary for future reports.

**Frontend explorers** (§8 of the brief) were audited and not changed:
- They remain browser-local, with their own parsers.
- No contradiction between status labels was found that needed a code change in this pass. The labels for equivalent evidence already come from `StatusVocabulary`.
- **PARTIAL:** no analyzer-identity or input-hash fields were added to the explorers.

## 7. Source-of-truth decisions

| Topic | Decision |
|---|---|
| Traceability: legacy TraceLinks vs canonical relations | Canonical relations decide release and coverage. Legacy scenario TraceLinks remain a separate feature (navigation off by default), used by Impact Analysis, Spec Drift and AI audit. No synchronisation and no promotion in either direction |
| Frozen release reports | The stored `AssessmentJson` is the record. It is never rewritten |
| Placeholder page-model readiness | Not assessed. The review pages' own engines are the authority |
| Delivery checklist % | One definition, `DeliveryMetrics.ChecklistPercent` (`checklist/1`), used by both the browser and the weekly report |
| Delivery "attention", "completed" vs "declared done", "verified" | Different meanings, kept separate and documented on `DeliveryMetrics` |
| Utdanning ↔ SkoleAdapter | Unchanged. It stays an IdentityConflict blocker |

## 8. Readiness semantics matrix

| Invariant | Where enforced | Test |
|---|---|---|
| Zero Required = Not assessed | `ReleaseReadinessRules.Decide` (unchanged) | existing ReleaseReadinessTests |
| Missing evidence never Ready | `EvidenceFreshnessRules`, registry pointers, Test Data dimensions | ConsistencyHardeningTests, SnapshotRegistryTests |
| Candidate ≠ confirmed | `CorrelationPrecedence.Counts` (a deferred candidate never counts) | ConsistencyHardeningTests |
| Generated XML ≠ approved | Fixture store `approved=false`; approval persisted or refused | ConsistencyHardeningTests |
| Deferred does not clear a blocker | `CorrelationPrecedence.Apply(prior)` and service wiring | ConsistencyHardeningTests, ReleaseReadinessTests |
| Executed ≠ passed; interrupted ≠ pass | Reconciliation → ExecutionFailed, thresholds not assessable | PerformanceTestReviewTests |
| Frozen assessments unchanged | No update path; freeze is serialised | ReleaseReadinessTests |
| Placeholder ≠ authoritative | `PlaceholderPageModel.NotAssessed` | QualityReviewPageModelServiceTests |

## 9. Delivery metric comparison

| Metric | Browser (`DeliveryIntelligence`) | Weekly report (`WeeklyReportBuilder`) | Same meaning? |
|---|---|---|---|
| Checklist % | checked ÷ total, truncated; no tasks → no percentage | Same (`ChecklistPercent`) | **Yes**. Shared and tested |
| Attention | Feature-level items, excluding source encoding | Every attention item | No. Documented |
| Completed | "Declared done" = explicit `Status: Done` | All tasks checked | No. Documented |
| Verified | Not assessed without test evidence | Not computed | No. Neither derives it from a checklist |

## 10. Database migrations and compatibility

- **No migrations.** The serialisation uses locks, not new unique indexes. Adding a unique index would require backfilling historical rows that may already contain duplicates.
- **API additions, all compatible:**
  - `SnapshotPointerView.TargetAvailable` (nullable, appended).
  - `QualityReviewStatus.NotAssessed` (appended; existing numeric values unchanged).
  - `QualityReviewPageModel.EvidenceAuthority` (nullable).
  - `TestDataRegistryStore.SetLifecycle` now returns `bool` (callers that ignore it compile unchanged).
- **Host-local stores:** the canonical fixture and Test Data registry JSON files are workstation-local by design. Back up `App_Data/test-data` with the database. Multi-instance hosting is not supported for these stores.

## 11. Tests added and results

**New tests**
- `Services/Persistence/ConsistencyHardeningTests.cs` (7):
  - baseline race;
  - Delivery retry idempotency;
  - fixture write failure and retry;
  - approval write failure;
  - Deferred precedence;
  - evidence freshness;
  - checklist metric.
- `ReleaseReadinessTests` (+2): concurrent freezes; deferring a contradicted rejection keeps the Conflict.
- `SnapshotRegistryTests` (+2): a deleted Delivery report is marked unavailable without touching the same-type release report, and the sync stays idempotent; a stale stored pointer is reported and computed pointers skip it.
- `PerformanceTestReviewTests` (+1): an interrupted run older than the 20 newest is reconciled.

**Updated tests** (intentional contract changes)
- `QualityReviewPageModelServiceTests` (3): placeholder builders now return Not assessed.
- `QualityReviewPageContractTests` (1): the enum has 7 values, with `NotAssessed` appended.

**Results**

| Suite | Result |
|---|---|
| Focused backend batches | 90 + 105 + 153 + 271 + 127 = 746 passed, 0 failed |
| Frontend full suite | **8327 passed, 0 failed** (unchanged count) |
| Backend full suite (six partitions) | **5930 passed, 13 skipped, 0 failed of 5943** (baseline 5931 + 12 new) |

### 11a. Backend partitions

| Partition | Total | Passed | Skipped | Failed | Peak memory | Minimum free |
|---|---|---|---|---|---|---|
| 1 | 971 | 970 | 1 | 0 | 1.0 GB | — |
| 2 | 1059 | 1054 | 5 | 0 | 2.3 GB | — |
| 3 | 806 | 806 | 0 | 0 | 0.4 GB | — |
| 4 | 646 | 646 | 0 | 0 | 0.7 GB | — |
| 5 | 511 | 511 | 0 | 0 | 1.9 GB | — |
| 6 | 1950 | 1943 | 7 | 0 | 3.7 GB | 3.4 GB |

All six partitions completed. The partitions are complete and non-overlapping, and their totals add up to 5943. No partition was aborted by the guard.

## 12. Memory guard and resource observations

- **First attempt aborted, as designed.** Mid-session, free memory fell to about 4.4 GB. The user's own `dotnet run` of the API and its 1.9 GB compiler server (not started by me, not stopped) were running at the same time. A combined build and focused test run peaked at 7.5 GB and the guard stopped it at 1.29 GB free; a plain build also hit the floor.
- **Fix: memory-conserving runtime settings.** With `DOTNET_gcServer=0`, `DOTNET_GCConserveMemory=7` and `-p:BuildInParallel=false`, the build peaked at 1.3–3.2 GB and tests ran in five bounded batches without approaching the floor.
- No operation ran concurrently with another heavy operation, and the floor was never lowered.

## 13. Changed files and local commits

Commits: database consistency · shared contracts · feature adoption · tests and docs (hashes in the git log of this branch). Files:

| Area | Files |
|---|---|
| Persistence | new `Services/Persistence/ScopedWriteLock.cs`; `CanonicalInterpretationService.Release.cs`, `PerformanceTestStore.cs`, `DeliveryReportingImportService.cs`, `PerformanceTestExecutionService.cs`, `CanonicalFixtureStore.cs`, `TestDataRegistryStore.cs`, `TestDataCatalogService.cs`, `SnapshotRegistryService.cs`, `SnapshotRegistrySources.cs` |
| Shared | new `EvidenceContracts.cs` (linked in both projects); `CanonicalCorrelation.cs`, `TestDataCatalogContracts.cs`, `DeliveryReportingContracts.cs`, `SnapshotContracts.cs` |
| Feature | `CanonicalInterpretationService.cs`, `QualityReviewPageModels.cs`, `QualityReviewPageModelService.cs`, `DeliveryIntelligence.cs`, `WeeklyReportBuilder.cs` |
| Tests and docs | the files listed in §11; `docs/architecture/data-ownership-and-consistency-2026-11-06.md`; this report |

## 14. Remaining risks and unresolved decisions

- **RUNTIME UNVERIFIED:** the PostgreSQL advisory-lock path of `ScopedWriteLock` (transaction plus `pg_advisory_xact_lock`) and cross-process registry races. The tests use the in-memory provider, which exercises the in-process gate only.
- **Project Import retry duplicate:** documented and not fixed (§2).
- **Frozen-report reproducibility:** the frozen JSON is authoritative, but the assessment cannot be recomputed from ids alone.
- **Impact Analysis divergence:** legacy TraceLink impact and canonical impact can name different tests. Merging them is a product decision.
- **Host-local file stores:** backup and restore are a manual responsibility, and multi-instance hosting is unsupported.
- **Not done here:** the broader API and GraphQL authorization gaps remain a separate workstream (queued as the localhost security hardening).

## 15. Architecture diagram changes required

- Show the snapshot registry as a **pull projection** of the owners: registry → owners arrows labelled "sync/inventory", and no owner → registry write arrows.
- Mark the canonical fixture and Test Data registry stores as **host-local file stores** outside PostgreSQL.
- Show traceability links (AppDbContext) and canonical relations as **two separate authorities**, with no synchronisation edge.
- Annotate freeze, baseline promotion and Delivery import with "serialised per scope (advisory lock)".
