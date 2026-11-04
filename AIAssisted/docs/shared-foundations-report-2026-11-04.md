# Shared foundations: Test Data identity, Snapshot Registry, Status Vocabulary (2026-11-04)

Branch `008-traceability-first`. Order as requested: Test Data, then Snapshot Registry, then Status Vocabulary.

Commits:
- `d539bb74`: Test Data
- `24ad6a67`: Snapshot Registry
- `40f7ad73`: Status Vocabulary

Nothing was pushed. Cases A–N, AQR/IQR execution and the Delivery rules were not re-implemented.

## A. Audit summary

| Area | Existing implementation (verified in code) | Verified gap | Change made | Consumers |
|---|---|---|---|---|
| Test Data | File stores in `App_Data/test-data` (`registry.json`, `canonical-fixtures.json`, `protected-defaults.json`). Asset ids are computed as `{provider}:{alias}` in five providers. Connected Systems already migrates alias → id. Server XSD check via Source Analysis snapshot artifacts. | No immutable id: renaming changes identity; a deleted alias registered again silently re-attaches references. No rename. The Skolenærvær XSD is in no stored snapshot, so approval falls back to the client's "Valid". No trust levels, and no persisted validation result. | Immutable TestDataAssetId, alias history, rename, compatibility resolver; explicit server-side schema associations; trust levels and persisted validation evidence | Performance, Critical E2E (alias), Connected Systems, registry usages, XML mappings, Skolenærvær, AQR/IQR readiness |
| Snapshot Registry | `SnapshotContracts` and a reference index (#15) over id columns, read-only. About 30 per-module stores. Delivery deletes snapshots (reports keep their frozen content). | No central metadata, pointers, retention or lineage. The Source Analysis list deserializes up to 50 full blobs. | `snapshot_registry` schema with module sync adapters, metadata-only listing, typed lineage, pointers, retention, deletion check, browser UI | Source Analysis, Delivery, canonical interpretation, Impact/CRR/TCR/IQR runs, Performance, Critical E2E |
| Status Vocabulary | `StatusVocabulary`, `SemanticStatusBadge` and `MetaBadge`, with a generated doc and drift test. 26 local badge implementations. | Missing terms (Not run, Available, Unresolved, Warning, Cancelled); "not run" mapped to Not assessed. About 25 contradictions: Missing/Not assessed shown red, Available styled as Ready, Detected as Ready, Partial grouped with Needs review, Blocked amber in AQR/E2E, Removed as review, uppercase labels. | Terms added, intents corrected, contradictions fixed at the mapping level, Dashboard migrated to the shared badge, uppercase removed from status selectors | Dashboard, Project Evidence, Source Analysis, Test Data, IQR, AQR, Critical E2E, Delivery, Schema Studio |

**Root causes:**
- Identity was derived from the mutable alias.
- Validation trust was implicit (a string the client recorded).
- Snapshot metadata existed only inside payloads.
- Each page had its own label→tone switch instead of reading the vocabulary's intent.

## B. Implementation summary

**Test Data** (`Services/TestData/`):
- `TestDataIdentityStore` (identities.json, lock + atomic replace).
- `TestDataSchemaAssociations` (`TestDataSchemaAssociationStore`, `CompositeTestDataSchemaSource`).
- Changes to the catalog, lifecycle, registry rename, validation service and controller.
- `shared/TestDataContracts.cs` gains `TestDataValidationTrust`, `TestDataValidationEvidence`, `TestDataSchemaOrigin` and the summary fields `AssetId`, `PreviousAliases`, `IdentityReview` and `ValidationTrust`.
- The UI asset dialog shows Asset ID, previous aliases, identity review and trust badge, and offers Rename.
- New endpoints:
  - `POST assets/{id}/rename`
  - `POST assets/{id}/validate`
  - `GET identities/{reference}`
  - `GET/POST schema-associations`

**Snapshot Registry:**
- `Data/SnapshotRegistry/` holds the DbContext, entities and migration `CreateSnapshotRegistrySchema` (own schema and history; 9 schema operations, all in `snapshot_registry`).
- `Services/Snapshots/SnapshotRegistryService.cs` plus `SnapshotRegistrySources.cs`, with five adapters: Source Analysis, Delivery, Interpretation, Review runs, Executions.
- `api/snapshots`: list, sync, get, lineage, pointers (GET/PUT), retention, deletion-check.
- The Delivery snapshot delete consults the deletion check.
- The full local reset clears the schema.
- `/snapshots` browser (Traceability & Impact).

**Status Vocabulary:**
- `StatusVocabulary` terms, intents and tooltips; `docs/status-vocabulary.md` regenerated.
- `DashboardPresentation.Semantic` and SemanticStatusBadge in Dashboard.
- Mapping fixes in ProjectEvidenceResolver, SourceSetupPresentation, SourceStatusPresentation, SourceEvidenceDomainsPresentation, TestDataPresentation, IntegrationReviewResultPresentation, AQR, Critical E2E, Delivery (2 presenters) and Schema Studio.
- Info and danger tone CSS from design tokens; six uppercase status selectors removed.

**Compatibility adapters:**
- Legacy ids, legacy aliases and renamed-from aliases resolve through `TestDataCatalog.ResolveReference` / `ByAlias`.
- Registry sync adapters read the owners' own ids, so no snapshot id changed.
- Status `FromLegacy` maps the new words.

## C. Test Data verification

**Stable identity:**
- The first catalog read assigns `tda_…` ids to existing assets (`Origin = Migrated`). It is idempotent across re-reads and new processes, and independent of the provider label.
- Rename keeps the id, re-keys usages, lifecycle and protection, and keeps the approval for unchanged content. The old legacy id and the old alias still resolve.
- A deleted alias registered again gets a new id; the old reference stays unresolved.
- Two assets sharing a legacy id are kept separate and flagged for review.
- Regeneration adds a version under the same id, and the approved version stays current.

**Migration:**
- Additive only: it writes `identities.json` and never rewrites `registry.json`, fixtures or protected defaults.
- The developer's real `App_Data` was not touched by any test.

**Schema evidence (real Skolenærvær v2.0.0 XSD from `AIAssisted/xsd`):**
- An explicit association is stored with namespace, declared version and fingerprints.
- Valid XML gives `ServerExplicitSchemaReference` + Validated.
- Invalid XML (absence code 6) gives `ValidationFailed`, even though the client claimed "Valid".
- No XSD + client "Valid" gives `ClientReportedOnly`; no XSD and no claim gives `SchemaUnavailable`.
- A snapshot-retained XSD gives `ServerVerifiedSchema`.
- A pinned or fingerprint-mismatched reference is never substituted.
- Evidence (trust, result, snapshot/association, path, hash, namespace, version, engine, timestamp) is persisted per identity, and every approval attempt records it.

**Limitations:**
- Performance and Critical E2E still persist aliases (not ids). They follow renames through `ByAlias`, but a reference made before a delete-and-reuse cannot be distinguished. Only id-based references (usages, Connected Systems) are exact.
- Approval still accepts a client-reported "Valid" when no XSD is available. It is now recorded and shown as "Client-reported only" and is never server-confirmed. Refusing it outright is a product decision.
- Explicit associations are created through the API only; there is no upload UI.
- The real Skolenærvær XSD is still in no stored snapshot. Associate it once to get server validation.

## D. Snapshot Registry verification

**Metadata:**
- Persisted per snapshot: owner id preserved; created vs effective; revision and fingerprint as recorded or null (never invented); analysis/rule-set version; storage reference; status; retention; frozen flag; metadata version.

**Listing:**
- Metadata only, with filters, paging and sorting.
- Live on a throwaway copy of `birknext_sa10` (7 real archives, 17 snapshots): list in about 190 ms. The first sync registered everything; a re-sync registered 0.

**References and lineage:**
- Live, M2LB shows → imported from its project import, and → interpreted by 2 canonical interpretations (one supersedes the other).
- Tests:
  - Delivery report → comparison → baseline/current, unchanged after a new import (scenario E).
  - Source Analysis → interpretation → Code Risk run (scenario D).

**Pointers:**
- LatestCreated and LatestCompleted are computed. Live, no Source Analysis snapshot is `Completed` (the real ones are Partial or Failed), so LatestCompleted is empty rather than pointing at a failed one.
- CurrentSelected and CurrentApproved are explicit and scoped; approval requires Completed; viewing never moves a pointer.

**Retention:**
- Classes: Temporary, Working, Historical, FrozenReportEvidence, Protected.
- Frozen evidence cannot be downgraded, and sync never overwrites a person's retention.
- Deletion check: Protected and frozen referrers block deletion. Live, protecting a snapshot made it non-deletable, with 6 stores named as not checkable. The Delivery snapshot delete refuses Protected snapshots.
- No automatic cleanup exists or was added.

**Consumers:** registered through adapters (no module write path changed):
- Source Analysis
- Delivery snapshots, comparisons and reports
- Canonical interpretations
- Impact Analysis, Code Risk Review, Test Coverage Review and IQR runs
- Performance runs
- Critical E2E runs

## E. Status Vocabulary verification

**Components migrated to SemanticStatusBadge:** Dashboard (hero, health areas, artifact cards, development readiness, runtime reviews) and the Test Data trust badge. Earlier work already covers Source Analysis overview/domain headers, Correlation Review, Snapshots, the AQR picker and FQR.

**Corrected mappings (local markup kept):** Project Evidence, Test Pack sources, Source Analysis tones, Source Domains, Test Data, IQR, AQR, Critical E2E, Delivery Reports/Overview, Schema Studio.

**Remaining local badge implementations:**
- `SourceStatusBadge` (sa-tone, now vocabulary-consistent).
- Performance `PtStatus` (Draft → partial unchanged).
- Traceability emoji labels.
- KPI score bands on the Dashboard (deliberately left as score bands, not statuses).
- `tep-`, `td-`, `iqr-`, `aqr-` and `e2e-` pills (tones fixed, markup kept).

**Accessibility and labels:**
- Every changed badge keeps a text label (never colour alone).
- New tones use the design tokens, which have dark-mode values.
- Status labels are no longer uppercased (Norwegian characters display as written).

**Not verified:** visual and dark-mode checks in a browser; covered by unit and bUnit tests only.

## F. Test results

**Commands:** `dotnet test` in the scratch worktree (backend and frontend), plus filtered runs.

**Workstream results:**

| Workstream | Tests | Result |
|---|---|---|
| Test Data | `FullyQualifiedName~TestData` + reset + Connected Systems (backend) | 124 passed |
| Test Data | Test Data / Schema (frontend) | 342 passed |
| Test Data | Trust | 7 passed |
| Snapshot Registry | Snapshot + Delivery (backend) | 106 + 8 passed |
| Snapshot Registry | Snapshot browser + nav/docs (frontend) | 496 passed |
| Status Vocabulary | Full frontend suite | **7918/7918 passed**, including 32 consistency tests |

- **Full backend (after all three workstreams):** **5713 passed, 13 skipped, 0 failed** (5 min).
- **Pinned tests updated deliberately:**
  - Delivery change labels (two tests).
  - Navigation rows (Snapshots).
  - Regenerated vocabulary doc (drift test).
- **Migrations:** `CreateSnapshotRegistrySchema` applied at startup on the throwaway copy `birknext_ci2`; it touches only `snapshot_registry`. The real `birknext` DB was never used.

## G. Remaining gaps

1. Test Data: Performance and Critical E2E references are alias-based (rename-safe, not reuse-safe). There is no schema association upload UI. Approval still accepts a client-only claim, but labelled as such.
2. Snapshot Registry:
   - The existing Source Analysis selectors (`SourceSnapshotSelector`, `GET api/source-analysis`) still load full snapshots. The registry list is the metadata-only path, but those selectors were not switched to it.
   - Sync is pull-based (throttled 30 s, forced on unknown ids). Owners do not push registrations.
   - Performance baselines, Dependency Review, Azure and Endpoint Discovery are not registered yet.
   - Delivery reports do not record an interpretation snapshot id; there is a rule-set string only.
3. Status Vocabulary:
   - Performance `PtStatus`, Traceability emoji labels and about 50 further uppercase badge selectors (plan explorer, suggestions and others) remain.
   - Most module pills keep local markup with corrected tones rather than SemanticStatusBadge (to avoid churn in about 100 pinned tests).
   - There is no browser-level dark-mode or screen-reader verification.
4. There is no authorization layer beyond BirkNext's local single-user model. Mutations record "the local tester" or the authenticated name.
