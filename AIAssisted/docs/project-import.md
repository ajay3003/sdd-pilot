# Project Import — one archive, two evidence domains

Import Project (`/project-import`) takes one project ZIP and populates both project inputs from it:

```
Import Project (one upload)
  └─ POST api/project-import/preview      validate once (IqrSourceArchiveReader, captureDocuments) → stage in memory
       ├─ documents  → frontend: generic role discovery → Shared Artifact Repository (scope import:{importId})
       └─ source     → POST api/project-import/{stagingId}/commit → Source Analysis snapshot (IqrSourceStore, insert-only)
```

One import is **not** one evidence model. The artifact repository keeps owning roles, selection, artifact fingerprints and
authority; Source Analysis keeps owning snapshot identity, currentness, evidence and analyzers. They share only provenance.

## Before this change

| Path | UI | Reader | Persistence |
| --- | --- | --- | --- |
| Sample Projects | `/sample-projects` | `SampleProjectDocumentInventory` (disk folders under `SampleData/`, never ZIP) | identity only (slug), documents resolved on demand |
| Manual documents | explorer Import (file/drop/paste) | `ArtifactExplorerContext.Import` | `SddArtifactRevision` in the workspace lifecycle (auto-save) |
| Source Analysis | `/source-analysis` upload | `IqrSourceArchiveReader.ReadDetailed` (in memory, never on disk) | `iqr_source_snapshots` row, per Target Environment |

Sample Projects never read a ZIP, so the same project was "loaded" from a folder or pasted documents and uploaded again as a
ZIP for Source Analysis. The raw archive was never persisted by either path (only derived evidence), so no stored copy was
duplicated — the duplication was the user's second upload.

## Contracts (`shared/ProjectImportContracts.cs`)

- **Import identity** — `ImportId = "import-" + sha256[..16]` of the exact uploaded bytes. The same archive always has the same
  identity whatever its file name; the file name is display metadata only (last path segment, never a client path).
- `ProjectImportProvenance { ImportId, ArchiveFileName, ArchiveSha256, ImportedAt }` is stamped on the source snapshot
  (`IqrSourceSnapshot.ProjectImport`, null for a standalone upload) and recorded on the frontend
  (`SddProjectImportRecord`, `SddArtifactRevision.ProjectImportId`).
- `ProjectImportPreview` — staging id, archive (name, sha, size, entries), project name + basis, readable Markdown documents with
  content, skipped documents with reason, source detection, other-file count, expiry. A preview persists nothing.
- `ProjectImportCommitResult.Source.State` — `NotDetected` · `Created` · `Reused` · `NotCreated` (Source Analysis turned off) ·
  `Failed`. `CanRetry` says the staging was kept so a genuine failure can be retried without a new upload. The result carries no
  environment: a source snapshot does not belong to a Target Environment.

## Backend

- **One validation path.** `SourceArchiveUpload.ReadAsync` reads the multipart stream (both endpoints), then
  `IqrSourceArchiveReader.ReadDetailed(name, bytes, captureDocuments: true)` validates and, in the same pass, captures Markdown
  documents with the Sample Project document rules (`.md`/`.markdown`, 1 MB each, no NUL, Sample Project ignored folders) and a 2,000-document cap (a project archive is often a monorepo; the real M2LB archive has 640 candidates).
  All limits are unchanged: 50 MB compressed, 100 MB expanded, 20,000 entries, 2 MB per read entry, traversal / absolute / UNC /
  drive / symlink / duplicate-path / encrypted rejections. Without `captureDocuments` the reader's output is byte-for-byte what it
  was (test `DocumentCapture_UsesTheSameValidationPass_AndLeavesSourceAnalysisOutputUnchanged`).
- **Source detection** (preview) — `TechnologyInventory.Detect(workspace)` on the path inventory and in-memory project files:
  source = C#/project/SQL/Dockerfile files, IaC/contracts evidence files, or any Language / Dependency / Pipeline / Contract
  technology. JSON/YAML configuration alone, scripts and documents are not source.
- **Staging** — `ProjectImportStagingStore` (singleton): in memory only, a random `StagingId` per upload (concurrent imports of
  the same archive never share state), at most 4 entries (oldest evicted), 30-minute expiry, a per-staging gate so double commits
  cannot create two snapshots, cleared by Local Data Reset.
