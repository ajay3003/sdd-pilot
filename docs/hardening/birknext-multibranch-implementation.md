# Multi-branch source context — implementation (tasks 14 + 15)

Design: `docs/architecture/birknext-canonical-source-context.md`. Root causes: `docs/audits/birknext-multibranch-source-context-audit.md`.

## Backend

| Change | Files |
|---|---|
| Canonical **content fingerprint** per archive (streamed SHA-256 per file, sorted path/hash list; single common root folder removed; ignored build folders excluded; entry order, compression and archive name irrelevant; line endings significant). Stored on `SourceArchive.ContentSha256` and in the import preview. | `Services/Integrations/SourceEvidence/IqrSourceArchiveReader.cs`, `shared/ApplicationMessagingContracts.cs`, `shared/ProjectImportContracts.cs`, `Services/ProjectImport/ProjectImportService.cs`, `shared/IntegrationJourneyContracts.cs` (`SourceSetupSnapshot.ContentFingerprint`), `Services/IntegrationJourneys/IntegrationJourneyService.cs` |
| **Analysis baseline + declared branch** decisions: append-only table `source_decisions` (migration `20261111230323_AddSourceDecisions`), with strictly increasing timestamps per subject, no revision for an identical re-selection, and a derived actor | `Models/SourceDecisionRecords.cs`, `Data/AppDbContext.cs`, `Data/Migrations/*AddSourceDecisions*`, `AppDbContextModelSnapshot.cs` |
| **Shared source context** service with the single resolution rule, context view (states, distinct contents, components, branch kind, used snapshot, context fingerprint) and history | `Services/SourceAnalysis/SourceBaselineService.cs`, `shared/SourceBaselineContracts.cs` |
| API (writes need SystemConfigurationWrite) | `Controllers/SourceContextController.cs`, `Program.cs` |
| Modules switched to the rule: Integration Topology, Contract Testing source candidates, System Understanding basis, Composite discovery, Technology coverage (Dashboard/Workflow) | `IntegrationTopologyService.cs`, `ContractRegistryService.Source.cs`, `SystemGuideService.cs`, `CompositeSourceRules.cs`, `CompositeContextService.cs`, `TechnologyCoverageController.cs` |
| Source Change Impact: one side may be an alternative version (same logical source) | `Services/SourceChangeImpactService.cs` |

## Frontend

| Change | Files |
|---|---|
| **Source versions** panel (Source Management): logical sources with grouping basis, state, versions, content and archive fingerprints, branch (verified/declared/unknown), components, baseline badge, "Use as analysis baseline", clear, "Compare with the version in use" (the existing Source Change Impact panel, now hosted), declared branch label, decision history, and access explained from the backend | `Components/SourceManagement/SourceContextPanel.razor(.css)`, `Pages/SourceManagement.razor` |
| **Source context notice**: one line on Project Quality, Recommended Workflow, System Understanding, Integration Topology and TE Project Evidence. Either "Select the M2LB source baseline. Multiple alternative source snapshots are available…", or "Analysing M2LB with M2LB (7).zip (analysis baseline, 5 versions). Not a deployment claim." | `Components/SourceManagement/SourceContextNotice.razor(.css)`, pages |
| **Project Evidence**: versions of one logical source are one decision. The baseline is the application source; the others are alternative versions (kept, not used); an automatic main package follows a baseline switch, a manual one never does | `Services/ProjectEvidence/ProjectEvidenceAssociation.cs`, `ProjectEvidenceService.cs` |
| Duplicate import by **content**: "a different archive with exactly the same files" | `Services/ProjectImport/ProjectImportHistory.cs`, `ProjectImportSession.cs`, `Pages/ProjectImport.razor` |
| Comparison panel accepts an explicit pair; the import scope is sent only when one side belongs to it | `Components/SourceChangeImpactPanel.razor` |
| API client | `Services/SourceContextApiService.cs`, `Program.cs` |

## Tests

- **Backend:** `SourceBaselineServiceTests` (resolution, unavailable baseline not substituted, states, append-only history and identical re-selection, declared vs verified branch, fetch outside window), `SourceContentFingerprintTests` (order, compression, root folder, one changed file, line endings, reader records both identities), and `SourceChangeImpactServiceTests` (cross-import rule updated, direction kept).
- **Frontend:** `ProjectEvidenceVersionsTests` (reproduction of the reported state; one decision without a baseline; baseline associated and alternatives kept; main package follows; a manual choice never moves), `ProjectImportContentDuplicateTests`, and `SourceManagementTests` (tile denominator wording).

## Test changes explained

- **`SourceChangeImpactServiceTests.Imported_project_target_must_be_the_exact_import_snapshot`** pinned the rule that blocked branch comparison. It is renamed and now asserts the new rule: one side must belong to the import, and different sources are still refused by the repository-key check.
- **`SourceManagementTests`**: the tile detail text now states its denominator.

## Continuation (2026-11-12): baseline correction, history integrity, composition guard, portability

