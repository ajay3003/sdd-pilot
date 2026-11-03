# Shared snapshot infrastructure — Phase 0 audit map (2026-11-03)

Scope: queue item #15 ("common snapshot metadata, lineage and references; domain payloads stay domain-owned"). The inventory comes from a read-only sweep of
backend, frontend and `shared/`. About 30 snapshot, version and history mechanisms exist, and **none share a metadata or reference type**.

## 1. Inventory (database)

| Feature | Owner / storage | Id · version | Mutable | Payload | References (how) | Created / effective | Analyzer · hash | Delete / ref check | List | Decision |
|---|---|---|---|---|---|---|---|---|---|---|
| Source Analysis snapshot (also from Project Import) | `IqrSourceStore` · `IqrSourceSnapshotRecord` | Guid · none (re-analysis inserts a row) | no | `EvidenceJson` blob | ProjectImport id inside JSON | `AnalyzedAt` | `AnalyzerVersion`, `Archive.Sha256` inside JSON only | **no delete path**; only the bulk local reset | **full blob of up to 50 rows, deserialized, then summarized** | wrap; metadata columns are follow-up work |
| XSD schema package | `SourceSchemaArtifactRecord` | PK = SnapshotId | no | `PackageJson` | FK → snapshot, cascade | `CapturedAt` | `SnapshotFingerprint` | cascades | metadata-only index (good pattern) | keep (derived) |
| IQR evidence binding | `IntegrationEvidenceBindingRecord` | (env, integration) | upsert | `DocumentJson` | `SnapshotId` column, no FK | `UpdatedAt` | fingerprint in JSON | unbind only | – | reference (Bound) |
| IQR runs / journey runs | `IntegrationReviewRunRecord`, `IntegrationJourneyRunRecord` | Guid | no / until done | `ResultJson` | snapshot ids inside JSON | `CompletedAt` | – | none | blob | wrap later (not checkable today) |
| IQR quality snapshot | `IntegrationQualitySnapshotRecord` | Guid · `SnapshotVersion` | no | `IntegrationsJson` | `PreviousSnapshotId` | `CapturedAt` | – | none | **unused by any service** | retire or merge |
| Impact Analysis | `ImpactAnalysisRunRecord` | Guid | no | `ResultJson` | Baseline/Current SnapshotId columns | `CreatedAt` | fingerprints | none | blob (take 100) | reference (Current/Baseline) |
| Code Risk Review / Test Coverage Review | `AiCodeReviewRunRecord`, `TestCoverageReviewRunRecord` | Guid | no | `ResultJson` | Current/Baseline columns | `CompletedAt` | analyzer in JSON | none | blob | reference |
| Dependency Review | `DependencyReviewRunRecord` (+ inventory, health) | Guid | no | `ResultJson` / `SnapshotJson` | ids inside JSON | `CompletedAt` | hashes in JSON | none | blob | wrap later |
| Security expectations | `SecurityExpectationDiscoveryRecord` | Guid · `Revision` | decisions mutable | `EvidenceJson` | `SourceSnapshotId` column | `CreatedAt` | in JSON | none | – | reference |
| Azure environment | `AzureEnvironmentSnapshotRecord` | Guid | no | `SummaryJson` + `SnapshotJson` | source id inside JSON | `CapturedAt` | infra analyzer version | none | **summary-only** (good pattern) | keep |
| Performance | definition / run / baseline records | string / Guid / string · definition `Version`, run `DefinitionVersion` + `DefinitionSnapshot`, baseline `SupersededByBaselineId` | definition yes; run no once finished | `DocumentJson` | `SourceSnapshotId`, `ContractSnapshotId`, `BaselineIdAtRun` inside the document | `CreatedAt` | definition/comparison fingerprints | definition archived if runs exist; baselines never hard-deleted | blob | keep (sound lifecycle) |
| Endpoint Discovery | run + observed operation records | string / Guid | operations upserted | typed | First/LastRunId | `StartedAt` | – | none | typed | keep |
| Delivery snapshot / comparison / weekly report | `delivery_reporting` schema | Guid · `ParserVersion`, `ContentVersion`, `CalculationVersion` (+ `InterpretationRuleSet` from #16) | snapshot label/role/EffectiveAt (audited); reports frozen | typed + frozen `ContentJson` | Baseline/Current ids (FK SetNull) | **`EffectiveAt` ≠ `ImportedAt`** | source fingerprint, git commit | snapshot delete nulls report ids but keeps frozen content | typed summary | keep — the best existing model |
| project_activity | `ProjectActivityRecord` | Guid | insert-only | typed | related entity id | `OccurredAt` | idempotency key | none | typed | keep (audit sink) |
| Workspace documents / SDD lifecycle | `SavedWorkspace`, `SavedWorkspaceArtifact` | Guid · `Version`, `ParseVersion` | overwritten | content + JSON | `SddSourceSnapshotReference` (string id) | `CreatedAt` | `ContentHash` | workspace delete, no check | – | wrap references later |

Computed on demand, not persisted: Pipeline Review (memory cache keyed by previous/current snapshot id), Technology Coverage, Generated Documentation
(embedded in the Source Analysis snapshot with its own analyzer version), Markdown diagnostics.

## 2. Inventory (files and browser)

Critical E2E `App_Data/critical-e2e/*.json` (run id, flow version, 200 runs per environment, archive flag); Test Data `registry.json` / `canonical-fixtures.json`
(alias + append-only `Version`) / `protected-defaults.json`; Project Import staging (30-minute temp); AQR `birknext:api-review` (runs, baselines per target); FQR
`birknext:fqr-history:{profile}` (immutable reports, max 10); Endpoint Discovery browser cache (`SchemaVersion` 3); Project Evidence
`ProjectEvidenceSourceReference.SnapshotId` in browser settings. Browser-only histories cannot be referenced or checked by the server.

## 3. Existing per-domain reference types (seeds, not replaced)

`DeliveryReportSnapshotRef`, `CoverageSnapshotRef`, `AiSnapshotProvenance`, `ContractArtifactRef`, `IqrSourceSnapshotSummary`, `ArtifactRevisionRef` (duplicated in
backend and frontend), `SddSourceSnapshotReference`, `ProjectEvidenceSourceReference`. `CoverageSnapshotRef` and `AiSnapshotProvenance` already share one shape.

## 4. Key risks found

1. A Source Analysis snapshot is referenced by at least 6 stores without any reference check. Today nothing deletes snapshots, so nothing breaks yet; any
   future cleanup would silently orphan historical reviews.
2. The Source Analysis list endpoint deserializes up to 50 full evidence blobs (several MB each) to show summaries.
3. Analyzer version and archive hash exist only inside the JSON payload, so lineage questions ("which source version fed this review?") need a full payload load.

## 5. What #15 delivers (scoped, no new tables)

1. `shared/SnapshotContracts.cs` — `SnapshotType` (typed, never free text), `SnapshotReference` (lightweight pointer), `SnapshotReferenceRole`
   (Current / Baseline / Bound / Derived), `SnapshotReferrer`, `SnapshotReferenceReport` (`IsReferenced` counts only non-derived referrers).
2. `SnapshotReferenceIndex` + `GET api/snapshots/source-analysis/{id}/references` — answers "what references this snapshot" from the id columns that
   already exist (Impact Analysis, Code Risk Review, Test Coverage Review, Security expectations, integration evidence bindings; schema packages as Derived),
   and always lists the stores it cannot check (JSON-embedded and browser references). It never claims "unreferenced" when it could not look.
3. Tests: role detection, derived data not blocking, unchecked stores always named, plus a live check on an isolated API against a throwaway DB copy.

## 6. Not done (follow-up)

- Metadata columns or index on `IqrSourceSnapshotRecord` (archive hash, analyzer version, import id, payload size) and a metadata-only list endpoint (needs an
  EF migration; the `ListSchemaSnapshotIndexAsync` / Azure summary pattern is the model).
- A reference registry the JSON-embedded consumers write to (IQR runs, journey runs, Performance runs, Dependency Review, Azure) so they become checkable.
- Retention classes, delete-with-reference-check, current/latest pointers, lineage UI ("Based on: …") and a compact snapshot header.
- Wrapping Delivery, Performance and Critical E2E into the shared metadata view (their own lifecycles are already sound; nothing was changed there).
