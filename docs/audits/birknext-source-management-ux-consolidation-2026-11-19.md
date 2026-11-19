# Source Management UX consolidation — 2026-11-19

Branch `008-traceability-first`. Local only; not pushed.

Starting HEAD: `d1d174ea`. The multi-role work (`fa4dc4f0`) was already committed, so this task builds on its final contracts. No concurrent edits were in progress.

## 1. Screenshot findings (from the brief)

The screenshot showed two sources: *Tiltak 2* (ZIP, Partly analyzed, Requires review, no application) and *M2LB* (ZIP, Partly analyzed, associated with M2LB / Development). The problems:

- **Source versions** filled a large block even though each source had one snapshot.
- **Six large summary cards** competed for attention.
- **"Role detected from content"** was shown on rows with no confirmed role, which reads detection as a role.
- **"No application yet"** wording.
- **Leftover "Azure DevOps" wording.** The lead text, origin help and empty state still said Azure DevOps after the Git-only change (`df7e9374`). So did two labels in Project Import.

## 2. Architecture and authoritative data (unchanged)

| Data | Authority | Read by Source Management via |
| --- | --- | --- |
| Imported sources | Import records (`SddLifecycle.ProjectImports`) + Git links (`IGitSourcesApiService.LinksAsync`) | `ProjectImportActivation`, the active application |
| Detected capabilities | Project Evidence packages (`IProjectEvidenceService`) | `ProjectEvidencePackage.Capabilities` |
| Source-level confirmed roles | `SddProjectImportRecord.ConfirmedRoles` / `GitSourceLink.Roles` (multi-role, `fa4dc4f0`) | unchanged |
| Application associations + bound evidence roles | Each Target Environment profile's `Project.Evidence[*].Sources` (the bindings Project Evidence persists) | `IFrontendAnalysisSettingsService.Settings.Profiles` |
| Pending association review | Project Evidence `PackageAssociation.State == NeedsReview` (active environment) | unchanged |
| Analysis per area | Shared source setup (`SourceSetupOutcome`), the same rule the Snapshot Registry uses | `IIntegrationJourneysApiService.SourceSetupAsync` |
| Versions / analysis baseline | Source context (`SourceContextApiService`, backend baseline engine) | `SourceContextPanel` |

No new registry, role API, role store or snapshot engine was added.

## 3. Ownership

- **Source Management:** what sources exist, what they contain, their versions, and where they are used.
- **Project Evidence:** per-application role bindings and their review. Source Management links to it with "View association" or "Review roles" and edits nothing there.
- **Snapshot Registry:** immutable snapshots and their lifecycle. Source Management links to the exact snapshot (`snapshots?open=SourceAnalysis:{id}`) and has no approval controls.

The existing source-level role editor (the *Capabilities & Roles* tab, which writes `ConfirmedRoles`) is kept. It predates this task, and multi-role made it the canonical source-level role. Project Evidence reads that value and shows "Roles confirmed in Source Management". It is not a second per-application editor.

## 4. Changes

**Source versions (`SourceContextPanel`)**
- The list is one line when no baseline decision is open:
  - "One snapshot per source — each is used for analysis." when every source has one snapshot;
  - "No analysis baseline needs choosing." otherwise.
- *Show versions* / *Hide versions* toggles it.
- When a decision is open, the list is open and cannot be hidden. Sources that need a decision open by themselves, as before.
- After a baseline decision the list stays open.
- New metric: "N with several snapshots".
- Subtitle shortened. It states that the baseline is not a deployment baseline.
- The baseline engine and its calls are unchanged. The backend never rewrites snapshots.

**Summary**
- The six cards are now a strip of compact pills. Each keeps its own count and definition: sources, requiring review, analyzed, partly analyzed, (failed), not analyzed, target applications.
- The explanation is the tooltip and is readable by screen readers.
- Analysis status and review status stay separate.

**Rows**
- "Detected" labels the capabilities.
- "No confirmed role" replaces "Role detected from content".
- "Not associated with an application" replaces "No application yet".

