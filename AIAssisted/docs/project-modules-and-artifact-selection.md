# Project modules and artifact selection

One project archive (Import Project) or Demo Project folder can hold several documents for the same role. A monorepo such as a
Spec-Kit repository with one sub-project per service holds a Specification, Plan, Tasks, Data Model and Research **per module**.
BirkNext keeps every candidate, groups candidates by module, and resolves a role per module. It never picks one document for the
whole project.

```
archive / Demo Project folder
  → generic role discovery (SampleArtifactClassifier; templates excluded)        all candidates kept
  → artifact revisions (import scope) / discovered documents (Demo Project)       one candidate = one document path
  → ArtifactModules (Spec-Kit roots from the scope's paths)                      module per candidate, else Project-level
  → IArtifactExplorerContext                                                     per module: only candidate / explicit selection / none
       ├─ GetStateAsync(role)            the focused module's resolution (explorers, traceability, DQR, TCO, review context)
       ├─ GetModuleOverviewAsync()       every role × module (Import Project candidate review, Dashboard counts)
       ├─ Select(role, id, module)       selection for one module
       └─ SelectModuleAsync(module)      which module reviews read
```

## Behaviour before this change (audited at 3b9a0974)

| Question | Answer (code) |
| --- | --- |
| Does Import Project detect source and documents in one ZIP? | Yes. `IqrSourceArchiveReader.ReadDetailed(captureDocuments: true)` captures Markdown in the same validation pass; `TechnologyInventory.Detect` decides source; commit creates the snapshot (`ProjectImportController`, `ProjectImportActivation`). |
| Are all candidates retained? | Yes per role (`ProjectImportActivation` adds every detected document as a revision) — **except** byte-identical documents at different paths: `AddArtifactRevision` reused a revision by content fingerprint per role and scope, so two modules with the same file kept one candidate. Fixed: the dedupe key now includes the document path. |
| Auto-selection | A role with exactly one document project-wide was selected; several → `SelectionRequired`. Never first/latest. |
| Selection scope | One selection per role **per project** (`SddExplorerSelection { WorkspaceScope, Role, ArtifactId }`, `IsCurrentSelection` one per role and scope). A monorepo with 23 specs had to pick one spec for the whole project; every other module's spec was hidden from every review. |
| Module concept | None for SDD documents in the frontend. (Generated Documentation on the backend has its own Spec-Kit module heuristic.) |
| Consumers | Explorers, Requirements Traceability, Document Quality Review, the review context (Test Coverage & Overlap requirements) and the Dashboard/Workflow projection all read `IArtifactExplorerContext.GetStateAsync(role)`: one artifact per role. Impact Analysis reads backend requirement runs by project id, not artifact documents. |
| Templates | Excluded by the classifier (`.specify/templates`, `*-template.md`) since a84e4612. |
| Demo (Sample) Projects | Built-in folders served by the backend catalog, classified by the same discovery, resolved on demand through the same `IArtifactExplorerContext` (scope = slug, documents not copied). Same downstream consumers; selection was session-only on the project card, persisted in Explorers. |

## Module identity

`Services/Explorers/ArtifactModules` (pure):

- A folder that holds a `.specify/` or `specs/` folder is a Spec-Kit root (`RootsOf` over the paths of the scope's detected artifacts,
  every role — the same evidence in the import preview, after import and for Demo Projects; a stray `specs/` folder among unclassified
  documents does not create a module).
- A document belongs to the deepest root it lives under (`ModuleOf`); `Person/autodoc/data-model.md` is in `Person` when `Person/specs` or
  `Person/.specify` exists.
- A root at the archive or project root, or a document under no root, is **Project-level** (key `""`). No module is ever guessed from
  file names or document text.
- `FeatureOf` names the Spec-Kit feature folder (`specs/001-name`) for display. It is never authority: a higher folder number is not
  "current".

A scope whose artifacts fall into **one** group (a simple project, or a single Spec-Kit project nested in a folder) has no modules: every
page behaves exactly as before, and no module wording appears.

## Selection

| Situation | Result |
| --- | --- |
| Module has one candidate for the role | Selected automatically (`OnlyArtifact`). |
| Module has several and none chosen | `SelectionRequired` — *Needs selection*. Nothing is picked. |
| Module has several and the user chose one | Selected (`Explicit`); stored as `SddExplorerSelection { Module, SelectedAt }`. |
| Module has one Approved/Baseline candidate | Selected (`Authoritative`) — explicit repository authority only. |
| Monorepo, no module chosen to review | `SelectionRequired` with `ModuleRequired` — the user chooses the module (*Needs selection*, not an error, not ready). |
| Role absent from the focused module | `Empty` for that module — another module's artifact never stands in. |

- Selections are per (scope, module, role). Choosing for module A never changes module B.
- The focused module (`SddLifecycleState.ModuleFocus`, per scope) decides what reviews read. Changing it mirrors the module's resolution
  into the repository's current selection and session artifacts (older consumers such as `workspace.Specification`) and, for Demo
  Projects, into the discovery choice — so every page reads the same module.
- Selections saved before module scoping have no `Module`; they are read as project-level and still apply to projects without modules.
- Both lists are part of the lifecycle JSON saved with the workspace (`sddLifecycleJson`): no schema change, no migration. They survive
  navigation, reload and reopening a saved workspace.
- Selecting never changes authority, baselines, the source snapshot or the import. Selections reference artifact ids
  (`workspace:<archive path>` within `import:<importId>`), never bare file names.

## Consumers

| Consumer | Reads |
| --- | --- |
| Specification / Constitution / Plan / Task / Data Model Explorers | Focused module. `ArtifactExplorerHost` shows a *Module* selector, a *Choose a module* state and *Project · Module · File* in the header. |
| Requirements Traceability | Focused module's Constitution, Specification, Plan, Tasks; the result `Key` includes the module, so a Person result is never shown as Frontend's. |
| Document Quality Review | Focused module's selected artifacts; the context strip names the module; a module change clears the shown result. |
| Test Coverage & Overlap | Requirements from the focused module's selected Specification (via the review context). The request and the stored result carry `ArtifactScope` ("Module Person") and `SpecificationArtifacts` (id, path, module, fingerprint), so a historical result stays bound to what it reviewed. No module → no requirements, never a template or the first spec. |
| Dashboard | Per role: "23 detected across 11 modules · 2 selected · 9 need selection"; the workspace panel names the module under review. |
| Recommended Workflow / Project inputs | *Needs selection: choose the module to review*, then *Needs selection: choose the … to review*. |
| Impact Analysis | Unchanged: backend requirement runs by project id (Project Import does not write that store). |

## Project-wide aggregate

`GetModuleOverviewAsync` is the project-wide view: every candidate of every role with each module's selection (Import Project
*Artifact candidates*, Dashboard counts). It is labelled per module and is never a single authoritative document. Reviews run on one
module at a time; there is no merged pseudo-specification.

## Demo Projects

*Sample Projects* is shown as **Demo Projects** (`/demo-projects`; `/sample-projects` still works). They are optional built-in examples.
After opening one, the same `IArtifactExplorerContext`, module grouping, selection model and consumers apply; only the input differs
(a backend folder instead of an uploaded ZIP, documents resolved on demand instead of revisions).

## Limits

- Module identity uses Spec-Kit folders only. Solution/project boundaries are not used yet; a monorepo without `.specify/`/`specs/`
  folders is one project-level group.
- Reviews run one module at a time; there is no "review every module" batch.
- Classification still runs in the browser for previews without backend discovery (see [project-import.md](project-import.md)).