| Requirement | Change | Files | Regression tests |
|---|---|---|---|
| No authoritative fallback to the newest import | Shared `SourceBaselineService.Authoritative` rule: baseline, else the only content, else **nothing** (`BASELINE_NOT_SELECTED`). `LogicalSourceView` gains `StateCode` and `PreviewSnapshotId` (non-authoritative, never persisted). Every consumer uses it: topology, contract candidates and composite discovery through `Resolve`; System Understanding (no "not imported" for an unselected source); technology coverage (no substitute source, explicit notice). | `SourceBaselineService.cs`, `shared/SourceBaselineContracts.cs`, `CompositeSourceRules.cs`, `TechnologyCoverageController.cs`, `SystemGuideService.cs`, `ContractRegistryService.Source.cs` (comment) | `SourceBaselineServiceTests` (rule, preview not persisted, restart persistence, coverage withheld), `CompositeSuiteTests` (discovery), `SystemGuideTests` (BASELINE_NOT_SELECTED basis) |
| UI states it honestly | The notice says the page "leaves it out until you choose" (never "uses the newest import"). Source versions: "Baseline not selected", a preview badge, compare **any two** versions, and compare with the newest import while unselected. A Source Analysis badge gives the shown snapshot's role. | `SourceContextNotice.razor`, `SourceContextPanel.razor(.css)`, new `SourceVersionBadge.razor(.css)`, `SourceAnalysis.razor` | `SourceContextBaselineNotSelectedTests` (notice, panel, badge), `ProjectEvidenceVersionsTests` (fixture follows the corrected backend view) |
| Identical import must not rewrite history | Append-only `SddProjectImportRecord.History` (`SddProjectImportEvent`); a legacy record is seeded before its first re-import. The duplicate help text now states this. | `SddLifecycleModels.cs`, `ProjectImportActivation.cs`, `ProjectImport.razor` | `ProjectImportActivationTests` (2 new), `ProjectImportSourceSetupTests` (wording) |
| DB-level append-only guard | Migration `20261112070530_GuardSourceDecisions`: `BEFORE UPDATE` trigger; DELETE stays possible for Local Data Reset; no model change | `Data/Migrations/20261112070530_GuardSourceDecisions*.cs` | `WorkspacePackageRoundTripTests.Source_baseline_decisions_travel_…` (UPDATE through EF refused on a real PostgreSQL test database) |
| No silent component mixing | `CompositeContextService.SaveAsync` refuses two different versions of one source in one context. Content-identical re-imports count as one version. | `CompositeContextService.cs`, `CompositeSourceRules.SnapshotFacts.ContentKey` | `CompositeSuiteTests` (refused mix, allowed re-zip) |
| Project export portability | `source-decision` package type: export with project packages; import as New, Identical or Conflict (never overrides a destination's own history); inserted only, with original actor and time; missing snapshots reported | `PackageModel.cs`, `WorkspacePackageService.cs/.Import.cs/.Apply.cs`, `WorkspaceTransfer.razor.cs` (label) | `WorkspacePackageRoundTripTests.Source_baseline_decisions_travel_with_the_project_and_never_override_the_destination` |

### Fixes from the real-archive acceptance (same commit `22710749`)

| Defect | Change | Files |
|---|---|---|
| Content-duplicate check raced the async source-setup load | Session reads the setup when the page has none yet | `Services/ProjectImport/ProjectImportSession.cs` |
| Decided source collapsed right after the choice | The acted-on source stays open | `SourceContextPanel.razor` |
| Re-zip reported as "unclassified change" | Equal content fingerprints = no source change (limitation states it) | `Services/SourceChangeImpactService.cs` |
| Topology hid the real reason | `TopologyContext.AwaitingBaseline`; the SourceAnalysis source names the sources left out | `TopologyProviders.cs`, `IntegrationTopologyService.cs` |
| Guide said "No longer imported" for a version still imported | `SourceChanges(…, stillImported)` | `SystemGuideService.cs` |
| Guard trigger used `OLD."Id"` (snake_case columns) | `OLD.id` | `20261112070530_GuardSourceDecisions.cs` |
| Text buttons showed browser chrome | Scoped `button.btn-link` styles | `SourceContextPanel.razor.css` |

### Process note

One focused test run reported 70/70 although its build had failed: the chained command ran the tests on stale binaries. That run was discarded.
All later focused runs were gated on a successful build. For the final regression, the build lines are checked before any count is accepted. The regression script's memory guard
was also narrowed to the command's own process tree; before, it also stopped any `testhost` started at the same time.

## Final regression

**IN PROGRESS when these reports were committed. No result is claimed here.**
- Code: commit `22710749`. The scratch build tree was verified identical to it, shared CSS included.
- Script: `scratchpad/final/regress.ps1` (tag `fin2`): build, backend partitions 1–5, partition 6 split into 6a/6b/6c, then the full frontend suite;
  sequential and memory-guarded.
- Available memory at start: 6.2 GB.
- Exact counts, the build check and the Release build will be added in a follow-up commit.
- The earlier task-13 regression (partitions 1–5 only; partition 6 aborted; frontend not run) does **not** count as a pass.