- **Commit** — uses the staged bytes and validated workspace (never a second copy):
  - no source → `NotDetected`, staging released;
  - Source Analysis turned off → `NotCreated` (`SOURCE_ANALYSIS_DISABLED`);
  - the workspace's current (newest) Source Analysis snapshot has the same `ImportId` and sha → `Reused` (no duplicate analysis);
    whether a Target Environment is selected changes neither the import identity nor this decision;
  - otherwise `IqrSourceStore.AnalyzeValidatedAsync(..., projectImport)` inserts a new immutable snapshot → `Created`;
  - analysis or save failure → `Failed` with the Source Analysis code, no snapshot, staging kept, retryable.
- No Target Environment is needed (see *Source snapshots and Target Environments* below).
- Endpoints: `POST api/project-import/preview`, `POST api/project-import/{stagingId}/commit` (an `environmentId` sent by an older
  client is ignored),
  `DELETE api/project-import/{stagingId}`. Failures use the Source Analysis upload body `{ code, stage, message, entryPath,
  actual, limit }`; an expired staging is 404 `IMPORT_STAGING_EXPIRED`.
- Auto-save no longer answers 204 for a workspace whose lifecycle has `CurrentProjectImportId` (a source-only import has no
  artifacts and no Sample Project name but is still the current project).

## Frontend

- `ProjectImportArtifactDiscovery.From(preview)` — the generic `ArtifactDocumentDiscovery` (shared with Sample Projects:
  `SampleArtifactClassifier`, duplicate fingerprints, role grouping). No project-specific names.
- `ProjectImportActivation.Activate` (only after a commit result exists — the workspace never holds half an import):
  - clears a selected Sample Project (one current project), upserts the `SddProjectImportRecord`, sets `CurrentProjectImportId`;
  - adds every detected document as a revision in scope `import:{importId}`, origin `ProjectImport`, file name = archive-relative
    path (two `spec.md` in different folders stay two documents), authority `Unknown`;
  - selects a role with exactly one document; a role with several is imported without a selection (`SelectionRequired`);
  - `RefreshSessionArtifacts` rebuilds session artifacts from the import scope only, so no role of a previous import or of the
    manual workspace stays behind.
- Scope precedence (`ArtifactExplorerContext.CurrentScope`): selected Sample Project slug → current import scope → manual (null).
  Selecting a Sample Project clears the current import; `Close imported project` returns to the manual workspace. Manual and
  earlier-import revisions stay in the lifecycle as history.
- Restore: `MainLayout` and `WorkspaceSessionRestoreService` restore a workspace whose lifecycle has a current import even with
  no artifacts and no project name. `CaptureRevision` captures into the session scope, so restoring never duplicates import
  documents into the manual workspace.
- Pages: Import Project (choose → preview → import → result, current imported project card), Source Analysis (`Created by`,
  import provenance link, Import Project as the recommended action, `Upload source only` kept), Sample Projects (current
  imported project banner), Dashboard (`Imported project archive`), Recommended Workflow and project-input cards (Import Project
  as the load action), Quality Review (imported project name).

## Semantics

| Archive | Documents | Source | Outcome |
| --- | --- | --- | --- |
| docs + source | roles detected | snapshot created | Imported |
| docs only | roles detected | No source detected (neutral) | Imported |
| source only | No supported project artifacts detected (neutral) | snapshot created | Imported |
| two candidate specs | both imported, choose one | unaffected | Imported with notes |
| unsupported language | — | snapshot created, `Partial`/`Failed` coverage, technology marked "not analyzed" | Imported with notes |
| no Target Environment | imported | snapshot created (target shown as *Not configured — required only for runtime reviews*) | Imported |
| nothing supported | — | — | Nothing to import (neutral, Import disabled) |
| unsafe / invalid | — | — | Archive rejected (nothing staged or activated) |

Imported never means reviewed, approved or passed.

## Source snapshots and Target Environments

Source evidence is not runtime evidence. Before this change Source Analysis snapshots were stored and listed per Target
Environment (`iqr_source_snapshots.EnvironmentId`, filtered by `IqrSourceStore.ListSourceAnalysisAsync/FindSourceAnalysisAsync`),
so the upload endpoint and Project Import refused source without an active target (`NO_ACTIVE_ENVIRONMENT`) and every
source-only page (Source Analysis, Technology Coverage, Pipeline Review, Dependency Review) hid behind a "select a target" state.
There was no foreign key and no analyzer that needed the target; it was only the storage key.

