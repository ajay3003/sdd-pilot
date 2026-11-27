# BirkNext — System Settings «Data Quality» (data quality validation center), 2026-11-27

**Status:**
- Part 1 (read-only architecture and data-lineage audit, design): **done**.
- Part 2 (implementation): **implemented**; browser verification below.

No commits or pushes. Nothing in the audit wrote to any database or App_Data.

The requirement-identity dry run that the user approved earlier the same day («approve the design direction, not the persistent migration») is
**Category B** of this feature. It is built here, not as a separate tool.

## Part 1 — Read-only audit

### 1.1 Overlap and safety
- **Working tree:** clean at `73b1327e` (V10). No other Claude session was running. The extra git worktrees are old, detached scratch copies from earlier sessions and do not overlap.
- **Isolation:** all verification uses the isolated instance (ports 5097/5197, disposable `birknext_adoverify_*` database, its own App_Data).
- **Not touched:** the normal TesterNative data, the repository's `App_Data` and the reset backups.

### 1.2 System Settings architecture (where the section goes)
| Fact | Evidence |
|---|---|
| One page, `/admin/system-settings`, with **sections** (`?section=`), not tabs. The sidebar comes from `DocumentationCatalog.SettingsGroups` (Configuration / Analysis / Developer). | `Pages/Admin/SystemSettings.razor:1,106-118,1400-1404`; `Services/DocumentationCatalog.cs:15,45-48` |
| A pane is chosen by `_activeSection`. Data loads lazily when the section is selected. Nothing runs on open. | `SystemSettings.razor:1368,1979-2013` |
| Every sidebar label must be documented (`DocumentedSettings`), or `DocumentationCatalogTests` and `UserGuidePageTests` fail. | `DocumentationCatalog.cs:51,214` |
| Diagnostic panes use `DiagnosticPageRenderer` (title, run, sections, export). | `SystemSettings.razor:1096-1137` |
| File download: `window.downloadTextFile(name, content, mime)`. | `wwwroot/js/reportExport.js:44` |

**Decision:** the Data Quality pane is a **separate component** (`Components/DataQuality/DataQualityCenter.razor`) hosted by the page.
- It resolves its API client with `Services.GetService`, as `SourceContextPanel` does, so the many existing System Settings test fixtures need no new registration.
- It goes in the «Analysis» group, with a Norwegian label.

### 1.3 Permissions
- **Backend policies:** `SystemConfigurationWrite` (`SystemConfiguration.Write`), `IntegrationConfigurationWrite` and `ActiveEventExecute`. A permission is held through an Entra claim or the Local Tester Mode loopback grant (`Program.cs:131-145`, `BirkNextPermissions.Holds`).
- **No admin or settings role exists.** The existing diagnostics (`POST environment-diagnostics`, data reset) are gated only by `AdminSettings:Enabled`.
- **Decision:**
  - The audit API requires `SystemConfigurationWrite`, the same as changing system settings. Launching an expensive audit and reading detailed record-level diagnostics are as sensitive as configuration.
  - The pane asks `GET api/admin/configuration-access` and shows «Du har ikke tilgang …» without the run controls when access is not allowed.
  - Unauthorized API calls get 403.

### 1.4 Long-running work
- **Pattern to copy:** `ApiFuzzingRunCoordinator`:
  - an in-memory singleton with one run at a time (a second start → 409);
  - a `CancellationTokenSource` per run, run with `Task.Run` in its own DI scope;
  - progress through a callback;
  - the last 20 runs kept;
  - start 202, status `GET …/runs/{id}` (no-store), cancel `POST …/runs/{id}/cancel`;
  - the frontend polls once a second (`ApiSafeFuzzingPanel.razor:299-350`).
- **Persistence:** there is no diagnostics store. Precedent: `CriticalE2EStore` (a JSON file under App_Data, bounded, cleared by Local Data Reset).
- **Decision:** the run coordinator keeps live runs in memory. Finished runs (metadata + report JSON) go to a **bounded JSON store** `App_Data/diagnostics/data-quality/`:
  - newest 20 runs;
  - 25 MB per report, larger detail truncated with a note;
  - cleared by Local Data Reset.
  - No database table and no migration.

### 1.5 Data lineage (what the dashboard number is made of)

