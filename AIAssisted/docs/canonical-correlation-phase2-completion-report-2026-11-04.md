# Canonical correlation — Phase 2 completion report (2026-11-04)

Branch `008-traceability-first`. Builds on Phase 2 (`393ffac1`, `5ff01f9c`, `1bdd304b`). This is an audit followed by gap closure, not a rebuild. Nothing was pushed.

## PHASE 2 CANONICAL CORRELATION AUDIT MAP

| Area | Owner | Entity model | Matcher / correlator | Persisted | Version aware | Canonical | Duplicate logic | Decision |
|---|---|---|---|---|---|---|---|---|
| Work items A–F | `shared/WorkItemInterpretation.cs` | Delivery task relations | `WorkItemInterpreter` (8 ordered rules) | via Delivery snapshots | `work-items/1` | yes | none | **keep (unchanged)** |
| Delivery adapter | `DeliverySnapshotStore` | features, stories, tasks | the work-item engine | yes | rule set recorded on reports | yes | none | keep; now also feeds tasks to correlation |
| Traceability adapter | Requirements Traceability | work-item hierarchy | the work-item engine | no (built on read) | `work-items/1` | partly | none | extended in #22 (canonical VERIFIES chain) |
| Source Analysis models | `IqrSourceStore` / analyzers | projects, components, endpoints, contracts, tests | — (owner of parsed facts) | yes | analyzer versions | input to canonical | none | **extended**: code-risk v3 records declared types |
| Source symbols | code-risk analyzer → canonical | endpoint methods only | CR-L-ROUTE / CR-L-NAME | yes | `correlation/1` | yes | none | **extended**: type table + CR-L-MEMBER + CR-L-TASK |
| Contracts | `ContractEvidenceQueries` → canonical | Contract / Version / Occurrence | CR-I-HASH, CR-J-* | yes | `correlation/1` | yes | none | **fixed**: version context (J) |
| Tests | `TestEvidenceQueries` → canonical | Test / TestSuite | CR-M-* | yes | yes | yes | none | keep |
| Package capability logic | — (did not exist) | Package entity | — | — | — | — | — | **added**: multi-capability attribute |
| Shared libraries (M2LB.Common) | — (did not exist) | — | — | — | — | — | — | **added**: Library entity, CONTAINS, USES |
| Alias handling | canonical aliases; Topology, IQR and Test Pack declarations | Alias | CR-K-DECLARED, CR-K-ACRONYM | yes | yes | yes (Topology and IQR consume) | Topology still has its own "Name (ABBR)" rule (kept, see §17) | **fixed**: conflicting declarations become a review item |
| Reviewed / manual associations | `CanonicalInterpretationService.DecideAsync` | ReviewDecision | `CorrelationPrecedence` | yes | evidence fingerprint | yes | IQR mapping confirmation remains IQR-local | keep; moving the IQR write into the shared queue is #22 |
| Project Evidence | browser-stored bindings plus the canonical package panel | Package | — | browser | — | partly | binding model | **debt** (§33) |
| Integration Topology | `TopologyBuilder` plus `CanonicalAliasTopologyProvider` | components | ComponentIdentity / aliases | rebuilt on read | resolver version | aliases canonical | none new | keep; MU is one component (§35) |
| Connected Systems / Integration Catalog | Integrations | IntegrationDefinition | `IntegrationIdentityResolver` | yes | — | aliases from canonical | resolver kept (setup identity) | keep |
| AQR | API Quality Review | own run keys `Method Path` | canonical operation lookup panel | yes | — | partly | raw run keys | **debt** (§38) |
| IQR | Integration Quality Review | integrations | canonical aliases in both correlation paths | yes | — | aliases canonical | none | keep |
| Snapshot integration | Snapshot Registry | interpretation snapshots | — | yes | rule sets per snapshot | yes | none | keep; the summary reads frozen membership |

## 1. Existing Phase 1 engine reused
`WorkItemInterpretation.cs` is untouched; `work-items/1` and the rule order are unchanged. The canonical engine now also reads Delivery **tasks** (task id + text) through the existing `ICanonicalWorkItemReader`. This uses a default interface method, so existing readers and fakes are unaffected.

## 2. A–F regression status
`WorkItemInterpretationTests` pass unchanged (part of the 75-test canonical subset).