- **Ownership.** Source Analysis owns immutable source snapshots; Project Import may trigger one; a Target Environment never owns,
  scopes or selects one.
- **Current snapshot.** `ListSourceAnalysisAsync()` / `FindSourceAnalysisAsync(id)` read the workspace's Source Analysis snapshots
  whatever target is selected; the newest is current (per repository where reviews group by repository).
- **`EnvironmentId`.** New Source Analysis snapshots are stored with an empty `EnvironmentId` (`IqrSourceStore.NoEnvironment`).
  Rows stored by earlier versions keep the target id they had; it no longer scopes anything, so they stay readable and in
  history. No schema change and no migration. Legacy per-integration IQR snapshots keep their environment scope.
- **Target later / switch / delete.** Configuring a target later, switching targets or deleting one (a client-side profile)
  never re-creates, hides or changes the current snapshot; runtime reviews then use the same snapshot and the target.
- **Endpoints.** `api/source-analysis` (list, upload, scope, infrastructure suggestions), `api/technology-coverage`,
  `api/pipeline-review/*` and `api/dependency-review/source-scope` accept a blank `environmentId`. Runtime endpoints keep
  requiring a target. Azure Environment Analysis (observed runtime evidence) and Security Classification review runs and test
  context stay per target; their source evidence is on the snapshot without one.

## Real-archive pilot (M2LB, 4.7 MB, 2,165 entries)

- Backend preview: ~0.2–0.4 s (validation, document capture, source detection). 640 Markdown candidates; source detected
  (980 files; C#, SQL, ASP.NET Core, Blazor WebAssembly, Service Bus, Event Hubs, SQL Server, NuGet, containers, xUnit; JavaScript,
  Python, browser frontend and TRX reported as *not analyzed*).
- It is a monorepo of ~20 Spec-Kit projects, so every role has 18–35 candidates: all are imported, none is selected, and each
  explorer asks the user to choose (never the first or latest). 50 documents need review (two roles fit), 421 are other documents.
- Commit (full Source Analysis): ~96 s; snapshot `Partial` coverage; Source Analysis shows it as current with the same import id.
- Name derived from the archive file name (`M2LB (2).zip` → `M2LB`); nothing about M2LB is hardcoded.

### Browser classification cost

Role discovery runs the shared `SampleArtifactClassifier` (five full extractors per document) in WebAssembly. For M2LB's 640
documents it took 312 s in a Release build. Two behavior-preserving fixes (both also speed up Sample Projects):

- `PlanAnalysisService` built 32 testing-framework regexes per call — more than the 15-entry static Regex cache, so every call
  re-parsed them and evicted every other cached pattern — plus one `new Regex(..., Compiled)` per inline-metadata line. Both are
  now built once. JIT: Plan extractor 5.2 s → 1.9 s, whole classification 7.5 s → 4.4 s over the 640 documents.
- Identical inputs (normalized text + the path parts the classifier reads, `SampleArtifactClassifier.PathKey`) are classified once
  (M2LB: 640 → 437 classifications). Test: `ArtifactDocumentDiscoveryTests`.

Result: 167 s in the Release browser build. Classification yields every 8 documents and the page shows
"Detecting document roles: n of N" with a progress bar. Still slow for very large monorepos (follow-up: classify on the backend
or trim extractor work).

## Limits and follow-ups

- Staging and the 30-minute retry remain only for genuine source failures (analysis or save); a missing target is not one.
- Staging is in memory: a backend restart between preview and commit loses it (the page asks for the ZIP again).
- Requirements Traceability (`/artifact-traceability`) reads the current workspace through the explorers' role authority
  (`TraceabilityInputs` over `IArtifactExplorerContext`): Sample Project, imported project and manual workspace alike. A role with
  several candidates is shown as *Needs selection* with a link to its explorer and is never picked; no documents is a neutral
  *not assessed* state; a result is bound to the scope and the selected artifacts' ids and content fingerprints and is rebuilt
  when any of them changes.
- There is no import history page; the lifecycle keeps one record per import identity.
