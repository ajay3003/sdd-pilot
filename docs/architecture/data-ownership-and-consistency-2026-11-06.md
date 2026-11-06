# Data ownership and cross-context consistency — decision record

Branch `008-traceability-first` · 2026-11-06. Source-verified audit (file:line evidence in the hardening report). **V** = verified in code, **I** = inference.

## 1. Architecture as found

- Four EF Core contexts share **one PostgreSQL database and connection string**, each in its own schema with its own migration history (`Program.cs:109-128`):

  | Context | Schema |
  |---|---|
  | AppDbContext | `public` |
  | DeliveryReportingDbContext | `delivery_reporting` |
  | CanonicalInterpretationDbContext | `canonical_interpretation` |
  | SnapshotRegistryDbContext | `snapshot_registry` |

  Startup migrates the four in sequence under a lock. There is no cross-schema foreign key, no `TransactionScope`, no outbox, no job table, and no row-version token, except `SecurityExpectationDiscoveryRecord.Revision`. **V**
- **The snapshot registry is a pull projection.** `SnapshotRegistryService.SyncAsync` reads every owner store when someone lists, finds or inspects snapshots (throttled to 30 s; forced on demand and via `POST api/snapshots/sync`). It inserts new entries and lineage edges in one SaveChanges.
  - No owner ever writes the registry at commit time, so a "save, then registration fails" window does not exist. **V**
- **Two file stores outside PostgreSQL**, both in App_Data and stated in code to be "workstation-local": the canonical XML fixtures (`canonical-fixtures.json`) and the Test Data registry (`registry.json`). **V**

## 2. Data ownership matrix (authoritative owner — never a second writable copy)

| Data family | Authoritative owner | Derived projections and consumers | Reference identity |
|---|---|---|---|
| Source snapshots, schema artifacts, review runs, traceability links/suggestions, workspace artifacts, performance definitions/runs/baselines | AppDbContext | Registry entries (SourceAnalysis, ImpactAnalysis…, PerformanceRun); canonical interpretation reads source snapshots | Snapshot Guid + `archive.sha256`; run Guid + comparison fingerprint |
| Delivery projects, sources, snapshots, comparisons, weekly reports, scopes | DeliveryReportingDbContext | Registry (Delivery*, WeeklyReport, FunctionalScope); canonical interpretation reads delivery snapshots | Snapshot Guid + `ReportSource.Fingerprint`/GitCommit |
| Canonical entities, relations, **human decisions**, interpretation snapshots, release reports, sign-offs | CanonicalInterpretationDbContext | Registry (Interpretation, ReleaseReadiness); Delivery scope service reads relations | `(Scope, Type, IdentityKey)`, `RelationKey`, decision `(Scope, Sequence)`, interpretation `ContentFingerprint` |
| Snapshot identity, lineage, pointers (metadata only) | SnapshotRegistryDbContext | Snapshot Browser, Source evidence list (falls back to owner), delivery delete check | `(Type, Id)` of the owner record — never a payload copy |
| Canonical XML fixture versions (draft/approval facts) | Canonical fixture file store (host-local) | Test Data catalog, readiness | Alias + version; approval in the registry file |

**Decision:** keep the four contexts and both file stores. There is no evidence for moving a boundary. Every cross-context link is **by reference** (owner id + fingerprint), and the owner stays authoritative. Consumers must treat a missing owner as *unavailable/stale*, never as present.

## 3. Cross-context consistency matrix (verified failure windows → decision)

