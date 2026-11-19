# Multi-role source associations and Snapshot consistency — 2026-11-19

Branch `008-traceability-first`. Local only; not pushed.

## Audit findings

**Where role data lives.** Three stores held role data, independently:

| Store | Location | What it records |
| --- | --- | --- |
| (a) Workspace lifecycle | `SddProjectImportRecord.ApplicationId` + `ConfirmedRoles`, in the workspace's SDD lifecycle | Which application a package belongs to and the roles confirmed in Source Management |
| (b) Target Environment Project Evidence | Bindings in `FrontendAnalysisProfile.Project.Evidence[].Sources[]`, in browser settings | The roles of a source for a Target Application; **contextual per application** |
| (c) Git link roles | `GitSourceLink.Roles` | The roles chosen for a linked Git repository |

"Suggested" and "Needs review" associations are computed on every read (`ProjectEvidenceAssociation.Of`) and never persisted.

**Defects:**

1. **Extra confirmed roles were dropped.** `ApplicationContexts.ConfirmedRole` reduced several confirmed roles to the first, so every other role
   was silently ignored downstream, although the Source Management roles UI said "several allowed".
2. **Single-select editor.** The Project Evidence association editor allowed one role per source, and listed only sources that were not yet
   associated, so a second role could not be added.
3. **Snapshots showed "Unassigned" for suggested sources.** The Snapshots page reads only persisted bindings (b), so a source that Project
   Evidence suggests read as "Unassigned". It never says "suggested".
4. **A confirmed related application source was never persisted.** Its category is `IntegrationSource`, and `Fits` returned false for it.
   Project Evidence showed it as associated, while Snapshots, Schema Studio and the review selectors did not see it.
5. **Git link roles never reached the import record** that Project Evidence reads.
6. **Suggestions counted as contributing.** Source Analysis's "infrastructure elsewhere" treated suggested packages (imported for another
   application) as contributing evidence.

## Changes

- **All confirmed roles count.** `ApplicationContexts.ConfirmedRoles(record)` returns every confirmed role. `ApplicationPackage.Roles` and
  `PackageAssociation.Roles` carry them; `Role` stays the primary role, for ordering and one-line labels.
- **Confirmed roles decide.** `ProjectEvidenceAssociation.Fits`: when roles are confirmed, they alone decide which evidence a source fills. A
  capability the content shows but nobody confirmed is not added. A confirmed related application or system source fills the
  supporting-source evidence, which takes several sources. Without confirmed roles, the content-based rule is unchanged.
- **Multi-role editor.** The Project Evidence editor has a checkbox per supported role and at least one role per source. Already associated
  sources can get further roles, and each role is saved as its own association choice; the existing API already accepted several.
- **Honest Snapshots label:**
  - a bound source shows the application names, as before;
  - a source only *suggested* by Project Evidence shows "Suggested for M2LB (not associated yet)";
  - otherwise "Not associated".

  Ownership is never guessed from names.
- **Git roles reach the record.** `GitImportRequest.Roles` carries the link's roles, and the import session writes them to the new import
  record when it has none. A person's own choice wins.
- **Infrastructure elsewhere** excludes suggestions.

## Semantics

- **Per application.** Roles are the person's statement about a source for one Target Application: the bindings in that environment's
  Project Evidence. Source Management's roles are the source's own default roles (store a).
- **Rejection.** Rejection is per role (`Declined {RoleKey, ImportId}`) or for the whole source (`*`, "not used for this application").
- **Snapshots stay immutable.** A snapshot never moves to another project because its source supports another application.

## Not changed (documented limits)

- **No per-role reviewer or rationale.** Bindings record `BoundBy` and `BoundAt`; BirkNext is single-user. A separate review-decision record
  per (source, role) was not added, because the existing binding and decline records carry confirm and reject per role.
- **No backend store for Project Evidence.** The browser-held store is unchanged.

## Tests

- `SourceRoleAssociationTests`:
  - every confirmed role counts;
  - confirmed roles decide the evidence;
  - an unconfirmed capability is not added;
  - a related source fills supporting evidence;
  - the detected path is unchanged.
- `SnapshotBrowserTests`: a suggested-only source is labelled as a suggestion; "Not associated" otherwise.
- `TargetEnvironmentProjectPaneTests`: multi-role checkboxes in the editor.

Results: see the commit message.
