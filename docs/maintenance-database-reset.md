# Local database reset

## Availability and safety

The Maintenance diagnostic reports whether reset is safely available. An enabled reset in a local development setup is `Pass`; the destructive nature of the action is communicated in the Danger Zone and confirmation dialog. A disabled policy or a non-local database is informational. An unknown database mode is not treated as Local.

The backend enforces the policy independently of the UI. Reset requires `AdminSettings:AllowLocalDatabaseReset=true`, `DatabaseSettings:Mode=Local`, a Development, Local, or Test backend environment, and a loopback PostgreSQL host (`localhost`, `127.0.0.1`, or `::1`). Production, unknown environments, remote hosts, and shared modes are rejected.

## What reset removes and preserves

Reset deletes rows from the application data tables represented by the EF Core model in one database transaction. Tables with foreign key dependencies are deleted before their principal tables. The model is inspected at runtime, so new mapped application data tables are included by default.

Reset removes saved workspaces and their artifacts (including embedded SDD lifecycle state), scenarios, reviews, traceability and suggestions, source and integration evidence, dependency/security analysis, test and performance run history, baselines, snapshots, and other persisted results. It refuses to begin while a CDC or performance run is marked `Running`.

Reset preserves the database, schema, EF migration history, and installation/target-environment setup tables: integration platforms and definitions, environment seed/import state, integration contract and GraphQL schema artifacts, integration message-flow configuration, performance test definitions, and performance test data profiles. Application settings, provider installation configuration, binaries, source code, frontend analysis profiles, and uploaded files stored outside the database are also preserved. Sample project catalog files remain available; saved workspace state for previously loaded samples is removed.

## Persistence inventory

The reset derives the deletion list from `AppDbContext`'s relational model. Current mapped entity sets are classified here:

| Reset action | Entity sets | Ownership |
| --- | --- | --- |
| Delete | `SavedWorkspaces`, `SavedWorkspaceArtifacts`, `WorkspaceReviewProgress` | Workspace data, artifact revisions/baselines/questions/decisions/traceability/implementation/test evidence stored in lifecycle JSON |
| Delete | `Scenarios`, `ReviewedCandidates`, `CandidateLinks`, `QaDeltaReviews`, `TraceLinks`, `TraceabilitySuggestions`, `CodeFiles`, `CodeLinks`, `ProjectDocuments` | Project review, traceability, source mappings, imported document records |
| Delete | `IqrSourceSnapshots`, `IntegrationQualitySnapshots`, `IntegrationReviewRuns`, `ApplicationMessagingEvidence`, `ActiveCdcRuns` | Source analysis and integration/test evidence |
| Delete | `DependencyReviewRuns`, `DependencyInventories`, `DependencyHealthRuns`, `ScimEvidence`, `SecurityClassificationEvidence`, `SecurityExpectationDiscoveries`, `AzureEnvironmentSnapshots` | Analysis and security evidence/results |
| Delete | `PerformanceTestRuns`, `PerformanceBaselines` | Performance execution history and baseline/supersession state |
| Preserve | `IntegrationPlatforms`, `IntegrationDefinitions`, `IntegrationEnvironmentStates`, `IntegrationContractArtifacts`, `GraphQlSchemaArtifacts`, `IntegrationMessageFlows` | Target environment and integration setup/configuration |
| Preserve | `PerformanceTestDefinitions`, `PerformanceTestDataProfiles` | Configured test definitions and approved test data profiles |

TRX execution evidence is currently represented in saved workspace lifecycle data; there is no separate TRX artifact table in `AppDbContext`. Raw uploaded files outside the database are not enumerated or deleted by reset.

## Client state and autosave

Before the reset request, the frontend cancels pending autosave timers and waits for an in-flight save to finish. Autosave remains suppressed while the backend reset runs. On success, workspace artifacts, SDD lifecycle state, current workspace ID, review sessions, dashboard snapshots, and extraction state are cleared before autosave resumes. A failed backend reset leaves the frontend data intact.

## Schema and result

Reset is a row deletion, not a database drop or schema recreation. It keeps PostgreSQL running and leaves migrations untouched. The UI reports the number of removed rows and whether the reset succeeded; a failure is logged in detail on the server and returns a safe message to the UI.