**Associations tab**
- Every target application that binds the source is listed with:
  - its environment;
  - the evidence roles bound to the source in that application's Project Evidence ("Confirmed: …");
  - its own *View association* link (`profile=<that environment>`).
- With several applications, each has its own link; the page never uses the first one.
- A note says that detected capabilities are not roles.

**Actions**
- *View analysis* (Source Analysis, exact snapshot).
- *View snapshots* (Snapshot Registry, exact snapshot).
- *Review roles* when associated; *Associate with application* when not.

**Address state**
- `?source=&q=&origin=&cap=&analysis=&review=1&sort=` hold the open source and the filters, written with `replace`.
- Back from a snapshot, Source Analysis or Project Evidence returns to the same view. So does a reload.
- An unknown `source` shows "The source in the link is not part of this project…" and opens nothing.
- A filter never closes the open source.

**Wording**
- Lead: "Manage imported sources, their versions, capabilities and application associations."
- Git wording in Source Management and Project Import ("From Git: <repository>"). The repository URL is not shown, because a URL can carry credentials.

**User Guide**
- The Source Management section now covers detected capabilities vs roles, the collapsed versions, and links that keep their place.

## 5. Tests

- **New `SourceManagementConsolidationTests`** (6):
  - the address format;
  - a deep link opens the source and filtering keeps the address in step without closing it;
  - filters are restored from the address;
  - an unknown source is reported;
  - two applications, each with its own roles and its own Project Evidence link, and capabilities never shown as roles;
  - exact snapshot and analysis links.
- **`SourceContextBaselineNotSelectedTests`:**
  - +2 tests: single-snapshot sources stay collapsed; an open decision shows versions without expanding;
  - 1 updated: a decided source is collapsed behind *Show versions*.
- **Updated:** `SourceManagementRedesignTests` (helper uses `SourceApplication`) and `SourceManagementTests` (wording).
- **Results:**
  - focused run (Source Management, Source Context, Project Import, Source Role Association, Git Sources): 200 passed / 0 failed;
  - full frontend suite: see the commit message.
- **Backend:** unchanged, so no backend tests were added.

## 6. Follow-up (same day): gaps closed

**Pending reviews per application.**
- Each application in *Associations* now shows its open reviews from that application's own Project Evidence view (`NeedsReview`, with the detail as a tooltip).
- The active environment comes from the page load. Other environments come from `IProjectEvidenceService.Peek`, which has no side effects; `GetAsync` can apply a target application, so it is never called for them.
- When an application's evidence has not been classified in this session, the row says "Open reviews: see its Project Evidence". It never says "none".
- Views are cached once per list build, not looked up per row.

**Performance** (bUnit, the real page component, synthetic import records; one source-setup read per load verified):

| Sources | Initial render | Search |
| --- | --- | --- |
| 100 | 34 ms | 3 ms |
| 500 | 519 ms | 32 ms |

**Real-browser acceptance.** Release publish of API + Web (0 build errors) on an isolated instance:
- API on :5097, web on :5197, and a new disposable database `birknext_sm_20261119` cloned from the acceptance template;
- Edge with the repository network guard, which blocks :5000/:5173 and external hosts.
- **Result: 24/24 passed:**
  - lead text; no Azure DevOps wording;
  - versions collapsed when no decision is needed, and *Show versions* works;
  - no horizontal overflow at 1440/1280/1024/768/390 px;
  - search writes `q=`, and the visible/total count is shown;
  - opening a source writes `source=`;
  - the unassociated state is shown;
  - *View snapshots* opens `snapshots?open=SourceAnalysis:<id>`;
  - Back restores the same address and reopens the source; reload restores it;
  - *View analysis* opens `source-analysis?snapshot=<id>`;
  - keyboard toggle works;
  - an unknown source is reported and opens nothing;
  - 0 blocked requests and 0 browser errors.
