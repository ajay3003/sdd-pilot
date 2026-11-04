# Shared foundations — completion pass (2026-11-04)

Branch `008-traceability-first`, starting at HEAD `4ea69eb6`. The foundations under completion are:
- Test Data identity: `d539bb74`
- Snapshot Registry: `24ad6a67`
- Status Vocabulary: `40f7ad73`

None of them was rebuilt. Nothing was pushed.

## SHARED FOUNDATIONS COMPLETION AUDIT MAP

| Area | Current implementation (before) | Remaining problem | Risk | Files / services | Smallest correct fix | Migration? | Browser? |
|---|---|---|---|---|---|---|---|
| Performance Test Data references | `ScenarioDataMapping.AssetAliases` and `ScenarioVariable.AssetAlias`, resolved with `catalog.ByAlias()` / `Reveal(alias)` | A deleted asset whose alias is reused by a new asset silently rebinds saved definitions | Wrong data in runs; wrong consumer counts | `PerformanceTestContracts`, `PerformanceCentralTestData`, `PerformanceTestDataReferences`, `PerformanceTestStore` | Position-aligned `AssetIds` / `AssetId` beside the aliases; resolve through a shared resolver; ids excluded from version and comparison fingerprints | Yes (idempotent, no new versions) | Covered via Test Data usages |
| Critical E2E Test Data references | `TestDataAliases` and `CriticalE2EDataMapping.AssetAliases`, resolved by alias; runs record aliases only | Same as Performance; history cannot prove which asset was used | Same | `CriticalE2EContracts`, `CriticalE2ETestData`, `CriticalE2ETestDataReferences`, `CriticalE2EStore`, `CriticalE2EService` | `TestDataAssetIds` / `AssetIds`; runs record `TestDataAssetIds`; old runs say "Historical Test Data identity not recorded" | Yes | Same |
| Source Analysis selectors | `GET api/source-analysis` deserialized up to 50 full evidence blobs per request to build summaries | Every selector open loaded every payload server-side | Latency and memory; past 20 MB-class incidents | `IqrSourceEvidenceController.List`, `IqrSourceStore` | Registry stores each snapshot's summary once at registration; the list is served from registry rows | Yes (`AddSnapshotSummary`, one-time backfill) | Yes |
| Status: Performance / Critical E2E / Traceability | Local tones: "Blocked — …" amber when not hard; Draft amber; E2E local switch; Traceability emoji labels | Contradictions with the vocabulary | Misleading readiness | `PerformanceTestReviewDashboard`, `CriticalE2EPresentation`, `TraceabilityView`, `ArtifactTraceability` | Semantic mapping and tones derived from vocabulary intents; shared badge for Traceability | No | Yes |
| XML approval trust | Browser "Valid" approved schema-backed XML when no XSD was on the server (labelled client-reported) | Client claims still reached Approved | False assurance | `TestDataCatalogService.ApproveAsync` | Schema-backed XML requires server validation; draft and save still allowed | No | Message text only |
| Dark mode | No global dark theme; 9 components lightened text under `prefers-color-scheme: dark` while surfaces stayed light | Washed-out badges and KPIs in dark OS mode | Unreadable statuses | Delivery Overview, Performance, Project pane, Delivery workspace, scenario builder/steps, Documentation Drift, Azure | Removed the text-recolouring dark rules (translucent-background ones kept); danger badge text darkened to reach AA | No | Yes |

## 1–7. Test Data references (Performance and Critical E2E)

**1. Performance, before / after**
- Before: aliases only.
- After: each mapping keeps `AssetAliases` (display and history) plus `AssetIds`, aligned by position. Scenario variables keep `AssetAlias` plus `AssetId`.
- Ids are excluded from the definition fingerprint and the `ComparisonFingerprint`. Recording them updates the same version in place and never breaks baseline comparability.

**2. Critical E2E, before / after**
- Flows gain `TestDataAssetIds` and placeholder mappings gain `AssetIds`.
- Ids are excluded from `Material()`, so recording them is not a new flow version.
- Runs now record `TestDataAssetIds` (the stable ids actually used).

**3. Legacy migration** (`TestDataConsumerReferenceMigration`)
- Runs as a startup background pass and via `POST api/test-data/consumer-references/migrate`; idempotent.
- A unique alias is migrated to its id.
- An ambiguous or reused alias is left alias-only and flagged Needs review.
- A missing alias is preserved as written and Unresolved.
- It reports migrated / already stable / needs review / unresolved counts.
- Replace-references drops stale ids, so the migration records the replacement's id.

**4. Rename:** stored ids follow the asset. The test proves the same id, every consumer resolving, the new alias shown and consumer counts unchanged.

**5. Alias reuse:** after delete X and create Y with the same alias, saved Performance and E2E definitions resolve to *Removed* and raise "no longer exists" issues. Y is unused, and the migration never re-points a stored id.

