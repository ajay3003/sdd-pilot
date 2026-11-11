# Multi-branch source context — audit and root causes (tasks 14 + 15)

- **Baseline:** `406919b1` on `008-traceability-first` (after task 13b). Unrelated untracked files (other sessions' audits, `AIAssisted/testdata/`) and a modified `appsettings.Local.json` were left untouched.
- **Scope:** the five real archives `M2LB (3)…(7).zip`. `C:\Downloads` does not exist; files with exactly these names exist in `C:\Users\ajaan\Downloads` and were read there, read-only. See `birknext-m2lb-source-inventory.md` for the independent oracle.
- **Architecture evidence:** a read-only code audit (file:line) is summarised below; the full notes are in `scratchpad/mb/architecture-audit.md`.

## What the reported screens mean (reproduced)

| Observation | Root cause (code) | Kind |
|---|---|---|
| Ten flat Source Management rows, several named "M2LB" | One row per **import record** (front-end `SddProjectImportRecord`), no grouping by logical source (`SourceManagement.razor`). The tiles count imports, not logical sources. | UI/model presentation. The data was not duplicated: each row is a real, distinct immutable snapshot. |
| "1 application" | All imports were made for one workspace application, named from the archive (`ProjectImportService.ProjectName`: "M2LB (n).zip" becomes "M2LB"). | Correct. |
| All M2LB ZIPs grouped under one logical source | The repository key comes from a single root `.sln`, otherwise from the ZIP name with " (n)" removed. These archives have no root `.sln` (only `.slnx` files in sub-folders), so the name decides. | **Grouping by name, unverified**: legitimate for alternative branches, but it must be presented as such (done: basis shown). |
| "4 requiring review", "Application source not selected · candidate: M2LB" | `ApplicationContexts` keeps exactly one primary and demotes every other same-application import to "related application source" (always NeedsReview). Two or more same-named primaries become a "Conflict". The TE pane shows the first NeedsReview primary as "candidate". | **BirkNext defect (P1)**: alternative versions of one source were treated as competing application sources. There was no way to decide "which version", only N separate reviews. |
| "0 fully analyzed, 10 partly analyzed" | Bucket per import over source-setup areas. Unsupported areas (e.g. .js) make an import "partial". | Correct per definition; the denominator is imports. The tile now says so. |
| "6 associated with M2LB" | Infrastructure/supporting/documentation are associated automatically; application versions were not. | Consequence of the above. |

## Cross-module defects (confirmed in code, reproduced with fixtures and the real archives)

| Id | Priority | Module | Defect | Evidence |
|---|---|---|---|---|
| MB-1 | **P0** (silent cross-branch contamination) | Integration Topology | Newest snapshot **per archive file name**, so five branch ZIPs became five "repositories" and were merged into one topology (both MM receiver generations). | `TopologyResolution.cs` `TopologySnapshots.Latest` |
| MB-2 | P1 | Contract Testing source candidates | Newest snapshot per (repository group, contract id): contracts from different branches were combined, and an older-branch-only contract still appeared. | `ContractRegistryService.Source.cs` |
| MB-3 | P1 | System Understanding | Every matching snapshot was kept as the guide's source basis, so all five versions were cited at once. | `SystemGuideService.CurrentSourcesAsync` |
| MB-4 | P1 | Dashboard / Recommended Workflow technology source | The newest snapshot overall was used, which may be an unrelated or alternative import. | `TechnologyCoverageController` |
| MB-5 | P1 | Composite discovery | Newest per project, with no way to choose the intended version. | `CompositeSourceRules.Discover` |
| MB-6 | P1 | Project Evidence (TE) | Versions handled as conflicting application sources (see above). | `ProjectEvidenceAssociation.Of` |
| MB-7 | P1 | Analysis baseline | **None existed.** Registry pointers (CurrentSelected) are per workspace/module and are read by no module. | `SnapshotRegistryService` |
| MB-8 | P2 | Fingerprints / duplicates | Only the archive hash existed; the UI claimed "same content, whatever its file name" but compared bytes, so a re-zip of the same files (real case: `M2LB.zip` vs `M2LB (3).zip`) was not detected. | `IqrSourceArchiveReader`, `ProjectImportHistory.Duplicate` |
| MB-9 | P2 | Branch identity | No declared-branch field; "Unknown" was not explicit everywhere. | `IqrSourceContracts` |
| MB-10 | P1 | Comparison | Source Change Impact refused two different imports of the same source; its panel was not hosted on any page, so there was no reachable branch comparison. | `SourceChangeImpactService.cs` (ProjectImportId check), `SourceChangeImpactPanel.razor` not used |
| MB-11 | P3 | Import again | Re-importing the same bytes updates the existing import record in place. Snapshots stay immutable; only the front-end record's pointer moves. | `ProjectImportActivation.cs` (documented, not changed) |

Not defects:
- Each import really is a distinct immutable snapshot (insert-only).
- Review source scopes already enforce "one snapshot per repository, nothing substituted".
- Execution manifests are DB-guarded and untouched.

## Security notes

Unchanged and verified:
- ZIP validation (traversal, absolute paths, duplicates after normalisation, symlinks, entry count, expanded-size limits).
- No script execution.
- `.tfstate` is never read.

Added:
- Choosing a baseline or declaring a branch requires the backend policy SystemConfigurationWrite; reading is open.
- Declared labels are restricted to a safe character set.