| Workflow | Authoritative commit | Derived | Verified gap | Decision |
|---|---|---|---|---|
| A Source import → snapshot → registry | One SaveChanges (snapshot + schema artifact) | Registry entry on the next sync; git-link file; staging marker | (I) A retry reuses the snapshot only if it is still the newest. The git-link file is written after the commit | Keep: the registry is a pull projection and self-heals. Document the retry limitation |
| B Evidence → canonical interpretation | `PersistAsync` in one transaction; snapshot reused when fingerprints and decision sequence match | Registry | Occurrences are never retired (history kept as fingerprints) | Keep (on demand, idempotent) |
| C Decisions → assessment → **freeze** | Freeze = check, then insert | Registry | **V: a concurrent freeze creates duplicates.** **V: Deferred overwrites Conflict / RevalidationRequired, so a blocker silently disappears** | **Fix:** serialise freeze per scope (DB advisory lock + in-process gate, inside a transaction). **Fix:** Deferred keeps the attention state of the decision it supersedes |
| D Delivery snapshot → comparison → reports | One SaveChanges per import / report | Registry | **V: a retry after a lost response, or a concurrent commit, creates a second snapshot.** Reports are hard-deletable without updating the registry | **Fix:** idempotent commit per staging id (same snapshot returned on retry; commits serialised). **Fix:** registry inventory for delivery owners |
| E Fixture save → catalog → readiness | File write | Catalog read, readiness | **V: the item is added in memory before the write; a failed write leaves a ghost version until restart. V: registry (approval) write failures are swallowed — approval reports success without persisting** | **Fix:** write first, then commit to memory; surface failures. Document the host-local constraint and backup |
| F Performance run → result | `SaveRunAsync` per transition; terminal run immutable | Registry, baselines | **V: lazy reconciliation scans only the 20 newest runs, so an older interrupted run stays "Running". V: concurrent baseline promotion can create two Active baselines with the same version** | **Fix:** reconcile every active run. **Fix:** serialise promotion per scope (advisory lock + gate) |
| Registry vs owners | Owner | Registry | **V: only SourceAnalysis detects missing owners; deleted delivery/canonical/run owners stay "available"; pointers are never revalidated. (I) A cross-process PK race fails the whole sync** | **Fix:** inventory for every adapter that can enumerate its owners (marks unavailable, never deletes). Pointer view reports a missing target. Sync retries once on a key race |

## 4. Source-of-truth decisions

- **Traceability links (AppDbContext) vs canonical relations.** These are overlapping concepts held as independent records. The legacy links are scenario-row `Covers` links, behind the default-off `LegacyTraceabilityNavigationEnabled` flag, used by Impact Analysis / Spec Drift / AI change audit. Canonical relations (`Verifies`, human decisions) are used by Release Readiness, Relationship Review and Delivery scope.
  - **Decision:** no synchronisation. Canonical relations are authoritative for release and coverage decisions; legacy links are a separate scenario-matrix feature.
  - A legacy link is never promoted to a canonical decision, and a canonical candidate is never auto-confirmed.
  - Known divergence: Impact Analysis (legacy) vs canonical impact can name different tests. It is documented as a risk, not merged.
- **Frozen release reports.** The stored `AssessmentJson` is the immutable record. Recomputing it from `InterpretationId` alone is **not** possible, because the assessment reads live relation review states.
  - **Decision:** the frozen JSON stays authoritative for history. Documented as a provenance limitation; no rewrite.
- **API / Integration / Frontend "page-model" readiness builders** (`api/quality-review-page-model/*`) use placeholder detectors that always report "not configured". The frontend service has **no** caller (V); the main pages use their own engines.
  - **Decision:** report these endpoints as *Not assessed (unsupported here)* instead of a false "Blocked: configure an endpoint". The routes are kept for compatibility; no new dependency from the main pages.
- **Delivery metrics.** Checklist % is the same definition in the frontend `DeliveryIntelligence` and the backend `WeeklyReportBuilder` (checked ÷ total, truncated).
  - **Decision:** one shared formula, with a test proving equality.
  - "Attention", "Completed / Declared done" and "Verified" have **different, intentional** definitions. Documented and tested as explicit divergence, not merged.

## 5. Readiness semantics

There is no universal Ready. Each domain keeps its own rules:

| Domain | Model |
|---|---|
| Test Data | 8 dimensions |
| Performance | readiness items |
| Release Readiness | `ReadinessDimension` + `GateDecision` |
| Delivery functional scope | `ScopeCoverage` |
| Journey | `JourneyReadinessDimension` |

The shared vocabulary (`CheckOutcome`, `QualityResult`/`ScoreSemantics`, frontend `StatusVocabulary`) already exists and is kept. One small shared provenance/freshness contract is added for **evidence references** (owner reference, fingerprint, freshness: Current / Stale / Missing / Unknown). It replaces ad-hoc "is this pointer still valid" checks. Invariants are kept and tested: zero Required = Not assessed; missing evidence ≠ Ready; candidate ≠ confirmed; generated XML ≠ approved; configured threshold ≠ passed; executed ≠ passed; Deferred does not clear a blocker; frozen reports never change.

## 6. Explicitly not done (and why)

- No global transaction, outbox or broker: no workflow needs atomic multi-context writes. Every gap above has a single-owner fix or an idempotent projection.
- No relocation of the fixture store into PostgreSQL: the deployment is single-host by design (stated in code), so the gap is documented instead.
- No change to the Utdanning ↔ SkoleAdapter decision; it stays an IdentityConflict blocker.
- No new migrations: every fix uses locks, transactions or idempotent reads. A unique index would need backfill of possibly duplicated historical rows.