## 3–4. Canonical entity and relation persistence
These already existed (8 tables in `canonical_interpretation`). No schema change was needed: new types persist as strings.

New in this pass:
- Entity type **Library**.
- Relation types **OccurrenceOf** and **Uses**.

Not added: RELATED_TO, HOSTED_BY and MAPS_TO. Nothing produces them yet, and the brief says not to add unused types.

## 5. Identity and scoping rules
Unchanged principle: (scope, type, identity key), never the display name.

New identity keys:

| Entity | Identity key |
|---|---|
| Library | normalized published/project name |
| Task | `module/feature/task` |
| Type symbol | `package|namespace.Type`; nested types keep `Outer.Inner` |
| Contract version | `kind:sha256:hash[@versionContext]` |

## 6. Work-item vs capability identity
Unchanged: G/H produce Candidate IMPLEMENTS to a candidate capability, and never merge.

## 7. Source symbol identity
- Code-risk analyzer **v3** records every declared production type: class, record, struct, interface, enum.
- Fields: name, namespace, kind, file, line.
- Test projects are excluded; the cap is 20,000 with a limitation when reached.
- Identity = package + namespace + type.
- Real corpus: **1,437 source symbols**, previously 98 endpoint methods only.
- CR-L-MEMBER links an endpoint method to its declared type (68 on the real corpus).
- Snapshots analyzed before v3 have no type table. Each such snapshot gets an explicit limitation ("re-analyze to correlate tasks with classes"); nothing is guessed.

## 8. Task → source correlation (regression M)
Rule **CR-L-TASK**:
1. A task names a symbol by `` `backticks` ``, `File.cs` or a PascalCase compound.
2. The type's owning service must have a dot-segment equal to the task's module (exact, case-insensitive).

| Matches | Outcome |
|---|---|
| Exactly one in the module | **Explicit REFERENCES** (exact text, module and symbol in the evidence) |
| Two or more in the module | **Ambiguous** to each, for review |
| None in the module, exactly one elsewhere | **Candidate** |
| None in the module, several elsewhere | no link; counted in a limitation |

The task entity is created only when it names a known symbol (no empty tasks). A FEATURE CONTAINS TASK relation is added when the feature is in the same interpretation.

## 9–10. API operation identity and API → source
- Unchanged: service + method + normalized route, so concrete ids never create new operations.
- The route attribute gives Explicit IMPLEMENTS.
- New: the method's type owns the method (CR-L-MEMBER).

## 11. GraphQL
- Resolver methods from code-risk endpoints (kind GraphQL) become operations: `service|QUERY /field` (MUTATION, SUBSCRIPTION likewise).
- The resolver symbol has Explicit IMPLEMENTS to its operation.
- Schema-file to resolver correlation is not done.

## 12–14. Contract identity, occurrences and version context (regressions I and J)
**Version context** (`CorrelationRules.VersionContext`), in priority order:
1. The declared version (`v2.0`).
2. A version segment of the path (`/v2/`, `Api.V1.`, `_v2_1`).
3. Otherwise none.

`dev2/` and `review/` are never treated as versions.

**Version identity** = content hash + version context. As a result:
- **J:** identical bytes under v1 and v2 are **two versions** sharing one `contentHash` attribute. No SameAs, Supersedes or compatibility is inferred.
- **I:** identical bytes in several packages under the same (or no) context are **one version with several occurrences**. Occurrences now use **OCCURRENCE_OF**.
- **J-conflict:** the same declared identity and version with different content is still Ambiguous (CR-J-CONFLICT), now keyed by context.
- **Real corpus:** 70 logical contracts, 84 versions, 81 distinct contents, 88 occurrences. Three contents genuinely live under more than one version context, and are no longer collapsed.

## 15. Package capabilities
- The Package entity carries `capabilities`, taken from what Source Analysis found (file roles, domain evidence, projects, published packages), never from the name.
- Possible capabilities: ApplicationSource, SharedSource, Infrastructure, Contracts, Pipelines, Tests, Configuration, Documentation.
- Each capability has its own evidence line.
- Real results:
  - **Terraform-iac-meldingsutveksler** → Infrastructure, Contracts, Pipelines.
  - **M2LB.Common** → SharedSource, Pipelines, Tests.
  - **M2LB** → ApplicationSource, Contracts, Pipelines, Tests, Configuration, Documentation.

