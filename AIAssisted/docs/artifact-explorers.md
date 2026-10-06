# Document Explorers and the workspace artifact repository

The Constitution, Specification, Plan, Task and Data Model Explorers read artifacts by **role** from the current workspace.
They do not depend on a Sample Project being selected or on file names such as `spec.md` or `constitution.md`.

```
Artifact source (Sample Project discovery, file import, drag and drop, paste, restored workspace)
    → workspace artifact repository (role, file name, revision, fingerprint, authority, scope)
    → IArtifactExplorerContext (artifacts of one role in the current scope; preferred artifact)
    → ArtifactExplorerHost (loading / error / empty / choose / loaded + selector)
    → explorer content (shared Markdown engine + domain extractor, no filename routing)
```

## Ownership

| Concern | Owner |
|---|---|
| Artifacts of a role in the current workspace, preferred artifact, selection, import | `Services/Explorers/ArtifactExplorerContext` (`IArtifactExplorerContext`, singleton) |
| Imported content, revisions, authority, explorer selection, persistence | `WorkspaceArtifactRepository.SddLifecycle` (saved with the workspace as `sddLifecycleJson`) |
| Sample Project documents by role | `ISampleProjectArtifactDiscovery` (see [sample-project-discovery.md](sample-project-discovery.md)) |
| Shared states, artifact context bar, selector, import panel | `Components/Explorers/ArtifactExplorerHost`, `ArtifactImportPanel`, `ExplorerAnalysisError` |
| Role wording (labels, purpose, example file names for help text only) | `Services/Explorers/ArtifactExplorerRoles` |

## Workspace scope

The scope is the selected Sample Project slug, or *manual workspace* when no project is selected.

- A selected project's detected documents are resolved on demand and never copied (identity-only selection is unchanged).
- An import is an `SddArtifactRevision` with `WorkspaceScope` (project slug or null) and `Origin` (`File`, `Drop`, `Paste`).
  Imports never appear in another scope, so a project's imports cannot leak into another project or into the manual
  workspace.
- In the manual workspace an import is also the role's session artifact (`Workspace.Get(role)`). Existing consumers of
  the manual workspace therefore see it, and it is restored with the workspace. Legacy `Workspace.Set` content belongs to
  the manual workspace.

Revisions with the same file name in one scope and role are one document's history. The document shows its current
selection, or else its newest revision that is not superseded. Different file names are different artifacts.

## Preferred artifact

1. **Explicit**: the Explorer selection (`SddLifecycleState.ExplorerSelections`, per scope and role, persisted), the choice
   on Sample Projects, or the repository's current selection for the role (set by an import or by selecting).
2. **Authoritative**: the only artifact whose authority is Approved or Baseline.
3. **Only artifact**.
4. Otherwise the user must choose (*Choose a … artifact*).

The Explorer never picks the first file, the latest file or a merge of several. Selecting is not authority: approval and
baselines are unchanged. Importing starts at authority *Unknown* and never creates a baseline. Choosing a Sample Project
document in an Explorer also sets the discovery choice, so pages that use `SampleProjectDocumentResolver` open the same
document in that session.

## States

| State | Meaning |
|---|---|
| Loading | Artifacts are being read. Never shown as empty. |
| Error | The project's documents could not be read. Not "no artifact". |
| Project unavailable | The persisted project slug is no longer in the catalog. |
| Empty | No artifact of the role in this scope. The wording differs for "no project" and "project without this role". |
| Choose | Several artifacts and nothing decides between them. |
| Loaded | Context bar: title, file name, path, source, revision, authority, Historical/Superseded, why it was opened. A selector appears when there are several. |
| Analysis failed | The artifact is loaded but parsing failed (`ExplorerAnalysisError`). Not the empty state. |

Explorers refresh on repository events (artifact change, selection, reset) and on project selection change, with no
polling. Reset Local Data clears the lifecycle, so every Explorer shows the empty state.

## Import safety

Imports keep the importer's limits: `.md`/`.txt` only (file picker and drop), 1 MB, no NUL/binary content, BOM and
line endings normalised. Pasted text has the same size and binary checks. The file name is reduced to a display name
(last path segment, no control or path characters, 200 characters at most). When the classifier strongly detects
another role, the import keeps the chosen role and shows a warning.

## Known limitations

- In a Sample Project scope, an imported artifact is visible to the Explorers only. Quality Review and traceability
  pages still resolve that project's documents through `SampleProjectDocumentResolver`.
- There is no revision-history browser in the Explorers. They show the revision number and count, and authority is
  changed on Implementation Review.
- Imports cannot be removed from an Explorer. Reset Local Data clears them.
- Research artifacts are kept and classified, but there is no Research Explorer.
