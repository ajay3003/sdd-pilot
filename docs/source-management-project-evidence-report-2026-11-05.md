# Source Management & Project Evidence: report (2026-11-05)

Branch `008-traceability-first`, not pushed.

## A. Audit and root causes

**Dependency chain traced:** import → identity (import record, workspace application) → capability detection (Source Analysis snapshot via `source-setup`) → role classification (`ApplicationContexts.Packages`) → snapshot → per-environment association (`ProjectEvidenceAssociation`, persisted as references on the Target Environment profile) → evidence selection → review readiness.

| # | Finding | Root cause | Classification |
|---|---|---|---|
| 1 | Roles set in Source Management had no effect on Project Evidence. | Source Management saved `SddProjectImportRecord.ConfirmedRoles`, and **no other code read it**. Project Evidence classified roles from content only, so the two editors silently disagreed. | Functional defect |
| 2 | "8 imported sources, Project Evidence: Not configured, 0 selected". | Association only considers packages imported for the workspace application that corresponds to the Target Application (same name, or the one holding the identity's own package). When none corresponds (packages imported under another application name, e.g. the first ZIP's), every package became "Not associated: no imported evidence is recorded for M2LB yet". There was no suggestion and no path except the advanced per-role tables. Nothing was associated by name: correct, but unexplained. | Functional and presentation defect |
| 3 | Project tab overloaded. | Decision line, 4 summary cards, review list, checklist panel, required/optional role cards, available-sources table, advanced rules, catalog and Test Pack roles were all shown at once. | Presentation defect |
| 4 | Source Management showed "Failed" for Terraform packages (introduced and caught in this task's own browser check). | It showed the snapshot status, which is only the application-code analyzer's outcome. Project Evidence already reads analysis per dimension. | Semantic defect (fixed before commit) |
| 5 | No shared vocabulary between Source Management roles and Project Evidence categories. | The mapping was implicit in a private `Fits` switch. | Missing shared definition |

**Environment scoping is not a cause.** The backend `source-setup` lists every Source Analysis snapshot whatever the environment (`ReviewSourceEvidenceProvider.ListAsync` ignores it). In the M2LB copy all 10 snapshots have an empty `EnvironmentId`.

**The user's exact data could not be read.** The real `birknext` DB is empty since the 2026-11-05 reset, so the "8 imported / 0 selected" case was reproduced with an environment whose Target Application no workspace application holds (§F, check 16).

## B. Source model (no new registry or engine)

- **Identity and snapshots are unchanged:** import record, Source Analysis snapshot, Snapshot Registry.
- **Capabilities** (what the content shows) still come only from shared Source Analysis extraction. A role never adds a capability.
- **Canonical role.** A role confirmed in Source Management is the package's canonical role (`ApplicationContexts.ConfirmedRole`; with several, the first in role order):
  - every consumer reads it through `ApplicationContexts.Packages` / `ProjectEvidenceAssociation`;
  - a confirmed primary application source is never demoted to "related";
  - a confirmed related source is associated;
  - saving roles of a Git-linked source also records them on its import, so link and import never disagree.
- **Association** is per Target Environment, unchanged. Explicit per-application decisions (bindings, "Not used for …") still win over everything.
- **Suggestions** (`AssociationSuggestion`) are never applied automatically:

  | Class | Basis |
  |---|---|
  | **Strong** | The identity's own package, or the same Target Application's confirmed binding in another Target Environment (reviewed association reused as a suggestion, not copied). |
  | **Possible** | Only when no workspace application corresponds to the Target Application, and the name is related ("names alone are weak evidence"). |
  | **Unrelated** | Not suggested. |

- **One vocabulary** (`SourceRoleVocabulary`):

  | Source role | Project Evidence category |
  |---|---|
  | Primary application source | Application source |
  | Related application/system source | Integration source |
  | Supporting/shared source | Supporting/shared source |
  | Documentation | Documentation |
  | Infrastructure evidence | Infrastructure |
  | Generated documentation | Generated documentation |
  | Contracts/schemas | Contracts and schemas |

  `Supported(package)` gives the roles a source's content supports.
- **Transactional save:** `IProjectEvidenceService.AssociateAsync(choices)` applies all choices to a copy and replaces the evidence only when every choice applied:
  - it creates the Project evidence for the Target Application when there is none;
  - an explicit choice reverses an earlier "Not used for …";
  - a role the content does not support is refused, and nothing is saved.
- **Removal:** `RemoveAssociationAsync` removes a source from every category and remembers the decision. The import and its snapshots are kept.

## C. Source Management UI (`/sources`)

- **Header:** "Source Management — Manage imported ZIP and connected Git sources, their evidence capabilities, revisions and associations."
- **Summary** (all calculated): sources, ZIP, Git, requiring review, associated applications, and analysis by dimension (Analyzed / Partly analyzed / Not analyzed).
- **Compact grid:** Source (with its roles line) · Origin · Capabilities · Associated applications (application and environment from Project Evidence records) · Current snapshot · Analysis status (per dimension, e.g. "Contracts: Partial · Infrastructure: Analyzed") · Last updated. Rows have no action buttons.
- **Detail panel.** A row opens it:
  - **Actions:** Upload new version / Check for updates and Import revision (Git), Manage roles, Associate with application (deep link to Project Evidence), View evidence, View snapshots.
  - **Tabs:** Overview, Capabilities & Roles, Associations, Snapshots, Analysis & Evidence, History (earlier imports for Git sources, decided by the person).
- **Role editor:**
  - detected capabilities (read-only) and the detected role with why;
  - assigned roles (several);
  - a note that the application association is per environment;
  - an "Unsaved role changes" warning; Save only when changed, plus Discard;
  - confirmation that Project Evidence uses the roles and that per-application decisions are unchanged.

## D. Project tab (Target Environments → Project)

- **Header:** "Project Evidence — Select the imported sources that provide application code, contracts, infrastructure and supporting evidence for this target application." Context line: Target Application · Environment.
- **One calculated summary:** "N imported sources available · M associated with <app> · Application source: <name>" (or "Application source not selected · candidate: …"), plus "K suggestions to review".
- **Primary action:** "Review suggested sources" (or "Select sources" / "Add source").
- **Single association editor** (`ProjectEvidenceAssociationEditor`):
  1. **Select sources:** search; suggestions first, with class and reason; other imported sources collapsed; View evidence per source; only strong suggestions start selected.
  2. **Review roles:** detected capabilities versus the role for the application; only supported roles are offered.
  3. **Review and save:** summary with category and basis. A possible match, or a source chosen without suggestion, needs an explicit confirmation. Save is all-or-nothing; on failure, "Nothing was saved" with reasons.
  4. **Result:** "Project Evidence configured — …", or "partially configured — still missing: …".
- **Compact associated-sources table:** Source · Capabilities · Role for <app> · Status (Associated automatically / role confirmed / Confirmed by you) · View evidence, Remove.
- **Collapsed:** "Evidence roles, requirements and diagnostics" holds the former cards, review list, checklist, role cards, available sources and advanced configuration. The functionality is unchanged.

## E. Cross-module

- Project Evidence associations and roles are the shared records the existing consumers already read (explorers' documentation scope, review source scopes, Test Pack satisfaction). The confirmed role now reaches all of them through `ApplicationContexts.Packages`.
- Source Management reads capabilities, associations and analysis from the same `IProjectEvidenceService` read, with no inference of its own. Associated applications come from the persisted Target Environment evidence records.
- Analysis status is per dimension in both Source Management and Project Evidence. The application-code analyzer's snapshot status is never shown as a package's state, so Terraform-only packages read "Infrastructure: Analyzed", not "Failed".

## F. Tests

**Automated:**

| Suite | What is tested |
|---|---|
| `SourceRoleAssociationTests` (new) | Confirmed role is canonical; several roles resolve by order; confirmed primary never demoted; invalid values ignored; suggestion classes (identity package and other-environment confirmation are strong; names only possible and only without an application; unrelated is none); role → category vocabulary (7 roles); supported roles only. |
| `TargetEnvironmentProjectPaneTests` (+4) | Imported-but-unassociated counted and suggested, never associated; editor flow (strong preselected, possible not, supported roles only, confirmation required, saved as the person's binding, table, application source, counts); all-or-nothing save with an unsupported role; removal remembered and the import kept. One assertion updated: the fixture's two suggestions are now counted. |
| `SourceManagementTests` (+1, 3 adapted) | Actions in the detail panel; check only reports; history decisions; roles saved several at once with the unsaved warning and a disabled Save until changed; grid columns, summary, tabs and associations deep link. |

| Run | Result |
|---|---|
| Full frontend suite (`dotnet test frontend/BirkNext.Web.Tests`) | **8137 passed, 0 failed** |

**Real browser** (`scratchpad/src-e2e.cjs`):

- **Setup:** Edge on the M2LB copy (DB `birknext_gaps`, 7 imported M2LB packages).
- **Isolation:** associations were saved only in this browser profile's settings (throwaway); no production record was changed.
- **Result: 23/23 checks passed.**

| Area | Checks |
|---|---|
| Source Management | 7 packages with summary; Terraform analysis per dimension ("Infrastructure: Analyzed", never a bare "Failed"); summary "3 analyzed, 4 partly analyzed"; no row buttons; detail panel roles without expanding the table; Save disabled until changed. |
| Project tab, Development (Target Application M2LB) | "7 imported sources available · 7 associated with M2LB · Application source: M2LB"; details collapsed. |
| Remove and re-associate | Remove Terraform-iac-meldingsutveksler: gone, import kept, still removed after reload. The editor offers it with supported roles only (Infrastructure, Contracts); confirmation required. Saved: "Project Evidence configured — 1 source associated with M2LB", row "Confirmed by you", stored `boundBy: User`, no duplicates. Visible in Source Management as "M2LB (Development)". Its Source Analysis evidence opens without re-import. |
| Target Application no workspace application holds (Meldingsutveksler) | "7 imported sources available · 0 associated with Meldingsutveksler · Application source not selected · 2 suggestions to review". Both Meldingsutveksler packages are possible matches only; none preselected or associated. |
| General | No horizontal scroll at 390 px; no external hosts; no page errors. |

## G. Gaps

1. **Requirement policy unchanged.** The default required roles (application source, project documentation) are still universal per project, as before. Making requiredness review-dependent (e.g. a Terraform-only project) was audited but not changed: it would change readiness rules, which this task must not do.
2. **Not tested:** cross-application isolation in the browser beyond the two environments shown, and Git-linked sources (no Azure DevOps links in the M2LB copy; unit-tested with a mocked link).
3. **Multiple confirmed roles per source in one environment.** Several roles are kept and shown, but association uses one canonical role per source. Binding one source to several categories is possible in the editor only by choosing it once per category, which is not offered yet.
4. **Real user DB.** The user's exact "8 imported / 0 selected" state could not be inspected (the real DB is empty); the cause was reproduced with the controlled environment above.