## 16. System, repository and component identity
- Package, Library, Service, System and SourceSymbol are separate entity types.
- A package CONTAINS a library; a service USES a library; a symbol BELONGS_TO a service.
- Nothing is merged across types.

## 17. Aliases (regression K)
**New:** an alias declared by two sources for **different** entities of the same type is no longer applied:
- Both declarations become Proposed, with rule **CR-K-CONFLICT**.
- An Ambiguous CANDIDATE_SAME_AS carrying both evidence sets goes to the review queue.
- A limitation is recorded.

**Real finding:** the Skolenærvær test pack declared `Skolenærværsrapport → M2LB.SkoleService.Api` and `Utdanning → M2LB.SkoleAdapter.Worker`, while the domain profile declares `M2LB.SkoleService.Api` for Skoletjenesten and `M2LB.SkoleAdapter.Worker` for SkoleAdapter. Under `correlation/1` both declarations were applied silently.

**Fix:** a pack's logical name that the domain profile itself declares as a name of a component (the profile lists `Skolenærværsrapport` as a Skoletjenesten alias) now merges into that component. This uses exact declared names, never similarity.

**Remaining genuine disagreement:** `Utdanning` vs `SkoleAdapter`. It stays a review item.

**Unchanged:** Topology keeps its own stated "Name (ABBR)" rule, which only reads explicit stated text.

## 18. Glossary usage
**Not used.** There is no machine-readable glossary to read. Declared aliases come from domain profiles, test packs and stated names only. The glossary is #24 (governance) scope.

## 19. Infrastructure normalization
**Not done.** The Infrastructure capability is recorded per package. Categories (workload, operations, identity, network, …) are deferred, because the Infrastructure domain owns resource typing and no consumer needs the category yet.

## 20. M2LB.Common shared capability
- Published packages and internal shared projects become **Library** entities. The capability comes from the last name segment (Health, Logging, …).
- A service's project or package references produce **USES**, but only to libraries some package in scope provides. Third-party packages are ignored and are not reported as errors.
- Real corpus: 24 libraries, 38 USES.
- Shared implementation is not duplicated per service.

## 21–22. Test identity and VERIFIES (regression N)
Unchanged from Phase 2: explicit/strong → Explicit VERIFIES; candidate → Candidate, never counted.

## 23–26. Semantic matching, precedence, negative rules, explainable confidence
Unchanged:
- Heuristics only propose.
- Precedence is via `CorrelationPrecedence`.
- Every relation carries a rule id, evidence and an explanation.

New negative rules:
- Generic shared segments do not name a module.
- `dev2` is not a version.
- Third-party packages are not shared capabilities.
- An unknown name in a task links nothing.

## 27–28. Rule ids and RuleSetVersion
- New rule ids: CR-K-CONFLICT, CR-L-TASK, CR-L-TASK-OWNER, CR-L-MEMBER, CR-N-LIBRARY, CR-N-USES.
- The rule set is now **`correlation/2`**, extending the existing scheme. It is stored on every interpretation snapshot.
- On the live throwaway DB, the old `correlation/1` interpretations remain unchanged. Compare shows `correlation/1 → correlation/2` with 386 added and 149 removed relations.

## 29–31. Reviewed associations, rejections and conflicts
Unchanged model (shared decision registry, evidence fingerprint, rejection persistence). The live DB kept its earlier Rejected decision through the upgrade (1,362 = 1,361 candidates + 1 alias conflict). Rule conflicts are now surfaced (§17).

## 32. Snapshot Registry integration
- Unchanged: interpretation snapshots are registered.
- New: the **summary** reads the latest snapshot's frozen membership, so entities left behind by an older rule set are never counted.

