# Specification Explorer — project scope report (2026-11-04)

Branch `008-traceability-first`. Nothing was pushed. Only frontend code changed: the Explorer document model lives in the client workspace.

## SPECIFICATION EXPLORER OWNERSHIP & SCOPE AUDIT MAP

**Column key**

| Short name | Meaning |
|---|---|
| Owner | Current owner of the component |
| Scope semantics | What scope the component works in today |
| Proj/src | Whether it is project-level or source-level |
| Data source | Where its data comes from |
| Problem / risk | The issue found |
| Decision | Keep / extend / migrate |

| Component | Owner | Scope semantics | Proj/src | Data source | Reusable | Duplicate logic | Problem / risk | Decision |
|---|---|---|---|---|---|---|---|---|
| `SpecificationExplorer.razor` | page | renders the host's selected artifact | — | `ArtifactExplorerHost` | — | no | none (content only) | keep |
| `ArtifactExplorerHost.razor` (shared frame of every document Explorer) | Explorers | one **Source** dropdown mixing "*Project* — aggregated project evidence" with each import | mixed | `IArtifactExplorerContext` | yes | no | the fake source; "one at a time" copy; "Open X", "Import … only" and "Review project artifacts" CTAs on source-scoped empties | **migrated**: View Project / Single source, new empty states and CTAs |
| `ArtifactExplorerContext.EvidenceScopes` | Explorer context | project view = documentation-bound imports; the main import's own documents only when it is bound as documentation | project | workspace lifecycle revisions | yes | no | **root cause of the false empty** (below) | **extended**: project corpus by association |
| `ExplorerSources.ProjectEvidence` (`"project-evidence"`) | context | saved as if it were a source id | — | `SddLifecycle.ExplorerSource` | — | — | conflates mode and source | legacy value read as Project view, never saved again |
| `ProjectEvidenceResolver.DocumentationImports / MainImport` | Project Evidence | documentation role / main package | project | Target Environment Project | yes | no | — | reused |
| Spec detection, parsing, Requirements / Scenarios / AC | `SampleArtifactClassifier`, spec parsers, `SpecExplorerPanel` | per document | — | canonical document analysis | yes | no | — | unchanged (no new parser or classifier) |
| Module / feature discovery | `ArtifactModules` (StrongRoot / ModuleOf / FeatureOf) | per path | — | paths | yes | no | — | reused for corpus module counts |
| Project Evidence strip (`ProjectContextStrip`) | shared | "Evidence 1 / 2 ready" | project | Project Evidence view | yes | no | compressed; read as spec readiness | **reworded**: "1 of 2 required evidence roles ready", tooltip says it is not document availability, link unchanged |
| Saved source state | `SddLifecycle.ExplorerSource` | import id or "project-evidence" | — | workspace | — | — | mode/source conflated; a removed source kept | import id only; removed source → Project view + notice |
| Other Explorers (Constitution, Data Model, Plan, Task, Document Quality, Documentation Drift, Generated docs) | same host + context | same source dropdown | mixed | same | — | — | **same pattern** (shared state) | fixed **with** this change: the control and corpus are shared, so a Specification-only fix would have diverged |
| Status vocabulary / badges | shared | — | — | — | yes | — | no "No evidence" badge for "this source has none" | unchanged |

## Answers