- After-screenshots (1440/1280/1024/768/390, plus the detail and associations panels) are in the session scratchpad (`sm-shots`).
- No before-screenshots were taken. The pre-change layout is the one described in section 1.
- Asset integrity: 86/86 framework assets match. The checker's one "missing hash" is the manifest's own `hash` key, not a file.
- One cosmetic fix came from the screenshots: the *partly analyzed* pill had a left-only coloured border.

## 7. Final verification (2026-11-19, final code)

**Review status is per application.** A source can serve several target applications (for example Tiltak 2), and each has its own open decisions.
- The row flag now names the application the decision belongs to: "Requires review · ScopesAcceptance (Development)", with tooltip "Association with … needs review: …". The detail callout does the same.
- *Associations* gives every application its own review state:
  - *Needs review* when that application has an open decision;
  - *No open review* when it was checked and has none;
  - *Open reviews: see its Project Evidence* when it was not checked in this session — never shown as zero.
- New bUnit test: a review open in Development is shown only for Development; Skolenærvær (checked) shows "No open review".

**Browser recheck.** Release publish of the final code, isolated :5097/:5197, the disposable `birknext_sm_20261119` database, network guard on. **20/20 passed:**
- the partly-analyzed pill border is uniform at 1440 and 390 px (computed style on all four sides), with no overflow;
- the review flag is scoped to one application;
- two applications binding one source: the active one "No open review", the unchecked one "Open reviews: see its Project Evidence", each with its own `profile=` link;
- exact `snapshots?open=SourceAnalysis:<id>` and `source-analysis?snapshot=<same id>` links;
- the Snapshot Registry and Source Analysis open that snapshot, and Back returns to the open source;
- multi-role: two confirmed roles saved, shown on the row, persisted across reload, and kept separate from detected capabilities. The original roles were restored afterwards;
- 0 blocked requests, 0 browser errors.
- The test servers were stopped afterwards. Only the processes started from the acceptance folder were stopped; nothing else was listening on 5000/5173.

**Multi-role compatibility.**
- The focused run covered Source Management, Source Context, Source Role Association, Project Evidence, Target Environment Project Pane and Snapshot Browser: 158/158 passed.
- Source Management reads the multi-role contracts (`ConfirmedRoles`, Project Evidence bindings and associations) and writes only the source-level `ConfirmedRoles` it already owned.

**Frontend.** Full suite: 9222 passed / 1 failed. The failure is `SchemaSourceSelectionTests.A_large_list_is_ranked_quickly_and_completely` (2.6 s against a 2 s timing limit under suite load). It is unrelated to Source Management and passed 3/3 when rerun alone.

**Backend.** Build guarded, memory floor 1500 MB, partitions run one at a time:

| Partition | Passed | Failed | Skipped |
| --- | --- | --- | --- |
| 1 | 1053 | 0 | 2 |
| 2 | 1101 | 0 | 5 |
| 3 | 904 | 0 | 0 |
| 4 | 682 | 0 | 1 |
| 5 | 541 | 0 | 3 |
| 6 | 2719 | 5 | 8 |

- The 5 failures in partition 6 were all 100-second request timeouts while that partition peaked at 7.2 GB used and free memory fell to 4.7 GB. The tests: AltinnTestingAuthorization, LocalSessionBootstrap, TargetEnvironmentContinuationClosure, ProjectImportPipeline, IntegrationTemplateApplyGuard.
- Rerun alone: 5/5 passed in 6 s. Their five classes in full: 21/21 passed.
- Source Management made no backend change.

**Not part of this task**
- **Dormant manual-run tables.** `manual_test_runs` and `manual_run_attachments` were not touched. Their cleanup is a separate decision.
- **Pending reviews for unchecked applications.** They are shown as unknown until that application's Project Evidence is opened in the session. This is deliberate: reading them would mean `GetAsync`, which may apply a target application.

## 8. Commits

- `dde8e080`: Source Management UX consolidation.
- `e3870c11`: open reviews per application, large-inventory check, first browser acceptance.
- Final commit: reviews scoped to their application, final verification (this report).

Starting HEAD `d1d174ea`. Not pushed.
