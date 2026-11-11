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