```
ZIP upload ─► staging (%TEMP%, 30 min; the ZIP is DELETED when the import commits)
   │
   ├─► IqrSourceSnapshots.EvidenceJson   archive sha256, path inventory + per-file fingerprints (no file contents)
   ├─► SpecificationCatalogs             DocumentsJson [{Path, Sha256}] · EntriesJson [{RequirementId, Path, TextSha256}]
   │                                      EvidenceJson = parsed requirements / acceptance scenarios / stories per spec (SpecificationEvidenceBuilder)
   └─► WorkspaceDocumentBodies           document text by content address (Sha256) ← the only surviving source text
                                         
ScenarioDiscoveryResults (ContentJson; newest 5 per project; SelectionScope inside the JSON)
TestPlanDrafts (ContentJson: Cases[Source.ScenarioId, Generated.SourceIdentity, Review, ReviewHistory], PlannedScenarios[ScenarioId, SourceIdentity])
ManualTestRuns (DraftId, CaseId — "occ:{occurrence}" for scenario results — Outcome)
StoryGroupings (per specification: type, test-kind and preparation decisions)
   │
   ▼
TestPlanSuggestionService.OverviewAsync  ── GET overview ──►  WASM: ManualTestingOverviewPresentation (Compute, Rows, Coverage,
                                                                       TileCount, P1Attention, ScenarioReadiness, DrillDown …) ──► Testoversikt
```

**Consequences for the audit:**
1. **Source → stored (stage 1).**
   - The original archive bytes are not retained, so archive-level checks report **SOURCE UNAVAILABLE**.
   - Document-level checks are possible: each catalog document's text is found by its `Sha256` in `WorkspaceDocumentBodies`. A missing body → SOURCE UNAVAILABLE for that document. A body whose hash differs → FAIL (integrity).
   - The text is re-read by an **independent reference parser** (its own rules, not `SpecExplorerService`). Differences against the stored evidence are reported per document and item.
   - A difference in **parsing rules** is reported as WARNING with both values, not as an import FAIL, because the reference parser is deliberately simpler. Neither is presumed right.
2. **Stored → API (stage 2).** A **reference calculator** reads the tables above directly, with its own code, and computes every KPI. It is compared with `OverviewAsync`'s response, record by record.
3. **API → UI (stage 3).** The dashboard numbers are computed **in the browser** (`ManualTestingOverviewPresentation`). The «actual dashboard value» is therefore produced in the browser by the production presentation code from the same API response. The server oracle's expected value is compared with it.
   - A count that is wrong in both backend and frontend is caught, because the oracle is independent of both.
   - A difference only in stage 3 is classified «frontend display».

