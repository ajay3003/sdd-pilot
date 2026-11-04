# Canonical Interpretation & Correlation Engine — Phase 2 report (2026-11-04)

Branch `008-traceability-first`. Builds on `2c8e2cb4` (audit map), `2b67b105` (shared work-item engine) and `85bf48d2` (Traceability consumer). Cases A–F,
`WorkItemInterpretation.cs`, its rule order and `work-items/1` are unchanged. Nothing was pushed.

## A. Audit findings

**Existing foundation, verified in code:**
- `shared/WorkItemInterpretation.cs`:
  - Has 8 ordered rules, a rule id on every outcome, and `RuleSetVersion = work-items/1`.
  - Delivery is an adapter onto it. New reports record `InterpretationRuleSet`; frozen reports are never reclassified.
  - Traceability consumes it.
- `shared/SnapshotContracts.cs`: `SnapshotReference`, `SnapshotType` and the Source Analysis reference index (#15).

**Canonical concepts that already existed under other names (reused, not duplicated):**

| Concept | Existing implementation | Phase 2 use |
|---|---|---|
| Contract identity | `ContractEvidenceQueries.Identity` (type, namespace without version, name stem) | Declared contract identity (`ContractOccurrenceInput.DeclaredIdentity`) |
| Contract occurrence | `ContractArtifactRef` (`snapshotId:path`) | Occurrence identity = snapshot + package + path |
| Test identity | `SourceTestDefinition.StableIdentity` (repo + project + FQN); link confidence via `TestEvidenceQueries` (Explicit/Strong vs Candidate/Inferred) | Test entity identity; explicit vs candidate VERIFIES |
| Operations / source symbols | `CodeRiskEvidence.Endpoints` (controller/minimal-API routes) | Source symbols with routes; ApiOperation = service + method + route with parameters folded |
| Messaging | `ArchitectureSnapshot.MessagingChannels` (producers/consumers) | MessagingChannel + Produces/Consumes |
| Stated names | `ComponentNames.Stated` ("Full name (ABBR)") | Explicit aliases |
| Declared aliases | Topology profile components, Test Pack `IntegrationAlias` | Explicit aliases |
| Observed operation identity | `EndpointObservationHistoryService.Normalize` / `PerformanceScenarioEngine.Template` | AQR path lookup |

**Gaps found:**
- **No persistence.** Topology is rebuilt on every read, and `SetupProfileService` notes that "BirkNext keeps no alias store".
- **Aliases live in four places:** the Topology `_aliases` dictionary, the IQR pack aliases, `IntegrationIdentityResolver` and Test Data aliases. None of them is persisted or reviewable.
- **Contract content hash:** no contract fingerprint is stored on `SourceContract`.
- **Source symbols:** there is no general class/method symbol table. Only endpoint routes and test definitions carry symbols.

**Architecture decisions:**
1. **Storage:** PostgreSQL schema `canonical_interpretation` with its own DbContext and migration history, mirroring `delivery_reporting`.
   - No graph database. Relations are rows with indexes on (scope, source/target/type).
2. **Identity:** canonical identity = (scope, type, identity key).
   - The id is generated once and found again by its key.
   - Display names, aliases and file paths are never part of an identity key, so moved files and renamed labels keep their ids.
   - Occurrences (snapshot + location) and aliases are separate tables.
3. **Rules:** pure and deterministic in `shared/CorrelationRules.cs`, with `RuleSetVersion = correlation/1`. Rule ids:
   - CR-G-INTENT, CR-H-CAPABILITY
   - CR-I-HASH, CR-J-VERSION, CR-J-CONFLICT
   - CR-K-DECLARED, CR-K-ACRONYM
   - CR-L-ROUTE, CR-L-REQUIREMENT, CR-L-CONTRACT, CR-L-SERVICE, CR-L-NAME
   - CR-M-EXPLICIT, CR-M-CANDIDATE, CR-M-SUITE
   - CR-N-EXPOSES, CR-N-CONTRACT, CR-N-CHANNEL, CR-N-PACKAGE
4. **Precedence (`CorrelationPrecedence.Apply`):**
   - A human decision applies within its scope while its evidence fingerprint is unchanged.
   - Changed evidence: a confirmation needs revalidation (unless the evidence itself now establishes the link). A rejection stays rejected against heuristics, and becomes a visible **Conflict** when the source now states the link explicitly.
   - Candidates never count as traceability.
5. **Interpretation snapshots are frozen membership rows** (relation state, rule and evidence fingerprint at that time).
   - A reinterpretation creates a new snapshot.
   - An identical one (same sources, rule set, decisions and content) is reused, so reimport is idempotent.
   - Source payloads are never copied; occurrences point at snapshot ids.
6. **Scope** = the project key the browser works in: the Target Environment profile id, which Topology and IQR already use as `environmentId`.
   - Validated (`[A-Za-z0-9._:-]`, at most 200 characters).
   - Every query is scoped, and aliases and decisions never cross projects.

## B. Implementation

**Shared (frontend and backend):**
- `shared/CanonicalCorrelation.cs`:
  - Entity, relation, resolution, confidence, alias and review enums (all persisted as strings).
  - Fact records, `CanonicalKeys`, `CorrelationPrecedence` and the API views.
- `shared/CorrelationRules.cs`: cases G–N, plus `CorrelationChain.Walk`.

**Backend:**
- `Data/CanonicalInterpretation/`:
  - Entities and the DbContext.
  - Migration `20261104073916_CreateCanonicalInterpretationSchema`, which creates 8 tables, all in its own schema.
- `Services/CanonicalInterpretation/`:
  - `CanonicalEvidenceAdapter`: stored Source Analysis snapshot → correlation input. Reads only; never re-parses.
  - `CanonicalInterpretationService`:
    - Ingestion is transactional, idempotent and batched: one query per table per scope, with no N+1.
    - Also: decisions, queries, chain, compare, frozen-snapshot relations, alias resolution, AQR operation lookup.
  - `CanonicalAliasSource`: the one alias source for consumers. It returns nothing on a non-relational test host.
  - `CanonicalInterpretationDataReset`.
- `Controllers/CanonicalInterpretationController`, under `api/canonical/{scope}/…`:
  - `interpret`
  - `snapshots`, `snapshots/{id}/relations`, `snapshots/compare`
  - `entities`, `entities/{id}`, `entities/{id}/chain`
  - `relations` (with `needsReview`), `aliases/resolve`, `operations`
  - `decisions` (GET and POST)
- **Topology:** `CanonicalAliasTopologyProvider` applies explicit and reviewer-confirmed canonical aliases through `TopologyBuilder.Alias`.
- **IQR:** `IntegrationReviewService` adds the project's canonical aliases to its pack aliases in both correlation paths. Confirmed aliases get provenance `Confirmed`. IQR keeps no alias logic of its own.
- **Local data reset:** "Reset everything" clears the canonical schema, children first. The scoped resets do not touch it.

**Frontend:**
- `CanonicalInterpretationApiService` and `CorrelationPresentation`:
  - Relation and alias states map onto existing `StatusVocabulary` terms: Explicit/Derived → Confirmed, Candidate → Inferred, Ambiguous/Revalidation → Needs review, Rejected → Removed, Conflict → Conflict.
  - Type and confidence are shown with `MetaBadge`.
- `CanonicalCorrelationPanel`: the shared embeddable panel.
  - Shows identity, aliases, occurrences and relations, with Confirm/Reject/Defer.
  - Uses the AQR `ReviewAssessment` + `ReviewStageList` staged loading, with generation-based stale-response protection and retry.
- `Pages/CorrelationReview` (`/correlation-review`, in Traceability & Impact):
  - Interpret selected snapshots.
  - Paged review queue with a rule filter, evidence and notes.
  - Explore entities.
  - Interpretation history with compare, and decision history.
- **Embeds:**
  - Integration Topology ("Shared system identities and aliases").
  - IQR ("Canonical identity", scoped to the selected integration).
  - AQR ("Canonical operations", method + path lookup).
  - Project Evidence pane ("Canonical package identities").
- Documentation catalog and navigation updated.

## C. Correlation cases

| Case | Implemented | Tested | Evidence |
|---|---|---|---|
| A–F | Preserved (unchanged) | Regression: `WorkItemInterpretationTests` pass | `shared/WorkItemInterpretation.cs` |
| G | Yes. Cross-module shared intent → Candidate IMPLEMENTS to a candidate capability, never SAME_AS. Thresholds: Jaccard ≥ 0.34 and ≥ 3 shared terms. Pairs within one module are never compared. | Pass | `CorrelationRulesTests.G_*` |
| H | Yes. Candidate Capability entity, features keep their identity, nothing collapses. | Pass | `H_three_modules_…` |
| I | Yes. ContractVersion keyed by content fingerprint; every occurrence kept with package, repository, path, snapshot, revision and declared version. | Pass | `I_identical_content…`, `I_a_moved_file…`; real corpus: 88 occurrences → 81 versions |
| J | Yes. Versions never collapse. Same declared version with different content → Ambiguous CANDIDATE_SAME_AS (CR-J-CONFLICT). No compatibility or SUPERSEDES inferred. Identical file names without a declared identity are never grouped. | Pass | `J_*` |
| K | Yes. Declared aliases are Explicit; an acronym match is only Proposed. Confirm/reject per scope; ingestion never overrides a human state. Topology and IQR apply only Explicit/Confirmed aliases. | Pass | `K_*`, `Scopes_are_isolated…`, `Consumers_read_only_applicable_aliases…` |
| L | Yes. A route attribute gives Explicit IMPLEMENTS; a name match only gives a Candidate. Requirement ids are REFERENCES, not IMPLEMENTS. Ownership gives Derived BELONGS_TO. Symbol identity survives file moves. | Pass | `L_*`; real corpus: 40 symbols, 41 operations |
| M | Yes. Explicit/Strong test links → Explicit VERIFIES; Candidate/Inferred → Candidate; no execution status on relations. | Pass | `M_*`; real corpus (below) |
| N | Yes. Packages contain services, services expose operations and produce to / consume from channels, contracts are referenced. Chains are partial; missing links are reported. | Pass | `N_partial_chain…`, `Chains_are_partial…` |

## D. Consumer integration

| Consumer | Status | What it does |
|---|---|---|
| Delivery | Compatible, partial | Delivery features feed cases G/H (`DeliveryCanonicalWorkItemReader`, the `deliverySnapshotId` in the interpret request). Report formats, persisted enums and frozen reports are unchanged. Reports do not yet record an interpretation snapshot id. |
| Traceability | Compatible, not migrated further | Still consumes the work-item engine (#16). VERIFIES and candidate links are available through the API, but Requirements Traceability does not read them yet. |
| Project Evidence | Partial | Canonical package identity (repository identity; each archive sha256 is an occurrence) is shown in the Project pane. The browser-stored binding model is unchanged. |
| Topology | Full for aliases | Backend provider applies shared canonical aliases; UI panel shows systems and aliases. Source-observed vs runtime topology is unchanged (Topology still owns that). |
| AQR | Partial | Canonical operation lookup (method + observed path, templated like Endpoint Discovery) shows implementing symbols, tests and contracts. AQR execution, authentication and readiness are untouched. AQR's own run keys still use raw `Method Path`. |
| IQR | Full for aliases, partial UI | Integration correlation uses the shared alias registry; a "Canonical identity" disclosure shows the selected integration's system entity. |

## E. Verification

**Unit and integration tests:**
- 38 backend tests in `Services/CanonicalInterpretation/`:
  - Pure rules G–N, including false positives.
  - Precedence matrix.
  - Persistence: stable ids across moves/renames, idempotent reinterpretation, rejected stays rejected, revalidation, frozen snapshots, compare, scope isolation, alias decisions, consumers, chain, request errors, schema self-containment.
  - Real-archive regression.
- 14 frontend bUnit tests: vocabulary mapping, panel, alias decisions, error with retry, no-scope state, page.
- Commands:
  - `dotnet test --filter "FullyQualifiedName~CanonicalInterpretation|FullyQualifiedName~WorkItemInterpretation|FullyQualifiedName~DeliveryReporting|FullyQualifiedName~Snapshot"` → 305 passed.
  - Frontend `--filter "…CanonicalCorrelation|…Navigation|…Documentation"` → 290 passed.
  - Full suites (scratch worktree):
    - Backend: **5649 passed, 13 skipped, 0 failed**.
    - Frontend: **7909 passed, 0 failed**.
  - Four pinned navigation tests (`NavMenuLayoutTests` ×2, `NavMenuSourceAnalysisTests`, `FeatureVisibilityTaxonomyTests`) were updated deliberately for the new "Correlation Review" row at the end of Traceability & Impact.

**Real archive regression** (all 7 corpus archives through the full Source Analysis pipeline, in memory; the ZIPs stay untracked):
- Reproduces the reference observation from the evidence: **1,710 tests, 26 with explicit links, 744 candidate-only**. These are logged, not hard-coded.
- 2,586 entities and 3,565 relations, built in about 1.2 s (in memory).
- VERIFIES: 20 explicit (the 26 explicit tests carry 29 links), 1,362 candidate.
- 296 links are unresolved because they are not requirement ids (ADR-028, B-12345, …). They are reported as a limitation and never forced.
- No SAME_AS, no confirmed relation without a decision, and every heuristic outcome is a Candidate.
- Reinterpretation is reused and every id is unchanged.

**Migration and live check** on a throwaway copy `birknext_ci2` of `birknext_sa10` (isolated API on localhost:5099, nothing external):
- The migration applied at startup into `canonical_interpretation` only.
- First interpretation of the 7 stored real snapshots: 2,595 entities and 3,565 relations in 3.9 s. The second run was reused (same id) in 1.5 s.
- 1,362 candidates are queued for review.
- A Rejected decision → reinterpretation produced a new snapshot (decision sequence 1). The relation stayed Rejected, and the compare shows 1 changed relation.
- Invalid scope → 400. Another scope sees 0 entities. Topology still returns 200 with the alias provider active.
- The API was stopped afterwards. The real `birknext` DB was not touched.

**Rollback:** the schema is self-contained. To roll back, run `dotnet ef database update 0 --context CanonicalInterpretationDbContext` (it drops only `canonical_interpretation`) and remove the DI registrations. No other table references it.

## F. Remaining gaps (not hidden)

1. **Capabilities from explicit declarations (H):** only candidate capabilities from shared intent exist. No source declares capabilities today, so capability→service and capability→contract links come only through reviewed chains, not automatically.
2. **Content hash (I/J):** a structural fingerprint of what Source Analysis parsed (operations, types, XSD structure, version), not a byte hash of the file. Contracts without recorded structure get no version entity (`SourceContract` stores no file hash).
3. **Source symbols (L):** limited to HTTP endpoints with routes and test definitions. There is no general class/method symbol table, and GraphQL resolvers are not mapped to operations yet.
4. **Delivery reports** do not record an interpretation snapshot id. Traceability does not read canonical VERIFIES relations yet.
5. **AQR** run keys stay raw `Method Path`. The canonical lookup is a separate panel, not wired into each AQR finding row.
6. **Project Evidence** bindings stay in browser storage. The canonical package identity is shown, but the binding is not migrated to it.
7. **Scope:** scope = Target Environment profile id. There is no authorization layer beyond BirkNext's existing local single-user model. The reviewer is the authenticated user when present, otherwise the name given or "local user".
8. **Topology rejected aliases:** Topology does not yet suppress its own "stated abbreviation" rule for an alias a reviewer rejected (that rule only applies to explicit "Name (ABBR)" text).
9. **Decision revalidation and new evidence:** an interpretation over a subset of snapshots marks relations from the other snapshots inactive. An interpretation is "the state for this source set", by design, and is documented in the snapshot's source list.