**6. Historical identity**
- Runs recorded before stable ids keep `TestDataAssetIds = null`, which means "Historical Test Data identity not recorded".
- A legacy alias-only reference whose alias was reused is `LegacyNeedsReview` and is never attached to today's asset.

**7. Consumer counts**
- Lifecycle usages resolve by stored id (renames keep counts).
- Legacy unique aliases are attached.
- Reused or ambiguous legacy references are NeedsReview and unresolved, so they are never counted.
- The asset dialog counts only resolving references, adds "N unresolved", and labels legacy ones "Legacy Test Data reference · Needs review".

## 8–11. Source Analysis selectors and Snapshot Registry

**8. Selector, before / after**
- Before: `GET api/source-analysis` deserialized every stored snapshot (live: 7 snapshots, about 16 MB of evidence JSON) on each call.
- After: summaries come from `snapshot_registry.snapshot_entry.summary`. Each is computed once when the snapshot is registered, and earlier entries are backfilled once. Only legacy per-integration snapshots of an environment are still read from their store, and the endpoint falls back to the legacy path if the registry cannot answer.

**9. API usage**
- The list forces an incremental sync, so a snapshot uploaded a moment ago appears at once. The sync reads ids, plus new payloads exactly once.
- Each row carries `RegistryPointers`.
- Option text: "M2LB · 2026-11-03 09:12 UTC · Partial · 1680 files · Current · <short fingerprint>". The fingerprint is last and secondary, because it tells same-name archives apart.

**10. Payload and load time** (live, throwaway copy `birknext_ci2`, 7 real archives):

| | Response | Time | Full payloads loaded on the server |
|---|---|---|---|
| Old list | 10.1 KB | 0.42–1.10 s | 7 |
| New list | 10.3 KB | 0.020–0.062 s | 0 |

Selected snapshot detail stays lazy (`GET api/source-analysis/{id}` for the chosen one only).

**11. Pointer semantics**
- Latest (newest stored), Latest completed (Completed only, never a Partial/Failed fallback), Current and Approved (explicit) stay distinct.
- Live: Latest = a Failed Terraform snapshot, Latest completed = empty (all real snapshots are Partial or Failed), Current = M2LB.Common.
- `/snapshots` now explains the four pointers in a legend.

## 12–15. Status migration

**12. Performance**
- `PtStatus.Semantic`: any "Blocked …" is Blocked (danger), Draft and unsaved changes are Draft (neutral), "Not ready" is Warning, Ready is Ready.
- Running and Cancelling stay execution lifecycle states (shown as in progress).
- The pill tone derives from the vocabulary intent; pills carry `data-semantic` and the shared tooltip.

**13. Critical E2E:** `Semantic` / `Tone`:
- Not run and Cancelled are neutral and distinct from each other.
- Blocked and Failed are danger.
- Inconclusive is a warning.
- Passed is Verified.

**14. Traceability:**
- Emoji labels are replaced by `SemanticStatusBadge`: Covered = Confirmed, Missing tests = Missing, Orphaned = Unresolved, Not applicable.
- Requirements Traceability transitions use the shared badge.
- Relationship types (Shared/Foundation, groups) remain type badges.

**15. Remaining local badge debt:**
- `SourceStatusBadge` (sa-tone, already consistent).
- Module pill markup (`tep-`, `td-`, `iqr-`, `aqr-`, `e2e-`, `dd-`, `dlr-`) with vocabulary-correct tones.
- Performance engine `CapabilityStatus` pills.
- About 50 uppercase label selectors that are not statuses (headings, `dt`). Status pills now reset `text-transform` themselves.

## 16–18. XML validation trust

**16. Trust model:** unchanged.
- Server-verified schema / server explicit reference / client-reported only / schema unavailable / failed / not performed.
- The server result always outranks the browser claim, in both directions.

**17. Approval rule:**
- Schema-backed XML is approved only when the server validated it. A browser-only "Valid" gives "Approval blocked — server validation required … only client-reported as valid. The associated XSD (…) is not available to the server", with the actions: associate the XSD, retry validation, or keep it as a draft. Saving and drafts stay allowed.
- Non-schema XML is not blocked. Negative-test fixtures keep their rule.

**18. XSD association:**
- Associations are made by importing a source package that contains the XSD, or with `POST api/test-data/schema-associations`.
- **UI gap:** there is no upload screen yet (reported, not built).

## 19–22. Browser acceptance (real)

Setup:
- Isolated API :5099 on throwaway `birknext_ci2`; published Blazor WASM served on :5199.
- Playwright through Edge, with every non-localhost request aborted. **External hosts contacted: none.**
- Pages: Dashboard, Source Analysis, Snapshots, Performance, Critical E2E, Requirements Traceability, Test Data, Delivery Overview.
- Each page in light and dark at 1440 and 390 px (32 page runs per pass, plus a status sheet).