### 1.6 Consistent reads
- `FullBackupService` uses a `RepeatableRead` + `READ ONLY` transaction (`pg_export_snapshot`). No other snapshot facility exists.
- **Decision:** the oracle reads every table inside **one `RepeatableRead`, `READ ONLY` transaction**, which gives a consistent snapshot. The overview API call (stage 2's «actual») runs outside it, so the run records a **change marker** (max `UpdatedAt` / row counts / draft revisions) before and after.
- If the marker changed during the run, the run is **INCONCLUSIVE**. It is never PASS.

### 1.7 Requirement identity (category B) — evidence already gathered
- **v1 for a requirement without a scenario:** `"tss-" + sha("specification|requirement:{module}|{id}")[..16]`. The **module is the evidence's `ArtifactScope`**: null at import, a module when the browser had one selected. So **v1 for the same requirement can differ between an import and a browser discovery**. v1 is not stable across selection scopes.
- **Latent defect confirmed in code:** `TestPlanSuggestionService.CandidateIndex` maps source identity → candidate with `TryAdd`. When one discovery holds `FR-001` from two specifications under one v1 identity, **the first wins**. A case for document B can then be compared with document A's text, giving a confidently wrong «Current» or «Changed». `WorkQueues.cs:66` (`FirstOrDefault`) has the same pattern.
- **Testoversikt coverage is already document-scoped:** `CoveredKeys` = path + identity, with the case's path taken from its evidence `{file}#{id}`. A case for `FR-001` in A does not mark `FR-001` in B as covered. A case without a resolvable path matches nothing, which **under-counts and never over-claims**.
- **identity/1** (the canonical interpretation's reviewed identity) has **one project-wide entity per unscoped id** (`Requirement|FR-012`): every document's FR-012 is an occurrence of it. **One confirmation maps all of them to one feature pack.** That is the silent merge the user asked about. The dry run reports, for each confirmed identity/1 decision, how many source documents it covers. Nothing is changed.
- **No safe source-declared specification identity exists** (M2LB (9), 42 documents with requirement ids):
  - no front-matter ids;
  - the Spec-Kit «Feature Branch» line is missing in 6 documents and copy-pasted in 3 groups (`feature/skolenaervaer-spec` in 3 modules, `002-child-search-profile` and `002-batch-cdc-self-heal` in 2 each).
  - **Design consequence:** v2 = `tsr2-` + sha(`srcid/2` | normalized specification path | normalized requirement id).
    - The path is the only distinguishing key the source offers.
    - **Moves and renames are handled by detection, not by the hash.** Between two source versions, a requirement that disappears from path P and appears at path Q with the same id and the same statement fingerprint is reported **«RELOCATED? — må vurderes»**. It never gets a silent new identity, and a later persistent registry would carry the confirmed link.
  - **Application** is reported per record but **not hashed into v2.** Resolution always happens inside one application's discovery, so the application adds no disambiguation. Hashing it would break matching for cases prepared from workspace documents (no application key) and for an application re-imported as new.

### 1.8 The attached source report (`M2LB_9_kildeanalyse_BirkNext_2026-11-27.md`)
- **SRC-004 «Prosjektnivå 8 / 92»:** BirkNext's own server parser (run read-only with `dotnet fsi` against the built API assembly, over the ZIP in memory) extracts **all 106 acceptance scenarios** (= the 106 numbered Given lines) and **112 requirements without a scenario** from the 6 project-level specs of `M2LB (9).zip`. **The parser is not the cause.**
- 8 / 92 equals exactly the composition of the old «Prosjektnivå» *without* the project's own specs: 8 documented TEST-E tests, plus 50 misattributed module documents and 42 `docs/` documents. The likeliest cause is a dashboard built from a **catalog that does not contain the 6 project-level specs** (an older or different import, or a catalog chosen by the wrong rule), or a build from before the project-level fix of 2026-11-27. The isolated import of `M2LB (9).zip` (result below) decides which.
- Further items from the report are listed in §3 «Follow-ups».

## Part 2 — Implementation

### 2.1 Architecture
| Layer | File | Role |
|---|---|---|
| Contracts (shared, linked into Api and Web) | `shared/DataQualityContracts.cs` | Categories, statuses (PASS / FAIL / WARNING / INCONCLUSIVE / NOT VERIFIED), causes, stages, checks with records, identity dry-run rows, KPI expectations, runs, reports. |
| Snapshot | `backend/…/Services/DataQuality/DataQualitySnapshot.cs` | `DataQualitySnapshotReader` reads every input once, inside **one `REPEATABLE READ` + `SET TRANSACTION READ ONLY` transaction** that is always rolled back. It keeps the rows as stored JSON. `MarkerAsync` gives row counts and newest change times. |
| Reference (oracle) | `ReferenceCalculator.cs` | **Independent** calculation from the stored JSON (`JsonDocument`), with its own module rule, occurrence rule and execution rule. It never calls `OverviewAsync`, `PlanOccurrences`, `ModuleStructure` or `ManualTestingOverviewPresentation`. `Contracts` holds the written KPI contracts. |
| Reference parser | `ReferenceSpecParser.cs` | **Independent** reading of stored document text (own regexes; shares no code with `SpecExplorerService`). |
| Identity dry run | `IdentityDryRun.cs` | v1 (the production formula) and the proposed v2 (`tsr2-`, `srcid/2`, normalized path + normalized id). It lists every stored reference and classifies each requirement UNIQUE / AMBIGUOUS / UNRESOLVED / NOT AFFECTED. It also reports v1 variants and possible relocations. **Writes nothing.** |
| Engine | `DataQualityEngine.cs` | Runs the categories and keeps the stages apart. Records per check are capped at 200 (the rest are counted). Marker before and after → `Consistent`; when false, every PASS becomes INCONCLUSIVE. |
| Runs and store | `DataQualityRunCoordinator.cs` | One run at a time (409 otherwise), cancellation, progress. Finished runs go to `App_Data/diagnostics/data-quality/*.json`: newest 20, 25 MB per report (detail truncated with a note above that), cleared by Local Data Reset. **No table, no migration.** Also contains the Markdown writer. |
| API | `Controllers/DataQualityController.cs` | See §2.4. |
| Browser stage | `frontend/…/Services/DataQualityUiStage.cs` | The **production** dashboard calculation (`ManualTestingOverviewPresentation`) over the same API response, per expected figure. |
| UI | `frontend/…/Components/DataQuality/DataQualityCenter.razor` (+ scoped CSS) | The section. Resolves its client with `GetService`, so it is optional in other test fixtures. |
| Wiring | `Program.cs` (Api, Web), `DocumentationCatalog.cs` (sidebar «Analysis → Data Quality» + documented entry), `SystemSettings.razor` (`?section=data-quality`), `LocalDataReset.cs` (clears the store) | |

### 2.2 Isolation and data safety
- **Reads:** one read-only transaction. Inside it any write fails at the database, and the transaction is rolled back in `finally`.
- **Writes:** only the diagnostics store (JSON files) and the browser stage's values, which are attached to the stored run.
  - No plan, case, approval, readiness decision, execution, discovery or catalog is written.
  - No identity is migrated or relinked.
  - Nothing is re-imported.
- **Proof** (tests): `DQ30` and `DQ31` (real PostgreSQL) compare a fingerprint of every business table before and after a read and after a **full audit**, and both are equal.
- **Concurrent change:** detected by the marker → INCONCLUSIVE (`DQ18`).

### 2.3 KPI contracts (shown in the UI next to every figure and in both reports)
| Id | Contract |
|---|---|
| `baseline` | The project's newest specification catalog revision with a stored parse. A newer catalog from another import of the same application → **WARNING, stale data**. |
| `spec.acceptance` | Stored acceptance-scenario records per specification, identified by user story + number. A repeated key is a duplicate identity. |
| `spec.requirementOnly` | Requirement ids in the stored parse that no acceptance scenario of the same document names. Never counted as scenarios. |
| `documented` | Candidates from test specifications (`test-specification`) in the project's newest current discovery, unique by source identity. Documented E2E = type `BusinessE2E` (declared by the document, not verified). |
| `e2eCandidates` | Other `BusinessE2E` candidates (suggestions). **Not** in the E2E tile. |
| `metric.scenarios` («Scenarioforslag») | Every Testoversikt row: acceptance scenarios + requirements without a scenario + documented tests. |
| `metric.testCases` | Active cases (not Excluded/Superseded) in the project's active plans **and** in active plans of other imports of the same application. This is the dashboard's own scope rule, written down. |
| `metric.approved` / `metric.ready` | Review «Accepted» / stored testability «ready». Approved ≠ ready. |
| `metric.covered` | Rows (specification path + source identity) that an active case has. **Never matched on the display id alone.** A hand-written case under a scenario uses that scenario's case. |
| `module.scenarios` / `module.requirements` | Per module, acceptance scenarios + documented tests / requirements without a scenario (decision 3). |
| `module.rule` | The reference's own rule: first folder = module when the project has `<folder>/specs/`; root `specs/` = Prosjektnivå; otherwise Uten modultilknytning. |
| `plans`, `plan.occurrences`, `plan.execution` | Active plans. Occurrences: each scenario once (planned item or its cases' scenario); a hand-written case without a scenario on its own; excluded and superseded cases not counted. Execution: the newest result per occurrence (`occ:` runs); no result = not run. |
| `gaps.priority` | Rows without a case, grouped by their story's priority (P1 … P7, «Ikke prioritert»). |

### 2.4 API (all `[Authorize(Policy = SystemConfigurationWrite)]`)
| Method | Path | Purpose |
|---|---|---|
| GET | `api/data-quality/projects` | Projects with catalogs/discoveries and their catalog revisions. Cheap; no audit. |
| POST | `api/data-quality/runs` | Start (202). 409 while a run is in progress; 400 without a project or category. |
| GET | `api/data-quality/runs` | Recent runs (newest 20). |
| GET | `api/data-quality/runs/{id}` | Progress or result (no-store). |
| POST | `api/data-quality/runs/{id}/cancel` | Cancel. |
| POST | `api/data-quality/runs/{id}/ui-values` | The browser stage's production values (only for known figure ids, at most 5,000). Stored with the run. |
| GET | `api/data-quality/runs/{id}/report.md` / `report.json` | Reports. |

### 2.5 UI behaviour
**Setup**
- Project, the source baseline (the newest catalog, «samme grunnlag som dashbordet bruker»), three category checkboxes and «Start kontroll».
- A workload category instead of an invented percentage: «middels» without source text, «tyngre» with it.
- **Opening the section runs nothing.**

**While running**
- State, phase, completed checks, failed checks, elapsed seconds and «Avbryt».
- Start and project are disabled while a run is in progress.

**Result**
- Summary chips with text + colour, never colour alone: PASS / FAIL / WARNING / INCONCLUSIVE / NOT VERIFIED with counts.
- The baseline, the time, and Markdown/JSON download.

**Checks table**
- Status, check, stage, expected, actual, difference.
- «Detaljer (n)» expands the explanation, the counting rule, the cause and the exact records (searchable, 100 at a time, «Vis flere»).
- Filters: search, status, category.

**Further sections**
- **«Dashbord-tall: API → visning»:** reference vs. what the production dashboard code shows. A figure the dashboard does not compute is NOT VERIFIED.
- **«Kravidentiteter»:** affected requirements only, filtered by UNIQUE / AMBIGUOUS / UNRESOLVED; v1, v2, documents with the same v1, and the counts of related cases, plan items, readiness decisions, approvals and executions.
- **«Ikke kontrollert» / «Anbefalt oppfølging».**
- **«Siste kontroller»:** opens an earlier run.

**Naming:** the section is named **«Data Quality»** (user decision 2026-11-27; System Settings labels are English). The text inside the section is Norwegian, as the brief asked.

### 2.6 Performance
- **Server side:** one snapshot read, no N+1.
- **Bounded output:**
  - document bodies are read only for the baseline's documents, in chunks of 200;
  - 200 records per check;
  - the identity list is paged in the UI.
- **Measured:** `DQ22` (synthetic: 150 specifications, 1,800 scenarios, 1,650 requirements, 3,000 cases), with the full audit well within the 20 s limit of the test. Real M2LB: see §2.8.

### 2.7 Regression tests
Backend: `BirkNext.Api.Tests/Services/DataQuality`, **28 / 28**.

| Brief # | Test |
|---|---|
| 1 Independent KPI calculation | DQ01 |
| 2 Discrepancies detected | DQ04, DQ06 |
| 3 Backend and frontend share the same wrong count | DQ05 (server: expected = stored records, not API); frontend DQ04 (browser stage FAIL) |
| 4 Source version | DQ07 (stale), DQ08 (newest revision with a parse) |
| 5 Project-level grouping | DQ02 |
| 6 Requirements without acceptance scenarios | DQ01, DQ09, DQ10 |
| 7 Repeated FR-001 | DQ13 |
| 8 v1 collisions | DQ14, DQ16 |
| 9 v2 dry-run resolution | DQ14, DQ15, DQ17 |
| 10 Ambiguous not auto-resolved | DQ14, DQ16 |
| 11 Source unavailable | DQ10 (archive), DQ11 (text), DQ12 (hash) |
| 12 Consistent snapshot | DQ18, DQ30 |
| 13 No writes to domain data | DQ30, DQ31 (PostgreSQL) |
| 14 Access | DQ19; frontend DQ02 |
| 15 Cancellation and partial runs | DQ20; frontend DQ05 |
| 16 Large project | DQ22 |
| 17 Markdown and JSON | DQ23, DQ24 |
| 18 Browser navigation and display | frontend `DataQualityCenterTests` (8 / 8) + the isolated browser run (§2.8) |

Frontend: `DataQualityCenterTests` **8 / 8**. Documentation coverage and System Settings tests unchanged: **101 / 101**.

### 2.8 Browser verification (isolated v1env37, M2LB)
PENDING: filled in from the run.

