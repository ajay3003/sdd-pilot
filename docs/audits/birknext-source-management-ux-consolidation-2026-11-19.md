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

## 6. Not done

- **Real-browser acceptance** (Release build on isolated ports, the 1440/1280/1024/768/390 px widths, before/after screenshots): not run. The layout changes are CSS-only (flex-wrap pills, wrapped association rows). Their responsive behaviour still needs a browser check.
- **Performance at 100/500 sources:** not measured. The page logic is unchanged and runs in memory: one Project Evidence read, one source-setup read and one links read per load, with no per-source requests. The new per-application role lookup walks the profiles' bindings once per row.
- **Pending role reviews per application:** only the active environment's pending state is available (Project Evidence computes associations for one environment). Other applications show their confirmed bindings only. That is not presented as "no pending review".

## 7. Commits

See `git log` (the Source Management UX consolidation commit after `d1d174ea`).