**First pass found real defects:**
1. Danger badges (Blocked, Failed, Conflict) had text contrast 4.41:1, below AA. Fixed with `#b91c1c`.
2. Delivery Overview and eight other components lightened text in dark OS mode on light surfaces, so badges and KPIs washed out. Fixed by removing those rules (there is no global dark theme).
3. The Performance "Execution engine status: AVAILABLE" pill inherited uppercase from its label. Fixed: `.ssb` and `.sd-pill` reset `text-transform`.
4. The snapshot detail showed raw storage and ids prominently, and labelled itself by id. Fixed: identifiers moved under "Technical details", self-reference uses the name, and the pointer legend was added.

**After fixes:**
- No horizontal page overflow, badge collisions, clipped badges, uppercase status badges or console errors on any page, theme or width.
- The status sheet uses real badge DOM and CSS. All 11 statuses (Not run, Available, Unresolved, Warning, Cancelled, Blocked, Needs review, Ready, Not assessed, Partial, Inventory complete) are distinct by label, icon and tone, with minimum contrast 4.79:1 in both schemes.
  - Available is blue, Ready is green.
  - Partial and Needs review are amber with different icons.
  - Blocked is red.
  - Not run, Not assessed and Cancelled are neutral.
  - Inventory complete is informational blue.
- Delivery Overview dark: badges and KPIs readable (screenshot checked). The remaining automated "1.58" reading comes from translucent `rgba` badge backgrounds the probe treats as opaque; it was visually verified as readable.
- At 390 px, Snapshots and Test Data stack without page overflow; the snapshot table scrolls inside its own container.
- Performance dark (screenshot): Draft neutral, "Available" informational (not Ready green), sentence case.

**Limits:**
- Performance, Critical E2E and Traceability had no definitions, flows or project selected in the throwaway browser profile, so they were checked in their empty and initial states. Their migrated statuses are covered by bUnit/presentation tests and the status sheet.
- Test Data rename, reuse and consumer-count flows were verified by backend tests, not clicked through.

## 23. Automated tests

**Focused new or updated tests:**
- `TestDataConsumerReferenceTests` (6): migration without new versions plus idempotency, rename + counts, delete + reuse, reused legacy alias, missing alias, run identity.
- `SnapshotRegistryTests` (+2): summaries served with broken payloads, one-time backfill.
- `TestDataXmlApprovalTrustTests` (3).
- `StatusSemanticsConsistencyTests` (+15): Performance, E2E, Not run vs Not assessed.

**Deliberate pinned-test updates (semantics changed on purpose):**
- Source Analysis option label.
- Two Performance blocker/draft tone tests.
- Two XML version tests: client-only approval is now refused, and promotion is proven with server validation.

**Full suites (scratch worktree, after all commits):**
- Frontend: **7965 passed, 0 failed**.
- Backend: **5724 passed, 13 skipped, 0 failed**.

## 24. Commits

- `629bdefe` testdata: migrate Performance and Critical E2E to stable asset references
- `00117327` snapshots: serve Source Analysis selectors from registry summaries
- `02c05112` status: migrate Performance, Critical E2E and Traceability statuses to the shared vocabulary
- `1d55c864` testdata: require server schema validation for XML approval
- (this commit) ui: browser acceptance fixes (contrast, dark-mode recolouring, inherited uppercase, snapshot detail) and report

## Confirmations

- Performance and Critical E2E store canonical TestDataAssetId references where applicable. Aliases remain as display and history.
- Alias rename does not break consumer references.
- Deleted or reused aliases do not silently rebind saved or historical consumers.
- Ambiguous legacy aliases are surfaced for review, never guessed.
- Historical alias-only references are not mapped to current assets.
- Used/Unused counts come from persisted stable references.
- Source Analysis selectors use Snapshot Registry summaries. The selected detail stays lazy-loaded.
- Pointer semantics are not conflated. Latest completed never falls back to Partial or Failed.
- Performance, Critical E2E and Traceability statuses use the canonical vocabulary:
  - Available ≠ Ready; Partial ≠ Needs review.
  - Blocked is danger.
  - Not run ≠ Not assessed; Cancelled ≠ Failed or Blocked.
- Schema-backed XML cannot be approved from browser-only validation. Server XSD validation is authoritative. A missing XSD allows draft and save but blocks approval. Non-schema XML is not blocked.
- Browser acceptance:
  - Light and dark, at 1440 and 390 px; the Snapshot Registry UI was visually checked.
  - Real defects were fixed before declaring completion.
- No real external traffic was generated (all non-localhost requests aborted; throwaway DB only).
- `appsettings.Local.json` was not modified. `AIAssisted/testdata/` and user Test Data were preserved. Nothing was pushed.

## Remaining gaps

- There is no XSD association upload UI (API and package import only).
- `GET api/source-analysis/dependency-sources` and AQR's OpenAPI snapshot loading still read full snapshots. They are not selectors and were out of scope.
- BirkNext has no dark theme. Dark OS mode now renders the light theme consistently, rather than a partial dark one.
- Performance/E2E pages were browser-checked in empty states only (no seeded definitions or flows in the throwaway profile).
