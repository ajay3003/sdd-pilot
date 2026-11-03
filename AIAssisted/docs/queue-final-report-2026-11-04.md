# Task queue final report — 2026-11-03 / 04

Branch `008-traceability-first`. 27 commits in this run (`c52f1e1f` … `12d0d7d8`). **Nothing was pushed by this session.** `git status` reports the local branch 8 commits ahead
of `origin` (#14–#16); the earlier commits already appear upstream, which was not done from here. `appsettings.Local.json` was not touched by any commit
(it stays a local modification), and `AIAssisted/testdata/` was not committed or changed.

Every task was implemented in a **scoped** form: a Phase 0 audit map first, then the foundation plus the highest-value consumers. Sections 2 and 3 list exactly
what is not done.

## 1. Done per task

| # | Task | Commits | What changed | Verified |
|---|---|---|---|---|
| 8 | Maintenance reset scopes | c52f1e1f, ccfa4936, b66a8931 | Backend reset plan with scopes (Delivery Management owns `delivery_reporting`, child-first FK order). "Reset everything" returns protected Test Data defaults to their product state. One "Reset data…" UI with scoped confirmation; Test Data reset moved under *Advanced reset options*. | Unit tests; browser on `birknext_rst_*` copies (18/18, 21/21, 24/24, 23/23) |
| 9 | SDD Dashboard | 3d6e6785, b507140b | Root cause: an import's activity counted application-wide setup items, so every import showed *Attention*. Now only its own facts count. New hero, evidence/review charts, packages, and a grouped activity timeline (pure `SddDashboard` model). | Unit tests; browser on `birknext_dash9` |
| 10 | Source Analysis | 632685f5, fd362621 | One `SourceEvidenceState` taxonomy, overview dashboard, and staged detail loading with cancellation. Architecture/Data keeps **Conflict** when conflicts exist, as you decided. | Unit tests; browser on `birknext_sa10` |
| 11 | Status/logic audit | ba4a8f95 | `docs/birknext-status-consistency-audit-2026-11-03.md`: 12 High, 27 Medium, 15 Low | — |
| 11 fixes | Audit fixes | 0a6e1132, d118350b, 02d91f0f | Headless runs: Failed/Blocked instead of a default Completed. A journey with 0 mandatory steps is Not verified. Interrupted runs recover. Runtime results are scoped to the environment. Data Quality Review save failures are reported honestly. The workflow no longer recommends already-run reviews. FQR runs no longer get stuck, and an empty run is not *Completed*. *Not assessed* is no longer red. (A1, A2, A3, A4, A6, A7, A15, B1, B9, C6, D1) | Unit tests |
| 12 | Canonical configuration/evidence ownership | 52b21601, 2288d0ef, 7fd50659, f08f706b, 84234333 | Audit map. Performance uses the live target and shows a drift notice; re-save is required before a run. Feature-visibility change events. Runtime results carry an environment fingerprint. The AQR last report counts as current only for the same target. One task/requirement reading (`+` bullets, FR/SC normalization). Package classification derived from one role rule. Exact run provenance snapshot. Technology Coverage states its scope. | Unit tests |
| 13 | Project Test Data | 3af43f68, 7dfdc3b1, 87a903d4 | Audit map. Consumers are safe with duplicate aliases. *Reveal* skips disabled assets. Approval refuses unvalidated values. Fixtures show *Regeneration required* when inputs changed. A full reset also clears protection records. | Unit tests |
| 14 | Global status vocabulary | 05704f2a, f2ed34de, 6dd1d582 | `StatusVocabulary`: one definition per term (label, family, intent, icon, meaning, use-when / do-not-use-when); intents map to design tokens once; `FromLegacy` falls back to a neutral *Unknown*. `SemanticStatusBadge` and `MetaBadge` keep type, provenance, environment and confidence apart from status colour. `docs/status-vocabulary.md` is generated from code, and a test fails on drift (regenerate with `BIRKNEXT_REGENERATE_DOCS=1`). Migrated: Source Analysis overview, coverage, legend and **domain headers (same state as the overview card)**; Infrastructure "Needs review" count renamed **Unresolved references**; Project Activity falls back to *Unknown* instead of *Completed*; review badge *Auto-accepted* / *Unknown*; environment and confidence pills in AQR/FQR/target picker; FQR access scope persisted as a string (old numbers still read). | 25 vocabulary tests; full suites |
| 15 | Shared snapshot infrastructure | 9a6edd61, 12d0d7d8 | Audit map (about 30 mechanisms, no shared type). `shared/SnapshotContracts.cs` adds `SnapshotType`, `SnapshotReference`, `SnapshotReferenceRole` and `SnapshotReferenceReport`. `GET api/snapshots/source-analysis/{id}/references` answers what references a snapshot from existing id columns, treats derived schema packages as non-blocking, and **always names the stores it cannot check**. No new tables, no payload copies. | 3 unit tests; live isolated API on `birknext_sa10` (synthetic binding inserted → *Bound*/blocking; removed → not referenced; unknown id → 404) |
| 16 | Canonical interpretation engine | 2c8e2cb4, 2b67b105, 85bf48d2 | Audit map. `shared/WorkItemInterpretation.cs` lifts Delivery's task→requirement-model rules: typed inputs, documented rule order, a **RuleId per outcome** (WI-SHARED, WI-PHASE, WI-MALFORMED, WI-REMOVED, WI-EXPLICIT, WI-FUNCTIONAL, WI-SCENARIO, WI-UNRESOLVED), `RuleSetVersion work-items/1`, and `SpecStoryHeadings` ingest rules. Delivery is now an adapter, with behaviour unchanged. New reports record `InterpretationRuleSet` and each task row its `RelationshipRule`; frozen reports are never reclassified. **Traceability consumes the same engine**: setup/polish tasks read *Shared & Foundational* (informational), and removed or undefined story references become review issues with Delivery's wording and a type badge. | Regression cases A–F, determinism, ingest parity, a Traceability consumer test; all 96 Delivery tests unchanged |

Final test state: frontend **7895/7895** passed. Backend **5653 passed, 13 skipped, 2 failed only under full parallel load**: the known GraphQL-variables timing flake and `SourceUploadPipelineTests` (`?environmentId=`). Both pass in isolation, and the Delivery and snapshot tests (99) pass.

## 2. Remaining work (by task)

**#6/#7 Delivery (carried over):** snapshot-history trend chart (placeholder), summary-first report payload (Overview loads ~5.4 s on v4 JSON), Delivery Reports Overview tab redesign.

**#11 audit — still open:** generic `catch → 500` paths, numeric enums elsewhere in persisted contracts, and most Medium/Low items in `birknext-status-consistency-audit-2026-11-03.md` that #12/#14 did not absorb.

**#12 deferred:** shared work-item model (partly addressed by #16), AQR contract ownership and GraphQL schemas, two XSD engines, five ZIP readers, summary list projection, glossary owner, unattended login per environment, FQR engine layers.

**#13 deferred:** stable non-alias asset ids (ids are `{provider}:{alias}`, so reusing an alias reuses the identity), sensitive values via the bind endpoint (product decision needed), the client-claimed "Valid" fallback (the Skolenærvær XSD is in no stored snapshot), per-environment values, persisted validation, server-side XML-mapping usages, list/count UI redesign.

**#14 deferred:** about 235 pill/badge class families, ~6,500 hard-coded colours and 98 uppercase-transform CSS files remain. 96 test files pin pill classes, so migrate page by page (badge + CSS + assertions together). Dashboard tones, ProjectEvidence "Auto-associated" as a provenance `MetaBadge`, and the remaining SourceDomain detail pages still use their own words.

**#15 deferred:**
- Metadata columns and a metadata-only list for Source Analysis snapshots. The list endpoint still deserializes up to 50 full evidence blobs (needs an EF migration).
- A reference registry the JSON-embedded consumers write to (IQR runs, journeys, Performance, Dependency Review, Azure).
- Retention classes, delete with reference check, current/latest pointers.
- Lineage UI ("Based on: …"), snapshot header, and wrapping Delivery/Performance/Critical E2E into the shared metadata view.
- Retire or merge the unused `IntegrationQualitySnapshotRecord`.

**#16 deferred:**
- No persisted canonical entity/relation tables and no interpretation snapshot.
- Cases G–N need a source/contract/test correlation layer: cross-module identity, capability candidates, contract occurrences with version context, evidence-only aliases, symbol correlation, explicit test VERIFIES.
- Shared human-reviewed associations.
- Migrating Task Alignment's positional fallback, the Topology and Project Evidence aliases, and the AQR/IQR consumers.
- Real-archive regression corpus run.
- Enums are still named `Delivery*` because frozen reports persist them.

## 3. Gaps and risks worth knowing

1. **Unchecked snapshot references (#15).** At least six stores keep Source Analysis ids with no reference check, and five more embed them in JSON. Nothing deletes snapshots today, so nothing breaks yet. Any future cleanup must use the reference index and respect `NotChecked`.
2. **Traceability removed-story detection** reads story headings only (SpecTree has no raw Markdown). A struck-through heading that the spec parser does not recognise as a story is caught by Delivery but missed by Traceability.
3. **Asset identity (#13).** Alias-derived ids mean renaming or reusing an alias changes or merges identity.
4. **The Skolenærvær XSD is in no stored snapshot,** so XML fixture validation for it falls back to client claims.
5. **Browser-only histories** (AQR, FQR, Endpoint Discovery cache, Project Evidence references) cannot be referenced or protected by the server.
6. **Flaky tests under parallel load:** ActivityCenter, MessageVerification, DatabaseMigrationLock, GraphQL variables, SourceUploadPipeline (`?environmentId=`), SchemaStudioReadableFirst. Observability was made robust.

## 4. Decisions taken without asking (as instructed)

- Each large brief was delivered as audit map, foundation and key consumers rather than a big-bang rewrite. The briefs themselves ask for gradual migration and no destructive recreation.
- Architecture/Data keeps **Conflict** (your earlier rejection of the change was respected).
- The Source Analysis domain header now shows the overview's canonical area state (e.g. *Detected*) instead of the old completeness word (*Partial*). Overview and detail can no longer disagree.
- *Missing* is neutral (not warning, never red). *Unknown* is the neutral fallback for any unrecognised persisted value.
- The work-item engine reuses the persisted `DeliveryTaskRelationship` / `DeliveryMappingConfidence` enums, so frozen reports remain readable.
- The snapshot reference index adds no table. It reads existing columns and reports what it cannot see instead of guessing.

## 5. Confirmations

- No external systems were called (no m2lbdev, TT02, Azure, Altinn, ID-porten, Service Bus or Event Hub). Acceptance used isolated local stacks on throwaway DB copies (`birknext_rst_*`, `birknext_dash9`, `birknext_sa10`). The real `birknext` DB was not modified or dropped. The one synthetic acceptance row in `birknext_sa10` was removed.
- `appsettings.Local.json` was not modified by this session. `AIAssisted/testdata/` and user Test Data were preserved.
- No reset, clean, stash, force-checkout or restore. All earlier commits and local work were preserved.
- Nothing pushed.
