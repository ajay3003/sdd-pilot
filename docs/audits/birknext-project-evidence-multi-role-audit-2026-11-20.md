# BirkNext — Project Evidence: multi-role source associations (2026-11-20)

Observed: System Settings → Target Environments → Project → Project Evidence for Target Application *Tiltak 2 – Sikkert digitalt økosystem*
with two imports, M2LB (Documentation, App source, Contracts, Generated docs; "Related application/system source", "Confirmed by user") and
Tiltak 2 (App source, Contracts; "Primary application source", "Associated automatically"); Application source: Tiltak 2, Partial; Project
documentation: M2LB, Ready; one review decision outstanding. The Application source could not be changed from the page, and the Project list
showed only Tiltak 2.

## 1. Current architecture (audited, reused)

| Concept | Where | Notes |
|---|---|---|
| Imported source + identity | `SddProjectImportRecord` (workspace lifecycle, saved server-side) | `ImportId`, `ArchiveSha256`, `SourceSnapshotId`, `ApplicationId` |
| Snapshot | Source Analysis snapshot (`iqr_source_snapshots`) | immutable; referenced, never copied |
| Detected capabilities | `ProjectEvidencePackages` (from Source Analysis setup + documents) | several per source |
| Project (Project list entry) | `SddApplicationContext` (workspace application) | packages grouped by `ApplicationId` |
| Target Application | `TargetApplicationIdentity` on a Target Environment profile | |
| Per-target role decisions | `EnvironmentProject.Evidence[*].Sources` on the Target Environment profile | `ProjectEvidenceSourceReference` (import, snapshot, `BoundBy` provenance) |
| Source-level role suggestion | `SddProjectImportRecord.ConfirmedRoles` (Source Management) | global; feeds automatic association only |
| Required/optional, cardinality | `EnvironmentProjectEvidenceBinding` | |

No new registry, table or API was added: the per-target bindings already were the many-to-many association (source × role × Target
Environment), and they are what every consumer (explorers, reviews, Test Pack satisfaction) reads.

## 2. Root causes

1. **Roles shown were not the per-target decision.** The associated table's "Role for {application}" came from the source's global
   classification (`ApplicationContexts.Packages`: first confirmed role, or detected role), not from the Target Environment's bindings. One
   role per source was shown, and a change made for one application could look like it changed another.
2. **No role control where the roles are shown.** The associated table offered only View evidence and Remove; role changes lived in a
   collapsed "Evidence roles, requirements and diagnostics" section (per-role pickers), and the Needs review "Associate" button only opened a
   role picker there.
3. **Application source was exclusive.** The default role had `Cardinality.Single`; binding another application source cleared the first.
   Automatic association refused to bind several candidates. Complex applications (M2LB: several repositories/packages) could not record
   contributing application sources.
4. **The Project list shows workspace applications.** M2LB was imported as "more evidence" of Tiltak 2 (`ApplicationId` = Tiltak 2), so the
   Project list showed one entry. There was no way to move an import to its own project without re-importing.

## 3. Changes

* **Sources table (Project Evidence):** columns *Detected capabilities* · *Assigned roles for {application}* · *Review status* · *Evidence
  completeness* — four separate facts, never one "Ready". Roles come from this Target Environment's bindings (`SourceRoleAssignments`);
  several per source; the primary application source is marked. A confirmed role with partial evidence shows "Confirmed by you" and
  "Partial — contracts and schemas incomplete" side by side.
* **Edit roles** on every row: a compact checklist of the project's roles; roles whose capability the source does not contain are shown with
  the reason and cannot be chosen; "Primary application source" when Application source is checked; Save roles / Cancel; the impact line says
  only this application in this environment changes.
* **`SetSourceRolesAsync`:** one atomic save per source (applied to a copy, all or nothing), backend-independent validation of role keys and
  capabilities, removed automatic roles remembered as declines (not re-added), explicit choice reverses an earlier decline.
* **Application source = one primary + contributing:** default role is `Multiple`; primary = `EnvironmentProject.ProjectImportId`; saved
  single-source evidence is upgraded on read (`ProjectEvidenceRoles.Upgrade`, only the default role; named expected evidence keeps its
  cardinality); `BoundSnapshot` returns the primary's snapshot so consumers stay deterministic. Removing the primary's application role
  promotes the only remaining application source, otherwise leaves the primary to be chosen.
* **Needs review** is shown above the table (no longer inside the collapsed diagnostics); its action opens Edit roles for exactly that source,
  prefilled with the roles its content suggests.
* **Belongs to project** (Source Management, per import): move an import to its own new project or another one. Only `ApplicationId` changes;
  the import, snapshot, documents, plans and every Target Environment's bindings stay; an emptied project is removed; duplicate names are
  refused.

Non-destructive by construction: no change deletes imports, snapshots, Source Analysis, saved test cases or requirement history, and no change
touches another Target Environment.

## 4. Specification discovery and Manual Testing

Manual Testing and the explorers read the active Target Environment's documentation bindings plus the imports of the current project
(`ArtifactExplorerContext.ProjectAssociations`). With roles editable, binding M2LB as *Project documentation* for a Target Application makes
its specifications selectable there; without any specification in the project, Plan & prepare now says *Not identified in source* instead of
pointing to the Specification Explorer (commit `121d3c06`). For the observed workspace: the Tiltak 2 import contains only README files; M2LB
is the documentation source. Making M2LB its own project (Belongs to project) gives it its own Project list entry and its own specifications
scope.

## 5. Tests

`ProjectEvidenceMultiRoleTests` (new): several roles per source, primary + contributing application sources, completeness vs review status,
unsupported roles never offered, upgrade once, per-target isolation (changing Tiltak 2's roles leaves M2LB's), Belongs to project (own
project, refusal of duplicates, emptied project removed, nothing re-imported). Updated: `ProjectEvidenceBindingTests` (conflict rule kept for
single-source roles), `TargetEnvironmentProjectPaneTests` (row shows per-target roles, primary marked).

## 6. Limitations

* Role decisions are stored on the Target Environment profile (browser-side settings, as before); two browser tabs editing the same
  environment at once still last-write-wins (no optimistic version on the profile).
* Explicit override of a role the source's content does not show (with a rationale) is not offered: such a role is refused with the reason.
* Real-browser acceptance at 1440/1280/1024/768/390 of the new table and editor was not run in this pass; bUnit covers the markup.
* The source-level `ConfirmedRoles` (Source Management) remains a global suggestion for automatic association; Project Evidence shows the
  per-target decision.

Commits: `eb3defef` (Project Evidence + Belongs to project), `121d3c06` / `baa6d118` (Not identified in source; guide scope).