## 33. Project Evidence migration
**Partial.** The Project pane shows canonical package identity; the summary shows multi-capability packages. Bindings still live in browser storage (debt; carried into #22/#27).

## 34. Integration Topology migration
Unchanged consumption of canonical aliases. The conflict rule makes Topology receive no ambiguous alias.

## 35. MU duplicate resolution
- Topology resolves "MU", "Meldingsutveksleren" and "Meldingsutveksleren (MU)" to one component. This is existing behaviour, pinned by `ComponentIdentityTests`.
- Live: the Topology for `ci-live` shows **one MU component**.
- Canonical: MU is one System with Explicit aliases (test `K_MU_stays_one_explicit_system_alias…`).

## 36. Delivery integration
Delivery tasks now feed CR-L-TASK. Delivery reports still do not record an interpretation snapshot id (debt).

## 37. Traceability extension
**Not done here.** The end-to-end canonical chain in Requirements Traceability is #22 (Phase 3).

## 38. AQR migration
**Not done.** AQR's canonical lookup panel is unchanged; run keys stay `Method Path` (debt → #22).

## 39. IQR migration
Aliases are canonical (unchanged). IQR's mapping confirmations still don't write shared decisions (debt → #22, shared review queue).

## 40. Dashboard consumption
**New:** a "Shared identities and relations" tile reads `GET api/canonical/{scope}/summary` only. It shows:
- established relations;
- decisions needed;
- conflicts;
- logical contracts with occurrences.

It links to Correlation Review only when decisions are needed, and renders nothing without a project.

## 41. Cross-feature count consistency
One summary endpoint over one frozen membership. Correlation Review and Dashboard show identical numbers (verified in the browser: 2,439 established, 1,362 needing a decision, 70 logical contracts, 88 occurrences). Contract counts are always labelled.

## 42. Payload and performance

| Measurement | Value |
|---|---|
| Summary endpoint | 3.9 KB, 0.06–0.25 s |
| Live interpretation of 7 real snapshots (throwaway DB) | 6.9–8.1 s new; 2.1 s when reused (was 3.9 s and 1.5 s under `correlation/1`) |
| Live entities and relations | 2,736 and 3,803 (was 2,595 and 3,565) |
| In-memory real corpus with the v3 symbol table | 4,067 entities and 4,257 relations in 1.2–1.3 s |
| Topology | 154 KB, 0.9 s (unchanged consumer) |

The extra live time comes from alias/library/task rules and more relations. It is within the interactive Interpret action, so it is acceptable.

## 43. Browser acceptance
- Isolated stack: API :5099 on `birknext_ci2`, web :5199, Edge, scope `ci-live`.
- Correlation Review and Dashboard checked at 1440/390 × light/dark: summary visible, no console errors, no external hosts, minimum contrast 4.76.
- One defect was found and fixed: Correlation Review overflowed at 390 px (934 px wide). Grid children lacked `min-width: 0`.

## 44. Automated regression totals
**New tests:**
- Backend, `CorrelationCompletionTests` (30): version context theory (9), J/I, J conflict, alias conflict, real Skolenærvær declarations, MU, symbols L, task M (4), name parsing (4), member containment, libraries/USES, package capabilities, code-risk v3 symbol table, summary over latest membership, determinism.
- Real-archive regression: semantic invariants for capabilities, contract count nesting and USES targets.
- Frontend: `CanonicalSummaryUiTests` (3), plus the page test extended for the summary.

**Pinned updates (deliberate):** two `correlation/1` assertions, and the case-I key, which now includes the declared version context `@v1.0`.

**Full suites:** see §44a.

## 44a. Full suites (exact)
**Frontend:** 7977 passed, 0 failed.

**Backend, last complete full run:** 5771 passed, 13 skipped, **6 failed**. Status of each failure:

| Failing test(s) | Status |
|---|---|
| 3 × `ProjectImportPipelineTests` | Real defect, **fixed** (§44b). They pass now, together with the registry and source-upload tests (66 tests). |
| `DatabaseMigrationLockTests.ConcurrentMigrationsOfAFreshDatabase…` (1 min 20 s) | Passed on rerun. Timing-dependent. |
| `LoopbackHttpTestServerTests.UnexpectedHandlerFailure_StillFailsTheOwningTest` and `PerformanceRequestResponseTests.GraphQL_variables_…` | Fail only in full runs, by hitting their wait bound (17–41 s). They pass alone and within their namespaces (269/269). Their code is unchanged in this session. **Not proven flaky**: classified as an open environment/timing issue for #27. |

**Second full rerun:** started to capture these messages, but Claude Code stopped it because the machine was critically low on memory. It was not restarted. Memory pressure is a plausible cause of the two timeouts.

**Targeted suites after the final changes:**
- Canonical, code-risk and work-item tests: 75/75.
- Registry, import and source-upload tests: 66/66.
- Correlation and topology tests: 118/118.

## 44b. Defect found during regression: test hosts reached the real database's Snapshot Registry
The full backend suite failed three `ProjectImportPipelineTests`: a list expected to be empty contained 13 snapshots. This is a defect, not a flaky test.

**Root cause.** Introduced with the shared foundations (`00117327`, registry-backed Source Analysis listing):
- Test hosts swap only `AppDbContext` for an in-memory store.
- `SnapshotRegistryDbContext` therefore fell back to the configured connection string: the developer's real `birknext` database.
- The Source Analysis listing synchronises the registry, so in-memory test snapshots were registered there.
- Later tests then read the real registry rows. Those include snapshots the user imported for real today, which is why the list had 13 entries.

**Effect on the real database** (inspected read-only, never modified):
- `snapshot_registry.snapshot_entry` has 22 rows.
- 15 describe real data: the user's 8 Source Analysis imports from 11:56–12:00, and their Delivery project and report.
- **7 are orphans written by test runs** (`shop.zip` ×4, `M2LB (2).zip` ×3) with no payload in the real database.
- Nothing else was written: canonical and Delivery data in the real database come from the user's own use.

**Fix.**
1. The listing uses the registry only when the payload store is the relational database the registry describes. This mirrors the existing startup rule that skips the registry, Delivery and canonical migrations for swapped test hosts. Verified: a rerun of the affected tests left the real registry at exactly 22 rows.
2. The registry sync now reconciles payload availability. Each owner reports which payload ids it still holds (`ISnapshotSource.InventoryAsync`, ids only). A registered entry whose payload is gone becomes **payload unavailable**: it is never deleted and no longer offered for selection, and the sync reports it once.

The 7 orphan rows in the user's real database will therefore be marked unavailable automatically on the next normal app start or registry sync. Nothing was deleted manually.

**Test:** `SnapshotRegistryTests.A_registered_entry_whose_payload_is_gone_is_marked_unavailable_never_deleted_and_never_listed_for_selection`.

**Remaining risk, queued for #27 hardening:**
- Any other code path in a test host that uses the Delivery, canonical or registry contexts would still reach the configured database.
- A test-assembly-wide guard (refuse the default `birknext` database in the Test environment) is recommended.

## 45. Commits
See the git log after this report:
1. `analysis: record declared source types (code-risk v3)`
2. `analysis: correlation/2 — version context, libraries, task→symbol, alias conflicts`
3. `analysis: canonical summary API and dashboard/review summaries`
4. `docs: phase 2 completion report`

## Confirmations
**Work items and identity**
- Existing A–F interpretation was reused, not reimplemented.
- Persisted canonical entities and relations exist. New types are Library, OccurrenceOf and Uses.
- The same story title across modules does not merge work items. Similar stories can share a Capability without becoming one User Story.

**Symbols and APIs**
- Task → source-symbol relations use scoped deterministic identity where explicit evidence exists, and are Ambiguous or Candidate otherwise.
- The same symbol name in different modules stays distinct.
- API operations have canonical scoped identities. GraphQL resolvers map to root + field operations.

**Contracts, packages and systems**
- Contracts keep logical identity, source occurrence, content hash and version context separate.
- Identical XSD content across repositories does not inflate logical contract counts.
- Identical bytes under different version contexts are not collapsed.
- Packages expose multiple evidence capabilities.
- System, Package, Library, Service and SourceSymbol are not conflated.
- MU/Meldingsutveksleren aliasing is shared canonically; conflicting declarations are surfaced, not applied.

**Tests, decisions and history**
- Explicit Test→Requirement relations are canonical.
- Semantic matching only creates candidates; deterministic evidence outranks it.
- Reviewed decisions are shared through one registry. The IQR and Project Evidence write paths into it are **not yet migrated** (#22).
- Rejected candidates stay rejected under unchanged evidence.
- Conflicting authoritative declarations surface as review items.
- Every derived relation is explainable (rule id + evidence + explanation).
- Interpretations are versioned (`correlation/2`) and registered; historical `correlation/1` interpretations are unchanged.

**Consumers**
- Project Evidence still keeps its binding model in the browser. Not fully migrated; documented.
- Topology consumes canonical aliases; IQR consumes canonical aliases. AQR uses the canonical lookup panel only (documented debt).
- Delivery and Traceability still share the same work-item interpretation.
- Consumers use scoped and summary queries; no full graph is downloaded.

**Safety**
- No external graph database or new microservice was introduced.
- No real external traffic was generated.
- `appsettings.Local.json` was not intentionally modified.
- User testdata was preserved.
- Nothing was pushed.