1. **Current model (before).**
   - One shared Explorer source per workspace: either "project evidence" (the Target Environment Project's documentation-bound imports) or one import.
2. **Why the Source selector was ambiguous.**
   - It listed the project itself ("M2LB — aggregated project evidence") next to packages.
   - So *mode* and *source* were one control, and the saved value `project-evidence` posed as a source id.
3. **Aggregated behaviour.**
   - The aggregated view read only the documentation-bound imports.
   - The main package counted only if it was itself bound as documentation.
4. **Root cause of the false empty** (`EvidenceScopes`).
   - The main package was flagged `ManualOnly` unless it was documentation-bound.
   - Reproduced on the throwaway DB: documentation bound to Terraform-iac-m2lb + M2LB.Common. The 40 specifications in `M2LB.zip` were invisible, giving "No Specification artifacts found across 2 project evidence sources".
5. **Evidence vs corpus.**
   - Evidence readiness stays in the context strip ("N of M required evidence roles ready", links to Project Evidence).
   - Specification availability is the corpus summary.
   - Neither is derived from the other.
6. **Canonical data reused.**
   - Workspace artifact revisions and their classification (role).
   - `ArtifactModules` module/feature rules.
   - `ProjectEvidenceResolver` bindings.
   - Import records (application id).
   - Existing artifact provenance (`EvidenceImportId`).
7. **Corpus query.**
   - `IArtifactExplorerContext.GetCorpusSummary(role)` returns `ExplorerCorpusSummary`: artifacts, occurrences, modules, conflicts, contributing sources, and per-source `ExplorerSourceCount` with its association.
   - Membership is resolved in the shared context, never in a page, using association precedence (`ExplorerSourceAssociation`): Documentation (reviewed binding) > MainProject > ProjectRole (bound to another project role) > SameApplication (automatic).
   - It is computed from the revision manifest only; no document body is parsed.
   - *Deviation:* this is not a backend endpoint. The Explorer documents live in the client workspace lifecycle, not in a backend document store, so a backend query would need a second store. The brief forbids that.
8. **Project view** (default): "M2LB · 13 modules · 40 Specification artifacts · 1 contributing source".
   - **Sources** (expandable provenance) lists each source's count and why it belongs.
   - **Notices:**
     - "Some specifications come from imported sources not currently marked as project documentation evidence. Review source associations" (deep link to the documentation role);
     - "Needs review: N … differ between sources at the same module and path", for conflicts.
9. **Single source view.**
   - The source selector appears only here, with per-source counts ("M2LB.Common — 0 Specification artifacts").
   - The header reads "Viewing single source: X · N Specification artifacts" and says when the source is not part of the project.
10. **Selector content.**
    - Imports grouped "Project sources" (associated) first, then by application or "Other imported sources".
    - The project is never an option.
11. **Modules** come from the existing module overview over the project corpus. Card counts read "N features · M Specification artifacts".
12. **Counting:** one counting word, "Specification artifact(s)". "documents" was replaced in module cards and the artifact selector.
13. **Multi-source provenance:** per artifact (existing `EvidenceSource`) and per corpus (Sources list).
14. **Duplicates:** the same module + path with identical content in several sources counts as one item with N occurrences. Rows stay selectable per occurrence; nothing is merged.
15. **Conflicts:** the same module + path with different content is counted and flagged "Needs review". It is never resolved by source order.
16. **Evidence header:** reworded as in item 5.
17. **Environment:** unchanged; separate from the view mode.
18. **Single-source empty state** (neutral, `data-state=NotInSource`):
    - "No specifications in M2LB.Common".
    - "This source contains no recognized Specification artifacts. It is a project documentation evidence of M2LB."
    - "Project M2LB has 40 Specification artifacts available in other source."
    - Actions: **View project specifications** and **Review source classification**.
    - Parser detail is under **Why?**
19. **True project empty** (`NotInProject`): "No specifications found for this project", only when no associated source holds any. Actions: Review project evidence, Import source, Import specification only.
20. **CTAs:**
    - "Open X" became "Inspect source X".
    - "Import specification only" is hidden whenever the project has specifications.
    - "Review project artifacts" became "Review source classification".
21. **Navigation and persistence.**
    - Mode and source are separate: Project = no stored source; Single = an import id.
    - Kept across Explorers within the session.
    - A full reload keeps it when the workspace autosaves (unchanged mechanism; the throwaway workspace sent no save).
    - Deep links: `?view=project` and `?source=<import id>`.
22. **Importing another package** never switches the view or source.
    - An infrastructure package without specifications leaves the corpus unchanged (test).
    - A new associated spec source joins automatically.
    - A removed single source falls back to Project view with a notice; no stale id is kept.
    - A saved workspace still restoring (no imports recorded yet) is not treated as "removed". This race was found in browser acceptance and fixed.
23. **Loading and error:** existing "Loading … artifacts" and error card unchanged. Empty states render only once the state has loaded.
24. **API / payload:** no backend change; new context members only (`ViewMode`, `GetCorpusSummary`, `ExplorerSourceAssociation`).
25. **Performance:** manifest-only grouping per render (no body loads or parsing).
26. **Reusable scope component:** the shared host already is the reusable Explorer scope frame. No separate component was needed.
27. **Other Explorers:** they share the host and context, so all of them now have Project / Single source and the project corpus. They were not individually redesigned.
28. **Tests.**
    - `ProjectEvidenceBindingTests`, new:
      - Case A/D: main package specs shown while documentation is bound elsewhere, plus the association notice;
      - Case E: an infrastructure import changes nothing and does not switch the view;
      - Case H/I: duplicates and conflicts.
    - `TargetEnvironmentProjectPaneTests`: true project-empty (`NotInProject`); single-source empty (Case B/C) with no import CTA and a working "View project specifications".
    - `ExplorerSourceSelectionTests`: the source list appears only in Single source view with no fake source; the choice is shared across Explorers; a removed source falls back to Project with no stale id.
    - Updated deliberately (old semantics):
      - main package now in `Evidence.Sources`;
      - project source count includes the main package;
      - "2 Task artifacts" wording.
    - **Frontend:** 8002/8002 passed. One load-dependent flake each (SchemaStudio regex timeout, ActivityCenter timing) passed in isolation and in the final full run.
29. **Browser** (Edge, isolated `:5199` → `:5099`, throwaway `birknext_ci2`, 1440/390 × light/dark, **0 external requests**, no page errors, no horizontal overflow):
    - Project view default: 13 modules · 40 Specification artifacts, association notice, module list. The old false-empty case is gone.
    - "aggregated project evidence" and "one at a time" are absent.
    - M2LB.Common shows a neutral empty state with "View project specifications" and "Review source classification", and no import CTA.
    - Single source M2LB: "Viewing single source: M2LB · 40 Specification artifacts".
    - Mode kept when moving to Task Explorer and back.
    - "View project specifications" returns to Project view.
    - Screenshots are in the session scratchpad, `se-shots/`.
30. **Commits:** see `git log` (spec-explorer commits after `96cced6a`).

## Confirmations

- **Project-centric:** Specification Explorer is project-centric by default. Project and Single source are explicit, separate modes. The aggregated project is no longer presented as an imported source.
- **Canonical data only:** the corpus uses the existing classified artifacts. No new parser or classifier was added. User Stories, Scenarios and Acceptance Criteria remain the existing canonical data.
- **Readiness vs availability:** Project Evidence readiness is separate from Specification availability. Project view no longer becomes empty because documentation associations are incomplete while valid specifications exist.
- **Single-source states:** infrastructure and shared-code sources without specifications read as normal. Single-source empty states say the source has none and offer "View project specifications".
- **CTAs:** "Open M2LB …" was replaced by intent wording. "Import specification only" is not shown when the project already has specifications.
- **Counts and provenance:** module and artifact counts use the project scope. Provenance stays available, per artifact and in the Sources list.
- **Imports and state:** importing a non-spec package neither empties nor switches the view. Source state and project scope are separate. No duplicate source of truth was introduced.
- **Standing constraints:** no real external traffic; `appsettings.Local.json` not intentionally modified; user Test Data preserved; nothing pushed.
